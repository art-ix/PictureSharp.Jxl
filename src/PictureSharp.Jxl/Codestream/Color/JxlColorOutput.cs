using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Color;

/// <summary>
/// The colour stages at the end of the render pipeline: YCbCr to RGB, or XYB to linear RGB in the image's colour
/// space followed by its transfer function. Ported from libjxl v0.12.0 lib/jxl/dec_xyb.cc, dec_xyb-inl.h,
/// render_pipeline/stage_xyb.cc, stage_ycbcr.cc, stage_from_linear.cc, cms/transfer_functions-inl.h,
/// cms/jxl_cms_internal.h, cms/opsin_params.h and base/matrix_ops.h (BSD-3-Clause).
/// </summary>
/// <remarks>
/// XYB images are rendered in their enumerated colour space, as libjxl does without a colour management system.
/// With an ICC profile libjxl converts linear sRGB to the profile with a colour management system; this decoder does
/// that for matrix/TRC and gray profiles (<see cref="JxlIccTarget"/>) and otherwise renders sRGB. On request
/// (<see cref="JxlColorTarget.Srgb"/>) every XYB image is rendered as sRGB, as libjxl does for an sRGB output encoding.
/// </remarks>
internal sealed partial class JxlColorOutput
{
    private static ReadOnlySpan<float> SrgbP => [(float)-5.135152395e-04, (float)5.287254571e-03, (float)3.903842876e-01, (float)1.474205315e+00, (float)7.352629620e-01];
    private static ReadOnlySpan<float> SrgbQ => [(float)1.004519624e-02, (float)3.036675394e-01, (float)1.340816930e+00, (float)9.258482155e-01, (float)2.424867759e-02];
    private static ReadOnlySpan<float> PqP => [(float)1.351392e-02, (float)-1.095778e+00, (float)5.522776e+01, (float)1.492516e+02, (float)4.838434e+01];
    private static ReadOnlySpan<float> PqQ => [(float)1.012416e+00, (float)2.016708e+01, (float)9.263710e+01, (float)1.120607e+02, (float)2.590418e+01];
    private static ReadOnlySpan<float> PqPLow => [(float)9.863406e-06, (float)3.881234e-01, (float)1.352821e+02, (float)6.889862e+04, (float)-2.864824e+05];
    private static ReadOnlySpan<float> PqQLow => [(float)3.371868e+01, (float)1.477719e+03, (float)1.608477e+04, (float)-4.389884e+04, (float)-2.072546e+05];

    private readonly JxlColorTransform transform;
    private readonly float[] inverseMatrix = new float[9];
    private readonly float[] biases = new float[3];
    private readonly float[] biasesCbrt = new float[3];
    private readonly JxlTransferFunction transfer;
    private readonly float inverseGamma;
    private readonly float pqScale;
    private readonly JxlToneCurve[]? curves;

    public JxlColorOutput(JxlImageMetadata metadata, JxlCustomTransformData transformData, JxlColorTransform transform,
        JxlColorTarget? target = null)
    {
        this.transform = transform;
        if (transform != JxlColorTransform.Xyb)
        {
            return;
        }

        JxlColorEncoding color = DescribeOutput(metadata, transform, target);
        bool gray = color.ColorSpace == JxlColorSpace.Gray;
        JxlIccTarget? icc = color.WantIcc ? target?.Icc : null;
        curves = icc?.Curves;

        if (color.Gamma == 0 && color.TransferFunction == JxlTransferFunction.Hlg)
        {
            throw JxlDescriptor.Unsupported("HLG (HDR) lossy images are not decoded yet.");
        }

        transfer = color.Gamma != 0 ? JxlTransferFunction.Unknown : color.TransferFunction;
        inverseGamma = color.Gamma != 0 ? (float)(color.Gamma * 1e-7) : JxlColorEncoding.DciGamma;
        float intensity = metadata.ToneMapping.IntensityTarget;
        pqScale = intensity * (1.0f / 10000.0f);
        float[] inverse = [.. transformData.EffectiveOpsinInverseMatrix];
        if (!gray && icc is not null)
        {
            inverse = Multiply(Multiply(icc.FromXyzD50, SrgbToXyzD50()), inverse);
        }
        else if (!gray && (color.Primaries != JxlPrimaries.Srgb || color.WhitePoint != JxlWhitePoint.D65))
        {
            inverse = Multiply(SrgbToOriginal(color), inverse);
        }

        if (gray)
        {
            ReadOnlySpan<float> luminances = [0.2126f, 0.7152f, 0.0722f];
            float[] toLuma = [.. luminances, .. luminances, .. luminances];
            inverse = Multiply(toLuma, inverse);
        }

        for (int i = 0; i < 9; i++)
        {
            inverseMatrix[i] = inverse[i] * (255.0f / intensity);
        }

        IReadOnlyList<float> opsinBiases = transformData.EffectiveOpsinBiases;
        for (int c = 0; c < 3; c++)
        {
            biases[c] = opsinBiases[c];
            biasesCbrt[c] = MathF.Cbrt(opsinBiases[c]);
        }
    }

