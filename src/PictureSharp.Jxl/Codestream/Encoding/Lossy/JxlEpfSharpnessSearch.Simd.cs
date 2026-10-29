using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.VarDct;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

internal static partial class JxlEpfSharpnessSearch
{
    /// <summary>
    /// BlockErrors for groups of eight whole blocks of one block row, one block per lane: each row's terms are computed
    /// eight pixels at a time (unfused, as the scalar mask2 * diff * diff), transposed so that a vector holds one column
    /// of the eight blocks, and added in the scalar pixel order, so every block keeps its own ordered float sums. Returns
    /// the number of blocks written to <paramref name="errors"/>.
    /// </summary>
    private static int BlockErrorsAvx2(JxlImage3F original, JxlPlane[] decoded, float[] mask1X1, int by, int width, int height,
        Span<float> errors)
    {
        if (!Avx2.IsSupported) return 0;
        int blocks = width / 64 * 8;
        int yEnd = Math.Min(by * 8 + 8, height);
        int stride = original.Width;
        Span<float> sums = stackalloc float[24];
        for (int bx = 0; bx < blocks; bx += 8)
        {
            for (int c = 0; c < 3; c++)
            {
                Vector256<float> sum = Vector256<float>.Zero;
                for (int y = by * 8; y < yEnd; y++)
                {
                    ReadOnlySpan<float> mask = mask1X1.AsSpan(y * stride + bx * 8, 64);
                    ReadOnlySpan<float> source = original.Planes[c].AsSpan(y * stride + bx * 8, 64);
                    ReadOnlySpan<float> result = decoded[c].Row(y).Slice(bx * 8, 64);
                    Vector256<float> t0 = Term(mask, source, result, 0), t1 = Term(mask, source, result, 8);
                    Vector256<float> t2 = Term(mask, source, result, 16), t3 = Term(mask, source, result, 24);
                    Vector256<float> t4 = Term(mask, source, result, 32), t5 = Term(mask, source, result, 40);
                    Vector256<float> t6 = Term(mask, source, result, 48), t7 = Term(mask, source, result, 56);
                    JxlDct.Transpose8x8Avx(ref t0, ref t1, ref t2, ref t3, ref t4, ref t5, ref t6, ref t7);
                    sum += t0;
                    sum += t1;
                    sum += t2;
                    sum += t3;
                    sum += t4;
                    sum += t5;
                    sum += t6;
                    sum += t7;
                }

                sum.CopyTo(sums.Slice(8 * c, 8));
            }

            for (int k = 0; k < 8; k++)
            {
                errors[bx + k] = (float)(12.339445295782363 * sums[k] + 1.0 * sums[8 + k] + 0.2 * sums[16 + k]);
            }
        }

        return blocks;
    }

    private static Vector256<float> Term(ReadOnlySpan<float> mask, ReadOnlySpan<float> source, ReadOnlySpan<float> result, int i)
    {
        Vector256<float> m = Vector256.Create(mask.Slice(i, 8));
        Vector256<float> diff = Vector256.Create(source.Slice(i, 8)) - Vector256.Create(result.Slice(i, 8));
        return m * m * diff * diff;
    }
}
