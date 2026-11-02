using PictureSharp.Formats.Jxl.Integration;

namespace PictureSharp.Formats.Jxl;

/// <summary>
/// The JPEG XL format plugin. It detects signatures, identifies raw codestreams and box containers (dimensions, colour
/// description, alpha, orientation, frame count), decodes lossy and lossless still images, layered images and
/// animations, encodes still images lossily (VarDCT, the default) or losslessly and reads and edits the ICC profile,
/// EXIF and XMP.
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
    /// Identify reads the headers, and for animations every frame header. Decode handles VarDCT and Modular images
    /// (including patches, DC frames, recompressed JPEGs, layers and animations) and throws NotSupportedException for
    /// noise, splines, float samples and other features outside that subset. The image carries the ICC profile, EXIF and
    /// XMP unless DecoderOptions.IgnoreMetadata is set.
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
