using System.Drawing;

namespace PictureSharp.Formats.Jxl;

/// <summary>One layer of a <see cref="JxlLayeredImage"/>: a frame at its own size, before blending.</summary>
public sealed class JxlLayer
{
    internal JxlLayer(Image image, Point offset, string name, JxlLayerBlendMode blendMode, bool clamp)
    {
        Image = image;
        Offset = offset;
        Name = name;
        BlendMode = blendMode;
        Clamp = clamp;
    }

    /// <summary>The layer's pixels at its own size, with its colour description; owned by the <see cref="JxlLayeredImage"/>.</summary>
    public Image Image { get; }

    /// <summary>The position of the layer's top-left pixel on the canvas; may be negative or outside the canvas.</summary>
    public Point Offset { get; }

    /// <summary>The frame name, or an empty string.</summary>
    public string Name { get; }

    /// <summary>How the layer is blended onto the layers below it.</summary>
    public JxlLayerBlendMode BlendMode { get; }

    /// <summary>Whether blended values are clamped to [0, 1].</summary>
    public bool Clamp { get; }
}

/// <summary>How a layer is blended onto the layers below it (JPEG XL frame blending modes).</summary>
public enum JxlLayerBlendMode
{
    /// <summary>The layer replaces the pixels below it.</summary>
    Replace = 0,

    /// <summary>The layer's samples are added to the samples below it.</summary>
    Add = 1,

    /// <summary>Alpha blending: the layer is drawn over the pixels below it by its alpha.</summary>
    Blend = 2,

    /// <summary>The layer's colour, weighted by its alpha, is added to the colour below it; alpha is kept.</summary>
    AlphaWeightedAdd = 3,

    /// <summary>The samples are multiplied.</summary>
    Multiply = 4,
}

/// <summary>The description of a layered JPEG XL image from <see cref="JxlLayers.Identify(ReadOnlyMemory{byte}, DecoderOptions)"/>.</summary>
/// <param name="Image">The image information as <c>Image.Identify</c> reports it.</param>
/// <param name="Layers">The layers, bottom first, placed as <see cref="JxlLayers.Load(ReadOnlyMemory{byte}, DecoderOptions, CancellationToken)"/> places them.</param>
public sealed record JxlLayeredImageInfo(ImageInfo Image, IReadOnlyList<JxlLayerInfo> Layers);

/// <summary>One layer of a layered JPEG XL image, without pixels.</summary>
/// <param name="Name">The frame name, or an empty string.</param>
/// <param name="Offset">The position of the layer's top-left pixel on the canvas; may be negative.</param>
/// <param name="Size">The size of the layer's image.</param>
/// <param name="BlendMode">How the layer is blended onto the layers below it.</param>
/// <param name="Clamp">Whether blended values are clamped to [0, 1].</param>
public sealed record JxlLayerInfo(string Name, Point Offset, Size Size, JxlLayerBlendMode BlendMode, bool Clamp);
