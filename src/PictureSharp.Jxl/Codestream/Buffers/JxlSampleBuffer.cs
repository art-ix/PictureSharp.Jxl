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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int capacity = values.Length == 0 ? 64 : Count <= Array.MaxLength / 2 ? Count * 2 : Array.MaxLength;
        if (capacity <= Count) throw JxlDescriptor.Unsupported("too many tree samples.");
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
