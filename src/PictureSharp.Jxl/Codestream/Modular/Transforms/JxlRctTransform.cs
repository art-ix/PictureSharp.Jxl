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
        JxlRctType type = description.RctType;
        if (type == JxlRctType.None)
        {
            return;
        }

        JxlChannel[] inputs = [image.Channels[m], image.Channels[m + 1], image.Channels[m + 2]];
        (int first, int second, int third) = type.ChannelOrder;
        int[] outputs = [m + first, m + second, m + third];
        if (type.Transform != 0)
        {
            bool yCoCg = type.IsYCoCg;
            bool subtractsFirst = type.SubtractsFirstFromThird;
            int secondMode = type.SecondChannelMode;
            int[] c0 = inputs[0].Pixels;
            int[] c1 = inputs[1].Pixels;
            int[] c2 = inputs[2].Pixels;
            for (int i = 0; i < inputs[0].Width * inputs[0].Height; i++)
            {
                if ((i & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                unchecked
                {
                    if (yCoCg)
                    {
                        int y = c0[i];
                        int co = c1[i];
                        int cg = c2[i];
                        int tmp = y - (cg >> 1);
                        int g = cg + tmp;
                        int b = tmp - (co >> 1);
                        (c0[i], c1[i], c2[i]) = (b + co, g, b);
                        continue;
                    }

                    int a = c0[i];
                    int s = c1[i];
                    int t = c2[i];
                    if (subtractsFirst)
                    {
                        t += a;
                    }

                    if (secondMode == 1)
                    {
                        s += a;
                    }
                    else if (secondMode == 2)
                    {
                        s += (a + t) >> 1;
                    }

                    (c0[i], c1[i], c2[i]) = (a, s, t);
                }
            }
        }

        for (int i = 0; i < 3; i++)
        {
            image.ReplaceChannel(outputs[i], inputs[i]);
        }
    }
}
