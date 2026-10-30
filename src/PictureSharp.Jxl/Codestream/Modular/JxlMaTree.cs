using System.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Modular;

/// <summary>
/// A meta-adaptive (MA) tree: decision nodes on pixel properties and leaves with a context, a predictor, an offset
/// and a multiplier. Ported from libjxl v0.12.0 lib/jxl/modular/encoding/dec_ma.cc, encoding.cc and
/// context_predict.h (BSD-3-Clause); only the generic (slowest) decoding path, which the fast paths equal.
/// </summary>
internal sealed class JxlMaTree
{
    public const int PredictorCount = 14;
    // Channel and stream (group) id, which filtering resolves per channel.
    private const int StaticProperties = JxlMaProperty.Stream + 1;
    public const long MaxSize = 1 << 22;
    private const int ExtraPropertiesPerChannel = 4;
    private const int HeightLimit = 2048;
    private const int MaxProperty = 255;
    public const int TreeContexts = (int)JxlMaTreeContext.MultiplierBits + 1;

    // Node arrays: Property is -1 for leaves, whose Left holds the leaf (context) index.
    private readonly int[] property;
    private readonly int[] splitValue;
    private readonly int[] left;
    private readonly int[] right;
    private readonly JxlPredictor[] predictor;
    private readonly long[] offset;
    private readonly int[] multiplier;

    private JxlMaTree(List<JxlMaNode> nodes)
    {
        property = [.. nodes.Select(n => n.Property)];
        splitValue = [.. nodes.Select(n => n.Split)];
        left = [.. nodes.Select(n => n.Left)];
        right = [.. nodes.Select(n => n.Right)];
        predictor = [.. nodes.Select(n => n.Predictor)];
        offset = [.. nodes.Select(n => n.Offset)];
        multiplier = [.. nodes.Select(n => n.Multiplier)];
    }

    public int Count => property.Length;

    /// <summary>The number of leaves, which is the number of entropy contexts of the channel data.</summary>
    public int Leaves => (Count + 1) / 2;

    /// <summary>Reads the tree's own entropy code and the tree (DecodeTree).</summary>
    public static JxlMaTree Read(ref JxlBitReader reader, long sizeLimit)
    {
        JxlEntropyCode code = JxlEntropyCode.Read(ref reader, TreeContexts);
        if (!code.UsePrefixCode && code.DegenerateSymbols[code.ContextMap[(int)JxlMaTreeContext.Property]] > 0)
        {
            throw JxlDescriptor.Corrupt("Infinite MA tree.");
        }

        sizeLimit = Math.Min(sizeLimit, MaxSize);
        var nodes = new List<JxlMaNode>();
        using (var symbols = new JxlSymbolReader(code, ref reader))
        {
            int leaves = 0;
            long pending = 1;
            while (pending > 0)
            {
                if (nodes.Count > sizeLimit)
                {
                    throw JxlDescriptor.Corrupt("The MA tree is too large.");
                }

                pending--;
                uint encodedProperty = symbols.Read((int)JxlMaTreeContext.Property, ref reader);
                if (encodedProperty > MaxProperty + 1)
                {
                    throw JxlDescriptor.Corrupt("Invalid MA tree property.");
                }

                if (encodedProperty == 0)
                {
                    nodes.Add(ReadLeaf(ref reader, symbols, leaves++));
                    continue;
                }

                int split = UnpackSigned(symbols.Read((int)JxlMaTreeContext.SplitValue, ref reader));
                int child = (int)(nodes.Count + pending + 1);
                nodes.Add(JxlMaNode.Decision((int)encodedProperty - 1, split, child));
                pending += 2;
            }

            if (!symbols.IsFinalStateValid)
            {
                throw JxlDescriptor.Corrupt("Invalid ANS stream of the MA tree.");
            }
        }

        var tree = new JxlMaTree(nodes);
        tree.Validate();
        return tree;
    }

