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
    private const float DescribedSrgbGamma = 0.45455f;

    /// <summary>The colour space an ICC profile declares (header bytes 16–19): RGB, gray, or null for any other.</summary>
    public static JxlColorSpace? IccColorSpace(ReadOnlySpan<byte> icc) => icc.Length < 20 ? null : icc.Slice(16, 4) switch
    {
        var space when space.SequenceEqual("RGB "u8) => JxlColorSpace.Rgb,
        var space when space.SequenceEqual("GRAY"u8) => JxlColorSpace.Gray,
        _ => null,
    };

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
                DescribedSrgbGamma when otherPrimaries => encoding,
                1.0f => encoding with { TransferFunction = JxlTransferFunction.Linear },
                1.0f / 2.6f => encoding with { TransferFunction = JxlTransferFunction.Dci },
                > 0 and <= 1 when gamma * JxlColorEncoding.MaxInverseGamma >= 1 =>
                    encoding with { Gamma = (uint)Math.Round(gamma * (double)JxlColorEncoding.GammaScale) },
                _ => encoding,
            };
        }

        if (metadata.Chromaticities is not { } xy) return encoding;
        encoding = (xy.WhiteX, xy.WhiteY) switch
        {
            var w when Near(w, 0.3127, 0.3290) => encoding,
            var w when Near(w, 0.314, 0.351) => encoding with { WhitePoint = JxlWhitePoint.Dci },
            var w when Near(w, 1.0 / 3, 1.0 / 3) => encoding with { WhitePoint = JxlWhitePoint.E },
            _ => encoding with { WhitePoint = JxlWhitePoint.Custom, CustomWhitePoint = Point(xy.WhiteX, xy.WhiteY) },
        };
        if (gray) return encoding;
        return Primaries(xy, 0.64, 0.33, 0.30, 0.60, 0.15, 0.06) ? encoding
            : Primaries(xy, 0.680, 0.320, 0.265, 0.690, 0.150, 0.060) ? encoding with { Primaries = JxlPrimaries.P3 }
            : Primaries(xy, 0.708, 0.292, 0.170, 0.797, 0.131, 0.046) ? encoding with { Primaries = JxlPrimaries.Bt2100 }
            : encoding with
            {
                Primaries = JxlPrimaries.Custom,
                CustomPrimaries = (Point(xy.RedX, xy.RedY), Point(xy.GreenX, xy.GreenY), Point(xy.BlueX, xy.BlueY)),
            };
    }

    private static bool Near((float X, float Y) point, double x, double y) => Math.Abs(point.X - x) < 1e-4 && Math.Abs(point.Y - y) < 1e-4;

    private static bool Primaries(CieChromaticities xy, double rx, double ry, double gx, double gy, double bx, double by) =>
        Near((xy.RedX, xy.RedY), rx, ry) && Near((xy.GreenX, xy.GreenY), gx, gy) && Near((xy.BlueX, xy.BlueY), bx, by);

    private static JxlChromaticity Point(float x, float y) => new((int)Math.Round(x * 1e6), (int)Math.Round(y * 1e6));

    /// <summary>The codestream orientation: the EXIF orientation, 1 when it is unknown.</summary>
    public static uint Orientation(ImageMetadata metadata) =>
        metadata.Orientation is >= ExifOrientation.TopLeft and <= ExifOrientation.LeftBottom ? (uint)metadata.Orientation : 1;

    /// <summary>An Exif box: a four-byte offset of zero to the TIFF header, then the TIFF stream (ISO/IEC 18181-2).</summary>
    public static JxlBox ExifBox(byte[] tiff, bool compress)
    {
        byte[] content = new byte[tiff.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(content, 0);
        tiff.CopyTo(content, 4);
        return new JxlBox("Exif", content, compress);
    }

    public static JxlBox XmpBox(byte[] xmp, bool compress) => new("xml ", xmp, compress);
}
