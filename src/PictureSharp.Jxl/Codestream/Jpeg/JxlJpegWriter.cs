using System.Numerics;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Jpeg;

// Scalar, buffered port of libjxl v0.12.0 lib/jxl/jpeg/dec_jpeg_data_writer.cc (BSD-3-Clause).
internal static class JxlJpegWriter
{
    public static byte[] Write(JxlJpegData jpeg, CancellationToken cancellationToken, int parallelism = JxlParallel.Sequential)
    {
        using var output = new MemoryStream();
        Marker(output, 0xd8);
        int app = 0, comment = 0, inter = 0, quant = 0, huffman = 0, scan = 0, padding = 0;
        bool progressive = false, seenDri = false;
        var tables = new JxlJpegHuffman?[32];
        foreach (byte marker in jpeg.Markers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (marker == 255) { output.Write(Get(jpeg.InterMarker, inter++)); continue; }
            if (marker is >= 0xe0 and <= 0xef) { output.WriteByte(255); output.Write(Get(jpeg.App, app++)); continue; }
            if (marker == 0xfe) { output.WriteByte(255); output.Write(Get(jpeg.Comments, comment++)); continue; }
            Marker(output, marker);
            switch (marker)
            {
                case 0xc0 or 0xc1 or 0xc2:
                    progressive = marker == 0xc2;
                    U16(output, 8 + 3 * jpeg.Components.Count);
                    output.WriteByte(8);
                    U16(output, jpeg.Height); U16(output, jpeg.Width);
                    output.WriteByte((byte)jpeg.Components.Count);
                    foreach (var c in jpeg.Components)
                    {
                        output.WriteByte((byte)c.Id); output.WriteByte((byte)((c.H << 4) | c.V));
                        output.WriteByte((byte)Get(jpeg.Quant, c.Quant).Index);
                    }
                    break;
                case 0xdb:
                    using (var segment = new MemoryStream())
                    {
                        JxlJpegQuant table;
                        do
                        {
                            table = Get(jpeg.Quant, quant++);
                            segment.WriteByte((byte)((table.Precision << 4) | table.Index));
                            foreach (byte k in JxlJpegData.NaturalOrder)
                            {
                                int value = table.Values[k];
                                if (value <= 0 || value > (table.Precision == 0 ? 255 : 65535)) throw JxlDescriptor.Corrupt("Invalid reconstructed JPEG quantization entry.");
                                if (table.Precision != 0) U16(segment, value); else segment.WriteByte((byte)value);
                            }
                        } while (!table.Last);
                        Segment(output, segment);
                    }
                    break;
                case 0xc4:
                    using (var segment = new MemoryStream())
                    {
                        JxlJpegHuffman table;
                        do
                        {
                            table = Get(jpeg.Huffman, huffman++);
                            table.Build();
                            int total = table.Counts.Sum();
                            if (total == 0) break;
                            tables[table.Slot] = table;
                            int maxDepth = Array.FindLastIndex(table.Counts, count => count > 0);
                            segment.WriteByte((byte)table.Slot);
                            for (int k = 1; k <= 16; k++) segment.WriteByte((byte)(table.Counts[k] - (k == maxDepth ? 1 : 0)));
                            for (int k = 0; k < total - 1; k++) segment.WriteByte((byte)table.Values[k]);
                        } while (!table.Last);
                        Segment(output, segment);
                    }
                    break;
                case 0xdd:
                    seenDri = true;
                    U16(output, 4); U16(output, jpeg.RestartInterval);
                    break;
                case 0xda:
                    var s = Get(jpeg.Scans, scan++);
                    U16(output, 6 + 2 * s.Components.Count);
                    output.WriteByte((byte)s.Components.Count);
                    foreach (var c in s.Components)
                    {
                        output.WriteByte((byte)Get(jpeg.Components, c.Component).Id);
                        output.WriteByte((byte)((c.DcTable << 4) | c.AcTable));
                    }
                    output.WriteByte((byte)s.Ss); output.WriteByte((byte)s.Se); output.WriteByte((byte)((s.Ah << 4) | s.Al));
                    WriteScan(jpeg, s, tables, progressive, seenDri, output, ref padding, cancellationToken, parallelism);
                    break;
                case 0xd9: output.Write(jpeg.Tail); break;
                case >= 0xd0 and <= 0xd7: break;
                default: throw JxlDescriptor.Corrupt("Invalid reconstructed JPEG marker.");
            }
        }
        if (jpeg.HasZeroPadding && padding != jpeg.Padding.Count) throw JxlDescriptor.Corrupt("Unused JPEG padding bits.");
        return output.ToArray();
    }

