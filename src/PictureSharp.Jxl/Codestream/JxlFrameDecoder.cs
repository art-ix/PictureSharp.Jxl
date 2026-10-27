using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Codestream;

/// <summary>
/// The rendered samples of a still image, nominally in [0, 1]: one (gray) or three colour planes in the image's
/// colour space and an optional alpha plane.
/// </summary>
internal sealed record JxlDecodedImage(int Width, int Height, JxlPlane[] Color, JxlPlane? Alpha);

/// <summary>
/// Decodes a still JPEG XL image of one frame: skips the ICC stream and a preview frame, reads the frame sections
/// (LF global, LF groups, HF global, pass groups) of a VarDCT or Modular frame, then runs the render pipeline.
/// Ported from libjxl v0.12.0 lib/jxl/dec_frame.cc, lib/jxl/dec_modular.cc, lib/jxl/dec_cache.cc
/// (PreparePipeline) and lib/jxl/icc_codec.cc (BSD-3-Clause). Valid features outside this subset throw
/// <see cref="NotSupportedException"/>.
/// </summary>
internal static class JxlFrameDecoder
{
    private const int IccContexts = 41;

    /// <summary>The headers of every frame after the preview, in codestream order, up to the last one.</summary>
    public static List<JxlFrameHeader> ReadFrameHeaders(JxlCodestream codestream, JxlHeaders headers)
    {
        var frames = new List<JxlFrameHeader>();
        var reader = FirstFrame(codestream, headers);
        while (frames.Count == 0 || !frames[^1].IsLast)
        {
            JxlFrameHeader frame = JxlFrameHeader.Read(ref reader, headers, preview: false);
            long start = ReadToc(ref reader, codestream, frame, out JxlToc toc);
            frames.Add(frame);
            reader = new JxlBitReader(codestream);
            reader.SkipBits((ulong)(start + toc.TotalSize) * 8);
        }

        return frames;
    }

