namespace PictureSharp.Formats.Jxl.Codestream.Jpeg;

// Scalar port of libjxl v0.12.0 lib/jxl/jpeg/jpeg_data.{h,cc} (BSD-3-Clause).
internal sealed class JxlJpegData
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int RestartInterval { get; set; }
    public List<JxlJpegComponent> Components { get; } = [];
    public List<JxlJpegQuant> Quant { get; } = [];
    public List<JxlJpegHuffman> Huffman { get; } = [];
    public List<JxlJpegScan> Scans { get; } = [];
    public List<byte> Markers { get; } = [];
    public List<byte[]> App { get; } = [];
    public List<int> AppTypes { get; } = [];
    public List<byte[]> Comments { get; } = [];
    public List<byte[]> InterMarker { get; } = [];
    public byte[] Tail { get; set; } = [];
    public bool HasZeroPadding { get; set; }
    public List<byte> Padding { get; } = [];
    public int MaxH => Components.Max(c => c.H);
    public int MaxV => Components.Max(c => c.V);
    public bool Gray => Components.Count == 1;
    public static ReadOnlySpan<byte> NaturalOrder =>
    [0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,
     35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63];

    public (int Columns, int Rows) Mcus(JxlJpegScan scan)
    {
        var c = Components[scan.Components[0].Component];
        int h = scan.Components.Count > 1 ? 1 : c.H;
        int v = scan.Components.Count > 1 ? 1 : c.V;
        return ((Width * h + 8 * MaxH - 1) / (8 * MaxH), (Height * v + 8 * MaxV - 1) / (8 * MaxV));
    }

    /// <summary>Validates the marker/table/scan relationships before reconstructing any JPEG bytes.</summary>
    public void ValidateReconstruction()
    {
        if (Components.Select(c => c.Id).Distinct().Count() != Components.Count)
            throw JxlDescriptor.Corrupt("Duplicate reconstruction component identifiers.");
        bool frame = false, progressive = false;
        int quant = 0, huffman = 0, scan = 0, app = 0, comment = 0, inter = 0;
        var definedQuant = new bool[Quant.Count];
        var definedHuffman = new bool[32];
        var progression = new ushort[Components.Count * 64];
        foreach (byte marker in Markers)
        {
            switch (marker)
            {
                case 0xc0 or 0xc1 or 0xc2:
                    if (frame) throw JxlDescriptor.Corrupt("Repeated reconstruction SOF marker.");
                    frame = true; progressive = marker == 0xc2; break;
                case 0xdb:
                    while (true)
                    {
                        if (quant >= Quant.Count) throw JxlDescriptor.Corrupt("Missing reconstruction quantization table.");
                        definedQuant[quant] = true;
                        if (Quant[quant++].Last) break;
                    }
                    break;
                case 0xc4:
                    while (true)
                    {
                        if (huffman >= Huffman.Count) throw JxlDescriptor.Corrupt("Missing reconstruction Huffman table.");
                        var table = Huffman[huffman++];
                        if (table.Counts.Sum() != 0) definedHuffman[table.Slot] = true;
                        if (table.Last) break;
                    }
                    break;
                case 0xda:
                    if (!frame || scan >= Scans.Count) throw JxlDescriptor.Corrupt("Missing reconstruction frame or scan.");
                    var s = Scans[scan++];
                    if (progressive && ((s.Ss == 0 && s.Se != 0) || (s.Ss > 0 && s.Components.Count != 1)))
                        throw JxlDescriptor.Corrupt("Invalid reconstruction progressive scan.");
                    int ss = progressive ? s.Ss : 0, se = progressive ? s.Se : 63, al = progressive ? s.Al : 0;
                    int mask = progressive && s.Ah != 0 ? 1 << al : (0xffff << al) & 0xffff;
                    foreach (var c in s.Components)
                    {
                        if (!definedQuant[Components[c.Component].Quant]
                            || ss == 0 && !definedHuffman[c.DcTable] || se != 0 && !definedHuffman[16 + c.AcTable])
                            throw JxlDescriptor.Corrupt("Reconstruction scan uses an undefined table.");
                        for (int k = ss; k <= se; k++)
                        {
                            int index = c.Component * 64 + k;
                            if ((progression[index] & (mask | ((1 << al) - 1))) != 0)
                                throw JxlDescriptor.Corrupt("Overlapping or misordered reconstruction scans.");
                            progression[index] |= (ushort)mask;
                        }
                    }
                    break;
                case >= 0xe0 and <= 0xef:
                    if (app >= App.Count || App[app++][0] != marker) throw JxlDescriptor.Corrupt("Reconstruction APP marker mismatch.");
                    break;
                case 0xfe: comment++; break;
                case 255: inter++; break;
            }
        }
        if (!frame || scan != Scans.Count || quant != Quant.Count || huffman != Huffman.Count
            || app != App.Count || comment != Comments.Count || inter != InterMarker.Count)
            throw JxlDescriptor.Corrupt("Inconsistent JPEG reconstruction marker counts.");
        for (int c = 0; c < Components.Count; c++)
            if (progression[c * 64] == 0) throw JxlDescriptor.Corrupt("Missing reconstructed JPEG component DC scan.");
    }

    public void ValidateSupported(bool checkColor = true)
    {
        if (Components.Count is not (1 or 3)) throw JxlDescriptor.Unsupported("JPEG must have one or three components.");
        if (Gray)
        {
            if (Components[0].H != 1 || Components[0].V != 1) throw JxlDescriptor.Unsupported("the grayscale JPEG sampling is unsupported.");
        }
        else
        {
            if (Components[0].H is not (1 or 2) || Components[0].V is not (1 or 2) || Components[0].H < Components[0].V
                || Components.Skip(1).Any(c => c.H != 1 || c.V != 1))
                throw JxlDescriptor.Unsupported("only JPEG 4:4:4, 4:2:2 and 4:2:0 sampling is supported.");
            // SetColorTransformFromJpegData: JFIF first, then Adobe's transform, then component IDs.
            if (checkColor && !Markers.Contains(0xe0))
            {
                byte[]? adobe = App.FirstOrDefault(app => app.Length == 15 && app[0] == 0xee && app.AsSpan(3, 5).SequenceEqual("Adobe"u8));
                bool rgbIds = Components[0].Id == 'R' && Components[1].Id == 'G' && Components[2].Id == 'B';
                if (adobe is not null ? adobe[14] != 1 : rgbIds)
                    throw JxlDescriptor.Unsupported("RGB/YCCK JPEG transcoding is unsupported.");
            }
        }
    }
}

