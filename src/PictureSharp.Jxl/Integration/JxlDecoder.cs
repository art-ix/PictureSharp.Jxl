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
        JxlCodestream codestream = JxlContainer.Parse(data, cancellationToken: context.CancellationToken).Codestream;
        JxlHeaders headers = JxlHeadersReader.Read(codestream);
        if (headers.Size.Width > int.MaxValue || headers.Size.Height > int.MaxValue)
            throw JxlDescriptor.Unsupported("image dimensions exceed the largest supported size.");

        context.ReservePixels((int)headers.Size.Width, (int)headers.Size.Height);
        JxlDecodedImage decoded = new JxlDecodeSession(codestream, headers, context.CancellationToken).Decode();
        return JxlPixelWriter.Write(decoded, headers.Metadata.Orientation, context.CancellationToken);
    }
}
