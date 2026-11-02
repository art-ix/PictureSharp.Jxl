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
    public static Scope Begin(long maxPixels) => Create(maxPixels).Enter(report: true);

    /// <summary>
    /// The budget of an operation that spans several calls (0.8.13 frame readers): each call makes it current with
    /// <see cref="Enter"/>, and what stays charged between calls (reference slots) stays charged.
    /// </summary>
    public static JxlWorkBudget Create(long maxPixels)
    {
        const long bytesPerPixel = SamplesPerPixel * sizeof(int);
        long limit = maxPixels > (long.MaxValue - FixedBytes) / bytesPerPixel ? long.MaxValue : FixedBytes + maxPixels * bytesPerPixel;
        return new JxlWorkBudget(limit);
    }

    /// <summary>
    /// Makes this budget current until the scope is disposed, which restores the previous one; with
    /// <paramref name="report"/> the checked build reports the charged bytes then (the end of the operation).
    /// </summary>
    public Scope Enter(bool report = false)
    {
        var scope = new Scope(Current.Value, report ? this : null);
        Current.Value = this;
        return scope;
    }

#if JXL_FUZZ_CHECKED
    /// <summary>Reports the bytes this budget charged, at the end of an operation that spans calls.</summary>
    public void Report() => Completed?.Invoke(Volatile.Read(ref used));
#endif

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

    /// <summary>The bytes charged so far by the current operation (0 outside one).</summary>
    public static long Used => Current.Value is { } budget ? Volatile.Read(ref budget.used) : 0;

    /// <summary>
    /// Sets the charged bytes back to <paramref name="bytes"/> after a frame whose temporary memory was returned (0.8.10:
    /// layered images keep only their canvases). Called on the operation's thread between parallel regions.
    /// </summary>
    public static void Rewind(long bytes)
    {
        if (Current.Value is not { } budget) return;
        if (bytes > budget.limit) throw JxlDescriptor.Corrupt("Decoding needs more working memory than MaxPixels allows.");
        Volatile.Write(ref budget.used, bytes);
    }

    internal readonly struct Scope(JxlWorkBudget? previous, JxlWorkBudget? reported) : IDisposable
    {
        public void Dispose()
        {
#if JXL_FUZZ_CHECKED
            reported?.Report();
#else
            _ = reported;
#endif
            Current.Value = previous;
        }
    }
}
