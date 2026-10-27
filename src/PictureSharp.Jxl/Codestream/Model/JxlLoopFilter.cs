namespace PictureSharp.Formats.Jxl.Codestream.Model;

// Ported from libjxl v0.12.0 lib/jxl/loop_filter.cc (BSD-3-Clause).

/// <summary>The restoration filters: Gaborish smoothing and the edge-preserving filter (EPF) with its parameters.</summary>
internal sealed record JxlLoopFilter
{
    public const int SharpnessEntries = 8;

    public static JxlLoopFilter Default { get; } = new();

    public bool Gaborish { get; init; } = true;

    /// <summary>The side and corner weights of the X, Y and B Gaborish kernels (before normalisation).</summary>
    public IReadOnlyList<float> GaborishWeights { get; init => field = value.Snapshot(); } = Array.AsReadOnly<float>([GaborishSide, GaborishCorner, GaborishSide, GaborishCorner, GaborishSide, GaborishCorner]);

    /// <summary>0 disables EPF; 1 runs stage one, 2 stages one and two, 3 stage zero first.</summary>
    public uint EpfIterations { get; init; } = 2;

    public IReadOnlyList<float> SharpnessLut { get; init => field = value.Snapshot(); } = Array.AsReadOnly<float>([.. Enumerable.Range(0, SharpnessEntries).Select(i => (float)i / (SharpnessEntries - 1))]);

    public IReadOnlyList<float> ChannelScale { get; init => field = value.Snapshot(); } = Array.AsReadOnly<float>([40.0f, 5.0f, 3.5f]);

    public float Pass1ZeroFlush { get; init; } = 0.45f;

    public float Pass2ZeroFlush { get; init; } = 0.6f;

    public float QuantMul { get; init; } = 0.46f;

    public float Pass0SigmaScale { get; init; } = 0.9f;

    public float Pass2SigmaScale { get; init; } = 6.5f;

    public float BorderSadMul { get; init; } = 0.6666666666666666f;

    public float SigmaForModular { get; init; } = 1.0f;

    private static float GaborishSide => (float)(1.1 * 0.104699568f);

    private static float GaborishCorner => (float)(1.1 * 0.055680538f);
}
