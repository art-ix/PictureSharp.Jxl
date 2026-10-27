using System.IO.Compression;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Jpeg;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

// libjxl v0.12.0 lib/jxl/jpeg/enc_jpeg_data.cc (BSD-3-Clause).
internal static class JxlJpegReconstructionWriter
{
    public static byte[] Write(JxlJpegData jpeg, CancellationToken cancellationToken)
    {
        var writer = new JxlBitWriter();
        var visitor = new Writer(writer);
        JxlJpegReconstruction.Visit(jpeg, ref visitor, _ => { }, cancellationToken);
        writer.ZeroPadToByte();
        using var payload = new MemoryStream();
        payload.Write(writer.ToArray());
        using (var brotli = new BrotliStream(payload, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            for (int i = 0; i < jpeg.App.Count; i++) if (jpeg.AppTypes[i] == 0) WritePayload(brotli, jpeg.App[i], cancellationToken);
            foreach (byte[] bytes in jpeg.Comments.Concat(jpeg.InterMarker).Append(jpeg.Tail))
            {
                cancellationToken.ThrowIfCancellationRequested();
                WritePayload(brotli, bytes, cancellationToken);
            }
        }
        return payload.ToArray();
    }

    private static void WritePayload(Stream output, ReadOnlySpan<byte> payload, CancellationToken cancellationToken)
    {
        const int batch = 16384;
        for (int at = 0; at < payload.Length; at += batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(payload.Slice(at, Math.Min(batch, payload.Length - at)));
        }
    }

    private readonly struct Writer(JxlBitWriter writer) : IJxlJpegFields
    {
        public bool Reading => false;
        public int Bits(int value, int count) { writer.Write(count, (uint)value); return value; }
        public bool Bool(bool value) { writer.Write(value); return value; }
        public int U32(int value, JxlU32Distribution distribution) { JxlFieldWriter.U32(writer, (uint)value, distribution); return value; }
    }
}
