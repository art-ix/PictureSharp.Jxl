using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;

namespace PictureSharp.Formats.Jxl.Codestream.Modular.Transforms;

/// <summary>Scalar transform ported from libjxl v0.12.0 modular/transform (BSD-3-Clause).</summary>
internal sealed class JxlRctTransform(JxlTransform description) : IJxlModularTransform
{
    public void MetaApply(JxlModularImage image) => image.CheckEqualChannels(description.BeginC, description.BeginC + 2L);

    public void Inverse(JxlModularImage image, JxlWeightedHeader weighted, CancellationToken cancellationToken)
    {
        image.CheckEqualChannels(description.BeginC, description.BeginC + 2L);
        int m = (int)description.BeginC;
        if (description.RctType == 0)
        {
            return;
        }

        // Permutation: 0=RGB, 1=GBR, 2=BRG, 3=RBG, 4=GRB, 5=BGR. Type 6 is YCoCg; 0–5 have the low bit for the third
        // channel (subtract first) and the high bits for the second (nothing, subtract first, subtract average).
        int permutation = (int)description.RctType / 7;
        int custom = (int)description.RctType % 7;
        JxlChannel[] inputs = [image.Channels[m], image.Channels[m + 1], image.Channels[m + 2]];
        int[] outputs = [m + permutation % 3, m + (permutation + 1 + permutation / 3) % 3, m + (permutation + 2 - permutation / 3) % 3];
        if (custom != 0)
        {
            int[] first = inputs[0].Pixels;
            int[] second = inputs[1].Pixels;
            int[] third = inputs[2].Pixels;
            for (int i = 0; i < first.Length; i++)
            {
                if ((i & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                unchecked
                {
                    if (custom == 6)
                    {
                        int y = first[i];
                        int co = second[i];
                        int cg = third[i];
                        int tmp = y - (cg >> 1);
                        int g = cg + tmp;
                        int b = tmp - (co >> 1);
                        (first[i], second[i], third[i]) = (b + co, g, b);
                        continue;
                    }

                    int a = first[i];
                    int s = second[i];
                    int t = third[i];
                    if ((custom & 1) != 0)
                    {
                        t += a;
                    }

                    if (custom >> 1 == 1)
                    {
                        s += a;
                    }
                    else if (custom >> 1 == 2)
                    {
                        s += (a + t) >> 1;
                    }

                    (first[i], second[i], third[i]) = (a, s, t);
                }
            }
        }

        for (int i = 0; i < 3; i++)
        {
            image.ReplaceChannel(outputs[i], inputs[i]);
        }
    }
}
