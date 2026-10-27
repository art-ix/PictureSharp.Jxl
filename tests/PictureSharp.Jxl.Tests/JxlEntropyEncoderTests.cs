using PictureSharp.Formats.Jxl.Codestream.Encoding;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.IO;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>The encoder's bit, field and entropy writers against the decoder's readers.</summary>
public sealed class JxlEntropyEncoderTests
{
    [Test]
    public async Task Fields_RoundTripThroughTheReader()
    {
        var writer = new JxlBitWriter();
        uint[] u32 = [0, 1, 2, 17, 18, 273, 1000, 4211711, 4211712, (1u << 30) - 1];
        ulong[] u64 = [0, 1, 16, 17, 272, 273, 4095, 4096, 1UL << 40, ulong.MaxValue];
        foreach (uint value in u32) JxlFieldWriter.U32(writer, value, Val(0), BitsOffset(4, 1), BitsOffset(8, 17), Bits32(30));
        foreach (ulong value in u64) JxlFieldWriter.U64(writer, value);
        JxlFieldWriter.F16(writer, 0.5f);
        JxlFieldWriter.Name(writer, "alpha");
        writer.Write(7, 0x55);
        (uint[] u32Read, ulong[] u64Read, float half, string name, uint tail) = ReadFields(writer.ToArray(), u32.Length, u64.Length);
        await Assert.That(u32Read).IsEquivalentTo(u32);
        await Assert.That(u64Read).IsEquivalentTo(u64);
        await Assert.That(half).IsEqualTo(0.5f);
        await Assert.That(name).IsEqualTo("alpha");
        await Assert.That(tail).IsEqualTo(0x55u);
    }

    private static (uint[], ulong[], float, string, uint) ReadFields(byte[] data, int u32Count, int u64Count)
    {
        var reader = new JxlBitReader(new JxlCodestream(data));
        uint[] u32 = new uint[u32Count];
        for (int i = 0; i < u32.Length; i++) u32[i] = JxlFields.U32(ref reader, Val(0), BitsOffset(4, 1), BitsOffset(8, 17), Bits32(30));
        ulong[] u64 = new ulong[u64Count];
        for (int i = 0; i < u64.Length; i++) u64[i] = JxlFields.U64(ref reader);
        return (u32, u64, JxlFields.F16(ref reader), JxlFields.Name(ref reader), reader.ReadBits(7));
    }

    [Test]
    [Arguments(1, 1, 0)]
    [Arguments(1, 2000, 1)]
    [Arguments(3, 5000, 2)]
    [Arguments(40, 20000, 3)]
    [Arguments(300, 50000, 4)]
    [Arguments(6, 30000, 5)]
    public async Task Streams_DecodeWithTheDecoder(int contexts, int count, int seed)
    {
        var random = new Random(seed);
        var streams = new List<JxlTokens>();
        for (int s = 0; s < 3; s++)
        {
            var tokens = new JxlTokens();
            for (int i = 0; i < count / 3 + s; i++)
            {
                int context = random.Next(contexts);
                // Context-dependent distributions: geometric, constant, wide and occasionally huge values.
                uint value = (context % 4) switch
                {
                    0 => (uint)Math.Min(random.Next(1, 1 << 12) / random.Next(1, 300), 4000),
                    1 => (uint)(context * 3),
                    2 => (uint)random.Next(0, 70000),
                    _ => random.Next(100) == 0 ? uint.MaxValue - (uint)random.Next(5) : (uint)random.Next(3),
                };
                tokens.Add(context, value);
            }

            streams.Add(tokens);
        }

        JxlEntropyEncoder encoder = JxlEntropyEncoder.Build(contexts, streams);
        var writer = new JxlBitWriter();
        encoder.WriteHeader(writer);
        foreach (JxlTokens tokens in streams) encoder.WriteStream(writer, tokens);
        writer.Write(16, 0xBEEF);

        await Assert.That(Decode(writer.ToArray(), contexts, streams)).IsEqualTo("ok");
    }

    internal static string Decode(byte[] data, int contexts, List<JxlTokens> streams)
    {
        var reader = new JxlBitReader(new JxlCodestream(data));
        JxlEntropyCode code = JxlEntropyCode.Read(ref reader, contexts);
        for (int s = 0; s < streams.Count; s++)
        {
            JxlTokens tokens = streams[s];
            using var symbols = new JxlSymbolReader(code, ref reader);
            for (int i = 0; i < tokens.Count; i++)
            {
                uint decoded = symbols.Read(tokens.Contexts[i], ref reader);
                if (decoded != tokens.Values[i]) return $"stream {s} token {i}: {decoded} instead of {tokens.Values[i]}";
            }

            if (!symbols.IsFinalStateValid) return $"stream {s}: invalid final state";
        }

        return reader.ReadBits(16) == 0xBEEF ? "ok" : "trailing bits differ";
    }

    [Test]
    public async Task SkewedHistograms_UseFewBits()
    {
        var tokens = new JxlTokens();
        for (int i = 0; i < 100000; i++) tokens.Add(0, i % 1000 == 0 ? 1u : 0u);
        var writer = new JxlBitWriter();
        JxlEntropyEncoder.WriteStandalone(writer, 1, tokens);
        // About 0.0114 bits per symbol; allow the ANS precision loss and the header.
        await Assert.That(writer.BitLength).IsLessThan(2000);
    }
}
