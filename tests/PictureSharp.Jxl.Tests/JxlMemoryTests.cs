using System.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Encoding;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Tests;

public sealed class JxlMemoryTests
{
    private sealed class DirtyPool : ArrayPool<int>
    {
        public int Returns { get; private set; }
        public override int[] Rent(int minimumLength) => Enumerable.Repeat(123456, minimumLength + 17).ToArray();
        public override void Return(int[] array, bool clearArray = false) { Returns++; array.AsSpan().Fill(-999); }
    }

    [Test]
    public async Task Rental_BoundsInitializationAndIdempotentReturn()
    {
        var pool = new DirtyPool();
        var buffer = new JxlPooledBuffer<int>(9, pool: pool);
        await Assert.That(buffer.Span.Length).IsEqualTo(9);
        await Assert.That(buffer.Span.ToArray()).IsEquivalentTo(new int[9]);
        buffer.Dispose(); buffer.Dispose();
        await Assert.That(pool.Returns).IsEqualTo(1);
        await Assert.That(() => buffer.Memory).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task SegmentedSections_PreserveUnalignedBitsAndBytePadding()
    {
        for (int alignment = 0; alignment < 8; alignment++)
        {
            using var buffers = new JxlBufferScope();
            var first = new JxlBitWriter(buffers);
            first.Write(alignment, 0x7f);
            var next = new JxlBitWriter(buffers);
            for (int i = 0; i < 1200; i++) next.Write(9, (uint)i);
            first.Append(next);
            byte[] expected = first.ToArray();
            var output = new JxlBitWriter(buffers, segmented: true);
            output.WriteSection(first);
            JxlCodestream data = output.AsCodestream();
            byte[] actual = data.Segments.ToArray().SelectMany(s => s.ToArray()).ToArray();
            await Assert.That(actual).IsEquivalentTo(expected);
            await Assert.That(data.Length).IsEqualTo((long)expected.Length);
        }
    }

    [Test]
    public async Task IntegerDecode_PreservesOldNormalizationAtEveryDepth()
    {
        var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();
        for (int bits = 1; bits <= 16; bits++)
        {
            int count = 1 << bits;
            int width = Math.Min(256, count), height = count / width;
            int[] samples = Enumerable.Range(0, count).ToArray();
            byte[] data = JxlCodestreamWriter.Write(new JxlEncodeImage(width, height, [samples], samples,
                JxlColorEncoding.Srgb with { ColorSpace = JxlColorSpace.Gray }, null, 1, bits));
            using Image narrow = Image.Load(data, new DecoderOptions { Configuration = configuration });
            using Image wide = Image.Load(data, new DecoderOptions { Configuration = configuration, PreserveBitDepth = true });
            float scale = (float)(1.0 / ((1u << bits) - 1));
            string error = "";
            for (int y = 0; y < height && error.Length == 0; y++)
                for (int x = 0; x < width; x++)
                {
                    float normalized = samples[y * width + x] * scale;
                    byte expected = JxlDither.ToByte(normalized, JxlDither.Row(y, 0)[JxlDither.Column(x, 0)]);
                    byte alpha = JxlDither.ToByte(normalized, JxlDither.Row(y, 1)[JxlDither.Column(x, 1)]);
                    Rgba32 pixel = narrow.GetRowSpan<Rgba32>(y)[x];
                    if (pixel != new Rgba32(expected, expected, expected, alpha)) { error = $"narrow depth {bits}, {x},{y}"; break; }
                    if (wide.PixelType == PixelType.Rgba64)
                    {
                        ushort v = (ushort)Math.Clamp(MathF.Floor(normalized * 65535.0f + 0.5f), 0, 65535);
                        if (wide.GetRowSpan<Rgba64>(y)[x] != new Rgba64(v, v, v, v)) { error = $"wide depth {bits}, {x},{y}"; break; }
                    }
                }
            await Assert.That(error).IsEqualTo("");
        }
    }
    private sealed class Destination(int limit = int.MaxValue) : Stream
    {
        public MemoryStream Bytes { get; } = new();
        public bool Closed { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, limit - Bytes.Length);
            Bytes.Write(buffer[..count]);
            if (count != buffer.Length) throw new IOException("Injected output failure.");
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task StreamingOutput_IsExactAndSurvivesFailure(bool lossless, bool boxed)
    {
        var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();
        using var image = new Image(270, 259);
        var random = new Random(17);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                image.GetRowSpan<Rgba32>(y)[x] = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        if (boxed) image.Metadata.Xmp = "<test/>"u8.ToArray();
        var options = new JxlEncoderOptions { Configuration = configuration, Distance = lossless ? 0 : 1, Effort = 3 };
        byte[] expected = image.ToJxl(options);
        var output = new Destination();
        image.SaveAsJxl(output, options);
        await Assert.That(output.Bytes.ToArray()).IsEquivalentTo(expected);
        await Assert.That(output.Closed).IsFalse();
        var failing = new Destination(101);
        await Assert.That(() => image.SaveAsJxl(failing, options)).Throws<IOException>();
        await Assert.That(failing.Bytes.ToArray()).IsEquivalentTo(expected[..101]);
        await Assert.That(failing.Closed).IsFalse();
        // Reuse pooled storage immediately after the exceptional path.
        await Assert.That(image.ToJxl(options)).IsEquivalentTo(expected);
    }

}
