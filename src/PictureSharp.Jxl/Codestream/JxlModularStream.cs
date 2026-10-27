namespace PictureSharp.Formats.Jxl.Codestream;

/// <summary>The header of a Modular stream: tree choice, weighted predictor parameters and transforms.</summary>
internal sealed record JxlGroupHeader(bool UseGlobalTree, JxlWeightedHeader Weighted, JxlTransform[] Transforms)
{
    public static JxlGroupHeader Default { get; } = new(false, JxlWeightedHeader.Default, []);

    public static JxlGroupHeader Read(ref JxlBitReader reader)
    {
        bool global = reader.ReadBool();
        JxlWeightedHeader weighted = JxlWeightedHeader.Read(ref reader);
        uint count = JxlFields.U32(ref reader, JxlU32.Val(0), JxlU32.Val(1), JxlU32.BitsOffset(4, 2), JxlU32.BitsOffset(8, 18));
        var transforms = new JxlTransform[count];
        for (int i = 0; i < transforms.Length; i++)
        {
            transforms[i] = JxlTransform.Read(ref reader);
        }

        return new JxlGroupHeader(global, weighted, transforms);
    }
}

/// <summary>The MA tree and its entropy code shared by the streams of a frame (from the LF global section).</summary>
internal sealed record JxlModularTree(JxlMaTree Tree, JxlEntropyCode Code);

/// <summary>
/// One Modular stream: header, transforms, optional local tree and the channels. Ported from libjxl v0.12.0
/// lib/jxl/modular/encoding/encoding.cc (ModularDecode, ModularGenericDecompress, ValidateChannelDimensions),
/// BSD-3-Clause.
/// </summary>
internal static class JxlModularStream
{
    /// <summary>Channels larger than this (other than meta channels) end the stream: libjxl's default limit.</summary>
    public const int DefaultMaxChannelSize = 0xFFFFFF;

    private const int DefaultGroupDimension = 0x1FFFFFFF;

    /// <summary>
    /// Decodes <paramref name="image"/>; channels from the first one wider or taller than
    /// <paramref name="maxChannelSize"/> on are left for the groups. Returns the stream header (default when the
    /// image has no channels, in which case nothing is read).
    /// </summary>
    public static JxlGroupHeader Decode(ref JxlBitReader reader, JxlModularImage image, int stream, JxlModularTree? global,
        bool undoTransforms, int maxChannelSize = DefaultMaxChannelSize, int groupDimension = DefaultGroupDimension,
        CancellationToken cancellationToken = default)
    {
        if (image.Channels.Count == 0)
        {
            return JxlGroupHeader.Default;
        }

        (int Width, int Height)[] requested = [.. image.Channels.Select(channel => (channel.Width, channel.Height))];
        JxlGroupHeader header = JxlGroupHeader.Read(ref reader);
        image.Transforms = [.. header.Transforms];
        foreach (JxlTransform transform in image.Transforms)
        {
            transform.MetaApply(image);
        }

        ValidateChannelDimensions(image, groupDimension);
        int decodable = DecodableChannels(image, maxChannelSize);
        int distanceMultiplier = 0;
        bool any = false;
        for (int i = 0; i < decodable; i++)
        {
            if (!image.Channels[i].IsEmpty)
            {
                any = true;
                distanceMultiplier = Math.Max(distanceMultiplier, image.Channels[i].Width);
            }
        }

        if (any)
        {
            JxlModularTree tree;
            if (header.UseGlobalTree)
            {
                tree = global ?? throw JxlDescriptor.Corrupt("A Modular stream uses the global tree, but there is none.");
            }
            else
            {
                long limit = 1024;
                for (int i = 0; i < decodable; i++)
                {
                    limit += (long)image.Channels[i].Width * image.Channels[i].Height;
                }

                JxlMaTree local = JxlMaTree.Read(ref reader, Math.Min(1 << 20, limit));
                tree = new JxlModularTree(local, JxlEntropyCode.Read(ref reader, local.Leaves));
            }

            using var symbols = new JxlSymbolReader(tree.Code, ref reader, distanceMultiplier);
            for (int i = 0; i < decodable; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tree.Tree.DecodeChannel(ref reader, symbols, tree.Code.ContextMap, header.Weighted, image, i, stream);
            }

            if (!symbols.IsFinalStateValid)
            {
                throw JxlDescriptor.Corrupt("Invalid ANS stream of Modular channel data.");
            }
        }

        if (undoTransforms)
        {
            image.UndoTransforms(header.Weighted);
            if (image.Channels.Count != requested.Length
                || image.Channels.Where((channel, i) => (channel.Width, channel.Height) != requested[i]).Any())
            {
                throw JxlDescriptor.Corrupt("Modular transforms do not restore the channel sizes.");
            }
        }

        return header;
    }

    /// <summary>The number of leading channels the stream codes: all meta channels, then up to the first too large one.</summary>
    public static int DecodableChannels(JxlModularImage image, int maxChannelSize)
    {
        int count = image.MetaChannels;
        while (count < image.Channels.Count && image.Channels[count].Width <= maxChannelSize && image.Channels[count].Height <= maxChannelSize)
        {
            count++;
        }

        return Math.Min(count, image.Channels.Count);
    }

    private static void ValidateChannelDimensions(JxlModularImage image, int groupDimension)
    {
        foreach (bool dc in (ReadOnlySpan<bool>)[true, false])
        {
            long dimension = (long)groupDimension * (dc ? JxlFrameHeader.BlockDim : 1);
            int c = DecodableChannels(image, groupDimension);
            for (; c < image.Channels.Count; c++)
            {
                JxlChannel channel = image.Channels[c];
                if (channel.IsEmpty || Math.Min(channel.HShift, channel.VShift) >= 3 != dc)
                {
                    continue;
                }

                if (dimension >> Math.Max(0, Math.Max(channel.HShift, channel.VShift)) == 0)
                {
                    throw JxlDescriptor.Corrupt("Inconsistent Modular transforms.");
                }
            }
        }
    }
}
