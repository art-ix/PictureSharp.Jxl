using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Codestream;

/// <summary>The decoded samples of a still image: one (gray) or three colour channels and an optional alpha channel.</summary>
internal sealed record JxlDecodedImage(int Width, int Height, int[][] Color, int ColorBits, int[]? Alpha, int AlphaBits);

/// <summary>
/// Decodes a still, lossless (non-XYB) Modular JPEG XL image: skips the ICC stream and a preview frame, then reads
/// the frame sections (LF global, LF groups, pass groups) and undoes the Modular transforms. Ported from libjxl
/// v0.12.0 lib/jxl/dec_frame.cc, lib/jxl/dec_modular.cc and lib/jxl/icc_codec.cc (BSD-3-Clause). Valid features
/// outside this subset throw <see cref="NotSupportedException"/>.
/// </summary>
internal static class JxlFrameDecoder
{
    private const int IccContexts = 41;
    private const int QuantTables = 17;

    public static JxlDecodedImage Decode(JxlCodestream codestream, JxlHeaders headers, CancellationToken cancellationToken = default)
    {
        JxlImageMetadata metadata = headers.Metadata;
        int alpha = EnsureSupported(metadata);
        var reader = new JxlBitReader(codestream);
        reader.SkipBits((ulong)headers.EndBitPosition);
        if (headers.IccSize is { } iccSize)
        {
            SkipIcc(ref reader, iccSize);
            reader.JumpToByteBoundary();
        }

        if (metadata.Preview is not null)
        {
            JxlFrameHeader preview = JxlFrameHeader.Read(ref reader, headers, preview: true);
            long previewStart = ReadToc(ref reader, codestream, preview, out JxlToc previewToc);
            reader = new JxlBitReader(codestream);
            reader.SkipBits((ulong)(previewStart + previewToc.TotalSize) * 8);
        }

        JxlFrameHeader frame = JxlFrameHeader.Read(ref reader, headers, preview: false);
        EnsureSupported(frame, headers);
        long start = ReadToc(ref reader, codestream, frame, out JxlToc toc);
        var decoder = new FrameState(frame, metadata, cancellationToken);
        if (toc.Sizes.Length == 1)
        {
            var section = new JxlBitReader(codestream.Slice(start, toc.Sizes[0]));
            decoder.DecodeLfGlobal(ref section);
            decoder.DecodeLfGroup(ref section, 0);
            decoder.DecodePassGroup(ref section, 0, 0);
        }
        else
        {
            JxlBitReader Section(int index) => new(codestream.Slice(start + toc.Offsets[index], toc.Sizes[index]));
            var global = Section(0);
            decoder.DecodeLfGlobal(ref global);
            for (int group = 0; group < frame.DcGroups; group++)
            {
                var section = Section(1 + group);
                decoder.DecodeLfGroup(ref section, group);
            }

            // The HF global section holds nothing for Modular frames.
            for (int pass = 0; pass < frame.Passes.Count; pass++)
            {
                for (int group = 0; group < frame.Groups; group++)
                {
                    var section = Section((int)(2 + frame.DcGroups + pass * frame.Groups + group));
                    decoder.DecodePassGroup(ref section, pass, group);
                }
            }
        }

        return decoder.Finish(alpha);
    }

