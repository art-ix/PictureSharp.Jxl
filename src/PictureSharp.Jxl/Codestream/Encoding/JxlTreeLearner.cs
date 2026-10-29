using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>A node of an encoder MA tree: a split on <c>Property &gt; Split</c> (left) or a leaf (Property -1).</summary>
internal readonly record struct JxlTreeNode(int Property, int Split, int Left, int Right, JxlPredictor Predictor)
{
    public bool IsLeaf => Property < 0;

    public static JxlTreeNode Leaf(JxlPredictor predictor) => new(-1, 0, 0, 0, predictor);
}

/// <summary>
/// Learns an MA tree from pixel samples: greedy splits on quantized properties that minimize the estimated coded
/// size of the residuals, choosing each leaf's predictor. Ported in simplified form from libjxl v0.12.0
/// lib/jxl/modular/encoding/enc_ma.cc (FindBestSplit, PreQuantizeProperties; BSD-3-Clause): no static-property
/// preferences, no sample deduplication, and quantiles of the sampled values as split candidates.
/// </summary>
internal sealed class JxlTreeLearner : IDisposable
{
    // The residual tokens the cost estimates use (libjxl's TreeSamples).
    private static readonly JxlHybridUint SampleConfig = new(4, 1, 2);
    private const float MinProbability = 1.0f / JxlEntropyCode.AnsTabSize;
    // The largest range of sample values quantized by counting rather than sorting.
    private const long MaxCountedRange = 1 << 20;

    private readonly int[] properties;
    private readonly JxlPredictor[] predictors;
    private readonly int maxPropertyValues;
    private readonly JxlSampleBuffer<int>[] values;
    private readonly JxlSampleBuffer<byte>[] tokens;
    private readonly JxlSampleBuffer<byte>[] extraBits;

    /// <param name="properties">The property indices the tree may split on.</param>
    /// <param name="predictors">The predictors leaves may use; the first is the root's.</param>
    /// <param name="maxPropertyValues">The most bins a property is quantized to.</param>
    public JxlTreeLearner(int[] properties, JxlPredictor[] predictors, int maxPropertyValues)
    {
        this.properties = properties;
        this.predictors = predictors;
        this.maxPropertyValues = maxPropertyValues;
        values = [.. properties.Select(_ => new JxlSampleBuffer<int>())];
        tokens = [.. predictors.Select(_ => new JxlSampleBuffer<byte>())];
        extraBits = [.. predictors.Select(_ => new JxlSampleBuffer<byte>())];
    }

    public void Dispose()
    {
        foreach (var buffer in values) buffer.Dispose();
        foreach (var buffer in tokens) buffer.Dispose();
        foreach (var buffer in extraBits) buffer.Dispose();
    }

    public int Count => tokens[0].Count;

    /// <summary>Adds a sample: all properties of the pixel and its residual for each predictor, in predictor order.</summary>
    public void Add(ReadOnlySpan<int> pixelProperties, ReadOnlySpan<long> residuals)
    {
        for (int i = 0; i < properties.Length; i++) values[i].Add(pixelProperties[properties[i]]);
        for (int i = 0; i < predictors.Length; i++)
        {
            uint token = SampleConfig.Encode(PackSigned(residuals[i]), out int bits, out _);
            tokens[i].Add((byte)Math.Min(token, 255));
            extraBits[i].Add((byte)bits);
        }
    }

    /// <summary>Appends the samples of <paramref name="batch"/> (a learner of the same properties and predictors) in their order.</summary>
    public void Append(JxlTreeLearner batch)
    {
        for (int i = 0; i < values.Length; i++) values[i].AddRange(batch.values[i].Span);
        for (int i = 0; i < tokens.Length; i++)
        {
            tokens[i].AddRange(batch.tokens[i].Span);
            extraBits[i].AddRange(batch.extraBits[i].Span);
        }
    }

