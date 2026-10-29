namespace PictureSharp.Formats.Jxl;

/// <summary>
/// Resource limits and threading for lossless JPEG transcoding and original JPEG reconstruction. All metadata is
/// preserved.
/// </summary>
public sealed record JxlTranscoderOptions
{
    /// <summary>Maximum stored image pixels, checked before coefficient allocation. Default: 268,435,456.</summary>
    public long MaxPixels
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxPixels));
    } = 1L << 28;

    /// <summary>Maximum encoded input bytes. Default: <see cref="Array.MaxLength"/>. Input is buffered in memory.</summary>
    public long MaxInputBytes
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxInputBytes));
    } = Array.MaxLength;

    /// <summary>Maximum metadata bytes, including decompressed reconstruction data. Default: 64 MiB.</summary>
    public long MaxMetadataBytes
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxMetadataBytes));
    } = 64L << 20;

    /// <summary>
    /// Maximum number of threads to use (1 = single-threaded, -1 = automatic). Output does not depend on it.
    /// Default: -1.
    /// </summary>
    public int MaxDegreeOfParallelism
    {
        get;
        init => field = value == -1 || value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism), value, "Expected -1 or a positive number.");
    } = -1;
}
