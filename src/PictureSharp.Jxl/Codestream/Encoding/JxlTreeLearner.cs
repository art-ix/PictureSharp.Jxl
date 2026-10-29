using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
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
    /// <remarks>
    /// With parallelism the tree is the same: properties are quantized in parallel, the largest nodes evaluate their
    /// properties in parallel, and once no node holds more than a share of the samples the remaining subtrees grow on
    /// their own workers. Every node's split depends only on its own samples and predictor, so the decisions are
    /// assembled in the sequential (depth-first, left child first) order, which numbers the nodes as before.
    /// </remarks>
    public List<JxlTreeNode> Learn(float threshold, CancellationToken cancellationToken, int parallelism = JxlParallel.Sequential)
    {
        var tree = new List<JxlTreeNode> { JxlTreeNode.Leaf(predictors[0]) };
        int count = Count;
        if (count == 0) return tree;

        // Quantize every property to at most maxPropertyValues bins at quantiles of its samples.
        int[][] thresholds = new int[properties.Length][];
        byte[][] bins = new byte[properties.Length][];
        JxlParallel.For(properties.Length, parallelism, cancellationToken, p => (thresholds[p], bins[p]) = Quantize(values[p].Span));

        byte[][] tok = [.. tokens.Select(list => list.Span.ToArray())];
        byte[][] extra = [.. extraBits.Select(list => list.Span.ToArray())];
        if (JxlParallel.IsParallel(parallelism, count)) return LearnParallel(tree, bins, tok, extra, thresholds, threshold, parallelism, cancellationToken);

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

    // A split decision: the property and bin, and the predictor indices of the children; Property -1 keeps a leaf.
    private readonly record struct Decision(int Property, int Bin, int LeftPredictor, int RightPredictor, int LeftKey = 0, int RightKey = 0)
    {
        public static readonly Decision Keep = new(-1, 0, 0, 0);
    }

    // A node not decided yet: its key, samples [Begin, End) and the index of its predictor.
    private readonly record struct Pending(int Key, int Begin, int End, int Current);

    // A node's samples are a share of the total small enough for the subtrees to balance across the workers.
    private const int SubtreesPerWorker = 1;

    private List<JxlTreeNode> LearnParallel(List<JxlTreeNode> tree, byte[][] bins, byte[][] tok, byte[][] extra, int[][] thresholds,
        float threshold, int parallelism, CancellationToken cancellationToken)
    {
        int count = tok[0].Length;
        long limit = Math.Max(1, (long)count / (SubtreesPerWorker * JxlParallel.Workers(parallelism, int.MaxValue)));
        // Split the largest node until none is too large; children are keyed in the order they are created.
        var decided = new Dictionary<int, Decision>();
        var pending = new List<Pending> { new(0, 0, count, Array.IndexOf(predictors, tree[0].Predictor)) };
        var top = new Search(bins, tok, extra, thresholds, threshold);
        int keys = 1;
        while (true)
        {
            int largest = -1;
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].End - pending[i].Begin > limit && (largest < 0 || pending[i].End - pending[i].Begin > pending[largest].End - pending[largest].Begin)) largest = i;
            }

            if (largest < 0) break;
            Pending node = pending[largest];
            pending.RemoveAt(largest);
            cancellationToken.ThrowIfCancellationRequested();
            if (!top.FindSplit(node.Begin, node.End, node.Current, out int property, out int bin, out int position, out int leftPredictor,
                    out int rightPredictor, parallelism, cancellationToken))
            {
                decided[node.Key] = Decision.Keep;
                continue;
            }

            top.Partition(node.Begin, position, node.End, property, bin);
            int leftKey = keys++, rightKey = keys++;
            decided[node.Key] = new Decision(property, bin, leftPredictor, rightPredictor, leftKey, rightKey);
            pending.Add(new Pending(leftKey, position, node.End, Array.IndexOf(predictors, predictors[leftPredictor])));
            pending.Add(new Pending(rightKey, node.Begin, position, Array.IndexOf(predictors, predictors[rightPredictor])));
        }

        // The remaining subtrees, largest first, each recorded in its own depth-first order.
        Pending[] order = [.. pending.OrderByDescending(node => node.End - node.Begin).ThenBy(node => node.Key)];
        var subtrees = new List<Decision>[order.Length];
        var subtreeOf = new Dictionary<int, int>();
        for (int i = 0; i < order.Length; i++) subtreeOf[order[i].Key] = i;
        JxlParallel.ForWorkers(order.Length, parallelism, cancellationToken, () => new Search(bins, tok, extra, thresholds, threshold),
            (search, i) => subtrees[i] = Grow(search, order[i], cancellationToken));

        // Replay the sequential loop over the decisions.
        int[] next = new int[order.Length];
        var nodes = new Stack<(int Node, int Key, int Subtree)>();
        nodes.Push((0, 0, subtreeOf.GetValueOrDefault(0, -1)));
        while (nodes.Count > 0)
        {
            (int node, int key, int subtree) = nodes.Pop();
            Decision decision = subtree < 0 ? decided[key] : subtrees[subtree][next[subtree]++];
            if (decision.Property < 0) continue;
            int left = tree.Count;
            tree[node] = new JxlTreeNode(properties[decision.Property], thresholds[decision.Property][decision.Bin], left, left + 1, JxlPredictor.Zero);
            tree.Add(new JxlTreeNode(-1, 0, 0, 0, predictors[decision.LeftPredictor]));
            tree.Add(new JxlTreeNode(-1, 0, 0, 0, predictors[decision.RightPredictor]));
            if (subtree < 0)
            {
                nodes.Push((left + 1, decision.RightKey, subtreeOf.GetValueOrDefault(decision.RightKey, -1)));
                nodes.Push((left, decision.LeftKey, subtreeOf.GetValueOrDefault(decision.LeftKey, -1)));
            }
            else
            {
                nodes.Push((left + 1, 0, subtree));
                nodes.Push((left, 0, subtree));
            }
        }

        return tree;
    }

    // The decisions of the subtree at root, in the order the sequential loop visits its nodes.
    private List<Decision> Grow(Search search, Pending root, CancellationToken cancellationToken)
    {
        var decisions = new List<Decision>();
        var nodes = new Stack<Pending>();
        nodes.Push(root);
        while (nodes.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Pending node = nodes.Pop();
            if (!search.FindSplit(node.Begin, node.End, node.Current, out int property, out int bin, out int position, out int leftPredictor,
                    out int rightPredictor))
            {
                decisions.Add(Decision.Keep);
                continue;
            }

            decisions.Add(new Decision(property, bin, leftPredictor, rightPredictor));
            search.Partition(node.Begin, position, node.End, property, bin);
            nodes.Push(new Pending(0, node.Begin, position, Array.IndexOf(predictors, predictors[rightPredictor])));
            nodes.Push(new Pending(0, position, node.End, Array.IndexOf(predictors, predictors[leftPredictor])));
        }

        return decisions;
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

        // The node's symbol counts per predictor, reused between nodes and grown as needed.
        private int[][] counts = [];
        private int[][] present = [];
        private int[] presentCount = [];
        private long[] totalExtra = [];
        private readonly Scratch scratch = new();

        // Per-property buffers; one per worker when properties are evaluated in parallel.
        private sealed class Scratch
        {
            public int[] Below = [];
            public int[] Above = [];
            public int[] Used = [];
            public float[] CostLeft = [];
            public float[] CostRight = [];
            public float[] ScoreLeft = [];
            public float[] ScoreRight = [];
            public int[] PredictorLeft = [];
            public int[] PredictorRight = [];
            public int[] Increase = [];
            public long[] ExtraIncrease = [];
        }

        // The best split found so far: the first of the lowest cost in property and bin order.
        private struct Candidate
        {
            public float Cost;
            public int Property, Bin, Position, Left, Right;
        }

        public bool FindSplit(int begin, int end, int current, out int bestProperty, out int bestBin, out int bestPosition,
            out int bestLeft, out int bestRight, int parallelism = JxlParallel.Sequential, CancellationToken cancellationToken = default)
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

            var best = new Candidate { Cost = float.MaxValue };
            if (!JxlParallel.IsParallel(parallelism, bins.Length))
            {
                Reuse(ref scratch.Below, symbols);
                Reuse(ref scratch.Above, symbols);
                for (int property = 0; property < bins.Length; property++) Evaluate(property, begin, end, current, symbols, scratch, ref best);
            }
            else
            {
                // Each property's own best, then the first lowest in property order: the sequential choice.
                var results = new Candidate[bins.Length];
                JxlParallel.ForWorkers(bins.Length, parallelism, cancellationToken, () =>
                {
                    var own = new Scratch();
                    Reuse(ref own.Below, symbols);
                    Reuse(ref own.Above, symbols);
                    return own;
                }, (own, property) =>
                {
                    var candidate = new Candidate { Cost = float.MaxValue };
                    Evaluate(property, begin, end, current, symbols, own, ref candidate);
                    results[property] = candidate;
                });
                foreach (Candidate candidate in results)
                {
                    if (candidate.Cost < best.Cost) best = candidate;
                }
            }

            (bestProperty, bestBin, bestPosition, bestLeft, bestRight) = (best.Property, best.Bin, best.Position, best.Left, best.Right);
            return best.Cost + threshold < baseBits;
        }

        private void Evaluate(int property, int begin, int end, int current, int symbols, Scratch s, ref Candidate best)
        {
            int predictorCount = tokens.Length;
            int total = end - begin;
            int[] below = s.Below;
            int[] above = s.Above;
            byte[] bin = bins[property];
            int binCount = thresholds[property].Length + 1;
            if (binCount < 2) return;
            Reuse(ref s.Used, binCount);
            int[] used = s.Used;
            int first = binCount;
            int last = 0;
            for (int i = begin; i < end; i++)
            {
                used[bin[i]]++;
                first = Math.Min(first, bin[i]);
                last = Math.Max(last, bin[i]);
            }

            if (first == last) return;
            int splits = last - first;
            Reuse(ref s.CostLeft, splits);
            Reuse(ref s.CostRight, splits);
            Reuse(ref s.ScoreLeft, splits);
            Reuse(ref s.ScoreRight, splits);
            Reuse(ref s.PredictorLeft, splits);
            Reuse(ref s.PredictorRight, splits);
            float[] costLeft = s.CostLeft, costRight = s.CostRight, scoreLeft = s.ScoreLeft, scoreRight = s.ScoreRight;
            int[] predictorLeft = s.PredictorLeft, predictorRight = s.PredictorRight;
            scoreLeft.AsSpan(0, splits).Fill(float.MaxValue);
            scoreRight.AsSpan(0, splits).Fill(float.MaxValue);
            if (s.Increase.Length < binCount * symbols) s.Increase = new int[binCount * symbols];
            Reuse(ref s.ExtraIncrease, binCount);
            int[] increase = s.Increase;
            long[] extraIncrease = s.ExtraIncrease;
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
                if (cost < best.Cost)
                {
                    best.Cost = cost;
                    best.Property = property;
                    best.Bin = b;
                    best.Position = position;
                    best.Left = predictorLeft[k];
                    best.Right = predictorRight[k];
                }
            }
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
