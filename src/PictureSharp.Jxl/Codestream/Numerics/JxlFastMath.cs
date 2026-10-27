namespace PictureSharp.Formats.Jxl.Codestream.Numerics;

/// <summary>
/// The scalar approximations libjxl uses for powers, logarithms and transfer curves, with the same operation order
/// and fused multiply-adds as its vector code. Ported from libjxl v0.12.0 lib/jxl/base/fast_math-inl.h and
/// lib/jxl/base/rational_polynomial-inl.h (BSD-3-Clause).
/// </summary>
internal static class JxlFastMath
{
    private static readonly float[] Log2P = [(float)-1.8503833400518310E-06, (float)1.4287160470083755E+00, (float)7.4245873327820566E-01];
    private static readonly float[] Log2Q = [(float)9.9032814277590719E-01, (float)1.0096718572241148E+00, (float)1.7409343003366853E-01];

    public static float Fma(float a, float b, float c) => MathF.FusedMultiplyAdd(a, b, c);

    /// <summary>Evaluates p(x) / q(x) by Horner's scheme; the arrays hold coefficients from degree 0 up.</summary>
    public static float EvalRationalPolynomial(float x, ReadOnlySpan<float> p, ReadOnlySpan<float> q)
    {
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
}
