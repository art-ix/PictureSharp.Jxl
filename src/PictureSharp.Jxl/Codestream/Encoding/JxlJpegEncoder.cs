using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;
using PictureSharp.Formats.Jxl.Codestream.Jpeg;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

// ComputeJPEGTranscodingData, libjxl v0.12.0 lib/jxl/enc_frame.cc (BSD-3-Clause).
// JPEG coefficients bypass the XYB input, transforms, quantizer search and restoration filters.
internal static class JxlJpegEncoder
{
    public static byte[] Write(JxlJpegData jpeg, JxlColorEncoding color, byte[]? icc, uint orientation, CancellationToken cancellationToken)
    {
        var metadata = new JxlImageMetadata { XybEncoded = false, Color = color, Orientation = orientation };
        var writer = new JxlBitWriter();
        JxlHeaderWriter.Signature(writer);
        JxlHeaderWriter.Size(writer, new JxlSizeHeader((uint)jpeg.Width, (uint)jpeg.Height));
        JxlHeaderWriter.ImageMetadata(writer, metadata);
        JxlHeaderWriter.DefaultTransformData(writer);
        if (icc is not null) JxlCodestreamWriter.Icc(writer, icc, cancellationToken);
        writer.ZeroPadToByte();
        int h = jpeg.MaxH == 2 ? 1 : 0, v = jpeg.MaxV == 2 ? 1 : 0;
        uint mode = h != 0 ? v != 0 ? 1u : 2u : 0u;
        var header = new JxlFrameHeader
        {
            Width = (uint)jpeg.Width, Height = (uint)jpeg.Height, ColorTransform = JxlColorTransform.YCbCr,
            ChromaModes = [0, mode, 0], Flags = JxlFrameFlags.SkipAdaptiveDcSmoothing,
            LoopFilter = new JxlLoopFilter { Gaborish = false, EpfIterations = 0 },
        };
        int[] qtable = new int[3 * 64];
        var frame = new JxlLossyFrame(header, JxlLossySettings.Create(1, 7, false)) { Jpeg = true };
        frame.Quantizer.SetJpeg();
        Array.Fill(frame.QuantField, 1);
        Array.Clear(frame.Sharpness);
        JxlAcStrategySearch.FillDct8(frame.Strategies);
        int[] map = jpeg.Gray ? [0, 0, 0] : [1, 0, 2];
        for (int c = 0; c < 3; c++)
        {
            int[] quant = jpeg.Quant[jpeg.Components[map[c]].Quant].Values;
            frame.Matrices.DcQuant[c] = (float)(Half)(128.0f * quant[0] / (8 * 255)) / 128;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) qtable[c * 64 + x * 8 + y] = quant[y * 8 + x];
        }
        for (int group = 0; group < header.Groups; group++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int x0, int y0, int width, int height) = frame.GroupRect(group);
            int area = width * height * 64;
            int[] coefficients = new int[3 * area];
            frame.Coefficients[group] = coefficients;
            for (int c = 0; c < 3; c++)
            {
                if (jpeg.Gray && c != 1) continue;
                var source = jpeg.Components[map[c]];
                int hs = header.HShift(c), vs = header.VShift(c);
                for (int by = 0; by < height; by++)
                    for (int bx = 0; bx < width; bx++)
                    {
                        if ((bx & ((1 << hs) - 1)) != 0 || (by & ((1 << vs) - 1)) != 0) continue;
                        int src = (((y0 + by) >> vs) * source.WidthBlocks + ((x0 + bx) >> hs)) * 64;
                        int dst = c * area + (by * width + bx) * 64;
                        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) coefficients[dst + x * 8 + y] = source.Coefficients[src + y * 8 + x];
                        coefficients[dst] = 0; // DC is stored separately, as integer Modular samples.
                    }
            }
        }
        var streams = new JxlModularStreamSet();
        for (int group = 0; group < header.DcGroups; group++)
        {
            (int x0, int y0, int width, int height) = frame.DcGroupRect(group);
            var dc = new JxlChannel[3];
            foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
            {
                int hs = header.HShift(c), vs = header.VShift(c);
                var channel = new JxlChannel(width >> hs, height >> vs);
                dc[c < 2 ? c ^ 1 : c] = channel;
                if (jpeg.Gray && c != 1) continue;
                var source = jpeg.Components[map[c]];
                for (int y = 0; y < channel.Height; y++) for (int x = 0; x < channel.Width; x++)
                    channel.Row(y)[x] = source.Coefficients[(((y0 >> vs) + y) * source.WidthBlocks + (x0 >> hs) + x) * 64];
            }
            streams.Add(JxlFrameHeader.VarDctDcStream(group), [.. dc], JxlTreeKind.WeightedFixedDc);
            streams.Add(header.AcMetadataStream(group), JxlVarDctEncoder.AcMetadata(frame, group), JxlTreeKind.AcMetadata);
        }
        var quantChannels = new List<JxlChannel>();
        for (int c = 0; c < 3; c++)
        {
            var channel = new JxlChannel(8, 8);
            qtable.AsSpan(c * 64, 64).CopyTo(channel.Pixels); quantChannels.Add(channel);
        }
        streams.Add(header.QuantTableStream(0), quantChannels, JxlTreeKind.Learn);
        streams.Build([0, JxlFrameHeader.VarDctDcStream(0), header.ModularDcStream(0), header.AcMetadataStream(0), header.QuantTableStream(0),
            header.ModularAcStream(0, 0), header.ModularAcStream(0, header.Groups)], cancellationToken);
        JxlVarDctEncoder.WritePrepared(writer, frame, metadata, streams, false, null, cancellationToken);
        return writer.ToArray();
    }
}
