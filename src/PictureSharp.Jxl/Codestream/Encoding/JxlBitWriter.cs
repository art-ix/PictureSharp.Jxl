namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// Least-significant-bit-first writer, the counterpart of <see cref="IO.JxlBitReader"/> (ISO/IEC 18181-1 clause 4).
/// Modelled on libjxl v0.12.0 lib/jxl/enc_bit_writer.cc (BSD-3-Clause).
/// </summary>
internal sealed class JxlBitWriter
{
    private byte[] bytes = new byte[256];
    private ulong buffer;
    private int bufferedBits;
    private int length;

    /// <summary>The number of bits written.</summary>
    public long BitLength => length * 8L + bufferedBits;

    /// <summary>Writes the low <paramref name="count"/> (0–56) bits of <paramref name="value"/>.</summary>
    public void Write(int count, ulong value)
    {
        if ((uint)count > 56) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;
        buffer |= (value & ((1UL << count) - 1)) << bufferedBits;
        bufferedBits += count;
        while (bufferedBits >= 8)
        {
            Push((byte)buffer);
            buffer >>= 8;
            bufferedBits -= 8;
        }
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
        byte[] result = new byte[length + (bufferedBits > 0 ? 1 : 0)];
        bytes.AsSpan(0, length).CopyTo(result);
        if (bufferedBits > 0) result[length] = (byte)buffer;
        return result;
    }

    private void Push(byte value)
    {
        Reserve(1);
        bytes[length++] = value;
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

        Array.Resize(ref bytes, (int)size);
    }
}
