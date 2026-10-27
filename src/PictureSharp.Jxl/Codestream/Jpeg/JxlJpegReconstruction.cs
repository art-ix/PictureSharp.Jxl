using System.Buffers;
using System.IO.Compression;
using PictureSharp.Formats.Jxl.Codestream.IO;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;

namespace PictureSharp.Formats.Jxl.Codestream.Jpeg;

// Wire format ported from libjxl v0.12.0 jpeg/jpeg_data.cc, enc_jpeg_data.cc and dec_jpeg_data.cc (BSD-3-Clause).
internal static class JxlJpegReconstruction
{
    private static readonly JxlU32Distribution SmallCount = new(Val(0), BitsOffset(2, 1), BitsOffset(4, 4), BitsOffset(16, 20));
    private static readonly JxlU32Distribution BlockDelta = new(Val(0), BitsOffset(3, 1), BitsOffset(5, 9), BitsOffset(28, 41));
    private static readonly JxlU32Distribution Four = new(Val(1), Val(2), Val(3), Val(4));

    public static JxlJpegData Read(ReadOnlyMemory<byte> bytes, Action<long> reserve, CancellationToken cancellationToken)
    {
        var jpeg = new JxlJpegData();
        var visitor = new Reader(bytes, reserve);
        Visit(jpeg, ref visitor, reserve, cancellationToken);
        int position = visitor.Finish();
        var buffers = new List<byte[]>();
        int iccCount = jpeg.AppTypes.Count(t => t == 1), iccIndex = 0;
        for (int i = 0; i < jpeg.App.Count; i++)
        {
            byte[] app = jpeg.App[i];
            int type = jpeg.AppTypes[i];
            if (type == 0) { buffers.Add(app); continue; }
            app[1] = (byte)((app.Length - 1) >> 8); app[2] = (byte)(app.Length - 1);
            ReadOnlySpan<byte> tag = type == 1 ? "ICC_PROFILE\0"u8 : type == 2 ? "Exif\0\0"u8 : "http://ns.adobe.com/xap/1.0/\0"u8;
            if (app.Length < 3 + tag.Length + (type == 1 ? 2 : 0)) throw JxlDescriptor.Corrupt("Reconstructed JPEG APP marker too short.");
            app[0] = type == 1 ? (byte)0xe2 : (byte)0xe1;
            tag.CopyTo(app.AsSpan(3));
            if (type == 1) { app[15] = (byte)++iccIndex; app[16] = (byte)iccCount; }
        }
        buffers.AddRange(jpeg.Comments); buffers.AddRange(jpeg.InterMarker); buffers.Add(jpeg.Tail);
        using var decoder = new BrotliDecoder();
        ReadOnlySpan<byte> compressed = bytes.Span[position..];
        bool done = false;
        foreach (byte[] buffer in buffers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int written = 0;
            while (written < buffer.Length)
            {
                if (done) throw JxlDescriptor.Corrupt("Truncated JPEG reconstruction payload.");
                OperationStatus status = decoder.Decompress(compressed, buffer.AsSpan(written), out int used, out int produced);
                compressed = compressed[used..]; written += produced;
                if (status == OperationStatus.InvalidData || status == OperationStatus.NeedMoreData || (used == 0 && produced == 0 && status != OperationStatus.Done))
                    throw JxlDescriptor.Corrupt("Invalid JPEG reconstruction Brotli stream.");
                done = status == OperationStatus.Done;
            }
        }
        if (!done)
        {
            Span<byte> excess = stackalloc byte[1];
            OperationStatus status = decoder.Decompress(compressed, excess, out int used, out int produced);
            compressed = compressed[used..];
            if (status != OperationStatus.Done || produced != 0) throw JxlDescriptor.Corrupt("Excess or incomplete JPEG reconstruction payload.");
        }
        if (!compressed.IsEmpty) throw JxlDescriptor.Corrupt("Trailing JPEG reconstruction Brotli data.");
        foreach (byte[] marker in jpeg.App.Where((_, i) => jpeg.AppTypes[i] == 0).Concat(jpeg.Comments))
            if ((marker[1] << 8) + marker[2] + 1 != marker.Length) throw JxlDescriptor.Corrupt("Invalid reconstructed JPEG marker size.");
        jpeg.ValidateReconstruction();
        return jpeg;
    }

