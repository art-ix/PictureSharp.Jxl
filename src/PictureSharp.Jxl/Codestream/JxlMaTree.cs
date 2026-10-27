namespace PictureSharp.Formats.Jxl.Codestream;

internal enum JxlPredictor
{
    Zero = 0,
    Left = 1,
    Top = 2,
    Average0 = 3,
    Select = 4,
    Gradient = 5,
    Weighted = 6,
    TopRight = 7,
    TopLeft = 8,
    LeftLeft = 9,
    Average1 = 10,
    Average2 = 11,
    Average3 = 12,
    Average4 = 13,
}

/// <summary>
/// A meta-adaptive (MA) tree: decision nodes on pixel properties and leaves with a context, a predictor, an offset
/// and a multiplier. Ported from libjxl v0.12.0 lib/jxl/modular/encoding/dec_ma.cc, encoding.cc and
/// context_predict.h (BSD-3-Clause); only the generic (slowest) decoding path, which the fast paths equal.
/// </summary>
internal sealed class JxlMaTree
{
    public const int PredictorCount = 14;
    public const int StaticProperties = 2;   // channel and stream (group) id
    public const int NonReferenceProperties = StaticProperties + 13 + 1;
    public const int WeightedProperty = NonReferenceProperties - 1;
    public const long MaxSize = 1 << 22;
    private const int ExtraPropertiesPerChannel = 4;
    private const int HeightLimit = 2048;

    private enum TreeContext { SplitValue = 0, Property = 1, Predictor = 2, Offset = 3, MultiplierLog = 4, MultiplierBits = 5 }

    // Node arrays: Property is -1 for leaves, whose Left holds the leaf (context) index.
    private readonly int[] property;
    private readonly int[] splitValue;
    private readonly int[] left;
    private readonly int[] right;
    private readonly JxlPredictor[] predictor;
    private readonly long[] offset;
    private readonly int[] multiplier;

    private JxlMaTree(List<(int Property, int Split, int Left, int Right, JxlPredictor Predictor, long Offset, int Multiplier)> nodes)
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
        JxlEntropyCode code = JxlEntropyCode.Read(ref reader, 6);
        if (!code.UsePrefixCode && code.DegenerateSymbols[code.ContextMap[(int)TreeContext.Property]] > 0)
        {
            throw JxlDescriptor.Corrupt("Infinite MA tree.");
        }

        sizeLimit = Math.Min(sizeLimit, MaxSize);
        var nodes = new List<(int, int, int, int, JxlPredictor, long, int)>();
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
                uint encodedProperty = symbols.Read((int)TreeContext.Property, ref reader);
                if (encodedProperty > 256)
                {
                    throw JxlDescriptor.Corrupt("Invalid MA tree property.");
                }

                if (encodedProperty == 0)
                {
                    uint leafPredictor = symbols.Read((int)TreeContext.Predictor, ref reader);
                    if (leafPredictor >= PredictorCount)
                    {
                        throw JxlDescriptor.Corrupt("Invalid MA tree predictor.");
                    }

                    long leafOffset = UnpackSigned(symbols.Read((int)TreeContext.Offset, ref reader));
                    uint log = symbols.Read((int)TreeContext.MultiplierLog, ref reader);
                    if (log >= 31)
                    {
                        throw JxlDescriptor.Corrupt("Invalid MA tree multiplier.");
                    }

                    uint bits = symbols.Read((int)TreeContext.MultiplierBits, ref reader);
                    if (bits >= (1u << (int)(31 - log)) - 1)
                    {
                        throw JxlDescriptor.Corrupt("Invalid MA tree multiplier.");
                    }

                    nodes.Add((-1, 0, leaves++, 0, (JxlPredictor)leafPredictor, leafOffset, (int)((bits + 1) << (int)log)));
                    continue;
                }

