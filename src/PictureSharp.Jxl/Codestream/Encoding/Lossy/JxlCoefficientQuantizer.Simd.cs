using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
}
