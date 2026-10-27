using PictureSharp.Formats.Jxl.Integration;

namespace PictureSharp.Formats.Jxl;

/// <summary>
/// The JPEG XL format plugin. Version 0.4.0 detects signatures, identifies raw codestreams and box containers
/// (dimensions, colour description, alpha, orientation, animation) and decodes still lossy and lossless images; it
/// cannot decode animations, encode or read metadata yet.
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

    /// <summary>
    /// Identify reads the headers. Decode handles still VarDCT and Modular images (including patches, DC frames and
    /// recompressed JPEGs) and throws NotSupportedException for animations, layers, noise and other features outside
    /// that subset in 0.4.0.
    /// </summary>
    public IImageFormatDecoder Decoder => JxlDecoder.Instance;
}
