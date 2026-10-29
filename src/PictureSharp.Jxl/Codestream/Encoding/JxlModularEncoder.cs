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
    public static void WriteFrame(JxlBitWriter writer, IReadOnlyList<JxlChannel> channels, int colorChannels, CancellationToken cancellationToken,
        JxlFrameHeader? header = null, JxlImageMetadata? metadata = null, float[]? dcQuant = null, int parallelism = 1)
    {
        JxlFrameHeader frame = header ?? new JxlFrameHeader
        {
            Encoding = JxlFrameEncoding.Modular, Width = (uint)channels[0].Width, Height = (uint)channels[0].Height, GroupSizeShift = GroupSizeShift,
        };
        if (frame.TocEntries > JxlToc.MaxEntries) throw JxlDescriptor.Unsupported("the image has too many groups to encode.");
        (List<JxlChannel> image, List<JxlTransform> transforms, int meta) = ApplyTransforms(channels, colorChannels, cancellationToken);
        using var tokenBuffers = new JxlBufferScope();
        List<(int Id, List<JxlChannel> Channels)> streams = SplitIntoStreams(frame, image, meta, tokenBuffers);
        var errors = new Dictionary<JxlChannel, int[]>(ReferenceEqualityComparer.Instance);
        List<JxlTreeNode> tree = JxlModularTreeEncoder.Learn(streams, cancellationToken, errors, tokenBuffers, parallelism);
        (JxlTokens treeTokens, int[] leafOf) = JxlModularTreeEncoder.Tokenize(tree, tokenBuffers);
        List<JxlTokens> tokens = JxlModularTreeEncoder.TokenizeStreams(streams, tree, leafOf, cancellationToken, errors, tokenBuffers, parallelism);
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

    // A palette when it pays off, else the best colour transform of RGB; the palette adds one meta channel in front.
    private static (List<JxlChannel> Image, List<JxlTransform> Transforms, int Meta) ApplyTransforms(IReadOnlyList<JxlChannel> channels,
        int colorChannels, CancellationToken cancellationToken)
    {
        var image = new List<JxlChannel>(channels);
        if (JxlModularTransformChooser.Palette(image, image.Count, cancellationToken) is { } palette)
        {
            return ([palette.Palette, palette.Indices], [palette.Transform], 1);
        }

        if (colorChannels == 3)
        {
            JxlRctType rct = JxlModularTransformChooser.ChooseRct(image, cancellationToken);
            if (rct != JxlRctType.None)
            {
                JxlModularTransformChooser.ForwardRct(image, rct, cancellationToken);
                return (image, [new JxlTransform { Id = JxlTransformId.Rct, BeginC = 0, RctType = rct }], 0);
            }
        }

        return (image, [], 0);
    }

    // The global stream holds the meta channels and the leading channels that fit a group; groups hold the rest.
    private static List<(int Id, List<JxlChannel> Channels)> SplitIntoStreams(JxlFrameHeader frame, List<JxlChannel> image, int meta, JxlBufferScope buffers)
    {
        int groupDim = frame.GroupDimension;
        int decodable = meta;
        while (decodable < image.Count && image[decodable].Width <= groupDim && image[decodable].Height <= groupDim) decodable++;
        var streams = new List<(int Id, List<JxlChannel> Channels)> { (JxlFrameHeader.GlobalStream, image.Take(decodable).ToList()) };
        for (int g = 0; g < frame.Groups; g++)
        {
            (int x0, int y0) = frame.GroupOrigin(g, groupDim);
            var rects = new List<JxlChannel>();
            for (int c = decodable; c < image.Count; c++)
            {
                rects.Add(Crop(image[c], x0, y0, Math.Min(groupDim, image[c].Width - x0), Math.Min(groupDim, image[c].Height - y0), buffers));
            }

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
