using System.Runtime.ExceptionServices;

namespace PictureSharp.Formats.Jxl.Codestream.Numerics;

/// <summary>
/// Deterministic parallel loops. A degree of 1 (or a single item) runs a plain loop on the calling thread; otherwise
/// items run on the thread pool, an item is skipped once a lower one has failed, and after the join the exception of
/// the lowest failed item is rethrown unchanged: the one the sequential loop throws first. Items must write disjoint
/// state; regions must not nest. See docs/design/0.8.1-parallelism.md.
/// </summary>
internal static class JxlParallel
{
    /// <summary>The degree that runs single-threaded.</summary>
    public const int Sequential = 1;

    /// <summary>Whether <paramref name="degree"/> may use more than one thread for <paramref name="count"/> items.</summary>
    public static bool IsParallel(int degree, int count) => degree != Sequential && count > 1;

    /// <summary>
    /// The number of workers a region of <paramref name="count"/> items uses at most: <paramref name="degree"/>, or the
    /// processor count for -1 (automatic).
    /// </summary>
    public static int Workers(int degree, int count) =>
        !IsParallel(degree, count) ? 1 : Math.Min(count, degree < 0 ? Environment.ProcessorCount : degree);

    /// <summary>Runs <paramref name="body"/> for every index in [0, <paramref name="count"/>).</summary>
    public static void For(int count, int degree, CancellationToken cancellationToken, Action<int> body)
    {
        if (!IsParallel(degree, count))
        {
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                body(i);
            }

            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var failure = new Failure();
        // The token is checked by the items, not by Parallel.For, so cancellation is not wrapped or reordered.
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = degree < 0 ? -1 : degree }, i =>
        {
            if (i > Volatile.Read(ref failure.Index)) return;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                body(i);
            }
            catch (Exception exception)
            {
                failure.Record(i, exception);
            }
        });
        failure.ThrowIfAny();
    }

    /// <summary>
    /// Runs <paramref name="body"/> for every index in [0, <paramref name="count"/>) with per-worker state: each worker
    /// creates one state and takes the next index until none is left, so items must not depend on which worker, or in
    /// which order, they run. A worker creates its state when it takes its first index (a failure to create it is that
    /// index's failure). A single-threaded run creates one state and visits the indices in order.
    /// </summary>
    public static void ForWorkers<TState>(int count, int degree, CancellationToken cancellationToken, Func<TState> create,
        Action<TState, int> body)
    {
        if (!IsParallel(degree, count))
        {
            TState state = create();
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                body(state, i);
            }

            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var failure = new Failure();
        int next = -1;
        int workers = Workers(degree, count);
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ =>
        {
            // The state is created for the first index the worker takes: a worker that finds no index left (the pool
            // was busy, as when callers decode several images at once) allocates no scratch.
            TState? state = default;
            bool created = false;
            for (int i; (i = Interlocked.Increment(ref next)) < count;)
            {
                if (i > Volatile.Read(ref failure.Index)) return;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!created)
                    {
                        state = create();
                        created = true;
                    }

                    body(state!, i);
                }
                catch (Exception exception)
                {
                    failure.Record(i, exception);
                    return;
                }
            }
        });
        failure.ThrowIfAny();
    }

    /// <summary>
    /// Runs <paramref name="body"/> for row bands [start, end) covering [0, <paramref name="rows"/>), at most one band
    /// per worker and at least <paramref name="minRows"/> rows per band. With one band the body gets all rows.
    /// </summary>
    public static void ForBands(int rows, int minRows, int degree, CancellationToken cancellationToken, Action<int, int> body)
    {
        int bands = Bands(rows, minRows, degree);
        For(bands, degree, cancellationToken, band => body(BandStart(rows, bands, band), BandStart(rows, bands, band + 1)));
    }

    /// <summary>The number of bands <see cref="ForBands"/> uses.</summary>
    public static int Bands(int rows, int minRows, int degree) =>
        Math.Max(1, Math.Min(Workers(degree, int.MaxValue), rows / Math.Max(1, minRows)));

    /// <summary>The first row of <paramref name="band"/> of <paramref name="bands"/> equal bands of <paramref name="rows"/>.</summary>
    public static int BandStart(int rows, int bands, int band) => (int)((long)rows * band / bands);

    private sealed class Failure
    {
        public int Index = int.MaxValue;
        private readonly Lock gate = new();
        private Exception? exception;

        public void Record(int index, Exception error)
        {
            lock (gate)
            {
                if (index >= Index) return;
                exception = error;
                Volatile.Write(ref Index, index);
            }
        }

        public void ThrowIfAny()
        {
            if (exception is not null) ExceptionDispatchInfo.Throw(exception);
        }
    }
}
