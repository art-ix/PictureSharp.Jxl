using System.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>The values of one entropy-coded stream, each with its (unclustered) context, in decoding order.</summary>
internal sealed class JxlTokens : IDisposable
{
    private int[] contexts;
    private uint[] values;
    private readonly bool pooled;
    private bool disposed;
    public JxlTokens(JxlBufferScope? owner = null)
    {
        pooled = owner is not null;
        contexts = pooled ? ArrayPool<int>.Shared.Rent(64) : new int[64];
        try { values = pooled ? ArrayPool<uint>.Shared.Rent(64) : new uint[64]; }
        catch { if (pooled) ArrayPool<int>.Shared.Return(contexts); throw; }
        try { owner?.Own(this); }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (pooled) { ArrayPool<int>.Shared.Return(contexts); ArrayPool<uint>.Shared.Return(values); }
        contexts = []; values = [];
    }

    public int Count { get; private set; }

    public ReadOnlySpan<int> Contexts => contexts.AsSpan(0, Count);

    public ReadOnlySpan<uint> Values => values.AsSpan(0, Count);

    public void Add(int context, uint value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Count == Math.Min(contexts.Length, values.Length)) Grow(1);
        contexts[Count] = context;
        values[Count++] = value;
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
        newValues = values.AsSpan(start, count);
    }

    // Makes room for at least `needed` more tokens, at least doubling the capacity.
    private void Grow(int needed)
    {
        if (needed > Array.MaxLength - Count) throw JxlDescriptor.Unsupported("the image has too many samples to encode.");
        int size = Count <= Array.MaxLength / 2 ? Math.Max(Count * 2, Count + needed) : Array.MaxLength;
        if (!pooled) { Array.Resize(ref contexts, size); Array.Resize(ref values, size); }
        else
        {
            int[] nextContexts = ArrayPool<int>.Shared.Rent(size);
            uint[] nextValues;
            try { nextValues = ArrayPool<uint>.Shared.Rent(size); }
            catch { ArrayPool<int>.Shared.Return(nextContexts); throw; }
            contexts.AsSpan(0, Count).CopyTo(nextContexts);
            values.AsSpan(0, Count).CopyTo(nextValues);
            ArrayPool<int>.Shared.Return(contexts); ArrayPool<uint>.Shared.Return(values);
            contexts = nextContexts; values = nextValues;
        }
    }
}
