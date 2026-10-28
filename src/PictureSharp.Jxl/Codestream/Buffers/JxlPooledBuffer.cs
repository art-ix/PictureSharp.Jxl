using System.Buffers;

namespace PictureSharp.Formats.Jxl.Codestream.Buffers;

/// <summary>A single rental with a logical length independent of bucket capacity.</summary>
internal sealed class JxlPooledBuffer<T> : IDisposable where T : unmanaged
{
    private readonly ArrayPool<T> pool;
    private T[]? array;
    public JxlPooledBuffer(int length, bool clear = true, ArrayPool<T>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        this.pool = pool ?? ArrayPool<T>.Shared;
        Length = length;
        array = this.pool.Rent(length);
        if (clear) array.AsSpan(0, length).Clear();
    }
    public int Length { get; }
    internal T[] Array => array ?? throw new ObjectDisposedException(nameof(JxlPooledBuffer<T>));
    public Memory<T> Memory => Array.AsMemory(0, Length);
    public Span<T> Span => Memory.Span;
    public void Dispose()
    {
        T[]? value = array;
        array = null;
        if (value is not null) pool.Return(value);
    }
}
