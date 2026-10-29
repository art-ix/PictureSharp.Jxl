using PictureSharp.Formats.Jxl.Codestream.Buffers;
namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// The weights of a symmetric 5×5 kernel: the centre, the four pixels at distance 1 (r) and 2 (R) in a line, the
/// diagonals at (1, 1) (d) and (2, 2) (D), and the eight knight's moves (1, 2) (L).
/// </summary>
internal readonly record struct JxlSymmetric5Weights(float C, float R1, float R2, float D1, float D2, float L);

/// <summary>Convolutions with mirrored borders. Ported from libjxl v0.12.0 lib/jxl/convolve_symmetric5.cc (BSD-3-Clause).</summary>
internal static partial class JxlConvolution
{
    /// <summary>Convolves a <paramref name="width"/>×<paramref name="height"/> plane with a symmetric 5×5 kernel.</summary>
    public static float[] Symmetric5(float[] input, int width, int height, JxlSymmetric5Weights w, CancellationToken cancellationToken, JxlBufferScope? buffers = null)
    {
        int count = checked(width * height);
        float[] output = buffers is null ? new float[count] : buffers.Rent<float>(count, clear: false);
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int r0 = Mirror(y, height) * width;
            int rm2 = Mirror(y - 2, height) * width;
            int rp2 = Mirror(y + 2, height) * width;
            int rm1 = Mirror(y - 1, height) * width;
            int rp1 = Mirror(y + 1, height) * width;
            for (int x = 0; x < width; x++)
            {
                if (x == 2) x += Symmetric5RowSimd(input, output, y * width, r0, rm2, rp2, rm1, rp1, width, w);
                (int xm2, int xm1, int xp1, int xp2) = x >= 2 && x < width - 2
                    ? (x - 2, x - 1, x + 1, x + 2)
                    : (Mirror(x - 2, width), Mirror(x - 1, width), Mirror(x + 1, width), Mirror(x + 2, width));
                float sum0 = Sum(input, r0, x, xm1, xp1, xm2, xp2, w.C, w.R1, w.R2) + Sum(input, rm2, x, xm1, xp1, xm2, xp2, w.R2, w.L, w.D2);
                float sum1 = Sum(input, rp2, x, xm1, xp1, xm2, xp2, w.R2, w.L, w.D2);
                sum0 += Sum(input, rm1, x, xm1, xp1, xm2, xp2, w.R1, w.D1, w.L);
                sum1 += Sum(input, rp1, x, xm1, xp1, xm2, xp2, w.R1, w.D1, w.L);
                output[y * width + x] = sum0 + sum1;
            }

        }

        return output;
    }

    private static float Sum(float[] input, int row, int x, int xm1, int xp1, int xm2, int xp2, float w0, float w1, float w2)
    {
        float sum2 = w2 * (input[row + xm2] + input[row + xp2]);
        float sum1 = w1 * (input[row + xm1] + input[row + xp1]);
        return sum2 + (sum1 + w0 * input[row + x]);
    }

    /// <summary>Reflects a coordinate into [0, size) (Mirror in image_ops.h).</summary>
    public static int Mirror(int x, int size)
    {
        while (x < 0 || x >= size)
        {
            x = x < 0 ? -x - 1 : 2 * size - 1 - x;
        }

        return x;
    }
}
