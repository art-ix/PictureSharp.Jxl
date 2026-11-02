using System.Drawing;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>
/// The layers of one <see cref="JxlLayers"/> call, gathered while the core runs <c>Image.Load</c> or <c>Image.Identify</c>
/// with a <see cref="JxlLayerModule"/> (docs/design/0.8.11-layers.md). Owns the layer images until
/// <see cref="Complete"/> hands them over; <see cref="Dispose"/> releases the rest.
/// </summary>
internal sealed class JxlLayerCollector : IDisposable
{
    private readonly List<Image> images = [];
    private IReadOnlyList<JxlFrameHeader> frames = [];
    private (uint Width, uint Height, uint Orientation) canvas;

    public int Count => images.Count;

    /// <summary>The frames that are layers (bottom first); throws NotSupportedException when they are not a stack.</summary>
    public IReadOnlyList<JxlFrameHeader> Select(JxlCodestream codestream, JxlHeaders headers, DecoderOptions options,
        CancellationToken cancellationToken)
    {
        int alpha = JxlDecodeSupportPolicy.EnsureImageSupported(headers.Metadata);
        List<JxlFrameHeader> all = JxlDecodeSession.ReadFrameHeaders(codestream, headers, cancellationToken, options.MaxFrames);
        frames = JxlLayerStack.Select(all, headers.Metadata, alpha);
        canvas = (headers.Size.Width, headers.Size.Height, headers.Metadata.Orientation);
        return frames;
    }

    /// <summary>Takes a decoded layer; it gets the colour description of its samples like the image.</summary>
    public void Add(Image image, JxlColorEncoding encoding, byte[]? icc, DecoderOptions options)
    {
        images.Add(image);
        if (!options.IgnoreMetadata) JxlMetadataReader.ApplyColour(image.Metadata, encoding, icc, options);
    }

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        ImageInfo info = JxlDecoder.Instance.Identify(data, options);
        JxlCodestream codestream = JxlContainer.Parse(data).Codestream;
        Select(codestream, JxlHeadersReader.Read(codestream), options, CancellationToken.None);
        return info;
    }

    /// <summary>The layer descriptions as <see cref="Complete"/> places the layers.</summary>
    public IReadOnlyList<JxlLayerInfo> Describe(DecoderOptions options) =>
    [
        .. frames.Select(frame =>
        {
            (Point offset, Size size) = Place(frame, options.ApplyExifOrientation);
            return new JxlLayerInfo(frame.Name, offset, size, (JxlLayerBlendMode)frame.Blending.Mode, frame.Blending.Clamp);
        }),
    ];

    /// <summary>
    /// The layered image: each layer converted and oriented as <c>Image.Load</c> did with <paramref name="flattened"/>
    /// (ToSrgb, then the reduction to 8 bits, then the orientation). Ownership of the layers passes to the result.
    /// </summary>
    public JxlLayeredImage Complete(Image flattened, DecoderOptions options, CancellationToken cancellationToken)
    {
        if (images.Count != frames.Count) throw new InvalidOperationException("The decoder did not produce every layer.");
        bool orient = options.ApplyExifOrientation && canvas.Orientation > 1;
        var layers = new JxlLayer[images.Count];
        for (int i = 0; i < layers.Length; i++)
        {
            Image image = images[i];
            if (options.ColorConversion == ColorConversionMode.ToSrgb)
            {
                image.Mutate(static x => x.ConvertToSrgb(), cancellationToken);
                if (!options.PreserveBitDepth && image.PixelType != PixelType.Rgba32)
                {
                    image.Mutate(static x => x.ConvertPixelType(PixelType.Rgba32), cancellationToken);
                }
            }

            if (orient)
            {
                image.Metadata.Orientation = (ExifOrientation)canvas.Orientation;
                image.Mutate(static x => x.AutoOrient(), cancellationToken);
            }

            JxlFrameHeader frame = frames[i];
            layers[i] = new JxlLayer(image, Place(frame, options.ApplyExifOrientation).Offset, frame.Name,
                (JxlLayerBlendMode)frame.Blending.Mode, frame.Blending.Clamp);
        }

        var result = new JxlLayeredImage(flattened, layers);
        images.Clear();
        return result;
    }

    public void Dispose()
    {
        foreach (Image image in images) image.Dispose();
        images.Clear();
    }

    // The offset and size of a layer on the canvas; oriented like libjxl's JxlDecoderGetFrameHeader when the
    // orientation is applied (decode.cc, GetCurrentDimensions and the crop offset).
    private (Point Offset, Size Size) Place(JxlFrameHeader frame, bool applyOrientation)
    {
        int x0 = frame.X0, y0 = frame.Y0;
        int xsize = (int)frame.Width, ysize = (int)frame.Height;
        uint orientation = canvas.Orientation;
        if (!applyOrientation || orientation <= 1) return (new Point(x0, y0), new Size(xsize, ysize));
        long width = canvas.Width, height = canvas.Height;
        if (orientation > 4)
        {
            (x0, y0) = (y0, x0);
            (xsize, ysize) = (ysize, xsize);
            (width, height) = (height, width);
        }

        uint flips = (orientation - 1) & 3;
        if (flips is 1 or 2) x0 = (int)(width - xsize - x0);
        if (flips > 1) y0 = (int)(height - ysize - y0);
        return (new Point(x0, y0), new Size(xsize, ysize));
    }
}
