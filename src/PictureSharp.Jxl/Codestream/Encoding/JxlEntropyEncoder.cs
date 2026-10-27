using PictureSharp.Formats.Jxl.Codestream.Entropy;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// Clustered ANS codes for a set of contexts and the streams coded with them: the counterpart of
/// <see cref="JxlEntropyCode"/> and <see cref="JxlSymbolReader"/> without LZ77. Modelled on libjxl v0.12.0
/// lib/jxl/enc_ans.cc, enc_cluster.cc and enc_context_map.cc (BSD-3-Clause); the histogram normalization, the
/// clustering and the choice of hybrid integer configurations are simpler searches of the same formats.
/// </summary>
internal sealed class JxlEntropyEncoder
{
    private const int TabSize = JxlEntropyCode.AnsTabSize;
    private const int LogTabSize = JxlEntropyCode.AnsLogTabSize;
    private const int MaxClusters = 64;
    // Merging histograms that cost less than this apart saves their header.
    private const double MinClusterDistance = 64;
    private static readonly JxlHybridUint ClusteringConfig = new(4, 2, 0);
    private const int SmallValues = 4096;

    private static readonly JxlHybridUint[] CandidateConfigs =
    [
        new(4, 2, 0), new(4, 1, 0), new(4, 0, 0), new(4, 1, 1), new(4, 2, 1), new(4, 1, 2), new(0, 0, 0), new(1, 0, 0),
        new(2, 0, 0), new(3, 0, 0), new(3, 1, 0), new(3, 2, 0), new(5, 0, 0), new(5, 1, 0), new(5, 2, 0), new(6, 0, 0),
        new(6, 1, 0), new(6, 2, 0), new(7, 0, 0), new(8, 0, 0),
    ];

    // Static code of ANS bit widths (count log + 1; 13 is the RLE marker), from enc_ans.cc.
    private static ReadOnlySpan<byte> BitWidthLengths => [5, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 6, 7, 7];
    private static ReadOnlySpan<byte> BitWidthSymbols => [17, 11, 15, 3, 9, 7, 4, 2, 5, 6, 0, 33, 1, 65];

    private readonly byte[] contextMap;
    private readonly JxlHybridUint[] configs;
    private readonly int[][] counts;
    private readonly Histogram[] histograms;
    private readonly int logAlphaSize;
    private readonly int[][] reverse;
    private readonly int[][] starts;

    private JxlEntropyEncoder(byte[] contextMap, JxlHybridUint[] configs, Histogram[] histograms, int logAlphaSize)
    {
        this.contextMap = contextMap;
        this.configs = configs;
        this.histograms = histograms;
        counts = [.. histograms.Select(h => h.Counts)];
        this.logAlphaSize = logAlphaSize;
        reverse = new int[counts.Length][];
        starts = new int[counts.Length][];
        for (int cluster = 0; cluster < counts.Length; cluster++)
        {
            (starts[cluster], reverse[cluster]) = ReverseMap(counts[cluster], logAlphaSize);
        }
    }

    /// <summary>Chooses the codes of <paramref name="contexts"/> contexts for all values of <paramref name="streams"/>.</summary>
    public static JxlEntropyEncoder Build(int contexts, IReadOnlyList<JxlTokens> streams)
    {
        // Token histograms per context under one configuration decide the clusters.
        var histograms = new long[contexts][];
        for (int c = 0; c < contexts; c++) histograms[c] = [];
        foreach (JxlTokens stream in streams)
        {
            ReadOnlySpan<int> ctx = stream.Contexts;
            ReadOnlySpan<uint> streamValues = stream.Values;
            for (int i = 0; i < ctx.Length; i++)
            {
                uint token = Tokenize(ClusteringConfig, streamValues[i], out _, out _);
                ref long[] histogram = ref histograms[ctx[i]];
                if (token >= histogram.Length) Array.Resize(ref histogram, Math.Max((int)token + 1, histogram.Length * 2));
                histogram[token]++;
            }
        }

        byte[] contextMap = Cluster(histograms, out int clusters);

        // Exact value histograms per cluster then choose each cluster's hybrid integer configuration.
        var small = new long[clusters][];
        var large = new Dictionary<uint, long>[clusters];
        for (int c = 0; c < clusters; c++)
        {
            small[c] = new long[SmallValues];
            large[c] = [];
        }

        foreach (JxlTokens stream in streams)
        {
            ReadOnlySpan<int> ctx = stream.Contexts;
            ReadOnlySpan<uint> v = stream.Values;
            for (int i = 0; i < ctx.Length; i++)
            {
                int cluster = contextMap[ctx[i]];
                if (v[i] < SmallValues)
                {
                    small[cluster][v[i]]++;
                }
                else
                {
                    large[cluster][v[i]] = large[cluster].GetValueOrDefault(v[i]) + 1;
                }
            }
        }

        var values = new List<(uint Value, long Count)>[clusters];
        for (int c = 0; c < clusters; c++)
        {
            values[c] = [.. Enumerable.Range(0, SmallValues).Where(v => small[c][v] > 0).Select(v => ((uint)v, small[c][v]))];
            values[c].AddRange(large[c].Select(pair => (pair.Key, pair.Value)));
        }

        var configs = new JxlHybridUint[clusters];
        var tokenCounts = new long[clusters][];
        for (int c = 0; c < clusters; c++)
        {
            (configs[c], tokenCounts[c]) = ChooseConfig(values[c]);
        }

        int logAlphaSize = 5;
        for (int c = 0; c < clusters; c++)
        {
            while (tokenCounts[c].Length > 1 << logAlphaSize || configs[c].SplitExponent > logAlphaSize) logAlphaSize++;
        }

        var normalized = new Histogram[clusters];
        for (int c = 0; c < clusters; c++) normalized[c] = Normalize(tokenCounts[c]);
        return new JxlEntropyEncoder(contextMap, configs, normalized, logAlphaSize);
    }

