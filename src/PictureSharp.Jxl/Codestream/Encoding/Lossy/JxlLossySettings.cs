using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// The parameters of the lossy encoder: the target butteraugli distance and libjxl's speed tier, with the decisions
/// that libjxl v0.12.0 derives from them (lib/jxl/enc_params.h, enc_frame.cc, enc_heuristics.cc; BSD-3-Clause).
/// Speed tiers follow libjxl: effort e is tier 10 − e (3 is effort 7, the default); tiers 8 and 9 use tier 8 for
/// VarDCT, as libjxl does, and efforts 8 and 9 use tier 3 because the butteraugli iterations are not implemented.
/// </summary>
internal sealed record JxlLossySettings
{
    public const float MinDistance = 0.05f;
    public const float MaxDistance = 25.0f;

    // libjxl SpeedTier values.
    public const int Kitten = 2;
    public const int Squirrel = 3;
    public const int Wombat = 4;
    public const int Hare = 5;
    public const int Cheetah = 6;
    public const int Falcon = 7;
    public const int Thunder = 8;

    private JxlLossySettings(float distance, float originalDistance, int speedTier, int resampling)
    {
        Distance = distance;
        OriginalDistance = originalDistance;
        SpeedTier = speedTier;
        Resampling = resampling;
    }

    /// <summary>The butteraugli distance the frame is encoded at (after the adjustment for resampling).</summary>
    public float Distance { get; }

    /// <summary>The requested distance on the full-size image.</summary>
    public float OriginalDistance { get; }

    public int SpeedTier { get; }

    /// <summary>1, or 2 when the image is downsampled before encoding (distances of 10 and more).</summary>
    public int Resampling { get; }

    /// <summary>
    /// The maximum degree of parallelism of tiles, groups and row bands (1 = single-threaded, -1 = automatic); the
    /// output does not depend on it.
    /// </summary>
    public int Parallelism { get; init; } = JxlParallel.Sequential;

    /// <summary>
    /// Settings for <paramref name="distance"/> (0.05–25) and <paramref name="effort"/> (1–9); without
    /// <paramref name="resample"/> the frame keeps its full size at every distance (images with alpha, which extra
    /// channels would otherwise share the downsampling of).
    /// </summary>
    public static JxlLossySettings Create(float distance, int effort, bool resample = true)
    {
        if (!(distance is >= MinDistance and <= MaxDistance)) throw new ArgumentOutOfRangeException(nameof(distance));
        if (effort is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(effort));
        int tier = effort >= 7 ? Squirrel : Math.Min(Thunder, 10 - effort);
        // ParamsPostInit: very low bit rates look better downsampled 2x, at an adjusted distance.
        return distance >= 10 && resample
            ? new JxlLossySettings(distance * 0.25f + 0.25f, distance, tier, 2)
            : new JxlLossySettings(distance, distance, tier, 1);
    }

    /// <summary>Gaborish smoothing (LoopFilterFromParams).</summary>
    public bool Gaborish => SpeedTier <= Hare && Distance > 0.5f;

    /// <summary>Edge-preserving filter iterations: one more at distances 0.7, 1.5 and 4.</summary>
    public int EpfIterations => (Distance >= 0.7f ? 1 : 0) + (Distance >= 1.5f ? 1 : 0) + (Distance >= 4.0f ? 1 : 0);

    /// <summary>The adaptive quantization map (InitialQuantField) instead of a constant field.</summary>
    public bool AdaptiveQuantization => SpeedTier <= Hare;

    /// <summary>The search over transform sizes and types (ProcessRectACS); otherwise every block is DCT8.</summary>
    public bool SearchAcStrategy => SpeedTier <= Hare;

    /// <summary>The per-block quantization adjustment of AdjustQuantBlockAC.</summary>
    public bool AdjustBlockQuantization => SpeedTier <= Hare;

    /// <summary>A first chroma-from-luma map with DCT8 before the transform search.</summary>
    public bool InitialChromaFromLuma => SpeedTier <= Squirrel;

    /// <summary>The chroma-from-luma map from the chosen transforms and quantization.</summary>
    public bool ChromaFromLuma => SpeedTier <= Hare;

    /// <summary>The closed-form chroma-from-luma estimate instead of Newton iterations.</summary>
    public bool FastChromaFromLuma => SpeedTier >= Wombat;

    /// <summary>DC quantized against the weighted predictor, with one more bit of precision.</summary>
    public bool NonlinearDc => SpeedTier < Falcon;

    /// <summary>Coefficient orders by frequency of zeros, and a block context model from the quantization field.</summary>
    public bool CustomOrders => SpeedTier < Falcon;

    /// <summary>The pixel statistics behind the X and B quantization matrix scales.</summary>
    public bool ChromacityStatistics => SpeedTier <= Squirrel;

    /// <summary>The EPF sharpness map chosen by reconstruction error (ComputeARHeuristics).</summary>
    public bool EpfSharpnessSearch => SpeedTier <= Wombat && Distance >= 0.5f && EpfIterations > 0;

    /// <summary>Patches of repeated text-like shapes.</summary>
    public bool Patches => SpeedTier <= Squirrel;

    /// <summary>The DC quantization step (InitialQuantDC).</summary>
    public float InitialQuantDc
    {
        get
        {
            const float dcMul = 0.3f;
            const float dcQuantPow = 0.83f;
            const float dcQuant = 1.095924047623553f;
            float target = Math.Max(0.5f * Distance, Math.Min(Distance, dcMul * MathF.Pow(1.0f / dcMul * Distance, dcQuantPow)));
            return Math.Min(dcQuant / target, 50.0f);
        }
    }
}
