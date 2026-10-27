using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlPreviewHeader using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlPreviewHeaderReader
{
    public static JxlPreviewHeader Read(ref JxlBitReader reader)
    {
        bool div8 = reader.ReadBool();
        uint height = Dimension(ref reader, div8);
        uint ratio = reader.ReadBits(3);
        uint width = ratio != 0 ? JxlAspectRatio.Width(ratio, height) : Dimension(ref reader, div8);
        return new JxlPreviewHeader(width, height);
    }

    private static uint Dimension(ref JxlBitReader reader, bool div8) => div8
        ? JxlFields.U32(ref reader, Val(16), Val(32), BitsOffset(5, 1), BitsOffset(9, 33)) * 8
        : JxlFields.U32(ref reader, BitsOffset(6, 1), BitsOffset(8, 65), BitsOffset(10, 321), BitsOffset(12, 1345));
}
