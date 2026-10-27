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
    public static List<JxlTreeNode> Learn(List<(int Id, List<JxlChannel> Channels)> streams, CancellationToken cancellationToken)
    {
        long total = streams.Sum(stream => stream.Channels.Sum(channel => (long)channel.Width * channel.Height));
        int[] properties = streams.Count < MinStreamsForStreamProperty ? FewStreamProperties : ManyStreamProperties;
        var learner = new JxlTreeLearner(properties, Predictors, MaxPropertyValues);
        double fraction = Math.Min(SampleFraction, MaxSamples / (double)Math.Max(total, 1));
        var visitor = new SampleVisitor(learner, (uint)(fraction * uint.MaxValue));
        foreach ((int id, List<JxlChannel> channels) in streams)
        {
            for (int c = 0; c < channels.Count; c++) Scan(channels[c], c, id, ref visitor, cancellationToken);
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
    public static List<JxlTokens> TokenizeStreams(List<(int Id, List<JxlChannel> Channels)> streams, List<JxlTreeNode> tree, int[] leafOf,
        CancellationToken cancellationToken)
    {
        var tokens = new List<JxlTokens>();
        foreach ((int id, List<JxlChannel> channels) in streams)
        {
            var visitor = new TokenVisitor(tree, leafOf, new JxlTokens());
            for (int c = 0; c < channels.Count; c++) Scan(channels[c], c, id, ref visitor, cancellationToken);
            tokens.Add(visitor.Tokens);
        }

        return tokens;
    }

    private interface IPixelVisitor
    {
        void Visit(ReadOnlySpan<int> properties, int pixel, long weighted, long gradient);
    }

    /// <summary>Visits every pixel with its properties and predictions, exactly as JxlMaTree.DecodeChannel computes them.</summary>
    private static void Scan<TVisitor>(JxlChannel channel, int channelIndex, int stream, ref TVisitor visitor, CancellationToken cancellationToken)
        where TVisitor : struct, IPixelVisitor
    {
        if (channel.IsEmpty) return;
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
                JxlMaProperty.SetLocal(properties, x, neighbours);
                long prediction = weighted.Predict(x, y, neighbours, out properties[JxlMaProperty.WeightedMaxError]);
                long gradient = JxlMaProperty.ClampedGradient((int)neighbours.W, (int)neighbours.N, (int)neighbours.NW);
                visitor.Visit(properties, pixels[row + x], prediction, gradient);
                weighted.Update(pixels[row + x], x, y);
            }
        }
    }

    private struct SampleVisitor(JxlTreeLearner learner, uint threshold) : IPixelVisitor
    {
        private readonly long[] residuals = new long[2];
        private uint state = 0x9E3779B9;

        public void Visit(ReadOnlySpan<int> properties, int pixel, long weighted, long gradient)
        {
            // xorshift32: a fixed sequence, so the output does not depend on anything but the image.
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            if (state > threshold) return;
            residuals[0] = pixel - weighted;
            residuals[1] = pixel - gradient;
            learner.Add(properties, residuals);
        }
    }

    private readonly struct TokenVisitor(List<JxlTreeNode> tree, int[] leafOf, JxlTokens tokens) : IPixelVisitor
    {
        public JxlTokens Tokens => tokens;

        public void Visit(ReadOnlySpan<int> properties, int pixel, long weighted, long gradient)
        {
            int node = 0;
            while (!tree[node].IsLeaf)
            {
                node = properties[tree[node].Property] > tree[node].Split ? tree[node].Left : tree[node].Right;
            }

            long prediction = tree[node].Predictor == JxlPredictor.Weighted ? weighted : gradient;
            tokens.Add(leafOf[node], PackSigned(pixel - prediction));
        }
    }
}
