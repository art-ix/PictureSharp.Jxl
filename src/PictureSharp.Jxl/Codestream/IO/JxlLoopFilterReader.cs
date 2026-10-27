using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlLoopFilter;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlLoopFilter using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlLoopFilterReader
{
    public static JxlLoopFilter Read(ref JxlBitReader reader, bool modular)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        var filter = new JxlLoopFilter { Gaborish = reader.ReadBool() };
        if (filter.Gaborish && reader.ReadBool())
        {
            float[] weights = new float[6];
            for (int channel = 0; channel < 3; channel++)
            {
                weights[2 * channel] = JxlFields.F16(ref reader);
                weights[2 * channel + 1] = JxlFields.F16(ref reader);
                if (Math.Abs(1.0f + (weights[2 * channel] + weights[2 * channel + 1]) * 4) < 1e-8)
                {
                    throw JxlDescriptor.Corrupt("Gaborish weights lead to a zero kernel.");
                }
            }

            filter = filter with { GaborishWeights = weights };
        }

        filter = filter with { EpfIterations = reader.ReadBits(2) };
        if (filter.EpfIterations > 0)
        {
            if (!modular && reader.ReadBool())
            {
                filter = filter with { SharpnessLut = JxlFields.F16Array(ref reader, SharpnessEntries) };
            }

            if (reader.ReadBool())
            {
                float[] values = JxlFields.F16Array(ref reader, 5);
                filter = filter with { ChannelScale = values[..3], Pass1ZeroFlush = values[3], Pass2ZeroFlush = values[4] };
            }

            if (reader.ReadBool())
            {
                float quantMul = modular ? filter.QuantMul : JxlFields.F16(ref reader);
                float[] values = JxlFields.F16Array(ref reader, 3);
                filter = filter with { QuantMul = quantMul, Pass0SigmaScale = values[0], Pass2SigmaScale = values[1], BorderSadMul = values[2] };
            }

            if (modular)
            {
                filter = filter with { SigmaForModular = JxlFields.F16(ref reader) };
                if (filter.SigmaForModular < 1e-8)
                {
                    throw JxlDescriptor.Corrupt("EPF sigma for Modular is too small.");
                }
            }
        }

        JxlFields.SkipExtensions(ref reader);
        return filter;
    }
}
