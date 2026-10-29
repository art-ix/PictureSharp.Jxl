using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Formats.Jxl.Codestream.VarDct;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

internal sealed partial class JxlCoefficientQuantizer
{
    internal static int QuantizeRowSimd(ReadOnlySpan<float> weights, ReadOnlySpan<float> input, Span<int> output,
        float quant, float left, float right)
    {
        if (Avx2.IsSupported) return QuantizeRowAvx2(weights, input, output, quant, left, right);
        return Sse41.IsSupported ? QuantizeRowSse41(weights, input, output, quant, left, right) : 0;
    }

    // Multiplication remains left-associated and unfused. Rounding is explicitly nearest-even.
    internal static int QuantizeRowAvx2(ReadOnlySpan<float> weights, ReadOnlySpan<float> input, Span<int> output,
        float quant, float left, float right)
    {
        int half = input.Length / 2;
        if ((half & 7) != 0 && half != 4) return 0;
        int end = input.Length & ~7;
        for (int i = 0; i < end; i += 8)
        {
            Vector256<float> threshold = i + 8 <= half ? Vector256.Create(left) : i >= half ? Vector256.Create(right)
                : Vector256.Create(left, left, left, left, right, right, right, right);
            Vector256<float> value = (Vector256.Create(weights.Slice(i, 8)) * quant) * Vector256.Create(input.Slice(i, 8));
            Vector256<int> rounded = Avx.ConvertToVector256Int32WithTruncation(Avx.RoundToNearestInteger(value));
            rounded = Avx2.Or(Avx2.And(Avx.CompareGreaterThanOrEqual(value, Vector256.Create(2147483648.0f)).AsInt32(), Vector256.Create(int.MaxValue)),
                Avx2.AndNot(Avx.CompareGreaterThanOrEqual(value, Vector256.Create(2147483648.0f)).AsInt32(), rounded));
            Avx2.And(rounded, Avx.CompareGreaterThanOrEqual(Vector256.Abs(value), threshold).AsInt32()).CopyTo(output.Slice(i, 8));
        }
        return end;
    }

    internal static int QuantizeRowSse41(ReadOnlySpan<float> weights, ReadOnlySpan<float> input, Span<int> output,
        float quant, float left, float right)
    {
        int half = input.Length / 2;
        if ((half & 3) != 0) return 0;
        int end = input.Length & ~3;
        for (int i = 0; i < end; i += 4)
        {
            Vector128<float> value = (Vector128.Create(weights.Slice(i, 4)) * quant) * Vector128.Create(input.Slice(i, 4));
            Vector128<float> threshold = Vector128.Create(i < half ? left : right);
            Vector128<int> rounded = Sse2.ConvertToVector128Int32WithTruncation(Sse41.RoundToNearestInteger(value));
            rounded = Sse41.BlendVariable(rounded, Vector128.Create(int.MaxValue),
                Sse.CompareGreaterThanOrEqual(value, Vector128.Create(2147483648.0f)).AsInt32());
            Sse2.And(rounded, Sse.CompareGreaterThanOrEqual(Vector128.Abs(value), threshold).AsInt32()).CopyTo(output.Slice(i, 4));
        }
        return end;
    }

    // The Y dequantization (AdjustQuantBias(q) * dequant * inverseQuant, left to right) and the chroma-from-luma
    // subtraction fma(-factor, y, chroma), eight coefficients at a time with the decoder's exact vector bias.
    internal static int DequantizeYAndSubtractAvx2(ReadOnlySpan<int> quantizedY, ReadOnlySpan<float> dequantY, Span<float> block, int size,
        float inverseQuant, float xFactor, float bFactor, float bias, float numeratorBias)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported) return 0;
        int end = size & ~7;
        for (int k = 0; k < end; k += 8)
        {
            Vector256<float> q = Avx.ConvertToVector256Single(Vector256.Create(quantizedY.Slice(k, 8)));
            Vector256<float> y = JxlVarDctFrame.AdjustQuantBiasAvx2(q, bias, numeratorBias) * Vector256.Create(dequantY.Slice(k, 8)) * inverseQuant;
            y.CopyTo(block.Slice(size + k, 8));
            Fma.MultiplyAdd(Vector256.Create(-xFactor), y, Vector256.Create(block.Slice(k, 8))).CopyTo(block.Slice(k, 8));
            Fma.MultiplyAdd(Vector256.Create(-bFactor), y, Vector256.Create(block.Slice(2 * size + k, 8))).CopyTo(block.Slice(2 * size + k, 8));
        }

        return end;
    }
}
