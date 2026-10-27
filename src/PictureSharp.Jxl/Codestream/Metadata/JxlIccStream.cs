using System.Buffers.Binary;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.IO;

namespace PictureSharp.Formats.Jxl.Codestream.Metadata;

/// <summary>
/// The entropy-coded ICC stream and its prediction (UnpredictICC), ported from libjxl v0.12.0 icc_codec.cc and
/// icc_codec_common.cc (BSD-3-Clause).
/// </summary>
internal static class JxlIccStream
{
    private const int IccContexts = 41;
    private const int HeaderSize = 128;
    private const int PreambleSize = 20;
    /// <summary>libjxl's limit for a decoded profile (CheckPreamble).</summary>
    public const long MaxProfileSize = 1 << 28;

    private const int CommandTagUnknown = 1;
    private const int CommandTagTrc = 2;
    private const int CommandTagXyz = 3;
    private const int CommandTagStringFirst = 4;
    private const int CommandInsert = 1;
    private const int CommandShuffle2 = 2;
    private const int CommandShuffle4 = 3;
    private const int CommandPredict = 4;
    private const int CommandXyz = 10;
    private const int CommandTypeStartFirst = 16;
    private const int FlagBitOffset = 64;
    private const int FlagBitSize = 128;

    // Tag names and types focused on RGB and gray monitor profiles, in libjxl's order.
    private static readonly uint[] TagStrings =
    [
        Tag("cprt"), Tag("wtpt"), Tag("bkpt"), Tag("rXYZ"), Tag("gXYZ"), Tag("bXYZ"), Tag("kXYZ"), Tag("rTRC"), Tag("gTRC"),
        Tag("bTRC"), Tag("kTRC"), Tag("chad"), Tag("desc"), Tag("chrm"), Tag("dmnd"), Tag("dmdd"), Tag("lumi"),
    ];

    private static readonly uint[] TypeStrings =
        [Tag("XYZ "), Tag("desc"), Tag("text"), Tag("mluc"), Tag("para"), Tag("curv"), Tag("sf32"), Tag("gbd ")];