    public static void FillExternalMetadata(JxlJpegData jpeg, ReadOnlySpan<byte> icc, ReadOnlySpan<byte> exif, ReadOnlySpan<byte> xmp)
    {
        int iccAt = 0;
        bool usedExif = false, usedXmp = false;
        for (int i = 0; i < jpeg.App.Count; i++)
        {
            Span<byte> marker = jpeg.App[i];
            switch (jpeg.AppTypes[i])
            {
                case 1:
                    int length = marker.Length - 17;
                    if (length > icc.Length - iccAt) throw JxlDescriptor.Corrupt("Reconstruction ICC size mismatch.");
                    icc.Slice(iccAt, length).CopyTo(marker[17..]); iccAt += length;
                    break;
                case 2:
                    if (usedExif || marker.Length - 9 != exif.Length) throw JxlDescriptor.Corrupt("Reconstruction EXIF size mismatch.");
                    exif.CopyTo(marker[9..]); usedExif = true; break;
                case 3:
                    if (usedXmp || marker.Length - 32 != xmp.Length) throw JxlDescriptor.Corrupt("Reconstruction XMP size mismatch.");
                    xmp.CopyTo(marker[32..]); usedXmp = true; break;
            }
        }
        if (iccAt != 0 && iccAt != icc.Length) throw JxlDescriptor.Corrupt("Unused reconstruction ICC data.");
    }

