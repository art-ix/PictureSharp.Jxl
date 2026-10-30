using System.Buffers;
using System.Runtime.InteropServices;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>The values of one entropy-coded stream, each with its (unclustered) context, in decoding order.</summary>
internal sealed class JxlTokens : IDisposable
{
    private int[] contexts;
    // The values as int, so storage lent out by ValueStorage can hold signed per-pixel data.
    private int[] values;
    private readonly bool pooled;
    private bool lent;
    private bool disposed;

    /// <param name="owner">Owns pooled storage when provided.</param>
    /// <param name="capacity">The expected number of tokens, when known, so the storage does not grow and copy.</param>
    public JxlTokens(JxlBufferScope? owner = null, int capacity = 64)
    {
        pooled = owner is not null;
        capacity = Math.Max(capacity, 64);
        contexts = pooled ? ArrayPool<int>.Shared.Rent(capacity) : new int[capacity];
        try { values = pooled ? ArrayPool<int>.Shared.Rent(capacity) : new int[capacity]; }
        catch { if (pooled) ArrayPool<int>.Shared.Return(contexts); throw; }
        try { owner?.Own(this); }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (pooled) { ArrayPool<int>.Shared.Return(contexts); ArrayPool<int>.Shared.Return(values); }
        contexts = []; values = [];
    }

    public int Count { get; private set; }

    public ReadOnlySpan<int> Contexts => contexts.AsSpan(0, Count);

    public ReadOnlySpan<uint> Values => MemoryMarshal.Cast<int, uint>(values.AsSpan(0, Count));

    /// <summary>
    /// The value storage of tokens <paramref name="start"/> to <paramref name="start"/> + <paramref name="length"/>
    /// before they are added (0.8.6): the Modular encoder records a pixel's weighted error where its token goes, and
    /// adding tokens overwrites the storage in order. The storage then stays in place: it may not grow.
    /// </summary>
    public Memory<int> ValueStorage(int start, int length)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (start < Count || length < 0 || start > Math.Min(contexts.Length, values.Length) - length)
            throw new ArgumentOutOfRangeException(nameof(start), "The storage must be within the capacity and after the added tokens.");
        lent = true;
        return values.AsMemory(start, length);
    }

    public void Add(int context, uint value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Count == Math.Min(contexts.Length, values.Length)) Grow(1);
        contexts[Count] = context;
        values[Count++] = (int)value;
    }

    /// <summary>Appends <paramref name="count"/> tokens the caller fills in through <paramref name="newContexts"/> and <paramref name="newValues"/>.</summary>
    public void Append(int count, out Span<int> newContexts, out Span<uint> newValues)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > Math.Min(contexts.Length, values.Length) - Count) Grow(count);
        int start = Count;
        Count += count;
        newContexts = contexts.AsSpan(start, count);
        newValues = MemoryMarshal.Cast<int, uint>(values.AsSpan(start, count));
    }

    // Makes room for at least `needed` more tokens, at least doubling the capacity.
    private void Grow(int needed)
    {
        // Lent storage must stay where it is.
        if (lent) throw new InvalidOperationException("The token storage was lent out and cannot grow.");
        if (needed > Array.MaxLength - Count) throw JxlDescriptor.Unsupported("the image has too many samples to encode.");
        int size = Count <= Array.MaxLength / 2 ? Math.Max(Count * 2, Count + needed) : Array.MaxLength;
        if (!pooled) { Array.Resize(ref contexts, size); Array.Resize(ref values, size); }
        else
        {
            int[] nextContexts = ArrayPool<int>.Shared.Rent(size);
            int[] nextValues;
            try { nextValues = ArrayPool<int>.Shared.Rent(size); }
            catch { ArrayPool<int>.Shared.Return(nextContexts); throw; }
            contexts.AsSpan(0, Count).CopyTo(nextContexts);
            values.AsSpan(0, Count).CopyTo(nextValues);
            ArrayPool<int>.Shared.Return(contexts); ArrayPool<int>.Shared.Return(values);
            contexts = nextContexts; values = nextValues;
        }
    }
}
