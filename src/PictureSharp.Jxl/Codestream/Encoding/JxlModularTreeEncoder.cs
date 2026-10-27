using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// The MA tree of a lossless Modular frame: learned from sampled pixels with the weighted and gradient predictors,
/// coded as tokens, and used to tokenize the residuals of every stream. Pixel properties and predictions are those of
/// <see cref="JxlMaTree.DecodeChannel"/>, so the decoder reproduces every prediction.
/// </summary>
internal static class JxlModularTreeEncoder
{
    // libjxl's splitting_heuristics_node_threshold at effort 7: 75 + 14 × speed tier 3.
    private const float NodeThreshold = 117;
    private const int MaxPropertyValues = 96;
    private const int MaxSamples = 1 << 20;
    private const double SampleFraction = 0.5;
    // libjxl leaves out the stream property when there are fewer streams.
    private const int MinStreamsForStreamProperty = 30;
    private static readonly JxlPredictor[] Predictors = [JxlPredictor.Weighted, JxlPredictor.Gradient];

    private static readonly int[] FewStreamProperties =
    [
        JxlMaProperty.Channel, JxlMaProperty.WeightedMaxError, JxlMaProperty.Gradient, JxlMaProperty.WMinusNW,
        JxlMaProperty.NWMinusN, JxlMaProperty.NMinusNE, JxlMaProperty.NMinusNN,
    ];

    private static readonly int[] ManyStreamProperties =
    [
        JxlMaProperty.Channel, JxlMaProperty.Stream, JxlMaProperty.WeightedMaxError, JxlMaProperty.Gradient,
        JxlMaProperty.WMinusNW, JxlMaProperty.NWMinusN, JxlMaProperty.NMinusNE,
    ];

    /// <summary>Learns one tree for all <paramref name="streams"/> from a fixed pseudo-random sample of their pixels.</summary>
    /// <param name="streams">The streams to learn from.</param>
    /// <param name="cancellationToken">Cancels learning.</param>
    /// <param name="errors">
    /// Receives the weighted predictor's error of every pixel of each channel, so <see cref="TokenizeStreams"/> need
    /// not run the predictor again; a channel is left out when its errors cannot reproduce the predictions.
    /// </param>
    public static List<JxlTreeNode> Learn(List<(int Id, List<JxlChannel> Channels)> streams, CancellationToken cancellationToken,
        Dictionary<JxlChannel, int[]>? errors = null)
    {
        long total = streams.Sum(stream => stream.Channels.Sum(channel => (long)channel.Width * channel.Height));
        int[] properties = streams.Count < MinStreamsForStreamProperty ? FewStreamProperties : ManyStreamProperties;
        // The sample scan sets the local properties of sampled pixels only, so the running one is not available.
        System.Diagnostics.Debug.Assert(!properties.Contains(JxlMaProperty.WMinusPreviousGradient));
        var learner = new JxlTreeLearner(properties, Predictors, MaxPropertyValues);
        double fraction = Math.Min(SampleFraction, MaxSamples / (double)Math.Max(total, 1));
        var visitor = new SampleVisitor(learner, (uint)(fraction * uint.MaxValue));
        foreach ((int id, List<JxlChannel> channels) in streams)
        {
            for (int c = 0; c < channels.Count; c++)
            {
                int[]? channelErrors = errors is null || channels[c].IsEmpty ? null : new int[channels[c].Pixels.Length];
                if (Scan(channels[c], c, id, ref visitor, cancellationToken, channelErrors) && channelErrors is not null)
                {
                    errors![channels[c]] = channelErrors;
                }
            }
        }

        return learner.Learn(NodeThreshold, cancellationToken);
    }

    /// <summary>The tree's tokens in breadth-first order (TokenizeTree) and the leaf (context) index of every node.</summary>
    public static (JxlTokens Tokens, int[] LeafOf) Tokenize(List<JxlTreeNode> tree)
    {
        var tokens = new JxlTokens();
        int[] leafOf = new int[tree.Count];
        Array.Fill(leafOf, -1);
        var queue = new Queue<int>();
        queue.Enqueue(0);
        int leaves = 0;
        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            JxlTreeNode node = tree[index];
            tokens.Add((int)JxlMaTreeContext.Property, (uint)(node.Property + 1));
            if (node.IsLeaf)
            {
                tokens.Add((int)JxlMaTreeContext.Predictor, (uint)node.Predictor);
                tokens.Add((int)JxlMaTreeContext.Offset, 0);
                tokens.Add((int)JxlMaTreeContext.MultiplierLog, 0);
                tokens.Add((int)JxlMaTreeContext.MultiplierBits, 0);
                leafOf[index] = leaves++;
                continue;
            }

            tokens.Add((int)JxlMaTreeContext.SplitValue, PackSigned(node.Split));
            queue.Enqueue(node.Left);
            queue.Enqueue(node.Right);
        }

