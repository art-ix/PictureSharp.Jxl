using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// Encodes integer channels as one lossless Modular frame: a palette (few colours) or the best reversible colour
/// transform, a global MA tree learned from samples of all groups with the weighted and gradient predictors, and
/// 256×256 groups. Modelled on libjxl v0.12.0 lib/jxl/enc_modular.cc and modular/encoding/enc_encoding.cc at effort 7
/// (BSD-3-Clause); see <see cref="JxlModularTransformChooser"/> and <see cref="JxlModularTreeEncoder"/>.
/// </summary>
internal static class JxlModularEncoder
{
    private const int GroupSizeShift = 1;

    /// <summary>
    /// Writes the frame header, TOC and sections of <paramref name="channels"/> (colour channels, then extra channels,
    /// all of the image size). The writer must be at a byte boundary and stays at one.
    /// </summary>
    /// <param name="writer">The codestream, at a byte boundary.</param>
    /// <param name="channels">The colour channels, then the extra channels, all of the frame size.</param>
    /// <param name="colorChannels">The number of colour channels (1 or 3).</param>
    /// <param name="cancellationToken">Cancels encoding.</param>
    /// <param name="header">
    /// The frame header to write with <paramref name="metadata"/>, for other frames than the image's only one (a patch
    /// reference frame); by default a regular, last frame of the channels' size.
    /// </param>
    /// <param name="metadata">The image metadata <paramref name="header"/> is written against.</param>
    /// <param name="dcQuant">Custom DC quantization steps of X, Y and B (the scale of XYB samples), or the defaults.</param>
    /// <param name="parallelism">The maximum degree of parallelism (1 = single-threaded, -1 = automatic); the output does not depend on it.</param>
    /// <param name="effort">The effort tier (tree, learning and colour transform search); effort 7 by default.</param>
    /// <param name="bitDepth">The bits per sample, which scale the cutoffs of the fixed trees.</param>
    public static void WriteFrame(JxlBitWriter writer, IReadOnlyList<JxlChannel> channels, int colorChannels, CancellationToken cancellationToken,
        JxlFrameHeader? header = null, JxlImageMetadata? metadata = null, float[]? dcQuant = null, int parallelism = 1,
        JxlModularEffort? effort = null, int bitDepth = 8)
    {
        effort ??= JxlModularEffort.Default;
        JxlFrameHeader frame = header ?? new JxlFrameHeader
        {
            Encoding = JxlFrameEncoding.Modular, Width = (uint)channels[0].Width, Height = (uint)channels[0].Height, GroupSizeShift = GroupSizeShift,
        };
        if (frame.TocEntries > JxlToc.MaxEntries) throw JxlDescriptor.Unsupported("the image has too many groups to encode.");
        (List<JxlChannel> image, List<JxlTransform> transforms, int meta, JxlRctType rct) =
            ApplyTransforms(channels, colorChannels, cancellationToken, parallelism, effort.SearchRct, bitDepth);
        using var tokenBuffers = new JxlBufferScope();
        List<(int Id, List<JxlChannel> Channels)> streams = SplitIntoStreams(frame, image, meta, rct, tokenBuffers, cancellationToken);
        // The tokens of every stream, one per pixel. A learned tree records the weighted error of every pixel where its
        // token goes (0.8.6): tokenization reads a row's errors before its tokens replace them, so no error planes.
        List<JxlTokens> storage = [.. streams.Select(stream => new JxlTokens(tokenBuffers, JxlModularTreeEncoder.Capacity(stream.Channels)))];
        var errors = new Dictionary<JxlChannel, Memory<int>>(ReferenceEqualityComparer.Instance);
        bool learned = effort.Tree == JxlModularTreeKind.Learned;
        for (int s = 0; learned && s < streams.Count; s++)
        {
            int start = 0;
            foreach (JxlChannel channel in streams[s].Channels)
            {
                int pixels = channel.Width * channel.Height;
                if (pixels > 0) errors[channel] = storage[s].ValueStorage(start, pixels);
                start += pixels;
            }
        }

        List<JxlTreeNode> tree = learned
            ? JxlModularTreeEncoder.Learn(streams, cancellationToken, errors, tokenBuffers, parallelism, effort)
            : effort.FixedTree(streams.Sum(stream => stream.Channels.Sum(channel => (long)channel.Width * channel.Height)), bitDepth,
                streams.Max(stream => stream.Channels.Count));
        (JxlTokens treeTokens, int[] leafOf) = JxlModularTreeEncoder.Tokenize(tree, tokenBuffers);
        List<JxlTokens> tokens = JxlModularTreeEncoder.TokenizeStreams(streams, tree, leafOf, cancellationToken, errors, tokenBuffers, parallelism,
            storage);
        JxlEntropyEncoder code = JxlEntropyEncoder.Build(leafOf.Count(leaf => leaf >= 0), tokens);

        var global = new JxlBitWriter(writer.Buffers);
        global.Write(dcQuant is null);       // default DC quantization
        if (dcQuant is not null)
        {
            foreach (float step in dcQuant) JxlFieldWriter.F16(global, step * 128.0f);
        }

        global.Write(true);                  // global tree
        JxlEntropyEncoder.WriteStandalone(global, JxlMaTree.TreeContexts, treeTokens);
        code.WriteHeader(global);
        if (image.Count > 0)
        {
            GroupHeader(global, transforms);
            if (streams[0].Channels.Any(channel => !channel.IsEmpty)) code.WriteStream(global, tokens[0]);
        }

        // Every group section is coded on its own; they are written in group order.
        var groupSections = new JxlBitWriter[frame.Groups];
        JxlParallel.For(groupSections.Length, parallelism, cancellationToken, g =>
        {
            var section = new JxlBitWriter(writer.Buffers);
            if (streams[g + 1].Channels.Count != 0)
            {
                GroupHeader(section, []);
                code.WriteStream(section, tokens[g + 1]);
            }

            groupSections[g] = section;
        });

        if (header is null) JxlHeaderWriter.LosslessModularFrame(writer, channels.Count - colorChannels, GroupSizeShift);
        else JxlHeaderWriter.Frame(writer, header, metadata!);
        WriteSections(writer, frame, global, groupSections);
    }