    public static JxlDecodedImage Decode(JxlCodestream codestream, JxlHeaders headers, CancellationToken cancellationToken = default)
    {
        JxlImageMetadata metadata = headers.Metadata;
        int alpha = EnsureSupported(metadata);
        var reader = FirstFrame(codestream, headers);
        // Reference frames (patch sources) and DC frames come first; the last frame is the image.
        var references = new JxlReferenceFrame?[4];
        var dcFrames = new JxlPlane[]?[4];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JxlFrameHeader frame = JxlFrameHeader.Read(ref reader, headers, preview: false);
            EnsureSupported(frame, headers, dcFrames);
            long start = ReadToc(ref reader, codestream, frame, out JxlToc toc);
            // A reference frame saved after the colour transform can only serve blending, which is not supported.
            if (frame.Type != JxlFrameType.ReferenceOnly || frame.SaveBeforeColorTransform)
            {
                var decoder = new FrameState(frame, headers, references, dcFrames, cancellationToken);
                DecodeSections(codestream, frame, start, toc, decoder);
                if (frame.Type == JxlFrameType.Regular)
                {
                    return decoder.Finish(alpha);
                }

                JxlReferenceFrame saved = decoder.Store();
                if (frame.Type == JxlFrameType.Dc)
                {
                    dcFrames[frame.DcLevel - 1] = saved.Color;
                }
                else
                {
                    references[frame.SaveAsReference] = saved;
                }
            }

            reader = new JxlBitReader(codestream);
            reader.SkipBits((ulong)(start + toc.TotalSize) * 8);
        }
    }

    private static void DecodeSections(JxlCodestream codestream, JxlFrameHeader frame, long start, JxlToc toc, FrameState decoder)
    {
        if (toc.Sizes.Length == 1)
        {
            var section = new JxlBitReader(codestream.Slice(start, toc.Sizes[0]));
            decoder.DecodeLfGlobal(ref section);
            decoder.DecodeLfGroup(ref section, 0);
            decoder.FinishLf();
            decoder.DecodeHfGlobal(ref section);
            int[]? coefficients = decoder.BeginGroup(0);
            decoder.DecodePassGroup(ref section, 0, 0, coefficients);
            decoder.FinishGroup(0, coefficients);
            return;
        }

        JxlBitReader Section(long index) => new(codestream.Slice(start + toc.Offsets[index], toc.Sizes[index]));
        var global = Section(0);
        decoder.DecodeLfGlobal(ref global);
        for (int group = 0; group < frame.DcGroups; group++)
        {
            var section = Section(1 + group);
            decoder.DecodeLfGroup(ref section, group);
        }

        decoder.FinishLf();
        var hfGlobal = Section(1 + frame.DcGroups);
        decoder.DecodeHfGlobal(ref hfGlobal);
        for (int group = 0; group < frame.Groups; group++)
        {
            int[]? coefficients = decoder.BeginGroup(group);
            for (int pass = 0; pass < frame.Passes.Count; pass++)
            {
                var section = Section(2 + frame.DcGroups + pass * frame.Groups + group);
                decoder.DecodePassGroup(ref section, pass, group, coefficients);
            }

            decoder.FinishGroup(group, coefficients);
        }
    }

    /// <summary>A reader at the first frame after the headers, the ICC stream and a preview frame.</summary>
    private static JxlBitReader FirstFrame(JxlCodestream codestream, JxlHeaders headers)
    {
        var reader = new JxlBitReader(codestream);
        reader.SkipBits((ulong)headers.EndBitPosition);
        if (headers.IccSize is { } iccSize)
        {
            SkipIcc(ref reader, iccSize);
            reader.JumpToByteBoundary();
        }

        if (headers.Metadata.Preview is not null)
        {
            JxlFrameHeader preview = JxlFrameHeader.Read(ref reader, headers, preview: true);
            long previewStart = ReadToc(ref reader, codestream, preview, out JxlToc previewToc);
            reader = new JxlBitReader(codestream);
            reader.SkipBits((ulong)(previewStart + previewToc.TotalSize) * 8);
        }

        return reader;
    }

    /// <summary>Checks the image-level features and returns the index of the alpha extra channel, or -1.</summary>
    private static int EnsureSupported(JxlImageMetadata metadata)
    {
        if (metadata.Animation is not null)
        {
            throw JxlDescriptor.Unsupported("animations are not decoded yet.");
        }

        if (metadata.BitDepth.FloatingPoint && !metadata.XybEncoded)
        {
            throw JxlDescriptor.Unsupported("floating-point samples are not decoded yet.");
        }

        int alpha = -1;
        for (int i = 0; i < metadata.ExtraChannels.Length; i++)
        {
            JxlExtraChannelInfo channel = metadata.ExtraChannels[i];
            switch (channel.Type)
            {
                case JxlExtraChannelType.Black or JxlExtraChannelType.SpotColor:
                    throw JxlDescriptor.Unsupported($"{channel.Type} extra channels are not decoded yet.");
                case JxlExtraChannelType.Alpha when alpha < 0:
                    if (channel.BitDepth.FloatingPoint || channel.AlphaAssociated)
                    {
                        throw JxlDescriptor.Unsupported("float or premultiplied alpha is not decoded yet.");
                    }

                    alpha = i;
                    break;
            }

            if (channel.BitDepth.FloatingPoint)
            {
                throw JxlDescriptor.Unsupported("floating-point extra channels are not decoded yet.");
            }
        }

        return alpha;
    }

    private static void EnsureSupported(JxlFrameHeader frame, JxlHeaders headers, JxlPlane[]?[] dcFrames)
    {
        if (frame.Type == JxlFrameType.SkipProgressive || (frame.Type == JxlFrameType.Regular && !frame.IsLast))
        {
            throw JxlDescriptor.Unsupported("images of several displayed frames (layers) are not decoded yet.");
        }

        if (frame.Type != JxlFrameType.Regular && frame.IsLast)
        {
            throw JxlDescriptor.Corrupt("The last frame is not a regular frame.");
        }

        if ((frame.Flags & (JxlFrameFlags.Noise | JxlFrameFlags.Splines)) != 0)
        {
            throw JxlDescriptor.Unsupported("noise and splines are not rendered yet.");
        }

        if ((frame.Flags & JxlFrameFlags.Patches) != 0 && frame.Upsampling != 1
            && frame.ExtraChannelUpsampling.Any(factor => factor != frame.Upsampling))
        {
            throw JxlDescriptor.Corrupt("Patches need extra channels upsampled like the colour channels.");
        }

        if ((frame.Flags & JxlFrameFlags.UseDcFrame) != 0 && (frame.DcLevel >= 4 || dcFrames[frame.DcLevel] is null))
        {
            throw JxlDescriptor.Corrupt("A frame uses a DC frame that was not decoded.");
        }

        if (frame.Type == JxlFrameType.Regular
            && (frame.X0 != 0 || frame.Y0 != 0 || frame.Width != headers.Size.Width || frame.Height != headers.Size.Height
                || frame.Blending.Mode != JxlBlendMode.Replace || frame.ExtraChannelBlending.Any(b => b.Mode != JxlBlendMode.Replace)))
        {
            throw JxlDescriptor.Unsupported("cropped or blended frames are not decoded yet.");
        }

        if (frame.Encoding == JxlFrameEncoding.VarDct && !frame.Is444 && (frame.Flags & JxlFrameFlags.SkipAdaptiveDcSmoothing) == 0)
        {
            throw JxlDescriptor.Corrupt("Chroma subsampling requires adaptive DC smoothing to be disabled.");
        }
    }

    /// <summary>Reads the TOC and returns the byte offset of the first section; the sections must be present.</summary>
    private static long ReadToc(ref JxlBitReader reader, JxlCodestream codestream, JxlFrameHeader frame, out JxlToc toc)
    {
        toc = JxlToc.Read(ref reader, frame.TocEntries);
        long start = reader.BitPosition / 8;
        if (toc.TotalSize > codestream.Length - start)
        {
            throw JxlDescriptor.Corrupt("Truncated codestream.");
        }

        return start;
    }

    /// <summary>Decodes the entropy-coded ICC stream only to find where the frames start.</summary>
    private static void SkipIcc(ref JxlBitReader reader, ulong size)
    {
        long begin = reader.BitPosition;
        JxlEntropyCode code = JxlEntropyCode.Read(ref reader, IccContexts);
        using var symbols = new JxlSymbolReader(code, ref reader);
        byte previous1 = 0;
        byte previous2 = 0;
        for (ulong i = 0; i < size; i++)
        {
            // libjxl's guard against streams that produce many bytes from few bits.
            if (i > 0 && (i & 0xFFFF) == 0 && i > (ulong)(reader.BitPosition - begin) / 8 * 256)
            {
                throw JxlDescriptor.Corrupt("Corrupt ICC profile stream.");
            }

            int context = i <= 128 ? 0 : 1 + ByteKind1(previous1) + ByteKind2(previous2) * 8;
            byte value = (byte)symbols.Read(context, ref reader);
            (previous2, previous1) = (previous1, value);
        }

        if (!symbols.IsFinalStateValid)
        {
            throw JxlDescriptor.Corrupt("Corrupt ICC profile stream.");
        }
    }

    private static int ByteKind1(byte b) => b switch
    {
        >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' => 0,
        >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)',' => 1,
        0 => 2,
        1 => 3,
        < 16 => 4,
        255 => 6,
        > 240 => 5,
        _ => 7,
    };

    private static int ByteKind2(byte b) => b switch
    {
        >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' => 0,
        >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)',' => 1,
        < 16 => 2,
        > 240 => 3,
        _ => 4,
    };

    /// <summary>
    /// The decoding state of a frame: the VarDCT part (if any), the full Modular image (colour for Modular frames,
    /// extra channels always) and the global tree, filled section by section.
    /// </summary>
    private sealed class FrameState
    {
        private readonly JxlFrameHeader frame;
        private readonly JxlImageMetadata metadata;
        private readonly JxlHeaders headers;
        private readonly CancellationToken cancellationToken;
        private readonly JxlQuantWeights matrices = new();
        private readonly JxlVarDctFrame? varDct;
        private readonly int colorChannels;
        private readonly int codedChannels;
        private JxlModularImage image = null!;
        private JxlModularTree? tree;
        private readonly JxlReferenceFrame?[] references;
        private readonly JxlPlane[]?[] dcFrames;
        private JxlGroupHeader header = JxlGroupHeader.Default;
        private JxlPatch[] patches = [];

        public FrameState(JxlFrameHeader frame, JxlHeaders headers, JxlReferenceFrame?[] references, JxlPlane[]?[] dcFrames,
            CancellationToken cancellationToken)
        {
            this.frame = frame;
            this.references = references;
            this.dcFrames = dcFrames;
            this.headers = headers;
            this.cancellationToken = cancellationToken;
            metadata = headers.Metadata;
            bool gray = metadata.Color.ColorSpace == JxlColorSpace.Gray && frame.ColorTransform == JxlColorTransform.None;
            codedChannels = gray ? 1 : 3;
            if (frame.Encoding == JxlFrameEncoding.VarDct)
            {
                varDct = new JxlVarDctFrame(frame, metadata, matrices, headers.TransformData.EffectiveQuantBiases);
            }
            else
            {
                colorChannels = codedChannels;
            }
        }

        private int Width => (int)frame.XSize;

        private int Height => (int)frame.YSize;

        public void DecodeLfGlobal(ref JxlBitReader reader)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((frame.Flags & JxlFrameFlags.Patches) != 0)
            {
                patches = JxlPatches.Read(ref reader, frame.XSizePadded, frame.YSizePadded, metadata.ExtraChannels.Length, references);
            }

            matrices.ReadDc(ref reader);
            varDct?.ReadGlobal(ref reader);
            if (varDct is not null && (frame.Flags & JxlFrameFlags.UseDcFrame) != 0)
            {
                varDct.UseDcFrame(dcFrames[frame.DcLevel]!);
            }

            int extra = metadata.ExtraChannels.Length;
            if (reader.ReadBool())
            {
                long limit = Math.Min(1 << 22, 1024 + (long)Width * Height * (codedChannels + extra) / 16);
                JxlMaTree global = JxlMaTree.Read(ref reader, limit);
                tree = new JxlModularTree(global, JxlEntropyCode.Read(ref reader, global.Leaves));
            }

            var channels = new List<JxlChannel>();
            for (int c = 0; c < colorChannels; c++)
            {
                int hShift = frame.ColorTransform == JxlColorTransform.YCbCr ? frame.HShift(c) : 0;
                int vShift = frame.ColorTransform == JxlColorTransform.YCbCr ? frame.VShift(c) : 0;
                channels.Add(new JxlChannel(DivCeil(Width, 1 << hShift), DivCeil(Height, 1 << vShift), hShift, vShift));
            }

            for (int i = 0; i < extra; i++)
            {
                uint factor = frame.ExtraChannelUpsampling[i];
                int shift = CeilLog2(factor) - CeilLog2(frame.Upsampling);
                channels.Add(new JxlChannel((int)((frame.Width + factor - 1) / factor), (int)((frame.Height + factor - 1) / factor), shift, shift));
            }

            image = new JxlModularImage((int)metadata.BitDepth.BitsPerSample, channels);
            header = JxlModularStream.Decode(ref reader, image, 0, tree, undoTransforms: false, frame.GroupDimension, frame.GroupDimension,
                cancellationToken);
        }

        public void DecodeLfGroup(ref JxlBitReader reader, int group)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((frame.Flags & JxlFrameFlags.UseDcFrame) == 0)
            {
                varDct?.DecodeDc(ref reader, group, tree);
            }

            int dimension = frame.GroupDimension * JxlFrameHeader.BlockDim;
            int x = (int)(group % frame.XDcGroups) * dimension;
            int y = (int)(group / frame.XDcGroups) * dimension;
            DecodeGroup(ref reader, x, y, dimension, 3, 1000, (int)(1 + frame.DcGroups + group));
            varDct?.DecodeAcMetadata(ref reader, group, tree);
        }

        public void FinishLf() => varDct?.SmoothDc();

        public void DecodeHfGlobal(ref JxlBitReader reader)
        {
            cancellationToken.ThrowIfCancellationRequested();
            varDct?.ReadHfGlobal(ref reader, ReadRawQuantTable);
        }

        // A raw quantisation table: a three-channel Modular image with its own stream id.
        private int[][] ReadRawQuantTable(ref JxlBitReader reader, int index, int width, int height)
        {
            var table = new JxlModularImage(8, [new JxlChannel(width, height), new JxlChannel(width, height), new JxlChannel(width, height)]);
            JxlModularStream.Decode(ref reader, table, (int)(1 + 3 * frame.DcGroups + index), tree, undoTransforms: true,
                cancellationToken: cancellationToken);
            return [.. table.Channels.Select(channel => channel.Pixels)];
        }

        public int[]? BeginGroup(int group) => varDct?.NewGroupCoefficients(group);

        public void DecodePassGroup(ref JxlBitReader reader, int pass, int group, int[]? coefficients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            varDct?.DecodePass(ref reader, group, pass, coefficients!);
            int dimension = frame.GroupDimension;
            int x = (int)(group % frame.XGroups) * dimension;
            int y = (int)(group / frame.XGroups) * dimension;
            (int minShift, int maxShift) = frame.Passes.DownsamplingBracket(pass);
            DecodeGroup(ref reader, x, y, dimension, minShift, maxShift,
                (int)(1 + 3 * frame.DcGroups + JxlQuantWeights.Tables + frame.Groups * pass + group));
        }

        public void FinishGroup(int group, int[]? coefficients)
        {
            if (varDct is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                varDct.RenderGroup(group, coefficients!);
            }
        }

        /// <summary>Decodes the part of the full image's large channels inside a group rectangle (DecodeGroup).</summary>
        private void DecodeGroup(ref JxlBitReader reader, int x0, int y0, int size, int minShift, int maxShift, int stream)
        {
            var channels = new List<JxlChannel>();
            var targets = new List<(JxlChannel Channel, int X, int Y)>();
            for (int c = JxlModularStream.DecodableChannels(image, frame.GroupDimension); c < image.Channels.Count; c++)
            {
                JxlChannel full = image.Channels[c];
                int shift = Math.Min(full.HShift, full.VShift);
                if (shift > maxShift || shift < minShift)
                {
                    continue;
                }

                int hShift = Math.Clamp(full.HShift, 0, 31);
                int vShift = Math.Clamp(full.VShift, 0, 31);
                int x = x0 >> hShift;
                int y = y0 >> vShift;
                int width = ClampedSize(x, size >> hShift, full.Width);
                int height = ClampedSize(y, size >> vShift, full.Height);
                if (width == 0 || height == 0)
                {
                    continue;
                }

                channels.Add(new JxlChannel(width, height, full.HShift, full.VShift));
                targets.Add((full, x, y));
            }

            if (channels.Count == 0)
            {
                return;
            }

            var group = new JxlModularImage(image.BitDepth, channels);
            JxlModularStream.Decode(ref reader, group, stream, tree, undoTransforms: true, cancellationToken: cancellationToken);
            for (int i = 0; i < targets.Count; i++)
            {
                (JxlChannel full, int x, int y) = targets[i];
                JxlChannel decoded = group.Channels[i];
                for (int row = 0; row < decoded.Height; row++)
                {
                    decoded.Row(row).CopyTo(full.Row(y + row)[x..]);
                }
            }
        }

        public JxlDecodedImage Finish(int alpha)
        {
            (JxlPlane[] color, JxlPlane[] extra) = Render(colorTransform: true);
            return new JxlDecodedImage((int)frame.Width, (int)frame.Height, color, alpha < 0 ? null : extra[alpha]);
        }

        /// <summary>The frame as saved for later frames: before the colour transform (save_before_color_transform).</summary>
        public JxlReferenceFrame Store()
        {
            (JxlPlane[] color, JxlPlane[] extra) = Render(colorTransform: false);
            return new JxlReferenceFrame(color, extra);
        }

        private (JxlPlane[] Color, JxlPlane[] Extra) Render(bool colorTransform)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image.UndoTransforms(header.Weighted);
            List<JxlChannel> channels = image.Channels;
            int extra = metadata.ExtraChannels.Length;
            if (channels.Count != colorChannels + extra || image.MetaChannels != 0)
            {
                throw JxlDescriptor.Corrupt("The Modular image does not match the frame.");
            }

            for (int c = 0; c < channels.Count; c++)
            {
                (int width, int height) = c < colorChannels
                    ? (DivCeil(Width, 1 << channels[c].HShift), DivCeil(Height, 1 << channels[c].VShift))
                    : ((int)DivCeil(frame.Width, frame.ExtraChannelUpsampling[c - colorChannels]),
                        (int)DivCeil(frame.Height, frame.ExtraChannelUpsampling[c - colorChannels]));
                if (channels[c].Width != width || channels[c].Height != height)
                {
                    throw JxlDescriptor.Corrupt("The Modular image does not match the frame.");
                }
            }

            JxlPlane[] color = varDct?.Planes ?? ModularColor(channels);
            JxlPlane[] extraPlanes = new JxlPlane[extra];
            for (int i = 0; i < extra; i++)
            {
                extraPlanes[i] = ToPlane(channels[colorChannels + i], (int)metadata.ExtraChannels[i].BitDepth.BitsPerSample);
            }

            color = RenderStages(color, extraPlanes, colorTransform);
            return (color, extraPlanes);
        }

        // Colour channels of a Modular frame as floats (ModularImageToDecodedRect): XYB is coded as Y, X, B - Y.
        private JxlPlane[] ModularColor(List<JxlChannel> channels)
        {
            if (frame.ColorTransform != JxlColorTransform.Xyb)
            {
                int bits = (int)metadata.BitDepth.BitsPerSample;
                return [.. channels.Take(colorChannels).Select(channel => ToPlane(channel, bits))];
            }

            float[] factors = matrices.DcQuant;
            var planes = new JxlPlane[3];
            for (int c = 0; c < 3; c++)
            {
                JxlChannel source = channels[c < 2 ? 1 - c : 2];
                var plane = new JxlPlane(source.Width, source.Height);
                for (int y = 0; y < source.Height; y++)
                {
                    Span<int> row = source.Row(y);
                    Span<int> luma = channels[0].Row(y);
                    Span<float> output = plane.Row(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        output[x] = (c == 2 ? row[x] + luma[x] : row[x]) * factors[c];
                    }
                }

                planes[c] = plane;
            }

            return planes;
        }

        // Integer samples of the given depth scaled to [0, 1] as libjxl does: in single precision below 23 bits.
        private static JxlPlane ToPlane(JxlChannel channel, int bits)
        {
            var plane = new JxlPlane(channel.Width, channel.Height);
            double factor = 1.0 / ((1u << bits) - 1);
            float single = (float)factor;
            for (int y = 0; y < channel.Height; y++)
            {
                Span<int> row = channel.Row(y);
                Span<float> output = plane.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    output[x] = bits < 23 ? row[x] * single : (float)(row[x] * factor);
                }
            }

            return plane;
        }

        /// <summary>The render pipeline stages of PreparePipeline that this decoder supports, in libjxl's order.</summary>
        private JxlPlane[] RenderStages(JxlPlane[] color, JxlPlane[] extra, bool colorTransform)
        {
            JxlLoopFilter filter = frame.LoopFilter;
            bool restore = filter.Gaborish || filter.EpfIterations > 0;
            if (color.Length == 1 && (restore || frame.Upsampling != 1 || patches.Length > 0 || !colorTransform))
            {
                color = [color[0], Copy(color[0]), Copy(color[0])];
            }

            if (frame.ColorTransform == JxlColorTransform.YCbCr)
            {
                for (int c = 0; c < color.Length; c++)
                {
                    if (frame.HShift(c) != 0)
                    {
                        color[c] = JxlRenderPipeline.UpsampleChromaHorizontally(color[c], Width);
                    }

                    if (frame.VShift(c) != 0)
                    {
                        color[c] = JxlRenderPipeline.UpsampleChromaVertically(color[c], Height);
                    }
                }
            }

            // Each filter writes into the planes the previous one read.
            JxlPlane[]? spare = null;
            if (filter.Gaborish)
            {
                (color, spare) = (JxlRenderPipeline.Gaborish(color, filter, spare), color);
            }

            if (filter.EpfIterations > 0)
            {
                int xBlocks = (int)frame.XSizeBlocks;
                float[] sigma = varDct is not null
                    ? JxlRenderPipeline.Sigma(filter, xBlocks, (int)frame.YSizeBlocks, varDct.QuantScale, varDct.QuantField, varDct.Sharpness,
                        varDct.Strategies)
                    : Enumerable.Repeat(JxlRenderPipeline.InvSigmaNum / filter.SigmaForModular, xBlocks * (int)frame.YSizeBlocks).ToArray();
                if (filter.EpfIterations >= 3)
                {
                    (color, spare) = (JxlRenderPipeline.Epf0(color, filter, sigma, xBlocks, spare), color);
                }

                (color, spare) = (JxlRenderPipeline.Epf1(color, filter, sigma, xBlocks, spare), color);
                if (filter.EpfIterations >= 2)
                {
                    (color, spare) = (JxlRenderPipeline.Epf2(color, filter, sigma, xBlocks, spare), color);
                }
            }

            int width = (int)frame.Width;
            int height = (int)frame.Height;
            bool lateExtraUpsampling = frame.Upsampling != 1 && frame.ExtraChannelUpsampling.All(factor => factor == frame.Upsampling);
            for (int i = 0; i < extra.Length; i++)
            {
                uint factor = frame.ExtraChannelUpsampling[i];
                if (factor != 1 && !lateExtraUpsampling)
                {
                    extra[i] = Upsample(extra[i], factor, width, height);
                }
            }

            if (patches.Length > 0)
            {
                JxlPatches.Apply(patches, color, extra, references, metadata.ExtraChannels);
            }

            if (frame.Upsampling != 1)
            {
                for (int c = 0; c < color.Length; c++)
                {
                    color[c] = Upsample(color[c], frame.Upsampling, width, height);
                }

                for (int i = 0; lateExtraUpsampling && i < extra.Length; i++)
                {
                    extra[i] = Upsample(extra[i], frame.Upsampling, width, height);
                }
            }

            if (colorTransform && color.Length == 3)
            {
                new JxlColorOutput(metadata, headers.TransformData, frame.ColorTransform).Apply(color);
            }

            return color;
        }

        private JxlPlane Upsample(JxlPlane plane, uint factor, int width, int height)
        {
            int shift = CeilLog2(factor);
            return JxlRenderPipeline.Upsample(plane, shift, headers.TransformData.UpsamplingWeights(shift), width, height);
        }

        private static JxlPlane Copy(JxlPlane plane)
        {
            var copy = new JxlPlane(plane.Width, plane.Height);
            for (int y = 0; y < plane.Height; y++)
            {
                plane.Row(y).CopyTo(copy.Row(y));
            }

            return copy;
        }

        private static int ClampedSize(int begin, int size, int end) => (long)begin + size <= end ? size : end > begin ? end - begin : 0;

        private static int CeilLog2(uint value) => value <= 1 ? 0 : JxlPrefixCode.Log2Floor(value - 1) + 1;

        private static int DivCeil(int value, int divisor) => (value + divisor - 1) / divisor;

        private static long DivCeil(long value, long divisor) => (value + divisor - 1) / divisor;
    }
}
