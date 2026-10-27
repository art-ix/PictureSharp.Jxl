namespace PictureSharp.Formats.Jxl.Codestream.Model;

internal enum JxlTransformId : uint { Rct = 0, Palette = 1, Squeeze = 2 }

internal readonly record struct JxlSqueeze(bool Horizontal, bool InPlace, uint BeginC, uint NumC);

/// <summary>
/// A Modular transform: reversible colour transform, (delta) palette or squeeze. Ported from libjxl v0.12.0
/// lib/jxl/modular/transform/transform.cc, rct.cc, palette.cc, palette.h and squeeze.cc (BSD-3-Clause), scalar paths.
/// </summary>
internal sealed record JxlTransform
{
    public JxlTransformId Id { get; init; }

    public uint BeginC { get; init; }

    public uint RctType { get; init; }

    public uint NumC { get; init; }

    public uint Colors { get; init; }

    public uint Deltas { get; init; }

    public JxlPredictor Predictor { get; init; }

    /// <summary>The squeeze steps; empty when the decoder must derive the default steps.</summary>
    public IReadOnlyList<JxlSqueeze> Squeezes { get; init => field = Array.AsReadOnly(value.ToArray()); } = Array.Empty<JxlSqueeze>();
}