    /// <summary>The tree; a single leaf with the first predictor when there are no samples or no useful split.</summary>
    public List<JxlTreeNode> Learn(float threshold, CancellationToken cancellationToken)
    {
        var tree = new List<JxlTreeNode> { JxlTreeNode.Leaf(predictors[0]) };
        int count = Count;
        if (count == 0) return tree;

        // Quantize every property to at most maxPropertyValues bins at quantiles of its samples.
        int[][] thresholds = new int[properties.Length][];
        byte[][] bins = new byte[properties.Length][];
        for (int p = 0; p < properties.Length; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (thresholds[p], bins[p]) = Quantize(values[p].Span);
        }

        byte[][] tok = [.. tokens.Select(list => list.Span.ToArray())];
        byte[][] extra = [.. extraBits.Select(list => list.Span.ToArray())];
        var learner = new Search(bins, tok, extra, thresholds, threshold);
        var nodes = new Stack<(int Node, int Begin, int End)>();
        nodes.Push((0, 0, count));
        while (nodes.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int node, int begin, int end) = nodes.Pop();
            int current = Array.IndexOf(predictors, tree[node].Predictor);
            if (learner.FindSplit(begin, end, current, out int property, out int bin, out int position, out int leftPredictor,
                    out int rightPredictor) is false)
            {
                continue;
            }

            int left = tree.Count;
            tree[node] = new JxlTreeNode(properties[property], thresholds[property][bin], left, left + 1, JxlPredictor.Zero);
            tree.Add(new JxlTreeNode(-1, 0, 0, 0, predictors[leftPredictor]));
            tree.Add(new JxlTreeNode(-1, 0, 0, 0, predictors[rightPredictor]));
            learner.Partition(begin, position, end, property, bin);
            // Samples at or below the split come first: they belong to the right child.
            nodes.Push((left + 1, begin, position));
            nodes.Push((left, position, end));
        }

