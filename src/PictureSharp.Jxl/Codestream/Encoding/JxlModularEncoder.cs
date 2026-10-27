using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// Encodes integer channels as one lossless Modular frame: a palette (few colours) or the best reversible colour
/// transform, a global MA tree learned from samples of all groups with the weighted and gradient predictors, and
/// 256×256 groups. Modelled on libjxl v0.12.0 lib/jxl/enc_modular.cc and modular/encoding/enc_encoding.cc at effort 7
/// (BSD-3-Clause); the pixel properties and predictions are those of <see cref="JxlMaTree"/>.
/// </summary>
internal static class JxlModularEncoder
{
    private const int GroupSizeShift = 1;
    private const int GroupDim = 256;
    private const int QuantTables = 17;
    private const int MaxPaletteColors = 1024;
    // libjxl's splitting_heuristics_node_threshold at effort 7: 75 + 14 × speed tier 3.
    private const float NodeThreshold = 117;
    private const int MaxPropertyValues = 96;
    private const int MaxSamples = 1 << 20;
    private const double SampleFraction = 0.5;
    private const int PropertyCount = JxlMaTree.NonReferenceProperties;
    private const int WeightedProperty = JxlMaTree.WeightedProperty;
    private static readonly JxlPredictor[] Predictors = [JxlPredictor.Weighted, JxlPredictor.Gradient];

    private enum TreeContext { SplitValue = 0, Property = 1, Predictor = 2, Offset = 3, MultiplierLog = 4, MultiplierBits = 5 }

    /// <summary>
    /// Writes the frame header, TOC and sections of <paramref name="channels"/> (colour channels, then extra channels,
    /// all of the image size). The writer must be at a byte boundary and stays at one.
    /// </summary>
    public static void WriteFrame(JxlBitWriter writer, IReadOnlyList<JxlChannel> channels, int colorChannels, CancellationToken cancellationToken)
    {
        int width = channels[0].Width;
        int height = channels[0].Height;
        var image = new List<JxlChannel>(channels);
        var transforms = new List<JxlTransform>();
        int meta = 0;
        if (Palette(image, image.Count, cancellationToken) is { } palette)
        {
            transforms.Add(palette.Transform);
            image = [palette.Palette, palette.Indices];
            meta = 1;
        }
        else if (colorChannels == 3)
        {
            uint rct = ChooseRct(image, cancellationToken);
            if (rct != 0)
            {
                ForwardRct(image, rct, cancellationToken);
                transforms.Add(new JxlTransform { Id = JxlTransformId.Rct, BeginC = 0, RctType = rct });
            }
        }

        // The global stream holds the meta channels and the leading channels that fit a group; groups hold the rest.
        int decodable = meta;
        while (decodable < image.Count && image[decodable].Width <= GroupDim && image[decodable].Height <= GroupDim) decodable++;
        long xGroups = (width + GroupDim - 1) / GroupDim;
        long yGroups = (height + GroupDim - 1) / GroupDim;
        long groups = xGroups * yGroups;
        long dcGroups = ((width + 7) / 8 + GroupDim - 1) / GroupDim * (((height + 7) / 8 + GroupDim - 1) / GroupDim);
        if (groups + dcGroups + 2 > JxlToc.MaxEntries) throw JxlDescriptor.Unsupported("the image has too many groups to encode.");
        var streams = new List<(int Id, List<JxlChannel> Channels)> { (0, image.Take(decodable).ToList()) };
        for (int g = 0; g < groups; g++)
        {
            int x0 = (int)(g % xGroups) * GroupDim;
            int y0 = (int)(g / xGroups) * GroupDim;
            var rects = new List<JxlChannel>();
            for (int c = decodable; c < image.Count; c++)
            {
                rects.Add(Crop(image[c], x0, y0, Math.Min(GroupDim, image[c].Width - x0), Math.Min(GroupDim, image[c].Height - y0)));
            }

            streams.Add(((int)(1 + 3 * dcGroups + QuantTables + g), rects));
        }

        List<JxlTreeNode> tree = LearnTree(streams, cancellationToken);
        (JxlTokens treeTokens, int[] leafOf) = TokenizeTree(tree);
        int leaves = leafOf.Count(leaf => leaf >= 0);

        var tokens = new List<JxlTokens>();
        foreach ((int id, List<JxlChannel> streamChannels) in streams)
        {
            var visitor = new TokenVisitor(tree, leafOf, new JxlTokens());
            for (int c = 0; c < streamChannels.Count; c++) Scan(streamChannels[c], c, id, ref visitor, cancellationToken);
            tokens.Add(visitor.Tokens);
        }

        JxlEntropyEncoder code = JxlEntropyEncoder.Build(leaves, tokens);

        var global = new JxlBitWriter();
        global.Write(true);                  // default DC quantization
        global.Write(true);                  // global tree
        JxlEntropyEncoder.WriteStandalone(global, 6, treeTokens);
        code.WriteHeader(global);
        if (image.Count > 0)
        {
            GroupHeader(global, transforms);
            if (streams[0].Channels.Any(channel => !channel.IsEmpty)) code.WriteStream(global, tokens[0]);
        }

        var groupSections = new JxlBitWriter[groups];
        for (int g = 0; g < groups; g++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            groupSections[g] = new JxlBitWriter();
            if (streams[g + 1].Channels.Count == 0) continue;
            GroupHeader(groupSections[g], []);
            code.WriteStream(groupSections[g], tokens[g + 1]);
        }

        JxlHeaderWriter.LosslessModularFrame(writer, channels.Count - colorChannels, GroupSizeShift);
        if (groups == 1)
        {
            global.Append(groupSections[0]);
            byte[] section = global.ToArray();
            JxlHeaderWriter.Toc(writer, [section.Length]);
            writer.WriteBytes(section);
            return;
        }

        // LfGlobal, the (empty) LF groups and HF global, then the groups of the only pass.
        var sections = new List<byte[]> { global.ToArray() };
        for (long i = 0; i < dcGroups + 1; i++) sections.Add([]);
        sections.AddRange(groupSections.Select(section => section.ToArray()));
        JxlHeaderWriter.Toc(writer, [.. sections.Select(section => section.Length)]);
        foreach (byte[] section in sections) writer.WriteBytes(section);
    }

