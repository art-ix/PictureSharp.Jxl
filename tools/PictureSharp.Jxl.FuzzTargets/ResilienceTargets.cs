using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Fuzz;

/// <summary>
/// The 0.8.9 failure-path targets (docs/design/0.8.9-resilience.md). <c>cancel-*</c> runs an input once, then cancels
/// it at the n-th rental or working-budget charge, at 1 and 4 threads; <c>stream-*</c> writes to a stream that fails
/// after n bytes and reads from streams that return short reads or fail. n comes from a hash of the input, so a saved
/// input reproduces alone. Every exit must leave the counting pool balanced (<see cref="FuzzTargets.Run"/>).
/// </summary>
public static class ResilienceTargets
{
    public static readonly string[] Names = ["cancel-decode", "cancel-tojpeg", "cancel-fromjpeg", "stream-tojpeg", "stream-fromjpeg", "stream-encode"];

    /// <summary>The corpus target whose seeds a target uses.</summary>
    public static string SeedTarget(string target) => target switch
    {
        "stream-encode" => "decode",
        _ => target[(target.IndexOf('-') + 1)..],
    };

    public static void Run(string target, byte[] data)
    {
        switch (target)
        {
            case "cancel-decode": Cancel(data, (input, threads, token) => Decode(input, threads, token)); break;
            case "cancel-tojpeg": Cancel(data, (input, threads, token) => Transcode(input, threads, toJpeg: true, token)); break;
            case "cancel-fromjpeg": Cancel(data, (input, threads, token) => Transcode(input, threads, toJpeg: false, token)); break;
            case "stream-tojpeg": Streams(data, toJpeg: true); break;
            case "stream-fromjpeg": Streams(data, toJpeg: false); break;
            case "stream-encode": StreamEncode(data); break;
            default: throw new ArgumentException($"Unknown target {target}.", nameof(target));
        }
    }

    // JXL_FUZZ_VERBOSE=1: each cancellation point and its outcome on stderr (triage).
    private static readonly bool Verbose = Environment.GetEnvironmentVariable("JXL_FUZZ_VERBOSE") == "1";

    private static readonly bool CancelAll = Environment.GetEnvironmentVariable("JXL_FUZZ_CANCEL_ALL") == "1";

    private delegate string Operation(byte[] data, int threads, CancellationToken cancellationToken);

    private static void Cancel(byte[] data, Operation operation)
    {
        JxlRentals.Reset();
        string full = Outcome(() => operation(data, 1, default));
        long events = JxlRentals.Events;
        if (events == 0) return;
        uint h = FuzzTargets.Hash(data);
        // Four points spread over the whole run (the last events too, where the result is being written), each at 1 and
        // 4 threads; JXL_FUZZ_CANCEL_ALL=1 tries every event at 1 thread instead (corpus sweeps).
        IEnumerable<(long At, int Threads)> points = CancelAll
            ? Enumerable.Range(1, (int)Math.Min(events, int.MaxValue)).Select(at => ((long)at, 1))
            : Enumerable.Range(0, 4).SelectMany(i => ((int[])[1, 4]).Select(threads =>
                (1 + (long)((ulong)((h ^ (uint)i * 0x9E3779B9u) * 2654435761u) % (ulong)events), threads)));
        foreach ((long at, int threads) in points)
        {
            using var source = new CancellationTokenSource();
            JxlRentals.Reset();
            JxlRentals.OnEvent = count => { if (count == at) source.Cancel(); };
            string outcome;
            try
            {
                outcome = Outcome(() => operation(data, threads, source.Token));
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == source.Token && source.IsCancellationRequested)
            {
                outcome = "canceled";
            }
            finally
            {
                JxlRentals.OnEvent = null;
            }

            if (JxlRentals.Outstanding != 0 || JxlRentals.Misuse is not null)
            {
                throw new FuzzLeakException($"After cancellation at event {at} of {events}, {threads} threads: "
                    + (JxlRentals.Misuse ?? $"{JxlRentals.Outstanding} rentals not returned ({string.Join(", ", JxlRentals.Describe().Order(StringComparer.Ordinal).Take(8))})"));
            }

            if (Verbose) Console.Error.WriteLine($"cancel at {at}/{events}, {threads} threads: {outcome}");
            if (outcome != "canceled" && outcome != full)
            {
                throw new FuzzMismatchException($"cancellation at event {at} of {events}, {threads} threads: {outcome}; uncanceled: {full}");
            }
        }
    }