    private static ReadOnlySpan<byte> InitialHeaderPrediction =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, (byte)'m', (byte)'n', (byte)'t', (byte)'r',
        (byte)'R', (byte)'G', (byte)'B', (byte)' ', (byte)'X', (byte)'Y', (byte)'Z', (byte)' ', 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, (byte)'a', (byte)'c', (byte)'s', (byte)'p', 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 246, 214, 0, 1, 0, 0, 0, 0, 211, 45,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary>
    /// Decodes the ICC stream of <paramref name="encodedSize"/> predicted bytes into the profile. The decoded size is
    /// checked against <paramref name="maxBytes"/> and passed to <paramref name="reserve"/> before it is allocated.
    /// </summary>
    public static byte[] Read(ref JxlBitReader reader, ulong encodedSize, long maxBytes, Action<long>? reserve,
        CancellationToken cancellationToken)
    {
        if (encodedSize > MaxProfileSize) throw Corrupt();
        long begin = reader.BitPosition;
        JxlEntropyCode code = JxlEntropyCode.Read(ref reader, IccContexts);
        using var symbols = new JxlSymbolReader(code, ref reader);
        int size = (int)encodedSize;
        byte[] encoded = new byte[Math.Min(size, 0x400)];
        for (int i = 0; i < size; i++)
        {
            if ((i & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            // libjxl's guard against streams that produce many bytes from few bits.
            if (i > 0 && (i & 0xFFFF) == 0 && i > (reader.BitPosition - begin) / 8 * 256)
            {
                throw Corrupt();
            }

            if (i == encoded.Length)
            {
                Array.Resize(ref encoded, Math.Min(size, encoded.Length * 2));
            }

            int context = i <= 128 ? 0 : 1 + ByteKind1(encoded[i - 1]) + ByteKind2(encoded[i - 2]) * 8;
            encoded[i] = (byte)symbols.Read(context, ref reader);
            if (i == PreambleSize - 1 && size > PreambleSize)
            {
                ulong profileSize = CheckPreamble(encoded.AsSpan(0, PreambleSize), Math.Min(size, 0x400), encodedSize);
                if ((long)profileSize > maxBytes) throw JxlDescriptor.Corrupt("The ICC profile exceeds MaxMetadataBytes.");
                reserve?.Invoke((long)profileSize);
            }
        }

        if (!symbols.IsFinalStateValid) throw Corrupt();
        if (size <= PreambleSize)
        {
            // Too short for the early check; the profile is at most a few bytes and Unpredict validates it.
            ulong declared = PeekDeclaredSize(encoded.AsSpan(0, size));
            if ((long)declared > Math.Min(maxBytes, 65536 + size)) throw Corrupt();
            reserve?.Invoke((long)declared);
        }

        return Unpredict(encoded.AsSpan(0, size), cancellationToken);
    }

    /// <summary>Decodes the ICC stream only to find where the frames start.</summary>
    public static void Skip(ref JxlBitReader reader, ulong size, CancellationToken cancellationToken) =>
        Read(ref reader, size, MaxProfileSize, null, cancellationToken);

    // Mimics the beginning of UnpredictICC for an early validity check; returns the declared profile size. Like libjxl,
    // the commands may extend to the end of the first buffered kilobyte, which is not decoded yet.
    private static ulong CheckPreamble(ReadOnlySpan<byte> data, int bufferedSize, ulong encodedSize)
    {
        int pos = 0;
        ulong outputSize = ReadVarInt(data, ref pos);
        CheckIs32Bit(outputSize);
        ulong commandsSize = ReadVarInt(data, ref pos);
        CheckIs32Bit(commandsSize);
        CheckOutOfBounds((ulong)pos, commandsSize, (ulong)bufferedSize);
        // UnpredictICC is expected to inflate its input, not the other way round.
        if (outputSize + 65536 < encodedSize || outputSize > MaxProfileSize) throw Corrupt();
        return outputSize;
    }

    private static ulong PeekDeclaredSize(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        return ReadVarInt(data, ref pos);
    }

    /// <summary>Decodes the result of PredictICC back to the ICC profile.</summary>
    public static byte[] Unpredict(ReadOnlySpan<byte> enc, CancellationToken cancellationToken = default)
    {
        int size = enc.Length;
        int pos = 0;
        ulong outputSize = ReadVarInt(enc, ref pos);
        CheckIs32Bit(outputSize);
        ulong commandsSize = ReadVarInt(enc, ref pos);
        CheckIs32Bit(commandsSize);
        CheckOutOfBounds((ulong)pos, commandsSize, (ulong)size);
        if (outputSize == 0 || outputSize > MaxProfileSize) throw Corrupt();
        var result = new Output((int)outputSize);
        int cpos = pos;
        int commandsEnd = cpos + (int)commandsSize;
        pos = commandsEnd;

        // Header
        Span<byte> header = stackalloc byte[HeaderSize];
        InitialHeaderPrediction.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)outputSize);
        for (int i = 0; i <= HeaderSize; i++)
        {
            if (result.Length == result.Capacity)
            {
                if (cpos != commandsEnd || pos != size) throw Corrupt();
                return result.Bytes;
            }

            if (i == HeaderSize) break;
            PredictHeader(result.Written, header, i);
            if (pos >= size) throw Corrupt();
            result.Add((byte)(enc[pos++] + header[i]));
        }

        if (cpos >= commandsEnd) throw Corrupt();

        // Tag list
        ReadOnlySpan<byte> commands = enc[..commandsEnd];
        ulong tagCount = ReadVarInt(commands, ref cpos);
        if (tagCount != 0)
        {
            tagCount--;
            CheckIs32Bit(tagCount);
            result.AddUInt32((uint)tagCount);
            ulong previousStart = HeaderSize + tagCount * 12;
            ulong previousSize = 0;
            while (cpos != commandsEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte command = enc[cpos++];
                int tagCode = command & 63;
                uint tag;
                if (tagCode == 0)
                {
                    break;
                }
                else if (tagCode == CommandTagUnknown)
                {
                    CheckOutOfBounds((ulong)pos, 4, (ulong)size);
                    tag = BinaryPrimitives.ReadUInt32BigEndian(enc[pos..]);
                    pos += 4;
                }
                else if (tagCode == CommandTagTrc)
                {
                    tag = Tag("rTRC");
                }
                else if (tagCode == CommandTagXyz)
                {
                    tag = Tag("rXYZ");
                }
                else
                {
                    if (tagCode - CommandTagStringFirst >= TagStrings.Length) throw Corrupt();
                    tag = TagStrings[tagCode - CommandTagStringFirst];
                }

                result.AddUInt32(tag);
                ulong tagSize = previousSize;
                if (tag == Tag("rXYZ") || tag == Tag("gXYZ") || tag == Tag("bXYZ") || tag == Tag("kXYZ")
                    || tag == Tag("wtpt") || tag == Tag("bkpt") || tag == Tag("lumi"))
                {
                    tagSize = 20;
                }

                ulong tagStart;
                if ((command & FlagBitOffset) != 0)
                {
                    tagStart = ReadVarInt(commands, ref cpos);
                }
                else
                {
                    CheckIs32Bit(previousStart);
                    tagStart = previousStart + previousSize;
                }

                CheckIs32Bit(tagStart);
                result.AddUInt32((uint)tagStart);
                if ((command & FlagBitSize) != 0)
                {
                    tagSize = ReadVarInt(commands, ref cpos);
                }

                CheckIs32Bit(tagSize);
                result.AddUInt32((uint)tagSize);
                previousStart = tagStart;
                previousSize = tagSize;
                if (tagCode == CommandTagTrc)
                {
                    result.AddTag(Tag("gTRC"), (uint)tagStart, (uint)tagSize);
                    result.AddTag(Tag("bTRC"), (uint)tagStart, (uint)tagSize);
                }

                if (tagCode == CommandTagXyz)
                {
                    CheckIs32Bit(tagStart + tagSize * 2);
                    result.AddTag(Tag("gXYZ"), (uint)(tagStart + tagSize), (uint)tagSize);
                    result.AddTag(Tag("bXYZ"), (uint)(tagStart + tagSize * 2), (uint)tagSize);
                }
            }
        }

        // Main content
        while (cpos != commandsEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte command = enc[cpos++];
            if (command == CommandInsert)
            {
                int count = Count(ReadVarInt(commands, ref cpos), pos, size);
                result.Add(enc.Slice(pos, count));
                pos += count;
            }
            else if (command is CommandShuffle2 or CommandShuffle4)
            {
                int count = Count(ReadVarInt(commands, ref cpos), pos, size);
                result.Add(Shuffle(enc.Slice(pos, count), command == CommandShuffle2 ? 2 : 4));
                pos += count;
            }
            else if (command == CommandPredict)
            {
                CheckOutOfBounds((ulong)cpos, 2, (ulong)commandsEnd);
                byte flags = enc[cpos++];
                int width = (flags & 3) + 1;
                if (width == 3) throw Corrupt();
                int order = (flags & 12) >> 2;
                if (order == 3) throw Corrupt();
                ulong stride = (ulong)width;
                if ((flags & 16) != 0)
                {
                    stride = ReadVarInt(commands, ref cpos);
                    if (stride < (ulong)width) throw Corrupt();
                }

                // stride * 4 >= size is rejected, as the specification requires.
                if (result.Length == 0 || (ulong)((result.Length - 1) >> 2) < stride) throw Corrupt();
                int count = Count(ReadVarInt(commands, ref cpos), pos, size);
                byte[] shuffled = width > 1 ? Shuffle(enc.Slice(pos, count), width) : enc.Slice(pos, count).ToArray();
                int start = result.Length;
                for (int i = 0; i < count; i++)
                {
                    byte predicted = LinearPredict(result.Written, start, i, (int)stride, width, order);
                    result.Add((byte)(predicted + shuffled[i]));
                }

                pos += count;
            }
            else if (command == CommandXyz)
            {
                result.AddUInt32(Tag("XYZ "));
                result.AddUInt32(0);
                CheckOutOfBounds((ulong)pos, 12, (ulong)size);
                result.Add(enc.Slice(pos, 12));
                pos += 12;
            }
            else if (command >= CommandTypeStartFirst && command < CommandTypeStartFirst + TypeStrings.Length)
            {
                result.AddUInt32(TypeStrings[command - CommandTypeStartFirst]);
                result.AddUInt32(0);
            }
            else
            {
                throw Corrupt();
            }
        }

        if (pos != size || result.Length != result.Capacity) throw Corrupt();
        return result.Bytes;
    }

