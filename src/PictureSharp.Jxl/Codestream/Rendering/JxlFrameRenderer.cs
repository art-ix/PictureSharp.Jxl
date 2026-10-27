using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

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
        color = ExpandGray(color, colorTransform);
        if (frame.ColorTransform == JxlColorTransform.YCbCr)
        {
            UpsampleChroma(color);
        }

        color = Restore(color);
        bool lateExtraUpsampling = frame.Upsampling != 1 && frame.ExtraChannelUpsampling.All(factor => factor == frame.Upsampling);
        if (!lateExtraUpsampling)
        {
            UpsampleExtraChannels(extra);
        }

        if (patches.Length > 0)
        {
            JxlPatches.Apply(patches, color, extra, references, Metadata.ExtraChannels, cancellationToken);
        }

        if (frame.Upsampling != 1)
        {
            UpsampleAll(color, lateExtraUpsampling ? extra : []);
        }

        if (colorTransform && color.Length == 3)
        {
            new JxlColorOutput(Metadata, headers.TransformData, frame.ColorTransform, colorTarget).Apply(color, cancellationToken);
        }

        return color;
    }

    // A gray plane becomes three when a later stage works on colour channels.
    private JxlPlane[] ExpandGray(JxlPlane[] color, bool colorTransform)
    {
        JxlLoopFilter filter = frame.LoopFilter;
        bool restore = filter.Gaborish || filter.EpfIterations > 0;
        return color.Length == 1 && (restore || frame.Upsampling != 1 || patches.Length > 0 || !colorTransform)
            ? [color[0], color[0].Clone(cancellationToken), color[0].Clone(cancellationToken)]
            : color;
    }

    private void UpsampleChroma(JxlPlane[] color)
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

    // Gaborish, then the EPF stages, each in place.
    private JxlPlane[] Restore(JxlPlane[] color)
    {
        JxlLoopFilter filter = frame.LoopFilter;
        if (filter.Gaborish)
        {
            JxlRenderPipeline.Gaborish(color, filter, cancellationToken);
        }

        if (filter.EpfIterations == 0)
        {
            return color;
        }

        int xBlocks = (int)frame.XSizeBlocks;
        float[] sigma = epfSigma ?? ModularSigma(filter, xBlocks * (int)frame.YSizeBlocks);
        if (filter.EpfIterations >= 3)
        {
            color = JxlRenderPipeline.Epf0(color, filter, sigma, xBlocks, cancellationToken);
        }

        color = JxlRenderPipeline.Epf1(color, filter, sigma, xBlocks, cancellationToken);
        if (filter.EpfIterations >= 2)
        {
            color = JxlRenderPipeline.Epf2(color, filter, sigma, xBlocks, cancellationToken);
        }

        return color;
    }

    // Modular frames filter every block with the same sigma.
    private static float[] ModularSigma(JxlLoopFilter filter, int blocks)
    {
        float[] sigma = new float[blocks];
        Array.Fill(sigma, JxlRenderPipeline.InvSigmaNum / filter.SigmaForModular);
        return sigma;
    }

    private void UpsampleExtraChannels(JxlPlane[] extra)
    {
        for (int i = 0; i < extra.Length; i++)
        {
            uint factor = frame.ExtraChannelUpsampling[i];
            if (factor != 1)
            {
                extra[i] = Upsample(extra[i], factor, (int)frame.Width, (int)frame.Height);
            }
        }
    }

    private void UpsampleAll(JxlPlane[] color, JxlPlane[] extra)
    {
        foreach (JxlPlane[] planes in (JxlPlane[][])[color, extra])
        {
            for (int c = 0; c < planes.Length; c++)
            {
                planes[c] = Upsample(planes[c], frame.Upsampling, (int)frame.Width, (int)frame.Height);
            }
        }
    }

    private JxlPlane Upsample(JxlPlane plane, uint factor, int width, int height)
    {
        int shift = CeilLog2(factor);
        return JxlRenderPipeline.Upsample(plane, shift, headers.TransformData.UpsamplingWeights(shift), width, height, cancellationToken);
    }

}