        return (tokens, leafOf);
    }

    /// <summary>The residual tokens of every stream, each in the context of its pixel's leaf.</summary>
    /// <remarks><paramref name="errors"/> are the weighted errors <see cref="Learn"/> recorded, if any.</remarks>
    public static List<JxlTokens> TokenizeStreams(List<(int Id, List<JxlChannel> Channels)> streams, List<JxlTreeNode> tree, int[] leafOf,
        CancellationToken cancellationToken, Dictionary<JxlChannel, int[]>? errors = null)
    {
        var tokens = new List<JxlTokens>();
        foreach ((int id, List<JxlChannel> channels) in streams)
        {
            var visitor = new TokenVisitor(tree, leafOf, new JxlTokens());
            for (int c = 0; c < channels.Count; c++)
            {
                visitor.StartChannel(c, id);
                if (errors is not null && errors.TryGetValue(channels[c], out int[]? channelErrors))
                {
                    TokenizeRecorded(channels[c], c, id, channelErrors, visitor.Nodes, visitor.Tokens, cancellationToken);
                }
                else
                {
                    Scan(channels[c], c, id, ref visitor, cancellationToken);
                }
            }

            tokens.Add(visitor.Tokens);
        }

        return tokens;
    }

    private interface IPixelVisitor
    {
        /// <summary>Whether the next pixel is visited; every pixel is offered once, in scan order.</summary>
        bool Wants();

        void Visit(ReadOnlySpan<int> properties, int pixel, long weighted, long gradient, JxlNeighbours neighbours);
    }

    /// <summary>Visits every pixel with its properties and predictions, exactly as JxlMaTree.DecodeChannel computes them.</summary>
    /// <returns>
    /// Whether <paramref name="errors"/>, when given, received weighted errors that reproduce every weighted prediction.
    /// </returns>
    private static bool Scan<TVisitor>(JxlChannel channel, int channelIndex, int stream, ref TVisitor visitor, CancellationToken cancellationToken,
        int[]? errors = null)
        where TVisitor : struct, IPixelVisitor
    {
        if (channel.IsEmpty) return false;
        bool reproducible = true;
        int width = channel.Width;
        int[] pixels = channel.Pixels;
        Span<int> properties = stackalloc int[JxlMaProperty.Local];
        var weighted = new JxlWeightedPredictor(JxlWeightedHeader.Default, width);
        for (int y = 0; y < channel.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JxlMaProperty.StartRow(properties, channelIndex, stream, y);
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                var neighbours = JxlNeighbours.At(pixels, width, x, y);
                long prediction = weighted.Predict(x, y, neighbours, out properties[JxlMaProperty.WeightedMaxError]);
                if (visitor.Wants())
                {
                    JxlMaProperty.SetLocal(properties, x, neighbours);
                    long gradient = JxlMaProperty.ClampedGradient((int)neighbours.W, (int)neighbours.N, (int)neighbours.NW);
                    visitor.Visit(properties, pixels[row + x], prediction, gradient, neighbours);
                }

                int error = weighted.Update(pixels[row + x], x, y);
                if (errors is not null)
                {
                    errors[row + x] = error;
                    reproducible &= JxlWeightedPredictor.FromError(error, pixels[row + x]) == prediction;
                }
            }
        }

        return reproducible;
    }

    /// <summary>
    /// Tokenizes a channel as <see cref="TokenVisitor"/> does, with the weighted errors recorded by an earlier scan
    /// instead of the predictor. Rows go through the tree together, one level at a time without branches: the steps
    /// of different pixels do not wait for each other, while one pixel's walk is a chain of dependent loads.
    /// </summary>
    private static void TokenizeRecorded(JxlChannel channel, int channelIndex, int stream, int[] errors, Node[] nodes, JxlTokens tokens,
        CancellationToken cancellationToken)
    {
        int width = channel.Width;
        int[] pixels = channel.Pixels;
        const int Stride = JxlMaProperty.Local;
        // Leaves step to themselves: the channel is never above int.MaxValue, so they take Left + 1.
        var walk = new Node[nodes.Length];
        for (int i = 0; i < nodes.Length; i++) walk[i] = nodes[i].Property >= 0 ? nodes[i] : new Node(JxlMaProperty.Channel, int.MaxValue, i - 1, 0);
        int[] rowProperties = new int[width * Stride];
        long[] weightedRow = new long[width];
        long[] gradientRow = new long[width];
        int[] at = new int[width];
        int[] zeros = new int[width];
        Span<int> properties = stackalloc int[Stride];
        for (int y = 0; y < channel.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JxlMaProperty.StartRow(properties, channelIndex, stream, y);
            int row = y * width;
            ReadOnlySpan<int> rowErrors = errors.AsSpan(row, width);
            ReadOnlySpan<int> previousErrors = y > 0 ? errors.AsSpan(row - width, width) : zeros;
            for (int x = 0; x < width; x++)
            {
                var neighbours = JxlNeighbours.At(pixels, width, x, y);
                JxlMaProperty.SetLocal(properties, x, neighbours);
                properties[JxlMaProperty.WeightedMaxError] = JxlWeightedPredictor.MaxError(rowErrors, previousErrors, x);
                properties.CopyTo(rowProperties.AsSpan(x * Stride, Stride));
                weightedRow[x] = JxlWeightedPredictor.FromError(rowErrors[x], pixels[row + x]);
                gradientRow[x] = JxlMaProperty.ClampedGradient((int)neighbours.W, (int)neighbours.N, (int)neighbours.NW);
            }

            Array.Clear(at);
            int moved;
            do
            {
                moved = 0;
                for (int x = 0; x < width; x++)
                {
                    int current = at[x];
                    Node node = walk[current];
                    int next = node.Left + (rowProperties[x * Stride + node.Property] > node.Split ? 0 : 1);
                    moved |= next ^ current;
                    at[x] = next;
                }
            }
            while (moved != 0);

            for (int x = 0; x < width; x++)
            {
                Node leaf = nodes[at[x]];
                long prediction = (JxlPredictor)leaf.Right switch
                {
                    JxlPredictor.Weighted => weightedRow[x],
                    JxlPredictor.Gradient => gradientRow[x],
                    JxlPredictor predictor => JxlMaTree.Predict(predictor, JxlNeighbours.At(pixels, width, x, y), weightedRow[x]),
                };
                tokens.Add(leaf.Left, PackSigned(pixels[row + x] - prediction));
            }
        }
    }

    private struct SampleVisitor(JxlTreeLearner learner, uint threshold) : IPixelVisitor
    {
        private readonly long[] residuals = new long[2];
        private uint state = 0x9E3779B9;

        public bool Wants()
        {
            // xorshift32: a fixed sequence, so the output does not depend on anything but the image.
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state <= threshold;
        }

        public readonly void Visit(ReadOnlySpan<int> properties, int pixel, long weighted, long gradient, JxlNeighbours neighbours)
        {
            residuals[0] = pixel - weighted;
            residuals[1] = pixel - gradient;
            learner.Add(properties, residuals);
        }
    }

    /// <summary>
    /// The tree with the decisions on the channel and stream resolved (FilterTree), packed for the per-pixel walk: a
    /// leaf has property -1, its context as the left child and its predictor as the right one.
    /// </summary>
    private static Node[] Filter(List<JxlTreeNode> tree, int[] leafOf, int channel, int stream)
    {
        var nodes = new List<Node>();
        var queue = new Queue<(int Node, int Parent, bool IsLeft)>();
        queue.Enqueue((0, -1, false));
        while (queue.Count > 0)
        {
            (int index, int parent, bool isLeft) = queue.Dequeue();
            JxlTreeNode node = tree[index];
            while (node.Property is JxlMaProperty.Channel or JxlMaProperty.Stream)
            {
                int value = node.Property == JxlMaProperty.Channel ? channel : stream;
                index = value > node.Split ? node.Left : node.Right;
                node = tree[index];
            }

            if (parent >= 0)
            {
                Node patched = nodes[parent];
                nodes[parent] = isLeft ? patched with { Left = nodes.Count } : patched with { Right = nodes.Count };
            }

            if (node.IsLeaf)
            {
                nodes.Add(new Node(-1, 0, leafOf[index], (int)node.Predictor));
                continue;
            }

            queue.Enqueue((node.Left, nodes.Count, true));
            queue.Enqueue((node.Right, nodes.Count, false));
            nodes.Add(new Node(node.Property, node.Split, 0, 0));
        }

        return [.. nodes];
    }

    private readonly record struct Node(int Property, int Split, int Left, int Right);

    private struct TokenVisitor(List<JxlTreeNode> tree, int[] leafOf, JxlTokens tokens) : IPixelVisitor
    {
        private Node[] nodes = [];

        public readonly JxlTokens Tokens => tokens;

        /// <summary>The tree of the current channel.</summary>
        public readonly Node[] Nodes => nodes;

        public readonly bool Wants() => true;

        /// <summary>Resolves the tree for the channel the following pixels belong to.</summary>
        public void StartChannel(int channel, int stream) => nodes = Filter(tree, leafOf, channel, stream);

        public readonly void Visit(ReadOnlySpan<int> properties, int pixel, long weighted, long gradient, JxlNeighbours neighbours)
        {
            Node[] packed = nodes;
            Node node = packed[0];
            while (node.Property >= 0)
            {
                node = packed[properties[node.Property] > node.Split ? node.Left : node.Right];
            }

            long prediction = (JxlPredictor)node.Right switch
            {
                JxlPredictor.Weighted => weighted,
                JxlPredictor.Gradient => gradient,
                JxlPredictor predictor => JxlMaTree.Predict(predictor, neighbours, weighted),
            };
            tokens.Add(node.Left, PackSigned(pixel - prediction));
        }
    }
}