    // The result, or the allowed exception that ended the run; anything else propagates as a finding.
    private static string Outcome(Func<string> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    private static string Decode(byte[] data, int threads, CancellationToken cancellationToken)
    {
        using Image image = Image.Load(data, FuzzTargets.DecodeOptions(data, threads), cancellationToken);
        return FuzzTargets.Describe(image);
    }

    private static string Transcode(byte[] data, int threads, bool toJpeg, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        if (toJpeg) JxlTranscoder.ToJpeg(data, output, FuzzTargets.Transcode(threads), cancellationToken);
        else JxlTranscoder.FromJpeg(data, output, FuzzTargets.Transcode(threads), cancellationToken);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(output.ToArray()))[..16];
    }

    private static void Streams(byte[] data, bool toJpeg)
    {
        void Write(Stream input, Stream output)
        {
            if (toJpeg) JxlTranscoder.ToJpeg(input, output, FuzzTargets.Transcode(1));
            else JxlTranscoder.FromJpeg(input, output, FuzzTargets.Transcode(1));
        }

        if (data.Length == 0) return;
        byte[] full;
        using (var output = new MemoryStream())
        {
            try
            {
                Write(new MemoryStream(data), output);
            }
            catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
            {
                return;
            }

            full = output.ToArray();
        }

        if (full.Length == 0) return;

        uint h = FuzzTargets.Hash(data);
        // Short reads from a stream that cannot seek: the same output.
        using (var output = new MemoryStream())
        {
            Write(new FuzzStream(data, readChunk: 1 + (int)(h % 7), seekable: false), output);
            if (!output.ToArray().AsSpan().SequenceEqual(full)) throw new FuzzMismatchException($"short reads: {output.Length} bytes, expected {full.Length}");
        }

        // A read that fails: its exception, unchanged.
        ExpectFailure(new FuzzStream(data, failReadAt: (int)(h % (uint)data.Length)), new MemoryStream(), Write, "read");
        // A write that fails after n bytes: its exception, and the first n bytes of the output.
        int limit = (int)((h >> 8) % (uint)full.Length);
        var failing = new FuzzStream([], failWriteAt: limit);
        ExpectFailure(new MemoryStream(data), failing, Write, "write");
        if (!failing.Written.AsSpan().SequenceEqual(full.AsSpan(0, failing.Written.Length)))
            throw new FuzzMismatchException($"write failure: the {failing.Written.Length} bytes written differ from the output");

        // Async, where the transcoder copies the input itself.
        var asyncFailing = new FuzzStream([], failWriteAt: limit);
        ExpectFailure(new MemoryStream(data), asyncFailing, (input, output) =>
        {
            Task task = toJpeg ? JxlTranscoder.ToJpegAsync(input, output, FuzzTargets.Transcode(1)) : JxlTranscoder.FromJpegAsync(input, output, FuzzTargets.Transcode(1));
            task.GetAwaiter().GetResult();
        }, "async write");
    }

