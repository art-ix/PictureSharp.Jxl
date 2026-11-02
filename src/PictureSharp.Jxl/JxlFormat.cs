using PictureSharp.Formats.Jxl.Integration;

namespace PictureSharp.Formats.Jxl;

/// <summary>
/// The JPEG XL format plugin. It detects signatures, identifies raw codestreams and box containers (dimensions, colour
/// description, alpha, orientation, frame count), decodes lossy and lossless still images, layered images and
/// animations (also frame by frame), encodes still images and animations (also frame by frame) lossily (VarDCT, the
/// default) or losslessly and reads and edits the ICC profile, EXIF and XMP.
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
    /// (including patches, DC frames, recompressed JPEGs, layers, animations, photon noise and splines) and throws
    /// NotSupportedException for float samples and other features outside that subset. The image carries the ICC profile, EXIF and
    /// XMP unless DecoderOptions.IgnoreMetadata is set.
    /// </summary>
    public IImageFormatDecoder Decoder => JxlDecoder.Instance;

    /// <summary>
    /// Reads frame by frame for <c>Image.OpenFrameReaderAsync</c>: the displayed frames, durations, loop count and
    /// metadata of <see cref="Decoder"/>, one canvas at a time, so <c>MaxPixels</c> needs to allow only one canvas. The
    /// input is buffered in memory within <c>MaxInputBytes</c>.
    /// </summary>
    public IImageFormatFrameDecoder FrameDecoder => JxlFrameReader.Instance;

    /// <summary>
    /// Writes still images and animations lossily or losslessly, with their metadata, as described on
    /// <see cref="JxlEncoderOptions"/>. An image with several frames becomes an animation: durations in milliseconds,
    /// <c>LoopCount</c> as the number of plays, each frame cropped to what changed since the previous one.
    /// </summary>
    public IImageFormatEncoder Encoder => JxlEncoder.Instance;

    /// <summary>
    /// Writes animations frame by frame for <c>Image.CreateFrameWriter</c>, as <see cref="Encoder"/> does, holding one
    /// encoded frame and the previous canvas. The output is written forward only, so it needs no seekable stream. Later
    /// frames are not known in advance, so the frames are stored as RGB (gray only with a gray ICC profile) with an alpha
    /// channel; the decoded pixels are the same.
    /// </summary>
    public IImageFormatFrameEncoder FrameEncoder => JxlFrameWriter.Instance;

    /// <summary>
    /// Reads the ICC profile (or the enumerated colour as a colour description), EXIF and XMP, including
    /// Brotli-compressed boxes, and the codestream orientation without decoding frames. <c>Image.EditMetadata</c>
    /// rewrites the headers and metadata boxes and copies the frames; it rejects resolutions and recompressed JPEGs.
    /// </summary>
    public IImageFormatMetadataCodec MetadataCodec => JxlMetadataCodec.Instance;
}
