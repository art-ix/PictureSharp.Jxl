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

        public ref readonly JxlMaLeaf Lookup(ReadOnlySpan<int> properties)
        {
            int node = 0;
            while (Property[node] >= 0)
            {
                node = properties[Property[node]] > Split[node] ? Left[node] : Right[node];
            }

            return ref Leaves[Left[node]];
        }
    }

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
        // At most 256 properties: the coded property index is below 256.
        Span<int> properties = stackalloc int[tree.PropertyCount];
        int referenceCount = tree.ReferenceProperties;
        int[] references = new int[referenceCount * width];
        JxlWeightedPredictor? weighted = tree.UsesWeighted ? new JxlWeightedPredictor(weightedHeader, width) : null;
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
                JxlMaProperty.SetLocal(properties, x, neighbours);
                long weightedPrediction = 0;
                if (weighted is not null)
                {
                    weightedPrediction = weighted.Predict(x, y, neighbours, out properties[JxlMaProperty.WeightedMaxError]);
                }

                references.AsSpan(x * referenceCount, referenceCount).CopyTo(properties[JxlMaProperty.Local..]);
                ref readonly JxlMaLeaf leaf = ref tree.Lookup(properties);
                long guess = leaf.Offset + Predict(leaf.Predictor, neighbours, weightedPrediction);
                uint value = symbols.ReadClustered(leaf.Cluster, ref reader);
                pixels[row + x] = unchecked((int)(UnpackSigned(value) * (long)leaf.Multiplier + guess));
                weighted?.Update(pixels[row + x], x, y);
            }
        }
    }

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

    private static long Predict(JxlPredictor predictor, in JxlNeighbours n, long weighted) => predictor switch
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
