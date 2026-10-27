using PictureSharp;
using PictureSharp.Formats.Jxl;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();
// tests/assets/corpus/tiny-1x1.jxl: a 1×1 lossless RGB codestream.
byte[] data = [0xFF, 0x0A, 0x00, 0x10, 0x10, 0x09, 0x08, 0x00, 0x01, 0x00, 0x18, 0x00, 0x4B, 0x18, 0x8B, 0x15, 0x82, 0x01];
if (Image.DetectFormat(data) != ImageFormat.Jxl || ImageConfiguration.Default.CanEncode(ImageFormat.Jxl))
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
    if (image.Width != 1 || image.Height != 1 || image.HasAlpha || image.GetPixelMemory().Span[0] != new Rgba32(0, 0, 0, 255))
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
    foreach (Rgba32 pixel in image.GetPixelMemory().Span)
    {
        if (image.Width != 8 || Math.Abs(pixel.R - 200) > 2 || Math.Abs(pixel.G - 100) > 2 || Math.Abs(pixel.B - 51) > 2 || pixel.A != 255)
            throw new InvalidOperationException($"Lossy decoding returned {pixel}.");
    }
}
Console.WriteLine("Package consumer: registration, detection, Identify, lossless and lossy decoding passed.");
