using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

// Adjacent interior output pixels with the scalar Sum's unfused products and order of additions.
internal static partial class JxlConvolution
{
    // The interior pixels of a row from x = 2, leaving at least the last two (mirrored) to the scalar loop. Returns
    // the number of pixels written.
    private static int Symmetric5RowSimd(float[] input, float[] output, int outRow, int r0, int rm2, int rp2, int rm1, int rp1,
        int width, JxlSymmetric5Weights w)
    {
        int x = 2;
        if (Avx2.IsSupported)
        {
            for (; x + 10 <= width; x += 8)
            {
                Vector256<float> sum0 = SumAvx2(input, r0 + x, w.C, w.R1, w.R2) + SumAvx2(input, rm2 + x, w.R2, w.L, w.D2);
                Vector256<float> sum1 = SumAvx2(input, rp2 + x, w.R2, w.L, w.D2);
                sum0 += SumAvx2(input, rm1 + x, w.R1, w.D1, w.L);
                sum1 += SumAvx2(input, rp1 + x, w.R1, w.D1, w.L);
                (sum0 + sum1).CopyTo(output.AsSpan(outRow + x, 8));
            }
        }
        else if (Sse41.IsSupported)
        {
            for (; x + 6 <= width; x += 4)
            {
                Vector128<float> sum0 = Sum128(input, r0 + x, w.C, w.R1, w.R2) + Sum128(input, rm2 + x, w.R2, w.L, w.D2);
                Vector128<float> sum1 = Sum128(input, rp2 + x, w.R2, w.L, w.D2);
                sum0 += Sum128(input, rm1 + x, w.R1, w.D1, w.L);
                sum1 += Sum128(input, rp1 + x, w.R1, w.D1, w.L);
                (sum0 + sum1).CopyTo(output.AsSpan(outRow + x, 4));
            }
        }

        return x - 2;
    }

    private static Vector256<float> SumAvx2(float[] input, int at, float w0, float w1, float w2)
    {
        ReadOnlySpan<float> row = input.AsSpan(at - 2, 12);
        Vector256<float> sum2 = w2 * (Vector256.Create(row) + Vector256.Create(row[4..]));
        Vector256<float> sum1 = w1 * (Vector256.Create(row[1..]) + Vector256.Create(row[3..]));
        return sum2 + (sum1 + w0 * Vector256.Create(row[2..]));
    }

    private static Vector128<float> Sum128(float[] input, int at, float w0, float w1, float w2)
    {
        ReadOnlySpan<float> row = input.AsSpan(at - 2, 8);
        Vector128<float> sum2 = w2 * (Vector128.Create(row) + Vector128.Create(row[4..]));
        Vector128<float> sum1 = w1 * (Vector128.Create(row[1..]) + Vector128.Create(row[3..]));
        return sum2 + (sum1 + w0 * Vector128.Create(row[2..]));
    }
}
