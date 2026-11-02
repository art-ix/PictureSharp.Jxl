using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// The noise LUT of a simulated full-frame sensor at an ISO setting, as <c>cjxl --photon_noise_iso</c> writes it.
/// Ported from libjxl v0.12.0 lib/jxl/enc_photon_noise.cc and enc_noise.cc (EncodeNoise; BSD-3-Clause).
/// </summary>
internal static class JxlPhotonNoise
{
    private const float PhotonsPerLxSPerUm2 = 11260;
    private const float EffectiveQuantumEfficiency = 0.20f;
    private const float PhotoResponseNonUniformity = 0.005f;
    private const float InputReferredReadNoise = 3;
    private const float SensorAreaUm2 = 36000.0f * 24000;
    private const float OpsinAbsorbanceBias = 0.0037930732552754493f;
    // kNoiseLutMax: the largest value that still rounds to 1023 tenth-bit steps.
    private const float NoiseLutMax = 1023.4999f / JxlNoiseParameters.Precision;

    /// <summary>The LUT for a frame of <paramref name="xSize"/> × <paramref name="ySize"/> pixels (SimulatePhotonNoise).</summary>
    public static float[] Simulate(long xSize, long ySize, float iso)
    {
        float opsinBiasCbrt = MathF.Cbrt(OpsinAbsorbanceBias);
        // Focal plane exposure for 18% of the default intensity target, in lx·s (ISO = 10 lx·s ÷ H).
        float h18 = 10 / iso;
        float pixelAreaUm2 = SensorAreaUm2 / (xSize * ySize);
        float electronsPerPixel18 = EffectiveQuantumEfficiency * PhotonsPerLxSPerUm2 * h18 * pixelAreaUm2;
        float[] lut = new float[JxlNoiseParameters.Points];
        for (int i = 0; i < lut.Length; i++)
        {
            float scaledIndex = i / (JxlNoiseParameters.Points - 2.0f);
            // scaledIndex is used for XYB = (0, 2·scaledIndex, 2·scaledIndex).
            float y = 2 * scaledIndex;
            float linear = MathF.Max(0.0f, Cube(y - opsinBiasCbrt) + OpsinAbsorbanceBias);
            float electronsPerPixel = electronsPerPixel18 * (linear / 0.18f);
            // Quadrature sum of read noise, photon shot noise and photo response non-uniformity, in electrons rms.
            float noise = MathF.Sqrt(InputReferredReadNoise * InputReferredReadNoise + electronsPerPixel
                + Square(PhotoResponseNonUniformity * electronsPerPixel));
            float linearNoise = noise * (0.18f / electronsPerPixel18);
            float opsinDerivative = (1.0f / 3) / Square(MathF.Cbrt(linear - OpsinAbsorbanceBias));
            float opsinNoise = linearNoise * opsinDerivative;
            // Normalized by the decoder's norm_const, the sum of two noise planes and their standard deviation.
            lut[i] = Math.Clamp(opsinNoise / (0.22f * MathF.Sqrt(2.0f) * 1.13f), 0.0f, NoiseLutMax);
        }

        return lut;
    }

    /// <summary>Writes the eight entries as 10-bit fixed point (EncodeNoise).</summary>
    public static void Write(JxlBitWriter writer, float[] lut)
    {
        foreach (float value in lut)
        {
            writer.Write(10, (ulong)MathF.Round(value * JxlNoiseParameters.Precision, MidpointRounding.AwayFromZero));
        }
    }

    private static float Square(float x) => x * x;

    private static float Cube(float x) => x * x * x;
}
