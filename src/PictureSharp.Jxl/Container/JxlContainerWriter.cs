using System.Buffers.Binary;
using System.IO.Compression;

namespace PictureSharp.Formats.Jxl.Container;

/// <summary>A box to write: its type and content, Brotli-compressed into a brob box when requested.</summary>
internal readonly record struct JxlBox(string Type, ReadOnlyMemory<byte> Content, bool Compress = false);

/// <summary>
/// Writes ISO/IEC 18181-2 boxes: the signature and ftyp boxes, an optional level box, metadata boxes and the codestream,
/// in the order libjxl v0.12.0 lib/jxl/encode.cc writes them.
/// </summary>
internal static class JxlContainerWriter
{
    private const int BrotliQuality = 11;
    private const int BrotliWindow = 22;

    /// <summary>The signature and ftyp boxes (brand "jxl ", version 0, compatible with "jxl ").</summary>
    public static ReadOnlySpan<byte> Header =>
    [
        0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 13, 10, 0x87, 10,
        0, 0, 0, 20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'j', (byte)'x', (byte)'l', (byte)' ', 0, 0, 0, 0,
        (byte)'j', (byte)'x', (byte)'l', (byte)' ',
    ];

    /// <summary>A file of <paramref name="codestream"/> in a jxlc box after the metadata boxes; level 10 adds a jxll box.</summary>
    public static byte[] Write(ReadOnlySpan<byte> codestream, IEnumerable<JxlBox> metadata, int level = 5)
    {
        var output = new MemoryStream();
        output.Write(Header);
        if (level != 5) WriteBox(output, new JxlBox("jxll", new[] { (byte)level }));
        foreach (JxlBox box in metadata) WriteBox(output, box);
        WriteBoxHeader(output, "jxlc", codestream.Length);
        output.Write(codestream);
        return output.ToArray();
    }

    /// <summary>Writes one box; a compressed box becomes "brob" with the type in front of the Brotli stream.</summary>
    public static void WriteBox(Stream output, JxlBox box)
    {
        if (!box.Compress)
        {
            WriteBoxHeader(output, box.Type, box.Content.Length);
            output.Write(box.Content.Span);
            return;
        }

        byte[] compressed = new byte[BrotliEncoder.GetMaxCompressedLength(box.Content.Length)];
        if (!BrotliEncoder.TryCompress(box.Content.Span, compressed, out int written, BrotliQuality, BrotliWindow))
        {
            throw new InvalidOperationException("Brotli compression failed.");
        }

        WriteBoxHeader(output, "brob", 4L + written);
        output.Write(System.Text.Encoding.Latin1.GetBytes(box.Type));
        output.Write(compressed, 0, written);
    }

    private static void WriteBoxHeader(Stream output, string type, long contentLength)
    {
        Span<byte> header = stackalloc byte[16];
        byte[] name = System.Text.Encoding.Latin1.GetBytes(type);
        if (contentLength + 8 <= uint.MaxValue)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(contentLength + 8));
            name.CopyTo(header[4..]);
            output.Write(header[..8]);
            return;
        }

        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        name.CopyTo(header[4..]);
        BinaryPrimitives.WriteUInt64BigEndian(header[8..], (ulong)(contentLength + 16));
        output.Write(header);
    }
}