    /// <summary>Writes the codes: no LZ77, the context map, ANS, the alphabet size, the configurations and histograms.</summary>
    public void WriteHeader(JxlBitWriter writer)
    {
        writer.Write(false);
        if (contextMap.Length > 1) WriteContextMap(writer, contextMap, counts.Length);
        writer.Write(false);
        writer.Write(2, (uint)(logAlphaSize - 5));
        foreach (JxlHybridUint config in configs) WriteConfig(writer, config, logAlphaSize);
        foreach (Histogram histogram in histograms) WriteHistogram(writer, histogram);
    }

    /// <summary>Writes the ANS stream of <paramref name="tokens"/>: the final state, then the renormalization and raw bits.</summary>
    public void WriteStream(JxlBitWriter writer, JxlTokens tokens)
    {
        ReadOnlySpan<int> ctx = tokens.Contexts;
        ReadOnlySpan<uint> values = tokens.Values;
        int[] chunks = new int[ctx.Length];
        uint state = JxlEntropyCode.AnsSignature;
        for (int i = ctx.Length - 1; i >= 0; i--)
        {
            int cluster = contextMap[ctx[i]];
            uint token = Tokenize(configs[cluster], values[i], out _, out _);
            uint frequency = (uint)counts[cluster][token];
            if (frequency == 0) throw new InvalidOperationException("A token has no probability.");
            chunks[i] = -1;
            if (state >> (32 - LogTabSize) >= frequency)
            {
                chunks[i] = (int)(state & 0xFFFF);
                state >>= 16;
            }

            state = ((state / frequency) << LogTabSize) + (uint)reverse[cluster][starts[cluster][token] + (int)(state % frequency)];
        }

        writer.Write(32, state);
        for (int i = 0; i < ctx.Length; i++)
        {
            if (chunks[i] >= 0) writer.Write(16, (uint)chunks[i]);
            _ = Tokenize(configs[contextMap[ctx[i]]], values[i], out int bits, out uint extra);
            writer.Write(bits, extra);
        }
    }

    /// <summary>Codes <paramref name="tokens"/> with their own codes: header and stream.</summary>
    public static void WriteStandalone(JxlBitWriter writer, int contexts, JxlTokens tokens)
    {
        JxlEntropyEncoder encoder = Build(contexts, [tokens]);
        encoder.WriteHeader(writer);
        encoder.WriteStream(writer, tokens);
    }

    /// <summary>The token of <paramref name="value"/> and its raw bits (HybridUintConfig::Encode).</summary>
    public static uint Tokenize(JxlHybridUint config, uint value, out int bitCount, out uint bits)
    {
        if (value < config.SplitToken)
        {
            bitCount = 0;
            bits = 0;
            return value;
        }

        int n = JxlPrefixCode.Log2Floor(value);
        uint m = value - (1u << n);
        int msb = config.MsbInToken;
        int lsb = config.LsbInToken;
        uint token = config.SplitToken + (uint)((n - config.SplitExponent) << (msb + lsb)) + ((m >> (n - msb)) << lsb) + (m & ((1u << lsb) - 1));
        bitCount = n - msb - lsb;
        bits = (value >> lsb) & (uint)((1UL << bitCount) - 1);
        return token;
    }

