using System.Buffers.Binary;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Codestream;

/// <summary>
/// Least-significant-bit-first reader over a codestream that may span several slices (ISO/IEC 18181-1 clause 4).
/// Scalar port of the reading side of libjxl v0.12.0 lib/jxl/dec_bit_reader.h (BSD-3-Clause, see
/// THIRD-PARTY-NOTICES.txt). Unlike libjxl it does not read zeros past the end: every read beyond the codestream
/// throws <see cref="ImageFormatException"/>.
/// </summary>
internal ref struct JxlBitReader
{
    private readonly ReadOnlyMemory<byte>[] segments;
    private readonly long totalBits;
    private int segment;
    private ReadOnlySpan<byte> current;
    private int offset;
    private ulong buffer;
    private int bufferedBits;

    public JxlBitReader(JxlCodestream codestream)
    {
        segments = codestream.Segments;
        totalBits = codestream.Length * 8;
        current = segments.Length > 0 ? segments[0].Span : default;
    }

    /// <summary>Number of bits consumed from the start of the codestream.</summary>
    public long BitPosition { get; private set; }

    /// <summary>Bits left before the end of the codestream.</summary>
    public readonly long RemainingBits => totalBits - BitPosition;

    /// <summary>Reads <paramref name="count"/> (0–32) bits.</summary>
    public uint ReadBits(int count)
    {
        if ((uint)count > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (bufferedBits < count)
        {
            Refill();
            if (bufferedBits < count)
            {
                throw Truncated();
            }
        }

        uint value = (uint)(buffer & ((1UL << count) - 1));
        buffer >>= count;
        bufferedBits -= count;
        BitPosition += count;
        return value;
    }

    public bool ReadBool() => ReadBits(1) != 0;

    /// <summary>
    /// Returns the next <paramref name="count"/> (0–32) bits without consuming them; bits past the end read as zero,
    /// as in libjxl. Table-driven decoders peek a fixed width and then <see cref="Consume"/> what the code used.
    /// </summary>
    public uint PeekBits(int count)
    {
        if (bufferedBits < count)
        {
            Refill();
        }

        return (uint)(buffer & ((1UL << count) - 1));
    }

    /// <summary>Consumes bits returned by <see cref="PeekBits"/>; failing when they lie past the end.</summary>
    public void Consume(int count)
    {
        if (bufferedBits < count)
        {
            throw Truncated();
        }

        buffer >>= count;
        bufferedBits -= count;
        BitPosition += count;
    }

    /// <summary>Skips <paramref name="count"/> bits, failing when fewer remain.</summary>
    public void SkipBits(ulong count)
    {
        if (count > (ulong)RemainingBits)
        {
            throw Truncated();
        }

        long bits = (long)count;
        int fromBuffer = (int)Math.Min(bits, bufferedBits);
        buffer = fromBuffer == 64 ? 0 : buffer >> fromBuffer;
        bufferedBits -= fromBuffer;
        BitPosition += fromBuffer;
        bits -= fromBuffer;

        // The buffer is empty now; skip whole bytes in the slices, then read the leftover bits.
        long bytes = bits / 8;
        BitPosition += bytes * 8;
        while (bytes > 0)
        {
            int available = current.Length - offset;
            if (available == 0)
            {
                NextSegment();
                continue;
            }

            int step = (int)Math.Min(bytes, available);
            offset += step;
            bytes -= step;
        }

        ReadBits((int)(bits % 8));
    }

    /// <summary>Moves to the next byte boundary; the padding bits must be zero.</summary>
    public void JumpToByteBoundary()
    {
        int padding = (int)((8 - BitPosition % 8) % 8);
        if (ReadBits(padding) != 0)
        {
            throw JxlDescriptor.Corrupt("Non-zero padding bits in the codestream.");
        }
    }

    private void Refill()
    {
        ReadOnlySpan<byte> rest = current[offset..];
        if (rest.Length >= 8)
        {
            // Take whole bytes only, so the buffer never holds part of a byte the next refill would add again.
            int bytes = (63 - bufferedBits) >> 3;
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(rest) & ((1UL << (bytes * 8)) - 1);
            buffer |= word << bufferedBits;
            bufferedBits += bytes * 8;
            offset += bytes;
            return;
        }

        while (bufferedBits <= 56)
        {
            if (offset == current.Length)
            {
                if (segment + 1 >= segments.Length)
                {
                    return;
                }

                NextSegment();
                continue;
            }

            buffer |= (ulong)current[offset++] << bufferedBits;
            bufferedBits += 8;
        }
    }

    private void NextSegment()
    {
        segment++;
        current = segments[segment].Span;
        offset = 0;
    }

    private static ImageFormatException Truncated() => JxlDescriptor.Corrupt("Truncated codestream.");
}
