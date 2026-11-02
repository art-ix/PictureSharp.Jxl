using System.Drawing;
using System.Runtime.InteropServices;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Encoding;
using PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Integration;

internal sealed partial class JxlEncoder
{
    // Image.Save of several frames: gray and alpha are decided from all frames, as for a still image.
    private static void EncodeAnimation(Image image, Stream output, JxlEncoderOptions options, CancellationToken cancellationToken)
    {
        bool wide = image.PixelType == PixelType.Rgba64;
        bool gray = true;
        bool alpha = false;
        foreach (ImageFrame frame in image.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            gray = gray && (wide ? IsGray(frame.GetPixelSpan<Rgba64>()) : IsGray(frame.GetPixelSpan<Rgba32>()));
            alpha = alpha || !frame.IsOpaque();
        }

        using var writer = new AnimationWriter(output, image.Width, image.Height, image.PixelType, image.LoopCount, image.Metadata, options, gray, alpha);
        foreach (ImageFrame frame in image.Frames) writer.Write(frame, frame.Duration, cancellationToken);
        writer.Complete(cancellationToken);
    }

    /// <summary>
    /// Writes an animation one frame at a time (docs/design/0.8.14-animation-encode.md): the headers with an animation
    /// header (1000 ticks per second) at the first frame, then each frame cropped to the rectangle that differs from the
    /// previous frame and placed over reference slot 1. The last encoded frame is held back until the next one or
    /// <see cref="Complete"/> says whether it is the last; identical frames add their duration to it.
    /// </summary>
    internal sealed class AnimationWriter : IDisposable
    {
        private const uint TicksPerSecond = 1000;
        private const uint Slot = 1;

        private readonly Stream output;
        private readonly JxlEncoderOptions options;
        private readonly int width;
        private readonly int height;
        private readonly bool wide;
        private readonly EncodePlan plan;
        private readonly bool grayKnown;
        private readonly JxlImageMetadata codestreamMetadata;
        private readonly JxlHeaders headers;
        private readonly JxlBox[] boxes;
        private readonly int level;
        private readonly bool container;
        private ImageFrame? previous;
        private PendingFrame? pending;
        private int parts;
        private bool started;

        // grayPixels: whether every frame is gray, or null when the frames are not known yet (gray only with a gray ICC
        // profile, checked per frame). alpha: whether any frame has alpha; a writer that cannot see the frames passes true.
        // Nothing is written before the first frame, so the options can be validated on Stream.Null.
        public AnimationWriter(Stream output, int width, int height, PixelType pixelType, int loopCount, ImageMetadata metadata,
            JxlEncoderOptions options, bool? grayPixels, bool alpha)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(loopCount);
            this.output = output;
            this.options = options;
            this.width = width;
            this.height = height;
            wide = pixelType == PixelType.Rgba64;
            plan = Plan(metadata, options, grayPixels, alpha, wide ? 16 : 8);
            grayKnown = grayPixels is not null;
            JxlColorEncoding color = plan.Icc is not null
                ? new JxlColorEncoding { WantIcc = true, ColorSpace = plan.Gray ? JxlColorSpace.Gray : JxlColorSpace.Rgb }
                : plan.Encoding;
            codestreamMetadata = JxlCodestreamWriter.Metadata(color, plan.Alpha, plan.BitDepth, plan.Orientation, xyb: plan.XybInput is not null,
                new JxlAnimationHeader(TicksPerSecond, 1, (uint)loopCount, HaveTimecodes: false));
            headers = new JxlHeaders(new JxlSizeHeader((uint)width, (uint)height), codestreamMetadata, JxlCustomTransformData.Default);
            boxes = DescriptiveBoxes(metadata, options);
            level = ConformanceLevel(width, height);
            container = boxes.Length > 0 || level != JxlContainer.DefaultLevel;
        }

        /// <summary>Encodes a full-canvas frame shown for <paramref name="duration"/> (rounded to milliseconds).</summary>
        public void Write(ImageFrame frame, TimeSpan duration, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.Width != width || frame.Height != height || frame.PixelType != (wide ? PixelType.Rgba64 : PixelType.Rgba32))
            {
                throw new ArgumentException("The frame does not match the canvas.", nameof(frame));
            }

            cancellationToken.ThrowIfCancellationRequested();
            uint ticks = Ticks(duration);
            if (!grayKnown && plan.Gray && !(wide ? IsGray(frame.GetPixelSpan<Rgba64>()) : IsGray(frame.GetPixelSpan<Rgba32>())))
            {
                throw GrayProfileMismatch();
            }

            Rectangle? changed = previous is null ? frame.Bounds
                : wide ? Changed(MemoryMarshal.Cast<Rgba64, ulong>(previous.GetPixelSpan<Rgba64>()), MemoryMarshal.Cast<Rgba64, ulong>(frame.GetPixelSpan<Rgba64>()), cancellationToken)
                : Changed(MemoryMarshal.Cast<Rgba32, uint>(previous.GetPixelSpan<Rgba32>()), MemoryMarshal.Cast<Rgba32, uint>(frame.GetPixelSpan<Rgba32>()), cancellationToken);
            if (changed is not { } rectangle)
            {
                // The same canvas again: the held-back frame is shown longer.
                ulong total = (ulong)pending!.Ticks + ticks;
                pending.Ticks = total <= uint.MaxValue ? (uint)total : throw DurationTooLong();
                return;
            }

