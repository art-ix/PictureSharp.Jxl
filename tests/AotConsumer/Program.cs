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
try
{
    using var image = Image.Load(data);
    throw new InvalidOperationException("Version 0.2.1 unexpectedly decoded an image.");
}
catch (NotSupportedException exception) when (exception.Message.Contains("0.2.1", StringComparison.Ordinal)) { }
Console.WriteLine("Package consumer: registration, detection, Identify and unsupported decoding passed.");
