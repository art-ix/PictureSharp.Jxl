using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Modular;

internal static class JxlGroupHeaderReader
{
    public static JxlGroupHeader Read(ref JxlBitReader reader)
    {
        bool global = reader.ReadBool();
        JxlWeightedHeader weighted = JxlWeightedHeaderReader.Read(ref reader);
        uint count = JxlFields.U32(ref reader, JxlU32.Val(0), JxlU32.Val(1), JxlU32.BitsOffset(4, 2), JxlU32.BitsOffset(8, 18));
        var transforms = new JxlTransform[count];
        for (int i = 0; i < transforms.Length; i++)
        {
            transforms[i] = JxlTransformReader.Read(ref reader);
        }

        return new JxlGroupHeader(global, weighted, transforms);
    }
}