            if (!started)
            {
                Start(cancellationToken);
                started = true;
            }

            if (pending is not null)
            {
                Emit(pending, last: false, cancellationToken);
                pending.Dispose();
                pending = null;
            }

            pending = Encode(frame, rectangle, ticks, cancellationToken);
            if (previous is null) previous = frame.Clone();
            else if (wide) CopyRectangle<Rgba64>(frame, previous, rectangle);
            else CopyRectangle<Rgba32>(frame, previous, rectangle);
        }

        /// <summary>Writes the held-back frame as the last one.</summary>
        public void Complete(CancellationToken cancellationToken)
        {
            if (pending is null) throw new InvalidOperationException("An animation needs at least one frame.");
            cancellationToken.ThrowIfCancellationRequested();
            Emit(pending, last: true, cancellationToken);
            pending.Dispose();
            pending = null;
            previous = null;
        }

        public void Dispose()
        {
            pending?.Dispose();
            pending = null;
            previous = null;
        }

        private static uint Ticks(TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
            // Milliseconds, midpoints away from zero.
            long ticks = (duration.Ticks + TimeSpan.TicksPerMillisecond / 2) / TimeSpan.TicksPerMillisecond;
            return ticks <= uint.MaxValue ? (uint)ticks : throw DurationTooLong();
        }

        private static NotSupportedException DurationTooLong() =>
            JxlDescriptor.Unsupported($"a frame is shown for at most {uint.MaxValue} ms.");

        // The bounding box of the pixels that differ, or null when none does.
        private Rectangle? Changed<T>(ReadOnlySpan<T> before, ReadOnlySpan<T> after, CancellationToken cancellationToken) where T : unmanaged, IEquatable<T>
        {
            int top = 0;
            while (top < height && Row(before, top).SequenceEqual(Row(after, top)))
            {
                if ((top & 0x3F) == 0) cancellationToken.ThrowIfCancellationRequested();
                top++;
            }

            if (top == height) return null;
            int bottom = height - 1;
            while (Row(before, bottom).SequenceEqual(Row(after, bottom))) bottom--;
            int left = width;
            int right = -1;
            for (int y = top; y <= bottom; y++)
            {
                if ((y & 0x3F) == 0) cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<T> a = Row(before, y);
                ReadOnlySpan<T> b = Row(after, y);
                int first = a.CommonPrefixLength(b);
                if (first == width) continue;
                left = Math.Min(left, first);
                int last = width - 1;
                while (last > right && a[last].Equals(b[last])) last--;
                right = Math.Max(right, last);
            }

            return new Rectangle(left, top, right - left + 1, bottom - top + 1);
        }

        private ReadOnlySpan<T> Row<T>(ReadOnlySpan<T> pixels, int y) => pixels.Slice(y * width, width);

        private static void CopyRectangle<T>(ImageFrame source, ImageFrame destination, Rectangle rectangle) where T : unmanaged
        {
            for (int y = rectangle.Top; y < rectangle.Bottom; y++)
            {
                source.GetRowSpan<T>(y).Slice(rectangle.X, rectangle.Width).CopyTo(destination.GetRowSpan<T>(y).Slice(rectangle.X));
            }
        }

        // The frame's own pixels when the rectangle covers the canvas, else a pooled copy of the rectangle.
        private static ReadOnlyMemory<T> Crop<T>(ImageFrame frame, Rectangle rectangle, JxlBufferScope buffers) where T : unmanaged
        {
            if (rectangle == frame.Bounds) return frame.GetPixelMemory<T>();
            T[] crop = buffers.Rent<T>(rectangle.Width * rectangle.Height, clear: false);
            for (int y = 0; y < rectangle.Height; y++)
            {
                frame.GetRowSpan<T>(rectangle.Y + y).Slice(rectangle.X, rectangle.Width).CopyTo(crop.AsSpan(y * rectangle.Width, rectangle.Width));
            }

            return crop.AsMemory(0, rectangle.Width * rectangle.Height);
        }

        // The signature, ftyp, level and metadata boxes in a container, and the codestream headers.
        private void Start(CancellationToken cancellationToken)
        {
            if (container) JxlContainerWriter.WriteStart(output, boxes, level);
            var writer = new JxlBitWriter();
            JxlCodestreamWriter.Headers(writer, width, height, codestreamMetadata, plan.Icc);
            WritePart([writer.AsCodestream()], last: false, cancellationToken);
        }

        private PendingFrame Encode(ImageFrame frame, Rectangle rectangle, uint ticks, CancellationToken cancellationToken)
        {
            var buffers = new JxlBufferScope();
            try
            {
                using var workingBuffers = new JxlBufferScope();
                var writer = new JxlBitWriter(buffers, segmented: true);
                var placement = new JxlFramePlacement(rectangle.X, rectangle.Y, (uint)width, (uint)height, ticks, IsLast: false, Slot);
                int w = rectangle.Width;
                int h = rectangle.Height;
                int parallelism = options.MaxDegreeOfParallelism;
                ReadOnlyMemory<Rgba32> pixels = wide ? default : Crop<Rgba32>(frame, rectangle, workingBuffers);
                ReadOnlyMemory<Rgba64> pixels64 = wide ? Crop<Rgba64>(frame, rectangle, workingBuffers) : default;
                if (plan.XybInput is { } xybInput)
                {
                    JxlImage3F xyb = wide
                        ? ToXyb(pixels64, w, h, xybInput, cancellationToken, workingBuffers, parallelism)
                        : ToXyb(pixels, w, h, xybInput, cancellationToken, workingBuffers, parallelism);
                    int[]? alphaPlane = !plan.Alpha ? null
                        : wide ? AlphaPlane(pixels64.Span, cancellationToken) : AlphaPlane(pixels.Span, cancellationToken);
                    var settings = JxlLossySettings.Create(Math.Max(plan.Distance, JxlLossySettings.MinDistance), options.Effort, resample: !plan.Alpha)
                        with { Parallelism = parallelism };
                    JxlVarDctEncoder.WriteFrame(writer, xyb, w, h, alphaPlane, codestreamMetadata, settings, cancellationToken, placement);
                }
                else
                {
                    (int[][] color, int[]? alphaPlane) = wide ? SplitPlanes(pixels64.Span, plan.Gray, plan.Alpha, workingBuffers, cancellationToken)
                        : SplitPlanes(pixels.Span, plan.Gray, plan.Alpha, workingBuffers, cancellationToken);
                    List<JxlChannel> channels = [.. (alphaPlane is null ? color : [.. color, alphaPlane]).Select(plane => new JxlChannel(w, h, plane))];
                    JxlFrameHeader header = placement.ApplyTo(new JxlFrameHeader
                    {
                        Encoding = JxlFrameEncoding.Modular,
                        Width = (uint)w,
                        Height = (uint)h,
                        GroupSizeShift = JxlModularEncoder.GroupSizeShift,
                        ExtraChannelUpsampling = alphaPlane is null ? [] : [1u],
                        LoopFilter = new JxlLoopFilter { Gaborish = false, EpfIterations = 0 },
                    });
                    JxlModularEncoder.WriteFrame(writer, channels, color.Length, cancellationToken, header, codestreamMetadata,
                        parallelism: parallelism, effort: JxlModularEffort.For(options.Effort), bitDepth: plan.BitDepth);
                }

                return new PendingFrame(buffers, writer.AsCodestream(), ticks);
            }
            catch
            {
                buffers.Dispose();
                throw;
            }
        }

        // The frame as encoded, or with its header rewritten when it is the last or shown longer than encoded.
        private void Emit(PendingFrame frame, bool last, CancellationToken cancellationToken)
        {
            if (!last && frame.Ticks == frame.EncodedTicks)
            {
                WritePart([frame.Data], last, cancellationToken);
                return;
            }

            // A lossy frame may start with a reference-only patch frame; the regular frame after it is the one to change.
            var reader = new JxlBitReader(frame.Data);
            while (true)
            {
                long start = reader.BitPosition / 8;
                JxlFrameHeader header = JxlFrameHeaderReader.Read(ref reader, headers, preview: false);
                JxlToc toc = JxlTocReader.Read(ref reader, header.TocEntries);
                long sections = reader.BitPosition / 8;
                if (header.Type != JxlFrameType.Regular)
                {
                    reader.SkipBits((ulong)toc.TotalSize * 8);
                    continue;
                }

                var rewritten = new JxlBitWriter();
                JxlHeaderWriter.Frame(rewritten, header with { IsLast = last, Duration = frame.Ticks, SaveAsReference = last ? 0 : header.SaveAsReference },
                    codestreamMetadata);
                JxlHeaderWriter.Toc(rewritten, [.. toc.Sizes.Select(size => (int)size)]);
                WritePart([frame.Data.Slice(0, start), rewritten.AsCodestream(), frame.Data.Slice(sections, frame.Data.Length - sections)], last,
                    cancellationToken);
                return;
            }
        }

        // One piece of the codestream: raw, or a jxlp box in a container.
        private void WritePart(ReadOnlySpan<JxlCodestream> pieces, bool last, CancellationToken cancellationToken)
        {
            if (container)
            {
                long length = 0;
                foreach (JxlCodestream piece in pieces) length += piece.Length;
                JxlContainerWriter.WritePartHeader(output, parts++, last, length);
            }

            foreach (JxlCodestream piece in pieces)
            {
                foreach (ReadOnlyMemory<byte> segment in piece.Segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(segment.Span);
                }
            }
        }

        /// <summary>An encoded frame not yet written, with the buffers that hold it and its (possibly extended) duration.</summary>
        private sealed class PendingFrame(JxlBufferScope buffers, JxlCodestream data, uint ticks) : IDisposable
        {
            public JxlCodestream Data { get; } = data;

            public uint EncodedTicks { get; } = ticks;

            public uint Ticks { get; set; } = ticks;

            public void Dispose() => buffers.Dispose();
        }
    }
}
