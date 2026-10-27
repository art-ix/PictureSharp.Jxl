using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlBlendingInfo;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlBlendingInfo using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlBlendingInfoReader
{
    public static JxlBlendingInfo Read(ref JxlBitReader reader, int extraChannels, bool partial)
    {
        uint mode = JxlFields.U32(ref reader, Val(0), Val(1), Val(2), BitsOffset(2, 3));
        if (mode > (uint)JxlBlendMode.Mul)
        {
            throw JxlDescriptor.Corrupt("Invalid blend mode.");
        }

        var blend = (JxlBlendMode)mode;
        bool usesAlpha = extraChannels > 0 && blend is JxlBlendMode.Blend or JxlBlendMode.AlphaWeightedAdd;
        uint alpha = 0;
        if (usesAlpha)
        {
            alpha = JxlFields.U32(ref reader, Val(0), Val(1), Val(2), BitsOffset(3, 3));
            if (alpha >= extraChannels)
            {
                throw JxlDescriptor.Corrupt("Invalid alpha channel for blending.");
            }
        }

        bool clamp = (usesAlpha || blend == JxlBlendMode.Mul) && reader.ReadBool();
        uint source = blend != JxlBlendMode.Replace || partial ? JxlFields.U32(ref reader, Val(0), Val(1), Val(2), Val(3)) : 0;
        return new JxlBlendingInfo(blend, alpha, clamp, source);
    }
}
