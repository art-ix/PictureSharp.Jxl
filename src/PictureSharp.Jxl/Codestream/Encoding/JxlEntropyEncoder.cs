using PictureSharp.Formats.Jxl.Codestream.Entropy;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// Clustered ANS codes for a set of contexts and the streams coded with them: the counterpart of
/// <see cref="JxlEntropyCode"/> and <see cref="JxlSymbolReader"/> without LZ77. Modelled on libjxl v0.12.0
/// lib/jxl/enc_ans.cc, enc_cluster.cc and enc_context_map.cc (BSD-3-Clause); the histogram normalization
/// (<see cref="JxlAnsHistogram"/>), the clustering (<see cref="JxlHistogramClustering"/>) and the choice of hybrid
/// integer configurations are simpler searches of the same formats.
/// </summary>
internal sealed class JxlEntropyEncoder
{
    private const int LogTabSize = JxlEntropyCode.AnsLogTabSize;
    private const int MinLogAlphaSize = 5;
    private const int MaxTokens = 256;
    private const int SmallValues = 4096;
    // Bits per used token that the histogram header costs, roughly.
    private const int TokenHeaderBits = 6;

    private static readonly JxlHybridUint[] CandidateConfigs =
    [
        new(4, 2, 0), new(4, 1, 0), new(4, 0, 0), new(4, 1, 1), new(4, 2, 1), new(4, 1, 2), new(0, 0, 0), new(1, 0, 0),
        new(2, 0, 0), new(3, 0, 0), new(3, 1, 0), new(3, 2, 0), new(5, 0, 0), new(5, 1, 0), new(5, 2, 0), new(6, 0, 0),
        new(6, 1, 0), new(6, 2, 0), new(7, 0, 0), new(8, 0, 0),
    ];

    private readonly byte[] contextMap;
    private readonly JxlHybridUint[] configs;
    private readonly JxlAnsHistogram[] histograms;
    private readonly JxlAnsEncodingTable[] tables;
    private readonly int logAlphaSize;

    private JxlEntropyEncoder(byte[] contextMap, JxlHybridUint[] configs, JxlAnsHistogram[] histograms, int logAlphaSize)
    {
        this.contextMap = contextMap;
        this.configs = configs;
        this.histograms = histograms;
        this.logAlphaSize = logAlphaSize;
        tables = [.. histograms.Select(histogram => new JxlAnsEncodingTable(histogram.Counts, logAlphaSize))];
    }

    /// <summary>Chooses the codes of <paramref name="contexts"/> contexts for all values of <paramref name="streams"/>.</summary>
    public static JxlEntropyEncoder Build(int contexts, IReadOnlyList<JxlTokens> streams)
    {
        // Token histograms per context under one configuration decide the clusters, then the exact values of each
        // cluster choose its hybrid integer configuration.
        byte[] contextMap = JxlHistogramClustering.Cluster(TokenHistograms(contexts, streams), out int clusters);
        List<(uint Value, long Count)>[] values = ValueHistograms(contextMap, clusters, streams);
        var configs = new JxlHybridUint[clusters];
        var tokenCounts = new long[clusters][];
        for (int c = 0; c < clusters; c++)
        {
            (configs[c], tokenCounts[c]) = ChooseConfig(values[c]);
        }

        int logAlphaSize = MinLogAlphaSize;
        for (int c = 0; c < clusters; c++)
        {
            while (tokenCounts[c].Length > 1 << logAlphaSize || configs[c].SplitExponent > logAlphaSize) logAlphaSize++;
        }

        return new JxlEntropyEncoder(contextMap, configs, [.. tokenCounts.Select(JxlAnsHistogram.Normalize)], logAlphaSize);
    }

    /// <summary>Writes the codes: no LZ77, the context map, ANS, the alphabet size, the configurations and histograms.</summary>
    public void WriteHeader(JxlBitWriter writer)
    {
        writer.Write(false);
        if (contextMap.Length > 1) WriteContextMap(writer, contextMap, histograms.Length);
        writer.Write(false);
        writer.Write(2, (uint)(logAlphaSize - MinLogAlphaSize));
        foreach (JxlHybridUint config in configs) WriteConfig(writer, config, logAlphaSize);
        foreach (JxlAnsHistogram histogram in histograms) histogram.Write(writer);
    }

