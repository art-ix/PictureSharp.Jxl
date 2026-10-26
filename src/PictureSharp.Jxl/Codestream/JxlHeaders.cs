using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Codestream;

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

    public static JxlHeaders Read(JxlCodestream codestream)
    {
        var reader = new JxlBitReader(codestream);
        if (codestream.Length < 2 || reader.ReadBits(16) != 0x0AFF)
        {
            throw JxlDescriptor.Corrupt("Missing codestream signature.");
        }

        JxlSizeHeader size = JxlSizeHeader.Read(ref reader);
        JxlImageMetadata metadata = JxlImageMetadata.Read(ref reader);
        var headers = new JxlHeaders(size, metadata, JxlCustomTransformData.Read(ref reader, metadata.XybEncoded));
        if (!metadata.Color.WantIcc)
        {
            reader.JumpToByteBoundary();
            return headers with { EndBitPosition = reader.BitPosition };
        }

        ulong iccSize = JxlFields.U64(ref reader);
        if (iccSize is 0 or > MaxIccSize)
        {
            throw JxlDescriptor.Corrupt($"Invalid ICC profile size {iccSize}.");
        }

        return headers with { IccSize = iccSize, EndBitPosition = reader.BitPosition };
    }
}
