using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PictureSharp;
using PictureSharp.Formats.Jxl;

// Prints one line per observable result (pixels, encoded bytes, metadata, exceptions) so two builds can be diffed.
// Usage: golden <repository root> [regex]; with a regex only results whose "name kind" matches are computed.
// Optional inputs from artifacts/speed-inputs (scripts/make-speed-inputs.py) are
// included when present; samples/private is used for decode only. Images deeper than 8 bits are also decoded with
// PreserveBitDepth (Rgba64) and encoded from that; 16-bit sources run a shorter encoder sweep.
if (args.Length is not (1 or 2))
{
    Console.Error.WriteLine("Usage: golden <repository root> [regex]");
    return 2;
}

System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
string root = Path.GetFullPath(args[0]);
var filter = args.Length == 2 ? new System.Text.RegularExpressions.Regex(args[1]) : null;
ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();
var output = new StringBuilder();
void Line(string name, string kind, Func<string> result)
{
    if (filter is null || filter.IsMatch($"{name} {kind}"))
    {
        output.AppendLine($"{name} {kind} {Try(result)}");
    }
}

IEnumerable<string> Files(string directory, string pattern) =>
    Directory.Exists(Path.Combine(root, directory))
        ? Directory.GetFiles(Path.Combine(root, directory), pattern, SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        : [];

string[] decodeFiles = [.. Files("tests/assets/corpus", "*.jxl"), .. Files("samples", "*.jxl"), .. Files("artifacts/speed-inputs", "*.jxl")];
foreach (string file in decodeFiles)
{
    string name = Path.GetRelativePath(root, file);
    byte[] data = File.ReadAllBytes(file);
    bool big = data.Length > 2_000_000 || name.Contains("photo12", StringComparison.Ordinal);
    Line(name, "identify", () => Image.Identify(data).ToString());
    Line(name, "meta", () => Meta(Image.ReadMetadata((ReadOnlyMemory<byte>)data)));
    Line(name, "decode", () => { using var image = Image.Load(data); return Pixels(image) + " " + Meta(image.Metadata); });
    Line(name, "srgb", () =>
    {
        using var image = Image.Load(data, new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });
        return Pixels(image) + " " + Meta(image.Metadata);
    });
    Line(name, "edit", () => Hash(Image.EditMetadata((ReadOnlyMemory<byte>)data, m =>
    {
        m.Orientation = ExifOrientation.RightTop;
        m.Xmp = "<x/>"u8.ToArray();
    })));
    Line(name, "tojpeg", () => { using var jpeg = new MemoryStream(); JxlTranscoder.ToJpeg(data, jpeg); return Hash(jpeg.ToArray()); });
    if (!big)
    {
        Line(name, "lossless", () => { using var image = Image.Load(data); return Hash(image.ToJxl(new JxlEncoderOptions { Distance = 0 })); });
        Line(name, "losslessC", () =>
        {
            using var image = Image.Load(data);
            return Hash(image.ToJxl(new JxlEncoderOptions { Distance = 0, CompressMetadata = true }));
        });
        Line(name, "lossy", () => { using var image = Image.Load(data); return Hash(image.ToJxl()); });
    }

    if (Deep(data))
    {
        var preserve = new DecoderOptions { PreserveBitDepth = true };
        Line(name, "decode16", () => { using var image = Image.Load(data, preserve); return Pixels(image) + " " + Meta(image.Metadata); });
        Line(name, "srgb16", () =>
        {
            using var image = Image.Load(data, preserve with { ColorConversion = ColorConversionMode.ToSrgb });
            return Pixels(image) + " " + Meta(image.Metadata);
        });
        if (!big)
        {
            Line(name, "lossless16", () => { using var image = Image.Load(data, preserve); return Hash(image.ToJxl(new JxlEncoderOptions { Distance = 0 })); });
            Line(name, "lossy16", () => { using var image = Image.Load(data, preserve); return Hash(image.ToJxl()); });
        }
    }

    Console.Error.Write('.');
}

foreach (string file in Files("tests/assets/corpus", "*.jpg"))
{
    string name = Path.GetRelativePath(root, file);
    byte[] data = File.ReadAllBytes(file);
    Line(name, "fromjpeg", () => { using var jxl = new MemoryStream(); JxlTranscoder.FromJpeg(data, jxl); return Hash(jxl.ToArray()); });
}

// Encoder sweep: synthetic patterns plus optional photo/screen crops, lossless and lossy at several distances/efforts.
var sources = new List<(string Name, Image Image)>();
foreach ((int width, int height, int kind) in new[] { (300, 200, 0), (513, 260, 1), (700, 300, 2), (257, 600, 3), (64, 64, 4), (1, 1, 0), (1030, 9, 2) })
{
    sources.Add(($"synthetic{kind}-{width}x{height}", Synthetic(width, height, kind)));
}

foreach (string file in Files("artifacts/speed-inputs", "crop-*.png"))
{
    sources.Add((Path.GetRelativePath(root, file), Image.Load(file)));
}

