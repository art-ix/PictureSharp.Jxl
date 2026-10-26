using System.Buffers.Binary;
using System.Text;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>LSB-first bit writer for hand-built codestream headers.</summary>
internal sealed class BitWriter
{
    private readonly List<bool> bits = [];

    public int Count => bits.Count;

    public BitWriter Bits(ulong value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            bits.Add(((value >> i) & 1) != 0);
        }

        return this;
    }

    public BitWriter Bool(bool value) => Bits(value ? 1UL : 0, 1);

    /// <summary>A U32 field: the selector followed by <paramref name="extraBits"/> bits of <paramref name="extra"/>.</summary>
    public BitWriter U32(int selector, ulong extra = 0, int extraBits = 0) => Bits((ulong)selector, 2).Bits(extra, extraBits);

    /// <summary>An Enum field holding <paramref name="value"/> (0, 1, 2..17 or 18..81).</summary>
    public BitWriter Enum(uint value) => value switch
    {
        0 => U32(0),
        1 => U32(1),
        < 18 => U32(2, value - 2, 4),
        _ => U32(3, value - 18, 6)
    };

    /// <summary>A U64 field for values up to 4095 or 0/1–16/17–272.</summary>
    public BitWriter U64(ulong value) => value switch
    {
        0 => Bits(0, 2),
        <= 16 => Bits(1, 2).Bits(value - 1, 4),
        <= 272 => Bits(2, 2).Bits(value - 17, 8),
        _ => Bits(3, 2).Bits(value & 0xFFF, 12).Bool(false)
    };

    public BitWriter F16(Half value) => Bits(BitConverter.HalfToUInt16Bits(value), 16);

    /// <summary>Pads to a byte boundary with zero bits.</summary>
    public byte[] ToArray()
    {
        byte[] bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return bytes;
    }
}

internal static class JxlTestData
{
    public static readonly byte[] SignatureBox = [0, 0, 0, 12, 0x4A, 0x58, 0x4C, 0x20, 13, 10, 0x87, 10];

    /// <summary>The signature and a small 8×8 size header.</summary>
    public static BitWriter Start(int smallHeightDiv8Minus1 = 0, int ratio = 1) =>
        new BitWriter().Bits(0x0AFF, 16).Bool(true).Bits((ulong)smallHeightDiv8Minus1, 5).Bits((ulong)ratio, 3);

    /// <summary>A complete minimal codestream: 8×8, default metadata and transform data.</summary>
    public static byte[] Minimal() => Start().Bool(true).Bool(true).ToArray();

    public static byte[] Box(string type, params byte[] payload)
    {
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.Latin1.GetBytes(type, box.AsSpan(4));
        payload.CopyTo(box, 8);
        return box;
    }

    public static byte[] FileType(uint version = 0) => Box("ftyp", [.. "jxl "u8, 0, 0, 0, (byte)version, .. "jxl "u8]);

    public static byte[] Partial(uint counter, bool last, ReadOnlySpan<byte> payload)
    {
        byte[] content = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(content, counter | (last ? 0x8000_0000 : 0));
        payload.CopyTo(content.AsSpan(4));
        return Box("jxlp", content);
    }

    public static byte[] Boxed(params byte[][] boxes) => [.. SignatureBox, .. boxes.SelectMany(box => box)];

    public static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    public static DecoderOptions Options(DecoderOptions? options = null) =>
        (options ?? new DecoderOptions()) with { Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build() };
}
