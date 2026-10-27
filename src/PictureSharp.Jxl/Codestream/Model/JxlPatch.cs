namespace PictureSharp.Formats.Jxl.Codestream.Model;

internal enum JxlPatchBlendMode
{
    None = 0,
    Replace = 1,
    Add = 2,
    Mul = 3,
    BlendAbove = 4,
    BlendBelow = 5,
    AlphaWeightedAddAbove = 6,
    AlphaWeightedAddBelow = 7,
}

internal static class JxlPatchBlendModeExtensions
{
    extension(JxlPatchBlendMode mode)
    {
        /// <summary>Whether the mode weights the patch by an alpha channel.</summary>
        public bool UsesAlpha => mode is JxlPatchBlendMode.BlendAbove or JxlPatchBlendMode.BlendBelow
            or JxlPatchBlendMode.AlphaWeightedAddAbove or JxlPatchBlendMode.AlphaWeightedAddBelow;
    }
}

internal readonly record struct JxlPatchBlending(JxlPatchBlendMode Mode, int AlphaChannel, bool Clamp);

/// <summary>One placement of a reference-frame rectangle, with the blending of colour and of each extra channel.</summary>
internal sealed class JxlPatch(int reference, int referenceX, int referenceY, int width, int height, int x, int y, JxlPatchBlending[] blendings)
{
    public int Reference { get; } = reference;
    public int ReferenceX { get; } = referenceX;
    public int ReferenceY { get; } = referenceY;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int X { get; } = x;
    public int Y { get; } = y;
    public IReadOnlyList<JxlPatchBlending> Blendings { get; } = blendings.Snapshot();
}
