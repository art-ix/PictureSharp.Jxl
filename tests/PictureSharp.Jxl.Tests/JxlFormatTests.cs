namespace PictureSharp.Formats.Jxl.Tests;

public sealed class JxlFormatTests
{
    private static byte[] Container => [0, 0, 0, 12, 0x4A, 0x58, 0x4C, 0x20, 13, 10, 0x87, 10];

    [Test]
    public async Task Signatures_AcceptOnlyCompleteMarkers()
    {
        var module = new JxlFormat();
        foreach (byte[] signature in new byte[][] { [0xFF, 0x0A], Container })
        {
            await Assert.That(module.IsSupported(signature)).IsTrue();
            await Assert.That(module.IsSupported([.. signature, 123])).IsTrue();
            for (int length = 0; length < signature.Length; length++)
                await Assert.That(module.IsSupported(signature.AsSpan(0, length))).IsFalse();
            for (int i = 0; i < signature.Length; i++)
            {
                byte[] damaged = (byte[])signature.Clone();
                damaged[i] ^= 1;
                await Assert.That(module.IsSupported(damaged)).IsFalse();
            }
        }
    }

    [Test]
    public async Task OtherFormats_AreNotJxl()
    {
        foreach (byte[] header in new byte[][] {
            [0xFF, 0xD8, 0xFF, 0xE0],
            [0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63],
            [0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20, 13, 10, 0x87, 10],
            [0xFF, 0x4F, 0xFF, 0x51], [0x89, 0x50, 0x4E, 0x47] })
            await Assert.That(new JxlFormat().IsSupported(header)).IsFalse();
    }

    [Test]
    public async Task Registration_UsesSharedDescriptor_IdentifiesReadsAndEditsMetadata()
    {
        IImageFormatModule module = new JxlFormat();
        var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(module).Build();
        await Assert.That(module.Format).IsSameReferenceAs(new JxlFormat().Format);
        await Assert.That(module.Format).IsSameReferenceAs(ImageFormat.Jxl);
        await Assert.That(module.HeaderSize).IsEqualTo(12);
        await Assert.That(module.Format.Name).IsEqualTo("JPEG XL");
        await Assert.That(module.Format.MimeType).IsEqualTo("image/jxl");
        await Assert.That(configuration.FromExtension("photo.JXL")).IsSameReferenceAs(ImageFormat.Jxl);
        await Assert.That(configuration.FromMimeType("image/jxl")).IsSameReferenceAs(ImageFormat.Jxl);
        await Assert.That(configuration.CanDecode(ImageFormat.Jxl)).IsTrue();
        await Assert.That(configuration.CanEncode(ImageFormat.Jxl)).IsTrue();
        await Assert.That(module.Encoder!.CreateDefaultOptions()).IsTypeOf<JxlEncoderOptions>();
        await Assert.That(module.MetadataCodec).IsNotNull();
        var options = new DecoderOptions { Configuration = configuration };
        byte[] data = JxlTestData.Minimal();
        ImageInfo info = Image.Identify(data, options);
        await Assert.That((info.Format, info.Width, info.Height, info.Color.BitsPerSample)).IsEqualTo((ImageFormat.Jxl, 8, 8, 8));
        await Assert.That(() => Image.Identify(new byte[] { 0xFF, 0x0A }, options)).Throws<ImageFormatException>();
        // Headers without a frame.
        await Assert.That(() => Image.Load(data, options)).Throws<ImageFormatException>();
        // Metadata needs only the headers; this image has none (sRGB, no boxes, orientation 1).
        await Assert.That(Image.ReadMetadata((ReadOnlyMemory<byte>)data, options).IsEmpty).IsTrue();
        // Editing needs only the headers too: the XMP box turns the codestream into a container.
        byte[] edited = Image.EditMetadata((ReadOnlyMemory<byte>)data, metadata => metadata.Xmp = "<x/>"u8.ToArray(), options);
        await Assert.That(Image.ReadMetadata((ReadOnlyMemory<byte>)edited, options).Xmp).IsEquivalentTo("<x/>"u8.ToArray());
    }

    [Test]
    [NotInParallel]
    public async Task Detection_UsesMemoryFileAndStream()
    {
        var previous = ImageConfiguration.Default;
        string path = Path.GetTempFileName();
        try
        {
            ImageConfiguration.Default = previous.ToBuilder().AddFormat(new JxlFormat()).Build();
            using var image = new Image(1, 1);
            using var output = new MemoryStream();
            image.Save(output, ImageFormat.Jxl);
            await Assert.That(Image.DetectFormat(output.ToArray())).IsSameReferenceAs(ImageFormat.Jxl);
            foreach (byte[] signature in new byte[][] { [0xFF, 0x0A], Container })
            {
                File.WriteAllBytes(path, signature);
                using var stream = new MemoryStream(signature);
                await Assert.That(Image.DetectFormat(signature)).IsSameReferenceAs(ImageFormat.Jxl);
                await Assert.That(Image.DetectFormat(path)).IsSameReferenceAs(ImageFormat.Jxl);
                await Assert.That(Image.DetectFormat(stream)).IsSameReferenceAs(ImageFormat.Jxl);
            }
        }
        finally
        {
            ImageConfiguration.Default = previous;
            File.Delete(path);
        }
    }
}