    /// <summary>
    /// The encoding of rendered samples: the image's own, which for XYB with an ICC profile requires a usable ICC
    /// target, or sRGB (<see cref="JxlColorEncoding.WantIcc"/> then is false) as the fallback or on request.
    /// </summary>
    public static JxlColorEncoding DescribeOutput(JxlImageMetadata metadata, JxlColorTransform transform, JxlColorTarget? target = null)
    {
        if (transform != JxlColorTransform.Xyb) return metadata.Color;
        JxlColorEncoding color = metadata.Color;
        bool gray = color.ColorSpace == JxlColorSpace.Gray;
        if (color.WantIcc && target?.Icc is { } icc && icc.IsGray == gray)
        {
            return color;
        }

        bool enumerated = target?.IsSrgb != true && !color.WantIcc && (color.Gamma != 0 || color.TransferFunction != JxlTransferFunction.Unknown)
            && (!gray || color.WhitePoint == JxlWhitePoint.D65);
        if (!enumerated)
        {
            // Rendered as sRGB (libjxl: linear sRGB, converted by a colour management system).
            color = JxlColorEncoding.Srgb with { ColorSpace = gray ? JxlColorSpace.Gray : JxlColorSpace.Rgb };
        }

        return color;
    }

    /// <summary>Converts the three colour planes in place to the output colour space and transfer function.</summary>
    public void Apply(JxlPlane[] planes, CancellationToken cancellationToken = default)
    {
        if (transform is not (JxlColorTransform.YCbCr or JxlColorTransform.Xyb))
        {
            return;
        }

        // Direct calls per sample (no delegate) so the conversion inlines into the row loop.
        for (int y = 0; y < planes[0].Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<float> r0 = planes[0].Row(y);
            Span<float> r1 = planes[1].Row(y)[..r0.Length];
            Span<float> r2 = planes[2].Row(y)[..r0.Length];
            if (transform == JxlColorTransform.YCbCr)
            {
                for (int x = YCbCrToRgbAvx2(r0, r1, r2); x < r0.Length; x++)
                {
                    YCbCrToRgb(ref r0[x], ref r1[x], ref r2[x]);
                }
            }
            else if (curves is null && transfer == JxlTransferFunction.Srgb)
            {
                for (int x = ApplyXybAvx2(r0, r1, r2, true); x < r0.Length; x++)
                {
                    XybToLinear(ref r0[x], ref r1[x], ref r2[x]);
                    r0[x] = SrgbFromLinear(r0[x]);
                    r1[x] = SrgbFromLinear(r1[x]);
                    r2[x] = SrgbFromLinear(r2[x]);
                }
            }
            else
            {
                int done = curves is null && transfer == JxlTransferFunction.Linear ? ApplyXybAvx2(r0, r1, r2, false) : 0;
                for (int x = done; x < r0.Length; x++)
                {
                    XybToRgb(ref r0[x], ref r1[x], ref r2[x]);
                }
            }
        }
    }

