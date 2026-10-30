using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Encoding;
using PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;
using PictureSharp.Formats.Jxl.Codestream.Modular;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.VarDct;
using static PictureSharp.Formats.Jxl.Tests.JxlCodestreamWriterTests;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>0.8.3 encoder changes: lossless effort tiers, the fixed trees, batched bit writing and lane-ordered sums.</summary>
public sealed class JxlEncoderTierTests
{
    private static readonly ImageConfiguration Formats = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();

    private static JxlEncoderOptions Lossless(int effort) => new() { Configuration = Formats, Distance = 0, Effort = effort };

    [Test]
    [Arguments(Pattern.Noise, false, false)]
    [Arguments(Pattern.Gradient, true, false)]
    [Arguments(Pattern.FewColors, false, true)]
    [Arguments(Pattern.Gradient, false, true)]
    [Arguments(Pattern.Flat, true, true)]
    public async Task LosslessEfforts_RoundTripExactly(Pattern pattern, bool gray, bool alpha)
    {
        foreach ((int width, int height) in new[] { (1, 1), (37, 19), (300, 270) })
        {
            byte[] rgba = Pixels(width, height, pattern, alpha);
            if (gray)
            {
                for (int i = 0; i < rgba.Length; i += 4) rgba[i + 1] = rgba[i + 2] = rgba[i];
            }

            using Image image = Image.LoadPixelData(rgba, width, height, PixelFormat.Rgba32, width * 4);
            for (int effort = 1; effort <= 9; effort++)
            {
                using Image decoded = Image.Load(image.ToJxl(Lossless(effort)), new DecoderOptions { Configuration = Formats });
                await Assert.That(JxlDecodeOracleTests.Rgba(decoded).SequenceEqual(rgba)).IsTrue()
                    .Because($"{width}x{height} {pattern} gray={gray} alpha={alpha} e{effort}");
            }
        }
    }

    [Test]
    public async Task LosslessEfforts_RoundTrip16BitSamplesAboveTheSignedRange()
    {
        using var image = new Image(150, 90, PixelType.Rgba64, new Rgba64(0, 0, 0, ushort.MaxValue));
        Span<Rgba64> pixels = image.GetPixelSpan<Rgba64>();
        for (int i = 0; i < pixels.Length; i++)
        {
            // Few colours above 32767 (a palette candidate), noise below, and alpha.
            ushort v = (ushort)(i % 3 == 0 ? 65535 - (i % 5) * 257 : (i * 2791) & 65535);
            pixels[i] = new Rgba64(v, (ushort)(v ^ 0x5555), (ushort)(65535 - v), (ushort)(40000 + (i % 7)));
        }

        Rgba64[] expected = pixels.ToArray();
        for (int effort = 1; effort <= 9; effort++)
        {
            using Image decoded = Image.Load(image.ToJxl(Lossless(effort)), new DecoderOptions { Configuration = Formats, PreserveBitDepth = true });
            await Assert.That(decoded.GetPixelSpan<Rgba64>().SequenceEqual(expected)).IsTrue().Because($"e{effort}");
        }
    }

    [Test]
    public async Task LosslessEfforts_7To9AreIdenticalAndLowerTiersDiffer()
    {
        byte[] rgba = Pixels(200, 150, Pattern.Gradient, alpha: false);
        using Image image = Image.LoadPixelData(rgba, 200, 150, PixelFormat.Rgba32, 800);
        byte[][] encoded = [.. Enumerable.Range(1, 9).Select(effort => image.ToJxl(Lossless(effort)))];
        await Assert.That(encoded[7].SequenceEqual(encoded[6]) && encoded[8].SequenceEqual(encoded[6])).IsTrue();
        await Assert.That(encoded.Take(6).Select(bytes => Convert.ToHexString(bytes)).Distinct().Count()).IsEqualTo(6);
        await Assert.That(encoded.Take(6).All(bytes => !bytes.SequenceEqual(encoded[6]))).IsTrue();
    }

