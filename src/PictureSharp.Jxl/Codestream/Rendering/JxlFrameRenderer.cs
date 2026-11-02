using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

/// <summary>
/// Ordered libjxl PreparePipeline stages (v0.12.0, BSD-3-Clause), independent of entropy decoding. Filters, upsampling
/// and colour output run in row bands of up to <paramref name="parallelism"/> threads with identical results.
/// </summary>
internal sealed class JxlFrameRenderer(JxlFrameHeader frame, JxlHeaders headers, JxlPatch[] patches,
    JxlReferenceFrames references, float[]? epfSigma, CancellationToken cancellationToken, JxlColorTarget? colorTarget = null,
    int parallelism = JxlParallel.Sequential, JxlBufferScope? buffers = null, JxlRenderFeatures? features = null)
{
    private JxlImageMetadata Metadata => headers.Metadata;

    private int Width => (int)frame.XSize;

    private int Height => (int)frame.YSize;

    /// <summary>
    /// With <c>deferChroma</c> in <see cref="Render"/>: the subsampled YCbCr planes, to be upsampled and converted row
    /// by row by the consumer, when nothing follows those stages; the returned colour planes are then left as decoded.
    /// </summary>
    public JxlYCbCrRows? Rows { get; private set; }

    /// <summary>
    /// With <see cref="JxlSpotRendering.Copy"/> in <see cref="Render"/>: the colour planes with the spot colours rendered
    /// (new planes, not owned by the frame's buffers), next to the returned planes without them; null without spot
    /// colours.
    /// </summary>
    public JxlPlane[]? SpotColor { get; private set; }

    /// <summary>
    /// The render pipeline stages of PreparePipeline that this decoder supports, in libjxl's order. <paramref name="spot"/>
    /// renders the spot colours after the colour transform (0.8.16), for frames that are not blended.
    /// </summary>
    public JxlPlane[] Render(JxlPlane[] color, JxlPlane[] extra, bool colorTransform, bool deferChroma = false,
        JxlSpotRendering spot = JxlSpotRendering.None)
    {
        JxlSpotColors? spots = colorTransform && spot != JxlSpotRendering.None ? JxlSpotColors.For(Metadata) : null;
        cancellationToken.ThrowIfCancellationRequested();
        color = ExpandGray(color, colorTransform);
        if (frame.ColorTransform == JxlColorTransform.YCbCr)
        {
            if (deferChroma && colorTransform && spots is null && CanDeferChroma(color))
            {
                // Chroma upsampling and YCbCr to RGB are the last stages: the consumer renders rows (0.8.6).
                Rows = new JxlYCbCrRows(color, [frame.HShift(0), frame.HShift(1), frame.HShift(2)],
                    [frame.VShift(0), frame.VShift(1), frame.VShift(2)], Width, Height, cancellationToken);
                UpsampleExtraChannels(extra);
                return color;
            }

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

        features?.Splines?.Draw(color, parallelism, cancellationToken);
        if (frame.Upsampling != 1)
        {
            UpsampleAll(color, lateExtraUpsampling ? extra : []);
        }

        if (features?.Noise is { } noise)
        {
            JxlNoise.Apply(color, frame, noise, features.VisibleIndex, features.NonvisibleIndex, features.YToX, features.YToB, parallelism,
                cancellationToken, buffers);
        }

        if (spots is null)
        {
            if (colorTransform && color.Length == 3)
            {
                new JxlColorOutput(Metadata, headers.TransformData, frame.ColorTransform, colorTarget).Apply(color, cancellationToken, parallelism);
            }

            return color;
        }

        // SpotColorStage after the colour transform: for XYB in linear light (inside the colour output), otherwise in
        // the coded colour space; with Copy on clones, keeping the planes without ink for blending or a reference slot.
        JxlPlane[] spotted = spot == JxlSpotRendering.Copy ? [.. color.Select(plane => plane.Clone(cancellationToken))] : color;
        if (color.Length == 3)
        {
            if (spot == JxlSpotRendering.Copy)
            {
                new JxlColorOutput(Metadata, headers.TransformData, frame.ColorTransform, colorTarget).Apply(color, cancellationToken, parallelism);
            }

            (JxlPlane, float[])[]? linear = frame.ColorTransform == JxlColorTransform.Xyb
                ? [.. spots.Channels.Select((channel, i) => (extra[channel], spots.Colors[i]))]
                : null;
            new JxlColorOutput(Metadata, headers.TransformData, frame.ColorTransform, colorTarget, linear).Apply(spotted, cancellationToken, parallelism);
            if (linear is not null)
            {
                SpotColor = spot == JxlSpotRendering.Copy ? spotted : null;
                return color;
            }
        }

        spots.Apply(spotted, extra, cancellationToken, parallelism);
        SpotColor = spot == JxlSpotRendering.Copy ? spotted : null;
        return color;
    }

    // Only chroma upsampling and the colour transform remain: no restoration, patches, splines, upsampling or noise.
    private bool CanDeferChroma(JxlPlane[] color) =>
        color.Length == 3 && features?.Any != true && !frame.LoopFilter.Gaborish && frame.LoopFilter.EpfIterations == 0 && patches.Length == 0 && frame.Upsampling == 1
        && JxlYCbCrRows.Applies([frame.HShift(0), frame.HShift(1), frame.HShift(2)], [frame.VShift(0), frame.VShift(1), frame.VShift(2)]);

    // A gray plane becomes three when a later stage works on colour channels.
    private JxlPlane[] ExpandGray(JxlPlane[] color, bool colorTransform)
    {
        JxlLoopFilter filter = frame.LoopFilter;
        bool restore = filter.Gaborish || filter.EpfIterations > 0;
        return color.Length == 1 && (restore || frame.Upsampling != 1 || patches.Length > 0 || features?.Any == true || !colorTransform)
            ? [color[0], color[0].Clone(cancellationToken), color[0].Clone(cancellationToken)]
            : color;
    }

    private void UpsampleChroma(JxlPlane[] color)
    {
        // The upsampled planes belong to the frame's buffers (the decode), like the planes they replace.
        for (int c = 0; c < color.Length; c++)
        {
            if (frame.HShift(c) != 0 && frame.VShift(c) != 0)
            {
                color[c] = JxlRenderPipeline.UpsampleChroma2x2(color[c], Width, Height, cancellationToken, parallelism, buffers);
                continue;
            }

            if (frame.HShift(c) != 0)
            {
                color[c] = JxlRenderPipeline.UpsampleChromaHorizontally(color[c], Width, cancellationToken, buffers);
            }

            if (frame.VShift(c) != 0)
            {
                color[c] = JxlRenderPipeline.UpsampleChromaVertically(color[c], Height, cancellationToken, buffers);
            }
        }
    }

    // Gaborish, then the EPF stages, each in place.
    private JxlPlane[] Restore(JxlPlane[] color)
    {
        JxlLoopFilter filter = frame.LoopFilter;
        if (filter.Gaborish)
        {
            JxlRenderPipeline.Gaborish(color, filter, cancellationToken, parallelism);
        }

        if (filter.EpfIterations == 0)
        {
            return color;
        }

        int xBlocks = (int)frame.XSizeBlocks;
        float[] sigma = epfSigma ?? ModularSigma(filter, xBlocks * (int)frame.YSizeBlocks);
        if (filter.EpfIterations >= 3)
        {
            color = JxlRenderPipeline.Epf0(color, filter, sigma, xBlocks, cancellationToken, parallelism);
        }

        color = JxlRenderPipeline.Epf1(color, filter, sigma, xBlocks, cancellationToken, parallelism);
        if (filter.EpfIterations >= 2)
        {
            color = JxlRenderPipeline.Epf2(color, filter, sigma, xBlocks, cancellationToken, parallelism);
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
        return JxlRenderPipeline.Upsample(plane, shift, headers.TransformData.UpsamplingWeights(shift), width, height, cancellationToken,
            parallelism);
    }

}

/// <summary>Whether <see cref="JxlFrameRenderer.Render"/> renders the spot colours, and where.</summary>
internal enum JxlSpotRendering
{
    /// <summary>Not rendered: the frame is blended first, or saved before the colour transform.</summary>
    None,

    /// <summary>Rendered into the returned planes.</summary>
    InPlace,

    /// <summary>Rendered into <see cref="JxlFrameRenderer.SpotColor"/>; the returned planes stay without ink.</summary>
    Copy,
}
