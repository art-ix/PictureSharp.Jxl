using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Rendering;

namespace PictureSharp.Formats.Jxl.Codestream.VarDct;

internal static class JxlEpfSigma
{
    /// <summary>
    /// The inverse sigma of every block: from the quantisation field and sharpness (VarDCT, ComputeSigma) or one
    /// value for the whole frame (Modular).
    /// </summary>
    public static float[] Compute(JxlLoopFilter filter, int xBlocks, int yBlocks, float quantScale, int[] quantField, byte[] sharpness,
        JxlVarblock[] varblocks, CancellationToken cancellationToken = default)
    {
        float[] sigma = new float[xBlocks * yBlocks];
        for (int by = 0; by < yBlocks; by++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int bx = 0; bx < xBlocks; bx++)
            {
                int index = by * xBlocks + bx;
                JxlVarblock varblock = varblocks[index];
                if (!varblock.IsFirst)
                {
                    continue;
                }

                JxlAcStrategyType type = varblock.Type;
                float sigmaQuant = filter.QuantMul / (quantScale * quantField[index] * JxlRenderPipeline.InvSigmaNum);
                for (int iy = 0; iy < type.BlocksY; iy++)
                {
                    for (int ix = 0; ix < type.BlocksX; ix++)
                    {
                        int block = index + iy * xBlocks + ix;
                        float value = Math.Min(-1e-4f, sigmaQuant * filter.SharpnessLut[sharpness[block]]);
                        sigma[block] = 1.0f / value;
                    }
                }
            }
        }

        return sigma;
    }
}
