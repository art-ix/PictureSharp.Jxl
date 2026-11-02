namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>
/// The JPEG XL module for one <see cref="JxlLayers"/> call: the core runs <c>Image.Load</c> or <c>Image.Identify</c>
/// with it in place of <see cref="JxlFormat"/>, so the layers are charged through <see cref="ImageDecodeContext"/> and
/// the flattened image is post-processed exactly as <c>Image.Load</c> does. Never registered globally.
/// </summary>
internal sealed class JxlLayerModule(JxlLayerCollector collector) : IImageFormatModule, IImageFormatDecoder
{
    private static readonly JxlFormat Format = new();

    ImageFormat IImageFormatModule.Format => Format.Format;

    public int HeaderSize => Format.HeaderSize;

    public bool IsSupported(ReadOnlySpan<byte> header) => Format.IsSupported(header);

    public IImageFormatDecoder Decoder => this;

    public IImageFormatEncoder Encoder => Format.Encoder;

    public IImageFormatMetadataCodec MetadataCodec => Format.MetadataCodec;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) => collector.Identify(data, options);

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context) => JxlDecoder.Decode(data, context, collector);

    /// <summary>The caller's configuration reduced to this module, so that other formats are rejected as unknown.</summary>
    public ImageConfiguration Configure(ImageConfiguration? configuration)
    {
        ImageConfigurationBuilder builder = (configuration ?? ImageConfiguration.Default).ToBuilder();
        builder = builder.Formats.Contains(JxlDescriptor.Format) ? builder.ReplaceFormat(this) : builder.AddFormat(this);
        return builder.KeepOnly(JxlDescriptor.Format).Build();
    }
}
