using System.Buffers.Binary;

namespace PictureSharp.Formats.Jxl.Codestream.Jpeg;

// Scalar port of libjxl v0.12.0 lib/jxl/jpeg/enc_jpeg_data_reader.cc (BSD-3-Clause).
internal static class JxlJpegReader
{
    public static JxlJpegData Read(ReadOnlySpan<byte> data, long maxPixels, Action<long> reserveMetadata, CancellationToken cancellationToken)
    {
        if (data.Length < 2 || data[0] != 255 || data[1] != 0xd8) throw JxlDescriptor.Corrupt("Missing JPEG SOI marker.");
        var jpeg = new JxlJpegData();
        var tables = new JxlJpegHuffman?[32];
        var progression = new ushort[3 * 64];
        int pos = 2;
        bool progressive = false;
        bool dri = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = pos;
            while (pos + 1 < data.Length && (data[pos] != 255 || !ValidMarker(data[pos + 1]))) pos++;
            if (pos + 1 >= data.Length) throw JxlDescriptor.Corrupt("Truncated JPEG marker.");
            if (pos != start)
            {
                if (pos - start > 65535) throw JxlDescriptor.Unsupported("JPEG inter-marker data exceeds the reconstruction format.");
                reserveMetadata(pos - start);
                jpeg.Markers.Add(255);
                jpeg.InterMarker.Add(data[start..pos].ToArray());
            }
            int marker = data[pos + 1];
            pos += 2;
            reserveMetadata(2);
            jpeg.Markers.Add((byte)marker);
            if (jpeg.Markers.Count > 16384) throw JxlDescriptor.Corrupt("Too many JPEG markers.");
            if (marker == 0xd9) break;
            if (marker is >= 0xd0 and <= 0xd7) continue;
            if (marker is 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf or 0xcc)
                throw JxlDescriptor.Unsupported("this JPEG coding process is unsupported.");
            if (pos + 2 > data.Length) throw JxlDescriptor.Corrupt("Truncated JPEG marker length.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            if (length < 2 || length > data.Length - pos) throw JxlDescriptor.Corrupt("Invalid JPEG marker length.");
            reserveMetadata(length);
            ReadOnlySpan<byte> payload = data.Slice(pos + 2, length - 2);
            int end = pos + length;
            switch (marker)
            {
                case 0xc0 or 0xc1 or 0xc2:
                    if (jpeg.Width != 0 || payload.Length < 6) throw JxlDescriptor.Corrupt("Invalid or repeated JPEG SOF.");
                    if (payload[0] != 8) throw JxlDescriptor.Unsupported("only 8-bit JPEG precision is supported.");
                    jpeg.Height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
                    jpeg.Width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
                    if (jpeg.Width == 0 || jpeg.Height == 0) throw JxlDescriptor.Corrupt("Invalid JPEG dimensions.");
                    if ((long)jpeg.Width * jpeg.Height > maxPixels) throw JxlDescriptor.Corrupt("JPEG exceeds MaxPixels.");
                    int count = payload[5];
                    if (count is < 1 or > 4 || payload.Length != 6 + 3 * count) throw JxlDescriptor.Corrupt("Invalid JPEG component count.");
                    if (count is not (1 or 3)) throw JxlDescriptor.Unsupported("only grayscale and YCbCr JPEG are supported.");
                    for (int i = 0; i < count; i++)
                    {
                        int at = 6 + i * 3;
                        int id = payload[at];
                        int h = payload[at + 1] >> 4;
                        int v = payload[at + 1] & 15;
                        if (h == 0 || v == 0 || payload[at + 2] > 3 || jpeg.Components.Any(c => c.Id == id))
                            throw JxlDescriptor.Corrupt("Invalid JPEG component.");
                        jpeg.Components.Add(new JxlJpegComponent { Id = id, H = h, V = v, Quant = payload[at + 2] });
                    }
                    jpeg.ValidateSupported(checkColor: false);
                    foreach (var component in jpeg.Components)
                    {
                        component.WidthBlocks = ((jpeg.Width + 8 * jpeg.MaxH - 1) / (8 * jpeg.MaxH)) * component.H;
                        component.HeightBlocks = ((jpeg.Height + 8 * jpeg.MaxV - 1) / (8 * jpeg.MaxV)) * component.V;
                        long coefficients = (long)component.WidthBlocks * component.HeightBlocks * 64;
                        if (coefficients > Array.MaxLength) throw JxlDescriptor.Unsupported("JPEG coefficient array is too large.");
                        component.Coefficients = new short[(int)coefficients];
                    }
                    progressive = marker == 0xc2;
                    break;
                case 0xc4:
                    ReadHuffman(payload, jpeg, tables);
                    break;
                case 0xdb:
                    if (payload.IsEmpty) throw JxlDescriptor.Corrupt("Empty JPEG quantization marker.");
                    for (int at = 0; at < payload.Length;)
                    {
                        if (jpeg.Quant.Count == 3) throw JxlDescriptor.Unsupported("more than three JPEG quantization tables cannot be reconstructed.");
                        var table = new JxlJpegQuant { Index = payload[at] & 15, Precision = payload[at++] >> 4 };
                        if (table.Index > 3 || table.Precision > 1 || payload.Length - at < (table.Precision + 1) * 64)
                            throw JxlDescriptor.Corrupt("Invalid JPEG quantization table.");
                        for (int k = 0; k < 64; k++)
                        {
                            int value = table.Precision == 0 ? payload[at++] : BinaryPrimitives.ReadUInt16BigEndian(payload[at..]);
                            if (table.Precision != 0) at += 2;
                            if (value == 0) throw JxlDescriptor.Corrupt("Zero JPEG quantization entry.");
                            table.Values[JxlJpegData.NaturalOrder[k]] = value;
                        }
                        table.Last = at == payload.Length;
                        jpeg.Quant.Add(table);
                    }
                    break;
                case 0xdd:
                    if (payload.Length != 2) throw JxlDescriptor.Corrupt("Invalid JPEG DRI length.");
                    if (dri) throw JxlDescriptor.Unsupported("repeated JPEG DRI is unsupported.");
                    dri = true;
                    jpeg.RestartInterval = BinaryPrimitives.ReadUInt16BigEndian(payload);
                    break;
                case 0xda:
                    if (jpeg.Width == 0 || payload.Length < 4) throw JxlDescriptor.Corrupt("JPEG SOS before SOF.");
                    var scan = ReadScan(payload, jpeg, progressive, tables, progression);
                    jpeg.Scans.Add(scan);
                    pos = end;
                    DecodeScan(data, ref pos, jpeg, scan, tables, progressive, reserveMetadata, cancellationToken);
                    continue;
                case >= 0xe0 and <= 0xef:
                case 0xfe:
                    byte[] bytes = data.Slice(pos - 1, length + 1).ToArray();
                    if (marker == 0xfe) jpeg.Comments.Add(bytes);
                    else { jpeg.App.Add(bytes); jpeg.AppTypes.Add(0); }
                    break;
                default:
                    throw JxlDescriptor.Unsupported($"JPEG marker 0x{marker:X2} is unsupported.");
            }
            pos = end;
        }
        if (jpeg.Width == 0 || jpeg.Scans.Count == 0) throw JxlDescriptor.Corrupt("JPEG requires a frame and scans.");
        reserveMetadata(data.Length - pos);
        if (data.Length - pos > 4260096) throw JxlDescriptor.Unsupported("JPEG tail exceeds the reconstruction format.");
        jpeg.Tail = data[pos..].ToArray();
        foreach (var component in jpeg.Components)
        {
            int index = jpeg.Quant.FindIndex(q => q.Index == component.Quant);
            if (index < 0) throw JxlDescriptor.Corrupt("Missing JPEG quantization table.");
            component.Quant = index;
        }
        for (int i = 0; i < jpeg.Quant.Count; i++)
            if (!jpeg.Components.Any(c => c.Quant == i) && (i == 0 || !jpeg.Quant[i].Values.AsSpan().SequenceEqual(jpeg.Quant[i - 1].Values)))
                throw JxlDescriptor.Unsupported("a nontrivial unused JPEG quantization table cannot be reconstructed.");
        if (jpeg.Huffman.Count is < 2 or > 89) throw JxlDescriptor.Unsupported("the reconstruction format supports 2 to 89 JPEG Huffman table records.");
        foreach (var scan in jpeg.Scans)
            if (scan.ResetPoints.Count > 65555 || scan.ExtraZeroRuns.Count > 65555)
                throw JxlDescriptor.Unsupported("JPEG scan reconstruction positions exceed the format limit.");
        for (int i = 0; i < jpeg.Components.Count; i++)
            if (progression[i * 64] == 0) throw JxlDescriptor.Corrupt("Missing JPEG component DC scan.");
        jpeg.ValidateSupported();
        jpeg.ValidateReconstruction();
        return jpeg;
    }