    private static void StreamEncode(byte[] data)
    {
        Image image;
        try
        {
            image = Image.Load(data, FuzzTargets.DecodeOptions(data));
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
        {
            return;
        }

        using (image)
        {
            uint h = FuzzTargets.Hash(data);
            var options = new JxlEncoderOptions
            {
                Configuration = FuzzTargets.Configuration, Distance = (h & 1) == 0 ? 0 : 1 + h % 4, Effort = 1 + (int)(h >> 1) % 3,
                MaxDegreeOfParallelism = 1,
            };
            // Every decoded image encodes. The one exception is its ICC profile: the decoder hands a damaged profile on as
            // djxl does, which the encoder cannot describe; without the profile the encode must succeed.
            byte[] full;
            try
            {
                full = image.Encode(options);
            }
            catch (Exception exception) when (FuzzTargets.IsAllowed(exception) && image.Metadata.IccProfile is not null)
            {
                image.Metadata.IccProfile = null;
                full = Encode(image, options);
            }
            catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
            {
                throw Unencodable(image, exception);
            }

            int limit = (int)((h >> 8) % (uint)full.Length);
            var failing = new FuzzStream([], failWriteAt: limit);
            ExpectFailure(null, failing, (_, output) => image.Save(output, options), "encode write");
            if (!failing.Written.AsSpan().SequenceEqual(full.AsSpan(0, failing.Written.Length)))
                throw new FuzzMismatchException($"encode write failure: the {failing.Written.Length} bytes written differ from the output");
        }
    }

    // One of the two streams fails; the call must end with that IOException, unwrapped, both streams left open.
    private static byte[] Encode(Image image, JxlEncoderOptions options)
    {
        try
        {
            return image.Encode(options);
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
        {
            throw Unencodable(image, exception);
        }
    }

    private static FuzzMismatchException Unencodable(Image image, Exception exception) =>
        new($"encoding a decoded {image.Width}x{image.Height} {image.PixelType} image failed: {exception.Message}");

    private static void ExpectFailure(Stream? input, Stream output, Action<Stream, Stream> write, string what)
    {
        FuzzStream stream = input as FuzzStream ?? (FuzzStream)output;
        try
        {
            write(input ?? Stream.Null, output);
        }
        catch (IOException exception) when (ReferenceEquals(exception, stream.Failure))
        {
            if (!output.CanWrite || (input is not null && !input.CanRead)) throw new FuzzMismatchException($"{what} failure: a stream was closed");
            return;
        }
        catch (Exception exception) when (stream.Failure is not null && Inner(exception, stream.Failure))
        {
            throw new FuzzMismatchException($"{what} failure: the IOException was wrapped in {exception.GetType().Name}");
        }

        if (stream.Failure is not null) throw new FuzzMismatchException($"{what} failure: the IOException was swallowed");
    }

    private static bool Inner(Exception exception, Exception target)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (ReferenceEquals(e, target)) return true;
        }

        return false;
    }

    /// <summary>Reads <paramref name="data"/> in chunks, or fails a read or a write at a byte position, once.</summary>
    private sealed class FuzzStream(byte[] data, int readChunk = int.MaxValue, bool seekable = true, int failReadAt = -1, int failWriteAt = -1) : Stream
    {
        private readonly MemoryStream written = new();
        private int position;
        private bool disposed;

        public IOException? Failure { get; private set; }

        public byte[] Written => written.ToArray();

        public override bool CanRead => !disposed;

        public override bool CanSeek => seekable && !disposed;

        public override bool CanWrite => !disposed;

        public override long Length => seekable ? data.Length : throw new NotSupportedException();

        public override long Position
        {
            get => seekable ? position : throw new NotSupportedException();
            set => position = seekable ? (int)value : throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (failReadAt >= 0 && position + Math.Min(buffer.Length, readChunk) > failReadAt && Failure is null)
            {
                throw Failure = new IOException($"read failure at byte {failReadAt}");
            }

            int count = Math.Min(Math.Min(buffer.Length, readChunk), data.Length - position);
            data.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (failWriteAt >= 0 && written.Length + buffer.Length > failWriteAt)
            {
                written.Write(buffer[..(int)(failWriteAt - written.Length)]);
                throw Failure = new IOException($"write failure at byte {failWriteAt}");
            }

            written.Write(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => seekable
            ? Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => data.Length + offset }
            : throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            disposed = true;
            base.Dispose(disposing);
        }
    }
}
