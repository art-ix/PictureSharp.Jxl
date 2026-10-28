using System.Buffers;

namespace PictureSharp.Formats.Jxl.Codestream.Buffers;

/// <summary>Explicit ownership of temporary rentals. Scopes are confined to one operation or group.</summary>
internal sealed class JxlBufferScope : IDisposable
{
    private readonly List<IDisposable> rentals = [];
    public T Own<T>(T resource) where T : IDisposable
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        rentals.Add(resource);
        return resource;
    }
    public JxlPooledBuffer<T> Lease<T>(int count, bool clear = true) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var rental = new JxlPooledBuffer<T>(count, clear);
        try { return Own(rental); }
        catch { rental.Dispose(); throw; }
    }
    public T[] Rent<T>(int count, bool clear = true) where T : unmanaged => Lease<T>(count, clear).Array;
    private bool disposed;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (IDisposable rental in rentals) rental.Dispose();
        rentals.Clear();
    }
}
