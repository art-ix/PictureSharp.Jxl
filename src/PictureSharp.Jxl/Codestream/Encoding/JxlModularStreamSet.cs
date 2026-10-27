using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>How the MA tree of a range of Modular streams is made.</summary>
internal enum JxlTreeKind
{
    /// <summary>Learned from the pixels.</summary>
    Learn,

    /// <summary>The weighted predictor with splits on its error (kWPFixedDC), for VarDCT DC.</summary>
    WeightedFixedDc,

    /// <summary>The fixed tree of chroma-from-luma, varblock, quantization and EPF channels (kACMeta).</summary>
    AcMetadata,

    /// <summary>One leaf with the left predictor (kFalconACMeta).</summary>
    LeftPredictor,
}

/// <summary>
/// The Modular streams of a frame with one global MA tree and one entropy code: a tree per range of stream ids
/// (predefined or learned), joined by splits on the stream property (MergeTrees). Modelled on libjxl v0.12.0
/// lib/jxl/enc_modular.cc (ComputeTree, ComputeTokens) and modular/encoding/enc_encoding.cc (PredefinedTree,
/// MakeFixedTree; BSD-3-Clause).
/// </summary>
internal sealed class JxlModularStreamSet
{
    private static readonly int[] DcCutoffs =
    [
        -500, -392, -255, -191, -127, -95, -63, -47, -31, -23, -15, -11, -7, -4, -3, -1, 0, 1, 3, 5, 7, 11, 15, 23, 31, 47, 63, 95, 127,
        191, 255, 392, 500,
    ];

    private readonly SortedDictionary<int, (List<JxlChannel> Channels, JxlTreeKind Kind)> streams = [];
    private JxlTokens treeTokens = new();
    private JxlEntropyEncoder? code;
    private readonly Dictionary<int, JxlTokens> tokens = [];

    /// <summary>Adds stream <paramref name="id"/>; every stream of a tree range must use the same kind.</summary>
    public void Add(int id, List<JxlChannel> channels, JxlTreeKind kind) => streams[id] = (channels, kind);

    /// <summary>Whether any stream has pixels, so the frame needs a global tree.</summary>
    public bool HasPixels => streams.Values.Any(stream => stream.Channels.Any(channel => !channel.IsEmpty));

    /// <summary>
    /// Builds the tree and code; <paramref name="splits"/> are the first stream ids of the tree ranges, ending with the
    /// number of streams.
    /// </summary>
    public void Build(IReadOnlyList<int> splits, CancellationToken cancellationToken)
    {
        var useful = new List<int>();
        var trees = new List<List<JxlTreeNode>>();
        for (int chunk = 0; chunk + 1 < splits.Count; chunk++)
        {
            List<(int Id, List<JxlChannel> Channels)> range = [.. streams
                .Where(pair => pair.Key >= splits[chunk] && pair.Key < splits[chunk + 1] && pair.Value.Channels.Any(channel => !channel.IsEmpty))
                .Select(pair => (pair.Key, pair.Value.Channels))];
            if (range.Count == 0) continue;
            useful.Add(splits[chunk]);
            JxlTreeKind kind = streams[range[0].Id].Kind;
            long pixels = Math.Max(1, range.Sum(stream => stream.Channels.Sum(channel => (long)channel.Width * channel.Height)));
            trees.Add(kind switch
            {
                JxlTreeKind.WeightedFixedDc => FixedTree(JxlMaProperty.WeightedMaxError, DcCutoffs, JxlPredictor.Weighted, pixels),
                JxlTreeKind.AcMetadata => AcMetadataTree(pixels),
                JxlTreeKind.LeftPredictor => [JxlTreeNode.Leaf(JxlPredictor.Left)],
                _ => JxlModularTreeEncoder.Learn(range, cancellationToken),
            });
        }

        if (trees.Count == 0) return;
        useful.Add(splits[^1]);
        var tree = new List<JxlTreeNode>();
        Merge(trees, useful, 0, trees.Count, tree);
        (treeTokens, int[] leafOf) = JxlModularTreeEncoder.Tokenize(tree);
        List<(int Id, List<JxlChannel> Channels)> all = [.. streams.Select(pair => (pair.Key, pair.Value.Channels))];
        List<JxlTokens> streamTokens = JxlModularTreeEncoder.TokenizeStreams(all, tree, leafOf, cancellationToken);
        for (int i = 0; i < all.Count; i++) tokens[all[i].Id] = streamTokens[i];
        code = JxlEntropyEncoder.Build(leafOf.Count(leaf => leaf >= 0), streamTokens);
    }

    /// <summary>Writes the global tree and its code (the start of the global Modular section), or that there is none.</summary>
    public void WriteGlobal(JxlBitWriter writer)
    {
        writer.Write(code is not null);
        if (code is null) return;
        JxlEntropyEncoder.WriteStandalone(writer, JxlMaTree.TreeContexts, treeTokens);
        code.WriteHeader(writer);
    }

