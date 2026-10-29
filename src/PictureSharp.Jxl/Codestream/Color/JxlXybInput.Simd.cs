using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Color;

// Convert eight pixels at a time: scalar table lookups, then the unfused primaries matrix, the fused opsin
// absorbance and the exact cube root of JxlCbrt.CbrtAvx2 (scalar Cbrt for the lanes it leaves).
internal sealed partial class JxlXybInput
{
    internal int ConvertAvx2(Span<float> x, Span<float> y, Span<float> b)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported) return 0;
        float[] lr = linear[0];
        float[] lg = linear[linear.Length == 3 ? 1 : 0];
        float[] lb = linear[^1];
        float[] m = absorbance;
        float[]? t = toSrgb;
        Vector256<float> m0 = Vector256.Create(m[0]), m1 = Vector256.Create(m[1]), m2 = Vector256.Create(m[2]);
        Vector256<float> m3 = Vector256.Create(m[3]), m4 = Vector256.Create(m[4]), m5 = Vector256.Create(m[5]);
        Vector256<float> m6 = Vector256.Create(m[6]), m7 = Vector256.Create(m[7]), m8 = Vector256.Create(m[8]);
        Vector256<float> bias = Vector256.Create(Bias), negativeBias = Vector256.Create(negativeBiasCbrt), half = Vector256.Create(0.5f);
        Span<float> samples = stackalloc float[24];
        int end = x.Length & ~7;
        for (int i = 0; i < end; i += 8)
        {
            ReadOnlySpan<float> xs = x.Slice(i, 8), ys = y.Slice(i, 8), bs = b.Slice(i, 8);
            for (int k = 0; k < 8; k++)
            {
                samples[k] = lr[(int)xs[k]];
                samples[8 + k] = lg[(int)ys[k]];
                samples[16 + k] = lb[(int)bs[k]];
            }

            Vector256<float> red = Vector256.Create(samples[..8]);
            Vector256<float> green = Vector256.Create(samples.Slice(8, 8));
            Vector256<float> blue = Vector256.Create(samples.Slice(16, 8));
            if (t is not null)
            {
                (red, green, blue) = (Vector256.Create(t[0]) * red + Vector256.Create(t[1]) * green + Vector256.Create(t[2]) * blue,
                    Vector256.Create(t[3]) * red + Vector256.Create(t[4]) * green + Vector256.Create(t[5]) * blue,
                    Vector256.Create(t[6]) * red + Vector256.Create(t[7]) * green + Vector256.Create(t[8]) * blue);
            }

            Vector256<float> mixed0 = Fma.MultiplyAdd(m0, red, Fma.MultiplyAdd(m1, green, Fma.MultiplyAdd(m2, blue, bias)));
            Vector256<float> mixed1 = Fma.MultiplyAdd(m3, red, Fma.MultiplyAdd(m4, green, Fma.MultiplyAdd(m5, blue, bias)));
            Vector256<float> mixed2 = Fma.MultiplyAdd(m6, red, Fma.MultiplyAdd(m7, green, Fma.MultiplyAdd(m8, blue, bias)));
            mixed0 = CbrtAvx2(mixed0) + negativeBias;
            mixed1 = CbrtAvx2(mixed1) + negativeBias;
            mixed2 = CbrtAvx2(mixed2) + negativeBias;
            (half * (mixed0 - mixed1)).CopyTo(x.Slice(i, 8));
            (half * (mixed0 + mixed1)).CopyTo(y.Slice(i, 8));
            mixed2.CopyTo(b.Slice(i, 8));
        }

        return end;
    }

    // JxlCbrt.Cbrt(Math.Max(value, 0)) per lane. maxps returns +0 for -0 and NaN; such lanes are redone in scalar code.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> CbrtAvx2(Vector256<float> value)
    {
        Vector256<float> clamped = Avx.Max(value, Vector256<float>.Zero);
        Vector128<float> lower = JxlCbrt.CbrtAvx2(clamped.GetLower(), out int lowerLanes);
        Vector128<float> upper = JxlCbrt.CbrtAvx2(clamped.GetUpper(), out int upperLanes);
        Vector256<float> result = Vector256.Create(lower, upper);
        int lanes = lowerLanes | upperLanes << 4;
        return lanes == 0 ? result : CbrtScalarLanes(value, result, lanes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector256<float> CbrtScalarLanes(Vector256<float> value, Vector256<float> result, int lanes)
    {
        for (int k = 0; k < 8; k++)
        {
            if ((lanes >> k & 1) != 0) result = result.WithElement(k, JxlCbrt.Cbrt(Math.Max(value.GetElement(k), 0)));
        }

        return result;
    }
}
