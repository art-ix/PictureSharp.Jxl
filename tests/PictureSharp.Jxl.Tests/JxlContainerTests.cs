using System.Buffers.Binary;
using PictureSharp.Formats.Jxl.Codestream;
using PictureSharp.Formats.Jxl.Container;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

public sealed class JxlContainerTests
{
    private static byte[] Joined(JxlContainer container) => [.. container.Codestream.Segments.SelectMany(segment => segment.ToArray())];

    [Test]
    public async Task RawCodestream_IsTheWholeInput()
    {
        byte[] data = Minimal();
        JxlContainer container = JxlContainer.Parse(data);
        await Assert.That(container.IsBoxed).IsFalse();
        await Assert.That(Joined(container)).IsEquivalentTo(data);
    }

    [Test]
    public async Task Jxlc_IsTheCodestream_AndMetadataBoxesAreSkipped()
    {
        byte[] codestream = Minimal();
        byte[] file = Boxed(FileType(), Box("jxll", 10), Box("Exif", 0, 0, 0, 0), Box("brob", [.. "xml "u8, 1, 2]), Box("jxlc", codestream), Box("xml ", 1));
        JxlContainer container = JxlContainer.Parse(file);
        await Assert.That(container.IsBoxed).IsTrue();
        await Assert.That(container.Level).IsEqualTo(10);
        await Assert.That(Joined(container)).IsEquivalentTo(codestream);
    }

    [Test]
    public async Task Jxlp_InOrder_IsJoined()
    {
        byte[] codestream = Minimal();
        byte[] file = Boxed(FileType(), Partial(0, false, codestream.AsSpan(0, 1)), Box("Exif", 0, 0, 0, 0),
            Partial(1, false, []), Partial(2, true, codestream.AsSpan(1)));
        JxlContainer container = JxlContainer.Parse(file);
        await Assert.That(container.Codestream.Segments.Length).IsEqualTo(3);
        await Assert.That(Joined(container)).IsEquivalentTo(codestream);
        await Assert.That(JxlHeaders.Read(container.Codestream).Size).IsEqualTo(new JxlSizeHeader(8, 8));
    }

    [Test]
    public async Task Jxlp_OutOfOrder_RequiresFileFormatVersion1()
    {
        byte[] codestream = Minimal();
        byte[][] parts = [Partial(2, true, codestream.AsSpan(2)), Partial(0, false, codestream.AsSpan(0, 1)), Partial(1, false, codestream.AsSpan(1, 1))];
        JxlContainer container = JxlContainer.Parse(Boxed([FileType(1), .. parts]));
        await Assert.That(container.FileFormatVersion).IsEqualTo(1u);
        await Assert.That(Joined(container)).IsEquivalentTo(codestream);
        await Assert.That(() => JxlContainer.Parse(Boxed([FileType(0), .. parts]))).Throws<ImageFormatException>();
    }

    [Test]
    [Arguments("duplicate")]
    [Arguments("gap")]
    [Arguments("early-last")]
    [Arguments("after-last")]
    [Arguments("short-index")]
    public async Task Jxlp_InvalidSequences_AreCorrupt(string kind)
    {
        byte[] cs = Minimal();
        byte[][] parts = kind switch
        {
            "duplicate" => [Partial(0, false, cs), Partial(0, true, [])],
            "gap" => [Partial(0, false, cs), Partial(2, true, [])],
            "early-last" => [Partial(0, true, cs), Partial(1, false, [])],
            "after-last" => [Partial(0, false, cs), Partial(1, true, []), Partial(2, false, [])],
            _ => [Box("jxlp", 0x80, 0, 0)]
        };
        foreach (uint version in new uint[] { 0, 1 })
        {
            await Assert.That(() => JxlContainer.Parse(Boxed([FileType(version), .. parts]))).Throws<ImageFormatException>();
        }
    }

