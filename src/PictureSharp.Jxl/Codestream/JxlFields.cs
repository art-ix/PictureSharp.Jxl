namespace PictureSharp.Formats.Jxl.Codestream;

/// <summary>One of the four U32 distributions: a constant, or <c>offset + Bits(bits)</c>.</summary>
internal readonly record struct JxlU32(int Bits, uint Offset)
{
    public static JxlU32 Val(uint value) => new(0, value);

    public static JxlU32 BitsOffset(int bits, uint offset) => new(bits, offset);

    public static JxlU32 Bits32(int bits) => new(bits, 0);
}

/// <summary>
/// Field encodings of ISO/IEC 18181-1 Annex A: U32, U64, F16, Enum and extensions. Ported from libjxl v0.12.0
/// lib/jxl/fields.cc (U32Coder, U64Coder, F16Coder, ReadVisitor::BeginExtensions/EndExtensions), BSD-3-Clause.
/// </summary>
internal static class JxlFields
{
    private static readonly JxlU32[] EnumDistribution =
        [JxlU32.Val(0), JxlU32.Val(1), JxlU32.BitsOffset(4, 2), JxlU32.BitsOffset(6, 18)];

    public static uint U32(ref JxlBitReader reader, JxlU32 d0, JxlU32 d1, JxlU32 d2, JxlU32 d3)
    {
        JxlU32 distribution = reader.ReadBits(2) switch
        {
            0 => d0,
            1 => d1,
            2 => d2,
            _ => d3
        };

        // The offsets of valid encodings never overflow; wrap like libjxl's uint32 arithmetic otherwise.
        return unchecked(distribution.Offset + reader.ReadBits(distribution.Bits));
    }

    public static ulong U64(ref JxlBitReader reader)
    {
        switch (reader.ReadBits(2))
        {
            case 0:
                return 0;
            case 1:
                return 1 + reader.ReadBits(4);
            case 2:
                return 17 + reader.ReadBits(8);
        }

        ulong result = reader.ReadBits(12);
        int shift = 12;
        while (reader.ReadBool())
        {
            if (shift == 60)
            {
                result |= (ulong)reader.ReadBits(4) << shift;
                break;
            }

            result |= (ulong)reader.ReadBits(8) << shift;
            shift += 8;
        }

        return result;
    }

    /// <summary>An IEEE 754 binary16 value; infinities and NaN are invalid.</summary>
    public static float F16(ref JxlBitReader reader)
    {
        uint bits = reader.ReadBits(16);
        if (((bits >> 10) & 0x1F) == 31)
        {
            throw JxlDescriptor.Corrupt("F16 infinity or NaN in a header.");
        }

        return (float)BitConverter.UInt16BitsToHalf((ushort)bits);
    }

    /// <summary>An enumerator; <paramref name="valid"/> has bit <c>v</c> set for every defined value <c>v</c>.</summary>
    public static uint Enum(ref JxlBitReader reader, ulong valid, string name)
    {
        uint value = U32(ref reader, EnumDistribution[0], EnumDistribution[1], EnumDistribution[2], EnumDistribution[3]);
        if (value >= 64 || (valid & (1UL << (int)value)) == 0)
        {
            throw JxlDescriptor.Corrupt($"Invalid {name} value {value}.");
        }

        return value;
    }

    /// <summary>
    /// Reads the extension mask and sizes of a bundle and skips every extension, none of which this decoder knows.
    /// Must be called after the last field of the bundle. Returns the mask.
    /// </summary>
    public static ulong SkipExtensions(ref JxlBitReader reader)
    {
        ulong extensions = U64(ref reader);
        ulong total = 0;
        for (ulong remaining = extensions; remaining != 0; remaining &= remaining - 1)
        {
            ulong bits = U64(ref reader);
            if (bits > ulong.MaxValue - total)
            {
                throw JxlDescriptor.Corrupt("Extension sizes overflow.");
            }

            total += bits;
        }

        reader.SkipBits(total);
        return extensions;
    }

    /// <summary>Name strings of extra channels and frames: up to 1071 UTF-8 bytes.</summary>
    public static string Name(ref JxlBitReader reader)
    {
        int length = (int)U32(ref reader, JxlU32.Val(0), JxlU32.Bits32(4), JxlU32.BitsOffset(5, 16), JxlU32.BitsOffset(10, 48));
        if (length == 0)
        {
            return "";
        }

        if (length * 8L > reader.RemainingBits)
        {
            throw JxlDescriptor.Corrupt("Truncated codestream.");
        }

        Span<byte> bytes = stackalloc byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)reader.ReadBits(8);
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Bundle bit masks for <see cref="Enum"/>.</summary>
    public static ulong Bits(params ReadOnlySpan<uint> values)
    {
        ulong mask = 0;
        foreach (uint value in values)
        {
            mask |= 1UL << (int)value;
        }

        return mask;
    }
}