int[] efforts = [1, 2, 3, 5, 7, 9];
float[] distances = [0, 0.5f, 1, 3, 10];
foreach ((string name, Image image) in sources)
{
    foreach (float distance in distances)
    {
        foreach (int effort in efforts)
        {
            Line(name, $"encode-d{distance}-e{effort}", () => Hash(image.ToJxl(new JxlEncoderOptions { Distance = distance, Effort = effort })));
        }
    }

    image.Dispose();
    Console.Error.Write('+');
}

// 16-bit sweep: lossless e1/e3/e7 and lossy d1 e3/e7 on Rgba64 sources (RGB, alpha, gray).
var sources16 = new List<(string Name, Image Image)>();
foreach ((int width, int height, int kind) in new[] { (300, 200, 0), (700, 300, 2), (257, 600, 3), (64, 64, 4), (1, 1, 0) })
{
    sources16.Add(($"synthetic16-{kind}-{width}x{height}", Synthetic16(width, height, kind)));
}

foreach (string file in Files("artifacts/speed-inputs", "crop16-*.png"))
{
    sources16.Add((Path.GetRelativePath(root, file), Image.Load(file, new DecoderOptions { PreserveBitDepth = true })));
}

foreach ((string name, Image image) in sources16)
{
    Line(name, "pixels", () => Pixels(image));
    foreach ((float distance, int effort) in new[] { (0f, 1), (0f, 3), (0f, 7), (1f, 3), (1f, 7) })
    {
        Line(name, $"encode-d{distance}-e{effort}", () => Hash(image.ToJxl(new JxlEncoderOptions { Distance = distance, Effort = effort })));
    }

    image.Dispose();
    Console.Error.Write('+');
}

Console.Error.WriteLine();
Console.Out.Write(output.ToString());
return 0;

static string Hash(ReadOnlySpan<byte> bytes) => $"{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))[..16]}";

static string Pixels(Image image) => image.PixelType == PixelType.Rgba64
    ? $"{image.Width}x{image.Height} rgba64 {Hash(MemoryMarshal.AsBytes(image.GetPixelMemory<Rgba64>().Span))}"
    : $"{image.Width}x{image.Height} {Hash(MemoryMarshal.AsBytes(image.GetPixelMemory<Rgba32>().Span))}";

static bool Deep(byte[] data)
{
    try
    {
        return Image.Identify(data).Color.BitsPerSample > 8;
    }
    catch (Exception)
    {
        return false;
    }
}

static string Meta(ImageMetadata m) =>
    $"icc={(m.IccProfile is { } i ? Hash(i) : "-")} exif={(m.Exif is { } e ? Hash(e) : "-")} xmp={(m.Xmp is { } x ? Hash(x) : "-")} " +
    $"o={m.Orientation} g={m.Gamma} c={m.Chromaticities} s={m.SrgbRenderingIntent}";

static string Try(Func<string> action)
{
    try
    {
        return action();
    }
    catch (Exception ex)
    {
        return $"{ex.GetType().Name}: {ex.Message}";
    }
}

static Image Synthetic(int width, int height, int kind)
{
    var image = new Image(width, height);
    Span<Rgba32> pixels = image.GetPixelMemory<Rgba32>().Span;
    for (int i = 0; i < pixels.Length; i++)
    {
        int x = i % width, y = i / width;
        int v = (x * 31 + y * 17) % 251;
        pixels[i] = kind switch
        {
            0 => new((byte)x, (byte)(y * 3), (byte)(x ^ y), 255),
            1 => new((byte)(x / 40 * 30), (byte)(y / 50 * 40), 7, 255),
            2 => new((byte)(x * y), (byte)(x + y), (byte)(x * 7), (byte)(y * 5)),
            3 => new((byte)v, (byte)v, (byte)v, 255),
            _ => new((byte)(i * 37), (byte)(i * 101), (byte)(i * 13), (byte)(i % 3 == 0 ? 0 : 255)),
        };
    }

    return image;
}

static Image Synthetic16(int width, int height, int kind)
{
    var image = new Image(width, height, PixelType.Rgba64);
    Span<Rgba64> pixels = image.GetPixelMemory<Rgba64>().Span;
    for (int i = 0; i < pixels.Length; i++)
    {
        int x = i % width, y = i / width;
        ushort v = (ushort)((x * 31 + y * 17) * 1013);
        pixels[i] = kind switch
        {
            0 => new((ushort)(x * 211 + y * 7), (ushort)(y * 389 ^ x * 13), (ushort)(x * y * 7), ushort.MaxValue),
            2 => new((ushort)(x * y * 3), (ushort)((x + y) * 97), (ushort)(x * 701), (ushort)(y * 65535 / (height - 1))),
            3 => new(v, v, v, ushort.MaxValue),
            _ => new((ushort)(i * 3701), (ushort)(i * 10103), (ushort)(i * 1301), (ushort)(i % 3 == 0 ? 0 : 40000 + i)),
        };
    }

    return image;
}
