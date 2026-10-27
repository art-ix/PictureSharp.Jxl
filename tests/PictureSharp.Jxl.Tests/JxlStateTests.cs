using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Rendering;
using PictureSharp.Formats.Jxl.Container;
using System.Buffers;
using System.Collections;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

public sealed class JxlStateTests
{
    [Test]
    public async Task Headers_SnapshotCollections_AndDefaultsCannotBeModified()
    {
        float[] weights = [1, 2, 3, 4, 5, 6];
        var filter = new JxlLoopFilter { GaborishWeights = weights };
        weights[0] = 99;
        await Assert.That(filter.GaborishWeights[0]).IsEqualTo(1f);
        await Assert.That(() => ((IList<float>)filter.GaborishWeights)[0] = 42).Throws<NotSupportedException>();
        float original = JxlCustomTransformData.Default.EffectiveOpsinInverseMatrix[0];
        await Assert.That(() => ((IList<float>)JxlCustomTransformData.Default.EffectiveOpsinInverseMatrix)[0] = 0)
            .Throws<NotSupportedException>();
        await Assert.That(JxlCustomTransformData.Default.EffectiveOpsinInverseMatrix[0]).IsEqualTo(original);
        await Assert.That(() => ((IList<uint>)JxlWeightedHeader.Default.Weights)[0] = 0).Throws<NotSupportedException>();
        uint[] shifts = [1];
        var passes = new JxlPasses(2, shifts, [], []);
        shifts[0] = 20;
        await Assert.That(passes.Shift[0]).IsEqualTo(1u);
        JxlTransform[] transforms = [new() { Id = JxlTransformId.Rct }];
        var group = new JxlGroupHeader(false, JxlWeightedHeader.Default, transforms);
        transforms[0] = new() { Id = JxlTransformId.Palette };
        await Assert.That(group.Transforms[0].Id).IsEqualTo(JxlTransformId.Rct);
    }

