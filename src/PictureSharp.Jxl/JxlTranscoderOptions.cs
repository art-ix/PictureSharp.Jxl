namespace PictureSharp.Formats.Jxl;

/// <summary>Resource limits for lossless JPEG transcoding and original JPEG reconstruction. All metadata is preserved.</summary>
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
}
