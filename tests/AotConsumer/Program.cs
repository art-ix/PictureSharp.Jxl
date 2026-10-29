using System.Runtime.Intrinsics.X86;
using PictureSharp;
using PictureSharp.Formats.Jxl;

Console.WriteLine($"JXL consumer ISA: AVX2={Avx2.IsSupported}; SSE4.1={Sse41.IsSupported}; FMA={Fma.IsSupported}");

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();
// tests/assets/corpus/tiny-1x1.jxl: a 1×1 lossless RGB codestream.
byte[] data = [0xFF, 0x0A, 0x00, 0x10, 0x10, 0x09, 0x08, 0x00, 0x01, 0x00, 0x18, 0x00, 0x4B, 0x18, 0x8B, 0x15, 0x82, 0x01];
if (Image.DetectFormat(data) != ImageFormat.Jxl || !ImageConfiguration.Default.CanEncode(ImageFormat.Jxl))
    throw new InvalidOperationException("Registration or detection failed.");
ImageInfo info = Image.Identify(data);
if (info.Format != ImageFormat.Jxl || info.Width != 1 || info.Height != 1 || info.Color.BitsPerSample != 8 || info.Color.HasAlpha
    || info.Color.Model != ImageColorModel.Rgb || info.Compression != ImageCompression.Unknown)
    throw new InvalidOperationException($"Identify returned {info}.");
try
{
    Image.Identify(data.AsSpan(0, 4));
    throw new InvalidOperationException("A truncated header was accepted.");
}
catch (ImageFormatException) { }
using (var image = Image.Load(data))
{
    if (image.Width != 1 || image.Height != 1 || image.HasAlpha || image.GetPixelMemory<Rgba32>().Span[0] != new Rgba32(0, 0, 0, 255))
        throw new InvalidOperationException("Lossless decoding returned the wrong pixel.");
}
// An 8×8 lossy (VarDCT, XYB) codestream of the colour (200, 100, 50), from cjxl -d 1.
byte[] lossy =
[
    0xFF, 0x0A, 0x41, 0x06, 0x00, 0x13, 0x88, 0x02, 0x00, 0xC4, 0x00, 0xB5, 0x9F, 0x20, 0x00, 0x00, 0x15, 0x2A, 0xA3, 0x8C,
    0x1B, 0xBC, 0x9C, 0xEB, 0xF9, 0xF2, 0x43, 0x87, 0xC5, 0xB4, 0x8D, 0xEB, 0x0C, 0x6D, 0xB5, 0x6D, 0x61, 0x09, 0x63, 0xB3,
    0xBD, 0x30, 0x48, 0x48, 0x38, 0x83, 0x8D, 0x8B, 0x66, 0xBB, 0x39, 0x3E, 0x23, 0xE9, 0x30, 0x20, 0x21, 0x95, 0x24, 0x00,
];
using (var image = Image.Load(lossy))
{
    foreach (Rgba32 pixel in image.GetPixelMemory<Rgba32>().Span)
    {
        if (image.Width != 8 || Math.Abs(pixel.R - 200) > 2 || Math.Abs(pixel.G - 100) > 2 || Math.Abs(pixel.B - 51) > 2 || pixel.A != 255)
            throw new InvalidOperationException($"Lossy decoding returned {pixel}.");
    }
}
// The 1×1 codestream in a container with a Brotli-compressed Exif box (brob): Exif with Make "AOT" and orientation 6.
byte[] tiff = [0, 0, 0, 0, .. "MM\0*"u8, 0, 0, 0, 8, 0, 2, 0x01, 0x0F, 0, 2, 0, 0, 0, 4, (byte)'A', (byte)'O', (byte)'T', 0,
    0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0, 0, 0];
byte[] compressed = new byte[256];
if (!System.IO.Compression.BrotliEncoder.TryCompress(tiff, compressed, out int compressedLength))
    throw new InvalidOperationException("Brotli compression failed.");
static byte[] Box(string type, params byte[][] parts)
{
    byte[] payload = [.. parts.SelectMany(part => part)];
    byte[] box = new byte[8 + payload.Length];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
    System.Text.Encoding.ASCII.GetBytes(type, box.AsSpan(4));
    payload.CopyTo(box, 8);
    return box;
}
byte[] container = [0, 0, 0, 12, 0x4A, 0x58, 0x4C, 0x20, 13, 10, 0x87, 10, .. Box("ftyp", "jxl "u8.ToArray(), [0, 0, 0, 0], "jxl "u8.ToArray()),
    .. Box("brob", "Exif"u8.ToArray(), compressed[..compressedLength]), .. Box("jxlc", data)];
ImageMetadata metadata = Image.ReadMetadata((ReadOnlyMemory<byte>)container);
// The codestream orientation (1) overrides the EXIF tag (6).
if (!metadata.ExifProfile.TryGetValue(ExifTag.Make, out string? make) || make != "AOT" || metadata.Orientation != ExifOrientation.TopLeft)
    throw new InvalidOperationException("Brotli-compressed EXIF was not read.");
