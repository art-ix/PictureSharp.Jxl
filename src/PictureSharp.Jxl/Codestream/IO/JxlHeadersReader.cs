using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlHeaders;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlHeaders using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlHeadersReader
{
    public static JxlHeaders Read(JxlCodestream codestream)
    {
        var reader = new JxlBitReader(codestream);
        if (codestream.Length < 2 || reader.ReadBits(16) != 0x0AFF)
        {
            throw JxlDescriptor.Corrupt("Missing codestream signature.");
        }

        JxlSizeHeader size = JxlSizeHeaderReader.Read(ref reader);
        JxlImageMetadata metadata = JxlImageMetadataReader.Read(ref reader);
        var headers = new JxlHeaders(size, metadata, JxlCustomTransformDataReader.Read(ref reader, metadata.XybEncoded));
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