    internal static void Visit<T>(JxlJpegData jpeg, ref T v, Action<long> reserve, CancellationToken cancellationToken)
        where T : IJxlJpegFields, allows ref struct
    {
        bool reading = v.Reading;
        bool gray = v.Bool(jpeg.Gray);
        if (reading) for (int i = 0; i < (gray ? 1 : 3); i++) jpeg.Components.Add(new JxlJpegComponent());
        int apps = 0, comments = 0, scans = 0, inter = 0;
        bool dri = false;
        for (int i = 0; ; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i >= 16384 || (!reading && i >= jpeg.Markers.Count)) throw JxlDescriptor.Corrupt("Invalid JPEG marker order.");
            int marker = v.Bits(reading ? 0 : jpeg.Markers[i] - 0xc0, 6) + 0xc0;
            if (reading) jpeg.Markers.Add((byte)marker);
            if (marker is >= 0xe0 and <= 0xef) apps++;
            else if (marker == 0xfe) comments++;
            else if (marker == 0xda) scans++;
            else if (marker == 255) inter++;
            else if (marker == 0xdd) dri = true;
            else if (marker is not (0xc0 or 0xc1 or 0xc2 or 0xc4 or >= 0xd0 and <= 0xd7 or 0xd9 or 0xdb))
                throw JxlDescriptor.Corrupt("Invalid JPEG reconstruction marker.");
            if (marker == 0xd9) break;
        }
        if (scans == 0) throw JxlDescriptor.Corrupt("JPEG reconstruction has no scans.");
        for (int i = 0; i < apps; i++)
        {
            int type = v.U32(reading ? 0 : jpeg.AppTypes[i], new(Val(0), Val(1), BitsOffset(1, 2), BitsOffset(2, 4)));
            if (type > 3) throw JxlDescriptor.Corrupt("Unknown JPEG reconstruction APP type.");
            int length = v.Bits(reading ? 0 : jpeg.App[i].Length - 1, 16) + 1;
            if (length < 3) throw JxlDescriptor.Corrupt("Invalid JPEG reconstruction APP length.");
            if (reading) { reserve(length); jpeg.App.Add(new byte[length]); jpeg.AppTypes.Add(type); }
        }
        for (int i = 0; i < comments; i++)
        {
            int length = v.Bits(reading ? 0 : jpeg.Comments[i].Length - 1, 16) + 1;
            if (length < 3) throw JxlDescriptor.Corrupt("Invalid JPEG reconstruction COM length.");
            if (reading) { reserve(length); jpeg.Comments.Add(new byte[length]); }
        }
        int quantCount = v.U32(jpeg.Quant.Count, Four);
        if (quantCount == 4) throw JxlDescriptor.Corrupt("Invalid JPEG reconstruction quantization table count.");
        for (int i = 0; i < quantCount; i++)
        {
            if (reading) jpeg.Quant.Add(new JxlJpegQuant());
            var q = jpeg.Quant[i]; q.Precision = v.Bits(q.Precision, 1); q.Index = v.Bits(q.Index, 2); q.Last = v.Bool(q.Last);
        }
        int componentType = jpeg.Gray && jpeg.Components[0].Id == 1 ? 0
            : !jpeg.Gray && jpeg.Components[0].Id == 1 && jpeg.Components[1].Id == 2 && jpeg.Components[2].Id == 3 ? 1
            : !jpeg.Gray && jpeg.Components[0].Id == 'R' && jpeg.Components[1].Id == 'G' && jpeg.Components[2].Id == 'B' ? 2 : 3;
        componentType = v.Bits(componentType, 2);
        int numComponents = componentType == 3 ? v.U32(jpeg.Components.Count, Four) : componentType == 0 ? 1 : 3;
        if (numComponents != jpeg.Components.Count) throw JxlDescriptor.Corrupt("Inconsistent reconstruction component count.");
        for (int i = 0; i < numComponents; i++)
            jpeg.Components[i].Id = componentType == 3 ? v.Bits(jpeg.Components[i].Id, 8) : componentType == 2 ? "RGB"[i] : i + 1;
        for (int i = 0; i < numComponents; i++)
        {
            var c = jpeg.Components[i]; c.Quant = v.Bits(c.Quant, 2);
            if (c.Quant >= quantCount) throw JxlDescriptor.Corrupt("Invalid reconstruction quantization index.");
        }
        int numHuffman = v.U32(jpeg.Huffman.Count, new(Val(4), BitsOffset(3, 2), BitsOffset(4, 10), BitsOffset(6, 26)));
        for (int i = 0; i < numHuffman; i++)
        {
            if (reading) jpeg.Huffman.Add(new JxlJpegHuffman());
            cancellationToken.ThrowIfCancellationRequested();
            var h = jpeg.Huffman[i]; bool ac = v.Bool(h.Slot >= 16); h.Slot = v.Bits(h.Slot & 15, 2) + (ac ? 16 : 0); h.Last = v.Bool(h.Last);
            int total = 0;
            for (int k = 0; k <= 16; k++)
            {
                h.Counts[k] = v.U32(h.Counts[k], new(Val(0), Val(1), BitsOffset(3, 2), Bits32(8)));
                total += h.Counts[k];
            }
            if (total > 257) throw JxlDescriptor.Corrupt("Reconstruction Huffman alphabet too large.");
            for (int k = 0; k < total; k++) h.Values[k] = v.U32(h.Values[k], new(Bits32(2), BitsOffset(2, 4), BitsOffset(4, 8), BitsOffset(8, 1)));
            h.Build();
        }
        for (int i = 0; i < scans; i++)
        {
            if (reading) jpeg.Scans.Add(new JxlJpegScan());
            var s = jpeg.Scans[i]; int count = v.U32(s.Components.Count, Four);
            if (count >= 4) throw JxlDescriptor.Corrupt("Invalid reconstruction scan component count.");
            s.Ss = v.Bits(s.Ss, 6); s.Se = v.Bits(s.Se, 6); s.Al = v.Bits(s.Al, 4); s.Ah = v.Bits(s.Ah, 4);
            if (s.Ss > s.Se || s.Al > 10 || s.Ah > 11 || s.Ah != 0 && s.Ah != s.Al + 1) throw JxlDescriptor.Corrupt("Invalid reconstruction scan parameters.");
            for (int k = 0; k < count; k++)
            {
                var sc = reading ? default : s.Components[k];
                int c = v.Bits(sc.Component, 2), a = v.Bits(sc.AcTable, 2), d = v.Bits(sc.DcTable, 2);
                if (c >= numComponents || (reading && s.Components.Any(old => old.Component == c))) throw JxlDescriptor.Corrupt("Invalid reconstruction scan component.");
                if (reading) s.Components.Add(new(c, d, a));
            }
            s.LastPass = v.U32(s.LastPass, new(Val(0), Val(1), Val(2), BitsOffset(3, 3)));
        }
        if (dri) jpeg.RestartInterval = v.Bits(jpeg.RestartInterval, 16);
        foreach (var s in jpeg.Scans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = v.U32(s.ResetPoints.Count, SmallCount), last = -1;
            if (reading) reserve((long)count * sizeof(int));
            for (int i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                int delta = v.U32(reading ? 0 : s.ResetPoints[i] - last - 1, BlockDelta);
                long block = (long)last + 1 + delta;
                if (block >= 3 << 26) throw JxlDescriptor.Corrupt("Reconstruction block index overflow.");
                last = (int)block; if (reading) s.ResetPoints.Add(last);
            }
            count = v.U32(s.ExtraZeroRuns.Count, SmallCount); last = -1;
            if (reading) reserve((long)count * 2 * sizeof(int));
            for (int i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                int n = v.U32(reading ? 1 : s.ExtraZeroRuns[i].Count, new(Val(1), BitsOffset(2, 2), BitsOffset(4, 5), BitsOffset(8, 20)));
                int delta = v.U32(reading ? 0 : s.ExtraZeroRuns[i].Block - last - 1, BlockDelta);
                long block = (long)last + 1 + delta;
                if (block >= 3 << 26 || n > 4) throw JxlDescriptor.Corrupt("Invalid reconstruction extra zero run.");
                last = (int)block; if (reading) s.ExtraZeroRuns.Add((last, n));
            }
        }
        for (int i = 0; i < inter; i++)
        {
            int length = v.Bits(reading ? 0 : jpeg.InterMarker[i].Length, 16);
            if (reading) { reserve(length); jpeg.InterMarker.Add(new byte[length]); }
        }
        int tail = v.U32(jpeg.Tail.Length, new(Val(0), BitsOffset(8, 1), BitsOffset(16, 257), BitsOffset(22, 65793)));
        if (reading) { reserve(tail); jpeg.Tail = new byte[tail]; }
        jpeg.HasZeroPadding = v.Bool(jpeg.HasZeroPadding);
        if (jpeg.HasZeroPadding)
        {
            int count = v.Bits(jpeg.Padding.Count, 24);
            if (reading) reserve(count);
            for (int i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                int bit = v.Bits(reading ? 0 : jpeg.Padding[i], 1);
                if (reading) jpeg.Padding.Add((byte)bit);
            }
        }
    }

    private ref struct Reader(ReadOnlyMemory<byte> data, Action<long> reserve) : IJxlJpegFields
    {
        private JxlBitReader reader = new(new JxlCodestream(data));
        public readonly bool Reading => true;
        private long charged;
        private void Charge()
        {
            long bytes = (reader.BitPosition + 7) / 8;
            reserve(bytes - charged); charged = bytes;
        }
        public int Bits(int value, int count) { int result = (int)reader.ReadBits(count); Charge(); return result; }
        public bool Bool(bool value) => Bits(0, 1) != 0;
        public int U32(int value, JxlU32Distribution distribution)
        {
            int result = checked((int)JxlFields.U32(ref reader, distribution)); Charge(); return result;
        }
        public int Finish() { reader.JumpToByteBoundary(); return (int)(reader.BitPosition / 8); }
    }
}

/// <summary>Shared JPEG reconstruction field order; implemented by bounded readers and encoding-side writers.</summary>
internal interface IJxlJpegFields
{
    bool Reading { get; }
    int Bits(int value, int count);
    bool Bool(bool value);
    int U32(int value, JxlU32Distribution distribution);
}
