using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

internal sealed partial class JxlChromaFromLumaSearch
{
    internal static int PrepareSlopesAvx2(ReadOnlySpan<float> m, ReadOnlySpan<float> s, Span<float> slopes,
        Span<float> offsets, float baseCorrelation)
    {
        if (!Avx2.IsSupported) return 0;
        int end = m.Length & ~7;
        for (int i = 0; i < end; i += 8)
        {
            Vector256<float> value = Vector256.Create(m.Slice(i, 8));
            ((1.0f / ColorFactor) * value).CopyTo(slopes.Slice(i, 8));
            ((baseCorrelation * value) - Vector256.Create(s.Slice(i, 8))).CopyTo(offsets.Slice(i, 8));
        }
        return end;
    }

    internal static int DerivativesAvx2(ReadOnlySpan<float> slopes, ReadOnlySpan<float> offsets,
        float x, float xPlus, float xMinus, ref float sum, ref float sumPlus, ref float sumMinus)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported) return 0;
        int end = slopes.Length & ~7;
        Vector128<float> sums = Vector128.Create(sum, sumPlus, sumMinus, 0.0f);
        for (int i = 0; i < end; i += 8)
        {
            Vector256<float> a = Vector256.Create(slopes.Slice(i, 8)), b = Vector256.Create(offsets.Slice(i, 8));
            Vector256<float> v = Fma.MultiplyAdd(a, Vector256.Create(x), b);
            int skip = Avx.MoveMask(Avx.CompareGreaterThanOrEqual(Vector256.Abs(v), Vector256.Create(100.0f)));
            if (skip == 255) continue;
            Vector256<float> vp = Fma.MultiplyAdd(a, Vector256.Create(xPlus), b);
            Vector256<float> vm = Fma.MultiplyAdd(a, Vector256.Create(xMinus), b);
            Vector256<float> scale = (2.0f / 3) * a;
            Vector256<float> d = SignedAvx2(scale * (Vector256.Abs(v) + Vector256.Create(1.0f)), v);
            Vector256<float> dp = SignedAvx2(scale * (Vector256.Abs(vp) + Vector256.Create(1.0f)), vp);
            Vector256<float> dm = SignedAvx2(scale * (Vector256.Abs(vm) + Vector256.Create(1.0f)), vm);
            // Transpose independent derivatives, not coefficients: each accumulator still consumes
            // coefficients 0..7 in scalar order. No horizontal reduction or temporary stack buffer.
            Vector256<float> lo = Avx.UnpackLow(d, dp), hi = Avx.UnpackHigh(d, dp);
            Vector256<float> ml = Avx.UnpackLow(dm, Vector256<float>.Zero);
            Vector256<float> mh = Avx.UnpackHigh(dm, Vector256<float>.Zero);
            Vector256<float> d04 = Avx.Shuffle(lo, ml, 0x44), d15 = Avx.Shuffle(lo, ml, 0xEE);
            Vector256<float> d26 = Avx.Shuffle(hi, mh, 0x44), d37 = Avx.Shuffle(hi, mh, 0xEE);
            if ((skip & 1) == 0) sums += d04.GetLower();
            if ((skip & 2) == 0) sums += d15.GetLower();
            if ((skip & 4) == 0) sums += d26.GetLower();
            if ((skip & 8) == 0) sums += d37.GetLower();
            if ((skip & 16) == 0) sums += d04.GetUpper();
            if ((skip & 32) == 0) sums += d15.GetUpper();
            if ((skip & 64) == 0) sums += d26.GetUpper();
            if ((skip & 128) == 0) sums += d37.GetUpper();
        }
        sum = sums.GetElement(0);
        sumPlus = sums.GetElement(1);
        sumMinus = sums.GetElement(2);
        return end;
    }

    private static Vector256<float> SignedAvx2(Vector256<float> d, Vector256<float> v) =>
        Avx.Xor(d, Avx.And(Avx.CompareLessThan(v, Vector256<float>.Zero), Vector256.Create(-0.0f)));
}
