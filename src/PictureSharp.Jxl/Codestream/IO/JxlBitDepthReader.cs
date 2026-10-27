using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlBitDepth using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlBitDepthReader
{
    public static JxlBitDepth Read(ref JxlBitReader reader)
    {
        if (!reader.ReadBool())
        {
            uint bits = JxlFields.U32(ref reader, Val(8), Val(10), Val(12), BitsOffset(6, 1));
            return bits <= 31 ? new JxlBitDepth(false, bits, 0) : throw JxlDescriptor.Corrupt($"Invalid bits per sample {bits}.");
        }

        uint floatBits = JxlFields.U32(ref reader, Val(32), Val(16), Val(24), BitsOffset(6, 1));
        uint exponent = reader.ReadBits(4) + 1;
        int mantissa = (int)floatBits - (int)exponent - 1;
        if (exponent is < 2 or > 8 || mantissa is < 2 or > 23)
        {
            throw JxlDescriptor.Corrupt($"Invalid float sample format {floatBits}/{exponent}.");
        }

        return new JxlBitDepth(true, floatBits, exponent);
    }
}
