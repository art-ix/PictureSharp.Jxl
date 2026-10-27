using System.Diagnostics;
using PictureSharp.Formats.Jxl.Codestream.Jpeg;
using PictureSharp.Formats.Jxl.Container;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

public sealed class JxlJpegTests
{
    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "corpus", "transcoding");
    private static string[] Jpegs => Directory.GetFiles(Corpus, "*.jpg").Order(StringComparer.Ordinal).ToArray();

    [Test]
    public async Task ParserWriter_PreserveSyntheticJpegs()
    {
        await Assert.That(Jpegs.Length).IsEqualTo(20);
        foreach (string path in Jpegs)
        {
            byte[] original = File.ReadAllBytes(path);
            var jpeg = JxlJpegReader.Read(original, 1 << 28, _ => { }, default);
            byte[] reconstructed = JxlJpegWriter.Write(jpeg, default);
            await Assert.That(reconstructed.AsSpan().SequenceEqual(original)).IsTrue().Because(Path.GetFileName(path));
        }
    }

    [Test]
    public async Task PublicApi_ReconstructsOwnAndCjxlFiles()
    {
        foreach (string path in Jpegs)
        {
            byte[] original = File.ReadAllBytes(path);
            using var encoded = new MemoryStream();
            JxlTranscoder.FromJpeg(original, encoded);
            using var restored = new MemoryStream();
            JxlTranscoder.ToJpeg(encoded.ToArray(), restored);
            await Assert.That(restored.ToArray().AsSpan().SequenceEqual(original)).IsTrue().Because(Path.GetFileName(path) + " own");
            restored.SetLength(0);
            JxlTranscoder.ToJpeg(File.ReadAllBytes(Path.ChangeExtension(path, ".jxl")), restored);
            await Assert.That(restored.ToArray().AsSpan().SequenceEqual(original)).IsTrue().Because(Path.GetFileName(path) + " cjxl");
        }
    }

    [Test]
    public async Task CjxlPixels_MatchPinnedDjxlRendering()
    {
        foreach (string path in Jpegs)
        {
            using Image actual = Image.Load(Path.ChangeExtension(path, ".jxl"), Options() with { ApplyExifOrientation = true });
            using Image expected = Image.Load(Path.ChangeExtension(path, ".png"));
            byte[] a = JxlDecodeOracleTests.Rgba(actual), b = JxlDecodeOracleTests.Rgba(expected);
            await Assert.That(a.Length).IsEqualTo(b.Length);
            int worst = a.Zip(b, (x, y) => Math.Abs(x - y)).Max();
            await Assert.That(worst).IsLessThanOrEqualTo(1).Because(Path.GetFileName(path));
        }
    }

    [Test]
    [Category("Oracle")]
    public async Task Encoder_ReconstructsWithDjxl()
    {
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(djxl is not null, "djxl is required for the JPEG reconstruction oracle.");
        foreach (string path in Jpegs)
        {
            byte[] original = File.ReadAllBytes(path);
            using var encoded = new MemoryStream();
            JxlTranscoder.FromJpeg(original, encoded);
            string input = Path.Combine(Path.GetTempPath(), $"jxl-jpeg-{Guid.NewGuid():N}.jxl");
            string output = input + ".jpg";
            try
            {
                File.WriteAllBytes(input, encoded.ToArray());
                using var process = Process.Start(new ProcessStartInfo(djxl!, [input, output]) { RedirectStandardError = true })!;
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                await Assert.That(process.ExitCode).IsEqualTo(0).Because(Path.GetFileName(path) + ": " + error);
                await Assert.That(error.Contains("Retrying", StringComparison.Ordinal)).IsFalse();
                using Image actual = Image.Load(encoded.ToArray(), Options() with { ApplyExifOrientation = true });
                using Image expected = JxlDecodeOracleTests.DjxlImage(djxl!, input);
                byte[] a = JxlDecodeOracleTests.Rgba(actual), b = JxlDecodeOracleTests.Rgba(expected);
                await Assert.That(a.Length).IsEqualTo(b.Length);
                await Assert.That(a.Zip(b, (x, y) => Math.Abs(x - y)).Max()).IsLessThanOrEqualTo(1).Because(Path.GetFileName(path));
                await Assert.That(File.ReadAllBytes(output).AsSpan().SequenceEqual(original)).IsTrue().Because(Path.GetFileName(path));
            }
            finally { File.Delete(input); File.Delete(output); }
        }
    }

    [Test]
    public async Task StreamAndAsyncOverloads_RespectPositionsAndLeaveStreamsOpen()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Corpus, "metadata-1.jpg"));
        using var input = new MemoryStream([0, 1, 2, .. original]); input.Position = 3;
        using var encoded = new MemoryStream();
        JxlTranscoder.FromJpeg(input, encoded);
        await Assert.That(input.CanRead && encoded.CanWrite).IsTrue();
        using var restored = new MemoryStream();
        encoded.Position = 0;
        JxlTranscoder.ToJpeg(encoded, restored);
        await Assert.That(restored.ToArray().AsSpan().SequenceEqual(original)).IsTrue();
        using var asyncInput = new AsyncOnlyStream(original);
        using var asyncOutput = new AsyncOnlyStream();
        await JxlTranscoder.FromJpegAsync(asyncInput, asyncOutput);
        using var asyncEncoded = new AsyncOnlyStream(asyncOutput.Bytes);
        using var asyncRestored = new AsyncOnlyStream();
        await JxlTranscoder.ToJpegAsync(asyncEncoded, asyncRestored);
        await Assert.That(asyncRestored.Bytes.AsSpan().SequenceEqual(original)).IsTrue();
        using var memoryEncoded = new AsyncOnlyStream();
        await JxlTranscoder.FromJpegAsync(original.AsMemory(), memoryEncoded);
        using var memoryRestored = new AsyncOnlyStream();
        await JxlTranscoder.ToJpegAsync(memoryEncoded.Bytes.AsMemory(), memoryRestored);
        await Assert.That(memoryRestored.Bytes.AsSpan().SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task PathOverloads_ReconstructAndKeepOutputOnFailure()
    {
        string source = Path.Combine(Corpus, "metadata-1.jpg");
        byte[] original = File.ReadAllBytes(source);
        string directory = Directory.CreateTempSubdirectory("jxl-transcoder-").FullName;
        try
        {
            string jxl = Path.Combine(directory, "archived.jxl"), jpeg = Path.Combine(directory, "restored.jpg");
            JxlTranscoder.FromJpeg(source, jxl);
            JxlTranscoder.ToJpeg(jxl, jpeg);
            await Assert.That(File.ReadAllBytes(jpeg).AsSpan().SequenceEqual(original)).IsTrue();
            File.Delete(jxl); File.Delete(jpeg);
            await JxlTranscoder.FromJpegAsync(source, jxl);
            await JxlTranscoder.ToJpegAsync(jxl, jpeg);
            await Assert.That(File.ReadAllBytes(jpeg).AsSpan().SequenceEqual(original)).IsTrue();

            byte[] existing = File.ReadAllBytes(jpeg);
            await Assert.That(() => JxlTranscoder.ToJpeg(source, jpeg)).Throws<ImageFormatException>();
            await Assert.That(() => JxlTranscoder.FromJpeg(source, jpeg, new() { MaxInputBytes = original.Length - 1 })).Throws<ImageFormatException>();
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.That(async () => await JxlTranscoder.FromJpegAsync(source, jpeg, cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
            await Assert.That(File.ReadAllBytes(jpeg).AsSpan().SequenceEqual(existing)).IsTrue();
            await Assert.That(() => JxlTranscoder.FromJpeg(Path.Combine(directory, "missing.jpg"), jxl)).Throws<FileNotFoundException>();
            await Assert.That(() => JxlTranscoder.FromJpeg("", jxl)).Throws<ArgumentException>();
            await Assert.That(() => JxlTranscoder.ToJpeg(jxl, (string)null!)).Throws<ArgumentNullException>();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task LimitsAndCancellation_AreEnforced()
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(Corpus, "color-2-1.jpg"));
        byte[] jxl = File.ReadAllBytes(Path.Combine(Corpus, "color-2-1.jxl"));
        using var output = new MemoryStream();
        foreach (JxlTranscoderOptions limits in new JxlTranscoderOptions[]
        {
            new() { MaxInputBytes = jpeg.Length - 1 }, new() { MaxPixels = 10 }, new() { MaxMetadataBytes = 0 },
        })
            await Assert.That(() => JxlTranscoder.FromJpeg(jpeg, output, limits)).Throws<ImageFormatException>();
        foreach (JxlTranscoderOptions limits in new JxlTranscoderOptions[]
        {
            new() { MaxInputBytes = jxl.Length - 1 }, new() { MaxPixels = 10 }, new() { MaxMetadataBytes = 0 },
        })
            await Assert.That(() => JxlTranscoder.ToJpeg(jxl, output, limits)).Throws<ImageFormatException>();
        using var nonseek = new AsyncOnlyStream(jpeg);
        await Assert.That(async () => await JxlTranscoder.FromJpegAsync(nonseek, output, new() { MaxInputBytes = jpeg.Length - 1 })).Throws<ImageFormatException>();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.That(() => JxlTranscoder.FromJpeg(jpeg, output, cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(() => JxlTranscoder.ToJpeg(jxl, output, cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await JxlTranscoder.FromJpegAsync(jpeg.AsMemory(), output, cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await JxlTranscoder.ToJpegAsync(jxl.AsMemory(), output, cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(output.Length).IsEqualTo(0);
        await Assert.That(() => new JxlTranscoderOptions { MaxPixels = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new JxlTranscoderOptions { MaxInputBytes = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new JxlTranscoderOptions { MaxMetadataBytes = -1 }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task MissingReconstructionAndUnsupportedJpeg_AreExplicit()
    {
        using var output = new MemoryStream();
        byte[] plain = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "corpus", "rgb8-lossy.jxl"));
        await Assert.That(() => JxlTranscoder.ToJpeg(plain, output)).Throws<NotSupportedException>();
        byte[] jpeg = File.ReadAllBytes(Path.Combine(Corpus, "color-0-0.jpg"));
        int sof = FindMarker(jpeg, 0xc0);
        byte[] arithmetic = (byte[])jpeg.Clone(); arithmetic[sof + 1] = 0xc9;
        await Assert.That(() => JxlTranscoder.FromJpeg(arithmetic, output)).Throws<NotSupportedException>();
        byte[] depth = (byte[])jpeg.Clone(); depth[sof + 4] = 12;
        await Assert.That(() => JxlTranscoder.FromJpeg(depth, output)).Throws<NotSupportedException>();
        byte[] sampling = (byte[])jpeg.Clone(); sampling[sof + 11] = 0x41;
        await Assert.That(() => JxlTranscoder.FromJpeg(sampling, output)).Throws<NotSupportedException>();
        byte[] rgb = (byte[])jpeg.Clone(); rgb[FindMarker(rgb, 0xe0) + 1] = 0xe3; rgb[sof + 10] = (byte)'R'; rgb[sof + 13] = (byte)'G'; rgb[sof + 16] = (byte)'B';
        int sos = FindMarker(rgb, 0xda);
        rgb[sos + 5] = (byte)'R'; rgb[sos + 7] = (byte)'G'; rgb[sos + 9] = (byte)'B';
        await Assert.That(() => JxlTranscoder.FromJpeg(rgb, output)).Throws<NotSupportedException>();
        using Image image = Image.Load(File.ReadAllBytes(Path.Combine(Corpus, "metadata-0.jxl")), Options());
        await Assert.That(() => Image.EditMetadata(File.ReadAllBytes(Path.Combine(Corpus, "metadata-0.jxl")).AsMemory(), m => m.Xmp = "x"u8.ToArray(), Options()))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task CorruptInputs_FailAsFormatErrors()
    {
        using var output = new MemoryStream();
        byte[] jpeg = File.ReadAllBytes(Path.Combine(Corpus, "color-2-1.jpg"));
        foreach (int length in new[] { 0, 1, 2, 10, 30, 100, jpeg.Length - 1, jpeg.Length - 10 })
            await Assert.That(() => JxlTranscoder.FromJpeg(jpeg.AsSpan(0, length), output)).Throws<ImageFormatException>();
        byte[] jxl = File.ReadAllBytes(Path.Combine(Corpus, "color-2-1.jxl"));
        var container = JxlContainer.Parse(jxl, includeJpegReconstruction: true);
        byte[] reconstruction = container.JpegReconstruction!.Value.ToArray();
        reconstruction[^1] ^= 0x80;
        await Assert.That(() => JxlJpegReconstruction.Read(reconstruction, _ => { }, default)).Throws<ImageFormatException>();
        byte[] duplicate = JxlContainerWriter.Write(container.Codestream.Segments.ToArray().SelectMany(x => x.ToArray()).ToArray(),
            [new("jbrd", container.JpegReconstruction.Value), new("jbrd", container.JpegReconstruction.Value)]);
        await Assert.That(() => JxlTranscoder.ToJpeg(duplicate, output)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task FragmentedContainersAndCompressedMetadata_ReconstructExactly()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Corpus, "metadata-1.jpg"));
        var source = JxlContainer.Parse(File.ReadAllBytes(Path.Combine(Corpus, "metadata-1.jxl")), includeMetadata: true, includeJpegReconstruction: true);
        byte[] code = source.Codestream.Segments.ToArray().SelectMany(segment => segment.ToArray()).ToArray();
        var boxes = source.MetadataBoxes.Select(box => new JxlBox(box.Type, box.Read(64L << 20, null)!.Value, Compress: true)).ToList();
        boxes.Insert(0, new JxlBox("jbrd", source.JpegReconstruction!.Value));
        byte[] compressed = JxlContainerWriter.Write(code, boxes);
        using var output = new MemoryStream();
        JxlTranscoder.ToJpeg(compressed, output);
        await Assert.That(output.ToArray().AsSpan().SequenceEqual(original)).IsTrue();
        // Container v1 allows out-of-order fragments; the last flag belongs to the highest index.
        using var fragmented = new MemoryStream();
        fragmented.Write(JxlDescriptor.ContainerSignature);
        JxlContainerWriter.WriteBox(fragmented, new("ftyp", new byte[] { (byte)'j', (byte)'x', (byte)'l', 32, 0, 0, 0, 1, (byte)'j', (byte)'x', (byte)'l', 32 }));
        foreach (var box in boxes) JxlContainerWriter.WriteBox(fragmented, box);
        int split = code.Length / 2;
        JxlContainerWriter.WriteBox(fragmented, new("jxlp", new byte[] { 0x80, 0, 0, 1 }.Concat(code[split..]).ToArray()));
        JxlContainerWriter.WriteBox(fragmented, new("jxlp", new byte[4].Concat(code[..split]).ToArray()));
        output.SetLength(0);
        JxlTranscoder.ToJpeg(fragmented.ToArray(), output);
        await Assert.That(output.ToArray().AsSpan().SequenceEqual(original)).IsTrue();
        boxes.RemoveAll(box => box.Type == "Exif");
        await Assert.That(() => JxlTranscoder.ToJpeg(JxlContainerWriter.Write(code, boxes), output)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task StreamValidationAndIoFailures_PropagateWithoutClosingStreams()
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(Corpus, "tiny.jpg"));
        using var output = new MemoryStream();
        await Assert.That(() => JxlTranscoder.FromJpeg(jpeg, null!)).Throws<ArgumentNullException>();
        await Assert.That(() => JxlTranscoder.FromJpeg((Stream)null!, output)).Throws<ArgumentNullException>();
        using var readOnly = new MemoryStream(jpeg, writable: false);
        await Assert.That(() => JxlTranscoder.FromJpeg(jpeg, readOnly)).Throws<ArgumentException>();
        using var failing = new ThrowingStream();
        await Assert.That(() => JxlTranscoder.FromJpeg(jpeg, failing)).Throws<IOException>();
        await Assert.That(async () => await JxlTranscoder.FromJpegAsync(jpeg.AsMemory(), failing)).Throws<IOException>();
        await Assert.That(() => JxlTranscoder.FromJpeg(failing, output)).Throws<IOException>();
        await Assert.That(async () => await JxlTranscoder.FromJpegAsync(failing, output)).Throws<IOException>();
        await Assert.That(failing.CanWrite && readOnly.CanRead).IsTrue();
        using var nonseek = new AsyncOnlyStream(jpeg, allowSync: true);
        JxlTranscoder.FromJpeg(nonseek, output);
        await Assert.That(output.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task MutatedInputs_DoNotEscapeFormatOrUnsupportedErrors()
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(Corpus, "color-2-1.jpg"));
        byte[] jxl = File.ReadAllBytes(Path.Combine(Corpus, "color-2-1.jxl"));
        var random = new Random(71);
        int rejected = 0;
        using var output = new MemoryStream();
        for (int i = 0; i < 600; i++)
        {
            bool isJpeg = i % 2 == 0;
            byte[] data = (byte[])(isJpeg ? jpeg : jxl).Clone();
            data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
            output.SetLength(0);
            try
            {
                var limits = new JxlTranscoderOptions { MaxPixels = 1 << 20, MaxMetadataBytes = 1 << 20 };
                if (isJpeg) JxlTranscoder.FromJpeg(data, output, limits); else JxlTranscoder.ToJpeg(data, output, limits);
            }
            catch (ImageFormatException) { rejected++; }
            catch (NotSupportedException) { rejected++; }
        }
        await Assert.That(rejected).IsGreaterThan(0);
    }

    private static int FindMarker(byte[] bytes, int marker)
    {
        for (int i = 0; i + 1 < bytes.Length; i++) if (bytes[i] == 255 && bytes[i + 1] == marker) return i;
        throw new InvalidOperationException("Missing fixture marker.");
    }

    private sealed class AsyncOnlyStream : Stream
    {
        private readonly MemoryStream inner;
        private readonly bool allowSync;
        public AsyncOnlyStream(byte[]? data = null, bool allowSync = false)
        { inner = data is null ? new MemoryStream() : new MemoryStream(data); this.allowSync = allowSync; }
        public byte[] Bytes => inner.ToArray();
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => allowSync ? inner.Read(buffer, offset, count) : throw new InvalidOperationException("Synchronous I/O used by async API.");
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous I/O used by async API.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Synthetic read failure.");
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Synthetic write failure.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException("Synthetic async read failure."));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("Synthetic async write failure."));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

}
