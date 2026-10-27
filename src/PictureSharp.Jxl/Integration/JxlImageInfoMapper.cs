using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Integration;

internal static class JxlImageInfoMapper
{
    public static ImageInfo Map(JxlHeaders headers, DecoderOptions options)
    {
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
            ChannelCount = (color.ColorSpace == JxlColorSpace.Gray ? 1 : 3) + metadata.ExtraChannels.Count,
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
}