    [Test]
    [Property("Category", "Oracle")]
    [Arguments(Pattern.Noise, false, true)]
    [Arguments(Pattern.Gradient, true, false)]
    [Arguments(Pattern.FewColors, false, false)]
    public async Task LosslessEfforts_DecodeLikeDjxl(Pattern pattern, bool gray, bool alpha)
    {
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(djxl is not null, "djxl v0.12.0 is not available (PATH or JXL_TOOLS).");
        byte[] rgba = Pixels(300, 270, pattern, alpha);
        for (int effort = 1; effort <= 6; effort++)
        {
            string path = Path.Combine(Path.GetTempPath(), $"jxl-tier-{Guid.NewGuid():N}.jxl");
            try
            {
                File.WriteAllBytes(path, Encode(rgba, 300, 270, gray, alpha, effort: effort));
                using Image decoded = JxlDecodeOracleTests.DjxlImage(djxl!, path);
                byte[] actual = JxlDecodeOracleTests.Rgba(decoded);
                int mismatch = Enumerable.Range(0, actual.Length).FirstOrDefault(i => actual[i] != (gray && i % 4 is 1 or 2 ? rgba[i - i % 4] : rgba[i]), -1);
                await Assert.That(mismatch).IsEqualTo(-1).Because($"e{effort}");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Test]
    public async Task Lossless16_WidenedEightBitIsAsSmallAsEightBitAtEveryEffort()
    {
        // 16-bit samples v × 257: the palette (samples above 32767) or channel compaction must find the 8-bit content.
        foreach (Pattern pattern in new[] { Pattern.Noise, Pattern.FewColors, Pattern.Gradient })
        {
            byte[] rgba = Pixels(160, 120, pattern, alpha: true);
            using Image narrow = Image.LoadPixelData(rgba, 160, 120, PixelFormat.Rgba32, 640);
            using var wide = new Image(160, 120, PixelType.Rgba64, default);
            Span<Rgba64> pixels = wide.GetPixelSpan<Rgba64>();
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Rgba64((ushort)(rgba[4 * i] * 257), (ushort)(rgba[4 * i + 1] * 257), (ushort)(rgba[4 * i + 2] * 257), (ushort)(rgba[4 * i + 3] * 257));
            }

            Rgba64[] expected = pixels.ToArray();
            foreach (int effort in new[] { 1, 2, 3, 4, 5, 6, 7 })
            {
                byte[] encoded = wide.ToJxl(Lossless(effort));
                int eightBit = narrow.ToJxl(Lossless(effort)).Length;
                await Assert.That(encoded.Length).IsLessThan(eightBit * 11 / 10 + 64).Because($"{pattern} e{effort}");
                using Image decoded = Image.Load(encoded, new DecoderOptions { Configuration = Formats, PreserveBitDepth = true });
                await Assert.That(decoded.GetPixelSpan<Rgba64>().SequenceEqual(expected)).IsTrue().Because($"{pattern} e{effort}");
            }
        }
    }

    [Test]
    public async Task Palette_AcceptsUnsignedOrSignedSamplesButNotBoth()
    {
        // 64 rows of the values: enough pixels for the palette.
        JxlChannel Channel(params int[] values) => new(values.Length, 64, [.. Enumerable.Range(0, 64).SelectMany(_ => values)]);
        var high = JxlModularTransformChooser.Palette([Channel(0, 40000, 65535, 40000)], 1, default);
        await Assert.That(high).IsNotNull();
        await Assert.That(high!.Palette.Pixels.Take(3)).IsEquivalentTo(new[] { 0, 40000, 65535 });
        var signed = JxlModularTransformChooser.Palette([Channel(-32768, 5, 32767, 5)], 1, default);
        await Assert.That(signed!.Palette.Pixels.Take(3)).IsEquivalentTo(new[] { -32768, 5, 32767 });
        await Assert.That(JxlModularTransformChooser.Palette([Channel(-1, 5, 40000, 5)], 1, default)).IsNull();
        await Assert.That(JxlModularTransformChooser.Palette([Channel(0, 65536, 5, 5)], 1, default)).IsNull();
    }

    [Test]
    public async Task CompactChannel_OnlyWhenAQuarterOfTheRangeOccurs()
    {
        int[] Values(int count, int step) => [.. Enumerable.Range(0, 4096).Select(i => i % count * step)];
        // 256 values spread over 65280: indices 0–255, the palette sorted.
        var widened = JxlModularTransformChooser.CompactChannel(new JxlChannel(64, 64, Values(256, 257)), default);
        await Assert.That(widened).IsNotNull();
        await Assert.That(widened!.Transform.Colors).IsEqualTo(256u);
        await Assert.That(widened.Palette.Pixels.Take(256)).IsEquivalentTo(Enumerable.Range(0, 256).Select(v => v * 257));
        await Assert.That(widened.Indices.Pixels.Take(4096)).IsEquivalentTo(Enumerable.Range(0, 4096).Select(i => i % 256));
        // Half of the range used, or more colours than one per 16 pixels: unchanged.
        await Assert.That(JxlModularTransformChooser.CompactChannel(new JxlChannel(64, 64, Values(200, 2)), default)).IsNull();
        await Assert.That(JxlModularTransformChooser.CompactChannel(new JxlChannel(64, 64, Values(300, 100)), default)).IsNull();
        await Assert.That(JxlModularTransformChooser.CompactChannel(new JxlChannel(64, 64, Values(1, 1)), default)).IsNull();
    }

    [Test]
    public async Task FixedTrees_FollowMakeFixedTree()
    {
        // 2^20 pixels: no minimum gap, every cutoff splits; 16-bit samples scale the cutoffs by 16.
        List<JxlTreeNode> full = JxlModularEffort.For(2).FixedTree(1 << 20, 16, 3);
        await Assert.That(full.Count).IsEqualTo(67);
        await Assert.That(full.Count(node => node.IsLeaf)).IsEqualTo(34);
        await Assert.That(full[0]).IsEqualTo(new JxlTreeNode(JxlMaProperty.Gradient, 0, 1, 2, JxlPredictor.Zero));
        await Assert.That(full[1].Split).IsEqualTo(47 * 16);
        await Assert.That(full.Where(node => node.IsLeaf).All(node => node.Predictor == JxlPredictor.Gradient)).IsTrue();
        // 2^10 pixels: a gap of 32 cutoffs leaves only the root split.
        List<JxlTreeNode> small = JxlModularEffort.For(3).FixedTree(1 << 10, 8, 3);
        await Assert.That(small).IsEquivalentTo(new[]
        {
            new JxlTreeNode(JxlMaProperty.WeightedMaxError, 0, 1, 2, JxlPredictor.Zero), JxlTreeNode.Leaf(JxlPredictor.Weighted),
            JxlTreeNode.Leaf(JxlPredictor.Weighted),
        });
        // Effort 1: one gradient leaf per channel.
        List<JxlTreeNode> channels = JxlModularEffort.For(1).FixedTree(100, 8, 4);
        await Assert.That(channels.Count(node => node.IsLeaf)).IsEqualTo(4);
        await Assert.That(channels.Where(node => !node.IsLeaf).Select(node => (node.Property, node.Split)))
            .IsEquivalentTo(new[] { (JxlMaProperty.Channel, 0), (JxlMaProperty.Channel, 1), (JxlMaProperty.Channel, 2) });
        await Assert.That(JxlModularEffort.For(1).FixedTree(100, 8, 1)).IsEquivalentTo(new[] { JxlTreeNode.Leaf(JxlPredictor.Gradient) });
        await Assert.That(JxlModularEffort.For(8)).IsEqualTo(JxlModularEffort.Default);
        await Assert.That(JxlModularEffort.For(9)).IsEqualTo(JxlModularEffort.Default);
    }

    [Test]
    public async Task WritePacked_EqualsSeparateWrites()
    {
        var random = new Random(5);
        foreach (int lead in new[] { 0, 3, 7, 8, 13 })
        {
            var expected = new JxlBitWriter();
            var actual = new JxlBitWriter();
            expected.Write(lead > 0 ? lead : 0, 0x1ABCDUL);
            actual.Write(lead > 0 ? lead : 0, 0x1ABCDUL);
            ulong[] entries = new ulong[5000];
            for (int i = 0; i < entries.Length; i++)
            {
                int count = i % 97 == 0 ? 56 : random.Next(0, 57);
                ulong value = (ulong)random.NextInt64() & (count == 0 ? 0 : ulong.MaxValue >> (64 - count));
                entries[i] = value << JxlBitWriter.PackedCountBits | (uint)count;
                expected.Write(count, value);
            }

            actual.WritePacked(entries);
            expected.Write(5, 0x15);
            actual.Write(5, 0x15);
            await Assert.That(actual.BitLength).IsEqualTo(expected.BitLength);
            await Assert.That(actual.ToArray().SequenceEqual(expected.ToArray())).IsTrue().Because($"lead {lead}");
        }
    }

    [Test]
    public async Task NarrowTokens_AppendGrowAndValidate()
    {
        // 16-bit contexts (0.8.6): announced before the first token, appended as ushort, bounded by the announced count.
        using var buffers = new JxlBufferScope();
        var tokens = new JxlTokens(buffers);
        tokens.ExpectContexts(2000);
        for (int i = 0; i < 50; i++) tokens.Add(i, (uint)i * 3);
        await Assert.That(() => tokens.Append(10, out Span<int> _, out Span<uint> _)).Throws<InvalidOperationException>();
        tokens.Append(1000, out Span<ushort> contexts, out Span<uint> values);
        for (int i = 0; i < 1000; i++) (contexts[i], values[i]) = ((ushort)(i + 50), (uint)(i + 50) * 3);
        tokens.Add(1050, 3150);
        await Assert.That(tokens.Count).IsEqualTo(1051);
        await Assert.That(tokens.NarrowContexts).IsTrue();
        bool ordered = true;
        for (int i = 0; i < tokens.Count; i++) ordered &= tokens.NarrowContextValues[i] == i && tokens.Values[i] == (uint)i * 3;
        await Assert.That(ordered).IsTrue();
        await Assert.That(() => tokens.Add(2000, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => tokens.ExpectContexts(10)).Throws<InvalidOperationException>();
        await Assert.That(() => tokens.Contexts.Length).Throws<InvalidOperationException>();

        // Lent value storage narrows too: the contexts are created with the first token, at the lent capacity.
        var lent = new JxlTokens(buffers, 300);
        Memory<int> storage = lent.ValueStorage(0, 300);
        storage.Span.Fill(-5);
        lent.ExpectContexts(4);
        for (int i = 0; i < 300; i++) lent.Add(i % 4, (uint)i);
        await Assert.That(lent.NarrowContexts && lent.Count == 300 && lent.Values[299] == 299 && lent.NarrowContextValues[299] == 3).IsTrue();
    }

    [Test]
    public async Task TokensAppend_GrowsAndKeepsEarlierTokens()
    {
        using var tokens = new JxlTokens();
        for (int i = 0; i < 50; i++) tokens.Add(i, (uint)i * 3);
        tokens.Append(1000, out Span<int> contexts, out Span<uint> values);
        for (int i = 0; i < 1000; i++) (contexts[i], values[i]) = (i + 50, (uint)(i + 50) * 3);
        tokens.Add(1050, 3150);
        await Assert.That(tokens.Count).IsEqualTo(1051);
        bool ordered = true;
        for (int i = 0; i < tokens.Count; i++) ordered &= tokens.Contexts[i] == i && tokens.Values[i] == (uint)i * 3;
        await Assert.That(ordered).IsTrue();
    }

    [Test]
    public async Task EntropyEstimate_VectorEqualsScalarForEveryTransform()
    {
        const int Size = 64;
        var image = new JxlImage3F(Size, Size);
        var random = new Random(9);
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < Size; y++)
            {
                Span<float> row = image.Row(c, y);
                for (int x = 0; x < Size; x++) row[x] = (float)(random.NextDouble() - 0.3) * (c == 1 ? 1.0f : 0.1f);
            }
        }

        float[] quant = [.. Enumerable.Range(0, (Size / 8) * (Size / 8)).Select(i => 0.5f + (i % 7) * 0.25f)];
        float[] mask = [.. Enumerable.Range(0, Size * Size).Select(i => 0.2f + (i % 11) * 0.03f)];
        var strategies = new JxlVarblock[(Size / 8) * (Size / 8)];
        JxlAcStrategySearch.FillDct8(strategies);
        var search = new JxlAcStrategySearch(image, quant, mask, new JxlQuantWeights(), strategies, 1.0f, 3);
        float[] cmap = [0.1f, 1.0f, -0.3f];
        int checkedTypes = 0;
        foreach (JxlAcStrategyType type in Enum.GetValues<JxlAcStrategyType>())
        {
            if (type.BlocksX > 8 || type.BlocksY > 8) continue;
            foreach ((int x, int y) in new[] { (0, 0), (64 - 8 * type.BlocksX, 64 - 8 * type.BlocksY) })
            {
                float vector = search.EstimateEntropy(type, 1.0f, x, y, cmap);
                float scalar = search.EstimateEntropy(type, 1.0f, x, y, cmap, vector: false);
                await Assert.That(BitConverter.SingleToInt32Bits(vector)).IsEqualTo(BitConverter.SingleToInt32Bits(scalar)).Because($"{type} at {x},{y}");
            }

            checkedTypes++;
        }

        await Assert.That(checkedTypes).IsGreaterThan(20);
    }
}
