using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlToneMapping;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlToneMapping using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlToneMappingReader
{
    public static JxlToneMapping Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        float intensity = JxlFields.F16(ref reader);
        if (intensity <= 0)
        {
            throw JxlDescriptor.Corrupt("Invalid intensity target.");
        }

        float minNits = JxlFields.F16(ref reader);
        if (minNits < 0 || minNits > intensity)
        {
            throw JxlDescriptor.Corrupt("Invalid minimum luminance.");
        }

        bool relative = reader.ReadBool();
        float linearBelow = JxlFields.F16(ref reader);
        if (linearBelow < 0 || (relative && linearBelow > 1))
        {
            throw JxlDescriptor.Corrupt("Invalid linear_below value.");
        }

        return new JxlToneMapping(intensity, minNits, relative, linearBelow);
    }
}
