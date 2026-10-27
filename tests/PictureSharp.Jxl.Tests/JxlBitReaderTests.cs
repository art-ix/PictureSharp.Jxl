using PictureSharp.Formats.Jxl.Codestream.IO;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;

namespace PictureSharp.Formats.Jxl.Tests;

public sealed class JxlBitReaderTests
{
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

    [Test]
    public async Task Bits_AreReadLeastSignificantFirst_AcrossSegments()
    {
        byte[] data = [.. Enumerable.Range(0, 40).Select(i => (byte)(i * 37 + 11))];
        System.Numerics.BigInteger all = new(data, isUnsigned: true, isBigEndian: false);
        int[][] splits = [[], [1], [0, 3, 3, 9], [7, 8, 20, 39]];
        foreach (int[] cuts in splits)
        {
            int[] widths = [1, 3, 32, 0, 7, 17, 31, 2, 32, 32, 8, 5, 32, 24, 13, 32, 19, 1, 1, 28];
            long position = 0;
            uint[] values = Read(Split(data, cuts), widths, out long end);
            for (int i = 0; i < widths.Length; i++)
            {
                uint expected = (uint)((all >> (int)position) & ((System.Numerics.BigInteger.One << widths[i]) - 1));
                await Assert.That(values[i]).IsEqualTo(expected);
                position += widths[i];
            }

            await Assert.That(end).IsEqualTo(position);
        }
    }

    private static uint[] Read(JxlCodestream codestream, int[] widths, out long position)
    {
        var reader = new JxlBitReader(codestream);
        uint[] values = new uint[widths.Length];
        for (int i = 0; i < widths.Length; i++)
        {
            values[i] = reader.ReadBits(widths[i]);
        }

        position = reader.BitPosition;
        return values;
    }

    [Test]
    public async Task Skip_CrossesSegments_AndTruncationThrows()
    {
        byte[] data = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        await Assert.That(SkipThenByte(Split(data, 5, 6, 30), 3, 8 * 40 - 3)).IsEqualTo(40u);
        await Assert.That(SkipThenByte(Split(data), 32, 32)).IsEqualTo(8u);
        await Assert.That(() => SkipThenByte(Split(data, 10), 0, 64 * 8)).Throws<ImageFormatException>();
        await Assert.That(() => SkipThenByte(Split(data), 1, ulong.MaxValue)).Throws<ImageFormatException>();
        await Assert.That(() => ReadPast(Split(data, 63))).Throws<ImageFormatException>();
    }

    private static uint SkipThenByte(JxlCodestream codestream, int first, ulong skip)
    {
        var reader = new JxlBitReader(codestream);
        reader.ReadBits(first);
        reader.SkipBits(skip);
        return reader.ReadBits(8);
    }

    private static void ReadPast(JxlCodestream codestream)
    {
        var reader = new JxlBitReader(codestream);
        reader.SkipBits(63 * 8 + 1);
        reader.ReadBits(8);
    }

    [Test]
    public async Task ByteBoundary_RequiresZeroPadding()
    {
        await Assert.That(Boundary([0b0000_0101, 9])).IsEqualTo(9u);
        await Assert.That(() => Boundary([0b0001_0101, 9])).Throws<ImageFormatException>();
    }

    private static uint Boundary(byte[] data)
    {
        var reader = new JxlBitReader(new JxlCodestream(data));
        reader.ReadBits(3);
        reader.JumpToByteBoundary();
        return reader.ReadBits(8);
    }

    [Test]
    public async Task Fields_DecodeTheirDistributions()
    {
        byte[] data = new BitWriter().U32(0).U32(3, 5, 4).U64(0).U64(16).U64(272).U64(4095)
            .Bits(3, 2).Bits(0xABC, 12).Bool(true).Bits(0xFF, 8).Bool(true).Bits(0x12, 8).Bool(false)
            .F16((Half)(-1.5)).F16(Half.Epsilon).Enum(0).Enum(17).Enum(81).ToArray();
        var reader = new JxlBitReader(new JxlCodestream(data));
        uint[] u32 = [JxlFields.U32(ref reader, Val(7), Val(8), Val(9), BitsOffset(4, 100)), JxlFields.U32(ref reader, Val(7), Val(8), Val(9), BitsOffset(4, 100))];
        ulong[] u64 = [JxlFields.U64(ref reader), JxlFields.U64(ref reader), JxlFields.U64(ref reader), JxlFields.U64(ref reader), JxlFields.U64(ref reader)];
        float[] f16 = [JxlFields.F16(ref reader), JxlFields.F16(ref reader)];
        uint[] enums = [JxlFields.Enum(ref reader, ~0UL, "test"), JxlFields.Enum(ref reader, ~0UL, "test")];
        long position = reader.BitPosition;
        await Assert.That(u32).IsEquivalentTo(new uint[] { 7, 105 });
        await Assert.That(u64).IsEquivalentTo(new ulong[] { 0, 16, 272, 4095, 0xABC | (0xFFUL << 12) | (0x12UL << 20) });
        await Assert.That(f16).IsEquivalentTo(new[] { -1.5f, (float)Half.Epsilon });
        await Assert.That(enums).IsEquivalentTo(new uint[] { 0, 17 });
        await Assert.That(() => EnumAt(data, position)).Throws<ImageFormatException>();
        await Assert.That(() => F16Of(0x7C00)).Throws<ImageFormatException>();
        await Assert.That(() => F16Of(0xFE00)).Throws<ImageFormatException>();
    }

    private static uint EnumAt(byte[] data, long position)
    {
        var reader = new JxlBitReader(new JxlCodestream(data));
        reader.SkipBits((ulong)position);
        return JxlFields.Enum(ref reader, ~0UL, "test");
    }

    private static float F16Of(ulong bits)
    {
        var reader = new JxlBitReader(new JxlCodestream(new BitWriter().Bits(bits, 16).ToArray()));
        return JxlFields.F16(ref reader);
    }

    [Test]
    public async Task U64_ReadsAllSixtyFourBits()
    {
        var writer = new BitWriter().Bits(3, 2).Bits(0xFFF, 12);
        for (int shift = 12; shift < 60; shift += 8)
        {
            writer.Bool(true).Bits(0xFF, 8);
        }

        byte[] data = writer.Bool(true).Bits(0xF, 4).ToArray();
        var reader = new JxlBitReader(new JxlCodestream(data));
        await Assert.That(JxlFields.U64(ref reader)).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task Extensions_AreSkipped_AndOverflowIsCorrupt()
    {
        byte[] data = new BitWriter().U64(0b101).U64(3).U64(16).Bits(0x7FFFF, 19).Bits(0x5A, 8).ToArray();
        await Assert.That(ExtensionsThenByte(data)).IsEqualTo((0b101UL, 0x5Au));

        var overflow = new BitWriter().U64(0b11);
        foreach (int _ in new[] { 0, 1 })
        {
            overflow.Bits(3, 2).Bits(0xFFF, 12);
            for (int shift = 12; shift < 60; shift += 8)
            {
                overflow.Bool(true).Bits(0xFF, 8);
            }

            overflow.Bool(true).Bits(0xF, 4);
        }

        await Assert.That(() => ExtensionsThenByte(overflow.ToArray())).Throws<ImageFormatException>();
    }

    private static (ulong, uint) ExtensionsThenByte(byte[] data)
    {
        var reader = new JxlBitReader(new JxlCodestream(data));
        ulong mask = JxlFields.SkipExtensions(ref reader);
        return (mask, reader.ReadBits(8));
    }
}
