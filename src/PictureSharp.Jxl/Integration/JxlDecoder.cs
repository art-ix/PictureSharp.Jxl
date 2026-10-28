using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>The stateless PictureSharp SPI adapter. Every decode owns its own session.</summary>
internal sealed class JxlDecoder : IImageFormatDecoder
{
    public static readonly JxlDecoder Instance = new();

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
        JxlImageInfoMapper.Map(JxlHeadersReader.Read(JxlContainer.Parse(data).Codestream), options);

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        context.ThrowIfCancellationRequested();
        DecoderOptions options = context.Options;
        JxlContainer container = JxlContainer.Parse(data, includeMetadata: !options.IgnoreMetadata, context.CancellationToken);
        JxlCodestream codestream = container.Codestream;
        JxlHeaders headers = JxlHeadersReader.Read(codestream);
        if (headers.Size.Width > int.MaxValue || headers.Size.Height > int.MaxValue)
            throw JxlDescriptor.Unsupported("image dimensions exceed the largest supported size.");

        JxlMetadataReader.EnsureConvertible(headers.Metadata.Color, options);
        PixelType pixelType = options.PreserveBitDepth && (headers.Metadata.BitDepth.BitsPerSample > 8
            || headers.Metadata.ExtraChannels.Any(channel => channel.Type == JxlExtraChannelType.Alpha && channel.BitDepth.BitsPerSample > 8))
            ? PixelType.Rgba64 : PixelType.Rgba32;
        context.ReservePixels((int)headers.Size.Width, (int)headers.Size.Height, pixelType);
        // The ICC profile is part of the codestream: with IgnoreMetadata it is decoded (as libjxl does) within libjxl's
        // limit, but neither charged nor reported. ToSrgb renders XYB frames as sRGB in floating point, so they are
        // quantized once and get no colour description for PictureSharp to convert again.
        var session = options.IgnoreMetadata
            ? new JxlDecodeSession(codestream, headers, context.CancellationToken)
            : new JxlDecodeSession(codestream, headers, context.CancellationToken, options.MaxMetadataBytes, context.ReserveMetadata,
                renderSrgb: options.ColorConversion == ColorConversionMode.ToSrgb);
        JxlDecodedImage decoded = session.Decode();
        Image image = pixelType == PixelType.Rgba64
            ? JxlPixelWriter.WriteWide(decoded, context.CancellationToken)
            : JxlPixelWriter.Write(decoded, context.CancellationToken);
        try
        {
            if (!options.IgnoreMetadata)
            {
                // The profile only when the samples are in it: XYB with an unsupported profile, or with ToSrgb, is
                // rendered as sRGB.
                JxlMetadataReader.ApplyColour(image.Metadata, decoded.ColorEncoding, session.IccProfile, options);
                JxlMetadataReader.ApplyDescriptive(image.Metadata, container.MetadataBoxes, context);
            }

            // Stored orientation, as for JPEG: ApplyExifOrientation (or AutoOrient) applies it.
            JxlMetadataReader.ApplyOrientation(image.Metadata, headers.Metadata.Orientation);
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return image;
    }
}