    [Test]
    [Arguments("jxlc+jxlp")]
    [Arguments("jxlp+jxlc")]
    [Arguments("two-jxlc")]
    [Arguments("no-codestream")]
    [Arguments("no-ftyp")]
    [Arguments("ftyp-third")]
    [Arguments("ftyp-brand")]
    [Arguments("ftyp-version")]
    [Arguments("ftyp-short")]
    [Arguments("brob-jxlc")]
    [Arguments("signature-inside")]
    [Arguments("empty-level")]
    public async Task InvalidBoxStructures_AreCorrupt(string kind)
    {
        byte[] cs = Minimal();
        byte[][] boxes = kind switch
        {
            "jxlc+jxlp" => [FileType(), Box("jxlc", cs), Partial(0, true, [])],
            "jxlp+jxlc" => [FileType(), Partial(0, false, cs), Box("jxlc", cs)],
            "two-jxlc" => [FileType(), Box("jxlc", cs), Box("jxlc", cs)],
            "no-codestream" => [FileType(), Box("Exif", 0, 0, 0, 0)],
            "no-ftyp" => [Box("jxlc", cs)],
            "ftyp-third" => [Box("jxll", 5), FileType(), Box("jxlc", cs)],
            "ftyp-brand" => [Box("ftyp", [.. "jxs "u8, 0, 0, 0, 0, .. "jxl "u8]), Box("jxlc", cs)],
            "ftyp-version" => [FileType(2), Box("jxlc", cs)],
            "ftyp-short" => [Box("ftyp", [.. "jxl "u8, 0, 0, 0, 0]), Box("jxlc", cs)],
            "brob-jxlc" => [FileType(), Box("brob", [.. "jxlc"u8, 1, 2, 3])],
            "signature-inside" => [FileType(), Box("JXL ", 13, 10, 0x87, 10), Box("jxlc", cs)],
            _ => [FileType(), Box("jxll"), Box("jxlc", cs)]
        };
        await Assert.That(() => JxlContainer.Parse(Boxed(boxes))).Throws<ImageFormatException>();
    }

    [Test]
    public async Task Jxlp_WithoutALastPart_IsAccepted_LikeLibjxl()
    {
        byte[] cs = Minimal();
        JxlContainer container = JxlContainer.Parse(Boxed(FileType(1), Partial(1, false, cs.AsSpan(1)), Partial(0, false, cs.AsSpan(0, 1))));
        await Assert.That(Joined(container)).IsEquivalentTo(cs);
        JxlContainer truncated = JxlContainer.Parse(Boxed(FileType(), Partial(0, false, cs.AsSpan(0, 2))));
        await Assert.That(() => JxlHeaders.Read(truncated.Codestream)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task SecondSignature_AfterACompleteCodestream_EndsTheFile()
    {
        byte[] cs = Minimal();
        byte[] file = [.. Boxed(FileType(), Box("jxlc", cs)), .. Boxed(FileType(), Box("jxlc", [1, 2, 3]))];
        await Assert.That(Joined(JxlContainer.Parse(file))).IsEquivalentTo(cs);
    }

    [Test]
    public async Task BoxSizes_ZeroLargeAndInvalid()
    {
        byte[] cs = Minimal();
        byte[] toEnd = Box("jxlc", cs);
        BinaryPrimitives.WriteUInt32BigEndian(toEnd, 0);
        await Assert.That(Joined(JxlContainer.Parse(Boxed(FileType(), toEnd)))).IsEquivalentTo(cs);

        byte[] large = [0, 0, 0, 1, .. "jxlc"u8, 0, 0, 0, 0, 0, 0, 0, (byte)(16 + cs.Length), .. cs];
        await Assert.That(Joined(JxlContainer.Parse(Boxed(FileType(), large)))).IsEquivalentTo(cs);

        foreach (ulong size in new ulong[] { 2, 15, (ulong)(17 + cs.Length), ulong.MaxValue, 1UL << 63 })
        {
            byte[] invalid = [.. large];
            BinaryPrimitives.WriteUInt64BigEndian(invalid.AsSpan(8), size);
            await Assert.That(() => JxlContainer.Parse(Boxed(FileType(), invalid))).Throws<ImageFormatException>();
        }

        byte[] small = Box("jxlc", cs);
        BinaryPrimitives.WriteUInt32BigEndian(small, 7);
        await Assert.That(() => JxlContainer.Parse(Boxed(FileType(), small))).Throws<ImageFormatException>();
        // A size-zero box extends to the end of the file, so a later box is part of its content.
        byte[] trailing = Box("Exif", 1, 2, 3, 4);
        await Assert.That(Joined(JxlContainer.Parse(Boxed(FileType(), toEnd, trailing)))).IsEquivalentTo([.. cs, .. trailing]);
    }

    [Test]
    public async Task EveryTruncatedPrefix_IsCorrupt()
    {
        byte[] cs = Minimal();
        byte[] file = Boxed(FileType(), Box("Exif", 0, 0, 0, 0), Partial(0, false, cs.AsSpan(0, 2)), Partial(1, true, cs.AsSpan(2)));
        await Assert.That(Joined(JxlContainer.Parse(file))).IsEquivalentTo(cs);
        for (int length = 0; length < file.Length; length++)
        {
            await Assert.That(() => JxlHeaders.Read(JxlContainer.Parse(file.AsMemory(0, length)).Codestream)).Throws<ImageFormatException>();
        }
    }
}