    [Test]
    public async Task Codestream_OwnsItsSegmentTable_ButBorrowsThePayload()
    {
        byte[] payload = [1, 2, 3];
        ReadOnlyMemory<byte>[] segments = [payload];
        var stream = new JxlCodestream(segments);
        segments[0] = new byte[20];
        payload[0] = 9;
        await Assert.That(stream.Length).IsEqualTo(3L);
        await Assert.That(stream.Segments[0].Span[0]).IsEqualTo((byte)9);
        await Assert.That(() => stream.Slice(2, 2)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task References_ValidateSlots_AndReplaceOnlyTheSelectedSlot()
    {
        var references = new JxlReferenceFrames();
        var first = new JxlReferenceFrame([new JxlPlane(1, 1)], []);
        var second = new JxlReferenceFrame([new JxlPlane(2, 2)], []);
        await Assert.That(() => references.GetPatch(0)).Throws<ImageFormatException>();
        await Assert.That(() => references.GetDc(0)).Throws<ImageFormatException>();
        await Assert.That(() => references.StoreReference(4, first, true)).Throws<ImageFormatException>();
        await Assert.That(() => references.StoreReference(0, first, false)).Throws<NotSupportedException>();
        await Assert.That(() => references.StoreDc(0, first)).Throws<ImageFormatException>();
        references.StoreReference(0, first, true);
        references.StoreReference(1, first, true);
        references.StoreReference(0, second, true);
        references.StoreDc(1, first);
        await Assert.That(references.GetPatch(0)).IsSameReferenceAs(second);
        await Assert.That(references.GetPatch(1)).IsSameReferenceAs(first);
        await Assert.That(references.GetDc(0)[0]).IsSameReferenceAs(first.Color[0]);
        references.StoreDc(1, second);
        await Assert.That(references.GetDc(0)[0]).IsSameReferenceAs(second.Color[0]);
    }

    [Test]
    public async Task FrameProgress_RejectsSkippedRepeatedAndPrematureStages()
    {
        var progress = new JxlFrameProgress(1, 2, 2);
        await Assert.That(progress.Consume).Throws<InvalidOperationException>();
        await Assert.That(progress.BeginHf).Throws<InvalidOperationException>();
        progress.BeginLf();
        await Assert.That(progress.BeginLf).Throws<InvalidOperationException>();
        await Assert.That(progress.FinishLf).Throws<InvalidOperationException>();
        progress.LfGroup(0);
        progress.FinishLf();
        progress.BeginHf();
        await Assert.That(() => progress.BeginGroup(1)).Throws<InvalidOperationException>();
        for (int group = 0; group < 2; group++)
        {
            progress.BeginGroup(group);
            await Assert.That(() => progress.Pass(group, 1)).Throws<InvalidOperationException>();
            progress.Pass(group, 0);
            await Assert.That(() => progress.FinishGroup(group)).Throws<InvalidOperationException>();
            progress.Pass(group, 1);
            progress.FinishGroup(group);
        }
        progress.Consume();
        await Assert.That(progress.Consume).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task InvalidDefaultValuesAndBufferSizes_FailBeforeAllocation()
    {
        await Assert.That(() => default(JxlSizeHeader).Validate()).Throws<ImageFormatException>();
        await Assert.That(() => default(JxlBitDepth).Validate()).Throws<ImageFormatException>();
        await Assert.That(() => new JxlBitDepth(true, 8, 8)).Throws<ImageFormatException>();
        await Assert.That(() => new JxlPlane(int.MaxValue, 1)).Throws<NotSupportedException>();
        await Assert.That(() => new JxlPlane(0, 1)).Throws<ImageFormatException>();
        await Assert.That(() => new JxlChannel(-1, 1)).Throws<ImageFormatException>();
        await Assert.That(new JxlChannel(0, 1).IsEmpty).IsTrue();
        await Assert.That(new JxlBitDepth(false, 31, 0).BitsPerSample).IsEqualTo(31u);
    }

    [Test]
    public async Task Render_ObservesCancellationRaisedAfterKernelWorkBegins()
    {
        using var cancellation = new CancellationTokenSource();
        var weights = new CancelingWeights(JxlCustomTransformData.Default.UpsamplingWeights(1), cancellation);
        var plane = new JxlPlane(8, 8);
        // No timer or scheduling race: reading the kernel cancels a token that was active on entry.
        await Assert.That(cancellation.IsCancellationRequested).IsFalse();
        await Assert.That(() => JxlRenderPipeline.Upsample(plane, 1, weights, 16, 16, cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(weights.Reads).IsGreaterThan(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task SymbolWindow_IsReturnedOnce_OnSuccessCorruptionAndCancellation(int failure)
    {
        var pool = new CountingPool();
        if (failure == 1)
            await Assert.That(() => ReadSymbols(pool, failure)).Throws<ImageFormatException>();
        else if (failure == 2)
            await Assert.That(() => ReadSymbols(pool, failure)).Throws<OperationCanceledException>();
        else
            ReadSymbols(pool, failure);
        await Assert.That(pool.Rents).IsEqualTo(1);
        await Assert.That(pool.Returns).IsEqualTo(1);
    }

    private static void ReadSymbols(CountingPool pool, int failure)
    {
        // LZ77, default minimum token/length, length split=8, one cluster, prefix, split=15, alphabet={0}.
        byte[] data = new BitWriter().Bool(true).Bits(0, 2).Bits(0, 2).Bits(8, 4)
            .Bool(true).Bits(0, 2).Bool(true).Bits(15, 4).Bool(false).ToArray();
        var reader = new JxlBitReader(new JxlCodestream(data));
        JxlEntropyCode code = JxlEntropyCode.Read(ref reader, 1);
        using var symbols = new JxlSymbolReader(code, ref reader, windowPool: pool);
        if (failure == 1) reader.ReadBits(32);
        if (failure == 2) new CancellationToken(true).ThrowIfCancellationRequested();
        symbols.Read(0, ref reader);
        symbols.Dispose(); // The using scope disposes again.
        try
        {
            symbols.Read(0, ref reader);
            Assert.Fail("A disposed symbol reader accepted more input.");
        }
        catch (ObjectDisposedException) { }
    }

    [Test]
    public async Task MetadataIndex_BorrowsEncodedBlocks_AndIsOptIn()
    {
        byte[] file = Boxed(FileType(), Box("Exif", 0, 0, 0, 0, 42), Box("brob", [.. "xml "u8, 7, 8]), Box("jxlc", Minimal()));
        await Assert.That(JxlContainer.Parse(file).MetadataBoxes).IsEmpty();
        JxlContainer indexed = JxlContainer.Parse(file, includeMetadata: true);
        await Assert.That(indexed.MetadataBoxes.Count).IsEqualTo(2);
        await Assert.That(indexed.MetadataBoxes[0].Type).IsEqualTo("Exif");
        await Assert.That(indexed.MetadataBoxes[1].IsBrotliCompressed).IsTrue();
        await Assert.That(indexed.MetadataBoxes[1].Payload.ToArray()).IsEquivalentTo(new byte[] { 7, 8 });
        bool borrowed = MemoryMarshal.TryGetArray(indexed.MetadataBoxes[0].Payload, out ArraySegment<byte> segment);
        await Assert.That(borrowed).IsTrue();
        await Assert.That(segment.Array).IsSameReferenceAs(file);
    }

    [Test]
    public async Task OutputColor_DescribesTheRenderedPixels_AndSessionIsSingleUse()
    {
        JxlCodestream stream = JxlContainer.Parse(File.ReadAllBytes(CorpusPath("icc-profile.jxl"))).Codestream;
        JxlHeaders headers = JxlHeadersReader.Read(stream);
        var session = new JxlDecodeSession(stream, headers);
        JxlDecodedImage result = session.Decode();
        await Assert.That(result.ColorEncoding.WantIcc).IsEqualTo(headers.Metadata.Color.WantIcc && !headers.Metadata.XybEncoded);
        await Assert.That(() => session.Decode()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task OutputColor_FallbackMatchesConversion_ForIccUnknownTransferAndGrayWhitePoint()
    {
        JxlColorEncoding[] sources =
        [
            new() { WantIcc = true, Primaries = JxlPrimaries.P3 },
            new() { TransferFunction = JxlTransferFunction.Unknown },
            new() { ColorSpace = JxlColorSpace.Gray, WhitePoint = JxlWhitePoint.Dci }
        ];
        foreach (JxlColorEncoding source in sources)
        {
            var metadata = new JxlImageMetadata { Color = source };
            JxlColorEncoding output = JxlColorOutput.DescribeOutput(metadata, JxlColorTransform.Xyb);
            await Assert.That(output.WantIcc).IsFalse();
            await Assert.That(output.TransferFunction).IsEqualTo(JxlTransferFunction.Srgb);
            await Assert.That(output.WhitePoint).IsEqualTo(JxlWhitePoint.D65);
            await Assert.That(output.ColorSpace).IsEqualTo(source.ColorSpace);
            await Assert.That(JxlColorOutput.DescribeOutput(metadata, JxlColorTransform.None)).IsSameReferenceAs(source);
        }
    }

    [Test]
    public async Task ConcurrentDecodes_DoNotShareWorkingState()
    {
        string[] names = ["palette.jxl", "squeeze.jxl", "lossy-patches.jxl", "lossy-progressive-dc.jxl", "lossy-epf3.jxl"];
        DecoderOptions options = Options();
        byte[][] inputs = [.. names.Select(name => File.ReadAllBytes(CorpusPath(name)))];
        string Hash(byte[] input)
        {
            using Image image = Image.Load(input, options);
            return Convert.ToHexString(SHA256.HashData(JxlDecodeOracleTests.Rgba(image)));
        }
        string[] expected = [.. inputs.Select(Hash)];
        string[] actual = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => Hash(inputs[i % inputs.Length]))));
        for (int i = 0; i < actual.Length; i++)
            await Assert.That(actual[i]).IsEqualTo(expected[i % inputs.Length]);
    }

    private sealed class CountingPool : ArrayPool<uint>
    {
        public int Rents { get; private set; }
        public int Returns { get; private set; }
        public override uint[] Rent(int minimumLength) { Rents++; return new uint[minimumLength]; }
        public override void Return(uint[] array, bool clearArray = false) { Returns++; }
    }

    private sealed class CancelingWeights(IReadOnlyList<float> values, CancellationTokenSource cancellation) : IReadOnlyList<float>
    {
        public int Reads { get; private set; }
        public int Count => values.Count;
        public float this[int index] { get { Reads++; cancellation.Cancel(); return values[index]; } }
        public IEnumerator<float> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
