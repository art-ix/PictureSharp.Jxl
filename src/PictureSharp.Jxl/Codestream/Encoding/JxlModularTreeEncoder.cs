using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
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
    /// <param name="buffers">Owns recorded error buffers when provided.</param>
    /// <param name="parallelism">
    /// The maximum degree of parallelism of the channel scans (1 = single-threaded, -1 = automatic). Every channel starts
    /// at the state of the sampling sequence its sequential scan would have, and its samples are added to the learner
    /// in channel order, so the tree does not depend on it.
    /// </param>
    public static List<JxlTreeNode> Learn(List<(int Id, List<JxlChannel> Channels)> streams, CancellationToken cancellationToken,
        Dictionary<JxlChannel, int[]>? errors = null, JxlBufferScope? buffers = null, int parallelism = JxlParallel.Sequential)
    {
        long total = streams.Sum(stream => stream.Channels.Sum(channel => (long)channel.Width * channel.Height));
        int[] properties = streams.Count < MinStreamsForStreamProperty ? FewStreamProperties : ManyStreamProperties;
        // The sample scan sets the local properties of sampled pixels only, so the running one is not available.
        System.Diagnostics.Debug.Assert(!properties.Contains(JxlMaProperty.WMinusPreviousGradient));
        using var learner = new JxlTreeLearner(properties, Predictors, MaxPropertyValues);
        double fraction = Math.Min(SampleFraction, MaxSamples / (double)Math.Max(total, 1));
        uint threshold = (uint)(fraction * uint.MaxValue);
        // Empty channels are not scanned and do not advance the sampling sequence.
        var scans = new List<(int Id, int Index, JxlChannel Channel, uint State)>();
        uint state = SampleVisitor.InitialState;
        foreach ((int id, List<JxlChannel> channels) in streams)
        {
            for (int c = 0; c < channels.Count; c++)
            {
                if (channels[c].IsEmpty) continue;
                scans.Add((id, c, channels[c], state));
                state = SampleVisitor.Advance(state, (long)channels[c].Width * channels[c].Height);
            }
        }

        if (!JxlParallel.IsParallel(parallelism, scans.Count))
        {
            var visitor = new SampleVisitor(learner, threshold, SampleVisitor.InitialState);
            foreach ((int id, int c, JxlChannel channel, _) in scans)
            {
                int[]? channelErrors = NewErrors(channel, errors, buffers);
                if (Scan(channel, c, id, ref visitor, cancellationToken, channelErrors) && channelErrors is not null)
                {
                    errors![channel] = channelErrors;
                }
            }

            return learner.Learn(NodeThreshold, cancellationToken);
        }

        // Each channel collects its own samples from its own position of the sequence.
        var batches = new JxlTreeLearner?[scans.Count];
        var recorded = new int[]?[scans.Count];
        try
        {
            JxlParallel.For(scans.Count, parallelism, cancellationToken, i =>
            {
                (int id, int c, JxlChannel channel, uint start) = scans[i];
                var batch = batches[i] = new JxlTreeLearner(properties, Predictors, MaxPropertyValues);
                var visitor = new SampleVisitor(batch, threshold, start);
                int[]? channelErrors = NewErrors(channel, errors, buffers);
                if (Scan(channel, c, id, ref visitor, cancellationToken, channelErrors)) recorded[i] = channelErrors;
            });
            for (int i = 0; i < scans.Count; i++)
            {
                learner.Append(batches[i]!);
                batches[i]!.Dispose();
                if (recorded[i] is { } channelErrors) errors![scans[i].Channel] = channelErrors;
            }
        }
        finally
        {
            foreach (JxlTreeLearner? batch in batches) batch?.Dispose();
        }

        return learner.Learn(NodeThreshold, cancellationToken);
    }

    private static int[]? NewErrors(JxlChannel channel, Dictionary<JxlChannel, int[]>? errors, JxlBufferScope? buffers) =>
        errors is null ? null : buffers is null ? new int[channel.Width * channel.Height] : buffers.Rent<int>(channel.Width * channel.Height, clear: false);

    /// <summary>The tree's tokens in breadth-first order (TokenizeTree) and the leaf (context) index of every node.</summary>
    public static (JxlTokens Tokens, int[] LeafOf) Tokenize(List<JxlTreeNode> tree, JxlBufferScope? buffers = null)
    {
        var tokens = new JxlTokens(buffers);
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
        CancellationToken cancellationToken, Dictionary<JxlChannel, int[]>? errors = null, JxlBufferScope? buffers = null,
        int parallelism = JxlParallel.Sequential)
    {
        // Streams are tokenized on their own and kept in stream order.
        var tokens = new JxlTokens[streams.Count];
        JxlParallel.For(streams.Count, parallelism, cancellationToken, s =>
        {
            (int id, List<JxlChannel> channels) = streams[s];
            var visitor = new TokenVisitor(tree, leafOf, new JxlTokens(buffers));
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

            tokens[s] = visitor.Tokens;
        });

        return [.. tokens];
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
        using var weighted = new JxlWeightedPredictor(JxlWeightedHeader.Default, width);
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
        using var buffers = new JxlBufferScope();
        var walk = buffers.Rent<Node>(nodes.Length, clear: false);
        for (int i = 0; i < nodes.Length; i++) walk[i] = nodes[i].Property >= 0 ? nodes[i] : new Node(JxlMaProperty.Channel, int.MaxValue, i - 1, 0);
        int[] rowProperties = buffers.Rent<int>(width * Stride);
        long[] weightedRow = buffers.Rent<long>(width);
        long[] gradientRow = buffers.Rent<long>(width);
        int[] at = buffers.Rent<int>(width);
        int[] zeros = buffers.Rent<int>(width);
        Span<int> properties = stackalloc int[Stride];
        for (int y = 0; y < channel.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JxlMaProperty.StartRow(properties, channelIndex, stream, y);
            int row = y * width;
            ReadOnlySpan<int> rowErrors = errors.AsSpan(row, width);
            ReadOnlySpan<int> previousErrors = y > 0 ? errors.AsSpan(row - width, width) : zeros.AsSpan(0, width);
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

    private struct SampleVisitor(JxlTreeLearner learner, uint threshold, uint state) : IPixelVisitor
    {
        public const uint InitialState = 0x9E3779B9;
        private readonly long[] residuals = new long[2];
        private uint state = state;

        public bool Wants()
        {
            // xorshift32: a fixed sequence, so the output does not depend on anything but the image.
            state = Next(state);
            return state <= threshold;
        }

        private static uint Next(uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        /// <summary>
        /// The state after <paramref name="steps"/> steps from <paramref name="state"/>. xorshift32 is linear over GF(2):
        /// one step is a 32×32 bit matrix (column i is the step of bit i), raised to the power by squaring.
        /// </summary>
        public static uint Advance(uint state, long steps)
        {
            Span<uint> step = stackalloc uint[32];
            Span<uint> square = stackalloc uint[32];
            for (int i = 0; i < 32; i++) step[i] = Next(1u << i);
            for (; steps > 0; steps >>= 1)
            {
                if ((steps & 1) != 0) state = Apply(step, state);
                for (int i = 0; i < 32; i++) square[i] = Apply(step, step[i]);
                square.CopyTo(step);
            }

            return state;
        }

        private static uint Apply(ReadOnlySpan<uint> matrix, uint value)
        {
            uint result = 0;
            for (; value != 0; value &= value - 1) result ^= matrix[System.Numerics.BitOperations.TrailingZeroCount(value)];
            return result;
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
