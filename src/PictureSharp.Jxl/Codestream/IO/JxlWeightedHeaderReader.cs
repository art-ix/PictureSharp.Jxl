using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlWeightedHeader;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

internal static class JxlWeightedHeaderReader
{
    public static JxlWeightedHeader Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        int Bits5(ref JxlBitReader r) => (int)r.ReadBits(5);
        return new JxlWeightedHeader(Bits5(ref reader), Bits5(ref reader), Bits5(ref reader), Bits5(ref reader), Bits5(ref reader),
            Bits5(ref reader), Bits5(ref reader), [reader.ReadBits(4), reader.ReadBits(4), reader.ReadBits(4), reader.ReadBits(4)]);
    }
}
