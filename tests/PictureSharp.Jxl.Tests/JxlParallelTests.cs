using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
using PictureSharp.Formats.Jxl.Codestream.Rendering;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// 0.8.1 group parallelism: every output and every exception must be the same at any MaxDegreeOfParallelism.
/// Degrees cover single-threaded, fewer workers than items, more workers than items, and automatic.
/// </summary>
public sealed class JxlParallelTests
{
    private static readonly int[] Degrees = [2, 3, 7, 64, -1];
    private static readonly ImageConfiguration Formats = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();

    private static DecoderOptions Load(int degree, DecoderOptions? options = null) => Options(options) with { MaxDegreeOfParallelism = degree };

    private static JxlEncoderOptions Encode(int degree, float distance, int effort) =>
        new() { Configuration = Formats, Distance = distance, Effort = effort, MaxDegreeOfParallelism = degree };

    private static string Hash(ReadOnlySpan<byte> bytes) => $"{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))}";

    private static string Pixels(Image image) => image.PixelType == PixelType.Rgba64
        ? "64:" + Hash(MemoryMarshal.AsBytes(image.GetPixelMemory<Rgba64>().Span))
        : Hash(MemoryMarshal.AsBytes(image.GetPixelMemory<Rgba32>().Span));

    private static string Outcome(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    // A multi-group test image: smooth gradients, edges and noise, optionally with alpha or at 16 bits.
    private static Image Synthetic(int width, int height, bool alpha, bool wide = false)
    {
        var image = new Image(width, height, wide ? PixelType.Rgba64 : PixelType.Rgba32);
        uint state = 12345;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                state = state * 1664525 + 1013904223;
                int noise = (int)(state >> 28);
                int r = (x * 255 / width + noise) & 255;
                int g = ((x / 37 + y / 23) % 2 == 0 ? 40 : 200) + noise;
                int b = (x * y / 97 + 3 * noise) & 255;
                int a = alpha ? (x + y) % 211 < 20 ? 0 : 128 + y % 128 : 255;
                if (wide) image.GetRowSpan<Rgba64>(y)[x] = new((ushort)(r * 257 + y), (ushort)(g * 257), (ushort)(b * 257 + x), (ushort)(a * 257));
                else image.GetRowSpan<Rgba32>(y)[x] = new((byte)r, (byte)g, (byte)b, (byte)a);
            }
        }

