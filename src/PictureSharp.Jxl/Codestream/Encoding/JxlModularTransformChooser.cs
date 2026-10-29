using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// Chooses and applies the encoder's Modular transforms: a palette for images with few colours, otherwise the
/// reversible colour transform that codes cheapest. The forward counterparts of the decoder's palette and RCT.
/// </summary>
internal static class JxlModularTransformChooser
{
    private const int MaxPaletteColors = 1024;
    private const int MaxPaletteChannels = 4;
    private const int MinPalettePixels = 64;
    // The palette must pay off: at most one colour per this many pixels.
    private const int PixelsPerColor = 8;
    private const int RctSamplePixels = 65536;
    private const int CostTokens = 64;
    // A transform must beat the best so far by this factor; the identity is the first candidate.
    private const double RctImprovement = 0.995;
    private const int CancellationCheckMask = 0xFFFF;

    /// <summary>The palette transform, the palette as a meta channel and the index channel.</summary>
    public sealed record PaletteResult(JxlTransform Transform, JxlChannel Palette, JxlChannel Indices);

    /// <summary>
    /// A palette of all <paramref name="count"/> channels when the image has at most 1024 colours, and few enough for
    /// the palette to pay off: entries sorted by luma, so that nearby indices have nearby colours.
    /// </summary>
    /// <remarks>
    /// Whether there is a palette does not depend on the order pixels are visited (every sample fits 16 bits and there
    /// are at most limit colours), so row bands collect their colours on their own and the sets are merged.
    /// </remarks>
    public static PaletteResult? Palette(List<JxlChannel> image, int count, CancellationToken cancellationToken,
        int parallelism = JxlParallel.Sequential)
    {
        int width = image[0].Width;
        int height = image[0].Height;
        long pixels = (long)width * height;
        if (count > MaxPaletteChannels || pixels < MinPalettePixels) return null;
        int limit = (int)Math.Min(MaxPaletteColors, pixels / PixelsPerColor);
        int[][] planes = [.. image.Take(count).Select(channel => channel.Pixels)];
        int bands = JxlParallel.Bands(height, PaletteBandRows, parallelism);
        var found = new HashSet<long>?[bands];
        JxlParallel.For(bands, parallelism, cancellationToken, band =>
            found[band] = Colors(planes, width * JxlParallel.BandStart(height, bands, band), width * JxlParallel.BandStart(height, bands, band + 1),
                limit, cancellationToken));
        var colors = new Dictionary<long, int>();
        foreach (HashSet<long>? set in found)
        {
            if (set is null) return null;
            foreach (long key in set)
            {
                if (colors.TryAdd(key, 0) && colors.Count > limit) return null;
            }
        }

        long[] sorted = [.. colors.Keys.OrderBy(key => Luma(key, count)).ThenBy(key => key)];
        for (int i = 0; i < sorted.Length; i++) colors[sorted[i]] = i;
        var palette = new JxlChannel(sorted.Length, count, -1, -1);
        for (int i = 0; i < sorted.Length; i++)
        {
            for (int c = 0; c < count; c++) palette.Pixels[c * sorted.Length + i] = Sample(sorted[i], c);
        }

        var indices = new JxlChannel(width, height);
        int[] output = indices.Pixels; // Allocated lazily: before the region.
        JxlParallel.ForBands(height, PaletteBandRows, parallelism, cancellationToken, (start, end) =>
        {
            long previous = 0;
            int index = -1;
            for (int i = width * start; i < width * end; i++)
            {
                long key = Key(planes, i);
                if (index < 0 || key != previous) (previous, index) = (key, colors[key]);
                output[i] = index;
            }
        });
        var transform = new JxlTransform
        {
            Id = JxlTransformId.Palette, BeginC = 0, NumC = (uint)count, Colors = (uint)sorted.Length, Deltas = 0, Predictor = JxlPredictor.Zero,
        };
        return new PaletteResult(transform, palette, indices);
    }

    private const int PaletteBandRows = 64;

    // The colours of pixels [begin, end), or null when a sample does not fit 16 bits or there are more than limit.
    private static HashSet<long>? Colors(int[][] planes, int begin, int end, int limit, CancellationToken cancellationToken)
    {
        var colors = new HashSet<long>();
        long previous = 0;
        for (int i = begin; i < end; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!Fits16Bits(planes, i)) return null;
            long key = Key(planes, i);
            // Runs of one colour are common in screen content.
            if (i > begin && key == previous) continue;
            previous = key;
            if (colors.Add(key) && colors.Count > limit) return null;
        }

