using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>A node of an encoder MA tree: a split on <c>Property &gt; Split</c> (left) or a leaf (Property -1).</summary>
internal record struct JxlTreeNode(int Property, int Split, int Left, int Right, JxlPredictor Predictor);

/// <summary>
/// Learns an MA tree from pixel samples: greedy splits on quantized properties that minimize the estimated coded
/// size of the residuals, choosing each leaf's predictor. Ported in simplified form from libjxl v0.12.0
/// lib/jxl/modular/encoding/enc_ma.cc (FindBestSplit, PreQuantizeProperties; BSD-3-Clause): no static-property
/// preferences, no sample deduplication, and quantiles of the sampled values as split candidates.
/// </summary>
internal sealed class JxlTreeLearner
{
    // The residual tokens the cost estimates use (libjxl's TreeSamples).
    private static readonly JxlHybridUint SampleConfig = new(4, 1, 2);
    private const float MinProbability = 1.0f / JxlEntropyCode.AnsTabSize;

    private readonly int[] properties;
    private readonly JxlPredictor[] predictors;
    private readonly int maxPropertyValues;
    private readonly List<int>[] values;
    private readonly List<byte>[] tokens;
    private readonly List<byte>[] extraBits;

    /// <param name="properties">The property indices the tree may split on.</param>
    /// <param name="predictors">The predictors leaves may use; the first is the root's.</param>
    /// <param name="maxPropertyValues">The most bins a property is quantized to.</param>
    public JxlTreeLearner(int[] properties, JxlPredictor[] predictors, int maxPropertyValues)
    {
        this.properties = properties;
        this.predictors = predictors;
        this.maxPropertyValues = maxPropertyValues;
        values = [.. properties.Select(_ => new List<int>())];
        tokens = [.. predictors.Select(_ => new List<byte>())];
        extraBits = [.. predictors.Select(_ => new List<byte>())];
    }

    public int Count => tokens[0].Count;

    /// <summary>Adds a sample: all properties of the pixel and its residual for each predictor, in predictor order.</summary>
    public void Add(ReadOnlySpan<int> pixelProperties, ReadOnlySpan<long> residuals)
    {
        for (int i = 0; i < properties.Length; i++) values[i].Add(pixelProperties[properties[i]]);
        for (int i = 0; i < predictors.Length; i++)
        {
            uint token = JxlEntropyEncoder.Tokenize(SampleConfig, JxlTokens.PackSigned(residuals[i]), out int bits, out _);
            tokens[i].Add((byte)Math.Min(token, 255));
            extraBits[i].Add((byte)bits);
        }
    }

    /// <summary>The tree; a single leaf with the first predictor when there are no samples or no useful split.</summary>
    public List<JxlTreeNode> Learn(float threshold, CancellationToken cancellationToken)
    {
        var tree = new List<JxlTreeNode> { new(-1, 0, 0, 0, predictors[0]) };
        int count = Count;
        if (count == 0) return tree;

        // Quantize every property to at most maxPropertyValues bins at quantiles of its samples.
        int[][] thresholds = new int[properties.Length][];
        byte[][] bins = new byte[properties.Length][];
        for (int p = 0; p < properties.Length; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int[] sorted = [.. values[p]];
            Array.Sort(sorted);
            var cuts = new List<int>();
            for (int k = 1; k < maxPropertyValues; k++)
            {
                int v = sorted[(int)((long)k * count / maxPropertyValues)];
                if (v < sorted[^1] && (cuts.Count == 0 || v > cuts[^1])) cuts.Add(v);
            }

            thresholds[p] = [.. cuts];
            bins[p] = new byte[count];
            for (int i = 0; i < count; i++)
            {
                int index = Array.BinarySearch(thresholds[p], values[p][i]);
                bins[p][i] = (byte)(index >= 0 ? index : ~index);
            }
        }

        byte[][] tok = [.. tokens.Select(list => list.ToArray())];
        byte[][] extra = [.. extraBits.Select(list => list.ToArray())];
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

    private sealed class Search(byte[][] bins, byte[][] tokens, byte[][] extra, int[][] thresholds, float threshold)
    {
        private readonly float changePenalty = 800.0f / (100.0f + threshold);

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

            int[][] counts = new int[predictorCount][];
            long[] totalExtra = new long[predictorCount];
            for (int p = 0; p < predictorCount; p++)
            {
                counts[p] = new int[symbols];
                for (int i = begin; i < end; i++)
                {
                    counts[p][tokens[p][i]]++;
                    totalExtra[p] += extra[p][i];
                }
            }

            int total = end - begin;
            float baseBits = EstimateBits(counts[current], total) + totalExtra[current];
            if (baseBits <= threshold) return false;

            float bestCost = float.MaxValue;
            int[] below = new int[symbols];
            int[] above = new int[symbols];
            for (int property = 0; property < bins.Length; property++)
            {
                byte[] bin = bins[property];
                int binCount = thresholds[property].Length + 1;
                if (binCount < 2) continue;
                int[] used = new int[binCount];
                int first = binCount;
                int last = 0;
                for (int i = begin; i < end; i++)
                {
                    used[bin[i]]++;
                    first = Math.Min(first, bin[i]);
                    last = Math.Max(last, bin[i]);
                }

                if (first == last) continue;
                float[] costLeft = new float[last - first];
                float[] costRight = new float[last - first];
                float[] scoreLeft = new float[last - first];
                float[] scoreRight = new float[last - first];
                int[] predictorLeft = new int[last - first];
                int[] predictorRight = new int[last - first];
                Array.Fill(scoreLeft, float.MaxValue);
                Array.Fill(scoreRight, float.MaxValue);
                int[] increase = new int[binCount * symbols];
                long[] extraIncrease = new long[binCount];
                for (int p = 0; p < predictorCount; p++)
                {
                    Array.Clear(increase);
                    Array.Clear(extraIncrease);
                    byte[] tok = tokens[p];
                    byte[] ext = extra[p];
                    for (int i = begin; i < end; i++)
                    {
                        increase[bin[i] * symbols + tok[i]]++;
                        extraIncrease[bin[i]] += ext[i];
                    }

                    Array.Copy(counts[p], above, symbols);
                    Array.Clear(below);
                    long extraBelow = 0;
                    int countBelow = 0;
                    // Discourage changing the predictor, but never moving away from the first (Weighted) one.
                    float penalty = p != current && current != 0 ? changePenalty : 0;
                    for (int b = first; b < last; b++)
                    {
                        if (used[b] == 0) continue;
                        extraBelow += extraIncrease[b];
                        countBelow += used[b];
                        for (int s = 0; s < symbols; s++)
                        {
                            int v = increase[b * symbols + s];
                            above[s] -= v;
                            below[s] += v;
                        }

                        // Samples at or below the split go right; the decoder takes the left child for property > split.
                        float right = EstimateBits(below, countBelow) + extraBelow;
                        float left = EstimateBits(above, total - countBelow) + totalExtra[p] - extraBelow;
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

        // Entropy of the symbols with probabilities floored at 1/4096, as ANS cannot code less likely symbols.
        private static float EstimateBits(int[] counts, int total)
        {
            if (total == 0) return 0;
            float inverse = 1.0f / total;
            float bits = 0;
            foreach (int count in counts)
            {
                if (count == 0 || count == total) continue;
                bits -= count * MathF.Log2(Math.Max(count * inverse, MinProbability));
            }

            return bits;
        }
    }
}
