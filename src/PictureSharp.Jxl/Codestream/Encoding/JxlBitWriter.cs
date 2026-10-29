using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.IO;
using System.Buffers.Binary;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// Least-significant-bit-first writer, the counterpart of <see cref="IO.JxlBitReader"/> (ISO/IEC 18181-1 clause 4).
/// Modelled on libjxl v0.12.0 lib/jxl/enc_bit_writer.cc (BSD-3-Clause).
/// </summary>
internal sealed class JxlBitWriter
{
    private byte[] bytes;
    private JxlPooledBuffer<byte>? rental;
    private readonly bool segmented;
    private readonly List<ReadOnlyMemory<byte>> segments = [];
    private long precedingBytes;
    internal JxlBufferScope? Buffers { get; }
    public JxlBitWriter(JxlBufferScope? buffers = null, bool segmented = false)
    {
        Buffers = buffers;
        this.segmented = segmented;
        bytes = buffers is null ? new byte[256] : (rental = buffers.Lease<byte>(256, clear: false)).Array;
    }
    private void CommitBytes()
    {
        if (length == 0) return;
        segments.Add(bytes.AsMemory(0, length));
        precedingBytes += length;
        length = 0;
        // Committed storage remains owned by the scope until output completes.
        rental = null;
        bytes = Buffers is null ? new byte[256] : (rental = Buffers.Lease<byte>(256, clear: false)).Array;
    }
    public void WriteSection(JxlBitWriter section)
    {
        section.ZeroPadToByte();
        if (!segmented) { WriteBytes(section.bytes.AsSpan(0, section.length)); return; }
        if (bufferedBits != 0) throw new InvalidOperationException("Section boundary is not byte aligned.");
        CommitBytes();
        segments.Add(section.bytes.AsMemory(0, section.length));
        precedingBytes += section.length;
    }
    public JxlCodestream AsCodestream()
    {
        ZeroPadToByte();
        return new JxlCodestream([.. segments, bytes.AsMemory(0, length)]);
    }
    private ulong buffer;
    private int bufferedBits;
    private int length;

    /// <summary>The number of bits written.</summary>
    public long BitLength => (precedingBytes + length) * 8L + bufferedBits;

    /// <summary>Writes the low <paramref name="count"/> (0–56) bits of <paramref name="value"/>.</summary>
    public void Write(int count, ulong value)
    {
        if ((uint)count > 56) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;
        buffer |= (value & ((1UL << count) - 1)) << bufferedBits;
        bufferedBits += count;
        if (bufferedBits >= 8) Flush();
    }

    public void Write(bool value) => Write(1, value ? 1UL : 0UL);

    /// <summary>Pads with zero bits to the next byte boundary.</summary>
    public void ZeroPadToByte()
    {
        if (bufferedBits > 0) Write(8 - bufferedBits, 0);
    }

    /// <summary>Appends whole bytes; the writer must be at a byte boundary.</summary>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        if (bufferedBits != 0) throw new InvalidOperationException("The writer is not at a byte boundary.");
        Reserve(data.Length);
        data.CopyTo(bytes.AsSpan(length));
        length += data.Length;
    }

    /// <summary>The low bits of a <see cref="WritePacked"/> entry that hold its bit count.</summary>
    public const int PackedCountBits = 6;

    /// <summary>
    /// Writes every entry of <paramref name="entries"/>: the bit count (0–56) in the low <see cref="PackedCountBits"/> bits,
    /// the bits above them. The same as calling <see cref="Write(int, ulong)"/> for each, with one capacity check.
    /// </summary>
    public void WritePacked(ReadOnlySpan<ulong> entries)
    {
        const int Batch = 1 << 20;
        for (; entries.Length > Batch; entries = entries[Batch..]) WritePackedBatch(entries[..Batch]);
        WritePackedBatch(entries);
    }

    private void WritePackedBatch(ReadOnlySpan<ulong> entries)
    {
        // The exact length, plus the 8-byte store of the last flush: a worst-case reservation (7 bytes per entry) would
        // grow retained section buffers to several times their content.
        long total = bufferedBits;
        foreach (ulong entry in entries) total += (int)(entry & ((1 << PackedCountBits) - 1));
        Reserve(checked((int)(total >> 3) + sizeof(ulong)));
        Span<byte> output = bytes;
        ulong bits = buffer;
        int count = bufferedBits;
        int position = length;
        foreach (ulong entry in entries)
        {
            int n = (int)(entry & ((1 << PackedCountBits) - 1));
            if (n > 56) throw new ArgumentOutOfRangeException(nameof(entries));
            bits |= (entry >> PackedCountBits & ((1UL << n) - 1)) << count;
            count += n;
            if (count >= 8)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(output[position..], bits);
                int whole = count >> 3;
                position += whole;
                bits = whole == 8 ? 0 : bits >> (whole * 8);
                count &= 7;
            }
        }

        (buffer, bufferedBits, length) = (bits, count, position);
    }

    /// <summary>Appends all bits of <paramref name="other"/>, which need not end at a byte boundary.</summary>
    public void Append(JxlBitWriter other)
    {
        if (bufferedBits == 0)
        {
            WriteBytes(other.bytes.AsSpan(0, other.length));
        }
        else
        {
            foreach (byte value in other.bytes.AsSpan(0, other.length)) Write(8, value);
        }

        Write(other.bufferedBits, other.buffer);
    }

    /// <summary>The bytes written, padded with zeros to a byte boundary.</summary>
    public byte[] ToArray()
    {
        byte[] result = new byte[checked((int)(precedingBytes + length + (bufferedBits > 0 ? 1 : 0)))];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in segments) { segment.Span.CopyTo(result.AsSpan(offset)); offset += segment.Length; }
        bytes.AsSpan(0, length).CopyTo(result.AsSpan(offset));
        if (bufferedBits > 0) result[offset + length] = (byte)buffer;
        return result;
    }

    // Moves the whole bytes of the buffer (at most 7, as it holds at most 63 bits) to the array with one store; the
    // bytes past them are overwritten by later writes.
    private void Flush()
    {
        int whole = bufferedBits >> 3;
        Reserve(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(length), buffer);
        length += whole;
        buffer >>= whole * 8;
        bufferedBits &= 7;
    }

    private void Reserve(int count)
    {
        if (length + (long)count <= bytes.Length) return;
        long size = Math.Max((long)bytes.Length * 2, length + (long)count);
        if (size > Array.MaxLength)
        {
            size = length + (long)count <= Array.MaxLength
                ? Array.MaxLength
                : throw JxlDescriptor.Unsupported("the encoded image exceeds the largest array.");
        }

        if (Buffers is null) Array.Resize(ref bytes, (int)size);
        else
        {
            var next = Buffers.Lease<byte>((int)size, clear: false);
            bytes.AsSpan(0, length).CopyTo(next.Span);
            rental?.Dispose();
            rental = next;
            bytes = next.Array;
        }
    }
}