    private static bool ValidMarker(byte marker) => marker is >= 0xc0 and <= 0xcf or >= 0xd0 and <= 0xdb or 0xdd or >= 0xe0 and <= 0xef or 0xfe;

    private static void ReadHuffman(ReadOnlySpan<byte> payload, JxlJpegData jpeg, JxlJpegHuffman?[] tables)
    {
        if (payload.IsEmpty) { jpeg.Huffman.Add(new JxlJpegHuffman()); return; }
        for (int at = 0; at < payload.Length;)
        {
            if (payload.Length - at < 17) throw JxlDescriptor.Corrupt("Truncated JPEG Huffman table.");
            var huff = new JxlJpegHuffman { Slot = payload[at++] };
            if ((huff.Slot & ~0x13) != 0) throw JxlDescriptor.Corrupt("Invalid JPEG Huffman slot.");
            int count = 0;
            int maxDepth = 1;
            for (int depth = 1; depth <= 16; depth++)
            {
                huff.Counts[depth] = payload[at++];
                count += huff.Counts[depth];
                if (huff.Counts[depth] > 0) maxDepth = depth;
            }
            if (count > 256 || count > payload.Length - at) throw JxlDescriptor.Corrupt("Invalid JPEG Huffman length.");
            for (int i = 0; i < count; i++) huff.Values[i] = payload[at++];
            huff.Counts[maxDepth]++;
            huff.Values[count] = 256;
            huff.Last = at == payload.Length;
            huff.Build();
            jpeg.Huffman.Add(huff);
            tables[huff.Slot] = huff;
            if (jpeg.Huffman.Count > 89) throw JxlDescriptor.Unsupported("too many JPEG Huffman tables for reconstruction.");
        }
    }

