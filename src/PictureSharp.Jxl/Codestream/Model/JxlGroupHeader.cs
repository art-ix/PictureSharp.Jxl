namespace PictureSharp.Formats.Jxl.Codestream.Model;

/// <summary>The header of a Modular stream: tree choice, weighted predictor parameters and transforms.</summary>
internal sealed class JxlGroupHeader(bool useGlobalTree, JxlWeightedHeader weighted, JxlTransform[] transforms)
{
    public bool UseGlobalTree { get; } = useGlobalTree;
    public JxlWeightedHeader Weighted { get; } = weighted;

    public IReadOnlyList<JxlTransform> Transforms { get; } = transforms.Snapshot();

    public static JxlGroupHeader Default { get; } = new(false, JxlWeightedHeader.Default, []);
}