    private static void GroupHeader(JxlBitWriter writer, IReadOnlyList<JxlTransform> transforms)
    {
        writer.Write(true);                  // use the global tree
        writer.Write(true);                  // default weighted predictor parameters
        JxlFieldWriter.U32(writer, (uint)transforms.Count, JxlU32.Val(0), JxlU32.Val(1), JxlU32.BitsOffset(4, 2), JxlU32.BitsOffset(8, 18));
        foreach (JxlTransform transform in transforms)
        {
            JxlFieldWriter.U32(writer, (uint)transform.Id, JxlU32.Val(0), JxlU32.Val(1), JxlU32.Val(2), JxlU32.Val(3));
            JxlFieldWriter.U32(writer, transform.BeginC, JxlU32.Bits32(3), JxlU32.BitsOffset(6, 8), JxlU32.BitsOffset(10, 72), JxlU32.BitsOffset(13, 1096));
            if (transform.Id == JxlTransformId.Rct)
            {
                JxlFieldWriter.U32(writer, transform.RctType, JxlU32.Val(6), JxlU32.Bits32(2), JxlU32.BitsOffset(4, 2), JxlU32.BitsOffset(6, 10));
                continue;
            }

            JxlFieldWriter.U32(writer, transform.NumC, JxlU32.Val(1), JxlU32.Val(3), JxlU32.Val(4), JxlU32.BitsOffset(13, 1));
            JxlFieldWriter.U32(writer, transform.Colors, JxlU32.Bits32(8), JxlU32.BitsOffset(10, 256), JxlU32.BitsOffset(12, 1280), JxlU32.BitsOffset(16, 5376));
            JxlFieldWriter.U32(writer, transform.Deltas, JxlU32.Val(0), JxlU32.BitsOffset(8, 1), JxlU32.BitsOffset(10, 257), JxlU32.BitsOffset(16, 1281));
            writer.Write(4, (uint)transform.Predictor);
        }
    }