    // A count of bytes that must remain in the data stream.
    private static int Count(ulong count, int pos, int size)
    {
        CheckOutOfBounds((ulong)pos, count, (ulong)size);
        return (int)count;
    }

    // Transposes width rows of ceil(size / width) columns ("ABCDabcd" -> "AaBbCcDd" for width 2).
    private static byte[] Shuffle(ReadOnlySpan<byte> data, int width)
    {
        int size = data.Length;
        int height = (size + width - 1) / width;
        byte[] result = new byte[size];
        int s = 0;
        int j = 0;
        for (int i = 0; i < size; i++)
        {
            result[i] = data[j];
            j += height;
            if (j >= size) j = ++s;
        }

        return result;
    }

    private static void PredictHeader(ReadOnlySpan<byte> icc, Span<byte> header, int pos)
    {
        int size = icc.Length;
        if (pos == 8 && size >= 8)
        {
            icc[4..8].CopyTo(header[80..]);
        }

        if (pos == 41 && size >= 41)
        {
            if (icc[40] == 'A') "PPL"u8.CopyTo(header[41..]);
            if (icc[40] == 'M') "SFT"u8.CopyTo(header[41..]);
        }

        if (pos == 42 && size >= 42)
        {
            if (icc[40] == 'S' && icc[41] == 'G') "I "u8.CopyTo(header[42..]);
            if (icc[40] == 'S' && icc[41] == 'U') "NW"u8.CopyTo(header[42..]);
        }
    }

