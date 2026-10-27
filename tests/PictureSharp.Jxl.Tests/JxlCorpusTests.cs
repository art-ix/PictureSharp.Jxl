using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;
using System.Text.Json;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>Generated files (tests/assets/generate-corpus.py) compared with the jxlinfo v0.12.0 oracle in corpus.json.</summary>
public sealed class JxlCorpusTests
{
    private static readonly JsonElement Oracle = JsonDocument.Parse(File.ReadAllText(CorpusPath("corpus.json"))).RootElement.GetProperty("files");

    public static IEnumerable<string> Files() => Oracle.EnumerateObject().Select(file => file.Name);

    [Test]
    public async Task Corpus_IsComplete()
    {
        string[] onDisk = [.. Directory.GetFiles(CorpusPath(""), "*.jxl").Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        await Assert.That(onDisk).IsEquivalentTo(Files().Order(StringComparer.Ordinal));
        await Assert.That(onDisk.Length).IsGreaterThanOrEqualTo(28);
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Identify_MatchesJxlinfo(string name)
    {
        JsonElement expected = Oracle.GetProperty(name);
        byte[] data = File.ReadAllBytes(CorpusPath(name));
        ImageInfo info = Image.Identify(data, Options());
        await Assert.That(info.Format).IsSameReferenceAs(ImageFormat.Jxl);
        await Assert.That(info.Width).IsEqualTo(expected.GetProperty("width").GetInt32());
        await Assert.That(info.Height).IsEqualTo(expected.GetProperty("height").GetInt32());
        await Assert.That(info.Color.BitsPerSample).IsEqualTo(expected.GetProperty("bits").GetInt32());
        await Assert.That(info.Color.HasAlpha).IsEqualTo(expected.GetProperty("alpha").GetBoolean());
        await Assert.That(info.IsAnimated).IsEqualTo(expected.GetProperty("animated").GetBoolean());
        await Assert.That(info.FrameCount).IsEqualTo(info.IsAnimated ? null : 1);
        await Assert.That(info.Orientation).IsEqualTo((ExifOrientation)expected.GetProperty("orientation").GetInt32());
        await Assert.That(Image.Identify(CorpusPath(name), Options()).Width).IsEqualTo(info.Width);
        using var stream = new MemoryStream(data);
        await Assert.That(Image.Identify(stream, Options()).Height).IsEqualTo(info.Height);
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Headers_MatchJxlinfo(string name)
    {
        JsonElement expected = Oracle.GetProperty(name);
        JxlContainer container = JxlContainer.Parse(File.ReadAllBytes(CorpusPath(name)));
        JxlHeaders headers = JxlHeadersReader.Read(container.Codestream);
        JxlImageMetadata metadata = headers.Metadata;
        await Assert.That(container.IsBoxed).IsEqualTo(expected.GetProperty("boxes").GetArrayLength() > 0);
        await Assert.That(metadata.BitDepth.FloatingPoint).IsEqualTo(expected.GetProperty("float").GetBoolean());
        await Assert.That(metadata.ExtraChannels.Count).IsEqualTo(expected.GetProperty("extraChannels").GetInt32());
        await Assert.That(metadata.ToneMapping.IntensityTarget).IsEqualTo(expected.GetProperty("intensityTarget").GetSingle());
        JxlColorEncoding color = metadata.Color;
        await Assert.That(color.WantIcc).IsEqualTo(expected.GetProperty("icc").GetBoolean());
        await Assert.That(headers.IccSize.HasValue).IsEqualTo(color.WantIcc);
        if (color.WantIcc)
        {
            return;
        }

        await Assert.That(Name(color.ColorSpace)).IsEqualTo(expected.GetProperty("colorSpace").GetString());
        await Assert.That(Name(color.WhitePoint)).IsEqualTo(expected.GetProperty("whitePoint").GetString());
        await Assert.That(color.HasPrimaries ? Name(color.Primaries) : null).IsEqualTo(expected.GetProperty("primaries").GetString());
        await Assert.That(TransferName(color)).IsEqualTo(expected.GetProperty("transferFunction").GetString());
        await Assert.That(color.RenderingIntent.ToString()).IsEqualTo(expected.GetProperty("renderingIntent").GetString());
    }

    // The names jxlinfo prints.
    private static string Name(JxlColorSpace value) => value switch
    {
        JxlColorSpace.Gray => "Grayscale",
        _ => value.ToString().ToUpperInvariant()
    };

    private static string Name(JxlWhitePoint value) => value == JxlWhitePoint.Dci ? "DCI" : value.ToString();

    private static string Name(JxlPrimaries value) => value switch
    {
        JxlPrimaries.Srgb => "sRGB",
        JxlPrimaries.Bt2100 => "Rec.2100",
        _ => value.ToString()
    };

    private static string TransferName(JxlColorEncoding color) => color.Gamma != 0
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"gamma({color.Gamma / (double)JxlColorEncoding.GammaScale:F6})")
        : color.TransferFunction switch
        {
            JxlTransferFunction.Srgb => "sRGB",
            JxlTransferFunction.Pq => "PQ",
            JxlTransferFunction.Hlg => "HLG",
            JxlTransferFunction.Bt709 => "709",
            JxlTransferFunction.Dci => "DCI",
            _ => color.TransferFunction.ToString()
        };

    [Test]
    public async Task OutOfOrderParts_GiveTheSameCodestream()
    {
        byte[] Joined(string name) => [.. JxlContainer.Parse(File.ReadAllBytes(CorpusPath(name))).Codestream.Segments.ToArray().SelectMany(s => s.ToArray())];
        JxlContainer swapped = JxlContainer.Parse(File.ReadAllBytes(CorpusPath("jxlp-v1-out-of-order.jxl")));
        await Assert.That(swapped.FileFormatVersion).IsEqualTo(1u);
        await Assert.That(Joined("jxlp-v1-out-of-order.jxl")).IsEquivalentTo(Joined("jxlp-v1.jxl"));
    }

    /// <summary>
    /// Bit flips in the headers and every truncation fail only with <see cref="ImageFormatException"/>. The parsers are
    /// called directly, because the library would wrap other exception types as corrupt data and hide the bug.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task DamagedFiles_FailOnlyWithFormatErrors(string name)
    {
        byte[] data = File.ReadAllBytes(CorpusPath(name));
        var unexpected = new List<string>();
        void Check(ReadOnlyMemory<byte> input, string label)
        {
            try
            {
                JxlHeadersReader.Read(JxlContainer.Parse(input).Codestream);
            }
            catch (ImageFormatException)
            {
            }
            catch (Exception exception)
            {
                unexpected.Add($"{label}: {exception.GetType().Name} {exception.Message}");
            }
        }

        for (int length = 0; length < data.Length; length++)
        {
            Check(data.AsMemory(0, length), $"prefix {length}");
        }

        for (int i = 0; i < Math.Min(data.Length, 96); i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] damaged = [.. data];
                damaged[i] ^= (byte)(1 << bit);
                Check(damaged, $"byte {i} bit {bit}");
            }
        }

        await Assert.That(unexpected).IsEmpty();
    }
}
