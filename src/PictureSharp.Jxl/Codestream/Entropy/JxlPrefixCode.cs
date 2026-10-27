using PictureSharp.Formats.Jxl.Codestream.IO;

namespace PictureSharp.Formats.Jxl.Codestream.Entropy;

/// <summary>A lookup entry: the code length (or second-level table width plus 8) and the symbol (or table offset).</summary>
internal readonly record struct JxlHuffmanEntry(byte Bits, ushort Value);

/// <summary>
/// A Brotli-style prefix code with an 8-bit root table and second-level tables. Ported from libjxl v0.12.0
/// lib/jxl/dec_huffman.cc and lib/jxl/huffman_table.cc (BSD-3-Clause).
/// </summary>
internal sealed class JxlPrefixCode
{
    public const int RootBits = 8;
    private const int MaxBits = 15;
    private const int CodeLengthCodes = 18;
    private const byte DefaultCodeLength = 8;
    private const byte RepeatCode = 16;

    private static ReadOnlySpan<byte> CodeLengthOrder => [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    // The static code for the code length code lengths: (bits, value) indexed by 4 peeked bits.
    private static readonly JxlHuffmanEntry[] CodeLengthLengths =
    [
        new(2, 0), new(2, 4), new(2, 3), new(3, 2), new(2, 0), new(2, 4), new(2, 3), new(4, 1),
        new(2, 0), new(2, 4), new(2, 3), new(3, 2), new(2, 0), new(2, 4), new(2, 3), new(4, 5),
    ];

    private readonly JxlHuffmanEntry[] table;

    private JxlPrefixCode(JxlHuffmanEntry[] table) => this.table = table;

    /// <summary>The code of a one-symbol alphabet: symbol 0 with no bits.</summary>
    public static JxlPrefixCode Zero { get; } = new(new JxlHuffmanEntry[1 << RootBits]);

    public uint ReadSymbol(ref JxlBitReader reader)
    {
        int index = (int)reader.PeekBits(RootBits);
        JxlHuffmanEntry entry = table[index];
        if (entry.Bits > RootBits)
        {
            reader.Consume(RootBits);
            index += entry.Value + (int)reader.PeekBits(entry.Bits - RootBits);
            entry = table[index];
        }

        reader.Consume(entry.Bits);
        return entry.Value;
    }

    public static JxlPrefixCode Read(ref JxlBitReader reader, int alphabetSize)
    {
        // 1: simple code; 0: no skipping; 2 or 3: skip that many code length code lengths.
        uint simpleOrSkip = reader.ReadBits(2);
        if (simpleOrSkip == 1)
        {
            return new JxlPrefixCode(ReadSimpleCode(ref reader, alphabetSize));
        }

        Span<byte> codeLengthLengths = stackalloc byte[CodeLengthCodes];
        int space = 32;
        int codes = 0;
        for (int i = (int)simpleOrSkip; i < CodeLengthCodes && space > 0; i++)
        {
            JxlHuffmanEntry entry = CodeLengthLengths[reader.PeekBits(4)];
            reader.Consume(entry.Bits);
            codeLengthLengths[CodeLengthOrder[i]] = (byte)entry.Value;
            if (entry.Value != 0)
            {
                space -= 32 >> entry.Value;
                codes++;
            }
        }

        byte[] lengths = new byte[alphabetSize];
        if ((codes != 1 && space != 0) || !ReadCodeLengths(codeLengthLengths, lengths, ref reader))
        {
            throw Invalid();
        }

        Span<ushort> counts = stackalloc ushort[16];
        foreach (byte length in lengths)
        {
            counts[length]++;
        }

        var result = new JxlHuffmanEntry[alphabetSize + 376];
        int size = Build(result, RootBits, lengths, counts);
        return size > 0 ? new JxlPrefixCode(result[..size]) : throw Invalid();
    }

    private static bool ReadCodeLengths(scoped ReadOnlySpan<byte> codeLengthLengths, scoped Span<byte> lengths, ref JxlBitReader reader)
    {
        Span<ushort> counts = stackalloc ushort[16];
        foreach (byte length in codeLengthLengths)
        {
            counts[length]++;
        }

        var lengthTable = new JxlHuffmanEntry[32];
        if (Build(lengthTable, 5, codeLengthLengths, counts) == 0)
        {
            return false;
        }

        int symbol = 0;
        byte previous = DefaultCodeLength;
        int repeat = 0;
        byte repeatLength = 0;
        int space = 32768;
        while (symbol < lengths.Length && space > 0)
        {
            JxlHuffmanEntry entry = lengthTable[reader.PeekBits(5)];
            reader.Consume(entry.Bits);
            byte length = (byte)entry.Value;
            if (length < RepeatCode)
            {
                repeat = 0;
                lengths[symbol++] = length;
                if (length != 0)
                {
                    previous = length;
                    space -= 32768 >> length;
                }

                continue;
            }

            int extraBits = length - 14;
            byte newLength = length == RepeatCode ? previous : (byte)0;
            if (repeatLength != newLength)
            {
                repeat = 0;
                repeatLength = newLength;
            }

            int oldRepeat = repeat;
            if (repeat > 0)
            {
                repeat = (repeat - 2) << extraBits;
            }

            repeat += (int)reader.ReadBits(extraBits) + 3;
            int delta = repeat - oldRepeat;
            if (symbol + delta > lengths.Length)
            {
                return false;
            }

            lengths.Slice(symbol, delta).Fill(repeatLength);
            symbol += delta;
            if (repeatLength != 0)
            {
                space -= delta << (15 - repeatLength);
            }
        }

        lengths[symbol..].Clear();
        return space == 0;
    }

    private static JxlHuffmanEntry[] ReadSimpleCode(ref JxlBitReader reader, int alphabetSize)
    {
        int maxBits = alphabetSize > 1 ? Log2Floor((uint)alphabetSize - 1) + 1 : 0;
        int count = (int)reader.ReadBits(2) + 1;
        Span<ushort> symbols = stackalloc ushort[4];
        for (int i = 0; i < count; i++)
        {
            symbols[i] = (ushort)reader.ReadBits(maxBits);
            if (symbols[i] >= alphabetSize)
            {
                throw Invalid();
            }
        }

        for (int i = 0; i < count - 1; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                if (symbols[i] == symbols[j])
                {
                    throw Invalid();
                }
            }
        }

        // Four symbols have two shapes: lengths 2,2,2,2 or 1,2,3,3.
        if (count == 4)
        {
            count += (int)reader.ReadBits(1);
        }

        var table = new JxlHuffmanEntry[1 << RootBits];
        int size;
        switch (count)
        {
            case 1:
                table[0] = new(0, symbols[0]);
                size = 1;
                break;
            case 2:
                SortPair(symbols, 0, 1);
                table[0] = new(1, symbols[0]);
                table[1] = new(1, symbols[1]);
                size = 2;
                break;
            case 3:
                SortPair(symbols, 1, 2);
                table[0] = new(1, symbols[0]);
                table[2] = new(1, symbols[0]);
                table[1] = new(2, symbols[1]);
                table[3] = new(2, symbols[2]);
                size = 4;
                break;
            case 4:
                for (int i = 0; i < 3; i++)
                {
                    for (int j = i + 1; j < 4; j++)
                    {
                        SortPair(symbols, i, j);
                    }
                }

                table[0] = new(2, symbols[0]);
                table[2] = new(2, symbols[1]);
                table[1] = new(2, symbols[2]);
                table[3] = new(2, symbols[3]);
                size = 4;
                break;
            default:
                SortPair(symbols, 2, 3);
                table[0] = new(1, symbols[0]);
                table[1] = new(2, symbols[1]);
                table[2] = new(1, symbols[0]);
                table[3] = new(3, symbols[2]);
                table[4] = new(1, symbols[0]);
                table[5] = new(2, symbols[1]);
                table[6] = new(1, symbols[0]);
                table[7] = new(3, symbols[3]);
                size = 8;
                break;
        }

        for (; size < table.Length; size <<= 1)
        {
            table.AsSpan(0, size).CopyTo(table.AsSpan(size));
        }

        return table;
    }