    private static T Get<T>(List<T> list, int index) => (uint)index < (uint)list.Count ? list[index] : throw JxlDescriptor.Corrupt("Invalid JPEG reconstruction index.");
    private static void Marker(Stream output, int marker) { output.WriteByte(255); output.WriteByte((byte)marker); }
    private static void U16(Stream output, int value) { output.WriteByte((byte)(value >> 8)); output.WriteByte((byte)value); }
    private static void Segment(Stream output, MemoryStream segment)
    {
        if (segment.Length > 65533) throw JxlDescriptor.Corrupt("JPEG marker too long.");
        U16(output, (int)segment.Length + 2); segment.WriteTo(output);
    }

    private static void WriteScan(JxlJpegData jpeg, JxlJpegScan scan, JxlJpegHuffman?[] tables, bool progressive, bool seenDri, Stream output,
        ref int padding, CancellationToken cancellationToken, int parallelism)
    {
        int ss = progressive ? scan.Ss : 0, se = progressive ? scan.Se : 63, al = progressive ? scan.Al : 0;
        int ah = progressive ? scan.Ah : 0;
        int interval = seenDri ? jpeg.RestartInterval : 0, remaining = interval, restart = 0;
        (int columns, int rows) = jpeg.Mcus(scan);
        // A scan that starts at the DC coefficient carries no state from block to block except the DC predictor (EOB
        // runs never span blocks), so chunks of MCUs (whole restart intervals, or MCU rows without them) can be coded
        // into separate bit strings and then appended with byte stuffing, padding and restart markers in order: the
        // bytes are those of the sequential loop.
        long mcus = (long)columns * rows;
        int unit = interval > 0 ? interval : Math.Max(1, columns);
        int units = (int)Math.Min(int.MaxValue, (mcus + unit - 1) / unit);
        int chunks = Math.Min(units, 4 * JxlParallel.Workers(parallelism, units));
        if (ah == 0 && ss == 0 && mcus <= int.MaxValue && JxlParallel.IsParallel(parallelism, chunks))
        {
            var parts = new ScanPart[chunks];
            try
            {
                JxlParallel.For(chunks, parallelism, cancellationToken, i =>
                    parts[i] = WriteMcus(jpeg, scan, tables, se, al, columns, interval,
                        (int)Math.Min(mcus, (long)JxlParallel.BandStart(units, chunks, i) * unit),
                        (int)Math.Min(mcus, (long)JxlParallel.BandStart(units, chunks, i + 1) * unit), cancellationToken));
            }
            catch (ImageFormatException)
            {
                // Padding is checked during the merge and may fail before this coefficient error. No scan bytes or
                // padding have been consumed yet: replay corrupt scans sequentially to report the first error.
                WriteScan(jpeg, scan, tables, progressive, seenDri, output, ref padding, cancellationToken, JxlParallel.Sequential);
                return;
            }
            var merged = new Bits(output);
            int consumedResets = 0, consumedZeros = 0;
            for (int i = 0; i < chunks; i++)
            {
                ScanPart part = parts[i];
                for (int k = 0; k < part.Bits.Segments; k++)
                {
                    // Chunks start at restart boundaries when there is an interval; every segment but the first starts one.
                    if (k > 0 || (interval > 0 && i > 0))
                    {
                        merged.Finish(jpeg, ref padding);
                        Marker(output, 0xd0 + restart); restart = (restart + 1) & 7;
                    }
                    merged.Append(part.Bits, k);
                }
                consumedResets += part.Resets; consumedZeros += part.Zeros;
            }
            merged.Finish(jpeg, ref padding);
            if (consumedResets != scan.ResetPoints.Count || consumedZeros != scan.ExtraZeroRuns.Count) throw JxlDescriptor.Corrupt("Invalid JPEG reset or zero-run position.");
            return;
        }

        int[] lastDc = new int[jpeg.Components.Count];
        var bits = new Bits(output);
        var state = new CodingState(bits);
        int block = 0, reset = 0, zeros = 0;
        for (int my = 0; my < rows; my++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int mx = 0; mx < columns; mx++)
            {
                if (interval > 0 && remaining == 0)
                {
                    state.Flush(); bits.Finish(jpeg, ref padding);
                    Marker(output, 0xd0 + restart); restart = (restart + 1) & 7;
                    remaining = interval; Array.Clear(lastDc);
                }
                remaining--;
                foreach (var sc in scan.Components)
                {
                    var c = Get(jpeg.Components, sc.Component);
                    int nx = scan.Components.Count > 1 ? c.H : 1, ny = scan.Components.Count > 1 ? c.V : 1;
                    for (int iy = 0; iy < ny; iy++)
                        for (int ix = 0; ix < nx; ix++, block++)
                        {
                            if (reset < scan.ResetPoints.Count && block == scan.ResetPoints[reset]) { state.Flush(); reset++; }
                            int extra = 0;
                            if (zeros < scan.ExtraZeroRuns.Count && block == scan.ExtraZeroRuns[zeros].Block) extra = scan.ExtraZeroRuns[zeros++].Count;
                            ReadOnlySpan<short> coefficients = c.Coefficients.AsSpan(((my * ny + iy) * c.WidthBlocks + mx * nx + ix) * 64, 64);
                            if (ah == 0) EncodeBlock(coefficients, ss, se, al, extra, tables[sc.DcTable], tables[16 + sc.AcTable], ref lastDc[sc.Component], state, bits);
                            else EncodeRefinement(coefficients, ss, se, al, tables[16 + sc.AcTable], state, bits);
                        }
                }
            }
        }
        state.Flush(); bits.Finish(jpeg, ref padding);
        if (reset != scan.ResetPoints.Count || zeros != scan.ExtraZeroRuns.Count) throw JxlDescriptor.Corrupt("Invalid JPEG reset or zero-run position.");
    }

    private readonly record struct ScanPart(Bits Bits, int Resets, int Zeros);

    /// <summary>
    /// Codes MCUs [<paramref name="start"/>, <paramref name="end"/>) of a scan with Ss = 0 and Ah = 0 into unstuffed bits,
    /// one bit string per restart interval, exactly as the sequential loop codes them: the DC predictors start at the
    /// last DC of each component before the chunk (zero at a restart), and the reset points and extra zero runs at the
    /// entries the sequential loop has consumed by then (the strictly increasing prefix below the chunk's first block).
    /// </summary>
    private static ScanPart WriteMcus(JxlJpegData jpeg, JxlJpegScan scan, JxlJpegHuffman?[] tables, int se, int al, int columns,
        int interval, int start, int end, CancellationToken cancellationToken)
    {
        int[] lastDc = new int[jpeg.Components.Count];
        int perMcu = 0;
        bool restartAtStart = interval > 0 && start % interval == 0;
        foreach (var sc in scan.Components)
        {
            var c = Get(jpeg.Components, sc.Component);
            int nx = scan.Components.Count > 1 ? c.H : 1, ny = scan.Components.Count > 1 ? c.V : 1;
            perMcu += nx * ny;
            if (start > 0 && !restartAtStart)
            {
                int my = (start - 1) / columns, mx = (start - 1) % columns;
                lastDc[sc.Component] = c.Coefficients.AsSpan(((my * ny + ny - 1) * c.WidthBlocks + mx * nx + nx - 1) * 64, 64)[0] >> al;
            }
        }
        int block = start * perMcu;
        int reset = Consumed(scan.ResetPoints.Count, i => scan.ResetPoints[i], block);
        int zeros = Consumed(scan.ExtraZeroRuns.Count, i => scan.ExtraZeroRuns[i].Block, block);
        int firstReset = reset, firstZeros = zeros;
        var bits = new Bits(null);
        var state = new CodingState(bits);
        for (int m = start; m < end; m++)
        {
            int my = m / columns, mx = m % columns;
            if (mx == 0 || m == start) cancellationToken.ThrowIfCancellationRequested();
            if (interval > 0 && m > start && m % interval == 0)
            {
                state.Flush();
                bits.EndSegment();
                Array.Clear(lastDc);
            }
            foreach (var sc in scan.Components)
            {
                var c = Get(jpeg.Components, sc.Component);
                int nx = scan.Components.Count > 1 ? c.H : 1, ny = scan.Components.Count > 1 ? c.V : 1;
                for (int iy = 0; iy < ny; iy++)
                    for (int ix = 0; ix < nx; ix++, block++)
                    {
                        if (reset < scan.ResetPoints.Count && block == scan.ResetPoints[reset]) { state.Flush(); reset++; }
                        int extra = 0;
                        if (zeros < scan.ExtraZeroRuns.Count && block == scan.ExtraZeroRuns[zeros].Block) extra = scan.ExtraZeroRuns[zeros++].Count;
                        ReadOnlySpan<short> coefficients = c.Coefficients.AsSpan(((my * ny + iy) * c.WidthBlocks + mx * nx + ix) * 64, 64);
                        EncodeBlock(coefficients, 0, se, al, extra, tables[sc.DcTable], tables[16 + sc.AcTable], ref lastDc[sc.Component], state, bits);
                    }
            }
        }
        state.Flush();
        bits.EndSegment();
        return new ScanPart(bits, reset - firstReset, zeros - firstZeros);
    }

    /// <summary>
    /// How many entries of a block position list the sequential loop has consumed before <paramref name="block"/>: it
    /// takes entry i when it reaches its block, so only a strictly increasing prefix of positions is ever consumed.
    /// </summary>
    private static int Consumed(int count, Func<int, int> position, int block)
    {
        int consumed = 0, previous = -1;
        while (consumed < count && position(consumed) > previous && position(consumed) < block) previous = position(consumed++);
        return consumed;
    }

    private static void EncodeBlock(ReadOnlySpan<short> coefficients, int ss, int se, int al, int extra,
        JxlJpegHuffman? dc, JxlJpegHuffman? ac, ref int lastDc, CodingState state, Bits bits)
    {
        bool allowEob = ss > 0;
        if (ss == 0)
        {
            int value = coefficients[0] >> al;
            int diff = value - lastDc;
            lastDc = value;
            int n = BitOperations.Log2((uint)Math.Abs(diff)) + (diff != 0 ? 1 : 0);
            bits.Symbol(dc, n); bits.Write(n, diff < 0 ? diff - 1 : diff); ss++;
        }
        if (ss > se) return;
        int run = 0;
        for (int k = ss; k <= se; k++)
        {
            int original = coefficients[JxlJpegData.NaturalOrder[k]];
            int value = Math.Abs(original) >> al;
            if (value == 0) { run++; continue; }
            state.Flush();
            while (run > 15) { bits.Symbol(ac, 0xf0); run -= 16; }
            int n = BitOperations.Log2((uint)value) + 1;
            if (n + al >= 12) throw JxlDescriptor.Corrupt("Reconstructed JPEG AC coefficient is too large.");
            bits.Symbol(ac, (run << 4) | n); bits.Write(n, original < 0 ? ~value : value);
            run = 0;
        }
        if (extra > 0) state.Flush();
        for (int i = 0; i < extra; i++) { bits.Symbol(ac, 0xf0); run -= 16; }
        if (run < 0) throw JxlDescriptor.Corrupt("Invalid JPEG extra zero runs.");
        if (run > 0)
        {
            state.Buffer(ac, []);
            if (!allowEob) state.Flush();
        }
    }

    private static void EncodeRefinement(ReadOnlySpan<short> coefficients, int ss, int se, int al, JxlJpegHuffman? ac, CodingState state, Bits bits)
    {
        bool allowEob = ss > 0;
        if (ss == 0) { bits.Write(1, (coefficients[0] >> al) & 1); ss++; }
        if (ss > se) return;
        Span<int> values = stackalloc int[64];
        int eob = 0;
        for (int k = ss; k <= se; k++)
        {
            values[k] = Math.Abs((int)coefficients[JxlJpegData.NaturalOrder[k]]) >> al;
            if (values[k] == 1) eob = k;
        }
        int run = 0;
        var refinement = new List<byte>();
        for (int k = ss; k <= se; k++)
        {
            if (values[k] == 0) { run++; continue; }
            while (run > 15 && k <= eob)
            {
                state.Flush(); bits.Symbol(ac, 0xf0); run -= 16;
                foreach (byte b in refinement) bits.Write(1, b);
                refinement.Clear();
            }
            if (values[k] > 1) { refinement.Add((byte)(values[k] & 1)); continue; }
            state.Flush(); bits.Symbol(ac, (run << 4) | 1);
            bits.Write(1, coefficients[JxlJpegData.NaturalOrder[k]] < 0 ? 0 : 1);
            foreach (byte b in refinement) bits.Write(1, b);
            refinement.Clear(); run = 0;
        }
        if (run > 0 || refinement.Count != 0)
        {
            state.Buffer(ac, refinement);
            if (!allowEob) state.Flush();
        }
    }

    private sealed class CodingState(Bits bits)
    {
        private int eob;
        private JxlJpegHuffman? table;
        private readonly List<byte> refinement = [];
        public void Flush()
        {
            if (eob > 0)
            {
                int n = BitOperations.Log2((uint)eob);
                bits.Symbol(table, n << 4); bits.Write(n, eob & ((1 << n) - 1)); eob = 0;
            }
            foreach (byte b in refinement) bits.Write(1, b);
            refinement.Clear();
        }
        public void Buffer(JxlJpegHuffman? ac, IEnumerable<byte> extra)
        {
            if (eob == 0) table = ac;
            eob++; refinement.AddRange(extra);
            if (eob == 0x7fff) Flush();
        }
    }

    // Bits accumulate in a 64-bit register and leave as bytes into a local buffer that is copied to the output at
    // every byte boundary the scan forces (restart markers and the end of the scan).
    private sealed class Bits(Stream? output)
    {
        private ulong pending;
        private int count;
        private byte[] buffer = new byte[4096];
        private int length;

        public void Symbol(JxlJpegHuffman? table, int symbol)
        {
            if (table is null || (uint)symbol >= 256 || table.Depths[symbol] == 0) throw JxlDescriptor.Corrupt("Missing reconstructed JPEG Huffman symbol.");
            Write(table.Depths[symbol], table.Codes[symbol]);
        }

        /// <summary>Writes the low <paramref name="n"/> (0–16) bits of <paramref name="value"/>, most significant first.</summary>
        public void Write(int n, int value)
        {
            pending = (pending << n) | ((uint)value & ((1u << n) - 1));
            count += n;
            if (count < 8) return;
            if (length > buffer.Length - 6) Array.Resize(ref buffer, buffer.Length * 2);
            do
            {
                count -= 8;
                byte b = (byte)(pending >> count);
                buffer[length++] = b;
                // A chunk without output collects unstuffed bits for Append.
                if (b == 255 && output is not null) buffer[length++] = 0;
            } while (count >= 8);
        }

        // Unstuffed chunks: per segment its end in the buffer and its last 0–7 bits.
        private List<(int End, int Tail, int TailBits)>? segments;

        public int Segments => segments?.Count ?? 0;

        /// <summary>Ends a segment of an unstuffed chunk; the next one starts at the next byte of the buffer.</summary>
        public void EndSegment()
        {
            (segments ??= []).Add((length, (int)(pending & ((1u << count) - 1)), count));
            pending = 0; count = 0;
        }

        /// <summary>Appends segment <paramref name="index"/> of an unstuffed chunk.</summary>
        public void Append(Bits chunk, int index)
        {
            int start = index == 0 ? 0 : chunk.segments![index - 1].End;
            (int end, int tail, int tailBits) = chunk.segments![index];
            if (count == 0)
            {
                if (length > buffer.Length - 2 * (end - start)) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + 2 * (end - start)));
                for (int i = start; i < end; i++)
                {
                    byte b = chunk.buffer[i];
                    buffer[length++] = b;
                    if (b == 255) buffer[length++] = 0;
                }
            }
            else
            {
                for (int i = start; i < end; i++) Write(8, chunk.buffer[i]);
            }
            Write(tailBits, tail);
        }

        public void Finish(JxlJpegData jpeg, ref int padding)
        {
            while (count != 0)
            {
                int bit = 1;
                if (jpeg.HasZeroPadding)
                {
                    if (padding >= jpeg.Padding.Count) throw JxlDescriptor.Corrupt("Missing reconstructed JPEG padding.");
                    bit = jpeg.Padding[padding++];
                }
                Write(1, bit);
            }
            output!.Write(buffer, 0, length);
            length = 0;
        }
    }
}