    private static JxlChannel Crop(JxlChannel source, int x0, int y0, int width, int height)
    {
        var crop = new JxlChannel(width, height, source.HShift, source.VShift);
        for (int y = 0; y < height; y++) source.Row(y0 + y).Slice(x0, width).CopyTo(crop.Row(y));
        return crop;
    }

    // ---- Transforms ----

    private sealed record PaletteResult(JxlTransform Transform, JxlChannel Palette, JxlChannel Indices);

    /// <summary>
    /// A palette of all <paramref name="count"/> channels when the image has at most 1024 colours, and few enough for
    /// the palette to pay off: entries sorted by luma, so that nearby indices have nearby colours.
    /// </summary>
    private static PaletteResult? Palette(List<JxlChannel> image, int count, CancellationToken cancellationToken)
    {
        int width = image[0].Width;
        int height = image[0].Height;
        long pixels = (long)width * height;
        if (count > 4 || pixels < 64) return null;
        int limit = (int)Math.Min(MaxPaletteColors, pixels / 8);
        var colors = new Dictionary<long, int>();
        int[][] planes = [.. image.Take(count).Select(channel => channel.Pixels)];
        for (int i = 0; i < planes[0].Length; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            long key = Key(planes, i);
            if (colors.TryAdd(key, 0) && colors.Count > limit) return null;
        }

        long[] sorted = [.. colors.Keys.OrderBy(key => Luma(key, count)).ThenBy(key => key)];
        for (int i = 0; i < sorted.Length; i++) colors[sorted[i]] = i;
        var palette = new JxlChannel(sorted.Length, count, -1, -1);
        for (int i = 0; i < sorted.Length; i++)
        {
            for (int c = 0; c < count; c++) palette.Pixels[c * sorted.Length + i] = (int)((sorted[i] >> (16 * c)) & 0xFFFF);
        }

        var indices = new JxlChannel(width, height);
        for (int i = 0; i < indices.Pixels.Length; i++) indices.Pixels[i] = colors[Key(planes, i)];
        var transform = new JxlTransform
        {
            Id = JxlTransformId.Palette, BeginC = 0, NumC = (uint)count, Colors = (uint)sorted.Length, Deltas = 0, Predictor = JxlPredictor.Zero,
        };
        return new PaletteResult(transform, palette, indices);
    }

    private static long Key(int[][] planes, int i)
    {
        long key = 0;
        for (int c = 0; c < planes.Length; c++) key |= (long)(planes[c][i] & 0xFFFF) << (16 * c);
        return key;
    }

    private static long Luma(long key, int count) => count < 3
        ? key & 0xFFFF
        : 299 * (key & 0xFFFF) + 587 * ((key >> 16) & 0xFFFF) + 114 * ((key >> 32) & 0xFFFF);

    /// <summary>
    /// The RCT type (permutation × 7 + transform) whose channels cost least with the gradient predictor on a sample of
    /// rows; 0 when none beats the untransformed channels.
    /// </summary>
    private static uint ChooseRct(List<JxlChannel> image, CancellationToken cancellationToken)
    {
        int width = image[0].Width;
        int height = image[0].Height;
        int[][] planes = [image[0].Pixels, image[1].Pixels, image[2].Pixels];
        int step = (int)Math.Max(1, (long)width * height / 65536);
        uint best = 0;
        double bestCost = double.MaxValue;
        Span<int> current = stackalloc int[3];
        Span<int> left = stackalloc int[3];
        Span<int> top = stackalloc int[3];
        Span<int> topLeft = stackalloc int[3];
        for (uint type = 0; type < 42; type++)
        {
            if (type % 7 == 0 && type != 0) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var histograms = new long[3][];
            for (int c = 0; c < 3; c++) histograms[c] = new long[64];
            long extra = 0;
            for (int y = 1; y < height; y += step)
            {
                for (int x = 1; x < width; x++)
                {
                    Forward(planes, y * width + x, type, current);
                    Forward(planes, y * width + x - 1, type, left);
                    Forward(planes, (y - 1) * width + x, type, top);
                    Forward(planes, (y - 1) * width + x - 1, type, topLeft);
                    for (int c = 0; c < 3; c++)
                    {
                        long residual = current[c] - ClampedGradient(left[c], top[c], topLeft[c]);
                        uint token = JxlEntropyEncoder.Tokenize(new(4, 2, 0), JxlTokens.PackSigned(residual), out int bits, out _);
                        histograms[c][Math.Min(token, 63)]++;
                        extra += bits;
                    }
                }
            }

            double cost = extra + histograms.Sum(JxlEntropyEncoder.Entropy);
            if (cost < bestCost * 0.995 || type == 0)
            {
                bestCost = cost;
                best = type;
            }
        }

        return best;
    }

