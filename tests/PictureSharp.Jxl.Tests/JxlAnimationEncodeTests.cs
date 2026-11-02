using System.Drawing;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// Animation encode (0.8.14, docs/design/0.8.14-animation-encode.md): <c>Image.Save</c> of several frames and
/// <c>Image.CreateFrameWriter</c> write every frame, cropped to what changed, with millisecond durations and the loop
/// count; <c>Image.Load</c>, the frame reader and djxl read the same frames back.
/// </summary>
public sealed class JxlAnimationEncodeTests
{
    private static readonly ImageConfiguration Formats = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();

    private static JxlEncoderOptions Lossless(JxlEncoderOptions? options = null) => (options ?? new()) with { Configuration = Formats, Distance = 0 };

    private static JxlEncoderOptions Lossy(JxlEncoderOptions? options = null) => (options ?? new()) with { Configuration = Formats };

    public static IEnumerable<string> AnimatedFiles() => JxlAnimationTests.AnimatedFiles();

    [Test]
    [MethodDataSource(nameof(AnimatedFiles))]
    public async Task Corpus_LosslessRoundTrip_KeepsFramesDurationsAndLoops(string name)
    {
        using Image source = Image.Load(CorpusPath(name), Options(new DecoderOptions { PreserveBitDepth = true }));
        if (source.Frames.Any(frame => Math.Round(frame.Duration.TotalMilliseconds) > uint.MaxValue))
        {
            // anim-16bit.jxl shows a frame for 2^32 - 1 ticks at 100 ticks per second: beyond 2^32 - 1 ms.
            await Assert.That(() => source.Encode(Lossless())).Throws<NotSupportedException>().WithMessageContaining("ms");
            await Assert.That(async () => await WriteFrames(source, Lossless())).Throws<NotSupportedException>();
            return;
        }

        byte[] saved = source.Encode(Lossless());
        using Image decoded = Image.Load(saved, Options(new DecoderOptions { PreserveBitDepth = true }));
        await AssertShown(decoded, Expected(source), name);
        await Assert.That(decoded.LoopCount).IsEqualTo(source.FrameCount > 1 ? source.LoopCount : 0);

        byte[] streamed = await WriteFrames(source, Lossless());
        using Image read = Image.Load(streamed, Options(new DecoderOptions { PreserveBitDepth = true }));
        await AssertShown(read, Expected(source), name + " (frame writer)");
    }

    [Test]
    public async Task FrameReader_ReadsWrittenAnimation()
    {
        using Image source = Animation(48, 40, 6);
        byte[] data = await WriteFrames(source, Lossless());
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(new MemoryStream(data), Options());
        int index = 0;
        await foreach (ImageFrame frame in reader.ReadFramesAsync())
        {
            await AssertPixels(frame, source.Frames[index], $"frame {index}");
            await Assert.That(frame.Duration).IsEqualTo(source.Frames[index].Duration);
            index++;
        }

        await Assert.That(index).IsEqualTo(6);
        await Assert.That(reader.LoopCount).IsEqualTo(3);
    }