    /// <summary>
    /// Writes stream <paramref name="id"/>: its header and residuals, or nothing when it has no pixels; the header
    /// alone with <paramref name="header"/> (the global stream of an image whose channels are all in groups).
    /// </summary>
    public void WriteStream(JxlBitWriter writer, int id, bool header = false)
    {
        bool pixels = streams.TryGetValue(id, out var stream) && stream.Channels.Any(channel => !channel.IsEmpty);
        if (!pixels && !header) return;
        writer.Write(true);                  // use the global tree
        writer.Write(true);                  // default weighted predictor parameters
        JxlFieldWriter.U32(writer, 0, JxlTransformFields.TransformCount);
        if (pixels) code!.WriteStream(writer, tokens[id]);
    }

    // MakeFixedTree: a balanced tree over the cutoffs of one property, lower for small images.
    private static List<JxlTreeNode> FixedTree(int property, int[] cutoffs, JxlPredictor predictor, long pixels)
    {
        int logPixels = CeilLog2(pixels);
        int minGap = logPixels < 14 ? 8 * (14 - logPixels) : 0;
        var tree = new List<JxlTreeNode> { JxlTreeNode.Leaf(predictor) };
        var queue = new Queue<(int Begin, int End, int Position)>();
        queue.Enqueue((0, cutoffs.Length, 0));
        while (queue.Count > 0)
        {
            (int begin, int end, int position) = queue.Dequeue();
            if (begin + minGap >= end) continue;
            int split = (begin + end) / 2;
            tree[position] = new JxlTreeNode(property, cutoffs[split], tree.Count, tree.Count + 1, predictor);
            queue.Enqueue((split + 1, end, tree.Count));
            tree.Add(JxlTreeNode.Leaf(predictor));
            queue.Enqueue((begin, split, tree.Count));
            tree.Add(JxlTreeNode.Leaf(predictor));
        }

        return tree;
    }

    // kACMeta: channels 0 and 1 (chroma from luma) with the gradient, 2 (varblocks and quantization) by the left
    // value, 3 (EPF sharpness) by the top and left values.
    private static List<JxlTreeNode> AcMetadataTree(long pixels)
    {
        if (pixels < 1024) return [JxlTreeNode.Leaf(JxlPredictor.Left)];
        static JxlTreeNode Split(int property, int value, int left) => new(property, value, left, left + 1, JxlPredictor.Zero);
        return
        [
            Split(JxlMaProperty.Channel, 1, 1),
            Split(JxlMaProperty.Channel, 2, 3),
            Split(JxlMaProperty.Channel, 0, 5),
            Split(JxlMaProperty.N, 3, 21),
            Split(JxlMaProperty.Y, 0, 7),
            JxlTreeNode.Leaf(JxlPredictor.Gradient),
            JxlTreeNode.Leaf(JxlPredictor.Gradient),
            Split(JxlMaProperty.W, 5, 9),
            Split(JxlMaProperty.W, 5, 15),
            Split(JxlMaProperty.W, 11, 11),
            Split(JxlMaProperty.W, 3, 13),
            JxlTreeNode.Leaf(JxlPredictor.Left),
            JxlTreeNode.Leaf(JxlPredictor.Left),
            JxlTreeNode.Leaf(JxlPredictor.Left),
            JxlTreeNode.Leaf(JxlPredictor.Left),
            Split(JxlMaProperty.W, 11, 17),
            Split(JxlMaProperty.W, 3, 19),
            JxlTreeNode.Leaf(JxlPredictor.Zero),
            JxlTreeNode.Leaf(JxlPredictor.Zero),
            JxlTreeNode.Leaf(JxlPredictor.Zero),
            JxlTreeNode.Leaf(JxlPredictor.Zero),
            Split(JxlMaProperty.W, 3, 23),
            Split(JxlMaProperty.W, 3, 25),
            JxlTreeNode.Leaf(JxlPredictor.Zero),
            JxlTreeNode.Leaf(JxlPredictor.Zero),
            JxlTreeNode.Leaf(JxlPredictor.Zero),
            JxlTreeNode.Leaf(JxlPredictor.Zero),
        ];
    }

    // MergeTrees: the trees of ranges [begin, end), split on the stream id at the middle range's first stream.
    private static void Merge(List<List<JxlTreeNode>> trees, List<int> splits, int begin, int end, List<JxlTreeNode> tree)
    {
        if (end == begin + 1)
        {
            int offset = tree.Count;
            foreach (JxlTreeNode node in trees[begin])
            {
                tree.Add(node.IsLeaf ? node : node with { Left = node.Left + offset, Right = node.Right + offset });
            }

            return;
        }

        int mid = (begin + end) / 2;
        int current = tree.Count;
        tree.Add(default);
        int left = tree.Count;
        Merge(trees, splits, mid, end, tree);
        int right = tree.Count;
        Merge(trees, splits, begin, mid, tree);
        tree[current] = new JxlTreeNode(JxlMaProperty.Stream, splits[mid] - 1, left, right, JxlPredictor.Zero);
    }
}