    private static void ForwardRct(List<JxlChannel> image, uint type, CancellationToken cancellationToken)
    {
        int[][] planes = [image[0].Pixels, image[1].Pixels, image[2].Pixels];
        var outputs = new JxlChannel[3];
        for (int c = 0; c < 3; c++) outputs[c] = new JxlChannel(image[c].Width, image[c].Height);
        Span<int> values = stackalloc int[3];
        for (int i = 0; i < planes[0].Length; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            Forward(planes, i, type, values);
            for (int c = 0; c < 3; c++) outputs[c].Pixels[i] = values[c];
        }

        for (int c = 0; c < 3; c++) image[c] = outputs[c];
    }

    // The inverse of JxlRctTransform: permute the input channels, then apply the forward colour transform.
    private static void Forward(int[][] planes, int i, uint type, Span<int> result)
    {
        int permutation = (int)type / 7;
        int custom = (int)type % 7;
        int a = planes[permutation % 3][i];
        int s = planes[(permutation + 1 + permutation / 3) % 3][i];
        int t = planes[(permutation + 2 - permutation / 3) % 3][i];
        if (custom == 6)
        {
            int co = a - t;
            int tmp = t + (co >> 1);
            int cg = s - tmp;
            (result[0], result[1], result[2]) = (tmp + (cg >> 1), co, cg);
            return;
        }

        if (custom >> 1 == 1) s -= a;
        else if (custom >> 1 == 2) s -= (a + t) >> 1;
        if ((custom & 1) != 0) t -= a;
        (result[0], result[1], result[2]) = (a, s, t);
    }

    // ---- Tree ----

    private static List<JxlTreeNode> LearnTree(List<(int Id, List<JxlChannel> Channels)> streams, CancellationToken cancellationToken)
    {
        long total = streams.Sum(stream => stream.Channels.Sum(channel => (long)channel.Width * channel.Height));
        // libjxl leaves out the group property when there are few groups.
        int[] properties = streams.Count < 30 ? [0, 15, 9, 10, 11, 12, 13] : [0, 1, 15, 9, 10, 11, 12];
        var learner = new JxlTreeLearner(properties, Predictors, MaxPropertyValues);
        double fraction = Math.Min(SampleFraction, MaxSamples / (double)Math.Max(total, 1));
        var visitor = new SampleVisitor(learner, (uint)(fraction * uint.MaxValue));
        foreach ((int id, List<JxlChannel> channels) in streams)
        {
            for (int c = 0; c < channels.Count; c++) Scan(channels[c], c, id, ref visitor, cancellationToken);
        }

        return learner.Learn(NodeThreshold, cancellationToken);
    }

    /// <summary>The tree's tokens in breadth-first order (TokenizeTree) and the leaf (context) index of every node.</summary>
    private static (JxlTokens Tokens, int[] LeafOf) TokenizeTree(List<JxlTreeNode> tree)
    {
        var tokens = new JxlTokens();
        int[] leafOf = new int[tree.Count];
        Array.Fill(leafOf, -1);
        var queue = new Queue<int>();
        queue.Enqueue(0);
        int leaves = 0;
        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            JxlTreeNode node = tree[index];
            tokens.Add((int)TreeContext.Property, (uint)(node.Property + 1));
            if (node.Property < 0)
            {
                tokens.Add((int)TreeContext.Predictor, (uint)node.Predictor);
                tokens.Add((int)TreeContext.Offset, 0);
                tokens.Add((int)TreeContext.MultiplierLog, 0);
                tokens.Add((int)TreeContext.MultiplierBits, 0);
                leafOf[index] = leaves++;
                continue;
            }

            tokens.Add((int)TreeContext.SplitValue, JxlTokens.PackSigned(node.Split));
            queue.Enqueue(node.Left);
            queue.Enqueue(node.Right);
        }

