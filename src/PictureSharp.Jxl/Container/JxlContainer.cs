using System.Buffers.Binary;
using PictureSharp.Formats.Jxl.Codestream.IO;

namespace PictureSharp.Formats.Jxl.Container;

/// <summary>
/// Locates the codestream of a raw or ISO BMFF-based JPEG XL file (ISO/IEC 18181-2). The whole input is in memory,
/// so all boxes are scanned before the jxlp parts are ordered. The order and box rules follow libjxl v0.12.0
/// lib/jxl/decode.cc (ParseBoxHeader, HandleBoxes; BSD-3-Clause):
/// <list type="bullet">
/// <item>the signature box comes first and the ftyp box second, with major brand "jxl " and file format version 0
/// or 1;</item>
/// <item>the codestream is one jxlc box, or jxlp boxes whose 31-bit counters run 0, 1, 2… and whose last part sets
/// the high bit; jxlc and jxlp are not mixed;</item>
/// <item>version 0 requires jxlp boxes in counter order; version 1 also allows them out of order (libjxl 0.12.0
/// buffers early parts). Duplicate counters, gaps and parts after the last one are errors. Like libjxl, a sequence
/// without a part marked last is accepted; a codestream that is really incomplete then fails as truncated.</item>
/// </list>
/// Metadata payloads are not decoded. Exif/xml slices may be collected on request; other metadata is skipped. A second signature box
/// after a complete codestream ends the file, as in libjxl.
/// </summary>
internal sealed class JxlContainer
{
    /// <summary>libjxl's limit on buffered out-of-order jxlp boxes.</summary>
    public const int MaxParts = 1 << 20;

    private static ReadOnlySpan<byte> Signature => [0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 13, 10, 0x87, 10];

    private JxlContainer(JxlCodestream codestream, bool boxed, uint version, int level, List<JxlMetadataBox>? metadata = null)
    {
        Codestream = codestream;
        IsBoxed = boxed;
        MetadataBoxes = metadata is null ? Array.Empty<JxlMetadataBox>() : metadata.AsReadOnly();
        FileFormatVersion = version;
        Level = level;
    }

    public JxlCodestream Codestream { get; }

    public IReadOnlyList<JxlMetadataBox> MetadataBoxes { get; }

    /// <summary>Whether the file uses the box container rather than a bare codestream.</summary>
    public bool IsBoxed { get; }

    /// <summary>The ftyp minor version: 0, or 1 when jxlp boxes may appear out of order.</summary>
    public uint FileFormatVersion { get; }

    /// <summary>The conformance level from the jxll box, 5 when absent.</summary>
    public int Level { get; }