    private static (JxlHybridUint Config, long[] Counts) ChooseConfig(List<(uint Value, long Count)> values)
    {
        JxlHybridUint best = ClusteringConfig;
        long[] bestCounts = [];
        double bestCost = double.MaxValue;
        foreach (JxlHybridUint config in CandidateConfigs)
        {
            long[] histogram = new long[16];
            long extra = 0;
            uint maxToken = 0;
            foreach ((uint value, long count) in values)
            {
                uint token = Tokenize(config, value, out int bits, out _);
                if (token >= histogram.Length) Array.Resize(ref histogram, Math.Max((int)token + 1, histogram.Length * 2));
                histogram[token] += count;
                extra += bits * count;
                maxToken = Math.Max(maxToken, token);
            }

            if (maxToken >= 256)
            {
                continue;
            }

            Array.Resize(ref histogram, (int)maxToken + 1);
            int used = histogram.Count(c => c > 0);
            double cost = Entropy(histogram) + extra + 6 * used;
            if (cost < bestCost)
            {
                bestCost = cost;
                best = config;
                bestCounts = histogram;
            }
        }

        return (best, bestCounts.Length == 0 ? [1] : bestCounts);
    }

    // Farthest-point seeding and nearest-centre assignment (FastClusterHistograms), then pairwise merges that save bits.
    private static byte[] Cluster(long[][] histograms, out int clusterCount)
    {
        int contexts = histograms.Length;
        byte[] map = new byte[contexts];
        int[] used = [.. Enumerable.Range(0, contexts).Where(c => histograms[c].Any(v => v > 0))];
        if (used.Length <= 1)
        {
            clusterCount = 1;
            return map;
        }

        double[] own = new double[contexts];
        foreach (int c in used) own[c] = Entropy(histograms[c]);
        var centres = new List<int> { used.MaxBy(c => histograms[c].Sum()) };
        double[] distance = new double[contexts];
        int[] nearest = new int[contexts];
        foreach (int c in used)
        {
            distance[c] = MergeCost(histograms[c], own[c], histograms[centres[0]], own[centres[0]]);
        }

        while (centres.Count < MaxClusters)
        {
            int farthest = used.MaxBy(c => distance[c]);
            if (distance[farthest] < MinClusterDistance) break;
            centres.Add(farthest);
            foreach (int c in used)
            {
                double d = MergeCost(histograms[c], own[c], histograms[farthest], own[farthest]);
                if (d < distance[c])
                {
                    distance[c] = d;
                    nearest[c] = centres.Count - 1;
                }
            }
        }

        var members = new List<List<int>>();
        var sums = new List<long[]>();
        for (int i = 0; i < centres.Count; i++)
        {
            members.Add([]);
            sums.Add([]);
        }

        foreach (int c in used)
        {
            members[nearest[c]].Add(c);
            sums[nearest[c]] = Add(sums[nearest[c]], histograms[c]);
        }

        // Merge the cheapest pair while it costs less than a histogram header.
        while (sums.Count > 1)
        {
            double[] costs = [.. sums.Select(Entropy)];
            (int a, int b, double delta) best = (-1, -1, double.MaxValue);
            for (int a = 0; a < sums.Count; a++)
            {
                for (int b = a + 1; b < sums.Count; b++)
                {
                    double delta = MergeCost(sums[a], costs[a], sums[b], costs[b]);
                    if (delta < best.delta) best = (a, b, delta);
                }
            }

            if (best.delta >= HeaderCost(sums[best.a]) + HeaderCost(sums[best.b]) - HeaderCost(Add(sums[best.a], sums[best.b])))
            {
                break;
            }

            sums[best.a] = Add(sums[best.a], sums[best.b]);
            members[best.a].AddRange(members[best.b]);
            sums.RemoveAt(best.b);
            members.RemoveAt(best.b);
        }

        // Number clusters by their first context, so the map starts 0, 1, 2… and suits move-to-front.
        int[] clusterOf = new int[contexts];
        for (int i = 0; i < members.Count; i++)
        {
            foreach (int c in members[i]) clusterOf[c] = i;
        }

        int[] renumber = new int[members.Count];
        Array.Fill(renumber, -1);
        int next = 0;
        foreach (int c in used)
        {
            if (renumber[clusterOf[c]] < 0) renumber[clusterOf[c]] = next++;
            map[c] = (byte)renumber[clusterOf[c]];
        }

        clusterCount = next;
        return map;
    }

