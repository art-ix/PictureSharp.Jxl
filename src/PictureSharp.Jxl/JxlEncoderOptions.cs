namespace PictureSharp.Formats.Jxl;

/// <summary>
/// Options for the JPEG XL encoder, which writes the root frame losslessly as one 8-bit Modular frame: gray when every
/// pixel is gray (unless an RGB ICC profile is written), with an alpha channel when any pixel is not opaque, keeping the
/// colour of fully transparent pixels. When <see cref="EncoderOptions.Metadata"/> allows them, the ICC profile is stored
/// in the codestream and EXIF and XMP in Exif and xml boxes of a container; without an ICC profile the colour
/// description (gamma, chromaticities, sRGB) becomes the enumerated colour encoding. The orientation is stored in the
/// codestream, where JPEG XL readers apply it.
/// </summary>
/// <example>
/// <code>
/// image.SaveAsJxl("photo.jxl", new JxlEncoderOptions { CompressMetadata = true });
/// </code>
/// </example>
public sealed record JxlEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => JxlFormat.Descriptor;

    /// <summary>
    /// Whether EXIF and XMP are Brotli-compressed into brob boxes, as cjxl does by default. Readers without Brotli
    /// support then cannot see them. Default: false.
    /// </summary>
    public bool CompressMetadata { get; init; }
}