    /// <summary>Checks the image-level features and returns the index of the alpha extra channel, or -1.</summary>
    private static int EnsureSupported(JxlImageMetadata metadata)
    {
        if (metadata.Animation is not null)
        {
            throw JxlDescriptor.Unsupported("animations are not decoded yet.");
        }

        if (metadata.XybEncoded)
        {
            throw JxlDescriptor.Unsupported("lossy (XYB) images are not decoded yet; this version decodes lossless Modular images.");
        }

        if (metadata.BitDepth.FloatingPoint)
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
                    if (channel.BitDepth.FloatingPoint || channel.AlphaAssociated || channel.DimShift != 0)
                    {
                        throw JxlDescriptor.Unsupported("float, premultiplied or subsampled alpha is not decoded yet.");
                    }

                    alpha = i;
                    break;
            }
        }

        return alpha;
    }

    private static void EnsureSupported(JxlFrameHeader frame, JxlHeaders headers)
    {
        if (frame.Encoding != JxlFrameEncoding.Modular)
        {
            throw JxlDescriptor.Unsupported("VarDCT (lossy) frames are not decoded yet.");
        }

        if (frame.Type != JxlFrameType.Regular || !frame.IsLast)
        {
            throw JxlDescriptor.Unsupported("images of several frames (layers, reference frames) are not decoded yet.");
        }

        if (frame.ColorTransform != JxlColorTransform.None)
        {
            throw JxlDescriptor.Unsupported("YCbCr frames are not decoded yet.");
        }

        if ((frame.Flags & (JxlFrameFlags.Noise | JxlFrameFlags.Patches | JxlFrameFlags.Splines | JxlFrameFlags.UseDcFrame)) != 0)
        {
            throw JxlDescriptor.Unsupported("noise, patches, splines and DC frames are not decoded yet.");
        }

        if (frame.Upsampling != 1 || frame.ExtraChannelUpsampling.Any(factor => factor != 1))
        {
            throw JxlDescriptor.Unsupported("upsampled frames are not decoded yet.");
        }

        if (frame.X0 != 0 || frame.Y0 != 0 || frame.Width != headers.Size.Width || frame.Height != headers.Size.Height
            || frame.Blending.Mode != JxlBlendMode.Replace || frame.ExtraChannelBlending.Any(b => b.Mode != JxlBlendMode.Replace))
        {
            throw JxlDescriptor.Unsupported("cropped or blended frames are not decoded yet.");
        }

        if (frame.LoopFilter.Gaborish || frame.LoopFilter.EpfIterations != 0)
        {
            throw JxlDescriptor.Unsupported("restoration filters (Gaborish, EPF) are not applied yet.");
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

    /// <summary>The full Modular image of a frame and the global tree, filled section by section.</summary>
    private sealed class FrameState(JxlFrameHeader frame, JxlImageMetadata metadata, CancellationToken cancellationToken)
    {
        private readonly int colorChannels = metadata.Color.ColorSpace == JxlColorSpace.Gray ? 1 : 3;
        private JxlModularImage image = null!;
        private JxlModularTree? tree;
        private JxlGroupHeader header = JxlGroupHeader.Default;

        private int Width => (int)frame.XSize;

        private int Height => (int)frame.YSize;

        public void DecodeLfGlobal(ref JxlBitReader reader)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // DC quantisation factors, read even though only XYB uses them.
            if (!reader.ReadBool())
            {
                for (int c = 0; c < 3; c++)
                {
                    if (JxlFields.F16(ref reader) / 128 < 1e-8f)
                    {
                        throw JxlDescriptor.Corrupt("Invalid DC quantisation factor.");
                    }
                }
            }

            int extra = metadata.ExtraChannels.Length;
            if (reader.ReadBool())
            {
                long limit = Math.Min(1 << 22, 1024 + (long)Width * Height * (colorChannels + extra) / 16);
                JxlMaTree global = JxlMaTree.Read(ref reader, limit);
                tree = new JxlModularTree(global, JxlEntropyCode.Read(ref reader, global.Leaves));
            }

            var channels = new List<JxlChannel>();
            for (int c = 0; c < colorChannels; c++)
            {
                channels.Add(new JxlChannel(Width, Height));
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
            int dimension = frame.GroupDimension * JxlFrameHeader.BlockDim;
            int x = (int)(group % frame.XDcGroups) * dimension;
            int y = (int)(group / frame.XDcGroups) * dimension;
            DecodeGroup(ref reader, x, y, dimension, 3, 1000, (int)(1 + frame.DcGroups + group));
        }

        public void DecodePassGroup(ref JxlBitReader reader, int pass, int group)
        {
            int dimension = frame.GroupDimension;
            int x = (int)(group % frame.XGroups) * dimension;
            int y = (int)(group / frame.XGroups) * dimension;
            (int minShift, int maxShift) = frame.Passes.DownsamplingBracket(pass);
            DecodeGroup(ref reader, x, y, dimension, minShift, maxShift, (int)(1 + 3 * frame.DcGroups + QuantTables + frame.Groups * pass + group));
        }

        /// <summary>Decodes the part of the full image's large channels inside a group rectangle (DecodeGroup).</summary>
        private void DecodeGroup(ref JxlBitReader reader, int x0, int y0, int size, int minShift, int maxShift, int stream)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            image.UndoTransforms(header.Weighted);
            List<JxlChannel> channels = image.Channels;
            if (channels.Count != colorChannels + metadata.ExtraChannels.Length || image.MetaChannels != 0
                || channels.Any(channel => channel.Width != Width || channel.Height != Height))
            {
                throw JxlDescriptor.Corrupt("The Modular image does not match the frame.");
            }

            return new JxlDecodedImage(Width, Height, [.. channels.Take(colorChannels).Select(channel => channel.Pixels)],
                (int)metadata.BitDepth.BitsPerSample, alpha < 0 ? null : channels[colorChannels + alpha].Pixels,
                alpha < 0 ? 0 : (int)metadata.ExtraChannels[alpha].BitDepth.BitsPerSample);
        }

        private static int ClampedSize(int begin, int size, int end) => (long)begin + size <= end ? size : end > begin ? end - begin : 0;

        private static int CeilLog2(uint value) => value <= 1 ? 0 : JxlPrefixCode.Log2Floor(value - 1) + 1;
    }
}
