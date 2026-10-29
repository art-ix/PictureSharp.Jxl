namespace PictureSharp.Formats.Jxl.Codestream.Model;

internal enum JxlTransformId : uint { Rct = 0, Palette = 1, Squeeze = 2 }

internal readonly record struct JxlSqueeze(bool Horizontal, bool InPlace, uint BeginC, uint NumC);

/// <summary>
/// A reversible colour transform type: a permutation of the three channels × 7 + one of seven transforms. Transforms
/// 0–5 subtract the first channel from the third (low bit) and nothing, the first or the average of first and third
/// from the second (high bits); 6 is YCoCg.
/// </summary>
internal readonly record struct JxlRctType(uint Value)
{
    public const uint Count = 42;
    private const uint Transforms = 7;
    private const int YCoCgTransform = 6;

    public static JxlRctType None => new(0);

    /// <summary>YCoCg without a permutation (libjxl rct_type 6).</summary>
    public static JxlRctType YCoCg => new(YCoCgTransform);

    /// <summary>0=RGB, 1=GBR, 2=BRG, 3=RBG, 4=GRB, 5=BGR.</summary>
    public int Permutation => (int)(Value / Transforms);

    public int Transform => (int)(Value % Transforms);

    public bool IsYCoCg => Transform == YCoCgTransform;

    public bool SubtractsFirstFromThird => (Transform & 1) != 0;

    /// <summary>What the second channel has subtracted: 0 nothing, 1 the first, 2 the average of the first and third.</summary>
    public int SecondChannelMode => Transform >> 1;

    /// <summary>The original channel of each of the three coded channels.</summary>
    public (int First, int Second, int Third) ChannelOrder =>
        (Permutation % 3, (Permutation + 1 + Permutation / 3) % 3, (Permutation + 2 - Permutation / 3) % 3);
}

/// <summary>
/// A Modular transform: reversible colour transform, (delta) palette or squeeze. Ported from libjxl v0.12.0
/// lib/jxl/modular/transform/transform.cc, rct.cc, palette.cc, palette.h and squeeze.cc (BSD-3-Clause), scalar paths.
/// </summary>
internal sealed record JxlTransform
{
    public JxlTransformId Id { get; init; }

    public uint BeginC { get; init; }

    public JxlRctType RctType { get; init; }

    public uint NumC { get; init; }

    public uint Colors { get; init; }

    public uint Deltas { get; init; }

    public JxlPredictor Predictor { get; init; }

    /// <summary>The squeeze steps; empty when the decoder must derive the default steps.</summary>
    public IReadOnlyList<JxlSqueeze> Squeezes { get; init => field = value.Snapshot(); } = Array.Empty<JxlSqueeze>();
}
