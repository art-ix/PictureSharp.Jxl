using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;
using PictureSharp.Formats.Jxl.Codestream.Rendering;
using PictureSharp.Formats.Jxl.Codestream.VarDct;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Frames;

/// <summary>Decodes the sections of one frame. Ported from libjxl v0.12.0 dec_frame.cc,
/// dec_modular.cc and dec_cache.cc (BSD-3-Clause).</summary>
internal sealed class JxlFrameDecoder
{
    // LF groups code the channels shifted by at least 3 (libjxl: minShift 3, maxShift 1000, i.e. no maximum).
    private const int LfMinShift = 3;
    private const int NoMaxShift = 1000;

    private readonly JxlFrameHeader frame;
    private readonly JxlFrameProgress progress;
    private readonly JxlImageMetadata metadata;
    private readonly JxlHeaders headers;
    private readonly CancellationToken cancellationToken;
    private readonly JxlQuantWeights matrices = new();
    private readonly JxlVarDctFrame? varDct;
    private readonly int colorChannels;
    private readonly int codedChannels;
    private JxlModularImage image = null!;
    private JxlModularTree? tree;
    private readonly JxlReferenceFrames references;
    private JxlGroupHeader header = JxlGroupHeader.Default;
    private JxlPatch[] patches = [];
    private readonly JxlColorTarget? colorTarget;

    public JxlFrameDecoder(JxlFrameHeader frame, JxlHeaders headers, JxlReferenceFrames references,
        CancellationToken cancellationToken, JxlColorTarget? colorTarget = null)
    {
        this.frame = frame;
        this.colorTarget = colorTarget;
        progress = new(frame.DcGroups, frame.Groups, frame.Passes.Count);
        this.references = references;
        this.headers = headers;
        this.cancellationToken = cancellationToken;
        metadata = headers.Metadata;
        bool gray = metadata.Color.ColorSpace == JxlColorSpace.Gray && frame.ColorTransform == JxlColorTransform.None;
        codedChannels = gray ? 1 : 3;
        if (frame.Encoding == JxlFrameEncoding.VarDct)
        {
            varDct = new JxlVarDctFrame(frame, metadata, matrices, headers.TransformData.EffectiveQuantBiases, cancellationToken);
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
        progress.BeginLf();
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.HasPatches)
        {
            patches = JxlPatchReader.Read(ref reader, frame.XSizePadded, frame.YSizePadded, metadata.ExtraChannels.Count, references);
        }

        matrices.ReadDc(ref reader);
        varDct?.ReadGlobal(ref reader);
        if (varDct is not null && frame.UsesDcFrame)
        {
            varDct.UseDcFrame(references.GetDc(frame.DcLevel));
        }

        int extra = metadata.ExtraChannels.Count;
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
        progress.LfGroup(group);
        cancellationToken.ThrowIfCancellationRequested();
        if (!frame.UsesDcFrame)
        {
            varDct?.DecodeDc(ref reader, group, tree);
        }

        int dimension = frame.GroupDimension * JxlFrameHeader.BlockDim;
        (int x, int y) = frame.DcGroupOrigin(group, dimension);
        DecodeGroup(ref reader, x, y, dimension, LfMinShift, NoMaxShift, frame.ModularDcStream(group));
        varDct?.DecodeAcMetadata(ref reader, group, tree);
    }

    public void FinishLf()
    {
        progress.FinishLf();
        varDct?.SmoothDc();
    }

    public void DecodeHfGlobal(ref JxlBitReader reader)
    {
        progress.BeginHf();
        cancellationToken.ThrowIfCancellationRequested();
        varDct?.ReadHfGlobal(ref reader, ReadRawQuantTable);
    }

    // A raw quantisation table: a three-channel Modular image with its own stream id.
    private int[][] ReadRawQuantTable(ref JxlBitReader reader, int index, int width, int height)
    {
        var table = new JxlModularImage(8, [new JxlChannel(width, height), new JxlChannel(width, height), new JxlChannel(width, height)]);
        JxlModularStream.Decode(ref reader, table, frame.QuantTableStream(index), tree, undoTransforms: true,
            cancellationToken: cancellationToken);
        return [.. table.Channels.Select(channel => channel.Pixels)];
    }

    public int[]? BeginGroup(int group)
    {
        progress.BeginGroup(group);
        return varDct?.NewGroupCoefficients(group);
    }

    public void DecodePassGroup(ref JxlBitReader reader, int pass, int group, int[]? coefficients)
    {
        progress.Pass(group, pass);
        cancellationToken.ThrowIfCancellationRequested();
        varDct?.DecodePass(ref reader, group, pass, coefficients!);
        int dimension = frame.GroupDimension;
        (int x, int y) = frame.GroupOrigin(group, dimension);
        (int minShift, int maxShift) = frame.Passes.DownsamplingBracket(pass);
        DecodeGroup(ref reader, x, y, dimension, minShift, maxShift, frame.ModularAcStream(pass, group));
    }

    public void FinishGroup(int group, int[]? coefficients)
    {
        progress.FinishGroup(group);
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
                cancellationToken.ThrowIfCancellationRequested();
                decoded.Row(row).CopyTo(full.Row(y + row)[x..]);
            }
        }
    }

    public JxlDecodedImage Finish(int alpha)
    {
        (JxlPlane[] color, JxlPlane[] extra) = Render(colorTransform: true);
        return new JxlDecodedImage((int)frame.Width, (int)frame.Height, color, alpha < 0 ? null : extra[alpha], metadata.Color.ColorSpace == JxlColorSpace.Gray,
            JxlColorOutput.DescribeOutput(metadata, frame.ColorTransform, colorTarget));
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
        progress.Consume();
        image.UndoTransforms(header.Weighted, cancellationToken);
        IReadOnlyList<JxlChannel> channels = image.Channels;
        int extra = metadata.ExtraChannels.Count;
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

        float[]? sigma = varDct is not null && frame.LoopFilter.EpfIterations > 0
            ? JxlEpfSigma.Compute(frame.LoopFilter, (int)frame.XSizeBlocks, (int)frame.YSizeBlocks,
                varDct.QuantScale, varDct.QuantField, varDct.Sharpness, varDct.Varblocks, cancellationToken)
            : null;
        color = new JxlFrameRenderer(frame, headers, patches, references, sigma, cancellationToken, colorTarget)
            .Render(color, extraPlanes, colorTransform);
        return (color, extraPlanes);
    }

    // Colour channels of a Modular frame as floats (ModularImageToDecodedRect): XYB is coded as Y, X, B - Y.
    private JxlPlane[] ModularColor(IReadOnlyList<JxlChannel> channels)
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
                cancellationToken.ThrowIfCancellationRequested();
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
    private JxlPlane ToPlane(JxlChannel channel, int bits)
    {
        var plane = new JxlPlane(channel.Width, channel.Height);
        double factor = 1.0 / ((1u << bits) - 1);
        float single = (float)factor;
        for (int y = 0; y < channel.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<int> row = channel.Row(y);
            Span<float> output = plane.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                output[x] = bits < 23 ? row[x] * single : (float)(row[x] * factor);
            }
        }

        return plane;
    }

    private static int ClampedSize(int begin, int size, int end) => (long)begin + size <= end ? size : end > begin ? end - begin : 0;

}