    [Test]
    public async Task ChangedRectangles_AreCroppedFramesOverSlotOne()
    {
        using Image source = Animation(64, 48, 4);
        byte[] data = source.Encode(Lossless());
        List<JxlFrameHeader> frames = Headers(data);
        await Assert.That(frames.Count).IsEqualTo(4);
        await Assert.That(frames[0].IsPartial).IsFalse();
        // Animation() moves a 5x4 square by 3 pixels per frame: the union of both positions changes.
        for (int i = 1; i < frames.Count; i++)
        {
            await Assert.That(frames[i].IsPartial).IsTrue();
            await Assert.That((frames[i].Width, frames[i].Height)).IsEqualTo((8u, 4u));
            await Assert.That(frames[i].Blending.Source).IsEqualTo(1u);
        }

        await Assert.That(frames.Take(3).All(frame => frame.SaveAsReference == 1 && !frame.IsLast)).IsTrue();
        await Assert.That(frames[3].IsLast).IsTrue();
        await Assert.That(frames.Select(frame => frame.Duration)).IsEquivalentTo(new uint[] { 40, 41, 42, 43 });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EdgeChanges_DecodeExactly(bool wide)
    {
        // One pixel, a corner, a full row and column, alpha only, everything, then nothing.
        using Image source = Solid(37, 29, new Rgba32(10, 20, 30), wide);
        Change(source, 0, 0, new Rgba32(200, 0, 0));
        Change(source, 36, 28, new Rgba32(0, 200, 0));
        Change(source, 0, 14, new Rgba32(1, 2, 3), row: true);
        Change(source, 18, 0, new Rgba32(4, 5, 6), column: true);
        Change(source, 5, 5, new Rgba32(10, 20, 30, 0));
        Change(source, 0, 0, new Rgba32(9, 9, 9), everything: true);
        source.Frames.AddClone(source.Frames[^1]);
        byte[] data = source.Encode(Lossless());
        using Image decoded = Image.Load(data, Options(new DecoderOptions { PreserveBitDepth = true }));
        await AssertShown(decoded, Expected(source), "edges");
    }

    [Test]
    public async Task IdenticalFrames_AddTheirDuration()
    {
        using Image source = Animation(16, 16, 2);
        source.Frames.AddClone(source.Frames[1]).Duration = TimeSpan.FromMilliseconds(100);
        source.Frames.AddClone(source.Frames[1]).Duration = TimeSpan.FromMilliseconds(7);
        using Image decoded = Image.Load(source.Encode(Lossless()), Options());
        await Assert.That(decoded.FrameCount).IsEqualTo(2);
        await Assert.That(decoded.Frames[1].Duration).IsEqualTo(TimeSpan.FromMilliseconds(41 + 100 + 7));
    }

    [Test]
    public async Task ZeroDurationFrames_MergeIntoTheNext()
    {
        using Image source = Animation(16, 16, 3);
        source.Frames[1].Duration = TimeSpan.Zero;
        using Image decoded = Image.Load(source.Encode(Lossless()), Options());
        await AssertShown(decoded, Expected(source), "zero");
        await Assert.That(decoded.FrameCount).IsEqualTo(2);
        await AssertPixels(decoded.Frames[1], source.Frames[2], "after the zero-duration frame");
    }

    [Test]
    public async Task Durations_AreRoundedToMilliseconds()
    {
        using Image source = Animation(8, 8, 3);
        source.Frames[0].Duration = TimeSpan.FromTicks(15_000);      // 1.5 ms
        source.Frames[1].Duration = TimeSpan.FromTicks(14_999);
        source.Frames[2].Duration = TimeSpan.FromTicks(4_999);       // the last frame is shown even at 0 ms
        using Image decoded = Image.Load(source.Encode(Lossless()), Options());
        await Assert.That(decoded.Frames.Select(frame => frame.Duration.TotalMilliseconds)).IsEquivalentTo(new double[] { 2, 1, 0 });
        source.Frames[0].Duration = TimeSpan.FromMilliseconds(uint.MaxValue + 1.0);
        await Assert.That(() => source.Encode(Lossless())).Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Lossy_IsCloseToTheSource(bool alpha)
    {
        using Image source = Animation(96, 80, 4, alpha);
        byte[] data = source.Encode(Lossy());
        using Image decoded = Image.Load(data, Options());
        await Assert.That(decoded.FrameCount).IsEqualTo(4);
        await Assert.That(Headers(data).Where(frame => frame.Type == JxlFrameType.Regular).Skip(1).All(frame => frame.IsPartial)).IsTrue();
        for (int i = 0; i < 4; i++)
        {
            double error = MeanError(decoded.Frames[i], source.Frames[i]);
            await Assert.That(error).IsLessThan(2.0);
        }
    }

    [Test]
    public async Task FrameWriter_StreamsToNonSeekableOutput_WithMetadataInJxlpBoxes()
    {
        using Image source = Animation(24, 20, 5);
        var exif = Image.ReadMetadata(CorpusPath("anim-orient6.jxl"), Options()).Exif;
        var output = new WriteOnlyStream();
        await using (ImageFrameWriter writer = Image.CreateFrameWriter(output, source.Size, Lossless(),
                         new ImageFrameWriterOptions { LoopCount = 3, Metadata = new ImageMetadata { Exif = exif, Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray() } }))
        {
            foreach (ImageFrame frame in source.Frames) await writer.WriteAsync(frame);
            await writer.CompleteAsync();
        }

        byte[] data = output.ToArray();
        JxlContainer container = JxlContainer.Parse(data);
        await Assert.That(container.IsBoxed).IsTrue();
        using Image decoded = Image.Load(data, Options());
        await AssertShown(decoded, Expected(source), "streamed");
        await Assert.That(decoded.Metadata.Xmp!.Length).IsGreaterThan(0);
        await Assert.That(decoded.Metadata.Exif).IsNotNull();
    }

    [Test]
    public async Task FrameWriter_SixteenBitFrames_StaySixteenBit()
    {
        using Image source = Solid(20, 10, new Rgba32(1, 2, 3), wide: true);
        Change(source, 3, 3, new Rgba32(4, 5, 6));
        source.Frames[1].GetPixelSpan<Rgba64>()[0] = new Rgba64(1234, 4321, 7, 65535);
        byte[] data = await WriteFrames(source, Lossless());
        using Image decoded = Image.Load(data, Options(new DecoderOptions { PreserveBitDepth = true }));
        await Assert.That(decoded.PixelType).IsEqualTo(PixelType.Rgba64);
        await AssertShown(decoded, Expected(source), "16-bit");
    }

    [Test]
    public async Task FrameWriter_GrayProfile_RejectsColourFrames()
    {
        byte[] grayIcc = Image.ReadMetadata(CorpusPath("icc-lossy-gray.jxl"), Options()).IccProfile!;
        using Image source = Animation(8, 8, 2);
        await using ImageFrameWriter writer = Image.CreateFrameWriter(new MemoryStream(), source.Size, Lossless(),
            new ImageFrameWriterOptions { Metadata = new ImageMetadata { IccProfile = grayIcc } });
        await Assert.That(async () => await writer.WriteAsync(source.Frames[0])).Throws<NotSupportedException>();
    }

    [Test]
    public async Task FrameWriter_DisposedOrCanceled_ReleasesEverything()
    {
        using Image source = Animation(300, 260, 3);
        var output = new MemoryStream();
        await using (ImageFrameWriter writer = Image.CreateFrameWriter(output, source.Size, Lossy()))
        {
            await writer.WriteAsync(source.Frames[0]);
            await writer.WriteAsync(source.Frames[1]);
        }

        // Only the headers and the first frame were written; the second was held back.
        await Assert.That(output.Length).IsGreaterThan(0);
        using var cts = new CancellationTokenSource();
        await using ImageFrameWriter canceled = Image.CreateFrameWriter(new MemoryStream(), source.Size, Lossy());
        await canceled.WriteAsync(source.Frames[0]);
        cts.Cancel();
        await Assert.That(async () => await canceled.WriteAsync(source.Frames[1], cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task StillImages_AreUnchanged()
    {
        using Image source = Animation(40, 30, 1);
        byte[] data = source.Encode(Lossless());
        JxlHeaders headers = JxlHeadersReader.Read(JxlContainer.Parse(data).Codestream);
        await Assert.That(headers.Metadata.Animation).IsNull();
        await Assert.That(Headers(data).Single().IsLast).IsTrue();
    }

    [Test]
    [Category("Oracle")]
    public async Task Written_DecodesLikeDjxl()
    {
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(djxl is not null, "djxl v0.12.0 is not available (PATH or JXL_TOOLS).");
        foreach (bool lossy in new[] { false, true })
        {
            using Image source = Animation(130, 90, 5, alpha: true);
            string path = Path.Combine(Path.GetTempPath(), $"jxl-anim-{Guid.NewGuid():N}.jxl");
            try
            {
                File.WriteAllBytes(path, source.Encode(lossy ? Lossy() : Lossless()));
                using Image ours = Image.Load(path, Options());
                using Image expected = JxlDecodeOracleTests.DjxlImage(djxl!, path);
                await Assert.That(expected.FrameCount).IsEqualTo(5);
                await Assert.That(expected.LoopCount).IsEqualTo(3);
                for (int i = 0; i < 5; i++)
                {
                    using Image actual = ours.Frames.CloneFrame(i);
                    using Image reference = expected.Frames.CloneFrame(i);
                    string? mismatch = lossy ? JxlDecodeTests.Tolerance(actual, reference)
                        : JxlDecodeOracleTests.Rgba(reference).SequenceEqual(JxlDecodeOracleTests.Rgba(source.Frames.CloneFrame(i))) ? null : "pixels differ";
                    if (mismatch is not null) Assert.Fail($"{(lossy ? "lossy" : "lossless")} frame {i}: {mismatch}");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    // A square moving 3 pixels right per frame over a gradient, durations 40, 41, … ms, three plays.
    internal static Image Animation(int width, int height, int frames, bool alpha = false)
    {
        Image image = Frame(width, height, 0, alpha);
        image.Frames.RootFrame.Duration = TimeSpan.FromMilliseconds(40);
        image.LoopCount = 3;
        for (int i = 1; i < frames; i++)
        {
            using Image next = Frame(width, height, i, alpha);
            image.Frames.Add(next, TimeSpan.FromMilliseconds(40 + i));
        }

        return image;
    }

    private static Image Frame(int width, int height, int index, bool alpha)
    {
        var pixels = new Rgba32[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte a = alpha ? (byte)(x * 255 / Math.Max(1, width - 1)) : (byte)255;
                pixels[y * width + x] = new Rgba32((byte)(x * 3), (byte)(y * 5), (byte)(x + y), a);
            }
        }

        for (int y = 2; y < Math.Min(height, 6); y++)
        {
            for (int x = 2 + 3 * index; x < Math.Min(width, 7 + 3 * index); x++) pixels[y * width + x] = new Rgba32(250, 240, 10);
        }

        return Image.LoadPixelData<Rgba32>(pixels, width, height);
    }

    private static Image Solid(int width, int height, Rgba32 color, bool wide)
    {
        var pixels = Enumerable.Repeat(color, width * height).ToArray();
        Image image = Image.LoadPixelData<Rgba32>(pixels, width, height);
        if (!wide) return image;
        var wideFrame = new ImageFrame(width, height, PixelType.Rgba64) { Duration = TimeSpan.FromMilliseconds(10) };
        Span<Rgba64> span = wideFrame.GetPixelSpan<Rgba64>();
        for (int i = 0; i < span.Length; i++) span[i] = new Rgba64((ushort)(color.R * 257), (ushort)(color.G * 257), (ushort)(color.B * 257));
        using (image)
        {
            Image result = Image.LoadPixelData<Rgba64>(wideFrame.GetPixelSpan<Rgba64>(), width, height);
            result.Frames.RootFrame.Duration = TimeSpan.FromMilliseconds(10);
            return result;
        }
    }

    // Appends a copy of the last frame with one pixel (or its row, column or every pixel) set to color.
    private static void Change(Image image, int x, int y, Rgba32 color, bool row = false, bool column = false, bool everything = false)
    {
        ImageFrame frame = image.Frames.AddClone(image.Frames[^1]);
        frame.Duration = TimeSpan.FromMilliseconds(10 + image.FrameCount);
        for (int py = 0; py < frame.Height; py++)
        {
            for (int px = 0; px < frame.Width; px++)
            {
                bool hit = everything || (px == x && py == y) || (row && py == y) || (column && px == x);
                if (!hit) continue;
                if (frame.PixelType == PixelType.Rgba64)
                {
                    frame.SetPixel(px, py, new Rgba64((ushort)(color.R * 257), (ushort)(color.G * 257), (ushort)(color.B * 257), (ushort)(color.A * 257)));
                }
                else
                {
                    frame.SetPixel(px, py, color);
                }
            }
        }
    }

    // The frames a decoder shows: identical neighbours merged (durations added), zero-duration frames before others dropped.
    private static List<(ImageFrame Frame, double Ms)> Expected(Image source)
    {
        var merged = new List<(ImageFrame Frame, double Ms)>();
        foreach (ImageFrame frame in source.Frames)
        {
            double ms = Math.Round(frame.Duration.TotalMilliseconds, MidpointRounding.AwayFromZero);
            if (merged.Count > 0 && merged[^1].Frame.GetPixelByteSpan().SequenceEqual(frame.GetPixelByteSpan()))
            {
                merged[^1] = (merged[^1].Frame, merged[^1].Ms + ms);
            }
            else
            {
                merged.Add((frame, ms));
            }
        }

        if (source.FrameCount == 1) return [(source.Frames[0], 0)];
        return [.. merged.Where((frame, index) => frame.Ms > 0 || index == merged.Count - 1)];
    }

    private static async Task AssertShown(Image decoded, List<(ImageFrame Frame, double Ms)> expected, string what)
    {
        await Assert.That(decoded.FrameCount).IsEqualTo(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            await AssertPixels(decoded.Frames[i], expected[i].Frame, $"{what} frame {i}");
            await Assert.That(decoded.Frames[i].Duration.TotalMilliseconds).IsEqualTo(expected[i].Ms);
        }
    }

    private static async Task AssertPixels(ImageFrame actual, ImageFrame expected, string what)
    {
        await Assert.That(actual.PixelType).IsEqualTo(expected.PixelType);
        if (!actual.GetPixelByteSpan().SequenceEqual(expected.GetPixelByteSpan())) Assert.Fail($"{what}: pixels differ");
    }

    private static double MeanError(ImageFrame actual, ImageFrame expected)
    {
        ReadOnlySpan<byte> a = actual.GetPixelByteSpan();
        ReadOnlySpan<byte> b = expected.GetPixelByteSpan();
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    private static async Task<byte[]> WriteFrames(Image source, JxlEncoderOptions options)
    {
        var output = new MemoryStream();
        await using (ImageFrameWriter writer = Image.CreateFrameWriter(output, source.Size, options,
                         new ImageFrameWriterOptions { LoopCount = source.LoopCount, Metadata = source.Metadata }))
        {
            foreach (ImageFrame frame in source.Frames) await writer.WriteAsync(frame);
            await writer.CompleteAsync();
        }

        return output.ToArray();
    }

    // Every frame header of the codestream, reference-only patch frames included.
    private static List<JxlFrameHeader> Headers(byte[] data)
    {
        JxlCodestream codestream = JxlContainer.Parse(data).Codestream;
        return JxlDecodeSession.ReadFrameHeaders(codestream, JxlHeadersReader.Read(codestream));
    }

    /// <summary>A destination that cannot seek or read, as a network stream.</summary>
    private sealed class WriteOnlyStream : Stream
    {
        private readonly MemoryStream inner = new();

        public byte[] ToArray() => inner.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