    // A leaf: its predictor, offset and multiplier (log and mantissa); the context is the leaf's index.
    private static JxlMaNode ReadLeaf(ref JxlBitReader reader, JxlSymbolReader symbols, int leaf)
    {
        uint leafPredictor = symbols.Read((int)JxlMaTreeContext.Predictor, ref reader);
        if (leafPredictor >= PredictorCount)
        {
            throw JxlDescriptor.Corrupt("Invalid MA tree predictor.");
        }

        long leafOffset = UnpackSigned(symbols.Read((int)JxlMaTreeContext.Offset, ref reader));
        uint log = symbols.Read((int)JxlMaTreeContext.MultiplierLog, ref reader);
        if (log >= 31)
        {
            throw JxlDescriptor.Corrupt("Invalid MA tree multiplier.");
        }

        uint bits = symbols.Read((int)JxlMaTreeContext.MultiplierBits, ref reader);
        if (bits >= (1u << (int)(31 - log)) - 1)
        {
            throw JxlDescriptor.Corrupt("Invalid MA tree multiplier.");
        }

        return JxlMaNode.Leaf(leaf, (JxlPredictor)leafPredictor, leafOffset, (int)((bits + 1) << (int)log));
    }

    /// <summary>Every split must narrow the range its property can still have on that path (ValidateTree).</summary>
    private void Validate()
    {
        int properties = property.Max() + 1;
        long[] low = new long[Math.Max(properties, 0)];
        long[] high = new long[low.Length];
        Array.Fill(low, int.MinValue);
        Array.Fill(high, int.MaxValue);
        // (node, saved low, saved high, step): 0 = check and go left, 1 = go right, 2 = restore.
        var stack = new Stack<(int Node, long Low, long High, int Step)>();
        stack.Push((0, 0, 0, 0));
        while (stack.Count > 0)
        {
            if (stack.Count >= HeightLimit)
            {
                throw JxlDescriptor.Corrupt("The MA tree is too tall.");
            }

            (int node, long savedLow, long savedHigh, int step) = stack.Pop();
            int p = property[node];
            if (p < 0)
            {
                continue;
            }

            long v = splitValue[node];
            switch (step)
            {
                case 0:
                    if (low[p] > v || high[p] <= v)
                    {
                        throw JxlDescriptor.Corrupt("Invalid MA tree.");
                    }

                    stack.Push((node, low[p], high[p], 1));
                    low[p] = v + 1;
                    stack.Push((left[node], 0, 0, 0));
                    break;
                case 1:
                    stack.Push((node, savedLow, savedHigh, 2));
                    low[p] = savedLow;
                    high[p] = v;
                    stack.Push((right[node], 0, 0, 0));
                    break;
                default:
                    high[p] = savedHigh;
                    break;
            }
        }
    }

    /// <summary>
    /// The tree with the decisions on the static properties of one channel resolved and every leaf's context mapped
    /// to its cluster (FilterTree, without the two-level flattening).
    /// </summary>
    public FilteredTree Filter(int channel, int stream, byte[] contextMap)
    {
        var properties = new List<int>();
        var splits = new List<int>();
        var lefts = new List<int>();
        var rights = new List<int>();
        var leaves = new List<JxlMaLeaf>();
        bool weighted = false;
        int maxProperty = -1;
        // (source node, index of the parent's child slot to patch, which side).
        var queue = new Queue<(int Node, int Parent, bool IsLeft)>();
        queue.Enqueue((0, -1, false));
        while (queue.Count > 0)
        {
            (int node, int parent, bool isLeft) = queue.Dequeue();
            while (property[node] is >= 0 and < StaticProperties)
            {
                int value = property[node] == JxlMaProperty.Channel ? channel : stream;
                node = value > splitValue[node] ? left[node] : right[node];
            }

            int index = properties.Count;
            if (parent >= 0)
            {
                (isLeft ? lefts : rights)[parent] = index;
            }

            if (property[node] < 0)
            {
                // Leaves keep their index into Leaves in Left.
                properties.Add(-1);
                splits.Add(0);
                lefts.Add(leaves.Count);
                rights.Add(0);
                leaves.Add(new JxlMaLeaf(contextMap[left[node]], predictor[node], offset[node], multiplier[node]));
                weighted |= predictor[node] == JxlPredictor.Weighted;
                continue;
            }

            properties.Add(property[node]);
            splits.Add(splitValue[node]);
            lefts.Add(0);
            rights.Add(0);
            maxProperty = Math.Max(maxProperty, property[node]);
            weighted |= property[node] == JxlMaProperty.WeightedMaxError;
            queue.Enqueue((left[node], index, true));
            queue.Enqueue((right[node], index, false));
        }

        int count = maxProperty + 1 > JxlMaProperty.Local
            ? DivCeil(maxProperty + 1 - JxlMaProperty.Local, ExtraPropertiesPerChannel) * ExtraPropertiesPerChannel + JxlMaProperty.Local
            : JxlMaProperty.Local;
        return new FilteredTree([.. properties], [.. splits], [.. lefts], [.. rights], [.. leaves], count, weighted);
    }

