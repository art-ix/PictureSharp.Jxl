using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlCustomTransformData;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlCustomTransformData using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlCustomTransformDataReader
{
    public static JxlCustomTransformData Read(ref JxlBitReader reader, bool xybEncoded)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        var data = Default;
        if (xybEncoded && !reader.ReadBool())
        {
            data = data with
            {
                OpsinInverseMatrix = Floats(ref reader, 9),
                OpsinBiases = Floats(ref reader, 3),
                QuantBiases = Floats(ref reader, 4)
            };
        }

        uint mask = reader.ReadBits(3);
        return data with
        {
            Upsampling2Weights = (mask & 1) != 0 ? Floats(ref reader, 15) : null,
            Upsampling4Weights = (mask & 2) != 0 ? Floats(ref reader, 55) : null,
            Upsampling8Weights = (mask & 4) != 0 ? Floats(ref reader, 210) : null
        };
    }

    private static float[] Floats(ref JxlBitReader reader, int count)
    {
        float[] values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = JxlFields.F16(ref reader);
        }

        return values;
    }
}
