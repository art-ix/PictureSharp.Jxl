using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Formats.Jxl.Codestream.Color;

// Same matrix order, fused cubes and rational polynomial as the 0.7.10 scalar path.
internal sealed partial class JxlColorOutput
{
    // The sRGB or linear transfer of XybToRgb; constants are loaded once instead of per vector.
    internal int ApplyXybAvx2(Span<float> x, Span<float> y, Span<float> b, bool srgb)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported) return 0;
        Vector256<float> cbrt0 = Vector256.Create(biasesCbrt[0]), cbrt1 = Vector256.Create(biasesCbrt[1]), cbrt2 = Vector256.Create(biasesCbrt[2]);
        Vector256<float> bias0 = Vector256.Create(biases[0]), bias1 = Vector256.Create(biases[1]), bias2 = Vector256.Create(biases[2]);
        float[] m = inverseMatrix;
        Vector256<float> m0 = Vector256.Create(m[0]), m1 = Vector256.Create(m[1]), m2 = Vector256.Create(m[2]);
        Vector256<float> m3 = Vector256.Create(m[3]), m4 = Vector256.Create(m[4]), m5 = Vector256.Create(m[5]);
        Vector256<float> m6 = Vector256.Create(m[6]), m7 = Vector256.Create(m[7]), m8 = Vector256.Create(m[8]);
        int end = x.Length & ~7;
        for (int i = 0; i < end; i += 8)
        {
            Vector256<float> vx = Vector256.Create(x.Slice(i, 8));
            Vector256<float> vy = Vector256.Create(y.Slice(i, 8));
            Vector256<float> vb = Vector256.Create(b.Slice(i, 8));
            Vector256<float> gr = (vy + vx) - cbrt0;
            Vector256<float> gg = (vy - vx) - cbrt1;
            Vector256<float> gb = vb - cbrt2;
            Vector256<float> mr = Fma.MultiplyAdd(gr * gr, gr, bias0);
            Vector256<float> mg = Fma.MultiplyAdd(gg * gg, gg, bias1);
            Vector256<float> mb = Fma.MultiplyAdd(gb * gb, gb, bias2);
            Vector256<float> r = Fma.MultiplyAdd(m2, mb, Fma.MultiplyAdd(m1, mg, m0 * mr));
            Vector256<float> g = Fma.MultiplyAdd(m5, mb, Fma.MultiplyAdd(m4, mg, m3 * mr));
            Vector256<float> bl = Fma.MultiplyAdd(m8, mb, Fma.MultiplyAdd(m7, mg, m6 * mr));
            if (srgb)
            {
                r = SrgbFromLinearAvx2(r);
                g = SrgbFromLinearAvx2(g);
                bl = SrgbFromLinearAvx2(bl);
            }

            r.CopyTo(x.Slice(i, 8));
            g.CopyTo(y.Slice(i, 8));
            bl.CopyTo(b.Slice(i, 8));
        }
        return end;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector256<float> SrgbFromLinearAvx2(Vector256<float> value)
    {
        Vector256<float> sign = Vector256.Create(-0.0f);
        Vector256<float> x = Avx.AndNot(sign, value);
        Vector256<float> root = Avx.Sqrt(x);
        ReadOnlySpan<float> p = SrgbP, q = SrgbQ;
        Vector256<float> numerator = Fma.MultiplyAdd(Vector256.Create(p[4]), root, Vector256.Create(p[3]));
        Vector256<float> denominator = Fma.MultiplyAdd(Vector256.Create(q[4]), root, Vector256.Create(q[3]));
        numerator = Fma.MultiplyAdd(numerator, root, Vector256.Create(p[2]));
        denominator = Fma.MultiplyAdd(denominator, root, Vector256.Create(q[2]));
        numerator = Fma.MultiplyAdd(numerator, root, Vector256.Create(p[1]));
        denominator = Fma.MultiplyAdd(denominator, root, Vector256.Create(q[1]));
        numerator = Fma.MultiplyAdd(numerator, root, Vector256.Create(p[0]));
        denominator = Fma.MultiplyAdd(denominator, root, Vector256.Create(q[0]));
        Vector256<float> magnitude = Avx.BlendVariable(x * 12.92f, numerator / denominator,
            Avx.CompareGreaterThan(x, Vector256.Create(0.0031308f)));
        return Avx.Or(Avx.AndNot(sign, magnitude), Avx.And(sign, value));
    }

    internal static int YCbCrToRgbAvx2(Span<float> cb, Span<float> luma, Span<float> cr)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported) return 0;
        int end = cb.Length & ~7;
        for (int i = 0; i < end; i += 8)
        {
            Vector256<float> c = Vector256.Create(cb.Slice(i, 8));
            Vector256<float> y = Vector256.Create(luma.Slice(i, 8)) + Vector256.Create(128.0f / 255);
            Vector256<float> r = Vector256.Create(cr.Slice(i, 8));
            Fma.MultiplyAdd(Vector256.Create(1.402f), r, y).CopyTo(cb.Slice(i, 8));
            Fma.MultiplyAdd(Vector256.Create(-0.299f * 1.402f / 0.587f), r,
                Fma.MultiplyAdd(Vector256.Create(-0.114f * 1.772f / 0.587f), c, y)).CopyTo(luma.Slice(i, 8));
            Fma.MultiplyAdd(Vector256.Create(1.772f), c, y).CopyTo(cr.Slice(i, 8));
        }
        return end;
    }
}
