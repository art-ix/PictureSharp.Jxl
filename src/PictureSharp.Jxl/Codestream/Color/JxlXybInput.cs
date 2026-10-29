using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Color;

/// <summary>
/// Converts 8-bit or 16-bit samples in the image's colour space to XYB, the inverse of the XYB rendering of
/// <see cref="JxlColorOutput"/>: the transfer function (or ICC tone curves) to linear light, the primaries to linear
/// sRGB, then the opsin absorbance and cube root. Ported from libjxl v0.12.0 lib/jxl/enc_xyb.cc (ToXYB,
/// LinearRGBToXYB) and lib/jxl/cms/opsin_params.h (BSD-3-Clause); libjxl converts other spaces with a colour management
/// system, this class handles enumerated encodings and matrix/TRC or gray ICC profiles.
/// </summary>
internal sealed partial class JxlXybInput
{
    private const float M00 = 0.30f;
    private const float M02 = 0.078f;
    private const float M01 = 1.0f - M02 - M00;
    private const float M10 = 0.23f;
    private const float M12 = 0.078f;
    private const float M11 = 1.0f - M12 - M10;
    private const float M20 = 0.24342268924547819f;
    private const float M21 = 0.20476744424496821f;
    private const float M22 = 1.0f - M20 - M21;
    private const float Bias = 0.0037930732552754493f;

    private readonly float[][] linear;
    private readonly float[]? toSrgb;
    private readonly float[] absorbance = new float[9];
    private readonly float negativeBiasCbrt = -JxlCbrt.Cbrt(Bias);

    private JxlXybInput(float[][] linear, float[]? toSrgb, float intensityTarget)
    {
        this.linear = linear;
        this.toSrgb = toSrgb;
        ReadOnlySpan<float> matrix = [M00, M01, M02, M10, M11, M12, M20, M21, M22];
        float mul = intensityTarget / 255.0f;
        for (int i = 0; i < 9; i++)
        {
            absorbance[i] = matrix[i] * mul;
        }
    }

    /// <summary>
    /// The conversion of samples described by <paramref name="color"/> (or <paramref name="icc"/> when the colour
    /// encoding wants an ICC profile), or null when they cannot be converted: lookup-table ICC profiles and HLG.
    /// </summary>
    public static JxlXybInput? TryCreate(JxlColorEncoding color, JxlIccTarget? icc, float intensityTarget, int bitDepth = 8)
    {
        bool gray = color.ColorSpace == JxlColorSpace.Gray;
        if (color.WantIcc)
        {
            if (icc is null || icc.IsGray != gray)
            {
                return null;
            }

            float[][] curves = new float[icc.Curves.Length][];
            for (int c = 0; c < curves.Length; c++) curves[c] = Table(icc.Curves[c].Decode, bitDepth);
            float[]? matrix = gray ? null : JxlColorOutput.Multiply(JxlColorOutput.Invert(JxlColorOutput.SrgbToXyzD50()), icc.ToXyzD50);
            return new JxlXybInput(curves, matrix, intensityTarget);
        }

        if (color.Gamma == 0 && color.TransferFunction == JxlTransferFunction.Hlg)
        {
            return null;
        }

        float[] table = Table(ToLinear(color, intensityTarget), bitDepth);
        float[]? primaries = !gray && (color.Primaries != JxlPrimaries.Srgb || color.WhitePoint != JxlWhitePoint.D65)
            ? JxlColorOutput.Invert(JxlColorOutput.SrgbToOriginal(color))
            : null;
        return new JxlXybInput([table], primaries, intensityTarget);
    }