    /// <summary>A tree for one channel; leaves hold clusters.</summary>
    internal sealed record FilteredTree(int[] Property, int[] Split, int[] Left, int[] Right, JxlMaLeaf[] Leaves, int PropertyCount,
        bool UsesWeighted)
    {
        public int ReferenceProperties => PropertyCount - JxlMaProperty.Local;

        /// <summary>
        /// Whether the decisions use none of properties 2–8 (position, neighbours and the running gradient), as
        /// libjxl's default trees: only the neighbour differences and the weighted error are then computed per pixel.
        /// </summary>
        public bool UsesOnlyDifferences { get; } = Array.TrueForAll(Property, p => p < 0 || p >= JxlMaProperty.Gradient);

        // The decision nodes packed together, so each step of a lookup reads one 16-byte entry. (libjxl's two-level
        // FlatDecisionNode was measured too: no faster with branches, about 10 % slower without them.)
        private readonly Node[] nodes = [.. Property.Select((p, i) => new Node(p, Split[i], Left[i], Right[i]))];

        public ref readonly JxlMaLeaf Lookup(ReadOnlySpan<int> properties)
        {
            Node[] packed = nodes;
            Node node = packed[0];
            while (node.Property >= 0)
            {
                node = packed[properties[node.Property] > node.Split ? node.Left : node.Right];
            }

            return ref Leaves[node.Left];
        }

        private readonly record struct Node(int Property, int Split, int Left, int Right);

        /// <summary>
        /// For a tree whose decisions all test one local property (other than the channel-wide ones), a table from the
        /// property value to the leaf index. Values below the smallest split take every right branch and values above
        /// the largest every left one, so the property is clamped to <c>[Min, Min + Table.Length - 1]</c>.
        /// </summary>
        public (int Property, int Min, int[] Table)? SinglePropertyLookup()
        {
            int single = -1;
            int min = int.MaxValue;
            int max = int.MinValue;
            for (int node = 0; node < Property.Length; node++)
            {
                if (Property[node] < 0)
                {
                    continue;
                }

                if (single >= 0 && Property[node] != single)
                {
                    return null;
                }

                single = Property[node];
                min = Math.Min(min, Split[node]);
                max = Math.Max(max, Split[node]);
            }

            if (single is < JxlMaProperty.Y or >= JxlMaProperty.Local || (long)max - min > MaxLookupRange)
            {
                return null;
            }

            int[] table = new int[max - min + 2];
            for (int i = 0; i < table.Length; i++)
            {
                int value = min + i;
                int node = 0;
                while (Property[node] >= 0)
                {
                    node = value > Split[node] ? Left[node] : Right[node];
                }

                table[i] = Left[node];
            }

            return (single, min, table);
        }
    }

    // Larger split ranges fall back to walking the tree.
    private const int MaxLookupRange = 1 << 16;

