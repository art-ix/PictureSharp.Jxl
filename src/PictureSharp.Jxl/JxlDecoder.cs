using PictureSharp.Formats.Jxl.Codestream;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl;

/// <summary>
/// Identify reads the container and the codestream headers. Pixel decoding is not implemented in this version, so
/// Decode throws <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class JxlDecoder : IImageFormatDecoder
{
    public static readonly JxlDecoder Instance = new();
    private const string DecodeMessage = "PictureSharp.Jxl 0.2.0 reads JPEG XL headers (Identify) only; pixel decoding is not implemented.";

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
            HasAlpha = metadata.HasAlpha,
            BitsPerChannel = (int)metadata.BitDepth.BitsPerSample,
            Orientation = (ExifOrientation)metadata.Orientation,
            IsAnimated = animated,
            // Counting animation frames requires the entropy-coded table of contents of every frame.
            FrameCount = animated ? 0 : 1
        };
    }

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context) => throw new NotSupportedException(DecodeMessage);
}
