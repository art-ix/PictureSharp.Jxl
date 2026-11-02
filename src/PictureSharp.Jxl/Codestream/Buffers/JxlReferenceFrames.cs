namespace PictureSharp.Formats.Jxl.Codestream.Buffers;

/// <summary>
/// One session's four reference slots and four DC levels. Saved planes are borrowed read-only by later frames: patches
/// read any slot, blending only slots saved after the colour transform at the image size.
/// </summary>
internal sealed class JxlReferenceFrames
{
    private readonly JxlReferenceFrame?[] references = new JxlReferenceFrame?[4];
    private readonly bool[] beforeColorTransform = new bool[4];
    private readonly IReadOnlyList<JxlPlane>?[] dc = new IReadOnlyList<JxlPlane>?[4];

    public JxlReferenceFrame GetPatch(uint slot) => slot < references.Length && references[slot] is { } frame
        ? frame : throw JxlDescriptor.Corrupt("Invalid reference frame of a patch.");

    /// <summary>
    /// The background of a blended frame in <paramref name="slot"/>, or null for an empty slot (zeros). Like libjxl's
    /// BlendingStage, a slot saved before the colour transform or smaller than the image cannot be a background.
    /// </summary>
    public JxlReferenceFrame? GetBackground(uint slot, int width, int height)
    {
        if (slot >= references.Length) throw JxlDescriptor.Corrupt("Invalid reference frame slot.");
        if (references[slot] is not { } frame) return null;
        if (beforeColorTransform[slot])
            throw JxlDescriptor.Corrupt("A frame blends onto a reference frame saved before the colour transform.");
        if (frame.Color[0].Width != width || frame.Color[0].Height != height)
            throw JxlDescriptor.Corrupt("A frame blends onto a cropped reference frame.");
        return frame;
    }

    public bool HasDc(uint index) => index < dc.Length && dc[index] is not null;

    public IReadOnlyList<JxlPlane> GetDc(uint index) => HasDc(index) ? dc[index]!
        : throw JxlDescriptor.Corrupt("A frame uses a DC frame that was not decoded.");

    public void StoreReference(uint slot, JxlReferenceFrame frame, bool beforeColorTransform)
    {
        if (slot >= references.Length) throw JxlDescriptor.Corrupt("Invalid reference frame slot.");
        references[slot] = frame;
        this.beforeColorTransform[slot] = beforeColorTransform;
    }

    /// <summary>The bytes of the distinct planes saved after the colour transform, which the session charges as retained.</summary>
    public long RetainedBytes(JxlReferenceFrame? also = null)
    {
        var planes = new HashSet<JxlPlane>(ReferenceEqualityComparer.Instance);
        for (int slot = 0; slot < references.Length; slot++)
        {
            if (references[slot] is { } frame && !beforeColorTransform[slot]) Add(frame);
        }

        if (also is not null) Add(also);
        return planes.Sum(plane => plane.Bytes);

        void Add(JxlReferenceFrame frame)
        {
            foreach (JxlPlane plane in frame.Color) planes.Add(plane);
            foreach (JxlPlane plane in frame.Extra) planes.Add(plane);
        }
    }

    public void StoreDc(uint level, JxlReferenceFrame frame)
    {
        if (level is < 1 or > 4) throw JxlDescriptor.Corrupt("Invalid DC frame level.");
        dc[level - 1] = frame.Color;
    }
}
