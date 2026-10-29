using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>How the MA tree of a lossless Modular frame is made.</summary>
internal enum JxlModularTreeKind
{
    /// <summary>Learned from sampled pixels with the weighted and gradient predictors (efforts 4–9).</summary>
    Learned,

    /// <summary>libjxl's kWPFixedDC: the weighted predictor, split on its maximum error (effort 3).</summary>
    FixedWeighted,

    /// <summary>libjxl's kGradientFixedDC: the gradient predictor, split on the gradient property (effort 2).</summary>
    FixedGradient,

    /// <summary>The gradient predictor with one context per channel (effort 1).</summary>
    ChannelGradient,
}

/// <summary>
/// The lossless Modular encoder's settings at one effort, modelled on libjxl v0.12.0 lib/jxl/enc_modular.cc
/// (ModularFrameEncoder: tree kind, splitting properties, max_property_values, nb_repeats and the node threshold
/// 75 + 14 × speed tier; the RCT search from effort 5) and modular/encoding/enc_encoding.cc (PredefinedTree); BSD-3-Clause.
/// Effort 7 is the encoder of 0.6.0–0.8.2; efforts 8 and 9 behave as 7. libjxl's effort-1 encoder (enc_fast_lossless) is a
/// separate design; effort 1 here keeps this encoder's bitstream with the cheapest tree.
/// </summary>
/// <param name="Tree">How the tree is made.</param>
/// <param name="PropertyCount">The number of leading splitting properties a learned tree may use.</param>
/// <param name="MaxPropertyValues">The quantization of each property for learning.</param>
/// <param name="SampleFraction">The largest fraction of pixels sampled for learning.</param>
/// <param name="NodeThreshold">The least estimated saving (bits) for a split.</param>
/// <param name="SearchRct">Whether the colour transform is searched; otherwise RGB is coded as YCoCg.</param>
internal sealed record JxlModularEffort(JxlModularTreeKind Tree, int PropertyCount, int MaxPropertyValues, double SampleFraction,
    float NodeThreshold, bool SearchRct)
{
    /// <summary>The effort-7 encoder.</summary>
    public static JxlModularEffort Default { get; } = new(JxlModularTreeKind.Learned, 7, 96, 0.5, 117, SearchRct: true);

    /// <summary>The settings of <paramref name="effort"/> 1–9.</summary>
    public static JxlModularEffort For(int effort) => effort switch
    {
        1 => new(JxlModularTreeKind.ChannelGradient, 0, 0, 0, 0, SearchRct: false),
        2 => new(JxlModularTreeKind.FixedGradient, 0, 0, 0, 0, SearchRct: false),
        3 => new(JxlModularTreeKind.FixedWeighted, 0, 0, 0, 0, SearchRct: false),
        // kCheetah, kHare and kWombat: 3, 4 and 5 properties, nb_repeats 0.5 × 0.3, 0.5 and 0.7.
        4 => new(JxlModularTreeKind.Learned, 3, 32, 0.15, 159, SearchRct: false),
        5 => new(JxlModularTreeKind.Learned, 4, 48, 0.25, 145, SearchRct: true),
        6 => new(JxlModularTreeKind.Learned, 5, 64, 0.35, 131, SearchRct: true),
        _ => Default,
    };

    /// <summary>libjxl's MakeFixedTree cutoffs of both fixed trees.</summary>
    private static readonly int[] FixedCutoffs =
    [
        -500, -392, -255, -191, -127, -95, -63, -47, -31, -23, -15, -11, -7, -4, -3, -1, 0,
        1, 3, 5, 7, 11, 15, 23, 31, 47, 63, 95, 127, 191, 255, 392, 500,
    ];

    /// <summary>
    /// The predefined tree of <see cref="Tree"/> (not <see cref="JxlModularTreeKind.Learned"/>) for
    /// <paramref name="totalPixels"/> pixels of <paramref name="bitDepth"/> bits in streams of at most
    /// <paramref name="channels"/> channels.
    /// </summary>
    public List<JxlTreeNode> FixedTree(long totalPixels, int bitDepth, int channels) => Tree switch
    {
        JxlModularTreeKind.FixedWeighted => MakeFixedTree(JxlMaProperty.WeightedMaxError, JxlPredictor.Weighted, totalPixels, bitDepth),
        JxlModularTreeKind.FixedGradient => MakeFixedTree(JxlMaProperty.Gradient, JxlPredictor.Gradient, totalPixels, bitDepth),
        JxlModularTreeKind.ChannelGradient => ChannelTree(channels),
        _ => throw new InvalidOperationException("A learned tree is not predefined."),
    };

    // MakeFixedTree: a balanced tree over the cutoffs, built breadth-first, the left child taking the values above the
    // split; small images get a shallower tree, and samples above 11 bits scale the cutoffs.
    private static List<JxlTreeNode> MakeFixedTree(int property, JxlPredictor predictor, long totalPixels, int bitDepth)
    {
        int logPixels = CeilLog2(Math.Max(totalPixels, 1));
        int minGap = logPixels < 14 ? 8 * (14 - logPixels) : 0;
        int scale = 1 << (bitDepth > 11 ? Math.Min(4, bitDepth - 11) : 0);
        var tree = new List<JxlTreeNode> { JxlTreeNode.Leaf(predictor) };
        var queue = new Queue<(int Begin, int End, int Position)>();
        queue.Enqueue((0, FixedCutoffs.Length, 0));
        while (queue.Count > 0)
        {
            (int begin, int end, int position) = queue.Dequeue();
            if (begin + minGap >= end) continue;
            int split = (begin + end) / 2;
            tree[position] = new JxlTreeNode(property, FixedCutoffs[split] * scale, tree.Count, tree.Count + 1, JxlPredictor.Zero);
            queue.Enqueue((split + 1, end, tree.Count));
            tree.Add(JxlTreeNode.Leaf(predictor));
            queue.Enqueue((begin, split, tree.Count));
            tree.Add(JxlTreeNode.Leaf(predictor));
        }

        return tree;
    }

    // A chain of channel decisions: channel k > the last split takes the next node, so every channel has its own leaf.
    private static List<JxlTreeNode> ChannelTree(int channels)
    {
        var tree = new List<JxlTreeNode>();
        for (int c = 0; c + 1 < channels; c++)
        {
            // Node 2c splits channel > c: its left child is the next split (or the last leaf), its right one channel c's leaf.
            tree.Add(new JxlTreeNode(JxlMaProperty.Channel, c, tree.Count + 2, tree.Count + 1, JxlPredictor.Zero));
            tree.Add(JxlTreeNode.Leaf(JxlPredictor.Gradient));
        }

        tree.Add(JxlTreeNode.Leaf(JxlPredictor.Gradient));
        return tree;
    }

    private static int CeilLog2(long value) => value <= 1 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(value - 1));
}
