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

        context.ReservePixels((int)headers.Size.Width, (int)headers.Size.Height);
        // The ICC profile is part of the codestream: with IgnoreMetadata it is decoded (as libjxl does) within libjxl's
        // limit, but neither charged nor reported.
        var session = options.IgnoreMetadata
            ? new JxlDecodeSession(codestream, headers, context.CancellationToken)
            : new JxlDecodeSession(codestream, headers, context.CancellationToken, options.MaxMetadataBytes, context.ReserveMetadata);
        JxlDecodedImage decoded = session.Decode();
        Image image = JxlPixelWriter.Write(decoded, context.CancellationToken);
        try
        {
            if (!options.IgnoreMetadata)
            {
                // The profile only when the samples are in it: XYB with an unsupported profile is rendered as sRGB.
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
