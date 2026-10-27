using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Buffers;

/// <summary>Rendered, GC-owned planes. ColorEncoding describes the output, not necessarily the source ICC.
/// The planes are transferred from the completed frame and consumed synchronously by the adapter.</summary>
internal sealed class JxlDecodedImage(int width, int height, JxlPlane[] color, JxlPlane? alpha,
    bool isGrayscale, JxlColorEncoding colorEncoding)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public ReadOnlySpan<JxlPlane> Color => color;
    public JxlPlane? Alpha { get; } = alpha;
    public bool IsGrayscale { get; } = isGrayscale;
    public JxlColorEncoding ColorEncoding { get; } = colorEncoding;
}
