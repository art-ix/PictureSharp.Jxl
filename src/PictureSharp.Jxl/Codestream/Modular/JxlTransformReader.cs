using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Modular;

internal static class JxlTransformReader
{
    public static JxlTransform Read(ref JxlBitReader reader)
    {
        uint id = JxlFields.U32(ref reader, JxlTransformFields.Id);
        if (id == 3)
        {
            throw JxlDescriptor.Corrupt("Invalid Modular transform.");
        }

        var transform = new JxlTransform { Id = (JxlTransformId)id };
        if (transform.Id == JxlTransformId.Squeeze)
        {
            uint count = JxlFields.U32(ref reader, JxlTransformFields.SqueezeCount);
            var squeezes = new List<JxlSqueeze>();
            for (int i = 0; i < count; i++)
            {
                squeezes.Add(new JxlSqueeze(reader.ReadBool(), reader.ReadBool(), JxlFields.U32(ref reader, JxlTransformFields.BeginC),
                    JxlFields.U32(ref reader, JxlTransformFields.SqueezeNumC)));
            }

            return new JxlTransform { Id = transform.Id, Squeezes = squeezes };
        }

        uint begin = JxlFields.U32(ref reader, JxlTransformFields.BeginC);
        if (transform.Id == JxlTransformId.Rct)
        {
            uint type = JxlFields.U32(ref reader, JxlTransformFields.RctType);
            return type < JxlRctType.Count
                ? new JxlTransform { Id = transform.Id, BeginC = begin, RctType = new JxlRctType(type) }
                : throw JxlDescriptor.Corrupt("Invalid RCT type.");
        }

        uint channels = JxlFields.U32(ref reader, JxlTransformFields.NumC);
        uint colors = JxlFields.U32(ref reader, JxlTransformFields.Colors);
        uint deltas = JxlFields.U32(ref reader, JxlTransformFields.Deltas);
        uint predictor = reader.ReadBits(JxlTransformFields.PredictorBits);
        return predictor < JxlMaTree.PredictorCount
            ? new JxlTransform { Id = transform.Id, BeginC = begin, NumC = channels, Colors = colors, Deltas = deltas, Predictor = (JxlPredictor)predictor }
            : throw JxlDescriptor.Corrupt("Invalid palette predictor.");
    }
}
