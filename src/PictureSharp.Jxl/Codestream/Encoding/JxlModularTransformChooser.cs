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
        var found = new ColorTable?[bands];
        JxlParallel.For(bands, parallelism, cancellationToken, band =>
            found[band] = Colors(planes, width * JxlParallel.BandStart(height, bands, band), width * JxlParallel.BandStart(height, bands, band + 1),
                limit, cancellationToken));
        var colors = new ColorTable(limit);
        // Keys hold 16 bits per sample: signed samples, or unsigned ones above 32767 (16-bit input), not both.
        bool high = found.Any(set => set is { High: true });
        if (high && found.Any(set => set is { Negative: true })) return null;
        foreach (ColorTable? set in found)
        {
            if (set is null) return null;
            foreach (long key in set.Keys)
            {
                if (colors.Add(key) && colors.Count > limit) return null;
            }
        }

        long[] sorted = [.. colors.Keys.ToArray().OrderBy(key => Luma(key, count, high)).ThenBy(key => key)];
        for (int i = 0; i < sorted.Length; i++) colors.Set(sorted[i], i);
        var palette = new JxlChannel(sorted.Length, count, -1, -1);
        for (int i = 0; i < sorted.Length; i++)
        {
            for (int c = 0; c < count; c++) palette.Pixels[c * sorted.Length + i] = Sample(sorted[i], c, high);
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
                if (index < 0 || key != previous) (previous, index) = (key, colors.Get(key));
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

    // A channel is compacted when at most this share of its range occurs (libjxl's channel_colors_pre_transform_percent: 95).
    private const double ChannelColorsPercent = 25;
    // ... and when its palette has at most one entry per this many pixels.
    private const int PixelsPerChannelColor = 16;

    /// <summary>
    /// libjxl's channel palette (FLIF's ChannelCompact, enc_palette.cc with one channel): when few of the values in the
    /// range of <paramref name="channel"/> occur, the sorted values become a palette and the samples their indices.
    /// Unlike libjxl (at most 95% of the range), at most a quarter of the range may occur: compaction then saves two bits
    /// per sample (widened 8-bit, 10- and 12-bit data in 16 bits, alpha ramps), while on full-range 16-bit photos it made
    /// the learned-tree output larger. It must also lower the gradient-residual cost of sampled rows.
    /// </summary>
    public static PaletteResult? CompactChannel(JxlChannel channel, CancellationToken cancellationToken)
    {
        ReadOnlySpan<int> samples = channel.Pixels.AsSpan(0, channel.Width * channel.Height);
        if (samples.IsEmpty) return null;
        int min = int.MaxValue, max = int.MinValue;
        foreach (int sample in samples)
        {
            min = Math.Min(min, sample);
            max = Math.Max(max, sample);
        }

        long range = (long)max - min + 1;
        long limit = Math.Min(samples.Length / PixelsPerChannelColor, (long)(ChannelColorsPercent / 100 * range));
        if (limit < 2 || range > 1 << 24) return null;
        int[] lookup = new int[range];
        int count = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();
            ref int seen = ref lookup[samples[i] - min];
            if (seen != 0) continue;
            seen = 1;
            if (++count > limit) return null;
        }

        var palette = new JxlChannel(count, 1, -1, -1);
        int next = 0;
        for (int v = 0; v < lookup.Length; v++)
        {
            if (lookup[v] == 0) continue;
            palette.Pixels[next] = min + v;
            lookup[v] = next++;
        }

        var indices = new JxlChannel(channel.Width, channel.Height);
        int[] output = indices.Pixels;
        for (int i = 0; i < samples.Length; i++) output[i] = lookup[samples[i] - min];
        // As libjxl's slower efforts check transforms (maybe_do_transform with EstimateCost): sorted indices of values
        // that wrap or interleave lose their smoothness.
        if (GradientCost(output, channel.Width, channel.Height) >= GradientCost(channel.Pixels, channel.Width, channel.Height)) return null;
        var transform = new JxlTransform
        {
            Id = JxlTransformId.Palette, BeginC = 0, NumC = 1, Colors = (uint)count, Deltas = 0, Predictor = JxlPredictor.Zero,
        };
        return new PaletteResult(transform, palette, indices);
    }

    // The colours of pixels [begin, end), or null when a sample is outside -32768..65535 or there are more than limit.
    private static ColorTable? Colors(int[][] planes, int begin, int end, int limit, CancellationToken cancellationToken)
    {
        var colors = new ColorTable(limit);
        long previous = 0;
        for (int i = begin; i < end; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!Fits16Bits(planes, i, colors)) return null;
            long key = Key(planes, i);
            // Runs of one colour are common in screen content.
            if (i > begin && key == previous) continue;
            previous = key;
            if (colors.Add(key) && colors.Count > limit) return null;
        }

        return colors;
    }

    /// <summary>
    /// A set of at most limit + 1 colour keys, each with a value: open addressing in a power-of-two table at most a
    /// quarter full, so lookups of the (few) palette colours stay short.
    /// </summary>
    private sealed class ColorTable
    {
        private readonly long[] keys;
        private readonly int[] values;
        private readonly bool[] used;
        private readonly long[] inserted;
        private readonly int shift;

        public ColorTable(int limit)
        {
            int size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(4 * (limit + 1)));
            shift = 64 - System.Numerics.BitOperations.Log2((uint)size);
            keys = new long[size];
            values = new int[size];
            used = new bool[size];
            inserted = new long[limit + 1];
        }

        public int Count { get; private set; }

        /// <summary>Whether a sample was negative.</summary>
        public bool Negative { get; set; }

        /// <summary>Whether a sample was above 32767.</summary>
        public bool High { get; set; }

        /// <summary>The keys in insertion order.</summary>
        public ReadOnlySpan<long> Keys => inserted.AsSpan(0, Count);

        /// <summary>Adds the key unless present (at most limit + 1 keys); whether it was added.</summary>
        public bool Add(long key)
        {
            int slot = Find(key);
            if (used[slot]) return false;
            (used[slot], keys[slot]) = (true, key);
            inserted[Count++] = key;
            return true;
        }

        public void Set(long key, int value) => values[Find(key)] = value;

        public int Get(long key) => values[Find(key)];

        // The slot of the key, or the empty slot where it belongs.
        private int Find(long key)
        {
            int mask = keys.Length - 1;
            int slot = (int)((ulong)key * 0x9E3779B97F4A7C15UL >> shift);
            while (used[slot] && keys[slot] != key) slot = (slot + 1) & mask;
            return slot;
        }
    }

    // The signed 16-bit samples of pixel i packed into one key.
    private static long Key(int[][] planes, int i)
    {
        long key = 0;
        for (int c = 0; c < planes.Length; c++) key |= (long)(planes[c][i] & 0xFFFF) << (16 * c);
        return key;
    }

    // Whether the samples of pixel i fit a key; records negative samples and samples above 32767 in the table.
    private static bool Fits16Bits(int[][] planes, int i, ColorTable colors)
    {
        foreach (int[] plane in planes)
        {
            int sample = plane[i];
            if (sample is < short.MinValue or > ushort.MaxValue) return false;
            if (sample < 0) colors.Negative = true;
            else if (sample > short.MaxValue) colors.High = true;
        }

        return true;
    }

    private static int Sample(long key, int c, bool unsigned)
    {
        int bits = (int)((key >> (16 * c)) & 0xFFFF);
        return unsigned ? bits : (short)bits;
    }

    private static long Luma(long key, int count, bool unsigned) => count < 3
        ? Sample(key, 0, unsigned)
        : 299L * Sample(key, 0, unsigned) + 587L * Sample(key, 1, unsigned) + 114L * Sample(key, 2, unsigned);

    /// <summary>
    /// The RCT type whose channels cost least with the gradient predictor on a sample of rows; none when no transform
    /// beats the untransformed channels. Distinct transformed channels are costed independently (in parallel) and the
    /// candidates chosen in type order.
    /// </summary>
    /// <param name="image">The channels; the first three are transformed.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <param name="parallelism">The maximum degree of parallelism; the choice does not depend on it.</param>
    /// <param name="only">The candidates in order, the first being None; by default every distinct RCT.</param>
    public static JxlRctType ChooseRct(List<JxlChannel> image, CancellationToken cancellationToken, int parallelism = JxlParallel.Sequential,
        JxlRctType[]? only = null)
    {
        int[][] planes = [image[0].Pixels, image[1].Pixels, image[2].Pixels];
        var candidates = new List<JxlRctType>();
        for (uint value = 0; value < JxlRctType.Count && only is null; value++)
        {
            var type = new JxlRctType(value);
            // The identity transform under another permutation only reorders the channels.
            if (type.Transform == 0 && type != JxlRctType.None) continue;
            candidates.Add(type);
        }

        if (only is not null) candidates.AddRange(only);

        // Each candidate's cost is the sum of its three channels' costs, and candidates share channels (the first
        // channel of every permutation, the differences): each distinct channel is costed once.
        var channels = new List<RctChannel>();
        var parts = new int[candidates.Count, 3];
        for (int i = 0; i < candidates.Count; i++)
        {
            var forward = new ForwardRctStep(candidates[i]);
            for (int c = 0; c < 3; c++)
            {
                RctChannel channel = forward.Channel(c);
                int index = channels.IndexOf(channel);
                if (index < 0)
                {
                    index = channels.Count;
                    channels.Add(channel);
                }

                parts[i, c] = index;
            }
        }

        var channelCosts = new (long Extra, double Entropy)[channels.Count];
        JxlParallel.For(channels.Count, parallelism, cancellationToken, i =>
            channelCosts[i] = ChannelCost(planes, image[0].Width, image[0].Height, channels[i]));
        var costs = new double[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            // The extra bits of all channels, then the channels' entropies summed in channel order.
            long extra = 0;
            double entropy = 0;
            for (int c = 0; c < 3; c++)
            {
                extra += channelCosts[parts[i, c]].Extra;
                entropy += channelCosts[parts[i, c]].Entropy;
            }

            costs[i] = extra + entropy;
        }

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

    // The estimated bits of one channel's gradient residuals on a sample of rows (ChannelCost for an untransformed plane).
    private static double GradientCost(int[] pixels, int width, int height)
    {
        int step = (int)Math.Max(1, (long)width * height / RctSamplePixels);
        long[] histogram = new long[CostTokens];
        long extra = 0;
        for (int y = 1; y < height; y += step)
        {
            int row = y * width;
            for (int x = 1; x < width; x++)
            {
                int i = row + x;
                long residual = pixels[i] - (long)JxlMaProperty.ClampedGradient(pixels[i - 1], pixels[i - width], pixels[i - 1 - width]);
                uint token = JxlHybridUint.Default.Encode(PackSigned(residual), out int bits, out _);
                histogram[Math.Min(token, CostTokens - 1)]++;
                extra += bits;
            }
        }

        return extra + JxlHistogramClustering.Entropy(histogram);
    }

    // The extra bits and entropy of one transformed channel's gradient residuals on a sample of rows.
    private static (long Extra, double Entropy) ChannelCost(int[][] planes, int width, int height, RctChannel channel)
    {
        int step = (int)Math.Max(1, (long)width * height / RctSamplePixels);
        // The sampled row and the row above it after the transform.
        int[] above = new int[width];
        int[] row = new int[width];
        long[] histogram = new long[CostTokens];
        long extra = 0;
        int aboveRow = -1;
        for (int y = 1; y < height; y += step)
        {
            if (aboveRow == y - 1) (above, row) = (row, above);
            else channel.Apply(planes, (y - 1) * width, above);
            channel.Apply(planes, y * width, row);
            aboveRow = y;
            for (int x = 1; x < width; x++)
            {
                long residual = row[x] - JxlMaProperty.ClampedGradient(row[x - 1], above[x], above[x - 1]);
                uint token = JxlHybridUint.Default.Encode(PackSigned(residual), out int bits, out _);
                histogram[Math.Min(token, CostTokens - 1)]++;
                extra += bits;
            }
        }

        return (extra, JxlHistogramClustering.Entropy(histogram));
    }

    /// <summary>
    /// Transforms the first three channels of <paramref name="image"/> in place. The channels must be the encoder's
    /// own copies (0.8.6: the group crops), never input samples.
    /// </summary>
    public static void ForwardRctInPlace(IReadOnlyList<JxlChannel> image, JxlRctType type, CancellationToken cancellationToken)
    {
        int[][] planes = [image[0].Pixels, image[1].Pixels, image[2].Pixels];
        var forward = new ForwardRctStep(type);
        int[] first = planes[0];
        int[] second = planes[1];
        int[] third = planes[2];
        for (int i = 0; i < image[0].Width * image[0].Height; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();
            // Apply reads the three samples of pixel i before they are replaced.
            (first[i], second[i], third[i]) = forward.Apply(planes, i);
        }
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

        /// <summary>Output channel <paramref name="c"/> as a function of the planes.</summary>
        public RctChannel Channel(int c) => yCoCg ? new RctChannel(RctChannelKind.YCoCg + c, first, second, third)
            : c == 0 ? new RctChannel(RctChannelKind.Plane, first, 0, 0)
            : c == 1 ? secondMode switch
            {
                0 => new RctChannel(RctChannelKind.Plane, second, 0, 0),
                1 => new RctChannel(RctChannelKind.Difference, second, first, 0),
                // (first + third) >> 1 does not depend on their order.
                _ => new RctChannel(RctChannelKind.MinusAverage, second, Math.Min(first, third), Math.Max(first, third)),
            }
            : subtractsFirst ? new RctChannel(RctChannelKind.Difference, third, first, 0)
            : new RctChannel(RctChannelKind.Plane, third, 0, 0);
    }

    private enum RctChannelKind
    {
        Plane,
        Difference,
        MinusAverage,
        YCoCg, // YCoCg + 1 and + 2: Co and Cg.
    }

    // One channel of a forward RCT: plane p, p − q, p − ((q + r) >> 1), or YCoCg channel (kind − YCoCg) of the
    // planes (p, q, r) in permutation order; the same values as ForwardRctStep.Apply.
    private readonly record struct RctChannel(RctChannelKind Kind, int P, int Q, int R)
    {
        public void Apply(int[][] planes, int start, int[] output)
        {
            int width = output.Length;
            ReadOnlySpan<int> p = planes[P].AsSpan(start, width);
            ReadOnlySpan<int> q = planes[Q].AsSpan(start, width);
            ReadOnlySpan<int> r = planes[R].AsSpan(start, width);
            switch (Kind)
            {
                case RctChannelKind.Plane:
                    p.CopyTo(output);
                    break;
                case RctChannelKind.Difference:
                    for (int x = 0; x < width; x++) output[x] = p[x] - q[x];
                    break;
                case RctChannelKind.MinusAverage:
                    for (int x = 0; x < width; x++) output[x] = p[x] - ((q[x] + r[x]) >> 1);
                    break;
                default:
                    int c = Kind - RctChannelKind.YCoCg;
                    for (int x = 0; x < width; x++)
                    {
                        int co = p[x] - r[x];
                        int tmp = r[x] + (co >> 1);
                        int cg = q[x] - tmp;
                        output[x] = c == 0 ? tmp + (cg >> 1) : c == 1 ? co : cg;
                    }

                    break;
            }
        }
    }
}