    private static double HeaderCost(long[] histogram) => 16 + 5 * histogram.Count(c => c > 0);

    private static long[] Add(long[] a, long[] b)
    {
        long[] sum = new long[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < a.Length; i++) sum[i] += a[i];
        for (int i = 0; i < b.Length; i++) sum[i] += b[i];
        return sum;
    }

    private static double MergeCost(long[] a, double costA, long[] b, double costB)
    {
        long totalA = 0;
        long totalB = 0;
        foreach (long v in a) totalA += v;
        foreach (long v in b) totalB += v;
        long total = totalA + totalB;
        double merged = 0;
        int length = Math.Max(a.Length, b.Length);
        for (int i = 0; i < length; i++)
        {
            long v = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
            if (v > 0) merged += v * Math.Log2((double)total / v);
        }

        return merged - costA - costB;
    }

    /// <summary>The Shannon cost in bits of coding the histogram's symbols with their own probabilities.</summary>
    public static double Entropy(long[] histogram)
    {
        long total = 0;
        foreach (long v in histogram) total += v;
        double bits = 0;
        foreach (long v in histogram)
        {
            if (v > 0 && v < total) bits += v * Math.Log2((double)total / v);
        }

        return bits;
    }

    // ---- Histograms ----

    private static int Precision(int logCount, int shift) => Math.Max(0, Math.Min(logCount, shift - ((LogTabSize - logCount) >> 1)));

    /// <summary>
    /// A distribution summing to 4096 that the histogram code can represent, and its method: -1 simple (one or two
    /// symbols), 0 flat, else the precision shift + 1. Normalizes for every shift and keeps the cheapest.
    /// </summary>
    private static Histogram Normalize(long[] frequencies)
    {
        int alphabet = frequencies.Length;
        while (alphabet > 0 && frequencies[alphabet - 1] == 0) alphabet--;
        int symbols = frequencies.Count(f => f > 0);
        long total = frequencies.Sum();
        if (symbols <= 1)
        {
            int[] single = new int[Math.Max(alphabet, 1)];
            single[Math.Max(alphabet - 1, 0)] = TabSize;
            return new Histogram(single, -1, 0);
        }

        if (symbols == 2)
        {
            int first = Array.FindIndex(frequencies, f => f > 0);
            int second = Array.FindLastIndex(frequencies, f => f > 0);
            int[] pair = new int[alphabet];
            pair[first] = (int)Math.Clamp(Math.Round(frequencies[first] * (double)TabSize / total), 1, TabSize - 1);
            pair[second] = TabSize - pair[first];
            return new Histogram(pair, -1, 0);
        }

        int[] best = [];
        int bestMethod = 0;
        int bestOmit = 0;
        double bestCost = double.MaxValue;
        if (alphabet <= 256)
        {
            int[] flat = new int[alphabet];
            for (int i = 0; i < alphabet; i++) flat[i] = TabSize / alphabet + (i < TabSize % alphabet ? 1 : 0);
            best = flat;
            bestCost = HistogramBits(new Histogram(flat, 0, 0)) + DataBits(frequencies, flat);
        }

        for (int shift = 0; shift < LogTabSize; shift++)
        {
            if (NormalizeWithShift(frequencies, alphabet, total, shift, out int omit) is not { } counts) continue;
            double cost = HistogramBits(new Histogram(counts, shift + 1, omit)) + DataBits(frequencies, counts);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = counts;
                bestMethod = shift + 1;
                bestOmit = omit;
            }
        }