    /// <summary>Decodes one channel of <paramref name="image"/> (DecodeModularChannelMAANS).</summary>
    public void DecodeChannel(ref JxlBitReader reader, JxlSymbolReader symbols, byte[] contextMap, JxlWeightedHeader weightedHeader,
        JxlModularImage image, int channelIndex, int stream, CancellationToken cancellationToken = default)
    {
        JxlChannel channel = image.Channels[channelIndex];
        if (channel.IsEmpty)
        {
            return;
        }

        FilteredTree tree = Filter(channelIndex, stream, contextMap);
        int width = channel.Width;
        int[] pixels = channel.Pixels;
        if (tree.ReferenceProperties == 0)
        {
            if (tree.Leaves.Length == 1 && !tree.UsesWeighted)
            {
                DecodeSingleLeaf(ref reader, symbols, tree.Leaves[0], channel, cancellationToken);
                return;
            }

            if (tree.SinglePropertyLookup() is { } lookup)
            {
                DecodeWithLookup(ref reader, symbols, tree, lookup, weightedHeader, channel, channelIndex, stream, cancellationToken);
                return;
            }
        }

        // At most 256 properties: the coded property index is below 256.
        Span<int> properties = stackalloc int[tree.PropertyCount];
        int referenceCount = tree.ReferenceProperties;
        using var buffers = new JxlBufferScope();
        int[] references = buffers.Rent<int>(referenceCount * width);
        using JxlWeightedPredictor? weighted = tree.UsesWeighted ? new JxlWeightedPredictor(weightedHeader, width) : null;
        bool differencesOnly = tree.UsesOnlyDifferences;
        for (int y = 0; y < channel.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (referenceCount > 0)
            {
                PrecomputeReferences(image, channelIndex, y, references, referenceCount);
            }

            JxlMaProperty.StartRow(properties, channelIndex, stream, y);
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                var neighbours = JxlNeighbours.At(pixels, width, x, y);
                if (differencesOnly)
                {
                    JxlMaProperty.SetDifferences(properties, neighbours);
                }
                else
                {
                    JxlMaProperty.SetLocal(properties, x, neighbours);
                }

                long weightedPrediction = 0;
                if (weighted is not null)
                {
                    weightedPrediction = weighted.Predict(x, y, neighbours, out properties[JxlMaProperty.WeightedMaxError]);
                }

                if (referenceCount > 0)
                {
                    references.AsSpan(x * referenceCount, referenceCount).CopyTo(properties[JxlMaProperty.Local..]);
                }

                ref readonly JxlMaLeaf leaf = ref tree.Lookup(properties);
                long guess = leaf.Offset + PredictMixed(leaf.Predictor, neighbours, weightedPrediction);
                uint value = symbols.ReadClustered(leaf.Cluster, ref reader);
                pixels[row + x] = unchecked((int)(UnpackSigned(value) * (long)leaf.Multiplier + guess));
                weighted?.Update(pixels[row + x], x, y);
            }
        }
    }

    /// <summary>
    /// A tree without decisions: every pixel uses the same leaf, so no properties are computed and the residuals of a
    /// row, which do not depend on the pixels, are read in one batch before the prediction pass.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static void DecodeSingleLeaf(ref JxlBitReader reader, JxlSymbolReader symbols, JxlMaLeaf leaf, JxlChannel channel,
        CancellationToken cancellationToken)
    {
        int width = channel.Width;
        int[] pixels = channel.Pixels;
        long multiplier = leaf.Multiplier;
        long offset = leaf.Offset;
        uint[] residuals = ArrayPool<uint>.Shared.Rent(width);
        try
        {
            for (int y = 0; y < channel.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                symbols.ReadClusteredRow(leaf.Cluster, ref reader, residuals.AsSpan(0, width));
                int row = y * width;
                if (leaf.Predictor == JxlPredictor.Gradient && y > 0)
                {
                    // Interior rows: W, N and NW exist from the second pixel on, so the edge substitutes are not needed.
                    pixels[row] = Value(residuals[0], multiplier, offset + pixels[row - width]);
                    for (int x = 1; x < width; x++)
                    {
                        int i = row + x;
                        long guess = offset + JxlMaProperty.ClampedGradient(pixels[i - 1], pixels[i - width], pixels[i - width - 1]);
                        pixels[i] = Value(residuals[x], multiplier, guess);
                    }

                    continue;
                }

                for (int x = 0; x < width; x++)
                {
                    long guess = offset + Predict(leaf.Predictor, JxlNeighbours.At(pixels, width, x, y), 0);
                    pixels[row + x] = Value(residuals[x], multiplier, guess);
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(residuals);
        }
    }

    private static int Value(uint residual, long multiplier, long guess) => unchecked((int)(UnpackSigned(residual) * multiplier + guess));

    /// <summary>
    /// A tree deciding on a single local property: the leaf comes from a table indexed by that property, and only that
    /// property (and the weighted prediction when a leaf or the property needs it) is computed per pixel.
    /// </summary>
    private static void DecodeWithLookup(ref JxlBitReader reader, JxlSymbolReader symbols, FilteredTree tree,
        (int Property, int Min, int[] Table) lookup, JxlWeightedHeader weightedHeader, JxlChannel channel, int channelIndex,
        int stream, CancellationToken cancellationToken)
    {
        int width = channel.Width;
        int[] pixels = channel.Pixels;
        JxlMaLeaf[] leaves = tree.Leaves;
        (int property, int min, int[] table) = lookup;
        int last = table.Length - 1;
        Span<int> properties = stackalloc int[JxlMaProperty.Local];
        using JxlWeightedPredictor? weighted = tree.UsesWeighted ? new JxlWeightedPredictor(weightedHeader, width) : null;
        // libjxl's gradient tree: every leaf predicts the clamped gradient and the tree splits on the unclamped one.
        bool gradientTree = property == JxlMaProperty.Gradient && weighted is null &&
            Array.TrueForAll(leaves, leaf => leaf.Predictor == JxlPredictor.Gradient);
        for (int y = 0; y < channel.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JxlMaProperty.StartRow(properties, channelIndex, stream, y);
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (gradientTree && y > 0 && x > 0)
                {
                    GradientTreeRow(ref reader, symbols, leaves, table, min, pixels, row + x, row + width, width);
                    break;
                }

                var neighbours = JxlNeighbours.At(pixels, width, x, y);
                long weightedPrediction = 0;
                if (weighted is not null)
                {
                    weightedPrediction = weighted.Predict(x, y, neighbours, out properties[JxlMaProperty.WeightedMaxError]);
                }

                if (property != JxlMaProperty.WeightedMaxError)
                {
                    JxlMaProperty.SetLocal(properties, x, neighbours);
                }

                long index = (long)properties[property] - min;
                ref readonly JxlMaLeaf leaf = ref leaves[table[index < 0 ? 0 : index > last ? last : (int)index]];
                long guess = leaf.Offset + Predict(leaf.Predictor, neighbours, weightedPrediction);
                pixels[row + x] = Next(ref reader, symbols, leaf.Cluster, leaf.Multiplier, guess);
                weighted?.Update(pixels[row + x], x, y);
            }
        }
    }

    /// <summary>Pixels <paramref name="start"/> to <paramref name="end"/> of an interior row of a gradient tree.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static void GradientTreeRow(ref JxlBitReader reader, JxlSymbolReader symbols, JxlMaLeaf[] leaves, int[] table,
        int min, int[] pixels, int start, int end, int width)
    {
        int last = table.Length - 1;
        for (int i = start; i < end; i++)
        {
            int w = pixels[i - 1];
            int n = pixels[i - width];
            int nw = pixels[i - width - 1];
            long index = (long)unchecked(w + n - nw) - min;
            ref readonly JxlMaLeaf leaf = ref leaves[table[index < 0 ? 0 : index > last ? last : (int)index]];
            pixels[i] = Next(ref reader, symbols, leaf.Cluster, leaf.Multiplier, leaf.Offset + JxlMaProperty.ClampedGradient(w, n, nw));
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int Next(ref JxlBitReader reader, JxlSymbolReader symbols, int cluster, long multiplier, long guess) =>
        unchecked((int)(UnpackSigned(symbols.ReadClustered(cluster, ref reader)) * multiplier + guess));

    /// <summary>Properties from earlier channels of the same shape: |v|, v, |v - gradient|, v - gradient.</summary>
    private static void PrecomputeReferences(JxlModularImage image, int channelIndex, int y, int[] references, int count)
    {
        Array.Clear(references);
        JxlChannel channel = image.Channels[channelIndex];
        int width = channel.Width;
        int offset = 0;
        for (int j = channelIndex - 1; j >= 0 && offset < count; j--)
        {
            JxlChannel other = image.Channels[j];
            if (!other.SameShape(channel))
            {
                continue;
            }

            int[] pixels = other.Pixels;
            int row = y * width;
            int previous = (y > 0 ? y - 1 : 0) * width;
            for (int x = 0; x < width; x++)
            {
                long v = pixels[row + x];
                long w = x > 0 ? pixels[row + x - 1] : 0;
                long n = y > 0 ? pixels[previous + x] : w;
                long nw = x > 0 && y > 0 ? pixels[previous + x - 1] : w;
                long residual = v - JxlMaProperty.ClampedGradient((int)w, (int)n, (int)nw);
                int at = x * count + offset;
                unchecked
                {
                    references[at] = (int)Math.Abs(v);
                    references[at + 1] = (int)v;
                    references[at + 2] = (int)Math.Abs(residual);
                    references[at + 3] = (int)residual;
                }
            }

            offset += ExtraPropertiesPerChannel;
        }
    }

    /// <summary>The prediction of a predictor without a tree, as the delta palette uses it.</summary>
    public static long PredictNoTree(int[] pixels, int width, int x, int y, JxlPredictor predictor, JxlWeightedPredictor? weighted)
    {
        var neighbours = JxlNeighbours.At(pixels, width, x, y);
        long weightedPrediction = weighted?.Predict(x, y, neighbours, out _) ?? 0;
        return Predict(predictor, neighbours, weightedPrediction);
    }

    /// <summary>
    /// <see cref="Predict"/> for trees whose leaves mix the gradient and weighted predictors from pixel to pixel, as
    /// libjxl's default trees do: those two are selected without a jump (0.8.4, an indirect jump mispredicts there).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static long PredictMixed(JxlPredictor predictor, in JxlNeighbours n, long weighted)
    {
        if ((uint)(predictor - JxlPredictor.Gradient) > JxlPredictor.Weighted - JxlPredictor.Gradient)
        {
            return Predict(predictor, n, weighted);
        }

        long gradient = JxlMaProperty.ClampedGradient((int)n.W, (int)n.N, (int)n.NW);
        long useWeighted = -(long)(predictor - JxlPredictor.Gradient);
        return gradient ^ ((gradient ^ weighted) & useWeighted);
    }

    internal static long Predict(JxlPredictor predictor, in JxlNeighbours n, long weighted) => predictor switch
    {
        JxlPredictor.Zero => 0,
        JxlPredictor.Left => n.W,
        JxlPredictor.Top => n.N,
        JxlPredictor.Select => Select(n.W, n.N, n.NW),
        JxlPredictor.Weighted => weighted,
        JxlPredictor.Gradient => JxlMaProperty.ClampedGradient((int)n.W, (int)n.N, (int)n.NW),
        JxlPredictor.TopLeft => n.NW,
        JxlPredictor.TopRight => n.NE,
        JxlPredictor.LeftLeft => n.WW,
        JxlPredictor.Average0 => (n.W + n.N) / 2,
        JxlPredictor.Average1 => (n.W + n.NW) / 2,
        JxlPredictor.Average2 => (n.NW + n.N) / 2,
        JxlPredictor.Average3 => (n.N + n.NE) / 2,
        JxlPredictor.Average4 => (6 * n.N - 2 * n.NN + 7 * n.W + n.WW + n.NEE + 3 * n.NE + 8) / 16,
        _ => 0,
    };

    private static long Select(long a, long b, long c)
    {
        long p = a + b - c;
        return Math.Abs(p - a) < Math.Abs(p - b) ? a : b;
    }
}

/// <summary>A node of a decoded MA tree; a leaf has no property and keeps its context index in <see cref="Left"/>.</summary>
internal readonly record struct JxlMaNode(int Property, int Split, int Left, int Right, JxlPredictor Predictor, long Offset, int Multiplier)
{
    public static JxlMaNode Decision(int property, int split, int firstChild) => new(property, split, firstChild, firstChild + 1, JxlPredictor.Zero, 0, 1);

    public static JxlMaNode Leaf(int context, JxlPredictor predictor, long offset, int multiplier) => new(-1, 0, context, 0, predictor, offset, multiplier);
}

/// <summary>A leaf of a filtered tree: the entropy cluster and how the pixel is predicted and scaled.</summary>
internal readonly record struct JxlMaLeaf(int Cluster, JxlPredictor Predictor, long Offset, int Multiplier);
