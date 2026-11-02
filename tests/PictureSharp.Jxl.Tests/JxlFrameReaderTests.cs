using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// Frame-by-frame reading (0.8.13, docs/design/0.8.13-frame-reader.md): <c>Image.OpenFrameReaderAsync</c> returns the
/// frames, durations, loop count and metadata of <c>Image.Load</c>, one canvas at a time, within the reader limits.
/// </summary>
public sealed class JxlFrameReaderTests
{
    private static readonly string[] Unsupported = ["float32.jxl"];

    public static IEnumerable<string> Files() =>
        Directory.GetFiles(CorpusPath("")).Where(file => file.EndsWith(".jxl", StringComparison.Ordinal)).Select(Path.GetFileName)
            .Where(name => !Unsupported.Contains(name)).Order(StringComparer.Ordinal)!;

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Corpus_MatchesImageLoad(string name)
    {
        foreach (DecoderOptions variant in new DecoderOptions[]
        {
            new(), new() { PreserveBitDepth = true }, new() { IgnoreMetadata = true }, new() { MaxDegreeOfParallelism = 16 },
        })
        {
            DecoderOptions options = Options(variant);
            using Image expected = Image.Load(CorpusPath(name), options);
            await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(CorpusPath(name), options);
            await Assert.That((reader.Width, reader.Height)).IsEqualTo((expected.Width, expected.Height));
            await AssertMetadata(reader.Metadata, expected.Metadata, name);
            int index = 0;
            while (await reader.MoveNextAsync())
            {
                await Assert.That(index).IsLessThan(expected.FrameCount);
                await AssertFrame(reader.Current, expected.Frames[index], $"{name} frame {index}");
                index++;
            }

            await Assert.That(index).IsEqualTo(expected.FrameCount);
            await Assert.That(reader.LoopCount).IsEqualTo(expected.LoopCount);
        }
    }

    [Test]
    public async Task Unsupported_ThrowsLikeImageLoad()
    {
        foreach (string name in Unsupported)
        {
            await Assert.That(async () =>
            {
                await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(CorpusPath(name), Options());
                while (await reader.MoveNextAsync()) { }
            }).Throws<NotSupportedException>();
        }
    }

    [Test]
    public async Task LongAnimation_NeedsMaxPixelsOfOneCanvas()
    {
        // 132 frames, 119 shown, 16x12: Image.Load needs MaxPixels for every shown frame, the reader for one canvas.
        string path = CorpusPath("anim-long.jxl");
        DecoderOptions oneCanvas = Options(new DecoderOptions { MaxPixels = 16 * 12 });
        await Assert.That(() => Image.Load(path, oneCanvas)).Throws<ImageFormatException>();
        using Image expected = Image.Load(path, Options());
        await Assert.That(expected.FrameCount).IsEqualTo(119);
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(path, oneCanvas);
        int index = 0;
        await foreach (ImageFrame frame in reader.ReadFramesAsync())
        {
            await AssertFrame(frame, expected.Frames[index], $"frame {index}");
            index++;
        }

        await Assert.That(index).IsEqualTo(119);
        await Assert.That(reader.LoopCount).IsEqualTo(2);
        // One pixel less than the canvas: rejected when the reader opens.
        await Assert.That(async () => await Image.OpenFrameReaderAsync(path, Options(new DecoderOptions { MaxPixels = 16 * 12 - 1 })))
            .Throws<ImageFormatException>();
    }

    [Test]
    public async Task MaxPixels_Counts16BitPixelsTwice()
    {
        string path = CorpusPath("anim-16bit.jxl");
        await using (ImageFrameReader reader = await Image.OpenFrameReaderAsync(path,
            Options(new DecoderOptions { PreserveBitDepth = true, MaxPixels = 2 * 32 * 20 })))
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            await Assert.That(reader.Current.PixelType).IsEqualTo(PixelType.Rgba64);
        }