        return (tokens, leafOf);
    }

    // ---- Pixel scan ----

    private interface IPixelVisitor
    {
        void Visit(ReadOnlySpan<int> properties, int pixel, long weighted, long gradient);
    }

    /// <summary>Visits every pixel with its properties and predictions, exactly as JxlMaTree.DecodeChannel computes them.</summary>
    private static void Scan<TVisitor>(JxlChannel channel, int channelIndex, int stream, ref TVisitor visitor, CancellationToken cancellationToken)
        where TVisitor : struct, IPixelVisitor
    {
        if (channel.IsEmpty) return;
        int width = channel.Width;
        int[] pixels = channel.Pixels;
        Span<int> properties = stackalloc int[PropertyCount];
        var weighted = new JxlWeightedPredictor(JxlWeightedHeader.Default, width);
        for (int y = 0; y < channel.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            properties[0] = channelIndex;
            properties[1] = stream;
            properties[2] = y;
            properties[9] = 0;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                long l = x > 0 ? pixels[i - 1] : y > 0 ? pixels[i - width] : 0;
                long t = y > 0 ? pixels[i - width] : l;
                long tl = x > 0 && y > 0 ? pixels[i - 1 - width] : l;
                long tr = x + 1 < width && y > 0 ? pixels[i + 1 - width] : t;
                long ll = x > 1 ? pixels[i - 2] : l;
                long tt = y > 1 ? pixels[i - 2 * width] : t;
                unchecked
                {
                    properties[3] = x;
                    properties[4] = (int)(t > 0 ? t : -t);
                    properties[5] = (int)(l > 0 ? l : -l);
                    properties[6] = (int)t;
                    properties[7] = (int)l;
                    properties[8] = (int)(l - properties[9]);
                    properties[9] = (int)(l + t - tl);
                    properties[10] = (int)(l - tl);
                    properties[11] = (int)(tl - t);
                    properties[12] = (int)(t - tr);
                    properties[13] = (int)(t - tt);
                    properties[14] = (int)(l - ll);
                }

                long prediction = weighted.Predict(x, y, width, t, l, tr, tl, tt, out properties[WeightedProperty]);
                visitor.Visit(properties, pixels[i], prediction, ClampedGradient((int)l, (int)t, (int)tl));
                weighted.Update(pixels[i], x, y, width);
            }
        }
    }

    private struct SampleVisitor(JxlTreeLearner learner, uint threshold) : IPixelVisitor
    {
        private readonly long[] residuals = new long[2];
        private uint state = 0x9E3779B9;

        public void Visit(ReadOnlySpan<int> properties, int pixel, long weighted, long gradient)
        {
            // xorshift32: a fixed sequence, so the output does not depend on anything but the image.
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            if (state > threshold) return;
            residuals[0] = pixel - weighted;
            residuals[1] = pixel - gradient;
            learner.Add(properties, residuals);
        }
    }

    private readonly struct TokenVisitor(List<JxlTreeNode> tree, int[] leafOf, JxlTokens tokens) : IPixelVisitor
    {
        public JxlTokens Tokens => tokens;

        public void Visit(ReadOnlySpan<int> properties, int pixel, long weighted, long gradient)
        {
            int node = 0;
            while (tree[node].Property >= 0)
            {
                node = properties[tree[node].Property] > tree[node].Split ? tree[node].Left : tree[node].Right;
            }

            long prediction = tree[node].Predictor == JxlPredictor.Weighted ? weighted : gradient;
            tokens.Add(leafOf[node], JxlTokens.PackSigned(pixel - prediction));
        }
    }

    /// <summary>left + top - topLeft clamped to the range of left and top (JxlMaTree's gradient predictor).</summary>
    private static int ClampedGradient(int n, int w, int l)
    {
        int min = Math.Min(n, w);
        int max = Math.Max(n, w);
        int gradient = unchecked((int)((uint)n + (uint)w - (uint)l));
        return l > max ? min : l < min ? max : gradient;
    }
}