// Lossless encoding keeps every sample, including the colour of transparent pixels, and the metadata.
using (var source = new Image(37, 21))
{
    Span<Rgba32> pixels = source.GetPixelMemory<Rgba32>().Span;
    for (int i = 0; i < pixels.Length; i++) pixels[i] = new Rgba32((byte)(i * 7), (byte)(i * 13), (byte)(i >> 2), (byte)(i % 3 == 0 ? 0 : i));
    source.Metadata.Xmp = "<x/>"u8.ToArray();
    byte[] encoded = source.ToJxl(new JxlEncoderOptions { CompressMetadata = true, Distance = 0 });
    using var decoded = Image.Load(encoded);
    if (!decoded.GetPixelMemory<Rgba32>().Span.SequenceEqual(source.GetPixelMemory<Rgba32>().Span) || !decoded.Metadata.Xmp!.AsSpan().SequenceEqual("<x/>"u8))
        throw new InvalidOperationException("Lossless encoding did not round-trip.");
    byte[] edited = Image.EditMetadata((ReadOnlyMemory<byte>)encoded, edit => edit.Orientation = ExifOrientation.RightTop);
    if (Image.Identify(edited).Orientation != ExifOrientation.RightTop)
        throw new InvalidOperationException("Editing the metadata failed.");
}
// The default lossy encoding stays close to the image and keeps alpha exact.
using (var source = new Image(96, 64))
{
    Span<Rgba32> pixels = source.GetPixelMemory<Rgba32>().Span;
    for (int i = 0; i < pixels.Length; i++) pixels[i] = new Rgba32((byte)(i % 96 * 2), (byte)(i / 96 * 3), 120, (byte)(i % 96 < 24 ? 0 : 255));
    using var decoded = Image.Load(source.ToJxl());
    ReadOnlySpan<Rgba32> actual = decoded.GetPixelMemory<Rgba32>().Span;
    for (int i = 0; i < pixels.Length; i++)
    {
        if (actual[i].A != pixels[i].A || pixels[i].A != 0 && Math.Abs(actual[i].R - pixels[i].R) + Math.Abs(actual[i].G - pixels[i].G) > 24)
            throw new InvalidOperationException("Lossy encoding did not round-trip.");
    }
}
// Exercise both 16-bit encoder paths and typed pixel access after trimming and Native AOT.
using (var source = new Image(48, 32, PixelType.Rgba64))
{
    Span<Rgba64> pixels = source.GetPixelSpan<Rgba64>();
    for (int i = 0; i < pixels.Length; i++) pixels[i] = new Rgba64((ushort)(i * 37), (ushort)(i * 31), (ushort)(i * 17), (ushort)(i * 41));
    foreach (float distance in new[] { 0f, 1f })
    {
        using Image decoded = Image.Load(source.ToJxl(new JxlEncoderOptions { Distance = distance, Effort = 1 }), new DecoderOptions { PreserveBitDepth = true });
        if (decoded.PixelType != PixelType.Rgba64 || distance == 0 && !decoded.GetPixelSpan<Rgba64>().SequenceEqual(pixels))
            throw new InvalidOperationException("16-bit JPEG XL did not round-trip.");
        for (int i = 0; i < pixels.Length; i++)
            if (pixels[i].A != decoded.GetPixelSpan<Rgba64>()[i].A) throw new InvalidOperationException("16-bit alpha changed.");
    }
}
// Consume the package's public coefficient transcoder, including cjxl-generated JXL and asynchronous APIs.
foreach (string name in new[] { "gray-1", "metadata-1" })
{
    string folder = Path.Combine(AppContext.BaseDirectory, "transcoding");
    byte[] original = File.ReadAllBytes(Path.Combine(folder, name + ".jpg"));
    using var encoded = new MemoryStream();
    JxlTranscoder.FromJpeg(original, encoded);
    using var restored = new MemoryStream();
    JxlTranscoder.ToJpeg(encoded.ToArray(), restored);
    if (!restored.ToArray().AsSpan().SequenceEqual(original)) throw new InvalidOperationException("JPEG transcoding changed original bytes.");
    using Image rendered = Image.Load(encoded.ToArray());
    if (rendered.Width == 0) throw new InvalidOperationException("Transcoded JPEG cannot be rendered.");
    using var reference = File.OpenRead(Path.Combine(folder, name + ".jxl"));
    restored.SetLength(0);
    JxlTranscoder.ToJpeg(reference, restored);
    if (!restored.ToArray().AsSpan().SequenceEqual(original)) throw new InvalidOperationException("cjxl JPEG reconstruction changed original bytes.");
    encoded.SetLength(0);
    JxlTranscoder.FromJpegAsync(original.AsMemory(), encoded).GetAwaiter().GetResult();
    encoded.Position = 0; restored.SetLength(0);
    JxlTranscoder.ToJpegAsync(encoded, restored).GetAwaiter().GetResult();
    if (!restored.ToArray().AsSpan().SequenceEqual(original)) throw new InvalidOperationException("Async JPEG reconstruction changed original bytes.");
}
Console.WriteLine("Package consumer: registration, detection, Identify, decoding, encoding, metadata and JPEG transcoding passed.");
