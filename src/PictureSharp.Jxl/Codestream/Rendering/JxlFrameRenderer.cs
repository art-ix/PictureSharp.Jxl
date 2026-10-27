using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

/// <summary>Ordered libjxl PreparePipeline stages (v0.12.0, BSD-3-Clause), independent of entropy decoding.</summary>
internal sealed class JxlFrameRenderer(JxlFrameHeader frame, JxlHeaders headers, JxlPatch[] patches,
    JxlReferenceFrames references, float[]? epfSigma, CancellationToken cancellationToken, JxlColorTarget? colorTarget = null)
{
    private JxlImageMetadata Metadata => headers.Metadata;
    private int Width => (int)frame.XSize;
    private int Height => (int)frame.YSize;
    /// <summary>The render pipeline stages of PreparePipeline that this decoder supports, in libjxl's order.</summary>
    public JxlPlane[] Render(JxlPlane[] color, JxlPlane[] extra, bool colorTransform)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                    color[c] = JxlRenderPipeline.UpsampleChromaHorizontally(color[c], Width, cancellationToken);
                }

                if (frame.VShift(c) != 0)
                {
                    color[c] = JxlRenderPipeline.UpsampleChromaVertically(color[c], Height, cancellationToken);
                }
            }
        }

        // Each filter writes into the planes the previous one read.
        JxlPlane[]? spare = null;
        if (filter.Gaborish)
        {
            (color, spare) = (JxlRenderPipeline.Gaborish(color, filter, spare, cancellationToken), color);
        }

        if (filter.EpfIterations > 0)
        {
            int xBlocks = (int)frame.XSizeBlocks;
            float[] sigma = epfSigma ?? Enumerable.Repeat(JxlRenderPipeline.InvSigmaNum / filter.SigmaForModular, xBlocks * (int)frame.YSizeBlocks).ToArray();
            if (filter.EpfIterations >= 3)
            {
                (color, spare) = (JxlRenderPipeline.Epf0(color, filter, sigma, xBlocks, spare, cancellationToken), color);
            }

            (color, spare) = (JxlRenderPipeline.Epf1(color, filter, sigma, xBlocks, spare, cancellationToken), color);
            if (filter.EpfIterations >= 2)
            {
                (color, spare) = (JxlRenderPipeline.Epf2(color, filter, sigma, xBlocks, spare, cancellationToken), color);
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
            JxlPatches.Apply(patches, color, extra, references, Metadata.ExtraChannels, cancellationToken);
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
            new JxlColorOutput(Metadata, headers.TransformData, frame.ColorTransform, colorTarget).Apply(color, cancellationToken);
        }

        return color;
    }

    private JxlPlane Upsample(JxlPlane plane, uint factor, int width, int height)
    {
        int shift = CeilLog2(factor);
        return JxlRenderPipeline.Upsample(plane, shift, headers.TransformData.UpsamplingWeights(shift), width, height, cancellationToken);
    }

    private JxlPlane Copy(JxlPlane plane)
    {
        var copy = new JxlPlane(plane.Width, plane.Height);
        for (int y = 0; y < plane.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plane.Row(y).CopyTo(copy.Row(y));
        }

        return copy;
    }


    private static int CeilLog2(uint value) => value <= 1 ? 0 : System.Numerics.BitOperations.Log2(value - 1) + 1;
}
