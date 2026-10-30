using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;
using PictureSharp.Formats.Jxl.Integration;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>The 0.8.4 Modular decoding kernels against their frozen or checked counterparts.</summary>
public sealed class JxlModularKernelTests
{
    [Test]
    public async Task WeightedPredictor_MatchesFrozenBaseline()
    {
        var random = new Random(84);
        JxlWeightedHeader[] headers =
        [
            JxlWeightedHeader.Default,
            new(0, 0, 0, 0, 0, 0, 0, [0, 0, 0, 0]),
            new(31, 31, 31, 31, 31, 31, 31, [15, 15, 15, 15]),
            .. Enumerable.Range(0, 6).Select(_ => new JxlWeightedHeader(random.Next(32), random.Next(32), random.Next(32),
                random.Next(32), random.Next(32), random.Next(32), random.Next(32),
                [(uint)random.Next(16), (uint)random.Next(16), (uint)random.Next(16), (uint)random.Next(16)])),
        ];
        Func<int, int>[] samples =
        [
            _ => random.Next(256),
            _ => random.Next(65536),
            _ => random.Next(2) == 0 ? int.MinValue + random.Next(4) : int.MaxValue - random.Next(4),
            _ => random.Next(),
            i => (i * 37 % 255) + random.Next(3),
        ];
        int mismatches = 0;
        foreach (JxlWeightedHeader header in headers)
        {
            foreach (Func<int, int> sample in samples)
            {
                foreach (int width in (int[])[1, 2, 3, 4, 7, 8, 9, 17])
                {
                    mismatches += Compare(header, width, 6, sample);
                }
            }
        }

        await Assert.That(mismatches).IsEqualTo(0);
    }

