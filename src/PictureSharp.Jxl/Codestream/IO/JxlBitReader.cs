using System.Buffers.Binary;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>
/// Least-significant-bit-first reader over a codestream that may span several slices (ISO/IEC 18181-1 clause 4).
/// Scalar port of the reading side of libjxl v0.12.0 lib/jxl/dec_bit_reader.h (BSD-3-Clause, see
/// THIRD-PARTY-NOTICES.txt). Unlike libjxl it does not read zeros past the end: every read beyond the codestream
/// throws <see cref="ImageFormatException"/>.
/// </summary>
internal ref struct JxlBitReader
{
    private readonly ReadOnlySpan<ReadOnlyMemory<byte>> segments;
    private readonly long totalBits;
    private int segment;
    // Bytes in the segments before the current one; the bit position is derived from it on demand.
    private long segmentStart;
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
    public readonly long BitPosition => (segmentStart + offset) * 8 - bufferedBits;

    /// <summary>Bits left before the end of the codestream.</summary>
    public readonly long RemainingBits => totalBits - BitPosition;

    /// <summary>Reads <paramref name="count"/> (0–32) bits.</summary>
    public uint ReadBits(int count)
    {
        if ((uint)count > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

#if JXL_FUZZ_TRACE
        // The fuzzing tool records where header fields lie, to mutate them; it defines JxlFuzzTrace.
        JxlFuzzTrace.Read(BitPosition, count);
#endif
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
        bits -= fromBuffer;

        // The buffer is empty now; skip whole bytes in the slices, then read the leftover bits.
        long bytes = bits / 8;
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

    /// <summary>
    /// Fills <paramref name="values"/> with symbols of <paramref name="decoder"/>, which consumes at most
    /// <see cref="IJxlBitDecoder.MaxBits"/> (up to 56) bits per symbol, and returns how many it filled: fewer when the
    /// decoder ends the row at a symbol, which <paramref name="stop"/> returns. While 8 bytes remain in the current
    /// slice the buffer stays in locals and is refilled once per symbol, so the decoder reads it without checks;
    /// elsewhere each symbol takes the checked path, with the same results and failures.
    /// </summary>
    public int ReadRow<TDecoder>(ref TDecoder decoder, Span<uint> values, out uint stop) where TDecoder : struct, IJxlBitDecoder
    {
        int i = 0;
        stop = 0;
        while (i < values.Length)
        {
            ulong bits = buffer;
            int count = bufferedBits;
            int at = offset;
            ReadOnlySpan<byte> data = current;
            bool more = true;
            while (i < values.Length && data.Length - at >= 8)
            {
                if (count < TDecoder.MaxBits)
                {
                    int bytes = (63 - count) >> 3;
                    bits |= (BinaryPrimitives.ReadUInt64LittleEndian(data[at..]) & ((1UL << (bytes * 8)) - 1)) << count;
                    count += bytes * 8;
                    at += bytes;
                }

                if (!decoder.TryDecode(ref bits, ref count, out values[i]))
                {
                    more = false;
                    break;
                }

                i++;
            }

            buffer = bits;
            bufferedBits = count;
            offset = at;
            if (more && i < values.Length)
            {
                more = decoder.TryDecode(ref this, out values[i]);
                i += more ? 1 : 0;
            }

            if (!more)
            {
                stop = values[i];
                return i;
            }
        }

        return i;
    }

    /// <summary>
    /// Takes <paramref name="count"/> bits from a buffer of <see cref="ReadRow"/> that holds at least that many.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static uint Take(ref ulong bits, ref int buffered, int count)
    {
        uint value = (uint)(bits & ((1UL << count) - 1));
        bits >>= count;
        buffered -= count;
        return value;
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
        segmentStart += current.Length;
        segment++;
        current = segments[segment].Span;
        offset = 0;
    }

    /// <summary>Fails as truncated unless <paramref name="bits"/> more bits remain.</summary>
    public readonly void EnsureRemaining(long bits)
    {
        if (bits > RemainingBits)
        {
            throw Truncated();
        }
    }

    public static ImageFormatException Truncated() => JxlDescriptor.Corrupt("Truncated codestream.");
}

/// <summary>A symbol decoder for <see cref="JxlBitReader.ReadRow"/>.</summary>
internal interface IJxlBitDecoder
{
    /// <summary>The most bits one symbol consumes; at most 56.</summary>
    static abstract int MaxBits { get; }

    /// <summary>
    /// Decodes a symbol from a buffer holding at least <see cref="MaxBits"/> bits; false when the symbol ends the row
    /// (<paramref name="value"/> is then the symbol).
    /// </summary>
    bool TryDecode(ref ulong bits, ref int buffered, out uint value);

    /// <summary>The same with the reader's checked operations.</summary>
    bool TryDecode(ref JxlBitReader reader, out uint value);
}