    private static JxlJpegScan ReadScan(ReadOnlySpan<byte> payload, JxlJpegData jpeg, bool progressive, JxlJpegHuffman?[] tables, ushort[] progression)
    {
        int count = payload[0];
        if (count < 1 || count > jpeg.Components.Count || payload.Length != 4 + count * 2) throw JxlDescriptor.Corrupt("Invalid JPEG SOS length.");
        var scan = new JxlJpegScan { Ss = payload[^3], Se = payload[^2], Ah = payload[^1] >> 4, Al = payload[^1] & 15 };
        if (scan.Ss > scan.Se || scan.Se > 63 || scan.Al > 10 || (scan.Ah != 0 && scan.Ah != scan.Al + 1))
            throw JxlDescriptor.Corrupt("Invalid JPEG scan parameters.");
        if (progressive && ((scan.Ss == 0 && scan.Se != 0) || (scan.Ss > 0 && count != 1)))
            throw JxlDescriptor.Corrupt("Invalid progressive JPEG scan.");
        for (int i = 0; i < count; i++)
        {
            int id = payload[1 + i * 2];
            int component = jpeg.Components.FindIndex(c => c.Id == id);
            int dc = payload[2 + i * 2] >> 4;
            int ac = payload[2 + i * 2] & 15;
            if (component < 0 || dc > 3 || ac > 3 || scan.Components.Any(c => c.Component == component)) throw JxlDescriptor.Corrupt("Invalid JPEG scan component.");
            if ((!progressive || scan.Ss == 0) && tables[dc] is null || (!progressive || scan.Se > 0) && tables[16 + ac] is null)
                throw JxlDescriptor.Corrupt("Undefined JPEG Huffman table.");
            scan.Components.Add(new(component, dc, ac));
            int first = progressive ? scan.Ss : 0;
            int last = progressive ? scan.Se : 63;
            int al = progressive ? scan.Al : 0;
            int mask = progressive && scan.Ah > 0 ? 1 << al : (0xffff << al) & 0xffff;
            for (int k = first; k <= last; k++)
            {
                int at = component * 64 + k;
                if ((progression[at] & (mask | ((1 << al) - 1))) != 0) throw JxlDescriptor.Corrupt("Overlapping or misordered JPEG scans.");
                progression[at] |= (ushort)mask;
            }
        }
        return scan;
    }