    private static void SortPair(Span<ushort> symbols, int i, int j)
    {
        if (symbols[i] > symbols[j])
        {
            (symbols[i], symbols[j]) = (symbols[j], symbols[i]);
        }
    }

    /// <summary>Builds the two-level table; returns its size, or 0 when the lengths do not form a valid code.</summary>
    private static int Build(JxlHuffmanEntry[] root, int rootBits, ReadOnlySpan<byte> lengths, Span<ushort> count)
    {
        Span<ushort> offset = stackalloc ushort[MaxBits + 1];
        int maxLength = 1;
        ushort sum = 0;
        for (int len = 1; len <= MaxBits; len++)
        {
            offset[len] = sum;
            if (count[len] != 0)
            {
                sum += count[len];
                maxLength = len;
            }
        }

        ushort[] sorted = new ushort[lengths.Length];
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            if (lengths[symbol] != 0)
            {
                sorted[offset[lengths[symbol]]++] = (ushort)symbol;
            }
        }

        int tableBits = rootBits;
        int tableSize = 1 << tableBits;
        int totalSize = tableSize;

        // A code with one symbol uses no bits.
        if (offset[MaxBits] == 1)
        {
            root.AsSpan(0, totalSize).Fill(new JxlHuffmanEntry(0, sorted[0]));
            return totalSize;
        }

