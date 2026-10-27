using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlColorEncoding;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlColorEncoding using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlColorEncodingReader
{
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
                encoding = encoding with { CustomWhitePoint = Valid(JxlChromaticityReader.Read(ref reader), "white point") };
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
                    CustomPrimaries = (Valid(JxlChromaticityReader.Read(ref reader), "red primary"),
                        Valid(JxlChromaticityReader.Read(ref reader), "green primary"),
                        Valid(JxlChromaticityReader.Read(ref reader), "blue primary"))
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

    private static JxlChromaticity Valid(JxlChromaticity xy, string name) => xy.X != 0 && xy.Y != 0
        ? xy
        : throw JxlDescriptor.Corrupt($"Invalid {name} chromaticity.");
}
