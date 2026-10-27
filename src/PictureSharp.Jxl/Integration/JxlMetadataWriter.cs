using System.Buffers.Binary;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>
/// The metadata the encoder and EditMetadata write: the colour encoding for the ICC profile or the colour
/// description, the codestream orientation and the Exif and xml boxes. The inverse of <see cref="JxlMetadataReader"/>.
/// </summary>
internal static class JxlMetadataWriter
{
    private static readonly JxlWhitePoint[] NamedWhitePoints = [JxlWhitePoint.D65, JxlWhitePoint.Dci, JxlWhitePoint.E];
    private static readonly JxlPrimaries[] NamedPrimaries = [JxlPrimaries.Srgb, JxlPrimaries.P3, JxlPrimaries.Bt2100];

    /// <summary>The colour space an ICC profile declares (header bytes 16–19): RGB, gray, or null for any other.</summary>
    public static JxlColorSpace? IccColorSpace(ReadOnlySpan<byte> icc) => icc.Length < 20 ? null : icc.Slice(16, 4) switch
    {
        var space when space.SequenceEqual("RGB "u8) => JxlColorSpace.Rgb,
        var space when space.SequenceEqual("GRAY"u8) => JxlColorSpace.Gray,
        _ => null,
    };

    /// <summary>The RGB or gray colour space of an ICC profile; other profiles throw NotSupportedException.</summary>
    public static JxlColorSpace RequireIccColorSpace(ReadOnlySpan<byte> icc, string message) =>
        IccColorSpace(icc) ?? throw new NotSupportedException(message);

    /// <summary>
    /// The enumerated colour encoding of the colour description: sRGB for none or <see cref="ImageMetadata.SrgbRenderingIntent"/>,
    /// else the chromaticities (named primaries and white points where they match) and the gamma, where 1/2.2 with
    /// primaries other than sRGB means the sRGB curve, as the decoder reports it. A gamma above 1, which JPEG XL cannot
    /// store, keeps the sRGB curve.
    /// </summary>
    public static JxlColorEncoding Encoding(ImageMetadata metadata, bool gray)
    {
        var encoding = new JxlColorEncoding { ColorSpace = gray ? JxlColorSpace.Gray : JxlColorSpace.Rgb };
        if (metadata.SrgbRenderingIntent is { } intent)
        {
            return encoding with
            {
                RenderingIntent = intent switch
                {
                    RenderingIntent.Perceptual => JxlRenderingIntent.Perceptual,
                    RenderingIntent.Saturation => JxlRenderingIntent.Saturation,
                    RenderingIntent.AbsoluteColorimetric => JxlRenderingIntent.Absolute,
                    _ => JxlRenderingIntent.Relative,
                },
            };
        }

        // The decoder describes sRGB-like curves as 1/2.2 only with other primaries, so that value means the sRGB curve
        // there and a pure gamma otherwise.
        bool otherPrimaries = metadata.Chromaticities is { } described && described != CieChromaticities.Srgb;
        if (metadata.Gamma is float gamma)
        {
            encoding = gamma switch
            {
                JxlMetadataReader.DescribedSrgbGamma when otherPrimaries => encoding,
                1.0f => encoding with { TransferFunction = JxlTransferFunction.Linear },
                JxlColorEncoding.DciGamma => encoding with { TransferFunction = JxlTransferFunction.Dci },
                > 0 and <= 1 when gamma * JxlColorEncoding.MaxInverseGamma >= 1 =>
                    encoding with { Gamma = (uint)Math.Round(gamma * (double)JxlColorEncoding.GammaScale) },
                _ => encoding,
            };
        }

        if (metadata.Chromaticities is not { } xy) return encoding;
        encoding = WithWhitePoint(encoding, new JxlXy(xy.WhiteX, xy.WhiteY));
        return gray ? encoding
            : WithPrimaries(encoding, new JxlPrimariesXy(new(xy.RedX, xy.RedY), new(xy.GreenX, xy.GreenY), new(xy.BlueX, xy.BlueY)));
    }

    // The named white point within 1e-4 of the coordinates, else a custom one.
    private static JxlColorEncoding WithWhitePoint(JxlColorEncoding encoding, JxlXy white)
    {
        foreach (JxlWhitePoint named in NamedWhitePoints)
        {
            if (Near(JxlColorEncoding.StandardXy(named)!.Value, white)) return encoding with { WhitePoint = named };
        }

        return encoding with { WhitePoint = JxlWhitePoint.Custom, CustomWhitePoint = JxlChromaticity.From(white) };
    }

    private static JxlColorEncoding WithPrimaries(JxlColorEncoding encoding, JxlPrimariesXy primaries)
    {
        foreach (JxlPrimaries named in NamedPrimaries)
        {
            JxlPrimariesXy standard = JxlColorEncoding.StandardXy(named)!.Value;
            if (Near(standard.Red, primaries.Red) && Near(standard.Green, primaries.Green) && Near(standard.Blue, primaries.Blue))
            {
                return encoding with { Primaries = named };
            }
        }

        return encoding with
        {
            Primaries = JxlPrimaries.Custom,
            CustomPrimaries = (JxlChromaticity.From(primaries.Red), JxlChromaticity.From(primaries.Green), JxlChromaticity.From(primaries.Blue)),
        };
    }

    private static bool Near(JxlXy a, JxlXy b) => Math.Abs(b.X - a.X) < 1e-4 && Math.Abs(b.Y - a.Y) < 1e-4;

    /// <summary>The codestream orientation: the EXIF orientation, 1 when it is unknown.</summary>
    public static uint Orientation(ImageMetadata metadata) =>
        metadata.Orientation is >= ExifOrientation.TopLeft and <= ExifOrientation.LeftBottom ? (uint)metadata.Orientation : 1;

    /// <summary>An Exif box: a four-byte offset of zero to the TIFF header, then the TIFF stream (ISO/IEC 18181-2).</summary>
    public static JxlBox ExifBox(byte[] tiff, bool compress)
    {
        byte[] content = new byte[tiff.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(content, 0);
        tiff.CopyTo(content, 4);
        return new JxlBox(JxlBoxType.Exif, content, compress);
    }

    public static JxlBox XmpBox(byte[] xmp, bool compress) => new(JxlBoxType.Xmp, xmp, compress);

    /// <summary>The Exif and xml boxes of the blocks that are present, in the order the encoder writes them.</summary>
    public static JxlBox[] DescriptiveBoxes(byte[]? exif, byte[]? xmp, bool compress) =>
        [.. exif is null ? [] : new[] { ExifBox(exif, compress) }, .. xmp is null ? [] : new[] { XmpBox(xmp, compress) }];
}