internal sealed class JxlJpegComponent
{
    public int Id { get; set; }
    public int H { get; set; } = 1;
    public int V { get; set; } = 1;
    public int Quant { get; set; }
    public int WidthBlocks { get; set; }
    public int HeightBlocks { get; set; }
    public short[] Coefficients { get; set; } = [];
}

internal sealed class JxlJpegQuant
{
    public int[] Values { get; } = new int[64];
    public int Precision { get; set; }
    public int Index { get; set; }
    public bool Last { get; set; } = true;
}

internal sealed class JxlJpegHuffman
{
    // The final symbol is libjxl's sentinel 256, not emitted in a JPEG DHT.
    public int[] Counts { get; } = new int[17];
    public int[] Values { get; } = new int[257];
    public int Slot { get; set; }
    public bool Last { get; set; } = true;
    public int[] Codes { get; } = new int[257];
    public int[] Depths { get; } = new int[257];
    private readonly int[] starts = new int[17];
    private readonly int[] offsets = new int[17];

    public void Build()
    {
        Array.Clear(Depths);
        int code = 0;
        int at = 0;
        var seen = new bool[257];
        if (Counts[0] != 0) throw JxlDescriptor.Corrupt("Invalid JPEG Huffman depth zero.");
        for (int depth = 1; depth <= 16; depth++)
        {
            starts[depth] = code;
            offsets[depth] = at;
            if (Counts[depth] < 0 || Counts[depth] > 257 - at || code + Counts[depth] > 1 << depth)
                throw JxlDescriptor.Corrupt("Invalid JPEG Huffman table.");
            for (int i = 0; i < Counts[depth]; i++)
            {
                int value = Values[at++];
                if (value is < 0 or > 256 || seen[value] || (Slot < 16 && value is >= 12 and < 256))
                    throw JxlDescriptor.Corrupt("Invalid or duplicate JPEG Huffman symbol.");
                seen[value] = true;
                Codes[value] = code++;
                Depths[value] = depth;
            }
            code <<= 1;
        }
        if (at != 0 && Values[at - 1] != 256) throw JxlDescriptor.Corrupt("Missing JPEG Huffman sentinel.");
    }

    public int Read(ref JxlJpegBits reader)
    {
        int code = 0;
        for (int depth = 1; depth <= 16; depth++)
        {
            code = (code << 1) | reader.Read(1);
            int delta = code - starts[depth];
            if ((uint)delta < (uint)Counts[depth])
            {
                int symbol = Values[offsets[depth] + delta];
                return symbol < 256 ? symbol : throw JxlDescriptor.Corrupt("JPEG entropy sentinel encountered.");
            }
        }
        throw JxlDescriptor.Corrupt("Invalid JPEG Huffman code.");
    }
}

internal sealed class JxlJpegScan
{
    public int Ss { get; set; }
    public int Se { get; set; } = 63;
    public int Ah { get; set; }
    public int Al { get; set; }
    public int LastPass { get; set; }
    public List<JxlJpegScanComponent> Components { get; } = [];
    public List<int> ResetPoints { get; } = [];
    public List<(int Block, int Count)> ExtraZeroRuns { get; } = [];
}

internal readonly record struct JxlJpegScanComponent(int Component, int DcTable, int AcTable);

// JPEG entropy bits are MSB-first; JPEG XL's bit reader is LSB-first.
internal ref struct JxlJpegBits(ReadOnlySpan<byte> source, int position)
{
    private readonly ReadOnlySpan<byte> data = source;
    public int Position { get; private set; } = position;
    private int remaining;
    private int current;

    public int Read(int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            if (remaining == 0)
            {
                if (Position >= data.Length) throw JxlDescriptor.Corrupt("Truncated JPEG entropy stream.");
                current = data[Position++];
                if (current == 255)
                {
                    if (Position >= data.Length || data[Position++] != 0) throw JxlDescriptor.Corrupt("Unexpected JPEG marker in entropy stream.");
                }
                remaining = 8;
            }
            value = (value << 1) | ((current >> --remaining) & 1);
        }
        return value;
    }

    public void Finish(JxlJpegData jpeg, Action<long> reserveMetadata)
    {
        reserveMetadata(remaining);
        while (remaining > 0)
        {
            byte bit = (byte)((current >> --remaining) & 1);
            jpeg.Padding.Add(bit);
            jpeg.HasZeroPadding |= bit == 0;
        }
    }

    public void Restart(int expected, JxlJpegData jpeg, Action<long> reserveMetadata)
    {
        Finish(jpeg, reserveMetadata);
        if (Position + 2 > data.Length || data[Position] != 255 || data[Position + 1] != 0xd0 + expected)
            throw JxlDescriptor.Corrupt("Missing or invalid JPEG restart marker.");
        Position += 2;
    }
}
