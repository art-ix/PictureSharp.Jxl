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

    /// <summary>
    /// The first multiple of eight of the derivative sums into <paramref name="lanes"/> (sum, plus, minus: eight lanes each,
    /// coefficient i in lane i % 8), adding zero for coefficients left out, as the scalar loop does. Returns the count done.
    /// </summary>
    internal static int DerivativesAvx2(ReadOnlySpan<float> slopes, ReadOnlySpan<float> offsets,
        float x, float xPlus, float xMinus, Span<float> lanes)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported) return 0;
        int end = slopes.Length & ~7;
        Vector256<float> sum = Vector256<float>.Zero, sumPlus = Vector256<float>.Zero, sumMinus = Vector256<float>.Zero;
        for (int i = 0; i < end; i += 8)
        {
            Vector256<float> a = Vector256.Create(slopes.Slice(i, 8)), b = Vector256.Create(offsets.Slice(i, 8));
            Vector256<float> v = Fma.MultiplyAdd(a, Vector256.Create(x), b);
            Vector256<float> keep = Avx.CompareLessThan(Vector256.Abs(v), Vector256.Create(100.0f));
            if (Avx.MoveMask(keep) == 0) continue;
            Vector256<float> vp = Fma.MultiplyAdd(a, Vector256.Create(xPlus), b);
            Vector256<float> vm = Fma.MultiplyAdd(a, Vector256.Create(xMinus), b);
            Vector256<float> scale = (2.0f / 3) * a;
            sum += Avx.And(keep, SignedAvx2(scale * (Vector256.Abs(v) + Vector256.Create(1.0f)), v));
            sumPlus += Avx.And(keep, SignedAvx2(scale * (Vector256.Abs(vp) + Vector256.Create(1.0f)), vp));
            sumMinus += Avx.And(keep, SignedAvx2(scale * (Vector256.Abs(vm) + Vector256.Create(1.0f)), vm));
        }

        sum.CopyTo(lanes);
        sumPlus.CopyTo(lanes[8..]);
        sumMinus.CopyTo(lanes[16..]);
        return end;
    }

    private static Vector256<float> SignedAvx2(Vector256<float> d, Vector256<float> v) =>
        Avx.Xor(d, Avx.And(Avx.CompareLessThan(v, Vector256<float>.Zero), Vector256.Create(-0.0f)));
}
