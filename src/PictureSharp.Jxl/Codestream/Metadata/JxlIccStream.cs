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
    private const int TagCodeMask = 63;
    private const int FlagBitOffset = 64;
    private const int FlagBitSize = 128;
    private const int PredictWidthMask = 3;
    private const int PredictOrderMask = 12;
    private const int PredictStrideFlag = 16;
    private const int TagEntrySize = 12;
    private const int XyzTagSize = 20;
    private const int XyzValuesSize = 12;
    // The first kilobyte is buffered before the commands must be complete (libjxl's initial ICC buffer).
    private const int InitialBufferSize = 0x400;
    // UnpredictICC may add at most this many bytes to its input.
    private const int MaxInflation = 65536;

    private static readonly uint RedTrc = Tag("rTRC");
    private static readonly uint GreenTrc = Tag("gTRC");
    private static readonly uint BlueTrc = Tag("bTRC");
    private static readonly uint RedXyz = Tag("rXYZ");
    private static readonly uint GreenXyz = Tag("gXYZ");
    private static readonly uint BlueXyz = Tag("bXYZ");
    private static readonly uint XyzType = Tag("XYZ ");
    // Tags whose size is that of one XYZ value unless given.
    private static readonly uint[] XyzTags = [RedXyz, GreenXyz, BlueXyz, Tag("kXYZ"), Tag("wtpt"), Tag("bkpt"), Tag("lumi")];

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
        byte[] encoded = new byte[Math.Min(size, InitialBufferSize)];
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

            int context = Context(encoded, i);
            encoded[i] = (byte)symbols.Read(context, ref reader);
            if (i == PreambleSize - 1 && size > PreambleSize)
            {
                ulong profileSize = CheckPreamble(encoded.AsSpan(0, PreambleSize), Math.Min(size, InitialBufferSize), encodedSize);
                if ((long)profileSize > maxBytes) throw JxlDescriptor.Corrupt("The ICC profile exceeds MaxMetadataBytes.");
                reserve?.Invoke((long)profileSize);
            }
        }

        if (!symbols.IsFinalStateValid) throw Corrupt();
        if (size <= PreambleSize)
        {
            // Too short for the early check; the profile is at most a few bytes and Unpredict validates it.
            ulong declared = PeekDeclaredSize(encoded.AsSpan(0, size));
            if ((long)declared > Math.Min(maxBytes, MaxInflation + size)) throw Corrupt();
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
        if (outputSize + MaxInflation < encodedSize || outputSize > MaxProfileSize) throw Corrupt();
        return outputSize;
    }

    private static ulong PeekDeclaredSize(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        return ReadVarInt(data, ref pos);
    }

    /// <summary>Decodes the result of PredictICC back to the ICC profile.</summary>
    public static byte[] Unpredict(ReadOnlySpan<byte> encoded, CancellationToken cancellationToken = default) =>
        new Unpredictor(encoded, cancellationToken).Run();

    /// <summary>
    /// The simplest valid input of <see cref="Unpredict"/> for <paramref name="icc"/>: the header predicted as
    /// UnpredictICC does, no tag list and one insert command for the rest. libjxl's PredictICC also models tags.
    /// </summary>
    public static byte[] Predict(ReadOnlySpan<byte> icc)
    {
        var commands = new List<byte>();
        var data = new List<byte>();
        Span<byte> header = stackalloc byte[HeaderSize];
        InitialHeaderPrediction.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)icc.Length);
        int headerLength = Math.Min(HeaderSize, icc.Length);
        for (int i = 0; i < headerLength; i++)
        {
            PredictHeader(icc[..i], header, i);
            data.Add((byte)(icc[i] - header[i]));
        }

        if (icc.Length > HeaderSize)
        {
            WriteVarInt(commands, 0);
            commands.Add(CommandInsert);
            WriteVarInt(commands, (ulong)(icc.Length - HeaderSize));
            data.AddRange(icc[HeaderSize..]);
        }

        var result = new List<byte>();
        WriteVarInt(result, (ulong)icc.Length);
        WriteVarInt(result, (ulong)commands.Count);
        result.AddRange(commands);
        result.AddRange(data);
        return [.. result];
    }

    /// <summary>The entropy context of encoded byte <paramref name="i"/>, from the two bytes before it.</summary>
    public static int Context(ReadOnlySpan<byte> encoded, int i) =>
        i <= 128 ? 0 : 1 + ByteKind1(encoded[i - 1]) + ByteKind2(encoded[i - 2]) * 8;

    public static int Contexts => IccContexts;

    private static void WriteVarInt(List<byte> output, ulong value)
    {
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }

        output.Add((byte)value);
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

    private static ImageFormatException Corrupt() => JxlDescriptor.Corrupt("Corrupt ICC profile stream.");

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

    // UnpredictICC: the commands follow two size varints, the data follows the commands; both are read in order.
    private ref struct Unpredictor
    {
        private readonly ReadOnlySpan<byte> encoded;
        private readonly ReadOnlySpan<byte> commands;
        private readonly CancellationToken cancellationToken;
        private readonly Output result;
        private int commandPosition;
        private int dataPosition;

        public Unpredictor(ReadOnlySpan<byte> encoded, CancellationToken cancellationToken)
        {
            int position = 0;
            ulong outputSize = ReadVarInt(encoded, ref position);
            CheckIs32Bit(outputSize);
            ulong commandsSize = ReadVarInt(encoded, ref position);
            CheckIs32Bit(commandsSize);
            CheckOutOfBounds((ulong)position, commandsSize, (ulong)encoded.Length);
            if (outputSize == 0 || outputSize > MaxProfileSize) throw Corrupt();
            this.encoded = encoded;
            this.cancellationToken = cancellationToken;
            result = new Output((int)outputSize);
            commands = encoded[..(position + (int)commandsSize)];
            commandPosition = position;
            dataPosition = commands.Length;
        }

        private readonly bool HasCommands => commandPosition != commands.Length;

        public byte[] Run()
        {
            if (ReadHeader())
            {
                return result.Bytes;
            }

            if (!HasCommands) throw Corrupt();
            ReadTagList();
            ReadContent();
            if (dataPosition != encoded.Length || result.Length != result.Capacity) throw Corrupt();
            return result.Bytes;
        }

        // The predicted 128-byte header; true when the profile ends within it.
        private bool ReadHeader()
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            InitialHeaderPrediction.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)result.Capacity);
            for (int i = 0; i <= HeaderSize; i++)
            {
                if (result.Length == result.Capacity)
                {
                    if (HasCommands || dataPosition != encoded.Length) throw Corrupt();
                    return true;
                }

                if (i == HeaderSize) break;
                PredictHeader(result.Written, header, i);
                if (dataPosition >= encoded.Length) throw Corrupt();
                result.Add((byte)(encoded[dataPosition++] + header[i]));
            }

            return false;
        }

        // The tag count and table: per tag a command with its name, and the offset and size unless they are implied.
        private void ReadTagList()
        {
            ulong tagCount = ReadVarInt(commands, ref commandPosition);
            if (tagCount == 0)
            {
                return;
            }

            tagCount--;
            CheckIs32Bit(tagCount);
            result.AddUInt32((uint)tagCount);
            ulong previousStart = HeaderSize + tagCount * TagEntrySize;
            ulong previousSize = 0;
            while (HasCommands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte command = commands[commandPosition++];
                int tagCode = command & TagCodeMask;
                if (tagCode == 0)
                {
                    break;
                }

                uint tag = ReadTagName(tagCode);
                result.AddUInt32(tag);
                ulong tagSize = XyzTags.Contains(tag) ? XyzTagSize : previousSize;
                ulong tagStart;
                if ((command & FlagBitOffset) != 0)
                {
                    tagStart = ReadVarInt(commands, ref commandPosition);
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
                    tagSize = ReadVarInt(commands, ref commandPosition);
                }

                CheckIs32Bit(tagSize);
                result.AddUInt32((uint)tagSize);
                (previousStart, previousSize) = (tagStart, tagSize);
                if (tagCode == CommandTagTrc)
                {
                    result.AddTag(GreenTrc, (uint)tagStart, (uint)tagSize);
                    result.AddTag(BlueTrc, (uint)tagStart, (uint)tagSize);
                }

                if (tagCode == CommandTagXyz)
                {
                    CheckIs32Bit(tagStart + tagSize * 2);
                    result.AddTag(GreenXyz, (uint)(tagStart + tagSize), (uint)tagSize);
                    result.AddTag(BlueXyz, (uint)(tagStart + tagSize * 2), (uint)tagSize);
                }
            }
        }

        private uint ReadTagName(int tagCode)
        {
            switch (tagCode)
            {
                case CommandTagUnknown:
                    CheckOutOfBounds((ulong)dataPosition, 4, (ulong)encoded.Length);
                    uint tag = BinaryPrimitives.ReadUInt32BigEndian(encoded[dataPosition..]);
                    dataPosition += 4;
                    return tag;
                case CommandTagTrc:
                    return RedTrc;
                case CommandTagXyz:
                    return RedXyz;
                default:
                    return tagCode - CommandTagStringFirst < TagStrings.Length ? TagStrings[tagCode - CommandTagStringFirst] : throw Corrupt();
            }
        }

        // The tag data: copied, transposed, predicted from earlier values, or XYZ and type headers.
        private void ReadContent()
        {
            while (HasCommands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte command = commands[commandPosition++];
                switch (command)
                {
                    case CommandInsert:
                        result.Add(ReadData());
                        break;
                    case CommandShuffle2 or CommandShuffle4:
                        result.Add(Shuffle(ReadData(), command == CommandShuffle2 ? 2 : 4));
                        break;
                    case CommandPredict:
                        ReadPredicted();
                        break;
                    case CommandXyz:
                        result.AddUInt32(XyzType);
                        result.AddUInt32(0);
                        result.Add(TakeData(XyzValuesSize));
                        break;
                    case >= CommandTypeStartFirst when command - CommandTypeStartFirst < TypeStrings.Length:
                        result.AddUInt32(TypeStrings[command - CommandTypeStartFirst]);
                        result.AddUInt32(0);
                        break;
                    default:
                        throw Corrupt();
                }
            }
        }

        // Bytes predicted linearly from the profile so far, with the width, order and stride of the command's flags.
        private void ReadPredicted()
        {
            CheckOutOfBounds((ulong)commandPosition, 2, (ulong)commands.Length);
            byte flags = commands[commandPosition++];
            int width = (flags & PredictWidthMask) + 1;
            if (width == 3) throw Corrupt();
            int order = (flags & PredictOrderMask) >> 2;
            if (order == 3) throw Corrupt();
            ulong stride = (ulong)width;
            if ((flags & PredictStrideFlag) != 0)
            {
                stride = ReadVarInt(commands, ref commandPosition);
                if (stride < (ulong)width) throw Corrupt();
            }

            // stride * 4 >= size is rejected, as the specification requires.
            if (result.Length == 0 || (ulong)((result.Length - 1) >> 2) < stride) throw Corrupt();
            ReadOnlySpan<byte> data = ReadData();
            byte[] shuffled = width > 1 ? Shuffle(data, width) : data.ToArray();
            int start = result.Length;
            for (int i = 0; i < shuffled.Length; i++)
            {
                byte predicted = LinearPredict(result.Written, start, i, (int)stride, width, order);
                result.Add((byte)(predicted + shuffled[i]));
            }
        }

        // A count from the commands, then that many data bytes.
        private ReadOnlySpan<byte> ReadData() => TakeData(Count(ReadVarInt(commands, ref commandPosition), dataPosition, encoded.Length));

        private ReadOnlySpan<byte> TakeData(int count)
        {
            CheckOutOfBounds((ulong)dataPosition, (ulong)count, (ulong)encoded.Length);
            ReadOnlySpan<byte> data = encoded.Slice(dataPosition, count);
            dataPosition += count;
            return data;
        }
    }

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