    // A palette when it pays off; else, for samples of more than 8 bits, a palette per channel that uses few of its
    // values (libjxl's channel compaction), then the best colour transform of RGB (or YCoCg without the search). Every
    // palette adds one meta channel in front. The colour transform is chosen here and applied by SplitIntoStreams to
    // the copies it makes of channels meta to meta + 2.
    private static (List<JxlChannel> Image, List<JxlTransform> Transforms, int Meta, JxlRctType Rct) ApplyTransforms(IReadOnlyList<JxlChannel> channels,
        int colorChannels, CancellationToken cancellationToken, int parallelism, bool searchRct, int bitDepth)
    {
        var image = new List<JxlChannel>(channels);
        if (JxlModularTransformChooser.Palette(image, image.Count, cancellationToken, parallelism) is { } palette)
        {
            return ([palette.Palette, palette.Indices], [palette.Transform], 1, JxlRctType.None);
        }

        var transforms = new List<JxlTransform>();
        int meta = 0;
        bool compactedColor = false;
        JxlRctType rct = JxlRctType.None;
        if (bitDepth > 8)
        {
            for (int c = 0; c < channels.Count; c++)
            {
                if (JxlModularTransformChooser.CompactChannel(image[meta + c], cancellationToken) is not { } compact) continue;
                image[meta + c] = compact.Indices;
                transforms.Add(compact.Transform with { BeginC = (uint)(meta + c) });
                image.Insert(0, compact.Palette);
                meta++;
                compactedColor |= c < colorChannels;
            }
        }

        if (colorChannels == 3)
        {
            List<JxlChannel> rgb = image.GetRange(meta, 3);
            // Without the search RGB becomes YCoCg, unless compacted channels (indices of different scales) code
            // cheaper untransformed.
            rct = searchRct ? JxlModularTransformChooser.ChooseRct(rgb, cancellationToken, parallelism)
                : compactedColor ? JxlModularTransformChooser.ChooseRct(rgb, cancellationToken, parallelism, [JxlRctType.None, JxlRctType.YCoCg])
                : JxlRctType.YCoCg;
            if (rct != JxlRctType.None)
            {
                transforms.Add(new JxlTransform { Id = JxlTransformId.Rct, BeginC = (uint)meta, RctType = rct });
            }
        }

        return (image, transforms, meta, rct);
    }