    /// <summary>
    /// Converts one row in place: <paramref name="x"/>, <paramref name="y"/> and <paramref name="b"/> hold the integer
    /// red, green and blue samples (gray: equal) on entry and X, Y and B on return.
    /// </summary>
    public void Convert(Span<float> x, Span<float> y, Span<float> b)
    {
        float[] lr = linear[0];
        float[] lg = linear[linear.Length == 3 ? 1 : 0];
        float[] lb = linear[^1];
        float[] m = absorbance;
        for (int i = ConvertAvx2(x, y, b); i < x.Length; i++)
        {
            float red = lr[(int)x[i]];
            float green = lg[(int)y[i]];
            float blue = lb[(int)b[i]];
            if (toSrgb is { } t)
            {
                (red, green, blue) = (t[0] * red + t[1] * green + t[2] * blue, t[3] * red + t[4] * green + t[5] * blue,
                    t[6] * red + t[7] * green + t[8] * blue);
            }

            float mixed0 = MathF.FusedMultiplyAdd(m[0], red, MathF.FusedMultiplyAdd(m[1], green, MathF.FusedMultiplyAdd(m[2], blue, Bias)));
            float mixed1 = MathF.FusedMultiplyAdd(m[3], red, MathF.FusedMultiplyAdd(m[4], green, MathF.FusedMultiplyAdd(m[5], blue, Bias)));
            float mixed2 = MathF.FusedMultiplyAdd(m[6], red, MathF.FusedMultiplyAdd(m[7], green, MathF.FusedMultiplyAdd(m[8], blue, Bias)));
            mixed0 = JxlCbrt.Cbrt(Math.Max(mixed0, 0)) + negativeBiasCbrt;
            mixed1 = JxlCbrt.Cbrt(Math.Max(mixed1, 0)) + negativeBiasCbrt;
            mixed2 = JxlCbrt.Cbrt(Math.Max(mixed2, 0)) + negativeBiasCbrt;
            x[i] = 0.5f * (mixed0 - mixed1);
            y[i] = 0.5f * (mixed0 + mixed1);
            b[i] = mixed2;
        }
    }

    /// <summary>The transfer function of <paramref name="color"/> to linear light (PQ relative to <paramref name="intensityTarget"/>).</summary>
    internal static Func<float, float> ToLinear(JxlColorEncoding color, float intensityTarget = 255)
    {
        float pqScale = 10000.0f / intensityTarget;
        return color.Gamma != 0
            ? Gamma(1.0f / (float)(color.Gamma * 1e-7))
            : color.TransferFunction switch
            {
                JxlTransferFunction.Linear => value => value,
                JxlTransferFunction.Bt709 => value => value < 0.081f ? value / 4.5f : MathF.Pow((value + 0.099f) / 1.099f, 1.0f / 0.45f),
                JxlTransferFunction.Dci => Gamma(1.0f / JxlColorEncoding.DciGamma),
                JxlTransferFunction.Pq => value => Pq(value) * pqScale,
                _ => Srgb,
            };
    }

    /// <summary>The inverse of <see cref="ToLinear"/> for gamma, linear, BT.709, DCI and sRGB curves.</summary>
    internal static Func<float, float> FromLinear(JxlColorEncoding color) =>
        color.Gamma != 0
            ? Gamma((float)(color.Gamma * 1e-7))
            : color.TransferFunction switch
            {
                JxlTransferFunction.Linear => value => value,
                JxlTransferFunction.Bt709 => value => value < 0.018f ? value * 4.5f : 1.099f * MathF.Pow(value, 0.45f) - 0.099f,
                JxlTransferFunction.Dci => Gamma(JxlColorEncoding.DciGamma),
                JxlTransferFunction.Srgb => value => value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - 0.055f,
                _ => throw new ArgumentOutOfRangeException(nameof(color), color.TransferFunction, "No inverse for this transfer function."),
            };

    private static float[] Table(Func<float, float> transfer, int bitDepth)
    {
        float[] table = new float[1 << bitDepth];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = transfer(i / (float)(table.Length - 1));
        }

        return table;
    }

    private static Func<float, float> Gamma(float exponent) => value => MathF.Pow(value, exponent);

    private static float Srgb(float value) => value <= 0.04045f ? value / 12.92f : (float)Math.Pow((value + 0.055) / 1.055, 2.4);

    // SMPTE ST 2084 EOTF, normalised to 10000 nits.
    private static float Pq(float value)
    {
        const double m1 = 2610.0 / 16384;
        const double m2 = 2523.0 / 4096 * 128;
        const double c1 = 3424.0 / 4096;
        const double c2 = 2413.0 / 4096 * 32;
        const double c3 = 2392.0 / 4096 * 32;
        double e = Math.Pow(value, 1 / m2);
        return (float)Math.Pow(Math.Max(e - c1, 0) / (c2 - c3 * e), 1 / m1);
    }
}