        return image;
    }

    [Test]
    public async Task Options_ValidateMaxDegreeOfParallelism()
    {
        await Assert.That(new JxlEncoderOptions().MaxDegreeOfParallelism).IsEqualTo(-1);
        await Assert.That(new JxlTranscoderOptions().MaxDegreeOfParallelism).IsEqualTo(-1);
        foreach (int valid in (int[])[-1, 1, 2, 1000])
        {
            await Assert.That(new JxlEncoderOptions { MaxDegreeOfParallelism = valid }.MaxDegreeOfParallelism).IsEqualTo(valid);
            await Assert.That(new JxlTranscoderOptions { MaxDegreeOfParallelism = valid }.MaxDegreeOfParallelism).IsEqualTo(valid);
        }

        foreach (int invalid in (int[])[0, -2, int.MinValue])
        {
            await Assert.That(() => new JxlEncoderOptions { MaxDegreeOfParallelism = invalid }).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => new JxlTranscoderOptions { MaxDegreeOfParallelism = invalid }).Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task For_RethrowsTheLowestFailedIndexUnchanged()
    {
        for (int run = 0; run < 50; run++)
        {
            foreach (int degree in (int[])[1, .. Degrees])
            {
                int completed = 0;
                var done = new bool[40];
                Exception? caught = null;
                try
                {
                    JxlParallel.For(done.Length, degree, default, i =>
                    {
                        // Later failures finish first; the lowest index must still win.
                        if (i == 31) throw new NotSupportedException("31");
                        if (i == 17) { Thread.Sleep(1); throw JxlDescriptor.Corrupt("17"); }
                        done[i] = true;
                        Interlocked.Increment(ref completed);
                    });
                }
                catch (Exception exception)
                {
                    caught = exception;
                }

                await Assert.That(caught).IsTypeOf<ImageFormatException>();
                await Assert.That(caught!.Message).EndsWith(": 17");
                // Every item below the failure ran, as in the sequential loop.
                await Assert.That(done.Take(17).All(d => d)).IsTrue();
            }
        }
    }

    [Test]
    public async Task ForWorkers_UsesOneStatePerWorkerAndTheLowestFailure()
    {
        foreach (int degree in (int[])[1, .. Degrees])
        {
            int states = 0;
            var seen = new int[100];
            JxlParallel.ForWorkers(seen.Length, degree, default, () => { Interlocked.Increment(ref states); return new object(); },
                (_, i) => Interlocked.Increment(ref seen[i]));
            await Assert.That(seen.All(count => count == 1)).IsTrue();
            await Assert.That(states >= 1 && states <= JxlParallel.Workers(degree, seen.Length)).IsTrue();
            await Assert.That(() => JxlParallel.ForWorkers(50, degree, default, () => 0, (_, i) =>
            {
                if (i is 12 or 40) throw new InvalidOperationException(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            })).Throws<InvalidOperationException>().WithMessageContaining("12");
        }
    }

    [Test]
    public async Task ForWorkers_CreatesStatesForTakenIndicesAndReportsCreationFailures()
    {
        foreach (int degree in (int[])[1, .. Degrees])
        {
            // A worker creates its state with its first index: never more states than indices.
            int states = 0;
            JxlParallel.ForWorkers(3, degree, default, () => Interlocked.Increment(ref states), (_, _) => Thread.Sleep(1));
            await Assert.That(states >= 1 && states <= 3).IsTrue();
            await Assert.That(() => JxlParallel.ForWorkers<object>(20, degree, default,
                () => throw new InvalidOperationException("state"), (_, _) => { })).Throws<InvalidOperationException>().WithMessageContaining("state");
        }
    }

    [Test]
    public async Task For_HonoursCancellationInsideWorkers()
    {
        foreach (int degree in (int[])[1, .. Degrees])
        {
            using var source = new CancellationTokenSource();
            int after = 0;
            await Assert.That(() => JxlParallel.For(1000, degree, source.Token, i =>
            {
                if (i == 10) source.Cancel();
                if (source.IsCancellationRequested && i > 500) Interlocked.Increment(ref after);
                source.Token.ThrowIfCancellationRequested();
            })).Throws<OperationCanceledException>();
            // Items are checked against the token before they start: few run after the cancellation.
            await Assert.That(after).IsLessThan(64);
        }
    }

    [Test]
    public async Task BufferScope_AcceptsConcurrentRentalsAndReturnsEachOnce()
    {
        var scope = new JxlBufferScope();
        var leases = new JxlPooledBuffer<int>[400];
        JxlParallel.For(leases.Length, -1, default, i => leases[i] = scope.Lease<int>(16 + i % 7));
        scope.Dispose();
        scope.Dispose();
        await Assert.That(leases.All(lease => lease is not null)).IsTrue();
        await Assert.That(leases.Count(lease => Throws(() => _ = lease.Memory))).IsEqualTo(leases.Length);
        await Assert.That(() => scope.Lease<int>(4)).Throws<ObjectDisposedException>();

        static bool Throws(Action action)
        {
            try { action(); return false; }
            catch (ObjectDisposedException) { return true; }
        }
    }

    [Test]
    public async Task Filters_AreIdenticalForEveryBandCount()
    {
        var random = new Random(7);
        foreach ((int width, int height) in new[] { (37, 9), (64, 130), (200, 261), (9, 400) })
        {
            int xBlocks = (width + 7) / 8, yBlocks = (height + 7) / 8;
            float[] sigma = [.. Enumerable.Range(0, xBlocks * yBlocks).Select(i => i % 5 == 0 ? -10f : -0.2f - (float)random.NextDouble())];
            float[][] source = [.. Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, width * height).Select(_ => (float)random.NextDouble()).ToArray())];
            var filter = new JxlLoopFilter { Gaborish = true, EpfIterations = 3 };
            string? expected = null;
            foreach (int degree in (int[])[1, .. Degrees])
            {
                JxlPlane[] planes = [.. source.Select(values =>
                {
                    var plane = new JxlPlane(width, height);
                    for (int y = 0; y < height; y++) values.AsSpan(y * width, width).CopyTo(plane.Row(y));
                    return plane;
                })];
                JxlRenderPipeline.Gaborish(planes, filter, default, degree);
                planes = JxlRenderPipeline.Epf0(planes, filter, sigma, xBlocks, default, degree);
                planes = JxlRenderPipeline.Epf1(planes, filter, sigma, xBlocks, default, degree);
                planes = JxlRenderPipeline.Epf2(planes, filter, sigma, xBlocks, default, degree);
                JxlPlane upsampled = JxlRenderPipeline.Upsample(planes[1], 1, Enumerable.Range(0, 15).Select(i => 0.01f * i).ToArray(),
                    2 * width, 2 * height, default, degree);
                string actual = string.Join(",", planes.Append(upsampled).Select(plane =>
                    Hash(MemoryMarshal.AsBytes(Enumerable.Range(0, plane.Height).SelectMany(y => plane.Row(y).ToArray()).ToArray().AsSpan()))));
                expected ??= actual;
                await Assert.That(actual).IsEqualTo(expected);
            }
        }
    }

    [Test]
    public async Task Decode_IsIdenticalAtEveryDegree()
    {
        string[] files = [.. JxlDecodeTests.CorpusFiles()];
        var differences = new List<string>();
        foreach (string name in files)
        {
            byte[] data = File.ReadAllBytes(CorpusPath(name));
            string Run(int degree) => string.Join(" | ",
                Outcome(() => { using Image image = Image.Load(data, Load(degree)); return Pixels(image); }),
                Outcome(() => { using Image image = Image.Load(data, Load(degree, new() { PreserveBitDepth = true })); return Pixels(image); }),
                Outcome(() => { using Image image = Image.Load(data, Load(degree, new() { ColorConversion = ColorConversionMode.ToSrgb })); return Pixels(image); }));
            string expected = Run(1);
            foreach (int degree in Degrees)
            {
                if (Run(degree) != expected) differences.Add($"{name} at {degree}");
            }
        }

        await Assert.That(differences).IsEmpty();
    }

    [Test]
    public async Task Encode_IsIdenticalAtEveryDegree()
    {
        using Image rgb = Synthetic(700, 530, alpha: false);
        using Image rgba = Synthetic(530, 300, alpha: true);
        using Image wide = Synthetic(300, 280, alpha: true, wide: true);
        var differences = new List<string>();
        foreach ((string name, Image image) in new[] { ("rgb", rgb), ("rgba", rgba), ("wide", wide) })
        {
            foreach ((float distance, int effort) in new[] { (0f, 1), (0f, 7), (1f, 1), (1f, 3), (1f, 5), (1f, 7), (3f, 7), (12f, 7) })
            {
                string expected = Outcome(() => Hash(image.ToJxl(Encode(1, distance, effort))));
                foreach (int degree in (int[])[2, 7, -1])
                {
                    if (Outcome(() => Hash(image.ToJxl(Encode(degree, distance, effort)))) != expected) differences.Add($"{name} d{distance} e{effort} at {degree}");
                }
            }
        }

        // Few colours in runs, as on screens: the palette is found in row bands (0.8.2); with one stray colour too many
        // in the last band it is not.
        foreach (int extra in (int[])[0, 1])
        {
            using var palette = new Image(640, 420);
            for (int y = 0; y < palette.Height; y++)
            {
                for (int x = 0; x < palette.Width; x++)
                {
                    int index = (x / 24 + y / 18 * 7) % 37;
                    palette.GetRowSpan<Rgba32>(y)[x] = new((byte)(index * 7), (byte)(255 - index * 5), (byte)(index * 13 % 256), 255);
                }
            }

            // 640 * 420 / 8 allows 1024 colours; the last rows add 1024 - 37 + extra more.
            for (int i = 0; i < 1024 - 37 + extra; i++) palette.GetRowSpan<Rgba32>(palette.Height - 1 - i / 600)[i % 600] = new((byte)i, (byte)(i >> 8), 77, 255);
            foreach (int effort in (int[])[1, 7])
            {
                string expected = Outcome(() => Hash(palette.ToJxl(Encode(1, 0, effort))));
                foreach (int degree in (int[])[2, 7, -1])
                {
                    if (Outcome(() => Hash(palette.ToJxl(Encode(degree, 0, effort)))) != expected) differences.Add($"palette+{extra} e{effort} at {degree}");
                }
            }
        }

        await Assert.That(differences).IsEmpty();
    }

    [Test]
    public async Task Transcoding_IsIdenticalAtEveryDegree()
    {
        var differences = new List<string>();
        foreach (string jpegPath in Directory.GetFiles(CorpusPath("transcoding"), "*.jpg").Order(StringComparer.Ordinal))
        {
            byte[] jpeg = File.ReadAllBytes(jpegPath);
            string Run(int degree)
            {
                var options = new JxlTranscoderOptions { MaxDegreeOfParallelism = degree };
                return Outcome(() =>
                {
                    using var jxl = new MemoryStream();
                    JxlTranscoder.FromJpeg(jpeg, jxl, options);
                    using var back = new MemoryStream();
                    JxlTranscoder.ToJpeg(jxl.ToArray(), back, options);
                    return Hash(jxl.ToArray()) + " " + Hash(back.ToArray());
                });
            }

            string expected = Run(1);
            foreach (int degree in Degrees)
            {
                if (Run(degree) != expected) differences.Add($"{Path.GetFileName(jpegPath)} at {degree}");
            }
        }

        await Assert.That(differences).IsEmpty();
    }

    [Test]
    public async Task JpegWriter_IsIdenticalAndFailsTheSameWayAtEveryDegree()
    {
        // Scans are coded in chunks of MCUs (restart intervals or rows) from 0.8.2; mutations of the coefficients,
        // reset points and extra zero runs must give the bytes or the first exception of the sequential writer.
        string corpus = Path.Combine(AppContext.BaseDirectory, "corpus", "transcoding");
        var differences = new List<string>();
        int failures = 0, outputs = 0;
        foreach (string path in Directory.GetFiles(corpus, "*.jpg").Order(StringComparer.Ordinal))
        {
            byte[] original = File.ReadAllBytes(path);
            for (int mutation = -1; mutation < 24; mutation++)
            {
                string Run(int degree) => Outcome(() =>
                {
                    var jpeg = Codestream.Jpeg.JxlJpegReader.Read(original, 1 << 28, _ => { }, default);
                    if (mutation >= 0) Mutate(jpeg, mutation);
                    return Hash(Codestream.Jpeg.JxlJpegWriter.Write(jpeg, default, degree));
                });
                string expected = Run(1);
                if (mutation < 0 && expected != Hash(original)) differences.Add($"{Path.GetFileName(path)} not reconstructed");
                if (expected.Contains("Exception", StringComparison.Ordinal)) failures++;
                else outputs++;
                foreach (int degree in Degrees)
                {
                    string actual = Run(degree);
                    if (actual != expected) differences.Add($"{Path.GetFileName(path)} #{mutation} at {degree}: {expected} / {actual}");
                }
            }
        }

        await Assert.That(differences).IsEmpty();
        await Assert.That(failures).IsGreaterThan(50);
        await Assert.That(outputs).IsGreaterThan(50);
    }

    [Test]
    public async Task JpegWriter_ReportsMissingPaddingBeforeLaterCoefficientFailure()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "corpus", "transcoding", "restart-0.jpg"));
        string Run(int degree, bool lateFailure) => Outcome(() =>
        {
            var jpeg = Codestream.Jpeg.JxlJpegReader.Read(original, 1 << 28, _ => { }, default);
            jpeg.HasZeroPadding = true;
            jpeg.Padding.Clear();
            // The first restart needs padding; place an unencodable coefficient before or after it.
            foreach (var component in jpeg.Components)
                component.Coefficients[lateFailure ? component.Coefficients.Length - 63 : 1] = short.MaxValue;
            return Hash(Codestream.Jpeg.JxlJpegWriter.Write(jpeg, default, degree));
        });
        foreach (bool lateFailure in (bool[])[false, true])
        {
            string expected = Run(1, lateFailure);
            await Assert.That(expected).Contains(lateFailure ? "Missing reconstructed JPEG padding." : "Reconstructed JPEG AC coefficient is too large.");
            foreach (int degree in Degrees) await Assert.That(Run(degree, lateFailure)).IsEqualTo(expected);
        }
    }

    // A deterministic damage of the reconstruction data: coefficients that cannot be coded at two places (the
    // sequential writer meets one first), or reset points and extra zero runs added in or out of order.
    private static void Mutate(Codestream.Jpeg.JxlJpegData jpeg, int mutation)
    {
        var random = new Random(mutation);
        var component = jpeg.Components[random.Next(jpeg.Components.Count)];
        int blocks = component.Coefficients.Length / 64;
        Codestream.Jpeg.JxlJpegScan scan = jpeg.Scans[random.Next(jpeg.Scans.Count)];
        int total = jpeg.Components.Sum(c => c.Coefficients.Length / 64);
        switch (mutation % 4)
        {
            case 0:
                component.Coefficients[random.Next(blocks) * 64 + 1 + random.Next(63)] = 4095;
                component.Coefficients[random.Next(blocks) * 64] = short.MinValue;
                break;
            case 1:
                component.Coefficients[random.Next(blocks) * 64 + 1 + random.Next(63)] = -4095;
                component.Coefficients[random.Next(blocks) * 64 + 1 + random.Next(63)] = 2047;
                break;
            case 2:
                int position = random.Next(total);
                scan.ExtraZeroRuns.Clear();
                scan.ExtraZeroRuns.Add((position, 1));
                if (mutation % 8 == 6) scan.ExtraZeroRuns.Add((random.Next(total), 1));
                break;
            default:
                scan.ResetPoints.Clear();
                scan.ResetPoints.Add(random.Next(total));
                scan.ResetPoints.Add(random.Next(total));
                break;
        }
    }

    [Test]
    public async Task CorruptGroups_FailTheSameWayAtEveryDegree()
    {
        var differences = new List<string>();
        int failures = 0;
        foreach (string name in (string[])["lossy-groups.jxl", "lf-groups.jxl", "groups-300x200.jxl", "lossy-progressive-ac.jxl"])
        {
            byte[] original = File.ReadAllBytes(CorpusPath(name));
            var random = new Random(name.Length);
            for (int mutation = 0; mutation < 40; mutation++)
            {
                byte[] data = (byte[])original.Clone();
                // Two damaged places in the later (group) sections, so several groups may fail at once.
                for (int k = 0; k < 2; k++) data[random.Next(data.Length / 3, data.Length)] ^= (byte)(1 << random.Next(8));
                string Run(int degree) => Outcome(() => { using Image image = Image.Load(data, Load(degree)); return Pixels(image); });
                string expected = Run(1);
                if (expected.Contains("Exception", StringComparison.Ordinal)) failures++;
                foreach (int degree in Degrees)
                {
                    string actual = Run(degree);
                    if (actual != expected) differences.Add($"{name} #{mutation} at {degree}: {expected} / {actual}");
                }
            }

            string truncated = Outcome(() => { using Image image = Image.Load(original.AsSpan(0, original.Length * 3 / 4).ToArray(), Load(1)); return ""; });
            string truncatedParallel = Outcome(() => { using Image image = Image.Load(original.AsSpan(0, original.Length * 3 / 4).ToArray(), Load(-1)); return ""; });
            await Assert.That(truncatedParallel).IsEqualTo(truncated);
        }

        await Assert.That(differences).IsEmpty();
        await Assert.That(failures).IsGreaterThan(10);
    }

    [Test]
    public async Task Budgets_FailBeforeWorkersAtEveryDegree()
    {
        byte[] data = File.ReadAllBytes(CorpusPath("lossy-groups.jxl"));
        long pixels;
        using (Image reference = Image.Load(data, Options())) pixels = (long)reference.Width * reference.Height;
        byte[] withMetadata = File.ReadAllBytes(CorpusPath("container-exif.jxl"));
        foreach (int degree in (int[])[1, .. Degrees])
        {
            await Assert.That(() => Image.Load(data, Load(degree, new() { MaxPixels = pixels - 1 })))
                .Throws<ImageFormatException>();
            await Assert.That(() => Image.Load(withMetadata, Load(degree, new() { MaxMetadataBytes = 1 }))).Throws<ImageFormatException>();
            using var canceled = new CancellationTokenSource();
            await canceled.CancelAsync();
            await Assert.That(() => Image.Load(data, Load(degree), canceled.Token)).Throws<OperationCanceledException>();
            using Image image = Synthetic(300, 300, alpha: true);
            await Assert.That(async () => await image.SaveAsJxlAsync(Stream.Null, Encode(degree, 1, 7), canceled.Token)).Throws<OperationCanceledException>();
        }

        string jpegPath = Directory.GetFiles(CorpusPath("transcoding"), "*.jpg").Order(StringComparer.Ordinal).First();
        byte[] jpeg = File.ReadAllBytes(jpegPath);
        foreach (int degree in (int[])[1, -1])
        {
            using var output = new MemoryStream();
            await Assert.That(() => JxlTranscoder.FromJpeg(jpeg, output, new JxlTranscoderOptions { MaxPixels = 1, MaxDegreeOfParallelism = degree }))
                .Throws<ImageFormatException>();
        }
    }

    [Test]
    public async Task Cancellation_DuringAParallelDecodeOrEncode_Throws()
    {
        using Image image = Synthetic(1100, 900, alpha: false);
        byte[] encoded = image.ToJxl(Encode(-1, 1, 7));
        foreach (int delay in (int[])[0, 1, 3, 10])
        {
            using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(delay));
            try
            {
                using Image decoded = Image.Load(encoded, Load(-1), source.Token);
                // Fast enough to finish before the timer: the result must be complete and correct.
                using Image reference = Image.Load(encoded, Load(1));
                await Assert.That(Pixels(decoded)).IsEqualTo(Pixels(reference));
            }
            catch (OperationCanceledException)
            {
            }

            using var encodeSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(delay));
            try
            {
                using var again = new MemoryStream();
                await image.SaveAsJxlAsync(again, Encode(-1, 1, 7), encodeSource.Token);
                await Assert.That(again.ToArray()).IsEquivalentTo(encoded);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
