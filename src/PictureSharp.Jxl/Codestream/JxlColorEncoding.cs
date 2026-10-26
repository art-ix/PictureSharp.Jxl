using static PictureSharp.Formats.Jxl.Codestream.JxlU32;

namespace PictureSharp.Formats.Jxl.Codestream;

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

    public static JxlChromaticity Read(ref JxlBitReader reader) => new(Coordinate(ref reader), Coordinate(ref reader));

    private static int Coordinate(ref JxlBitReader reader)
    {
        uint packed = JxlFields.U32(ref reader, Bits32(19), BitsOffset(19, 524288), BitsOffset(20, 1048576), BitsOffset(21, 2097152));
        return (int)(packed >> 1) ^ -(int)(packed & 1);
    }
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

    private const uint MaxInverseGamma = 8192;

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

    public static JxlColorEncoding Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return Srgb;
        }

        bool wantIcc = reader.ReadBool();
        var colorSpace = (JxlColorSpace)JxlFields.Enum(ref reader, JxlFields.Bits(0, 1, 2, 3), "colour space");
        var encoding = new JxlColorEncoding { WantIcc = wantIcc, ColorSpace = colorSpace };
        if (wantIcc)
        {
            return encoding;
        }

        if (colorSpace != JxlColorSpace.Xyb)
        {
            var whitePoint = (JxlWhitePoint)JxlFields.Enum(ref reader, JxlFields.Bits(1, 2, 10, 11), "white point");
            encoding = encoding with { WhitePoint = whitePoint };
            if (whitePoint == JxlWhitePoint.Custom)
            {
                encoding = encoding with { CustomWhitePoint = Valid(JxlChromaticity.Read(ref reader), "white point") };
            }
        }

        if (encoding.HasPrimaries)
        {
            var primaries = (JxlPrimaries)JxlFields.Enum(ref reader, JxlFields.Bits(1, 2, 9, 11), "primaries");
            encoding = encoding with { Primaries = primaries };
            if (primaries == JxlPrimaries.Custom)
            {
                encoding = encoding with
                {
                    CustomPrimaries = (Valid(JxlChromaticity.Read(ref reader), "red primary"),
                        Valid(JxlChromaticity.Read(ref reader), "green primary"),
                        Valid(JxlChromaticity.Read(ref reader), "blue primary"))
                };
            }
        }

        if (colorSpace == JxlColorSpace.Xyb)
        {
            // Implicit: the XYB transfer is a cube root (rounded like libjxl SetGamma).
            encoding = encoding with { Gamma = (uint)Math.Round(GammaScale / 3.0) };
        }
        else if (reader.ReadBool())
        {
            uint gamma = reader.ReadBits(24);
            if (gamma > GammaScale || (ulong)gamma * MaxInverseGamma < GammaScale)
            {
                throw JxlDescriptor.Corrupt($"Invalid gamma {gamma}.");
            }

            encoding = encoding with { Gamma = gamma };
        }
        else
        {
            encoding = encoding with
            {
                TransferFunction = (JxlTransferFunction)JxlFields.Enum(ref reader, JxlFields.Bits(1, 2, 8, 13, 16, 17, 18), "transfer function")
            };
        }

        encoding = encoding with
        {
            RenderingIntent = (JxlRenderingIntent)JxlFields.Enum(ref reader, JxlFields.Bits(0, 1, 2, 3), "rendering intent")
        };
        if (colorSpace == JxlColorSpace.Unknown || (encoding.Gamma == 0 && encoding.TransferFunction == JxlTransferFunction.Unknown))
        {
            throw JxlDescriptor.Corrupt("Colour encoding without an ICC profile has an unknown colour space or transfer function.");
        }

        return encoding;
    }

    // libjxl rejects zero coordinates when it builds the profile from custom chromaticities.
    private static JxlChromaticity Valid(JxlChromaticity xy, string name) => xy.X != 0 && xy.Y != 0
        ? xy
        : throw JxlDescriptor.Corrupt($"Invalid {name} chromaticity.");
}
