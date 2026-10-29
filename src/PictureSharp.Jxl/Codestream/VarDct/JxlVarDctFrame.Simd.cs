using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Formats.Jxl.Codestream.VarDct;

internal sealed partial class JxlVarDctFrame
{
    internal static int DequantizeAvx2(ReadOnlySpan<int> qx, ReadOnlySpan<int> qy, ReadOnlySpan<int> qb,
        ReadOnlySpan<float> matrix, Span<float> block, int size, float stepX, float step, float stepB,
        float xCc, float bCc, ReadOnlySpan<float> biases)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported) return 0;
        int end = size & ~7;
        for (int k = 0; k < end; k += 8)
        {
            Vector256<float> xMul = Vector256.Create(matrix.Slice(k, 8)) * stepX;
            Vector256<float> yMul = Vector256.Create(matrix.Slice(size + k, 8)) * step;
            Vector256<float> bMul = Vector256.Create(matrix.Slice(2 * size + k, 8)) * stepB;
            Vector256<float> x = AdjustQuantBiasAvx2(Avx.ConvertToVector256Single(Vector256.Create(qx.Slice(k, 8))), biases[0], biases[3]) * xMul;
            Vector256<float> y = AdjustQuantBiasAvx2(Avx.ConvertToVector256Single(Vector256.Create(qy.Slice(k, 8))), biases[1], biases[3]) * yMul;
            Vector256<float> b = AdjustQuantBiasAvx2(Avx.ConvertToVector256Single(Vector256.Create(qb.Slice(k, 8))), biases[2], biases[3]) * bMul;
            Fma.MultiplyAdd(Vector256.Create(xCc), y, x).CopyTo(block.Slice(k, 8));
            y.CopyTo(block.Slice(size + k, 8));
            Fma.MultiplyAdd(Vector256.Create(bCc), y, b).CopyTo(block.Slice(2 * size + k, 8));
        }
        return end;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector256<float> AdjustQuantBiasAvx2(Vector256<float> q, float bias, float numeratorBias)
    {
        Vector256<float> magnitude = Vector256.Abs(q);
        Vector256<float> signedBias = Avx.Or(Vector256.Create(Math.Abs(bias)), Avx.And(q, Vector256.Create(-0.0f)));
        Vector256<float> small = Avx.And(Avx.CompareGreaterThan(magnitude, Vector256<float>.Zero), signedBias);
        Vector256<float> adjusted = Fma.MultiplyAdd(Vector256.Create(-numeratorBias), Vector256.Create(1.0f) / q, q);
        return Avx.BlendVariable(adjusted, small, Avx.CompareLessThan(magnitude, Vector256.Create(1.125f)));
    }
}