        await Assert.That(async () => await Image.OpenFrameReaderAsync(path,
            Options(new DecoderOptions { PreserveBitDepth = true, MaxPixels = 2 * 32 * 20 - 1 }))).Throws<ImageFormatException>();
    }

    [Test]
    public async Task MaxFrames_CountsEveryStoredFrame()
    {
        // Five frames in the codestream, three shown; FirstFrameOnly still reads the remaining headers.
        string path = CorpusPath("anim-ticks-loops.jxl");
        foreach (bool firstOnly in new[] { false, true })
        {
            await Assert.That(await Count(path, Options(new DecoderOptions { MaxFrames = 5, FirstFrameOnly = firstOnly }))).IsEqualTo(firstOnly ? 1 : 3);
            await Assert.That(async () => await Count(path, Options(new DecoderOptions { MaxFrames = 4, FirstFrameOnly = firstOnly })))
                .Throws<ImageFormatException>();
        }

        // 132 stored frames.
        await Assert.That(await Count(CorpusPath("anim-long.jxl"), Options(new DecoderOptions { MaxFrames = 132 }))).IsEqualTo(119);
        await Assert.That(async () => await Count(CorpusPath("anim-long.jxl"), Options(new DecoderOptions { MaxFrames = 131 })))
            .Throws<ImageFormatException>();
    }

    [Test]
    public async Task MaxInputBytes_CountsTheBufferedInput()
    {
        string path = CorpusPath("anim-long.jxl");
        long length = new FileInfo(path).Length;
        await Assert.That(await Count(path, Options(new DecoderOptions { MaxInputBytes = length }))).IsEqualTo(119);
        await Assert.That(async () => await Count(path, Options(new DecoderOptions { MaxInputBytes = length - 1 })))
            .Throws<ImageFormatException>();
    }

    [Test]
    public async Task MaxMetadataBytes_ChargesIccExifAndXmp()
    {
        foreach (string name in new[] { "icc-profile.jxl", "container-exif.jxl", "metadata-brob.jxl" })
        {
            using Image image = Image.Load(CorpusPath(name), Options());
            ImageMetadata m = image.Metadata;
            long total = (m.IccProfile?.Length ?? 0) + (m.Exif?.Length ?? 0) + (m.Xmp?.Length ?? 0);
            await Assert.That(total).IsGreaterThan(0);
            await Assert.That(async () => await Count(CorpusPath(name), Options(new DecoderOptions { MaxMetadataBytes = total - 1 })))
                .Throws<ImageFormatException>();
            // Ignored metadata is neither read nor charged.
            await Assert.That(await Count(CorpusPath(name), Options(new DecoderOptions { MaxMetadataBytes = 0, IgnoreMetadata = true })))
                .IsEqualTo(1);
        }
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task FirstFrameOnly_ReturnsImageLoadsFrame(string name)
    {
        DecoderOptions options = Options(new DecoderOptions { FirstFrameOnly = true });
        using Image expected = Image.Load(CorpusPath(name), options);
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(CorpusPath(name), options);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await AssertFrame(reader.Current, expected.Frames[0], name);
        await Assert.That(await reader.MoveNextAsync()).IsFalse();
        await Assert.That(reader.LoopCount).IsEqualTo(0);
    }

    [Test]
    public async Task FirstFrameOnly_StillReadsTheRemainingHeaders()
    {
        // A later frame is cut off: Image.Load fails, and so does the reader (its frame count comes from every header).
        byte[] data = File.ReadAllBytes(CorpusPath("anim-long.jxl"));
        byte[] truncated = data[..(data.Length * 3 / 4)];
        DecoderOptions options = Options(new DecoderOptions { FirstFrameOnly = true });
        await Assert.That(() => Image.Load(truncated, options)).Throws<ImageFormatException>();
        await Assert.That(async () => await Count(new MemoryStream(truncated), options)).Throws<ImageFormatException>();
        // MaxFrames covers the frames after the first one, too.
        await Assert.That(async () => await Count(CorpusPath("anim-long.jxl"), Options(new DecoderOptions { FirstFrameOnly = true, MaxFrames = 131 })))
            .Throws<ImageFormatException>();
    }

    [Test]
    public async Task Truncated_FailsWithAFormatError()
    {
        byte[] data = File.ReadAllBytes(CorpusPath("anim-crop.jxl"));
        foreach (int length in new[] { 16, data.Length / 4, data.Length / 2, data.Length - 1 })
        {
            await Assert.That(async () => await Count(new MemoryStream(data[..length]), Options())).Throws<ImageFormatException>();
        }
    }

    [Test]
    public async Task Cancellation_BeforeOpenAndBetweenFrames()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        string path = CorpusPath("anim-long.jxl");
        await Assert.That(async () => await Image.OpenFrameReaderAsync(path, Options(), canceled.Token)).Throws<OperationCanceledException>();
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(path, Options());
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(async () => await reader.MoveNextAsync(canceled.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Cancellation_AtAnyTime_EndsWithCancellationOrTheFullSequence()
    {
        // Cancelled from another thread at growing delays (the checked build's cancel-frames fuzz target cancels at
        // every rental and charge deterministically): the result is a cancellation or the complete sequence, never
        // another error, and a new reader afterwards returns the same frames.
        string path = CorpusPath("anim-long.jxl");
        using Image expected = Image.Load(path, Options());
        int canceled = 0;
        for (int delay = 0; delay < 40; delay += 2)
        {
            using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(delay));
            try
            {
                int count = 0;
                await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(path, Options(), source.Token);
                while (await reader.MoveNextAsync(source.Token)) count++;
                await Assert.That(count).IsEqualTo(expected.FrameCount);
            }
            catch (OperationCanceledException)
            {
                canceled++;
            }
        }

        await Assert.That(canceled).IsGreaterThan(0);
        await Assert.That(await Count(path, Options())).IsEqualTo(expected.FrameCount);
    }

    [Test]
    public async Task Dispose_MidSequence_ReleasesTheSource()
    {
        // Disposed after every possible number of frames, synchronously and asynchronously; the next full read is unchanged.
        string path = CorpusPath("anim-ticks-loops.jxl");
        using Image expected = Image.Load(path, Options());
        for (int stop = 0; stop <= expected.FrameCount; stop++)
        {
            ImageFrameReader reader = await Image.OpenFrameReaderAsync(path, Options());
            for (int i = 0; i < stop; i++) await Assert.That(await reader.MoveNextAsync()).IsTrue();
            if (stop % 2 == 0) reader.Dispose();
            else await reader.DisposeAsync();
            await Assert.That(async () => await reader.MoveNextAsync()).Throws<ObjectDisposedException>();
        }

        await using ImageFrameReader again = await Image.OpenFrameReaderAsync(path, Options());
        for (int i = 0; await again.MoveNextAsync(); i++) await AssertFrame(again.Current, expected.Frames[i], $"frame {i}");
    }

    [Test]
    public async Task NonSeekableStream_ShortReads_ReadTheSameFrames()
    {
        string path = CorpusPath("anim-vardct-alpha.jxl");
        using Image expected = Image.Load(path, Options());
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(new ForwardOnlyStream(File.ReadAllBytes(path), chunk: 3), Options());
        int index = 0;
        while (await reader.MoveNextAsync()) await AssertFrame(reader.Current, expected.Frames[index], $"frame {index++}");
        await Assert.That(index).IsEqualTo(expected.FrameCount);
    }

    [Test]
    public async Task Frames_AreBorrowedAndReused()
    {
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(CorpusPath("anim-crop.jxl"), Options());
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        ImageFrame first = reader.Current;
        using Image kept = reader.CloneCurrent();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(ReferenceEquals(first, reader.Current)).IsTrue();
        await Assert.That(kept.Frames[0].GetPixelByteSpan().SequenceEqual(reader.Current.GetPixelByteSpan())).IsFalse();
    }

    private static async Task<int> Count(string path, DecoderOptions options)
    {
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(path, options);
        int count = 0;
        while (await reader.MoveNextAsync()) count++;
        return count;
    }

    private static async Task<int> Count(Stream stream, DecoderOptions options)
    {
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(stream, options);
        int count = 0;
        while (await reader.MoveNextAsync()) count++;
        return count;
    }

    private static async Task AssertFrame(ImageFrame actual, ImageFrame expected, string what)
    {
        await Assert.That(actual.PixelType).IsEqualTo(expected.PixelType);
        await Assert.That(actual.Duration).IsEqualTo(expected.Duration);
        if (!actual.GetPixelByteSpan().SequenceEqual(expected.GetPixelByteSpan())) Assert.Fail($"{what}: pixels differ from Image.Load");
    }

    private static async Task AssertMetadata(ImageMetadata actual, ImageMetadata expected, string name)
    {
        if (!Bytes(actual.IccProfile).SequenceEqual(Bytes(expected.IccProfile))) Assert.Fail($"{name}: ICC differs");
        if (!Bytes(actual.Exif).SequenceEqual(Bytes(expected.Exif))) Assert.Fail($"{name}: EXIF differs");
        if (!Bytes(actual.Xmp).SequenceEqual(Bytes(expected.Xmp))) Assert.Fail($"{name}: XMP differs");
        await Assert.That(actual.Orientation).IsEqualTo(expected.Orientation);
        await Assert.That(actual.Gamma).IsEqualTo(expected.Gamma);
        await Assert.That(actual.Chromaticities).IsEqualTo(expected.Chromaticities);
    }

    private static byte[] Bytes(ReadOnlyMemory<byte>? block) => block?.ToArray() ?? [];

    /// <summary>A stream that cannot seek and returns at most <c>chunk</c> bytes per read.</summary>
    private sealed class ForwardOnlyStream(byte[] data, int chunk) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(Math.Min(count, chunk), data.Length - position);
            data.AsSpan(position, read).CopyTo(buffer.AsSpan(offset));
            position += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
