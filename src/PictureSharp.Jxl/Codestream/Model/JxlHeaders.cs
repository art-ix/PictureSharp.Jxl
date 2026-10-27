
namespace PictureSharp.Formats.Jxl.Codestream.Model;

/// <summary>
/// The codestream headers before the first frame: signature, SizeHeader, ImageMetadata, CustomTransformData and,
/// when an ICC profile is signalled, its encoded size. The entropy-coded ICC stream itself is not read yet.
/// Order and checks follow libjxl v0.12.0 lib/jxl/decode.cc (JxlDecoderReadBasicInfo, JxlDecoderReadAllHeaders).
/// </summary>
internal sealed record JxlHeaders(JxlSizeHeader Size, JxlImageMetadata Metadata, JxlCustomTransformData TransformData)
{
    /// <summary>libjxl's limit on the encoded ICC size, which keeps a corrupt size from reserving memory.</summary>
    public const ulong MaxIccSize = 1 << 28;

    /// <summary>The encoded ICC profile size when <see cref="JxlColorEncoding.WantIcc"/> is set.</summary>
    public ulong? IccSize { get; init; }

    /// <summary>
    /// The bit position after the headers: after the ICC size, where the entropy-coded profile continues, when one
    /// follows; otherwise the byte-aligned start of the first frame.
    /// </summary>
    public long EndBitPosition { get; init; }
}
