namespace PictureSharp.Formats.Jxl.Codestream.Buffers;

/// <summary>GC-owned planes transferred from a completed frame; later frames borrow them without mutation.</summary>
internal sealed class JxlReferenceFrame(JxlPlane[] color, JxlPlane[] extra)
{
    public IReadOnlyList<JxlPlane> Color { get; } = Array.AsReadOnly((JxlPlane[])color.Clone());
    public IReadOnlyList<JxlPlane> Extra { get; } = Array.AsReadOnly((JxlPlane[])extra.Clone());
}
