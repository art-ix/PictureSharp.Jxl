using PictureSharp.Formats.Jxl.Integration;

namespace PictureSharp.Formats.Jxl;

/// <summary>
/// The JPEG XL format plugin. Version 0.7.0 detects signatures, identifies raw codestreams and box containers
/// (dimensions, colour description, alpha, orientation, animation), decodes still lossy and lossless images, encodes
/// them lossily (VarDCT, the default) or losslessly and reads and edits the ICC profile, EXIF and XMP; it cannot
/// decode animations yet.
/// </summary>
public sealed class JxlFormat : IImageFormatModule
{
    internal static readonly ImageFormat Descriptor = JxlDescriptor.Format;

    /// <inheritdoc />
    public ImageFormat Format => Descriptor;

    /// <inheritdoc />
    public int HeaderSize => JxlDescriptor.ContainerSignature.Length;

    /// <summary>Recognizes a two-byte codestream or twelve-byte container signature; does not validate the image.</summary>
    public bool IsSupported(ReadOnlySpan<byte> header) =>
        header.StartsWith(JxlDescriptor.CodestreamSignature) || header.StartsWith(JxlDescriptor.ContainerSignature);

    /// <summary>
    /// Identify reads the headers. Decode handles still VarDCT and Modular images (including patches, DC frames and
    /// recompressed JPEGs) and throws NotSupportedException for animations, layers, noise and other features outside
    /// that subset in 0.7.0. The image carries the ICC profile, EXIF and XMP unless DecoderOptions.IgnoreMetadata is set.
    /// </summary>
    public IImageFormatDecoder Decoder => JxlDecoder.Instance;

    /// <summary>Writes still images lossily or losslessly, with their metadata, as described on <see cref="JxlEncoderOptions"/>.</summary>
    public IImageFormatEncoder Encoder => JxlEncoder.Instance;

    /// <summary>
    /// Reads the ICC profile (or the enumerated colour as a colour description), EXIF and XMP, including
    /// Brotli-compressed boxes, and the codestream orientation without decoding frames. <c>Image.EditMetadata</c>
    /// rewrites the headers and metadata boxes and copies the frames; it rejects resolutions and recompressed JPEGs.
    /// </summary>
    public IImageFormatMetadataCodec MetadataCodec => JxlMetadataCodec.Instance;
}
