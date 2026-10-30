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

    private readonly Lazy<JxlIccTarget?>? icc;

    private JxlColorTarget(Lazy<JxlIccTarget?>? icc, bool srgb)
    {
        this.icc = icc;
        IsSrgb = srgb;
    }

    /// <summary>The image's ICC profile as an output colour space, when it has a usable one.</summary>
    public JxlIccTarget? Icc => icc?.Value;

    public bool IsSrgb { get; }

    /// <summary>
    /// The profile <paramref name="icc"/>, parsed when first needed: only images whose colour encoding is an ICC profile
    /// render to it, so most images (enumerated colour spaces, whose profile is synthesized) never parse it.
    /// </summary>
    public static JxlColorTarget Profile(byte[]? icc, bool gray) =>
        icc is null ? Original : new(new Lazy<JxlIccTarget?>(() => JxlIccTarget.TryCreate(icc, gray)), srgb: false);
}
