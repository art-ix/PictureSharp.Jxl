using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlSizeHeader using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlSizeHeaderReader
{
    public static JxlSizeHeader Read(ref JxlBitReader reader)
    {
        bool small = reader.ReadBool();
        uint height = small ? SmallDimension(ref reader) : Dimension(ref reader);
        uint ratio = reader.ReadBits(3);
        uint width = ratio != 0 ? JxlAspectRatio.Width(ratio, height) : small ? SmallDimension(ref reader) : Dimension(ref reader);
        return new JxlSizeHeader(width, height);
    }

    // A multiple of 8 up to 256.
    private static uint SmallDimension(ref JxlBitReader reader) => (reader.ReadBits(5) + 1) * 8;

    private static uint Dimension(ref JxlBitReader reader) =>
        JxlFields.U32(ref reader, BitsOffset(9, 1), BitsOffset(13, 1), BitsOffset(18, 1), BitsOffset(30, 1));
}