    private static void DecodeScan(ReadOnlySpan<byte> data, ref int pos, JxlJpegData jpeg, JxlJpegScan scan, JxlJpegHuffman?[] tables, bool progressive, Action<long> reserveMetadata, CancellationToken cancellationToken)
    {
        var bits = new JxlJpegBits(data, pos);
        (int columns, int rows) = jpeg.Mcus(scan);
        int[] lastDc = new int[jpeg.Components.Count];
        int remaining = jpeg.RestartInterval;
        int restart = 0;
        int eob = -1;
        int block = 0;
        int ss = progressive ? scan.Ss : 0;
        int se = progressive ? scan.Se : 63;
        int al = progressive ? scan.Al : 0;
        for (int my = 0; my < rows; my++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int mx = 0; mx < columns; mx++)
            {
                if (jpeg.RestartInterval > 0 && remaining == 0)
                {
                    if (eob > 0) throw JxlDescriptor.Corrupt("JPEG EOB crosses restart.");
                    bits.Restart(restart, jpeg, reserveMetadata);
                    restart = (restart + 1) & 7;
                    Array.Clear(lastDc);
                    eob = -1;
                    remaining = jpeg.RestartInterval;
                }
                remaining--;
                foreach (var sc in scan.Components)
                {
                    var c = jpeg.Components[sc.Component];
                    int ny = scan.Components.Count > 1 ? c.V : 1;
                    int nx = scan.Components.Count > 1 ? c.H : 1;
                    for (int iy = 0; iy < ny; iy++)
                        for (int ix = 0; ix < nx; ix++, block++)
                        {
                            Span<short> coefficients = c.Coefficients.AsSpan(((my * ny + iy) * c.WidthBlocks + mx * nx + ix) * 64, 64);
                            bool reset = false;
                            int zeroRuns = 0;
                            if (!progressive || scan.Ah == 0)
                                DecodeBlock(coefficients, ss, se, al, tables[sc.DcTable], tables[16 + sc.AcTable], ref lastDc[sc.Component], ref eob, ref reset, ref zeroRuns, ref bits);
                            else RefineBlock(coefficients, ss, se, al, tables[16 + sc.AcTable], ref eob, ref reset, ref bits);
                            if (reset) { reserveMetadata(sizeof(int)); scan.ResetPoints.Add(block); }
                            if (zeroRuns > 0) { reserveMetadata(2 * sizeof(int)); scan.ExtraZeroRuns.Add((block, zeroRuns)); }
                        }
                }
            }
        }
        if (eob > 0) throw JxlDescriptor.Corrupt("JPEG EOB exceeds scan.");
        bits.Finish(jpeg, reserveMetadata);
        pos = bits.Position;
    }

    private static int Symbol(JxlJpegHuffman? table, ref JxlJpegBits bits) => (table ?? throw JxlDescriptor.Corrupt("Missing JPEG Huffman table.")).Read(ref bits);
    private static int Extend(int value, int n) => n == 0 || value >= 1 << (n - 1) ? value : value - (1 << n) + 1;
    private static short Coefficient(int value) => value is >= short.MinValue and <= short.MaxValue ? (short)value : throw JxlDescriptor.Corrupt("JPEG coefficient overflow.");

    private static void DecodeBlock(Span<short> coefficients, int ss, int se, int al, JxlJpegHuffman? dc, JxlJpegHuffman? ac,
        ref int lastDc, ref int eob, ref bool reset, ref int zeroRuns, ref JxlJpegBits bits)
    {
        bool allowEob = ss > 0;
        if (ss == 0)
        {
            int n = Symbol(dc, ref bits);
            if (n >= 12) throw JxlDescriptor.Corrupt("Invalid JPEG DC symbol.");
            lastDc += Extend(bits.Read(n), n);
            coefficients[0] = Coefficient(lastDc << al);
            ss++;
        }
        if (ss > se) return;
        if (eob > 0) { eob--; return; }
        for (int k = ss; k <= se; k++)
        {
            int symbol = Symbol(ac, ref bits);
            int run = symbol >> 4;
            int n = symbol & 15;
            if (n > 0)
            {
                k += run;
                if (k > se || n + al >= 12) throw JxlDescriptor.Corrupt("Invalid JPEG AC coefficient.");
                coefficients[JxlJpegData.NaturalOrder[k]] = Coefficient(Extend(bits.Read(n), n) << al);
                zeroRuns = 0;
            }
            else if (run == 15) { k += 15; zeroRuns++; }
            else
            {
                if (allowEob && k == ss && eob == 0) reset = true;
                if (run > 0 && !allowEob) throw JxlDescriptor.Corrupt("JPEG EOB crosses DC.");
                eob = (1 << run) + bits.Read(run);
                break;
            }
        }
        eob--;
    }

    private static void RefineBlock(Span<short> coefficients, int ss, int se, int al, JxlJpegHuffman? ac, ref int eob, ref bool reset, ref JxlJpegBits bits)
    {
        int step = 1 << al;
        bool allowEob = ss > 0;
        if (ss == 0) { coefficients[0] |= (short)(bits.Read(1) * step); ss++; }
        if (ss > se) return;
        int k = ss;
        bool zeroRun = false;
        if (eob <= 0)
        {
            for (; k <= se; k++)
            {
                int symbol = Symbol(ac, ref bits);
                int run = symbol >> 4;
                int value = symbol & 15;
                if (value != 0)
                {
                    if (value != 1) throw JxlDescriptor.Corrupt("Invalid JPEG refinement symbol.");
                    value = bits.Read(1) != 0 ? step : -step;
                    zeroRun = false;
                }
                else if (run != 15)
                {
                    if (allowEob && k == ss && eob == 0) reset = true;
                    if (run > 0 && !allowEob) throw JxlDescriptor.Corrupt("JPEG EOB crosses DC.");
                    eob = (1 << run) + bits.Read(run);
                    break;
                }
                else zeroRun = true;
                do
                {
                    int index = JxlJpegData.NaturalOrder[k];
                    if (coefficients[index] != 0) RefineCoefficient(coefficients, index, step, ref bits);
                    else if (--run < 0) break;
                    k++;
                } while (k <= se);
                if (value != 0)
                {
                    if (k > se) throw JxlDescriptor.Corrupt("JPEG refinement outside band.");
                    coefficients[JxlJpegData.NaturalOrder[k]] = (short)value;
                }
            }
        }
        if (zeroRun) throw JxlDescriptor.Corrupt("Extra JPEG zero run before EOB.");
        if (eob > 0)
            for (; k <= se; k++)
            {
                int index = JxlJpegData.NaturalOrder[k];
                if (coefficients[index] != 0) RefineCoefficient(coefficients, index, step, ref bits);
            }
        eob--;
    }

    private static void RefineCoefficient(Span<short> coefficients, int index, int step, ref JxlJpegBits bits)
    {
        int value = coefficients[index];
        if (bits.Read(1) != 0 && (value & step) == 0) coefficients[index] = Coefficient(value + (value >= 0 ? step : -step));
    }
}
