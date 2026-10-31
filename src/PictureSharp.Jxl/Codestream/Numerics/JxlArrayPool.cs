using System.Buffers;
#if JXL_FUZZ_CHECKED
using System.Runtime.CompilerServices;
#endif

namespace PictureSharp.Formats.Jxl.Codestream.Numerics;

/// <summary>
/// The pool of every rental in src. The release build uses <see cref="ArrayPool{T}.Shared"/>; the fuzz targets'
/// checked build (0.8.9, docs/design/0.8.9-resilience.md) count rentals through <c>JxlRentals</c> to find
/// buffers that are not returned, or returned twice, on any exit.
/// </summary>
internal static class JxlArrayPool<T>
{
#if JXL_FUZZ_CHECKED
    public static ArrayPool<T> Shared { get; } = new JxlRentals.CountingPool<T>();
#else
    public static ArrayPool<T> Shared => ArrayPool<T>.Shared;
#endif
}

#if JXL_FUZZ_CHECKED
/// <summary>Rentals outstanding across all element types, for the checked build.</summary>
internal static class JxlRentals
{
    private static readonly ConditionalWeakTable<Array, string> Live = new();
    private static long outstanding;
    private static long largest;
    private static long events;
    private static string? misuse;

    /// <summary>
    /// Called with the running count of every rental and every working-budget charge (JxlWorkBudget.Charge), on the
    /// thread that makes it: the cancel targets cancel at the n-th one, a point that is reproducible at one thread.
    /// </summary>
    public static Action<long>? OnEvent { get; set; }

    /// <summary>Rentals and charges since <see cref="Reset"/>.</summary>
    public static long Events => Interlocked.Read(ref events);

    /// <summary>Counts one rental or charge and passes it to <see cref="OnEvent"/>.</summary>
    public static void Event()
    {
        long count = Interlocked.Increment(ref events);
        OnEvent?.Invoke(count);
    }

    /// <summary>Rentals not returned yet.</summary>
    public static long Outstanding => Interlocked.Read(ref outstanding);

    /// <summary>The largest rental in bytes since <see cref="Reset"/>.</summary>
    public static long Largest => Interlocked.Read(ref largest);

    /// <summary>The first array returned twice or never rented since <see cref="Reset"/>, or null.</summary>
    public static string? Misuse => Volatile.Read(ref misuse);

    /// <summary>Descriptions of the outstanding rentals (element type and length).</summary>
    public static IEnumerable<string> Describe()
    {
        foreach (KeyValuePair<Array, string> entry in Live) yield return entry.Value;
    }

    public static void Reset()
    {
        Live.Clear();
        Interlocked.Exchange(ref outstanding, 0);
        Interlocked.Exchange(ref largest, 0);
        Interlocked.Exchange(ref events, 0);
        Volatile.Write(ref misuse, null);
    }

    internal sealed class CountingPool<T> : ArrayPool<T>
    {
        public override T[] Rent(int minimumLength)
        {
            T[] array = ArrayPool<T>.Shared.Rent(minimumLength);
            // Rent(0) hands out the shared empty array to every caller; it is never pooled.
            if (array.Length == 0) return array;
            if (!Live.TryAdd(array, $"{typeof(T).Name}[{minimumLength}]"))
            {
                // ArrayPool<T>.Shared never hands out an array twice; this one was returned without being removed.
                Interlocked.CompareExchange(ref misuse, $"{typeof(T).Name}[{array.Length}] rented while still live", null);
            }

            Interlocked.Increment(ref outstanding);
            Event();
            long bytes = (long)array.Length * Unsafe.SizeOf<T>();
            for (long seen = Largest; bytes > seen && Interlocked.CompareExchange(ref largest, bytes, seen) != seen; seen = Largest)
            {
            }

            return array;
        }

        public override void Return(T[] array, bool clearArray = false)
        {
            if (array.Length == 0) return;
            if (!Live.Remove(array))
            {
                Interlocked.CompareExchange(ref misuse, $"{typeof(T).Name}[{array.Length}] returned twice or never rented", null);
                return;
            }

            Interlocked.Decrement(ref outstanding);
            ArrayPool<T>.Shared.Return(array, clearArray);
        }
    }
}
#endif
