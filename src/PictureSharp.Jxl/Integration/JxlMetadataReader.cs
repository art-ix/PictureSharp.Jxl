using System.Buffers.Binary;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>
/// The metadata Decode and ReadMetadata report: the ICC profile or the enumerated colour as a colour description, the
/// first Exif and xml boxes (decompressed from brob) and the codestream orientation, which overrides the EXIF tag.
/// </summary>
internal static class JxlMetadataReader
{
    /// <summary>
    /// The ICC profile when the samples are in it (<see cref="JxlColorEncoding.WantIcc"/>), or the enumerated colour
    /// as chromaticities and gamma. sRGB, the default, gets none, and PQ/HLG cannot be described.
    /// </summary>
    public static void ApplyColour(ImageMetadata metadata, JxlColorEncoding encoding, byte[]? icc, DecoderOptions options)
    {
        if (encoding.WantIcc)
        {
            if (icc is not null)
            {
                metadata.SetBlock(MetadataKinds.Icc, icc);
            }

            return;
        }

        if (encoding.Gamma == 0 && encoding.TransferFunction is JxlTransferFunction.Pq or JxlTransferFunction.Hlg)
        {
            if (options.ColorConversion != ColorConversionMode.None)
            {
                throw new NotSupportedException("JPEG XL images with PQ or HLG transfer functions cannot be converted to sRGB.");
            }

            return;
        }

        if (encoding.ColorSpace is not (JxlColorSpace.Rgb or JxlColorSpace.Gray))
        {
            return;
        }

        bool srgbPrimaries = encoding.ColorSpace == JxlColorSpace.Gray
            || encoding.Primaries == JxlPrimaries.Srgb && encoding.WhitePoint == JxlWhitePoint.D65;
        float? gamma = encoding.Gamma != 0 ? encoding.Gamma / (float)JxlColorEncoding.GammaScale : encoding.TransferFunction switch
        {
            JxlTransferFunction.Linear => 1.0f,
            JxlTransferFunction.Dci => 1.0f / 2.6f,
            // sRGB-family curves as 1/2.2, as for PNG and HEIF; sRGB itself needs no description.
            JxlTransferFunction.Srgb or JxlTransferFunction.Bt709 => srgbPrimaries ? null : 0.45455f,
            _ => null,
        };
        if (!srgbPrimaries)
        {
            metadata.Chromaticities = Chromaticities(encoding);
        }

        if (gamma is not null)
        {
            metadata.Gamma = gamma;
        }
    }

    private static CieChromaticities Chromaticities(JxlColorEncoding encoding)
    {
        (double wx, double wy) = encoding.WhitePoint switch
        {
            JxlWhitePoint.D65 => (0.3127, 0.3290),
            JxlWhitePoint.Dci => (0.314, 0.351),
            JxlWhitePoint.E => (1.0 / 3, 1.0 / 3),
            _ => (encoding.CustomWhitePoint.XValue, encoding.CustomWhitePoint.YValue),
        };
        double[] p = encoding.Primaries switch
        {
            JxlPrimaries.Srgb => [0.64, 0.33, 0.30, 0.60, 0.15, 0.06],
            JxlPrimaries.Bt2100 => [0.708, 0.292, 0.170, 0.797, 0.131, 0.046],
            JxlPrimaries.P3 => [0.680, 0.320, 0.265, 0.690, 0.150, 0.060],
            _ =>
            [
                encoding.CustomPrimaries.Red.XValue, encoding.CustomPrimaries.Red.YValue,
                encoding.CustomPrimaries.Green.XValue, encoding.CustomPrimaries.Green.YValue,
                encoding.CustomPrimaries.Blue.XValue, encoding.CustomPrimaries.Blue.YValue,
            ],
        };
        try
        {
            return new CieChromaticities((float)wx, (float)wy, (float)p[0], (float)p[1], (float)p[2], (float)p[3], (float)p[4], (float)p[5]);
        }
        catch (ArgumentException exception)
        {
            throw new ImageFormatException("The JPEG XL colour encoding has invalid chromaticities.", exception);
        }
    }

    /// <summary>The first Exif and xml boxes. Malformed Exif boxes and corrupt Brotli streams are skipped.</summary>
    public static void ApplyDescriptive(ImageMetadata metadata, IReadOnlyList<JxlMetadataBox> boxes, ImageDecodeContext context)
    {
        bool exif = false;
        bool xmp = false;
        foreach (JxlMetadataBox box in boxes)
        {
            bool isExif = box.Type == "Exif";
            if (isExif ? exif : xmp)
            {
                continue;
            }

            if (box.Read(context.Options.MaxMetadataBytes, context.ReserveMetadata, context.CancellationToken) is not { } content)
            {
                continue;
            }

            if (!isExif)
            {
                metadata.SetBlock(MetadataKinds.Xmp, content.Span);
                xmp = true;
            }
            else if (TryGetTiff(content.Span, out ReadOnlySpan<byte> tiff))
            {
                metadata.SetBlock(MetadataKinds.Exif, tiff);
                exif = true;
            }
        }
    }

    /// <summary>
    /// The codestream orientation: it is authoritative, so an EXIF orientation tag is replaced (ISO/IEC 18181-2,
    /// Exif box). PictureSharp applies it on request; EXIF that cannot be edited is dropped.
    /// </summary>
    public static void ApplyOrientation(ImageMetadata metadata, uint orientationValue)
    {
        var orientation = (ExifOrientation)orientationValue;
        if (orientation == ExifOrientation.TopLeft)
        {
            if (metadata.GetBlockLength(MetadataKinds.Exif) > 0 && !metadata.TrySetOrientation(orientation) && metadata.Orientation != ExifOrientation.Unknown)
            {
                metadata.RemoveBlocks(MetadataKinds.Exif);
            }

            return;
        }

        try
        {
            metadata.Orientation = orientation;
        }
        catch (ImageFormatException)
        {
            metadata.RemoveBlocks(MetadataKinds.Exif);
            metadata.Orientation = orientation;
        }
    }

    // ISO/IEC 18181-2: a four-byte offset to the TIFF header precedes the Exif payload, as in HEIF.
    private static bool TryGetTiff(ReadOnlySpan<byte> exif, out ReadOnlySpan<byte> tiff)
    {
        tiff = default;
        if (exif.Length < 4)
        {
            return false;
        }

        uint offset = BinaryPrimitives.ReadUInt32BigEndian(exif);
        if (offset > (uint)(exif.Length - 4))
        {
            return false;
        }

        tiff = exif[(4 + (int)offset)..];
        return tiff.StartsWith("II*\0"u8) || tiff.StartsWith("MM\0*"u8);
    }
}
