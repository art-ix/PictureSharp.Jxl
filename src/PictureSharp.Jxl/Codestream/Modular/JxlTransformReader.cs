using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Modular;

internal static class JxlTransformReader
{
    public static JxlTransform Read(ref JxlBitReader reader)
    {
        uint id = JxlFields.U32(ref reader, JxlU32.Val(0), JxlU32.Val(1), JxlU32.Val(2), JxlU32.Val(3));
        if (id == 3)
        {
            throw JxlDescriptor.Corrupt("Invalid Modular transform.");
        }

        var transform = new JxlTransform { Id = (JxlTransformId)id };
        if (transform.Id == JxlTransformId.Squeeze)
        {
            uint count = JxlFields.U32(ref reader, JxlU32.Val(0), JxlU32.BitsOffset(4, 1), JxlU32.BitsOffset(6, 9), JxlU32.BitsOffset(8, 41));
            var squeezes = new List<JxlSqueeze>();
            for (int i = 0; i < count; i++)
            {
                squeezes.Add(new JxlSqueeze(reader.ReadBool(), reader.ReadBool(), Channel(ref reader),
                    JxlFields.U32(ref reader, JxlU32.Val(1), JxlU32.Val(2), JxlU32.Val(3), JxlU32.BitsOffset(4, 4))));
            }

            return new JxlTransform { Id = transform.Id, Squeezes = squeezes };
        }

        uint begin = Channel(ref reader);
        if (transform.Id == JxlTransformId.Rct)
        {
            uint type = JxlFields.U32(ref reader, JxlU32.Val(6), JxlU32.Bits32(2), JxlU32.BitsOffset(4, 2), JxlU32.BitsOffset(6, 10));
            return type < 42 ? new JxlTransform { Id = transform.Id, BeginC = begin, RctType = type } : throw JxlDescriptor.Corrupt("Invalid RCT type.");
        }

        uint channels = JxlFields.U32(ref reader, JxlU32.Val(1), JxlU32.Val(3), JxlU32.Val(4), JxlU32.BitsOffset(13, 1));
        uint colors = JxlFields.U32(ref reader, JxlU32.Bits32(8), JxlU32.BitsOffset(10, 256), JxlU32.BitsOffset(12, 1280), JxlU32.BitsOffset(16, 5376));
        uint deltas = JxlFields.U32(ref reader, JxlU32.Val(0), JxlU32.BitsOffset(8, 1), JxlU32.BitsOffset(10, 257), JxlU32.BitsOffset(16, 1281));
        uint predictor = reader.ReadBits(4);
        return predictor < JxlMaTree.PredictorCount
            ? new JxlTransform { Id = transform.Id, BeginC = begin, NumC = channels, Colors = colors, Deltas = deltas, Predictor = (JxlPredictor)predictor }
            : throw JxlDescriptor.Corrupt("Invalid palette predictor.");
    }

    private static uint Channel(ref JxlBitReader reader) =>
        JxlFields.U32(ref reader, JxlU32.Bits32(3), JxlU32.BitsOffset(6, 8), JxlU32.BitsOffset(10, 72), JxlU32.BitsOffset(13, 1096));
}
