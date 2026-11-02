namespace PictureSharp.Formats.Jxl;

/// <summary>
/// The layers of a JPEG XL still image read by <see cref="JxlLayers"/>, bottom (the first frame) to top, and the
/// flattened image. Disposing it disposes every layer image.
/// </summary>
public sealed class JxlLayeredImage : IDisposable
{
    private readonly Image flattened;
    private bool disposed;

    internal JxlLayeredImage(Image flattened, IReadOnlyList<JxlLayer> layers)
    {
        this.flattened = flattened;
        Layers = layers;
    }

    /// <summary>The canvas width: the width of the flattened image (oriented with <see cref="DecoderOptions.ApplyExifOrientation"/>).</summary>
    public int Width => flattened.Width;

    /// <summary>The canvas height: the height of the flattened image.</summary>
    public int Height => flattened.Height;

    /// <summary>The layers, bottom first. Each image belongs to this object.</summary>
    public IReadOnlyList<JxlLayer> Layers { get; }

    /// <summary>The metadata of the flattened image: colour description, EXIF, XMP and the orientation.</summary>
    public ImageMetadata Metadata => flattened.Metadata;

    /// <summary>A new image equal to what <c>Image.Load</c> returns for the same data and options; the caller owns it.</summary>
    public Image Flatten()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return flattened.Clone();
    }

    /// <summary>Disposes the flattened image and every layer image.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        flattened.Dispose();
        foreach (JxlLayer layer in Layers) layer.Image.Dispose();
    }
}
