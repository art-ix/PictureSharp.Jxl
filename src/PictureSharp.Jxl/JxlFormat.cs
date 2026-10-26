namespace PictureSharp.Formats.Jxl;

/// <summary>A JPEG XL signature-detection scaffold. Version 0.1.0 cannot identify, decode or encode images.</summary>
public sealed class JxlFormat : IImageFormatModule
{
    internal static readonly ImageFormat Descriptor = ImageFormat.Create("JPEG XL", "image/jxl", ".jxl");

    /// <inheritdoc />
    public ImageFormat Format => Descriptor;

    /// <inheritdoc />
    public int HeaderSize => 12;

    /// <summary>Recognizes a two-byte codestream or twelve-byte container signature; does not validate the image.</summary>
    public bool IsSupported(ReadOnlySpan<byte> header) => JxlSignature.IsSupported(header);

    /// <summary>Registration placeholder: Identify and Decode always throw NotSupportedException.</summary>
    public IImageFormatDecoder Decoder => JxlDecoder.Instance;
}
