using System.Buffers.Binary;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Color;

/// <summary>
/// The enumerated colour encoding an ICC profile describes, for XYB images: libjxl's encoder stores such a profile as
/// the enumerated encoding instead of the ICC bytes, so that decoders can return pixels in it without a colour
/// management system (with an ICC profile they return linear sRGB). Ported from libjxl v0.12.0
/// lib/jxl/color_encoding_internal.cc (DecideIfWantICC) and lib/jxl/cms/jxl_cms.cc (JxlCmsSetFieldsFromICC,
/// UnadaptedWhitePoint, IdentifyPrimaries, DetectTransferFunction), BSD-3-Clause; libjxl compares the profiles with
/// skcms, this class compares the colourant matrix and the tone curves at the 8-bit sample values.
/// </summary>
internal static class JxlIccEncoding
{
    // ApproxEq of lib/jxl/cms/color_encoding_cms.h.
    private const double Tolerance = 1e-3;
    // Matrix entries may differ by the s15Fixed16 rounding of the profile and of the chad tag.
    private const float MatrixTolerance = 2e-3f;
    // Every 8-bit sample must map to the same linear value within half a code value of the encoded scale.
    private const float CurveTolerance = 0.5f / 255;

    /// <summary>
    /// The enumerated encoding equivalent to the matrix/TRC or gray <paramref name="icc"/>, or null when there is none
    /// that this encoder converts to XYB (PQ and HLG curves keep the profile).
    /// </summary>
    public static JxlColorEncoding? TryEnumerate(ReadOnlySpan<byte> icc, bool gray)
    {
        if (icc.Length < 68 || JxlIccTarget.TryCreate(icc, gray) is not { MediaWhite: { } white } target)
        {
            return null;
        }

        // Some files store the intent little-endian; libjxl takes the smaller reading.
        uint intent = Math.Min(BinaryPrimitives.ReadUInt32BigEndian(icc[64..]), BinaryPrimitives.ReadUInt32LittleEndian(icc[64..]));
        if (intent > 3)
        {
            return null;
        }

        float[]? unadapt = target.Adaptation is { } chad ? Invert(chad) : null;
        JxlXy? whiteXy = Xy(unadapt is null ? white : Multiply3(unadapt, white));
        if (whiteXy is not { } wp)
        {
            return null;
        }

        var encoding = new JxlColorEncoding
        {
            ColorSpace = gray ? JxlColorSpace.Gray : JxlColorSpace.Rgb,
            RenderingIntent = (JxlRenderingIntent)intent,
        };
        encoding = WithWhitePoint(encoding, wp);
        if (!gray)
        {
            unadapt ??= Invert(Adapt(wp));
            if (unadapt is null)
            {
                return null;
            }

            float[] m = target.ToXyzD50;
            JxlXy?[] primaries = [.. Enumerable.Range(0, 3).Select(i => Xy(Multiply3(unadapt, [m[i], m[3 + i], m[6 + i]])))];
            if (primaries is not [{ } red, { } green, { } blue])
            {
                return null;
            }

            encoding = WithPrimaries(encoding, new JxlPrimariesXy(red, green, blue));
            if (!SameMatrix(encoding, m))
            {
                return null;
            }
        }

        return DetectTransferFunction(encoding, target.Curves);
    }

    // The gamma when every curve is the same power function, then the enumerated curves (DetectTransferFunction).
    private static JxlColorEncoding? DetectTransferFunction(JxlColorEncoding encoding, JxlToneCurve[] curves)
    {
        if (curves.Select(curve => curve.Exponent).Distinct().ToArray() is [float exponent]
            && exponent is >= 1 and <= JxlColorEncoding.MaxInverseGamma)
        {
            double gamma = 1.0 / exponent;
            JxlColorEncoding candidate = Approx(gamma, 1.0) ? encoding with { TransferFunction = JxlTransferFunction.Linear }
                : Approx(gamma, JxlColorEncoding.DciGamma) ? encoding with { TransferFunction = JxlTransferFunction.Dci }
                : encoding with { Gamma = (uint)Math.Round(gamma * JxlColorEncoding.GammaScale) };
            if (SameCurves(candidate, curves)) return candidate;
        }

        foreach (JxlTransferFunction transfer in (ReadOnlySpan<JxlTransferFunction>)
            [JxlTransferFunction.Bt709, JxlTransferFunction.Linear, JxlTransferFunction.Srgb, JxlTransferFunction.Dci])
        {
            JxlColorEncoding candidate = encoding with { TransferFunction = transfer };
            if (SameCurves(candidate, curves)) return candidate;
        }

        return null;
    }

