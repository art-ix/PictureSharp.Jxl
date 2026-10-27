using System.Buffers.Binary;
using System.Text;

namespace PictureSharp.Formats.Jxl.Container;

/// <summary>The ISO/IEC 18181-2 box types this codec reads or writes.</summary>
internal static class JxlBoxType
{
    public const string Signature = "JXL ";
    public const string FileType = "ftyp";
    public const string Level = "jxll";
    public const string Codestream = "jxlc";
    public const string CodestreamPart = "jxlp";
    public const string Brotli = "brob";
    public const string Exif = "Exif";
    public const string Xmp = "xml ";
    public const string JpegReconstruction = "jbrd";
}

/// <summary>
/// An ISO/IEC 14496-12 box header: a 32-bit size (1: a 64-bit size follows; 0: the box runs to the end of the file)
/// and a four-character type, with the offsets of the box, its content and its end.
/// </summary>
internal readonly record struct JxlBoxHeader(string Type, long Start, long ContentStart, long End)
{
    private const int Size = 8;
    private const int LargeSize = 16;

    public long ContentLength => End - ContentStart;

    /// <summary>The four-character code at the start of <paramref name="bytes"/>.</summary>
    public static string FourCc(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes[..4]);

    public static JxlBoxHeader Read(ReadOnlySpan<byte> bytes, long position)
    {
        long available = bytes.Length - position;
        if (available < Size)
        {
            throw JxlDescriptor.Corrupt("Truncated box header.");
        }

        ReadOnlySpan<byte> header = bytes[(int)position..];
        ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
        string type = FourCc(header[4..]);
        int headerSize = Size;
        if (size == 1)
        {
            if (available < LargeSize)
            {
                throw JxlDescriptor.Corrupt("Truncated box header.");
            }

            size = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            headerSize = LargeSize;
        }

        if (size == 0)
        {
            size = (ulong)available;
        }
        else if (size < (ulong)headerSize)
        {
            throw JxlDescriptor.Corrupt($"Invalid box size {size}.");
        }
        else if (size > (ulong)available)
        {
            throw JxlDescriptor.Corrupt($"Truncated '{type}' box.");
        }

        return new JxlBoxHeader(type, position, position + headerSize, position + (long)size);
    }

    /// <summary>Writes the header of a box with <paramref name="contentLength"/> bytes, with a 64-bit size when needed.</summary>
    public static void Write(Stream output, string type, long contentLength)
    {
        bool large = contentLength + Size > uint.MaxValue;
        Span<byte> header = stackalloc byte[LargeSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, large ? 1 : (uint)(contentLength + Size));
        Encoding.Latin1.GetBytes(type, header.Slice(4, 4));
        if (large) BinaryPrimitives.WriteUInt64BigEndian(header[8..], (ulong)(contentLength + LargeSize));
        output.Write(header[..(large ? LargeSize : Size)]);
    }
}

/// <summary>Enumerates the box headers of a container from a position to the end of the input.</summary>
internal ref struct JxlBoxEnumerator
{
    private readonly ReadOnlySpan<byte> bytes;
    private long next;

    public JxlBoxEnumerator(ReadOnlySpan<byte> bytes, long position)
    {
        this.bytes = bytes;
        next = position;
    }

    public JxlBoxHeader Current { get; private set; }

    public readonly JxlBoxEnumerator GetEnumerator() => this;

    public bool MoveNext()
    {
        if (next >= bytes.Length) return false;
        Current = JxlBoxHeader.Read(bytes, next);
        next = Current.End;
        return true;
    }
}

/// <summary>A box of an accepted container: its header and, for a brob box, the type of the compressed content.</summary>
internal readonly record struct JxlBoxEntry(JxlBoxHeader Header, string? BrotliType)
{
    public string Type => Header.Type;

    /// <summary>The type of the content, compressed or not.</summary>
    public string Kind => BrotliType ?? Header.Type;

    public bool IsCodestream => Header.Type is JxlBoxType.Codestream or JxlBoxType.CodestreamPart;
}
