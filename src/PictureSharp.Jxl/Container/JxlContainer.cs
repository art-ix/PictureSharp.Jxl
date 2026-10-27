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

    /// <summary>The conformance level of a file without a jxll box.</summary>
    public const int DefaultLevel = 5;

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

    /// <summary>The conformance level from the jxll box, <see cref="DefaultLevel"/> when absent.</summary>
    public int Level { get; }

    public static JxlContainer Parse(ReadOnlyMemory<byte> data, bool includeMetadata = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<byte> bytes = data.Span;
        if (bytes.StartsWith(JxlDescriptor.CodestreamSignature))
        {
            return new JxlContainer(new JxlCodestream(data), false, 0, DefaultLevel);
        }

        if (!bytes.StartsWith(JxlDescriptor.ContainerSignature))
        {
            throw JxlDescriptor.Corrupt("Not a codestream or container.");
        }

        ReadOnlyMemory<byte>? jxlcPayload = null;
        var parts = new List<Part>();
        List<JxlMetadataBox>? metadata = includeMetadata ? [] : null;
        bool lastPartSeen = false;
        uint version = 0;
        int level = DefaultLevel;
        int boxCount = 1;
        foreach (JxlBoxHeader box in new JxlBoxEnumerator(bytes, JxlDescriptor.ContainerSignature.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            boxCount++;
            if (box.Type == JxlBoxType.Signature)
            {
                if (jxlcPayload is not null || lastPartSeen)
                {
                    break;
                }

                throw JxlDescriptor.Corrupt("Signature box inside the file.");
            }

            if ((boxCount == 2) != (box.Type == JxlBoxType.FileType))
            {
                throw JxlDescriptor.Corrupt("The ftyp box must be the second box.");
            }

            ReadOnlyMemory<byte> content = data.Slice((int)box.ContentStart, (int)box.ContentLength);
            switch (box.Type)
            {
                case JxlBoxType.FileType:
                    version = FileType(content.Span);
                    break;
                case JxlBoxType.Level:
                    level = content.Length >= 1 ? content.Span[0] : throw JxlDescriptor.Corrupt("Empty level box.");
                    break;
                case JxlBoxType.Codestream:
                    EnsureSingleCodestream(jxlcPayload is not null || parts.Count > 0);
                    jxlcPayload = content;
                    break;
                case JxlBoxType.CodestreamPart:
                    EnsureSingleCodestream(jxlcPayload is not null);
                    Part part = ReadPart(content, parts.Count, version);
                    lastPartSeen |= part.IsLast;
                    parts.Add(part);
                    break;
                case JxlBoxType.Brotli:
                    if (ReadBrotliBox(content) is { } compressed) metadata?.Add(compressed);
                    break;
                case JxlBoxType.Exif or JxlBoxType.Xmp:
                    metadata?.Add(new(box.Type, content, false));
                    break;
            }
        }

        if (boxCount < 2)
        {
            throw JxlDescriptor.Corrupt("Missing ftyp box.");
        }

        if (jxlcPayload is { } single)
        {
            return new JxlContainer(new JxlCodestream(single), true, version, level, metadata);
        }

        return parts.Count == 0
            ? throw JxlDescriptor.Corrupt("Container without a codestream box.")
            : new JxlContainer(new JxlCodestream(Order(parts, cancellationToken)), true, version, level, metadata);
    }

    /// <summary>The boxes of a container that <see cref="Parse"/> accepted, up to a second signature box.</summary>
    public static List<JxlBoxEntry> Boxes(ReadOnlySpan<byte> bytes)
    {
        var boxes = new List<JxlBoxEntry>();
        foreach (JxlBoxHeader box in new JxlBoxEnumerator(bytes, 0))
        {
            if (box.Type == JxlBoxType.Signature && box.Start > 0) break;
            string? brotliType = box.Type == JxlBoxType.Brotli ? JxlBoxHeader.FourCc(bytes[(int)box.ContentStart..]) : null;
            boxes.Add(new JxlBoxEntry(box, brotliType));
        }

        return boxes;
    }

    private static void EnsureSingleCodestream(bool hasOther)
    {
        if (hasOther)
        {
            throw JxlDescriptor.Corrupt("A file has one jxlc box and no jxlp boxes besides it.");
        }
    }

    // A jxlp box: a 31-bit counter, the last-part flag and a piece of the codestream.
    private static Part ReadPart(ReadOnlyMemory<byte> content, int index, uint version)
    {
        if (content.Length < 4)
        {
            throw JxlDescriptor.Corrupt("A jxlp box is too small to contain its index.");
        }

        if (index == MaxParts)
        {
            throw JxlDescriptor.Corrupt("Too many jxlp boxes.");
        }

        uint counter = BinaryPrimitives.ReadUInt32BigEndian(content.Span);
        var part = new Part(counter & 0x7FFF_FFFF, (counter & 0x8000_0000) != 0, content[4..]);
        if (version == 0 && part.Counter != index)
        {
            throw JxlDescriptor.Corrupt(
                $"The jxlp box index {part.Counter} is out of order (expected {index}); out-of-order boxes require file format version 1.");
        }

        return part;
    }

    // A brob box: Exif and xml payloads are indexed without their inner type; other metadata is skipped.
    private static JxlMetadataBox? ReadBrotliBox(ReadOnlyMemory<byte> content)
    {
        if (content.Length < 4)
        {
            throw JxlDescriptor.Corrupt("A brob box is too small to contain its type.");
        }

        string inner = JxlBoxHeader.FourCc(content.Span);
        // libjxl skips such a box and then finds no codestream; report the cause instead.
        if (inner is JxlBoxType.Codestream or JxlBoxType.CodestreamPart or JxlBoxType.Brotli)
        {
            throw JxlDescriptor.Corrupt($"Box type '{inner}' cannot be Brotli-compressed.");
        }

        return inner is JxlBoxType.Exif or JxlBoxType.Xmp ? new JxlMetadataBox(inner, content[4..], true) : null;
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

    private readonly record struct Part(uint Counter, bool IsLast, ReadOnlyMemory<byte> Content);
}
