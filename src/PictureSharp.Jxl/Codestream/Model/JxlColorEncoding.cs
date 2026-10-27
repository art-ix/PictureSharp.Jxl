namespace PictureSharp.Formats.Jxl.Codestream.Model;

// Enumerator values follow libjxl v0.12.0 lib/jxl/cms/color_encoding_cms.h (CICP code points where applicable).
internal enum JxlColorSpace : uint { Rgb = 0, Gray = 1, Xyb = 2, Unknown = 3 }

internal enum JxlWhitePoint : uint { D65 = 1, Custom = 2, E = 10, Dci = 11 }

internal enum JxlPrimaries : uint { Srgb = 1, Custom = 2, Bt2100 = 9, P3 = 11 }

internal enum JxlTransferFunction : uint { Bt709 = 1, Unknown = 2, Linear = 8, Srgb = 13, Pq = 16, Dci = 17, Hlg = 18 }

internal enum JxlRenderingIntent : uint { Perceptual = 0, Relative = 1, Saturation = 2, Absolute = 3 }

/// <summary>A CIE xy chromaticity, stored as the codestream's integer millionths.</summary>
internal readonly record struct JxlChromaticity(int X, int Y)
{
    public double XValue => X / 1e6;

    public double YValue => Y / 1e6;
}

/// <summary>
/// The colour encoding of the image metadata: either "an ICC profile follows" or an enumerated description.
/// Ported from libjxl v0.12.0 lib/jxl/color_encoding_internal.cc (ColorEncoding, Customxy and
/// CustomTransferFunction VisitFields), BSD-3-Clause. Fields that do not apply to the colour space keep the
/// implicit values libjxl assigns (D65 and gamma 1/3 for XYB).
/// </summary>
internal sealed record JxlColorEncoding
{
    /// <summary>The scale of <see cref="Gamma"/>: the stored value is gamma × 10^7, in (0, 1].</summary>
    public const uint GammaScale = 10_000_000;

    public const uint MaxInverseGamma = 8192;

    public static JxlColorEncoding Srgb { get; } = new();

    public bool WantIcc { get; init; }

    public JxlColorSpace ColorSpace { get; init; } = JxlColorSpace.Rgb;

    public JxlWhitePoint WhitePoint { get; init; } = JxlWhitePoint.D65;

    public JxlChromaticity CustomWhitePoint { get; init; }

    public JxlPrimaries Primaries { get; init; } = JxlPrimaries.Srgb;

    /// <summary>Red, green and blue chromaticities when <see cref="Primaries"/> is custom.</summary>
    public (JxlChromaticity Red, JxlChromaticity Green, JxlChromaticity Blue) CustomPrimaries { get; init; }

    /// <summary>Gamma × <see cref="GammaScale"/> when the transfer function is a pure gamma, otherwise 0.</summary>
    public uint Gamma { get; init; }

    /// <summary>The enumerated transfer function; meaningful when <see cref="Gamma"/> is 0.</summary>
    public JxlTransferFunction TransferFunction { get; init; } = JxlTransferFunction.Srgb;

    public JxlRenderingIntent RenderingIntent { get; init; } = JxlRenderingIntent.Relative;

    /// <summary>Whether primaries apply: not for grayscale and XYB.</summary>
    public bool HasPrimaries => ColorSpace is not (JxlColorSpace.Gray or JxlColorSpace.Xyb);

}
