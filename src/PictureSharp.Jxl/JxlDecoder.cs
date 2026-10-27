using PictureSharp.Formats.Jxl.Codestream;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl;

/// <summary>
/// Identify reads the container and the codestream headers. Decode supports still single-frame VarDCT and Modular
/// images (see <see cref="JxlFrameDecoder"/>); other valid images throw <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class JxlDecoder : IImageFormatDecoder
{
    public static readonly JxlDecoder Instance = new();
    public static JxlHeaders ReadHeaders(ReadOnlyMemory<byte> data) => JxlHeaders.Read(JxlContainer.Parse(data).Codestream);

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        JxlHeaders headers = ReadHeaders(data);
        (uint width, uint height) = (headers.Size.Width, headers.Size.Height);
        if ((ulong)width * height > (ulong)options.MaxPixels)
        {
            throw JxlDescriptor.Corrupt($"Image size {width}x{height} exceeds DecoderOptions.MaxPixels.");
        }

        if (width > int.MaxValue)
        {
            throw JxlDescriptor.Unsupported($"image width {width} exceeds the largest supported width.");
        }

        JxlImageMetadata metadata = headers.Metadata;
        bool animated = metadata.Animation is not null;
        return new ImageInfo
        {
            Format = JxlDescriptor.Format,
            // Stored dimensions; the orientation is reported, not applied, as for JPEG with EXIF.
            Width = (int)width,
            Height = (int)height,
            Color = Color(metadata),
            // XYB is always lossy; without it the codestream can be lossless Modular or lossy, which the header does not say.
            Compression = metadata.XybEncoded ? ImageCompression.Lossy : ImageCompression.Unknown,
            Orientation = (ExifOrientation)metadata.Orientation,
            IsAnimated = animated,
            // Counting animation frames requires the entropy-coded table of contents of every frame.
            FrameCount = animated ? null : 1
        };
    }

    // The decoded model: XYB-encoded RGB is RGB; a Black extra channel makes CMYK. Every extra channel shares the
    // raster (with its own dim_shift) and counts. Extra channels have their own bit depth: the widest channel wins,
    // and any float channel makes the samples float.
    private static ImageColorInfo Color(JxlImageMetadata metadata)
    {
        JxlColorEncoding color = metadata.Color;
        JxlExtraChannelInfo? alpha = metadata.ExtraChannels.FirstOrDefault(channel => channel.Type == JxlExtraChannelType.Alpha);
        JxlBitDepth[] depths = [metadata.BitDepth, .. metadata.ExtraChannels.Select(channel => channel.BitDepth)];
        return new ImageColorInfo
        {
            Model = metadata.ExtraChannels.Any(channel => channel.Type == JxlExtraChannelType.Black) ? ImageColorModel.Cmyk
                : color.ColorSpace switch
                {
                    JxlColorSpace.Gray => ImageColorModel.Gray,
                    JxlColorSpace.Rgb => ImageColorModel.Rgb,
                    _ => ImageColorModel.Other,   // XYB declared as the image colour space, or unknown
                },
            ChannelCount = (color.ColorSpace == JxlColorSpace.Gray ? 1 : 3) + metadata.ExtraChannels.Length,
            BitsPerSample = (int)depths.Max(depth => depth.BitsPerSample),
            SampleFormat = depths.Any(depth => depth.FloatingPoint) ? ImageSampleFormat.Float : ImageSampleFormat.UnsignedInteger,
            AlphaMode = alpha is null ? ImageAlphaMode.None
                : alpha.AlphaAssociated ? ImageAlphaMode.Premultiplied : ImageAlphaMode.Straight,
            HasIccProfile = color.WantIcc,
            Cicp = Cicp(color),
        };
    }

    // The enumerated colour encoding as H.273 code points (RGB matrix, full range), when it maps exactly: not with an
    // ICC profile, custom primaries or white point, a gamma exponent or an unknown transfer. JPEG XL's P3 (11) is
    // CICP 12 (Display P3) with D65 and CICP 11 with the DCI white point.
    private static CicpColorDescription? Cicp(JxlColorEncoding color)
    {
        if (color.WantIcc || color.Gamma != 0 || color.ColorSpace is not (JxlColorSpace.Rgb or JxlColorSpace.Gray)
            || color.TransferFunction == JxlTransferFunction.Unknown)
        {
            return null;
        }

        int? primaries = (color.ColorSpace, color.Primaries, color.WhitePoint) switch
        {
            (JxlColorSpace.Gray, _, JxlWhitePoint.D65) => 2,
            (JxlColorSpace.Rgb, JxlPrimaries.Srgb, JxlWhitePoint.D65) => 1,
            (JxlColorSpace.Rgb, JxlPrimaries.Bt2100, JxlWhitePoint.D65) => 9,
            (JxlColorSpace.Rgb, JxlPrimaries.P3, JxlWhitePoint.D65) => 12,
            (JxlColorSpace.Rgb, JxlPrimaries.P3, JxlWhitePoint.Dci) => 11,
            _ => null,
        };
        return primaries is { } p ? new CicpColorDescription(p, (int)color.TransferFunction, 0, fullRange: true) : null;
    }

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        JxlCodestream codestream = JxlContainer.Parse(data).Codestream;
        JxlHeaders headers = JxlHeaders.Read(codestream);
        (uint width, uint height) = (headers.Size.Width, headers.Size.Height);
        if (width > int.MaxValue)
        {
            throw JxlDescriptor.Unsupported($"image width {width} exceeds the largest supported width.");
        }

        context.ReservePixels((int)width, (int)height);
        JxlDecodedImage decoded = JxlFrameDecoder.Decode(codestream, headers, context.CancellationToken);
        JxlPlane? alpha = decoded.Alpha;
        var image = alpha is not null ? new Image(decoded.Width, decoded.Height) : new Image(decoded.Width, decoded.Height, new Rgba32(0, 0, 0, 255));
        try
        {
            Span<Rgba32> pixels = image.GetPixelMemory().Span;
            JxlPlane red = decoded.Color[0];
            JxlPlane green = decoded.Color[decoded.Color.Length == 3 ? 1 : 0];
            JxlPlane blue = decoded.Color[^1];
            // Grayscale is written as one channel (dithered as channel 0, alpha as channel 1) and replicated.
            bool gray = headers.Metadata.Color.ColorSpace == JxlColorSpace.Gray;
            static byte Gray(float value, int x, int y) => JxlDither.ToByte(value, x, y, 0);
            for (int y = 0; y < decoded.Height; y++)
            {
                Span<float> r = red.Row(y);
                Span<float> g = green.Row(y);
                Span<float> b = blue.Row(y);
                Span<float> a = alpha is null ? default : alpha.Row(y);
                Span<Rgba32> row = pixels.Slice(y * decoded.Width, decoded.Width);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = gray
                        ? new Rgba32(Gray(r[x], x, y), Gray(r[x], x, y), Gray(r[x], x, y), alpha is null ? (byte)255 : JxlDither.ToByte(a[x], x, y, 1))
                        : new Rgba32(JxlDither.ToByte(r[x], x, y, 0), JxlDither.ToByte(g[x], x, y, 1), JxlDither.ToByte(b[x], x, y, 2),
                            alpha is null ? (byte)255 : JxlDither.ToByte(a[x], x, y, 3));
                }
            }

            // Stored orientation, as for JPEG: ApplyExifOrientation (or AutoOrient) applies it.
            var orientation = (ExifOrientation)headers.Metadata.Orientation;
            if (orientation != ExifOrientation.TopLeft)
            {
                image.Metadata.Orientation = orientation;
            }

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }
}