    public static JxlContainer Parse(ReadOnlyMemory<byte> data, bool includeMetadata = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<byte> bytes = data.Span;
        if (bytes is [0xFF, 0x0A, ..])
        {
            return new JxlContainer(new JxlCodestream(data), false, 0, 5);
        }

        if (!bytes.StartsWith(Signature))
        {
            throw JxlDescriptor.Corrupt("Not a codestream or container.");
        }

        ReadOnlyMemory<byte>? whole = null;
        var parts = new List<Part>();
        List<JxlMetadataBox>? metadata = includeMetadata ? [] : null;
        bool lastPartSeen = false;
        uint version = 0;
        int level = 5;
        int count = 1;
        long position = Signature.Length;
        while (position < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Box box = ReadBox(bytes, position);
            count++;
            if (box.Is("JXL "))
            {
                if (whole is not null || lastPartSeen)
                {
                    break;
                }

                throw JxlDescriptor.Corrupt("Signature box inside the file.");
            }

            if ((count == 2) != box.Is("ftyp"))
            {
                throw JxlDescriptor.Corrupt("The ftyp box must be the second box.");
            }

            ReadOnlyMemory<byte> content = data.Slice((int)box.ContentStart, (int)box.ContentLength);
            switch (box.Type)
            {
                case "ftyp":
                    version = FileType(content.Span);
                    break;
                case "jxll":
                    level = content.Length >= 1 ? content.Span[0] : throw JxlDescriptor.Corrupt("Empty level box.");
                    break;
                case "jxlc":
                    if (whole is not null || parts.Count > 0)
                    {
                        throw JxlDescriptor.Corrupt("A file has one jxlc box and no jxlp boxes besides it.");
                    }

                    whole = content;
                    break;
                case "jxlp":
                    if (whole is not null)
                    {
                        throw JxlDescriptor.Corrupt("A file has one jxlc box and no jxlp boxes besides it.");
                    }

                    if (content.Length < 4)
                    {
                        throw JxlDescriptor.Corrupt("A jxlp box is too small to contain its index.");
                    }

                    if (parts.Count == MaxParts)
                    {
                        throw JxlDescriptor.Corrupt("Too many jxlp boxes.");
                    }

                    uint index = BinaryPrimitives.ReadUInt32BigEndian(content.Span);
                    var part = new Part(index & 0x7FFF_FFFF, (index & 0x8000_0000) != 0, content[4..]);
                    if (version == 0 && part.Counter != parts.Count)
                    {
                        throw JxlDescriptor.Corrupt(
                            $"The jxlp box index {part.Counter} is out of order (expected {parts.Count}); out-of-order boxes require file format version 1.");
                    }

                    lastPartSeen |= part.IsLast;
                    parts.Add(part);
                    break;
                case "brob":
                    if (content.Length < 4)
                    {
                        throw JxlDescriptor.Corrupt("A brob box is too small to contain its type.");
                    }

                    string inner = System.Text.Encoding.Latin1.GetString(content.Span[..4]);
                    // libjxl skips such a box and then finds no codestream; report the cause instead.
                    if (inner is "jxlc" or "jxlp" or "brob")
                    {
                        throw JxlDescriptor.Corrupt($"Box type '{inner}' cannot be Brotli-compressed.");
                    }

                    if (inner is "Exif" or "xml ") metadata?.Add(new(inner, content[4..], true));
                    break;
                case "Exif" or "xml ":
                    metadata?.Add(new(box.Type, content, false));
                    break;
            }

            position = box.End;
        }

        if (count < 2)
        {
            throw JxlDescriptor.Corrupt("Missing ftyp box.");
        }

        if (whole is { } single)
        {
            return new JxlContainer(new JxlCodestream(single), true, version, level, metadata);
        }

        return parts.Count == 0
            ? throw JxlDescriptor.Corrupt("Container without a codestream box.")
            : new JxlContainer(new JxlCodestream(Order(parts, cancellationToken)), true, version, level, metadata);
    }

    // Counters must be unique and contiguous from 0, and only the highest one may be marked last.
    private static ReadOnlyMemory<byte>[] Order(List<Part> parts, CancellationToken cancellationToken)
    {
        var ordered = new ReadOnlyMemory<byte>[parts.Count];
        var seen = new bool[parts.Count];
        foreach (Part part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part.Counter >= parts.Count)
            {
                throw JxlDescriptor.Corrupt($"The jxlp box index {part.Counter} leaves a gap in the codestream.");
            }

            if (seen[part.Counter])
            {
                throw JxlDescriptor.Corrupt($"Duplicate jxlp box index {part.Counter}.");
            }

            if (part.IsLast && part.Counter != parts.Count - 1)
            {
                throw JxlDescriptor.Corrupt("A jxlp box follows the last jxlp box.");
            }

            seen[part.Counter] = true;
            ordered[part.Counter] = part.Content;
        }

        return ordered;
    }

    private static uint FileType(ReadOnlySpan<byte> content)
    {
        if (content.Length < 12)
        {
            throw JxlDescriptor.Corrupt("File type box too small.");
        }

        if (!content.StartsWith("jxl "u8))
        {
            throw JxlDescriptor.Corrupt("File type box major brand must be \"jxl \".");
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(content[4..]);
        return version <= 1
            ? version
            : throw JxlDescriptor.Corrupt($"Unknown file format version {version} (known versions: 0, 1).");
    }

    // ISO/IEC 14496-12 box header: 32-bit size (1 = 64-bit size follows, 0 = to the end of the file) and type.
    private static Box ReadBox(ReadOnlySpan<byte> bytes, long position)
    {
        long available = bytes.Length - position;
        if (available < 8)
        {
            throw JxlDescriptor.Corrupt("Truncated box header.");
        }

        ReadOnlySpan<byte> header = bytes[(int)position..];
        ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
        string type = System.Text.Encoding.Latin1.GetString(header.Slice(4, 4));
        int headerSize = 8;
        if (size == 1)
        {
            if (available < 16)
            {
                throw JxlDescriptor.Corrupt("Truncated box header.");
            }

            size = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            headerSize = 16;
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

        return new Box(type, position + headerSize, (long)size - headerSize);
    }

    private readonly record struct Box(string Type, long ContentStart, long ContentLength)
    {
        public long End => ContentStart + ContentLength;

        public bool Is(string type) => Type == type;
    }

    private readonly record struct Part(uint Counter, bool IsLast, ReadOnlyMemory<byte> Content);
}