        return colors;
    }

    // The signed 16-bit samples of pixel i packed into one key.
    private static long Key(int[][] planes, int i)
    {
        long key = 0;
        for (int c = 0; c < planes.Length; c++) key |= (long)(planes[c][i] & 0xFFFF) << (16 * c);
        return key;
    }

    private static bool Fits16Bits(int[][] planes, int i)
    {
        foreach (int[] plane in planes)
        {
            if (plane[i] is < short.MinValue or > short.MaxValue) return false;
        }

        return true;
    }

    private static int Sample(long key, int c) => (short)((key >> (16 * c)) & 0xFFFF);

    private static long Luma(long key, int count) => count < 3
        ? Sample(key, 0)
        : 299L * Sample(key, 0) + 587L * Sample(key, 1) + 114L * Sample(key, 2);

    /// <summary>
    /// The RCT type whose channels cost least with the gradient predictor on a sample of rows; none when no transform
    /// beats the untransformed channels. Candidates are costed independently (in parallel) and chosen in type order.
    /// </summary>
    public static JxlRctType ChooseRct(List<JxlChannel> image, CancellationToken cancellationToken, int parallelism = JxlParallel.Sequential)
    {
        int[][] planes = [image[0].Pixels, image[1].Pixels, image[2].Pixels];
        var candidates = new List<JxlRctType>();
        for (uint value = 0; value < JxlRctType.Count; value++)
        {
            var type = new JxlRctType(value);
            // The identity transform under another permutation only reorders the channels.
            if (type.Transform == 0 && type != JxlRctType.None) continue;
            candidates.Add(type);
        }

        var costs = new double[candidates.Count];
        JxlParallel.For(candidates.Count, parallelism, cancellationToken, i =>
            costs[i] = RctCost(planes, image[0].Width, image[0].Height, candidates[i]));
        JxlRctType best = JxlRctType.None;
        double bestCost = double.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (costs[i] < bestCost * RctImprovement || candidates[i] == JxlRctType.None)
            {
                bestCost = costs[i];
                best = candidates[i];
            }
        }

        return best;
    }

    private static double RctCost(int[][] planes, int width, int height, JxlRctType type)
    {
        int step = (int)Math.Max(1, (long)width * height / RctSamplePixels);
        var forward = new ForwardRctStep(type);
        // The sampled row and the row above it after the transform, three interleaved channels per pixel.
        int[] above = new int[3 * width];
        int[] row = new int[3 * width];
        var histograms = new long[3][];
        for (int c = 0; c < 3; c++) histograms[c] = new long[CostTokens];
        long extra = 0;
        for (int y = 1; y < height; y += step)
        {
            for (int x = 0; x < width; x++)
            {
                (above[3 * x], above[3 * x + 1], above[3 * x + 2]) = forward.Apply(planes, (y - 1) * width + x);
                (row[3 * x], row[3 * x + 1], row[3 * x + 2]) = forward.Apply(planes, y * width + x);
            }

            for (int x = 1; x < width; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int i = 3 * x + c;
                    long residual = row[i] - JxlMaProperty.ClampedGradient(row[i - 3], above[i], above[i - 3]);
                    uint token = JxlHybridUint.Default.Encode(PackSigned(residual), out int bits, out _);
                    histograms[c][Math.Min(token, CostTokens - 1)]++;
                    extra += bits;
                }
            }
        }

        return extra + histograms.Sum(JxlHistogramClustering.Entropy);
    }

    /// <summary>Replaces the first three channels of <paramref name="image"/> by their transformed values.</summary>
    public static void ForwardRct(List<JxlChannel> image, JxlRctType type, CancellationToken cancellationToken)
    {
        int[][] planes = [image[0].Pixels, image[1].Pixels, image[2].Pixels];
        var outputs = new JxlChannel[3];
        for (int c = 0; c < 3; c++) outputs[c] = new JxlChannel(image[c].Width, image[c].Height);
        var forward = new ForwardRctStep(type);
        int[] first = outputs[0].Pixels;
        int[] second = outputs[1].Pixels;
        int[] third = outputs[2].Pixels;
        for (int i = 0; i < image[0].Width * image[0].Height; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();
            (first[i], second[i], third[i]) = forward.Apply(planes, i);
        }

        for (int c = 0; c < 3; c++) image[c] = outputs[c];
    }

    // The inverse of JxlRctTransform: permute the input channels, then apply the forward colour transform. The type
    // is decoded once, as this runs for every sampled pixel of every candidate.
    private readonly struct ForwardRctStep(JxlRctType type)
    {
        private readonly int first = type.ChannelOrder.First;
        private readonly int second = type.ChannelOrder.Second;
        private readonly int third = type.ChannelOrder.Third;
        private readonly bool yCoCg = type.IsYCoCg;
        private readonly int secondMode = type.SecondChannelMode;
        private readonly bool subtractsFirst = type.SubtractsFirstFromThird;

        public (int First, int Second, int Third) Apply(int[][] planes, int i)
        {
            int a = planes[first][i];
            int s = planes[second][i];
            int t = planes[third][i];
            if (yCoCg)
            {
                int co = a - t;
                int tmp = t + (co >> 1);
                int cg = s - tmp;
                return (tmp + (cg >> 1), co, cg);
            }

            if (secondMode == 1) s -= a;
            else if (secondMode == 2) s -= (a + t) >> 1;
            if (subtractsFirst) t -= a;
            return (a, s, t);
        }
    }
}
