using PictureSharp.Formats.Jxl.Integration;

namespace PictureSharp.Formats.Jxl;

/// <summary>
/// Reads the layers of a layered JPEG XL still image as separate images, together with the flattened image that
/// <c>Image.Load</c> returns. Layers are exposed only when the displayed frames form a simple stack: each frame blends
/// onto the result of the frame before it (the first onto an empty canvas), extra channels from the same reference
/// slot. Reference-only, DC and patch frames are not layers. Anything else, and animations, throw
/// <see cref="NotSupportedException"/>; <c>Image.Load</c> still decodes them to one image. A single-frame image is one
/// layer.
/// </summary>
/// <remarks>
/// <para>The image is decoded once through <c>Image.Load</c> with the given options, so input limits, cancellation and
/// errors are the same. <see cref="DecoderOptions.MaxPixels"/> covers the flattened image and every layer, and
/// <see cref="DecoderOptions.MaxFrames"/> counts the flattened image and each layer (and still bounds the frames in the
/// codestream). Layers use the pixel type of the flattened image.</para>
/// <para><see cref="ColorConversionMode.ToSrgb"/> converts every layer like the flattened image.
/// <see cref="DecoderOptions.ApplyExifOrientation"/> orients every layer and maps its offset onto the oriented canvas
/// (as libjxl does); without it layers and offsets are in the stored orientation, given by
/// <see cref="JxlLayeredImage.Metadata"/>. <see cref="DecoderOptions.TargetSize"/> is not supported.</para>
/// </remarks>
public static class JxlLayers
{
    /// <summary>Reads the layers of a JPEG XL file.</summary>
    /// <exception cref="NotSupportedException">The frames are not a simple layer stack, or a feature is not decoded yet.</exception>
    public static JxlLayeredImage Load(string path, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
        Load(options, configured => Image.Load(path, configured, cancellationToken), cancellationToken);

    /// <summary>Reads the layers of JPEG XL from the current stream position.</summary>
    /// <exception cref="NotSupportedException">The frames are not a simple layer stack, or a feature is not decoded yet.</exception>
    public static JxlLayeredImage Load(Stream stream, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
        Load(options, configured => Image.Load(stream, configured, cancellationToken), cancellationToken);

    /// <summary>Reads the layers of buffered JPEG XL.</summary>
    /// <exception cref="NotSupportedException">The frames are not a simple layer stack, or a feature is not decoded yet.</exception>
    public static JxlLayeredImage Load(ReadOnlyMemory<byte> data, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
        Load(options, configured => Image.Load(data, configured, cancellationToken), cancellationToken);

    /// <summary>Reads the layers of a JPEG XL file using asynchronous file I/O.</summary>
    /// <exception cref="NotSupportedException">The frames are not a simple layer stack, or a feature is not decoded yet.</exception>
    public static Task<JxlLayeredImage> LoadAsync(string path, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
        LoadAsync(options, configured => Image.LoadAsync(path, configured, cancellationToken), cancellationToken);

    /// <summary>Reads the layers of JPEG XL from the current stream position using asynchronous I/O.</summary>
    /// <exception cref="NotSupportedException">The frames are not a simple layer stack, or a feature is not decoded yet.</exception>
    public static Task<JxlLayeredImage> LoadAsync(Stream stream, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
        LoadAsync(options, configured => Image.LoadAsync(stream, configured, cancellationToken), cancellationToken);

    /// <summary>Describes the layers of a JPEG XL file without decoding pixels.</summary>
    /// <exception cref="NotSupportedException">The frames are not a simple layer stack, or a feature is not decoded yet.</exception>
    public static JxlLayeredImageInfo Identify(string path, DecoderOptions? options = null) =>
        Identify(options, configured => Image.Identify(path, configured));

    /// <summary>Describes the layers of JPEG XL from the current stream position without decoding pixels.</summary>
    /// <exception cref="NotSupportedException">The frames are not a simple layer stack, or a feature is not decoded yet.</exception>
    public static JxlLayeredImageInfo Identify(Stream stream, DecoderOptions? options = null) =>
        Identify(options, configured => Image.Identify(stream, configured));

    /// <summary>Describes the layers of buffered JPEG XL without decoding pixels.</summary>
    /// <exception cref="NotSupportedException">The frames are not a simple layer stack, or a feature is not decoded yet.</exception>
    public static JxlLayeredImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions? options = null) =>
        Identify(options, configured => Image.Identify(data, configured));

    private static JxlLayeredImage Load(DecoderOptions? options, Func<DecoderOptions, Image> load, CancellationToken cancellationToken)
    {
        options = Check(options);
        using var collector = new JxlLayerCollector();
        Image flattened = load(options with { Configuration = new JxlLayerModule(collector).Configure(options.Configuration) });
        return Complete(collector, flattened, options, cancellationToken);
    }

    private static async Task<JxlLayeredImage> LoadAsync(DecoderOptions? options, Func<DecoderOptions, Task<Image>> load,
        CancellationToken cancellationToken)
    {
        options = Check(options);
        using var collector = new JxlLayerCollector();
        Image flattened = await load(options with { Configuration = new JxlLayerModule(collector).Configure(options.Configuration) })
            .ConfigureAwait(false);
        return Complete(collector, flattened, options, cancellationToken);
    }

    private static JxlLayeredImageInfo Identify(DecoderOptions? options, Func<DecoderOptions, ImageInfo> identify)
    {
        options = Check(options);
        using var collector = new JxlLayerCollector();
        ImageInfo info = identify(options with { Configuration = new JxlLayerModule(collector).Configure(options.Configuration) });
        return new JxlLayeredImageInfo(info, collector.Describe(options));
    }

    private static JxlLayeredImage Complete(JxlLayerCollector collector, Image flattened, DecoderOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return collector.Complete(flattened, options, cancellationToken);
        }
        catch
        {
            flattened.Dispose();
            throw;
        }
    }

    private static DecoderOptions Check(DecoderOptions? options)
    {
        options ??= DecoderOptions.Default;
        if (options.TargetSize is not null)
        {
            throw new ArgumentException("JxlLayers does not support DecoderOptions.TargetSize.", nameof(options));
        }

        return options;
    }
}