        return tree;
    }

    // The cut values at the quantiles of the samples, and the bin of every sample: the number of cuts below it. A
    // counting sort over the value range when it is small, else a sort; both give the same quantiles.
    private (int[] Cuts, byte[] Bins) Quantize(ReadOnlySpan<int> samples)
    {
        int count = samples.Length;
        int min = int.MaxValue;
        int max = int.MinValue;
        foreach (int v in samples)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        var cuts = new List<int>();
        byte[] bins = new byte[count];
        if ((long)max - min < MaxCountedRange)
        {
            // Counts per value, then the value at each quantile position from the running total.
            int[] histogram = new int[max - min + 1];
            foreach (int v in samples) histogram[v - min]++;
            int value = 0;
            long below = histogram[0];
            for (int k = 1; k < maxPropertyValues; k++)
            {
                long position = (long)k * count / maxPropertyValues;
                while (below <= position) below += histogram[++value];
                int v = value + min;
                if (v < max && (cuts.Count == 0 || v > cuts[^1])) cuts.Add(v);
            }

            // The bin of every value: the number of cuts below it.
            byte[] binOf = new byte[histogram.Length];
            int bin = 0;
            for (int v = 0; v < binOf.Length; v++)
            {
                while (bin < cuts.Count && cuts[bin] < v + min) bin++;
                binOf[v] = (byte)bin;
            }

            for (int i = 0; i < count; i++) bins[i] = binOf[samples[i] - min];
            return ([.. cuts], bins);
        }

        int[] sorted = samples.ToArray();
        Array.Sort(sorted);
        for (int k = 1; k < maxPropertyValues; k++)
        {
            int v = sorted[(int)((long)k * count / maxPropertyValues)];
            if (v < sorted[^1] && (cuts.Count == 0 || v > cuts[^1])) cuts.Add(v);
        }

        int[] thresholds = [.. cuts];
        for (int i = 0; i < count; i++)
        {
            int index = Array.BinarySearch(thresholds, samples[i]);
            bins[i] = (byte)(index >= 0 ? index : ~index);
        }

        return (thresholds, bins);
    }

    private sealed class Search(byte[][] bins, byte[][] tokens, byte[][] extra, int[][] thresholds, float threshold)
    {
        private readonly float changePenalty = 800.0f / (100.0f + threshold);

        // Buffers reused between nodes, grown as needed.
        private int[][] counts = [];
        private int[][] present = [];
        private int[] presentCount = [];
        private long[] totalExtra = [];
        private int[] below = [];
        private int[] above = [];
        private int[] used = [];
        private float[] costLeft = [];
        private float[] costRight = [];
        private float[] scoreLeft = [];
        private float[] scoreRight = [];
        private int[] predictorLeft = [];
        private int[] predictorRight = [];
        private int[] increase = [];
        private long[] extraIncrease = [];

        public bool FindSplit(int begin, int end, int current, out int bestProperty, out int bestBin, out int bestPosition,
            out int bestLeft, out int bestRight)
        {
            bestProperty = bestBin = bestPosition = bestLeft = bestRight = 0;
            int predictorCount = tokens.Length;
            int symbols = 0;
            for (int p = 0; p < predictorCount; p++)
            {
                for (int i = begin; i < end; i++) symbols = Math.Max(symbols, tokens[p][i] + 1);
            }

            if (counts.Length != predictorCount)
            {
                counts = [.. tokens.Select(_ => Array.Empty<int>())];
                present = [.. tokens.Select(_ => Array.Empty<int>())];
                presentCount = new int[predictorCount];
                totalExtra = new long[predictorCount];
            }

            for (int p = 0; p < predictorCount; p++)
            {
                Reuse(ref counts[p], symbols);
                Reuse(ref present[p], symbols);
                int[] pc = counts[p];
                byte[] tok = tokens[p];
                byte[] ext = extra[p];
                long sumExtra = 0;
                for (int i = begin; i < end; i++)
                {
                    pc[tok[i]]++;
                    sumExtra += ext[i];
                }

                totalExtra[p] = sumExtra;
                // The symbols of the node in increasing order: the cost sums skip the others, which add nothing.
                int n = 0;
                for (int t = 0; t < symbols; t++)
                {
                    if (pc[t] != 0) present[p][n++] = t;
                }

                presentCount[p] = n;
            }

            int total = end - begin;
            float baseBits = EstimateBits(counts[current], present[current], presentCount[current], total) + totalExtra[current];
            if (baseBits <= threshold) return false;

            float bestCost = float.MaxValue;
            Reuse(ref below, symbols);
            Reuse(ref above, symbols);
            for (int property = 0; property < bins.Length; property++)
            {
                byte[] bin = bins[property];
                int binCount = thresholds[property].Length + 1;
                if (binCount < 2) continue;
                Reuse(ref used, binCount);
                int first = binCount;
                int last = 0;
                for (int i = begin; i < end; i++)
                {
                    used[bin[i]]++;
                    first = Math.Min(first, bin[i]);
                    last = Math.Max(last, bin[i]);
                }

                if (first == last) continue;
                int splits = last - first;
                Reuse(ref costLeft, splits);
                Reuse(ref costRight, splits);
                Reuse(ref scoreLeft, splits);
                Reuse(ref scoreRight, splits);
                Reuse(ref predictorLeft, splits);
                Reuse(ref predictorRight, splits);
                scoreLeft.AsSpan(0, splits).Fill(float.MaxValue);
                scoreRight.AsSpan(0, splits).Fill(float.MaxValue);
                if (increase.Length < binCount * symbols) increase = new int[binCount * symbols];
                Reuse(ref extraIncrease, binCount);
                for (int p = 0; p < predictorCount; p++)
                {
                    // Dense symbols are faster to clear as a contiguous active-bin range; sparse
                    // predictors initialize just the cells that can be read or incremented.
                    bool dense = presentCount[p] * 4 >= symbols;
                    if (dense) increase.AsSpan(first * symbols, (last - first + 1) * symbols).Clear();
                    for (int b = first; b <= last; b++)
                    {
                        if (used[b] == 0) continue;
                        extraIncrease[b] = 0;
                        if (!dense)
                            for (int j = 0; j < presentCount[p]; j++) increase[b * symbols + present[p][j]] = 0;
                    }

                    byte[] tok = tokens[p];
                    byte[] ext = extra[p];
                    for (int i = begin; i < end; i++)
                    {
                        increase[bin[i] * symbols + tok[i]]++;
                        extraIncrease[bin[i]] += ext[i];
                    }

                    int[] symbolsOf = present[p];
                    int n = presentCount[p];
                    int[] pc = counts[p];
                    for (int j = 0; j < n; j++)
                    {
                        above[symbolsOf[j]] = pc[symbolsOf[j]];
                        below[symbolsOf[j]] = 0;
                    }

                    long extraBelow = 0;
                    int countBelow = 0;
                    // Discourage changing the predictor, but never moving away from the first (Weighted) one.
                    float penalty = p != current && current != 0 ? changePenalty : 0;
                    for (int b = first; b < last; b++)
                    {
                        if (used[b] == 0) continue;
                        extraBelow += extraIncrease[b];
                        countBelow += used[b];
                        int row = b * symbols;
                        for (int j = 0; j < n; j++)
                        {
                            int t = symbolsOf[j];
                            int v = increase[row + t];
                            above[t] -= v;
                            below[t] += v;
                        }

                        // Samples at or below the split go right; the decoder takes the left child for property > split.
                        float right = EstimateBits(below, symbolsOf, n, countBelow) + extraBelow;
                        float left = EstimateBits(above, symbolsOf, n, total - countBelow) + totalExtra[p] - extraBelow;
                        int k = b - first;
                        if (right + penalty < scoreRight[k])
                        {
                            scoreRight[k] = right + penalty;
                            costRight[k] = right;
                            predictorRight[k] = p;
                        }

                        if (left + penalty < scoreLeft[k])
                        {
                            scoreLeft[k] = left + penalty;
                            costLeft[k] = left;
                            predictorLeft[k] = p;
                        }
                    }
                }

                int position = begin;
                for (int b = first; b < last; b++)
                {
                    if (used[b] == 0) continue;
                    position += used[b];
                    int k = b - first;
                    float cost = costLeft[k] + costRight[k];
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestProperty = property;
                        bestBin = b;
                        bestPosition = position;
                        bestLeft = predictorLeft[k];
                        bestRight = predictorRight[k];
                    }
                }
            }

            return bestCost + threshold < baseBits;
        }

        // A cleared buffer of at least length elements.
        private static void Reuse<T>(ref T[] buffer, int length)
        {
            if (buffer.Length < length) buffer = new T[Math.Max(length, buffer.Length * 2)];
            else buffer.AsSpan(0, length).Clear();
        }

        /// <summary>Moves the samples with bins at most <paramref name="bin"/> to [begin, position).</summary>
        public void Partition(int begin, int position, int end, int property, int bin)
        {
            byte[] key = bins[property];
            int low = begin;
            int high = position;
            while (true)
            {
                while (low < position && key[low] <= bin) low++;
                while (high < end && key[high] > bin) high++;
                if (low >= position || high >= end) break;
                Swap(low++, high++);
            }
        }

        private void Swap(int a, int b)
        {
            foreach (byte[] array in bins) (array[a], array[b]) = (array[b], array[a]);
            foreach (byte[] array in tokens) (array[a], array[b]) = (array[b], array[a]);
            foreach (byte[] array in extra) (array[a], array[b]) = (array[b], array[a]);
        }

        // Entropy of the symbols with probabilities floored at 1/4096, as ANS cannot code less likely symbols; only the
        // listed symbols, in order, may have counts.
        private static float EstimateBits(int[] counts, int[] symbols, int symbolCount, int total)
        {
            if (total == 0) return 0;
            float inverse = 1.0f / total;
            float bits = 0;
            for (int j = 0; j < symbolCount; j++)
            {
                int count = counts[symbols[j]];
                if (count == 0 || count == total) continue;
                bits -= count * MathF.Log2(Math.Max(count * inverse, MinProbability));
            }

            return bits;
        }
    }
}
