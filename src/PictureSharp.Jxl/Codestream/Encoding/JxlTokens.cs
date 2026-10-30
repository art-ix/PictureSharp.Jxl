using System.Buffers;
using System.Runtime.InteropServices;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>The values of one entropy-coded stream, each with its (unclustered) context, in decoding order.</summary>
/// <remarks>
/// The contexts are stored in 32 bits, or in 16 bits once <see cref="ExpectContexts"/> announced at most 65536 of them
/// (0.8.6); their storage is created with the first token, so storage created before the context count is known (the
/// value storage the Modular encoder lends out) can still narrow them.
/// </remarks>
internal sealed class JxlTokens : IDisposable
{
    private const int MaxNarrowContexts = ushort.MaxValue + 1;
    private int[]? contexts;
    private ushort[]? narrowContexts;
    // The values as int, so storage lent out by ValueStorage can hold signed per-pixel data.
    private int[] values;
    // The number of tokens the storage holds: 0 until the context storage exists.
    private int capacity;
    private int contextCount = int.MaxValue;
    private readonly bool pooled;
    private bool lent;
    private bool disposed;

    /// <param name="owner">Owns pooled storage when provided.</param>
    /// <param name="capacity">The expected number of tokens, when known, so the storage does not grow and copy.</param>
    public JxlTokens(JxlBufferScope? owner = null, int capacity = 64)
    {
        pooled = owner is not null;
        capacity = Math.Max(capacity, 64);
        values = pooled ? ArrayPool<int>.Shared.Rent(capacity) : new int[capacity];
        try { owner?.Own(this); }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (pooled)
        {
            if (contexts is not null) ArrayPool<int>.Shared.Return(contexts);
            if (narrowContexts is not null) ArrayPool<ushort>.Shared.Return(narrowContexts);
            ArrayPool<int>.Shared.Return(values);
        }

        contexts = null; narrowContexts = null; values = []; capacity = 0;
    }

    public int Count { get; private set; }

    /// <summary>Whether the contexts are stored in 16 bits: read them with <see cref="NarrowContextValues"/>.</summary>
    public bool NarrowContexts { get; private set; }

    /// <summary>The 32-bit contexts; not available when <see cref="NarrowContexts"/>.</summary>
    public ReadOnlySpan<int> Contexts => NarrowContexts
        ? throw new InvalidOperationException("The contexts are stored in 16 bits.")
        : contexts.AsSpan(0, Count);

    /// <summary>The 16-bit contexts when <see cref="NarrowContexts"/>.</summary>
    public ReadOnlySpan<ushort> NarrowContextValues => NarrowContexts
        ? narrowContexts.AsSpan(0, Count)
        : throw new InvalidOperationException("The contexts are stored in 32 bits.");

    public ReadOnlySpan<uint> Values => MemoryMarshal.Cast<int, uint>(values.AsSpan(0, Count));

    /// <summary>
    /// Announces that every context is below <paramref name="count"/>, before the first token: with at most 65536
    /// contexts they are stored in 16 bits (0.8.6), and larger ones are rejected.
    /// </summary>
    public void ExpectContexts(int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (capacity != 0) throw new InvalidOperationException("The context count must be announced before the first token.");
        contextCount = count;
        NarrowContexts = count <= MaxNarrowContexts;
    }

    /// <summary>
    /// The value storage of tokens <paramref name="start"/> to <paramref name="start"/> + <paramref name="length"/>
    /// before they are added (0.8.6): the Modular encoder records a pixel's weighted error where its token goes, and
    /// adding tokens overwrites the storage in order. The storage then stays in place: it may not grow.
    /// </summary>
    public Memory<int> ValueStorage(int start, int length)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (start < Count || length < 0 || start > values.Length - length)
            throw new ArgumentOutOfRangeException(nameof(start), "The storage must be within the capacity and after the added tokens.");
        lent = true;
        return values.AsMemory(start, length);
    }

    public void Add(int context, uint value)
    {
        if (Count == capacity) Reserve(1);
        if (NarrowContexts)
        {
            if ((uint)context >= (uint)contextCount) throw new ArgumentOutOfRangeException(nameof(context));
            narrowContexts![Count] = (ushort)context;
        }
        else
        {
            contexts![Count] = context;
        }

        values[Count++] = (int)value;
    }

    /// <summary>
    /// Appends <paramref name="count"/> tokens the caller fills in through <paramref name="newContexts"/> (of the
    /// stored width: <see cref="int"/>, or <see cref="ushort"/> when <see cref="NarrowContexts"/>) and <paramref name="newValues"/>.
    /// </summary>
    public void Append<TContext>(int count, out Span<TContext> newContexts, out Span<uint> newValues) where TContext : unmanaged
    {
        if (typeof(TContext) != (NarrowContexts ? typeof(ushort) : typeof(int)))
            throw new InvalidOperationException($"The contexts are stored as {(NarrowContexts ? "ushort" : "int")}.");
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > capacity - Count) Reserve(count);
        int start = Count;
        Count += count;
        newContexts = NarrowContexts
            ? MemoryMarshal.Cast<ushort, TContext>(narrowContexts.AsSpan(start, count))
            : MemoryMarshal.Cast<int, TContext>(contexts.AsSpan(start, count));
        newValues = MemoryMarshal.Cast<int, uint>(values.AsSpan(start, count));
    }

    // Creates the context storage at the value capacity, then makes room for at least `needed` more tokens, at least
    // doubling the capacity.
    private void Reserve(int needed)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (capacity == 0)
        {
            if (NarrowContexts) narrowContexts = pooled ? ArrayPool<ushort>.Shared.Rent(values.Length) : new ushort[values.Length];
            else contexts = pooled ? ArrayPool<int>.Shared.Rent(values.Length) : new int[values.Length];
            capacity = values.Length;
            if (needed <= capacity - Count) return;
        }

        // Lent storage must stay where it is.
        if (lent) throw new InvalidOperationException("The token storage was lent out and cannot grow.");
        if (needed > Array.MaxLength - Count) throw JxlDescriptor.Unsupported("the image has too many samples to encode.");
        int size = Count <= Array.MaxLength / 2 ? Math.Max(Count * 2, Count + needed) : Array.MaxLength;
        if (!pooled)
        {
            if (NarrowContexts) Array.Resize(ref narrowContexts, size);
            else Array.Resize(ref contexts, size);
            Array.Resize(ref values, size);
        }
        else
        {
            int[] nextValues = ArrayPool<int>.Shared.Rent(size);
            try
            {
                if (NarrowContexts) narrowContexts = Grown(ArrayPool<ushort>.Shared, narrowContexts!, size);
                else contexts = Grown(ArrayPool<int>.Shared, contexts!, size);
            }
            catch { ArrayPool<int>.Shared.Return(nextValues); throw; }
            values.AsSpan(0, Count).CopyTo(nextValues);
            ArrayPool<int>.Shared.Return(values);
            values = nextValues;
        }

        capacity = Math.Min(values.Length, NarrowContexts ? narrowContexts!.Length : contexts!.Length);
    }

    private T[] Grown<T>(ArrayPool<T> pool, T[] current, int size)
    {
        T[] next = pool.Rent(size);
        current.AsSpan(0, Count).CopyTo(next);
        pool.Return(current);
        return next;
    }
}