    // The global stream holds the meta channels and the leading channels that fit a group; groups hold the rest.
    // Channels meta to meta + 2 get colour transform rct, applied to copies: to the group crops, or to whole copies
    // when they are in the global stream (0.8.6; transforming each pixel on its own, this equals transforming first).
    private static List<(int Id, List<JxlChannel> Channels)> SplitIntoStreams(JxlFrameHeader frame, List<JxlChannel> image, int meta,
        JxlRctType rct, JxlBufferScope buffers, CancellationToken cancellationToken)
    {
        int groupDim = frame.GroupDimension;
        int decodable = meta;
        while (decodable < image.Count && image[decodable].Width <= groupDim && image[decodable].Height <= groupDim) decodable++;
        List<JxlChannel> global = image.Take(decodable).ToList();
        bool transform = rct != JxlRctType.None;
        if (transform && decodable > meta)
        {
            // The three channels have one size: all of them are in the global stream.
            for (int c = meta; c < meta + 3; c++) global[c] = Crop(image[c], 0, 0, image[c].Width, image[c].Height, buffers);
            JxlModularTransformChooser.ForwardRctInPlace(global.GetRange(meta, 3), rct, cancellationToken);
            transform = false;
        }

        var streams = new List<(int Id, List<JxlChannel> Channels)> { (JxlFrameHeader.GlobalStream, global) };
        for (int g = 0; g < frame.Groups; g++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int x0, int y0) = frame.GroupOrigin(g, groupDim);
            var rects = new List<JxlChannel>();
            for (int c = decodable; c < image.Count; c++)
            {
                rects.Add(Crop(image[c], x0, y0, Math.Min(groupDim, image[c].Width - x0), Math.Min(groupDim, image[c].Height - y0), buffers));
            }

            if (transform) JxlModularTransformChooser.ForwardRctInPlace(rects.GetRange(meta - decodable, 3), rct, cancellationToken);
            streams.Add((frame.ModularAcStream(0, g), rects));
        }

        return streams;
    }

    // One section when there is one group; else LfGlobal, the (empty) LF groups and HF global, then the groups.
    private static void WriteSections(JxlBitWriter writer, JxlFrameHeader frame, JxlBitWriter global, JxlBitWriter[] groupSections)
    {
        if (groupSections.Length == 1)
        {
            global.Append(groupSections[0]);
            JxlHeaderWriter.Toc(writer, [checked((int)((global.BitLength + 7) / 8))]);
            writer.WriteSection(global);
            return;
        }

        int[] sizes = new int[checked((int)frame.TocEntries)];
        sizes[0] = checked((int)((global.BitLength + 7) / 8));
        for (int g = 0; g < groupSections.Length; g++) sizes[sizes.Length - groupSections.Length + g] = checked((int)((groupSections[g].BitLength + 7) / 8));
        JxlHeaderWriter.Toc(writer, sizes);
        writer.WriteSection(global);
        foreach (JxlBitWriter section in groupSections) writer.WriteSection(section);
    }

    private static void GroupHeader(JxlBitWriter writer, IReadOnlyList<JxlTransform> transforms)
    {
        writer.Write(true);                  // use the global tree
        writer.Write(true);                  // default weighted predictor parameters
        JxlFieldWriter.U32(writer, (uint)transforms.Count, JxlTransformFields.TransformCount);
        foreach (JxlTransform transform in transforms)
        {
            JxlFieldWriter.U32(writer, (uint)transform.Id, JxlTransformFields.Id);
            JxlFieldWriter.U32(writer, transform.BeginC, JxlTransformFields.BeginC);
            if (transform.Id == JxlTransformId.Rct)
            {
                JxlFieldWriter.U32(writer, transform.RctType.Value, JxlTransformFields.RctType);
                continue;
            }

            JxlFieldWriter.U32(writer, transform.NumC, JxlTransformFields.NumC);
            JxlFieldWriter.U32(writer, transform.Colors, JxlTransformFields.Colors);
            JxlFieldWriter.U32(writer, transform.Deltas, JxlTransformFields.Deltas);
            writer.Write(JxlTransformFields.PredictorBits, (uint)transform.Predictor);
        }
    }

    private static JxlChannel Crop(JxlChannel source, int x0, int y0, int width, int height, JxlBufferScope buffers)
    {
        var crop = new JxlChannel(width, height, source.HShift, source.VShift, buffers);
        for (int y = 0; y < height; y++) source.Row(y0 + y).Slice(x0, width).CopyTo(crop.Row(y));
        return crop;
    }
}
