using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlAnimationHeader using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlAnimationHeaderReader
{
    public static JxlAnimationHeader Read(ref JxlBitReader reader) => new(
        JxlFields.U32(ref reader, Val(100), Val(1000), BitsOffset(10, 1), BitsOffset(30, 1)),
        JxlFields.U32(ref reader, Val(1), Val(1001), BitsOffset(8, 1), BitsOffset(10, 1)),
        JxlFields.U32(ref reader, Val(0), Bits32(3), Bits32(16), Bits32(32)),
        reader.ReadBool());
}
