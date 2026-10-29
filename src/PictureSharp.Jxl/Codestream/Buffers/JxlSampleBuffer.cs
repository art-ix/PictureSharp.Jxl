using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.Jxl.Codestream.Buffers;

/// <summary>Append-only sampled values; growth returns the previous rental immediately.</summary>
internal sealed class JxlSampleBuffer<T> : IDisposable where T : unmanaged
{
    private JxlPooledBuffer<T>? buffer;
    private T[] values = [];
    private bool disposed;
    public int Count { get; private set; }
    public ReadOnlySpan<T> Span => values.AsSpan(0, Count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(T value)
    {
        int count = Count;
        if (count == values.Length) Grow();
        values[count] = value;
        Count = count + 1;
    }

    public void AddRange(ReadOnlySpan<T> items)
    {
        if (values.Length - Count < items.Length) Grow(items.Length);
        items.CopyTo(values.AsSpan(Count));
        Count += items.Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int extra = 1)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Count > Array.MaxLength - extra) throw JxlDescriptor.Unsupported("too many tree samples.");
        int capacity = values.Length == 0 ? 64 : Count <= Array.MaxLength / 2 ? Count * 2 : Array.MaxLength;
        capacity = Math.Max(capacity, Count + extra);
        var next = new JxlPooledBuffer<T>(capacity, clear: false);
        Span.CopyTo(next.Span);
        buffer?.Dispose();
        buffer = next;
        values = next.Array;
    }

    public void Dispose()
    {
        disposed = true;
        buffer?.Dispose();
        buffer = null;
        values = [];
        Count = 0;
    }
}
