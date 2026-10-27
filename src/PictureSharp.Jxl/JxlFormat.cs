namespace PictureSharp.Formats.Jxl;

/// <summary>
/// The JPEG XL format plugin. Version 0.2.1 detects signatures and identifies raw codestreams and box containers
/// (dimensions, bit depth, alpha, orientation, animation); it cannot decode pixels, encode or read metadata yet.
/// </summary>
public sealed class JxlFormat : IImageFormatModule
{
    internal static readonly ImageFormat Descriptor = JxlDescriptor.Format;

    /// <inheritdoc />
    public ImageFormat Format => Descriptor;

    /// <inheritdoc />
    public int HeaderSize => 12;

    /// <summary>Recognizes a two-byte codestream or twelve-byte container signature; does not validate the image.</summary>
    public bool IsSupported(ReadOnlySpan<byte> header) => JxlSignature.IsSupported(header);

    /// <summary>Identify reads the headers; Decode, and therefore Image.Load, throws NotSupportedException in 0.2.1.</summary>
    public IImageFormatDecoder Decoder => JxlDecoder.Instance;
}