        if (tableBits > maxLength)
        {
            tableBits = maxLength;
            tableSize = 1 << tableBits;
        }

        int key = 0;
        int next = 0;
        int step = 2;
        for (int bits = 1; bits <= tableBits; bits++, step <<= 1)
        {
            for (; count[bits] != 0; count[bits]--)
            {
                Replicate(root, key, step, tableSize, new JxlHuffmanEntry((byte)bits, sorted[next++]));
                key = NextKey(key, bits);
            }
        }

        for (; totalSize != tableSize; tableSize <<= 1)
        {
            root.AsSpan(0, tableSize).CopyTo(root.AsSpan(tableSize));
        }

        // Second-level tables follow the root; each root entry points at one with its width and relative offset.
        int mask = totalSize - 1;
        int low = -1;
        int table = 0;
        step = 2;
        for (int len = rootBits + 1; len <= maxLength; len++, step <<= 1)
        {
            for (; count[len] != 0; count[len]--)
            {
                if ((key & mask) != low)
                {
                    table += tableSize;
                    tableBits = NextTableBits(count, len, rootBits);
                    tableSize = 1 << tableBits;
                    totalSize += tableSize;
                    if (table + tableSize > root.Length)
                    {
                        return 0;
                    }

                    low = key & mask;
                    root[low] = new JxlHuffmanEntry((byte)(tableBits + rootBits), (ushort)(table - low));
                }

                Replicate(root, table + (key >> rootBits), step, tableSize, new JxlHuffmanEntry((byte)(len - rootBits), sorted[next++]));
                key = NextKey(key, len);
            }
        }

        return totalSize;
    }

    /// <summary>reverse(reverse(key, len) + 1, len) for the low <paramref name="len"/> bits.</summary>
    private static int NextKey(int key, int len)
    {
        int step = 1 << (len - 1);
        while ((key & step) != 0)
        {
            step >>= 1;
        }

        return (key & (step - 1)) + step;
    }

    private static void Replicate(JxlHuffmanEntry[] table, int start, int step, int end, JxlHuffmanEntry entry)
    {
        do
        {
            end -= step;
            table[start + end] = entry;
        }
        while (end > 0);
    }

    private static int NextTableBits(ReadOnlySpan<ushort> count, int len, int rootBits)
    {
        int left = 1 << (len - rootBits);
        while (len < MaxBits)
        {
            if (left <= count[len])
            {
                break;
            }

            left -= count[len];
            len++;
            left <<= 1;
        }

        return len - rootBits;
    }

    internal static int Log2Floor(uint value) => 31 - System.Numerics.BitOperations.LeadingZeroCount(value);

    private static ImageFormatException Invalid() => JxlDescriptor.Corrupt("Invalid prefix code.");
}
