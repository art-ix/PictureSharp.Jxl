using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.Jxl.Codestream.Numerics;

/// <summary>
/// The scalar approximations libjxl uses for powers, logarithms and transfer curves, with the same operation order
/// and fused multiply-adds as its vector code. Ported from libjxl v0.12.0 lib/jxl/base/fast_math-inl.h and
/// lib/jxl/base/rational_polynomial-inl.h (BSD-3-Clause).
/// </summary>
internal static class JxlFastMath
{
    private static ReadOnlySpan<float> Log2P => [(float)-1.8503833400518310E-06, (float)1.4287160470083755E+00, (float)7.4245873327820566E-01];
    private static ReadOnlySpan<float> Log2Q => [(float)9.9032814277590719E-01, (float)1.0096718572241148E+00, (float)1.7409343003366853E-01];

    public static float Fma(float a, float b, float c) => MathF.FusedMultiplyAdd(a, b, c);

    /// <summary>Evaluates p(x) / q(x) by Horner's scheme; the arrays hold coefficients from degree 0 up.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EvalRationalPolynomial(float x, ReadOnlySpan<float> p, ReadOnlySpan<float> q)
    {
        // Degree 4 over 4 (the transfer functions), unrolled; with constant tables the JIT keeps only this branch.
        if (p.Length == 5 && q.Length == 5)
        {
            float numerator = Fma(Fma(Fma(Fma(p[4], x, p[3]), x, p[2]), x, p[1]), x, p[0]);
            float denominator = Fma(Fma(Fma(Fma(q[4], x, q[3]), x, q[2]), x, q[1]), x, q[0]);
            return numerator / denominator;
        }

        float yp = p[^1];
        for (int i = p.Length - 2; i >= 0; i--)
        {
            yp = Fma(yp, x, p[i]);
        }

        float yq = q[^1];
        for (int i = q.Length - 2; i >= 0; i--)
        {
            yq = Fma(yq, x, q[i]);
        }

        return yp / yq;
    }

    /// <summary>log2(x) for positive x, L1 error about 3.9e-6.</summary>
    public static float FastLog2f(float x)
    {
        int bits = BitConverter.SingleToInt32Bits(x);
        int exponent = (bits - 0x3f2aaaab) >> 23;
        float mantissa = BitConverter.Int32BitsToSingle(bits - (exponent << 23));
        return EvalRationalPolynomial(mantissa - 1.0f, Log2P, Log2Q) + exponent;
    }

    /// <summary>2^x, relative error about 3e-7.</summary>
    public static float FastPow2f(float x)
    {
        float floor = MathF.Floor(x);
        float exponent = BitConverter.Int32BitsToSingle(((int)floor + 127) << 23);
        float fraction = x - floor;
        float numerator = fraction + (float)1.01749063e+01;
        numerator = Fma(numerator, fraction, (float)4.88687798e+01);
        numerator = Fma(numerator, fraction, (float)9.85506591e+01);
        numerator *= exponent;
        float denominator = Fma(fraction, (float)2.10242958e-01, (float)-2.22328856e-02);
        denominator = Fma(denominator, fraction, (float)-1.94414990e+01);
        denominator = Fma(denominator, fraction, (float)9.85506633e+01);
        return numerator / denominator;
    }

    /// <summary>base^exponent for positive bases, relative error about 3e-5.</summary>
    public static float FastPowf(float value, float exponent) => FastPow2f(FastLog2f(value) * exponent);

    /// <summary>cos(x) for finite x, L1 error about 7e-5 (used by splines).</summary>
    public static float FastCosf(float x)
    {
        // libjxl's kPi is a double: the constants are rounded once from double.
        const float Pi = (float)Math.PI;
        const float Pi2 = (float)(Math.PI * 2.0);
        const float Pi2Inverse = (float)(0.5 / Math.PI);
        const float PiHalf = (float)(Math.PI / 2.0);
        // Range reduction to [0, 2pi), then [0, pi], then [0, pi/2].
        float xModPi2 = x - MathF.Floor(x * Pi2Inverse) * Pi2;
        float xPi = MathF.Min(xModPi2, Pi2 - xModPi2);
        bool abovePiHalf = xPi >= PiHalf;
        float xPiHalf = abovePiHalf ? Pi - xPi : xPi;
        // Taylor-like approximation on x/4, scaled by 2^0.75, then two angle duplications.
        float xs = xPiHalf * 0.25f;
        float x2 = xs * xs;
        float x4 = x2 * x2;
        float prescaled = Fma(x4, 0.06960438f, Fma(x2, -0.84087373f, 1.68179268f));
        float scale1 = Fma(prescaled, prescaled, -1.414213562f);
        float scale2 = Fma(scale1, scale1, -1.0f);
        return abovePiHalf ? -scale2 : scale2;
    }

    /// <summary>erf(x), L1 error about 7e-4 (used by splines).</summary>
    public static float FastErff(float x)
    {
        float absX = MathF.Abs(x);
        // 1 - 1 / ((((x * a + b) * x + c) * x + d) * x + 1)^4
        float denom1 = Fma(absX, 7.77394369e-02f, 2.05260015e-04f);
        float denom2 = Fma(denom1, absX, 2.32120216e-01f);
        float denom3 = Fma(denom2, absX, 2.77820801e-01f);
        float denom4 = Fma(denom3, absX, 1.0f);
        float denom5 = denom4 * denom4;
        float invDenom5 = 1.0f / denom5;
        float result = Fma(-invDenom5, invDenom5, 1.0f);
        return x <= 0 ? -result : result;
    }
}
