#if JXL_FUZZ_CHECKED
using PictureSharp.Formats.Jxl.Codestream.Numerics;
#endif

namespace PictureSharp.Formats.Jxl.Codestream.Buffers;

/// <summary>
/// The working memory of one decode or transcode (0.8.9, docs/design/0.8.9-resilience.md). <c>MaxPixels</c> bounds only
/// the returned image, so a few hundred bytes declaring many extra channels could allocate gigabytes. Every plane,
/// channel and rental is charged before it is allocated, released or not, against <see cref="SamplesPerPixel"/> 32-bit
/// samples per allowed pixel plus <see cref="FixedBytes"/>. The budget is ambient for the operation and flows into the
/// workers of <c>Parallel.For</c> with the execution context, so stages need no parameter; code outside an operation
/// (the encoder, tests of single parsers) charges nothing.
/// </summary>
internal sealed class JxlWorkBudget
{
    /// <summary>
    /// K of the design: working samples per allowed pixel. The corpus and the speed matrix charge at most 9 (12 MP
    /// 16-bit lossless, ToSrgb, all threads).
    /// </summary>
    public const long SamplesPerPixel = 16;

    /// <summary>Allowed beyond K per pixel: group scratch and block padding (at most 3.7 MB in the corpus, 1100x40).</summary>
    public const long FixedBytes = 64 << 20;

    private static readonly AsyncLocal<JxlWorkBudget?> Current = new();
    private readonly long limit;
    private long used;

    private JxlWorkBudget(long limit) => this.limit = limit;

#if JXL_FUZZ_CHECKED
    /// <summary>Receives the bytes each operation charged, for the fuzz targets and measurements.</summary>
    internal static Action<long>? Completed { get; set; }
#endif

    /// <summary>Starts the budget of an operation allowed <paramref name="maxPixels"/> pixels; dispose it when done.</summary>
    public static Scope Begin(long maxPixels)
    {
        const long bytesPerPixel = SamplesPerPixel * sizeof(int);
        long limit = maxPixels > (long.MaxValue - FixedBytes) / bytesPerPixel ? long.MaxValue : FixedBytes + maxPixels * bytesPerPixel;
        var scope = new Scope(Current.Value);
        Current.Value = new JxlWorkBudget(limit);
        return scope;
    }

    /// <summary>Charges <paramref name="bytes"/> against the current operation's budget, if any.</summary>
    /// <exception cref="ImageFormatException">The operation would exceed its budget.</exception>
    public static void Charge(long bytes)
    {
#if JXL_FUZZ_CHECKED
        JxlRentals.Event();
#endif
        if (Current.Value is not { } budget) return;
        if (Interlocked.Add(ref budget.used, bytes) > budget.limit)
        {
            throw JxlDescriptor.Corrupt("Decoding needs more working memory than MaxPixels allows.");
        }
    }

    internal readonly struct Scope(JxlWorkBudget? previous) : IDisposable
    {
        public void Dispose()
        {
#if JXL_FUZZ_CHECKED
            if (Current.Value is { } budget) Completed?.Invoke(Volatile.Read(ref budget.used));
#endif
            Current.Value = previous;
        }
    }
}
