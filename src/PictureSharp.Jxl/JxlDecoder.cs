namespace PictureSharp.Formats.Jxl;

internal sealed class JxlDecoder : IImageFormatDecoder
{
    public static readonly JxlDecoder Instance = new();
    private const string Message = "PictureSharp.Jxl 0.1.0 is a signature-detection scaffold; JPEG XL Identify and pixel decoding are not implemented.";

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) => throw new NotSupportedException(Message);

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context) => throw new NotSupportedException(Message);
}