    // Each 8-bit sample decodes to the same linear value, compared on the encoded scale of the candidate's curve.
    private static bool SameCurves(JxlColorEncoding candidate, JxlToneCurve[] curves)
    {
        Func<float, float> transfer = JxlXybInput.ToLinear(candidate);
        Func<float, float> inverse = JxlXybInput.FromLinear(candidate);
        foreach (JxlToneCurve curve in curves)
        {
            for (int i = 0; i < 256; i++)
            {
                float value = i / 255.0f;
                if (Math.Abs(inverse(curve.Decode(value)) - inverse(transfer(value))) > CurveTolerance) return false;
            }
        }

        return true;
    }

    // The candidate's linear RGB → XYZ D50 (Bradford-adapted, as libjxl creates profiles) against the colourants.
    private static bool SameMatrix(JxlColorEncoding candidate, float[] toXyzD50)
    {
        float[] expected = JxlColorOutput.Multiply(JxlColorOutput.SrgbToXyzD50(), JxlColorOutput.Invert(JxlColorOutput.SrgbToOriginal(candidate)));
        return expected.Zip(toXyzD50).All(pair => Math.Abs(pair.First - pair.Second) <= MatrixTolerance);
    }

    private static JxlColorEncoding WithWhitePoint(JxlColorEncoding encoding, JxlXy xy)
    {
        foreach (JxlWhitePoint named in (ReadOnlySpan<JxlWhitePoint>)[JxlWhitePoint.D65, JxlWhitePoint.E, JxlWhitePoint.Dci])
        {
            if (Approx(JxlColorEncoding.StandardXy(named)!.Value, xy)) return encoding with { WhitePoint = named };
        }

        return encoding with { WhitePoint = JxlWhitePoint.Custom, CustomWhitePoint = JxlChromaticity.From(xy) };
    }

    private static JxlColorEncoding WithPrimaries(JxlColorEncoding encoding, JxlPrimariesXy xy)
    {
        foreach (JxlPrimaries named in (ReadOnlySpan<JxlPrimaries>)[JxlPrimaries.Srgb, JxlPrimaries.Bt2100, JxlPrimaries.P3])
        {
            JxlPrimariesXy standard = JxlColorEncoding.StandardXy(named)!.Value;
            if (Approx(standard.Red, xy.Red) && Approx(standard.Green, xy.Green) && Approx(standard.Blue, xy.Blue))
            {
                return encoding with { Primaries = named };
            }
        }

        return encoding with
        {
            Primaries = JxlPrimaries.Custom,
            CustomPrimaries = (JxlChromaticity.From(xy.Red), JxlChromaticity.From(xy.Green), JxlChromaticity.From(xy.Blue)),
        };
    }

    private static bool Approx(double a, double b) => Math.Abs(a - b) <= Tolerance;

    private static bool Approx(JxlXy a, JxlXy b) => Approx(a.X, b.X) && Approx(a.Y, b.Y);

    private static JxlXy? Xy(float[] xyz)
    {
        double sum = (double)xyz[0] + xyz[1] + xyz[2];
        return sum > 0 && xyz[1] > 0 ? new JxlXy(xyz[0] / sum, xyz[1] / sum) : null;
    }

    private static float[] Adapt(JxlXy white)
    {
        try
        {
            return JxlColorOutput.AdaptToXyzD50((float)white.X, (float)white.Y);
        }
        catch (ImageFormatException)
        {
            return [0, 0, 0, 0, 0, 0, 0, 0, 0];
        }
    }

    private static float[]? Invert(float[] m)
    {
        try
        {
            return JxlColorOutput.Invert(m);
        }
        catch (ImageFormatException)
        {
            return null;
        }
    }

    private static float[] Multiply3(float[] m, float[] v) =>
        [.. Enumerable.Range(0, 3).Select(row => (float)((double)m[row * 3] * v[0] + (double)m[row * 3 + 1] * v[1] + (double)m[row * 3 + 2] * v[2]))];
}