    /// <summary>Writes the ANS stream of <paramref name="tokens"/>: the final state, then the renormalization and raw bits.</summary>
    public void WriteStream(JxlBitWriter writer, JxlTokens tokens)
    {
        ReadOnlySpan<int> ctx = tokens.Contexts;
        ReadOnlySpan<uint> values = tokens.Values;
        int[] chunks = new int[ctx.Length];
        // ANS codes backwards; the decoder reads the state, then per symbol a renormalization chunk and the raw bits.
        uint state = JxlEntropyCode.AnsSignature;
        for (int i = ctx.Length - 1; i >= 0; i--)
        {
            int cluster = contextMap[ctx[i]];
            uint token = configs[cluster].Encode(values[i], out _, out _);
            JxlAnsEncodingTable table = tables[cluster];
            uint frequency = table.Frequency(token);
            if (frequency == 0) throw new InvalidOperationException("A token has no probability.");
            chunks[i] = -1;
            if (state >> (32 - LogTabSize) >= frequency)
            {
                chunks[i] = (int)(state & 0xFFFF);
                state >>= 16;
            }

            state = ((state / frequency) << LogTabSize) + table.Slot(token, state % frequency);
        }

        writer.Write(32, state);
        for (int i = 0; i < ctx.Length; i++)
        {
            if (chunks[i] >= 0) writer.Write(16, (uint)chunks[i]);
            // Tokenized again rather than stored: fewer arrays keep encoding faster.
            _ = configs[contextMap[ctx[i]]].Encode(values[i], out int bitCount, out uint bits);
            writer.Write(bitCount, bits);
        }
    }

    /// <summary>Codes <paramref name="tokens"/> with their own codes: header and stream.</summary>
    public static void WriteStandalone(JxlBitWriter writer, int contexts, JxlTokens tokens)
    {
        JxlEntropyEncoder encoder = Build(contexts, [tokens]);
        encoder.WriteHeader(writer);
        encoder.WriteStream(writer, tokens);
    }

    // Per context, the counts of the tokens under the default configuration.
    private static long[][] TokenHistograms(int contexts, IReadOnlyList<JxlTokens> streams)
    {
        var histograms = new long[contexts][];
        for (int c = 0; c < contexts; c++) histograms[c] = [];
        foreach (JxlTokens stream in streams)
        {
            ReadOnlySpan<int> ctx = stream.Contexts;
            ReadOnlySpan<uint> streamValues = stream.Values;
            for (int i = 0; i < ctx.Length; i++)
            {
                uint token = JxlHybridUint.Default.Encode(streamValues[i], out _, out _);
                Increment(ref histograms[ctx[i]], token, 1);
            }
        }

        return histograms;
    }

    // Per cluster, every distinct value with its count, small values first in value order.
    private static List<(uint Value, long Count)>[] ValueHistograms(byte[] contextMap, int clusters, IReadOnlyList<JxlTokens> streams)
    {
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

        return values;
    }

    // The candidate configuration with the fewest estimated bits, and its token counts.
    private static (JxlHybridUint Config, long[] Counts) ChooseConfig(List<(uint Value, long Count)> values)
    {
        JxlHybridUint best = JxlHybridUint.Default;
        long[] bestCounts = [];
        double bestCost = double.MaxValue;
        foreach (JxlHybridUint config in CandidateConfigs)
        {
            long[] histogram = new long[16];
            long extra = 0;
            uint maxToken = 0;
            foreach ((uint value, long count) in values)
            {
                uint token = config.Encode(value, out int bits, out _);
                Increment(ref histogram, token, count);
                extra += bits * count;
                maxToken = Math.Max(maxToken, token);
            }

            if (maxToken >= MaxTokens)
            {
                continue;
            }

            Array.Resize(ref histogram, (int)maxToken + 1);
            int used = histogram.Count(c => c > 0);
            double cost = JxlHistogramClustering.Entropy(histogram) + extra + TokenHeaderBits * used;
            if (cost < bestCost)
            {
                bestCost = cost;
                best = config;
                bestCounts = histogram;
            }
        }

        return (best, bestCounts.Length == 0 ? [1] : bestCounts);
    }

    // Adds count to the token's bin, doubling the histogram when the token is beyond it.
    private static void Increment(ref long[] histogram, uint token, long count)
    {
        if (token >= histogram.Length) Array.Resize(ref histogram, Math.Max((int)token + 1, histogram.Length * 2));
        histogram[token] += count;
    }

    private static void WriteConfig(JxlBitWriter writer, JxlHybridUint config, int logAlphaSize)
    {
        writer.Write(JxlHybridUint.SplitExponentBits(logAlphaSize), (uint)config.SplitExponent);
        if (config.SplitExponent == logAlphaSize) return;
        writer.Write(JxlHybridUint.MsbBits(config.SplitExponent), (uint)config.MsbInToken);
        writer.Write(JxlHybridUint.LsbBits(config.SplitExponent, config.MsbInToken), (uint)config.LsbInToken);
    }

    // The cheapest of the simple code (up to 8 clusters) and an entropy-coded map with or without move-to-front.
    private static void WriteContextMap(JxlBitWriter writer, byte[] map, int clusters)
    {
        int bits = CeilLog2(clusters);
        JxlBitWriter? best = null;
        if (bits <= 3)
        {
            best = new JxlBitWriter();
            best.Write(true);
            best.Write(2, (uint)bits);
            foreach (byte cluster in map) best.Write(bits, cluster);
        }

        foreach (bool moveToFront in (ReadOnlySpan<bool>)[false, true])
        {
            byte[] symbols = moveToFront ? JxlMoveToFront.Forward(map) : map;
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
}
