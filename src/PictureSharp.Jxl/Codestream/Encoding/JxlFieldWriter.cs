using PictureSharp.Formats.Jxl.Codestream.IO;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// Field encodings of ISO/IEC 18181-1 Annex A, the counterpart of <see cref="JxlFields"/>. Ported from libjxl v0.12.0
/// lib/jxl/fields.cc (U32Coder, U64Coder, F16Coder), BSD-3-Clause.
/// </summary>
internal static class JxlFieldWriter
{
    /// <summary>Writes <paramref name="value"/> with the shortest of the four distributions that can hold it.</summary>
    public static void U32(JxlBitWriter writer, uint value, JxlU32 d0, JxlU32 d1, JxlU32 d2, JxlU32 d3)
    {
        int best = -1;
        int bestBits = int.MaxValue;
        ReadOnlySpan<JxlU32> distributions = [d0, d1, d2, d3];
        for (int i = 0; i < distributions.Length; i++)
        {
            JxlU32 d = distributions[i];
            if (value >= d.Offset && (d.Bits == 32 || value - d.Offset < 1UL << d.Bits) && d.Bits < bestBits
                && (d.Bits > 0 || value == d.Offset))
            {
                best = i;
                bestBits = d.Bits;
            }
        }

        if (best < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "The value cannot be encoded.");
        writer.Write(2, (uint)best);
        writer.Write(distributions[best].Bits, value - distributions[best].Offset);
    }

    public static void U64(JxlBitWriter writer, ulong value)
    {
        if (value == 0)
        {
            writer.Write(2, 0);
        }
        else if (value <= 16)
        {
            writer.Write(2, 1);
            writer.Write(4, value - 1);
        }
        else if (value <= 272)
        {
            writer.Write(2, 2);
            writer.Write(8, value - 17);
        }
        else
        {
            writer.Write(2, 3);
            writer.Write(12, value & 0xFFF);
            value >>= 12;
            int shift = 12;
            while (value > 0 && shift < 60)
            {
                writer.Write(true);
                writer.Write(8, value & 0xFF);
                value >>= 8;
                shift += 8;
            }

            if (value > 0)
            {
                writer.Write(true);
                writer.Write(4, value & 0xF);
            }
            else
            {
                writer.Write(false);
            }
        }
    }

    public static void F16(JxlBitWriter writer, float value)
    {
        Half half = (Half)value;
        if (!Half.IsFinite(half)) throw new ArgumentOutOfRangeException(nameof(value), value, "Not a finite binary16 value.");
        writer.Write(16, BitConverter.HalfToUInt16Bits(half));
    }

    public static void Enum(JxlBitWriter writer, uint value) =>
        U32(writer, value, JxlU32.Val(0), JxlU32.Val(1), JxlU32.BitsOffset(4, 2), JxlU32.BitsOffset(6, 18));

    /// <summary>An extension mask of zero: no extensions.</summary>
    public static void NoExtensions(JxlBitWriter writer) => U64(writer, 0);

    /// <summary>A name string of up to 1071 UTF-8 bytes.</summary>
    public static void Name(JxlBitWriter writer, string name)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
        U32(writer, (uint)bytes.Length, JxlU32.Val(0), JxlU32.Bits32(4), JxlU32.BitsOffset(5, 16), JxlU32.BitsOffset(10, 48));
        foreach (byte b in bytes) writer.Write(8, b);
    }
}
