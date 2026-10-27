namespace PictureSharp.Formats.Jxl.Codestream.Color;

/// <summary>
/// The colour space XYB frames are rendered to: the image's own (its ICC profile through <see cref="Icc"/> when that
/// is usable) or sRGB, which libjxl renders for an sRGB output encoding without a colour management system.
/// </summary>
internal sealed class JxlColorTarget
{
    /// <summary>XYB to sRGB in floating point: the samples are quantized once (djxl --color_space=sRGB).</summary>
    public static readonly JxlColorTarget Srgb = new(null, srgb: true);

    /// <summary>The image's colour space.</summary>
    public static readonly JxlColorTarget Original = new(null, srgb: false);

    private JxlColorTarget(JxlIccTarget? icc, bool srgb)
    {
        Icc = icc;
        IsSrgb = srgb;
    }

    /// <summary>The image's ICC profile as an output colour space, when it has a usable one.</summary>
    public JxlIccTarget? Icc { get; }

    public bool IsSrgb { get; }

    public static JxlColorTarget Profile(JxlIccTarget? icc) => icc is null ? Original : new(icc, srgb: false);
}
