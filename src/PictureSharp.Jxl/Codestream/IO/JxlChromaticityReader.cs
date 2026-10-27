using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlChromaticity;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlChromaticity using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlChromaticityReader
{
    public static JxlChromaticity Read(ref JxlBitReader reader) => new(Coordinate(ref reader), Coordinate(ref reader));

    private static int Coordinate(ref JxlBitReader reader)
    {
        uint packed = JxlFields.U32(ref reader, Bits32(19), BitsOffset(19, 524288), BitsOffset(20, 1048576), BitsOffset(21, 2097152));
        return (int)(packed >> 1) ^ -(int)(packed & 1);
    }
}