        return best.Length > 0 ? new Histogram(best, bestMethod, bestOmit) : throw new InvalidOperationException("No ANS histogram fits.");
    }

    private static int[]? NormalizeWithShift(long[] frequencies, int alphabet, long total, int shift, out int omit)
    {
        int[] counts = new int[alphabet];
        omit = -1;
        for (int i = 0; i < alphabet; i++)
        {
            if (frequencies[i] > 0 && (omit < 0 || frequencies[i] > frequencies[omit])) omit = i;
        }

        for (int attempt = 0; attempt < alphabet; attempt++)
        {
            int sum = 0;
            for (int i = 0; i < alphabet; i++)
            {
                if (i == omit || frequencies[i] == 0)
                {
                    counts[i] = 0;
                    continue;
                }

                counts[i] = Representable(Math.Clamp((int)Math.Round(frequencies[i] * (double)TabSize / total), 1, TabSize - 1), shift);
                sum += counts[i];
            }

            // Reduce the largest counts until the omitted symbol keeps a positive remainder.
            while (sum >= TabSize)
            {
                int largest = -1;
                for (int i = 0; i < alphabet; i++)
                {
                    if (i != omit && counts[i] > 1 && (largest < 0 || counts[i] > counts[largest])) largest = i;
                }

                if (largest < 0) return null;
                int reduced = Lower(counts[largest], shift);
                sum -= counts[largest] - reduced;
                counts[largest] = reduced;
            }

            counts[omit] = TabSize - sum;
            // The decoder omits the first symbol of the largest written width; the omitted width is written as at
            // least that of every symbol after it and more than every one before, which is impossible past 2048.
            int conflict = Array.FindIndex(counts, 0, omit, c => c >= 2048);
            if (conflict < 0) return counts;
            omit = conflict;
        }

        return null;
    }

    // The nearest count the shift can represent.
    private static int Representable(int count, int shift)
    {
        int log = JxlPrefixCode.Log2Floor((uint)count);
        int drop = log - Precision(log, shift);
        int step = 1 << drop;
        int down = count & ~(step - 1);
        int up = down + step;
        int result = count - down <= up - count || up >= TabSize ? down : up;
        return Math.Max(result, 1);
    }

    // The largest representable count below count.
    private static int Lower(int count, int shift)
    {
        for (int c = count - 1; c >= 1; c--)
        {
            if (Representable(c, shift) == c) return c;
        }

        return 1;
    }

    private static double DataBits(long[] frequencies, int[] counts)
    {
        double bits = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (frequencies[i] > 0) bits += frequencies[i] * (LogTabSize - Math.Log2(counts[i]));
        }

        return bits;
    }

    private static long HistogramBits(Histogram histogram)
    {
        var writer = new JxlBitWriter();
        WriteHistogram(writer, histogram);
        return writer.BitLength;
    }

    private static void WriteHistogram(JxlBitWriter writer, Histogram histogram)
    {
        (int[] counts, int method, int omit) = histogram;
        int alphabet = counts.Length;
        while (alphabet > 0 && counts[alphabet - 1] == 0) alphabet--;
        int[] nonzero = [.. Enumerable.Range(0, alphabet).Where(i => counts[i] > 0)];
        if (method < 0)
        {
            writer.Write(true);
            writer.Write(1, (uint)Math.Max(nonzero.Length - 1, 0));
            foreach (int symbol in nonzero.Length == 0 ? [0] : nonzero) WriteVarLenUint8(writer, symbol);
            if (nonzero.Length == 2) writer.Write(LogTabSize, (uint)counts[nonzero[0]]);
            return;
        }

        writer.Write(false);
        if (method == 0)
        {
            writer.Write(true);
            WriteVarLenUint8(writer, alphabet - 1);
            return;
        }

        writer.Write(false);
        // Elias gamma-like code of the method, without the terminating zero at the largest length.
        int log = JxlPrefixCode.Log2Floor((uint)method);
        writer.Write(log, (1u << log) - 1);
        if (log != 3) writer.Write(false);
        writer.Write(log, (uint)(method & ((1 << log) - 1)));
        WriteVarLenUint8(writer, alphabet - 3);

        int shift = method - 1;
        int[] widths = new int[alphabet];
        for (int i = 0; i < alphabet; i++) widths[i] = counts[i] > 0 ? JxlPrefixCode.Log2Floor((uint)counts[i]) + 1 : 0;
        // The omitted symbol takes the remainder. Its width is written as at least that of every later symbol and
        // more than every earlier one, so the decoder finds it as the first of the widest.
        int omitWidth = 10;
        for (int i = 0; i < alphabet; i++)
        {
            if (i != omit && counts[i] > 0) omitWidth = Math.Max(omitWidth, widths[i] + (i < omit ? 1 : 0));
        }

        widths[omit] = omitWidth;

        int[] same = new int[alphabet];
        int last = 0;
        for (int i = 1; i <= alphabet; i++)
        {
            if (i == alphabet || i == omit || i == omit + 1 || counts[i] != counts[last])
            {
                same[last] = i - last;
                last = i;
            }
        }

        const int MinReps = 5;
        const int Rep = LogTabSize + 1;
        for (int i = 0; i < alphabet; i++)
        {
            writer.Write(BitWidthLengths[widths[i]], BitWidthSymbols[widths[i]]);
            if (same[i] >= MinReps)
            {
                writer.Write(BitWidthLengths[Rep], BitWidthSymbols[Rep]);
                WriteVarLenUint8(writer, same[i] - MinReps);
                i += same[i] - 1;
            }
        }

        if (shift != 0)
        {
            for (int i = 0; i < alphabet; i++)
            {
                if (widths[i] > 1 && i != omit)
                {
                    int bitCount = Precision(widths[i] - 1, shift);
                    int drop = widths[i] - 1 - bitCount;
                    writer.Write(bitCount, (uint)((counts[i] >> drop) - (1 << bitCount)));
                }

                if (same[i] >= MinReps) i += same[i] - 1;
            }
        }
    }

    private static void WriteVarLenUint8(JxlBitWriter writer, int n)
    {
        if (n == 0)
        {
            writer.Write(false);
            return;
        }

        writer.Write(true);
        int bits = JxlPrefixCode.Log2Floor((uint)n);
        writer.Write(3, (uint)bits);
        writer.Write(bits, (uint)(n - (1 << bits)));
    }

    private static void WriteConfig(JxlBitWriter writer, JxlHybridUint config, int logAlphaSize)
    {
        writer.Write(CeilLog2(logAlphaSize + 1), (uint)config.SplitExponent);
        if (config.SplitExponent == logAlphaSize) return;
        writer.Write(CeilLog2(config.SplitExponent + 1), (uint)config.MsbInToken);
        writer.Write(CeilLog2(config.SplitExponent - config.MsbInToken + 1), (uint)config.LsbInToken);
    }

    private static int CeilLog2(int value) => value <= 1 ? 0 : JxlPrefixCode.Log2Floor((uint)value - 1) + 1;

    // ---- Context map ----

    private static void WriteContextMap(JxlBitWriter writer, byte[] map, int clusters)
    {
        int bits = CeilLog2(clusters);
        var simple = new JxlBitWriter();
        if (bits <= 3)
        {
            simple.Write(true);
            simple.Write(2, (uint)bits);
            foreach (byte cluster in map) simple.Write(bits, cluster);
        }

        JxlBitWriter? best = bits <= 3 ? simple : null;
        foreach (bool moveToFront in (ReadOnlySpan<bool>)[false, true])
        {
            byte[] symbols = moveToFront ? MoveToFront(map) : map;
            var tokens = new JxlTokens();
            foreach (byte symbol in symbols) tokens.Add(0, symbol);
            var coded = new JxlBitWriter();
            coded.Write(false);
            coded.Write(moveToFront);
            WriteStandalone(coded, 1, tokens);
            if (best is null || coded.BitLength < best.BitLength) best = coded;
        }

        writer.Append(best!);
    }

    private static byte[] MoveToFront(byte[] values)
    {
        Span<byte> order = stackalloc byte[256];
        for (int i = 0; i < order.Length; i++) order[i] = (byte)i;
        byte[] result = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            int index = order.IndexOf(values[i]);
            result[i] = (byte)index;
            if (index != 0)
            {
                order[..index].CopyTo(order[1..]);
                order[0] = values[i];
            }
        }

        return result;
    }

    /// <summary>A normalized distribution; Method is -1 for the simple code, 0 for flat, else the shift + 1.</summary>
    private readonly record struct Histogram(int[] Counts, int Method, int Omit);

    // ---- ANS tables ----

    // Per symbol, the first index of its slots; then the slots that decode to (symbol, offset) in offset order.
    private static (int[] Starts, int[] Slots) ReverseMap(int[] counts, int logAlphaSize)
    {
        int tableSize = 1 << logAlphaSize;
        var table = new JxlAliasEntry[tableSize];
        JxlEntropyCode.InitAliasTable(counts, logAlphaSize, table);
        int[] starts = new int[counts.Length + 1];
        for (int s = 0; s < counts.Length; s++) starts[s + 1] = starts[s] + counts[s];
        int[] slots = new int[TabSize];
        int logEntrySize = LogTabSize - logAlphaSize;
        for (int slot = 0; slot < TabSize; slot++)
        {
            JxlAliasEntry entry = table[slot >> logEntrySize];
            int position = slot & ((1 << logEntrySize) - 1);
            bool greater = position >= entry.Cutoff;
            int symbol = greater ? entry.RightValue : slot >> logEntrySize;
            int offset = (greater ? entry.Offsets1 : 0) + position;
            slots[starts[symbol] + offset] = slot;
        }

        return (starts, slots);
    }
}
