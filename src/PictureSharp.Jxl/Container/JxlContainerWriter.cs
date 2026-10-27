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

    // The ftyp content: brand "jxl ", version 0, compatible with "jxl ".
    private static readonly byte[] FileTypeContent = [.. "jxl "u8, 0, 0, 0, 0, .. "jxl "u8];

    /// <summary>
    /// A file of <paramref name="codestream"/> in a jxlc box after the metadata boxes; a level other than
    /// <see cref="JxlContainer.DefaultLevel"/> adds a jxll box.
    /// </summary>
    public static byte[] Write(ReadOnlySpan<byte> codestream, IEnumerable<JxlBox> metadata, int level = JxlContainer.DefaultLevel)
    {
        using var output = new MemoryStream();
        output.Write(JxlDescriptor.ContainerSignature);
        WriteBox(output, new JxlBox(JxlBoxType.FileType, FileTypeContent));
        if (level != JxlContainer.DefaultLevel) WriteBox(output, new JxlBox(JxlBoxType.Level, new[] { (byte)level }));
        foreach (JxlBox box in metadata) WriteBox(output, box);
        JxlBoxHeader.Write(output, JxlBoxType.Codestream, codestream.Length);
        output.Write(codestream);
        return output.ToArray();
    }

    /// <summary>Writes one box; a compressed box becomes "brob" with the type in front of the Brotli stream.</summary>
    public static void WriteBox(Stream output, JxlBox box)
    {
        if (!box.Compress)
        {
            JxlBoxHeader.Write(output, box.Type, box.Content.Length);
            output.Write(box.Content.Span);
            return;
        }

        byte[] compressed = new byte[BrotliEncoder.GetMaxCompressedLength(box.Content.Length)];
        if (!BrotliEncoder.TryCompress(box.Content.Span, compressed, out int written, BrotliQuality, BrotliWindow))
        {
            throw new InvalidOperationException("Brotli compression failed.");
        }

        JxlBoxHeader.Write(output, JxlBoxType.Brotli, 4L + written);
        output.Write(System.Text.Encoding.Latin1.GetBytes(box.Type));
        output.Write(compressed, 0, written);
    }
}
