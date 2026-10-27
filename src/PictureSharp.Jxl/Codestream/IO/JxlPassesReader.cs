using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlPasses;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlPasses using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlPassesReader
{
    public static JxlPasses Read(ref JxlBitReader reader)
    {
        uint count = JxlFields.U32(ref reader, Val(1), Val(2), Val(3), BitsOffset(3, 4));
        if (count == 1)
        {
            return JxlPasses.Single;
        }

        uint downsamples = JxlFields.U32(ref reader, Val(0), Val(1), Val(2), BitsOffset(1, 3));
        if (downsamples > count)
        {
            throw JxlDescriptor.Corrupt("More downsampling steps than passes.");
        }

        uint[] shift = new uint[count];
        for (int i = 0; i < count - 1; i++)
        {
            shift[i] = reader.ReadBits(2);
        }

        uint[] downsample = new uint[downsamples];
        for (int i = 0; i < downsample.Length; i++)
        {
            downsample[i] = JxlFields.U32(ref reader, Val(1), Val(2), Val(4), Val(8));
            if (i > 0 && downsample[i] >= downsample[i - 1])
            {
                throw JxlDescriptor.Corrupt("The downsampling sequence must decrease.");
            }
        }

        uint[] lastPass = new uint[downsamples];
        for (int i = 0; i < lastPass.Length; i++)
        {
            lastPass[i] = JxlFields.U32(ref reader, Val(0), Val(1), Val(2), Bits32(3));
            if ((i > 0 && lastPass[i] <= lastPass[i - 1]) || lastPass[i] >= count)
            {
                throw JxlDescriptor.Corrupt("Invalid last pass of a downsampling step.");
            }
        }

        return new JxlPasses(count, shift, downsample, lastPass);
    }
}