                int split = UnpackSigned(symbols.Read((int)TreeContext.SplitValue, ref reader));
                int child = (int)(nodes.Count + pending + 1);
                nodes.Add(((int)encodedProperty - 1, split, child, child + 1, JxlPredictor.Zero, 0, 1));
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
        var leaves = new List<(int Cluster, JxlPredictor Predictor, long Offset, int Multiplier)>();
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
                int value = property[node] == 0 ? channel : stream;
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
                leaves.Add((contextMap[left[node]], predictor[node], offset[node], multiplier[node]));
                weighted |= predictor[node] == JxlPredictor.Weighted;
                continue;
            }

            properties.Add(property[node]);
            splits.Add(splitValue[node]);
            lefts.Add(0);
            rights.Add(0);
            maxProperty = Math.Max(maxProperty, property[node]);
            weighted |= property[node] == WeightedProperty;
            queue.Enqueue((left[node], index, true));
            queue.Enqueue((right[node], index, false));
        }

        int count = maxProperty + 1 > NonReferenceProperties
            ? (maxProperty + 1 - NonReferenceProperties + ExtraPropertiesPerChannel - 1) / ExtraPropertiesPerChannel * ExtraPropertiesPerChannel
                + NonReferenceProperties
            : NonReferenceProperties;
        return new FilteredTree([.. properties], [.. splits], [.. lefts], [.. rights], [.. leaves], count, weighted);
    }

    /// <summary>A tree for one channel; leaves hold clusters.</summary>
    internal sealed record FilteredTree(int[] Property, int[] Split, int[] Left, int[] Right,
        (int Cluster, JxlPredictor Predictor, long Offset, int Multiplier)[] Leaves, int PropertyCount, bool UsesWeighted)
    {
        public int ReferenceProperties => PropertyCount - NonReferenceProperties;

        public ref readonly (int Cluster, JxlPredictor Predictor, long Offset, int Multiplier) Lookup(ReadOnlySpan<int> properties)
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
        JxlModularImage image, int channelIndex, int stream)
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
            if (referenceCount > 0)
            {
                PrecomputeReferences(image, channelIndex, y, references, referenceCount);
            }

            properties[0] = channelIndex;
            properties[1] = stream;
            properties[2] = y;
            properties[9] = 0;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                long l = x > 0 ? pixels[i - 1] : y > 0 ? pixels[i - width] : 0;
                long t = y > 0 ? pixels[i - width] : l;
                long tl = x > 0 && y > 0 ? pixels[i - 1 - width] : l;
                long tr = x + 1 < width && y > 0 ? pixels[i + 1 - width] : t;
                long ll = x > 1 ? pixels[i - 2] : l;
                long tt = y > 1 ? pixels[i - 2 * width] : t;
                long trr = x + 2 < width && y > 0 ? pixels[i + 2 - width] : tr;
                unchecked
                {
                    properties[3] = x;
                    properties[4] = (int)(t > 0 ? t : -t);
                    properties[5] = (int)(l > 0 ? l : -l);
                    properties[6] = (int)t;
                    properties[7] = (int)l;
                    properties[8] = (int)(l - properties[9]);
                    properties[9] = (int)(l + t - tl);
                    properties[10] = (int)(l - tl);
                    properties[11] = (int)(tl - t);
                    properties[12] = (int)(t - tr);
                    properties[13] = (int)(t - tt);
                    properties[14] = (int)(l - ll);
                }

                long weightedPrediction = 0;
                if (weighted is not null)
                {
                    weightedPrediction = weighted.Predict(x, y, width, t, l, tr, tl, tt, out properties[WeightedProperty]);
                }

                references.AsSpan(x * referenceCount, referenceCount).CopyTo(properties[NonReferenceProperties..]);
                ref readonly var leaf = ref tree.Lookup(properties);
                long guess = leaf.Offset + PredictOne(leaf.Predictor, l, t, tt, tl, tr, ll, trr, weightedPrediction);
                uint value = symbols.ReadClustered(leaf.Cluster, ref reader);
                pixels[i] = unchecked((int)(UnpackSigned(value) * (long)leaf.Multiplier + guess));
                weighted?.Update(pixels[i], x, y, width);
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
                long l = x > 0 ? pixels[row + x - 1] : 0;
                long t = y > 0 ? pixels[previous + x] : l;
                long tl = x > 0 && y > 0 ? pixels[previous + x - 1] : l;
                long residual = v - ClampedGradient((int)l, (int)t, (int)tl);
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
        int i = y * width + x;
        long l = x > 0 ? pixels[i - 1] : y > 0 ? pixels[i - width] : 0;
        long t = y > 0 ? pixels[i - width] : l;
        long tl = x > 0 && y > 0 ? pixels[i - 1 - width] : l;
        long tr = x + 1 < width && y > 0 ? pixels[i + 1 - width] : t;
        long ll = x > 1 ? pixels[i - 2] : l;
        long tt = y > 1 ? pixels[i - 2 * width] : t;
        long trr = x + 2 < width && y > 0 ? pixels[i + 2 - width] : tr;
        long weightedPrediction = weighted?.Predict(x, y, width, t, l, tr, tl, tt, out _) ?? 0;
        return PredictOne(predictor, l, t, tt, tl, tr, ll, trr, weightedPrediction);
    }

    private static long PredictOne(JxlPredictor predictor, long left, long top, long topTop, long topLeft, long topRight, long leftLeft,
        long topRightRight, long weighted) => predictor switch
    {
        JxlPredictor.Zero => 0,
        JxlPredictor.Left => left,
        JxlPredictor.Top => top,
        JxlPredictor.Select => Select(left, top, topLeft),
        JxlPredictor.Weighted => weighted,
        JxlPredictor.Gradient => ClampedGradient((int)left, (int)top, (int)topLeft),
        JxlPredictor.TopLeft => topLeft,
        JxlPredictor.TopRight => topRight,
        JxlPredictor.LeftLeft => leftLeft,
        JxlPredictor.Average0 => (left + top) / 2,
        JxlPredictor.Average1 => (left + topLeft) / 2,
        JxlPredictor.Average2 => (topLeft + top) / 2,
        JxlPredictor.Average3 => (top + topRight) / 2,
        JxlPredictor.Average4 => (6 * top - 2 * topTop + 7 * left + leftLeft + topRightRight + 3 * topRight + 8) / 16,
        _ => 0,
    };

    private static long Select(long a, long b, long c)
    {
        long p = a + b - c;
        return Math.Abs(p - a) < Math.Abs(p - b) ? a : b;
    }

    /// <summary>n + w - l clamped to the range of n and w.</summary>
    private static int ClampedGradient(int n, int w, int l)
    {
        int min = Math.Min(n, w);
        int max = Math.Max(n, w);
        int gradient = unchecked((int)((uint)n + (uint)w - (uint)l));
        return l > max ? min : l < min ? max : gradient;
    }

    internal static int UnpackSigned(uint value) => unchecked((int)((value >> 1) ^ (((~value) & 1) - 1)));
}
