namespace PictureSharp.Formats.Jxl.Codestream.Model;

/// <summary>The parameters of the self-correcting (weighted) predictor.</summary>
internal sealed class JxlWeightedHeader(int p1C, int p2C, int p3Ca, int p3Cb, int p3Cc, int p3Cd, int p3Ce, uint[] weights)
{
    public int P1C { get; } = p1C;
    public int P2C { get; } = p2C;
    public int P3Ca { get; } = p3Ca;
    public int P3Cb { get; } = p3Cb;
    public int P3Cc { get; } = p3Cc;
    public int P3Cd { get; } = p3Cd;
    public int P3Ce { get; } = p3Ce;

    public IReadOnlyList<uint> Weights { get; } = weights.Snapshot();

    public static JxlWeightedHeader Default { get; } = new(16, 10, 7, 7, 7, 0, 0, [0xD, 0xC, 0xC, 0xC]);
}
