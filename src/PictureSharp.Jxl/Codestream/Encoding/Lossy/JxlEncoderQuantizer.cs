namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// The global quantization scale and DC quantizer of a frame and the conversion of a float quantization field to the
/// coded 1–256 values. Ported from libjxl v0.12.0 lib/jxl/quantizer.cc and quantizer.h (BSD-3-Clause), with the
/// default DC quantization weights.
/// </summary>
internal sealed class JxlEncoderQuantizer
{
    public const int GlobalScaleDenominator = 1 << 16;
    public const int QuantMax = 256;
    private const int GlobalScaleNumerator = 4096;
    private const int DefaultQuant = 64;

    /// <summary>The default DC quantization weights (kDCQuant) and their reciprocals.</summary>
    private static ReadOnlySpan<float> InverseDcWeights => [4096.0f, 512.0f, 256.0f];

    public int GlobalScale { get; private set; } = GlobalScaleDenominator / DefaultQuant;

    public int QuantDc { get; private set; } = DefaultQuant;

    /// <summary>global_scale / 65536, the scale of every AC quantization step.</summary>
    public float Scale { get; private set; }

    public float InverseGlobalScale { get; private set; }

    public JxlEncoderQuantizer() => Recompute();

    /// <summary>The dequantization multiplier of quantization value <paramref name="quant"/> (inv_quant_ac).</summary>
    public float InverseQuantAc(int quant) => InverseGlobalScale / quant;

    /// <summary>The dequantized DC step of channel <paramref name="c"/> (X, Y, B): MulDC.</summary>
    public float DcStep(int c) => InverseGlobalScale / QuantDc * (1.0f / InverseDcWeights[c]);

    /// <summary>The reciprocal of <see cref="DcStep"/>: InvMulDC.</summary>
    public float InverseDcStep(int c) => InverseDcWeights[c] * (Scale * QuantDc);

    /// <summary>
    /// Chooses the global scale so the median of the field lands near 5 and the DC quantizer for
    /// <paramref name="quantDc"/> (ComputeGlobalScaleAndQuant).
    /// </summary>
    public void ComputeGlobalScaleAndQuant(float quantDc, float quantMedian, float quantMedianAbsoluteDeviation)
    {
        const float quantFieldTarget = 5;
        float scale = GlobalScaleDenominator * (quantMedian - quantMedianAbsoluteDeviation) / quantFieldTarget;
        scale = Math.Clamp(scale, 1, 1 << 15);
        int globalScale = (int)scale;
        // Keeps quant_dc at least 0.625 * kGlobalScaleDenom / kGlobalScaleNumerator = 10.
        int scaledQuantDc = (int)(quantDc * GlobalScaleNumerator * 1.6);
        if (globalScale > scaledQuantDc)
        {
            globalScale = Math.Max(scaledQuantDc, 1);
        }

        GlobalScale = globalScale;
        Recompute();
        QuantDc = (int)Math.Min(1 << 16, quantDc * InverseGlobalScale + 0.5f);
        Recompute();
    }

    /// <summary>The coded quantization value of a float field value (SetQuantFieldRect, ClampVal).</summary>
    public int Quantize(float value) => (int)Math.Max(1.0f, Math.Min(value * InverseGlobalScale + 0.5f, QuantMax));

    public void SetJpeg()
    {
        GlobalScale = GlobalScaleDenominator;
        QuantDc = 1;
        Recompute();
    }

    private void Recompute()
    {
        Scale = (float)(GlobalScale * (1.0 / GlobalScaleDenominator));
        InverseGlobalScale = (float)(1.0 * GlobalScaleDenominator / GlobalScale);
    }
}
