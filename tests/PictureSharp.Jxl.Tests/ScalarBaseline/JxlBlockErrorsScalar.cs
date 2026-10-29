// Frozen 0.7.10 scalar reference (6cd7f9b, JxlEpfSharpnessSearch.BlockErrors). Do not update from SIMD output.
using PictureSharp.Formats.Jxl.Codestream.Buffers;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

internal static class JxlBlockErrorsScalar
{
    // ComputeBlockL2Distance: the squared, masked error of each block, weighted per channel.
    public static float[] BlockErrors(int xBlocks, int yBlocks, JxlImage3F original, JxlPlane[] decoded, float[] mask1X1)
    {
        int width = decoded[0].Width;
        int height = decoded[0].Height;
        float[] errors = new float[xBlocks * yBlocks];
        Span<float> error = stackalloc float[3];
        for (int by = 0; by < yBlocks; by++)
        {
            for (int bx = 0; bx < xBlocks; bx++)
            {
                error.Clear();
                for (int y = by * 8; y < Math.Min(by * 8 + 8, height); y++)
                {
                    for (int x = bx * 8; x < Math.Min(bx * 8 + 8, width); x++)
                    {
                        float mask = mask1X1[y * original.Width + x];
                        float mask2 = mask * mask;
                        for (int c = 0; c < 3; c++)
                        {
                            float diff = original.Planes[c][y * original.Width + x] - decoded[c].Row(y)[x];
                            error[c] += mask2 * diff * diff;
                        }
                    }
                }

                errors[by * xBlocks + bx] = (float)(12.339445295782363 * error[0] + 1.0 * error[1] + 0.2 * error[2]);
            }
        }

        return errors;
    }
}
