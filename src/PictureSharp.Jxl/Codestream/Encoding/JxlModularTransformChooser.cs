using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;
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
    public static PaletteResult? Palette(List<JxlChannel> image, int count, CancellationToken cancellationToken)
    {
        int width = image[0].Width;
        int height = image[0].Height;
        long pixels = (long)width * height;
        if (count > MaxPaletteChannels || pixels < MinPalettePixels) return null;
        int limit = (int)Math.Min(MaxPaletteColors, pixels / PixelsPerColor);
        var colors = new Dictionary<long, int>();
        int[][] planes = [.. image.Take(count).Select(channel => channel.Pixels)];
        for (int i = 0; i < planes[0].Length; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();
            long key = Key(planes, i);
            if (colors.TryAdd(key, 0) && colors.Count > limit) return null;
        }

        long[] sorted = [.. colors.Keys.OrderBy(key => Luma(key, count)).ThenBy(key => key)];
        for (int i = 0; i < sorted.Length; i++) colors[sorted[i]] = i;
        var palette = new JxlChannel(sorted.Length, count, -1, -1);
        for (int i = 0; i < sorted.Length; i++)
        {
            for (int c = 0; c < count; c++) palette.Pixels[c * sorted.Length + i] = (int)((sorted[i] >> (16 * c)) & 0xFFFF);
        }

        var indices = new JxlChannel(width, height);
        for (int i = 0; i < indices.Pixels.Length; i++) indices.Pixels[i] = colors[Key(planes, i)];
        var transform = new JxlTransform
        {
            Id = JxlTransformId.Palette, BeginC = 0, NumC = (uint)count, Colors = (uint)sorted.Length, Deltas = 0, Predictor = JxlPredictor.Zero,
        };
        return new PaletteResult(transform, palette, indices);
    }

    // The 16-bit samples of pixel i packed into one key.
    private static long Key(int[][] planes, int i)
    {
        long key = 0;
        for (int c = 0; c < planes.Length; c++) key |= (long)(planes[c][i] & 0xFFFF) << (16 * c);
        return key;
    }

    private static long Luma(long key, int count) => count < 3
        ? key & 0xFFFF
        : 299 * (key & 0xFFFF) + 587 * ((key >> 16) & 0xFFFF) + 114 * ((key >> 32) & 0xFFFF);

    /// <summary>
    /// The RCT type whose channels cost least with the gradient predictor on a sample of rows; none when no transform
    /// beats the untransformed channels.
    /// </summary>
    public static JxlRctType ChooseRct(List<JxlChannel> image, CancellationToken cancellationToken)
    {
        int width = image[0].Width;
        int height = image[0].Height;
        int[][] planes = [image[0].Pixels, image[1].Pixels, image[2].Pixels];
        int step = (int)Math.Max(1, (long)width * height / RctSamplePixels);
        JxlRctType best = JxlRctType.None;
        double bestCost = double.MaxValue;
        Span<int> current = stackalloc int[3];
        Span<int> west = stackalloc int[3];
        Span<int> north = stackalloc int[3];
        Span<int> northWest = stackalloc int[3];
        for (uint value = 0; value < JxlRctType.Count; value++)
        {
            var type = new JxlRctType(value);
            // The identity transform under another permutation only reorders the channels.
            if (type.Transform == 0 && type != JxlRctType.None) continue;
            var forward = new ForwardRctStep(type);
            cancellationToken.ThrowIfCancellationRequested();
            var histograms = new long[3][];
            for (int c = 0; c < 3; c++) histograms[c] = new long[CostTokens];
            long extra = 0;
            for (int y = 1; y < height; y += step)
            {
                for (int x = 1; x < width; x++)
                {
                    forward.Apply(planes, y * width + x, current);
                    forward.Apply(planes, y * width + x - 1, west);
                    forward.Apply(planes, (y - 1) * width + x, north);
                    forward.Apply(planes, (y - 1) * width + x - 1, northWest);
                    for (int c = 0; c < 3; c++)
                    {
                        long residual = current[c] - JxlMaProperty.ClampedGradient(west[c], north[c], northWest[c]);
                        uint token = JxlHybridUint.Default.Encode(PackSigned(residual), out int bits, out _);
                        histograms[c][Math.Min(token, CostTokens - 1)]++;
                        extra += bits;
                    }
                }
            }

            double cost = extra + histograms.Sum(JxlHistogramClustering.Entropy);
            if (cost < bestCost * RctImprovement || type == JxlRctType.None)
            {
                bestCost = cost;
                best = type;
            }
        }

        return best;
    }

    /// <summary>Replaces the first three channels of <paramref name="image"/> by their transformed values.</summary>
    public static void ForwardRct(List<JxlChannel> image, JxlRctType type, CancellationToken cancellationToken)
    {
        int[][] planes = [image[0].Pixels, image[1].Pixels, image[2].Pixels];
        var outputs = new JxlChannel[3];
        for (int c = 0; c < 3; c++) outputs[c] = new JxlChannel(image[c].Width, image[c].Height);
        Span<int> values = stackalloc int[3];
        var forward = new ForwardRctStep(type);
        for (int i = 0; i < planes[0].Length; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();
            forward.Apply(planes, i, values);
            for (int c = 0; c < 3; c++) outputs[c].Pixels[i] = values[c];
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

        public void Apply(int[][] planes, int i, Span<int> result)
        {
            int a = planes[first][i];
            int s = planes[second][i];
            int t = planes[third][i];
            if (yCoCg)
            {
                int co = a - t;
                int tmp = t + (co >> 1);
                int cg = s - tmp;
                (result[0], result[1], result[2]) = (tmp + (cg >> 1), co, cg);
                return;
            }

            if (secondMode == 1) s -= a;
            else if (secondMode == 2) s -= (a + t) >> 1;
            if (subtractsFirst) t -= a;
            (result[0], result[1], result[2]) = (a, s, t);
        }
    }
}
