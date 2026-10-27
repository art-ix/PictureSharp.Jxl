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
    /// <summary>The gamma that describes sRGB-family curves with other primaries, as for PNG and HEIF.</summary>
    public const float DescribedSrgbGamma = 0.45455f;

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

        EnsureConvertible(encoding, options);
        if (encoding.IsHdr || encoding.ColorSpace is not (JxlColorSpace.Rgb or JxlColorSpace.Gray))
        {
            return;
        }

        bool srgbPrimaries = encoding.ColorSpace == JxlColorSpace.Gray
            || encoding.Primaries == JxlPrimaries.Srgb && encoding.WhitePoint == JxlWhitePoint.D65;
        float? gamma = encoding.Gamma != 0 ? encoding.Gamma / (float)JxlColorEncoding.GammaScale : encoding.TransferFunction switch
        {
            JxlTransferFunction.Linear => 1.0f,
            JxlTransferFunction.Dci => JxlColorEncoding.DciGamma,
            // sRGB itself needs no description.
            JxlTransferFunction.Srgb or JxlTransferFunction.Bt709 => srgbPrimaries ? null : DescribedSrgbGamma,
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

    /// <summary>The colour, the descriptive blocks and the orientation, in the order that lets the orientation win.</summary>
    public static void ApplyAll(ImageMetadata metadata, JxlColorEncoding encoding, byte[]? icc, IReadOnlyList<JxlMetadataBox> boxes,
        uint orientation, ImageDecodeContext context)
    {
        ApplyColour(metadata, encoding, icc, context.Options);
        ApplyDescriptive(metadata, boxes, context);
        ApplyOrientation(metadata, orientation);
    }

    /// <summary>Rejects a conversion to sRGB of PQ or HLG (HDR) samples, which needs tone mapping.</summary>
    public static void EnsureConvertible(JxlColorEncoding encoding, DecoderOptions options)
    {
        if (options.ColorConversion != ColorConversionMode.None && !encoding.WantIcc && encoding.IsHdr)
        {
            throw new NotSupportedException("JPEG XL images with PQ or HLG transfer functions cannot be converted to sRGB.");
        }
    }

    private static CieChromaticities Chromaticities(JxlColorEncoding encoding)
    {
        (JxlXy white, (JxlXy red, JxlXy green, JxlXy blue)) = (encoding.WhitePointXy, encoding.PrimariesXy);
        try
        {
            return new CieChromaticities((float)white.X, (float)white.Y, (float)red.X, (float)red.Y, (float)green.X, (float)green.Y,
                (float)blue.X, (float)blue.Y);
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
            bool isExif = box.Type == JxlBoxType.Exif;
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