    private static int Compare(JxlWeightedHeader header, int width, int height, Func<int, int> sample)
    {
        int[] pixels = [.. Enumerable.Range(0, width * height).Select(sample)];
        using var actual = new JxlWeightedPredictor(header, width);
        using var expected = new JxlWeightedPredictorScalar(header, width);
        int mismatches = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var neighbours = JxlNeighbours.At(pixels, width, x, y);
                long prediction = actual.Predict(x, y, neighbours, out int maxError);
                long reference = expected.Predict(x, y, neighbours, out int referenceError);
                int error = actual.Update(pixels[y * width + x], x, y);
                int referenceUpdate = expected.Update(pixels[y * width + x], x, y);
                mismatches += prediction != reference || maxError != referenceError || error != referenceUpdate ? 1 : 0;
            }
        }

        return mismatches;
    }

    [Test]
    public async Task WeightedPredictor_RejectsPositionsOutsideTheRow()
    {
        using var predictor = new JxlWeightedPredictor(JxlWeightedHeader.Default, 4);
        await Assert.That(() => predictor.Predict(4, 0, default, out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => predictor.Predict(-1, 0, default, out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => predictor.Update(0, 4, 0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ReadRow_MatchesCheckedReads_AcrossSlices()
    {
        var random = new Random(8);
        byte[] data = [.. Enumerable.Range(0, 200).Select(_ => (byte)random.Next(256))];
        int[] widths = [.. Enumerable.Range(0, 120).Select(i => i % 7 == 0 ? 0 : random.Next(1, 25))];
        int[][] splits = [[], [1], [5, 6, 7], [8, 16, 17], [3, 60, 61, 62, 199]];
        foreach (int[] cuts in splits)
        {
            foreach (int count in (int[])[0, 1, 9, 120])
            {
                (uint[] expected, long expectedEnd) = ReadChecked(Split(data, cuts), widths, count);
                (uint[] actual, int filled, _, long end) = ReadRow(Split(data, cuts), widths, count, stopValue: -1);
                await Assert.That(filled).IsEqualTo(count);
                await Assert.That(actual).IsEquivalentTo(expected);
                await Assert.That(end).IsEqualTo(expectedEnd);
            }
        }
    }

    private static (uint[] Values, long End) ReadChecked(JxlCodestream codestream, int[] widths, int count)
    {
        var reader = new JxlBitReader(codestream);
        uint[] values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadBits(widths[i]);
        }

        return (values, reader.BitPosition);
    }

    private static (uint[] Values, int Filled, uint Stop, long End) ReadRow(JxlCodestream codestream, int[] widths, int count,
        int stopValue)
    {
        var reader = new JxlBitReader(codestream);
        var decoder = new WidthDecoder(widths, stopValue);
        uint[] values = new uint[count];
        int filled = reader.ReadRow(ref decoder, values, out uint stop);
        return (values, filled, stop, reader.BitPosition);
    }

    [Test]
    public async Task ReadRow_StopsAtTheStopSymbol_AndFailsAtTheEnd()
    {
        byte[] data = [.. Enumerable.Range(0, 40).Select(i => (byte)(i * 29 + 3))];
        int[] widths = [.. Enumerable.Repeat(8, 64)];
        // Bytes are read 8 bits at a time, so the value of symbol i is data[i].
        int stopAt = 30;
        (_, int filled, uint stop, long end) = ReadRow(Split(data, 13), widths, 64, stopValue: data[stopAt]);
        await Assert.That(filled).IsEqualTo(stopAt);
        await Assert.That(stop).IsEqualTo((uint)data[stopAt]);
        await Assert.That(end).IsEqualTo(8L * (stopAt + 1));
        await Assert.That(() => ReadRow(Split(data, 13), widths, 64, stopValue: -1)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task IntegerPixels_MatchFrozenWriter()
    {
        var random = new Random(16);
        int mismatches = 0;
        foreach (int width in (int[])[1, 3, 4, 7, 8, 9, 17, 33])
        foreach (int bits in (int[])[8, 12, 16, 31])
        foreach (bool gray in (bool[])[false, true])
        foreach (bool alpha in (bool[])[false, true])
        foreach (bool wide in (bool[])[false, true])
        {
            const int height = 3;
            JxlChannel Channel() => new(width, height, [.. Enumerable.Range(0, width * height).Select(i =>
                i % 5 == 0 ? random.Next(int.MinValue, int.MaxValue) : random.Next(1 << Math.Min(bits, 30)))]);
            var samples = new JxlIntegerImage(gray ? [Channel()] : [Channel(), Channel(), Channel()], alpha ? Channel() : null, bits, bits);
            var decoded = new JxlDecodedImage(width, height, [], null, gray, JxlColorEncoding.Srgb) { IntegerSamples = samples };
            using Image actual = wide ? JxlPixelWriter.WriteWide(decoded, default) : JxlPixelWriter.Write(decoded, default);
            using Image expected = wide ? JxlPixelWriterScalar.WriteWide(decoded, default) : JxlPixelWriterScalar.Write(decoded, default);
            bool same = wide
                ? actual.GetPixelMemory<Rgba64>().Span.SequenceEqual(expected.GetPixelMemory<Rgba64>().Span)
                : actual.GetPixelMemory<Rgba32>().Span.SequenceEqual(expected.GetPixelMemory<Rgba32>().Span);
            mismatches += same ? 0 : 1;
        }

        await Assert.That(mismatches).IsEqualTo(0);
    }

    private static JxlCodestream Split(byte[] data, params int[] cuts)
    {
        var segments = new List<ReadOnlyMemory<byte>>();
        int start = 0;
        foreach (int cut in cuts.Append(data.Length))
        {
            segments.Add(data.AsMemory(start, cut - start));
            start = cut;
        }

        return new JxlCodestream([.. segments]);
    }

    /// <summary>Reads symbol i as widths[i] raw bits; a value equal to the stop value ends the row.</summary>
    private struct WidthDecoder(int[] widths, int stopValue) : IJxlBitDecoder
    {
        private int next;

        public static int MaxBits => 24;

        public bool TryDecode(ref ulong bits, ref int buffered, out uint value)
        {
            value = JxlBitReader.Take(ref bits, ref buffered, widths[next++]);
            return value != (uint)stopValue;
        }

        public bool TryDecode(ref JxlBitReader reader, out uint value)
        {
            value = reader.ReadBits(widths[next++]);
            return value != (uint)stopValue;
        }
    }
}