    // Full-range BT.601 as defined by JFIF; the channels arrive as Cb, Y, Cr.
    private static void YCbCrToRgb(ref float cb, ref float luma, ref float cr)
    {
        float y = luma + 128.0f / 255;
        float red = JxlFastMath.Fma(1.402f, cr, y);
        float green = JxlFastMath.Fma(-0.299f * 1.402f / 0.587f, cr, JxlFastMath.Fma(-0.114f * 1.772f / 0.587f, cb, y));
        float blue = JxlFastMath.Fma(1.772f, cb, y);
        (cb, luma, cr) = (red, green, blue);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void XybToLinear(ref float x, ref float y, ref float b)
    {
        float gammaR = (y + x) - biasesCbrt[0];
        float gammaG = (y - x) - biasesCbrt[1];
        float gammaB = b - biasesCbrt[2];
        float mixedR = JxlFastMath.Fma(gammaR * gammaR, gammaR, biases[0]);
        float mixedG = JxlFastMath.Fma(gammaG * gammaG, gammaG, biases[1]);
        float mixedB = JxlFastMath.Fma(gammaB * gammaB, gammaB, biases[2]);
        float[] m = inverseMatrix;
        float red = m[0] * mixedR;
        float green = m[3] * mixedR;
        float blue = m[6] * mixedR;
        red = JxlFastMath.Fma(m[1], mixedG, red);
        green = JxlFastMath.Fma(m[4], mixedG, green);
        blue = JxlFastMath.Fma(m[7], mixedG, blue);
        red = JxlFastMath.Fma(m[2], mixedB, red);
        green = JxlFastMath.Fma(m[5], mixedB, green);
        blue = JxlFastMath.Fma(m[8], mixedB, blue);
        (x, y, b) = (red, green, blue);
    }

    private void XybToRgb(ref float x, ref float y, ref float b)
    {
        XybToLinear(ref x, ref y, ref b);
        float red = x, green = y, blue = b;
        if (curves is not null)
        {
            (x, y, b) = (curves[0].Encode(red), curves[curves.Length == 3 ? 1 : 0].Encode(green), curves[^1].Encode(blue));
            return;
        }

        (x, y, b) = (FromLinear(red), FromLinear(green), FromLinear(blue));
    }

    private float FromLinear(float value) => transfer switch
    {
        JxlTransferFunction.Linear => value,
        JxlTransferFunction.Srgb => SrgbFromLinear(value),
        JxlTransferFunction.Pq => PqFromLinear(value),
        JxlTransferFunction.Bt709 => value <= (float)0.018 ? (float)4.5 * value
            : JxlFastMath.Fma((float)1.099, JxlFastMath.FastPowf(value, (float)0.45), (float)-0.099),
        _ => value <= 1e-5f ? 0 : JxlFastMath.FastPowf(value, inverseGamma),
    };

    // TF_SRGB::EncodedFromDisplay, odd-symmetric.
    private static float SrgbFromLinear(float value)
    {
        float x = Math.Abs(value);
        float magnitude = x > 0.0031308f ? JxlFastMath.EvalRationalPolynomial(MathF.Sqrt(x), SrgbP, SrgbQ) : x * 12.92f;
        return MathF.CopySign(magnitude, value);
    }

    private float PqFromLinear(float value)
    {
        float x = Math.Abs(value);
        float root = MathF.Sqrt(MathF.Sqrt(x * pqScale));
        float magnitude = x < 1e-4f ? JxlFastMath.EvalRationalPolynomial(root, PqPLow, PqQLow) : JxlFastMath.EvalRationalPolynomial(root, PqP, PqQ);
        return MathF.CopySign(magnitude, value);
    }

    // The linear sRGB → linear original-space matrix through XYZ D50 (OutputEncodingInfo::SetColorEncoding).
    // CIE xy of the sRGB primaries as libjxl stores them (red, green, blue).
    private static ReadOnlySpan<float> SrgbPrimaries =>
        [(float)0.639998686, (float)0.330010138, (float)0.300003784, (float)0.600003357, (float)0.150002046, (float)0.059997204];

    internal static float[] SrgbToXyzD50()
    {
        (float wx, float wy) = ((float)JxlColorEncoding.D65.X, (float)JxlColorEncoding.D65.Y);
        return Multiply(AdaptToXyzD50(wx, wy), PrimariesToXyz(SrgbPrimaries, wx, wy));
    }

    internal static float[] SrgbToOriginal(JxlColorEncoding color)
    {
        float[] srgbToXyzD50 = SrgbToXyzD50();
        (float wx, float wy) = ((float)color.WhitePointXy.X, (float)color.WhitePointXy.Y);
        (JxlXy red, JxlXy green, JxlXy blue) = color.PrimariesXy;
        ReadOnlySpan<float> primaries = color.Primaries == JxlPrimaries.Srgb ? SrgbPrimaries
            : [(float)red.X, (float)red.Y, (float)green.X, (float)green.Y, (float)blue.X, (float)blue.Y];
        float[] originalToXyz = PrimariesToXyz(primaries, wx, wy);
        float[] xyzD50ToOriginal = Invert(Multiply(AdaptToXyzD50(wx, wy), originalToXyz));
        return Multiply(xyzD50ToOriginal, srgbToXyzD50);
    }

    // The primaries as red, green and blue xy pairs.
    private static float[] PrimariesToXyz(ReadOnlySpan<float> xy, float wx, float wy)
    {
        float[] w = WhiteXyz(wx, wy);
        (float rx, float ry, float gx, float gy, float bx, float by) = (xy[0], xy[1], xy[2], xy[3], xy[4], xy[5]);
        float[] primaries = [rx, gx, bx, ry, gy, by, 1.0f - rx - ry, 1.0f - gx - gy, 1.0f - bx - by];
        float[] xyz = Multiply3(Invert(primaries), w);
        return Multiply(primaries, [xyz[0], 0, 0, 0, xyz[1], 0, 0, 0, xyz[2]]);
    }

    internal static float[] AdaptToXyzD50(float wx, float wy)
    {
        float[] bradford = [0.8951f, 0.2664f, -0.1614f, -0.7502f, 1.7135f, 0.0367f, 0.0389f, -0.0685f, 1.0296f];
        float[] bradfordInverse = [0.9869929f, -0.1470543f, 0.1599627f, 0.4323053f, 0.5183603f, 0.0492912f, -0.0085287f, 0.0400428f, 0.9684867f];
        float[] lms = Multiply3(bradford, WhiteXyz(wx, wy));
        float[] lms50 = Multiply3(bradford, [0.96422f, 1.0f, 0.82521f]);
        if (lms[0] == 0 || lms[1] == 0 || lms[2] == 0)
        {
            throw JxlDescriptor.Corrupt("Invalid white point.");
        }

        float[] a = [lms50[0] / lms[0], 0, 0, 0, lms50[1] / lms[1], 0, 0, 0, lms50[2] / lms[2]];
        return a.All(float.IsFinite) ? Multiply(bradfordInverse, Multiply(a, bradford)) : throw JxlDescriptor.Corrupt("Invalid white point.");
    }

    private static float[] WhiteXyz(float wx, float wy)
    {
        float[] w = (wx >= 0 && wx <= 1 && wy > 0 && wy <= 1) ? [wx / wy, 1.0f, (1.0f - wx - wy) / wy] : [float.NaN];
        return w.Length == 3 && float.IsFinite(w[0]) && float.IsFinite(w[2]) ? w : throw JxlDescriptor.Corrupt("Invalid white point.");
    }

    // Matrix products accumulate in double precision and round to float, as libjxl's Mul3x3Matrix.
    internal static float[] Multiply(float[] a, float[] b)
    {
        float[] c = new float[9];
        for (int x = 0; x < 3; x++)
        {
            for (int y = 0; y < 3; y++)
            {
                c[y * 3 + x] = (float)(a[y * 3] * (double)b[x] + a[y * 3 + 1] * (double)b[3 + x] + a[y * 3 + 2] * (double)b[6 + x]);
            }
        }

        return c;
    }

    private static float[] Multiply3(float[] a, float[] v)
    {
        float[] c = new float[3];
        for (int y = 0; y < 3; y++)
        {
            double e = 0;
            for (int x = 0; x < 3; x++)
            {
                e += (double)a[y * 3 + x] * v[x];
            }

            c[y] = (float)e;
        }

        return c;
    }

    internal static float[] Invert(float[] m)
    {
        double[] t = new double[9];
        double determinant = JxlMatrix3.Adjugate([.. m.Select(value => (double)value)], t);
        if (Math.Abs(determinant) < 1e-10)
        {
            throw JxlDescriptor.Corrupt("Singular colour matrix.");
        }

        double inverse = 1.0 / determinant;
        return [.. t.Select(value => (float)(value * inverse))];
    }
}
