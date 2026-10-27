namespace PictureSharp.Formats.Jxl.Codestream.Buffers;

/// <summary>One session's four patch slots and four DC levels. Saved planes are borrowed read-only by later frames.</summary>
internal sealed class JxlReferenceFrames
{
    private readonly JxlReferenceFrame?[] references = new JxlReferenceFrame?[4];
    private readonly IReadOnlyList<JxlPlane>?[] dc = new IReadOnlyList<JxlPlane>?[4];

    public JxlReferenceFrame GetPatch(uint slot) => slot < references.Length && references[slot] is { } frame
        ? frame : throw JxlDescriptor.Corrupt("Invalid reference frame of a patch.");

    public bool HasDc(uint index) => index < dc.Length && dc[index] is not null;

    public IReadOnlyList<JxlPlane> GetDc(uint index) => HasDc(index) ? dc[index]!
        : throw JxlDescriptor.Corrupt("A frame uses a DC frame that was not decoded.");

    public void StoreReference(uint slot, JxlReferenceFrame frame, bool beforeColorTransform)
    {
        if (slot >= references.Length) throw JxlDescriptor.Corrupt("Invalid reference frame slot.");
        if (!beforeColorTransform) throw JxlDescriptor.Unsupported("post-colour-transform references require frame blending.");
        references[slot] = frame;
    }

    public void StoreDc(uint level, JxlReferenceFrame frame)
    {
        if (level is < 1 or > 4) throw JxlDescriptor.Corrupt("Invalid DC frame level.");
        dc[level - 1] = frame.Color;
    }
}