    // Linear prediction of order 0-2 for integers of width bytes, stride bytes apart (LinearPredictICCValue).
    private static byte LinearPredict(ReadOnlySpan<byte> data, int start, int i, int stride, int width, int order)
    {
        int pos = start + i;
        if (width == 1)
        {
            return (byte)Predict(data[pos - stride], data[pos - stride * 2], data[pos - stride * 3], order);
        }

        if (width == 2)
        {
            int p = start + (i & ~1);
            uint p1 = (uint)((data[p - stride] << 8) + data[p - stride + 1]);
            uint p2 = (uint)((data[p - stride * 2] << 8) + data[p - stride * 2 + 1]);
            uint p3 = (uint)((data[p - stride * 3] << 8) + data[p - stride * 3 + 1]);
            ushort predicted = (ushort)Predict(p1, p2, p3, order);
            return (byte)((i & 1) != 0 ? predicted & 255 : (predicted >> 8) & 255);
        }

        int q = start + (i & ~3);
        uint value = Predict(Load(data, pos, q - stride), Load(data, pos, q - stride * 2), Load(data, pos, q - stride * 3), order);
        int shiftBytes = 3 - (i & 3);
        return (byte)((value >> (shiftBytes * 8)) & 255);
    }

    // libjxl reads the previous values bounded by the current position, yielding 0 past it (DecodeUint32).
    private static uint Load(ReadOnlySpan<byte> data, int end, int at) =>
        at + 4 > end ? 0 : BinaryPrimitives.ReadUInt32BigEndian(data[at..]);

    private static uint Predict(uint p1, uint p2, uint p3, int order) => order switch
    {
        0 => p1,
        1 => 2 * p1 - p2,
        2 => 3 * p1 - 3 * p2 + p3,
        _ => 0,
    };

    // A base-128 varint of at most 10 bytes whose value fits in 64 bits.
    private static ulong ReadVarInt(ReadOnlySpan<byte> input, ref int pos)
    {
        ulong value = 0;
        for (int i = 0; i < 9; i++)
        {
            if (pos >= input.Length) throw Corrupt();
            byte b = input[pos++];
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return value;
        }

        if (pos >= input.Length) throw Corrupt();
        byte last = input[pos++];
        if ((last & 0xFE) != 0) throw Corrupt();
        return value | (ulong)(last & 1) << 63;
    }

    private static void CheckOutOfBounds(ulong a, ulong b, ulong size)
    {
        ulong end = a + b;
        if (end > size || end < a) throw Corrupt();
    }

    private static void CheckIs32Bit(ulong value)
    {
        if (value > uint.MaxValue) throw Corrupt();
    }

    private static uint Tag(string name) =>
        (uint)(name[0] << 24 | name[1] << 16 | name[2] << 8 | name[3]);

    private static Exception Corrupt() => JxlDescriptor.Corrupt("Corrupt ICC profile stream.");

    private static int ByteKind1(byte b) => b switch
    {
        >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' => 0,
        >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)',' => 1,
        0 => 2,
        1 => 3,
        < 16 => 4,
        255 => 6,
        > 240 => 5,
        _ => 7,
    };

    private static int ByteKind2(byte b) => b switch
    {
        >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' => 0,
        >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)',' => 1,
        < 16 => 2,
        > 240 => 3,
        _ => 4,
    };

    // The profile being reconstructed. It never grows past the declared size: libjxl fails such streams later.
    private sealed class Output(int capacity)
    {
        private readonly byte[] bytes = new byte[capacity];

        public int Length { get; private set; }
        public int Capacity => bytes.Length;
        public byte[] Bytes => bytes;
        public ReadOnlySpan<byte> Written => bytes.AsSpan(0, Length);

        public void Add(byte value)
        {
            if (Length == bytes.Length) throw Corrupt();
            bytes[Length++] = value;
        }

        public void Add(ReadOnlySpan<byte> values)
        {
            if (values.Length > bytes.Length - Length) throw Corrupt();
            values.CopyTo(bytes.AsSpan(Length));
            Length += values.Length;
        }

        public void AddUInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            Add(buffer);
        }

        public void AddTag(uint tag, uint start, uint size)
        {
            AddUInt32(tag);
            AddUInt32(start);
            AddUInt32(size);
        }
    }
}
