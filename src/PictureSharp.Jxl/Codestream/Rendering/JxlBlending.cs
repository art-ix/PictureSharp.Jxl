using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

/// <summary>
/// Blending of patches and frames. Ported from libjxl v0.12.0 lib/jxl/blending.cc (NeedsBlending, PerformBlending),
/// lib/jxl/alpha.cc and lib/jxl/render_pipeline/stage_blending.cc (BSD-3-Clause).
/// </summary>
internal static class JxlBlending
{
    /// <summary>Whether a displayed frame is blended onto the canvas: it is cropped or a channel does not replace.</summary>
    public static bool NeedsBlending(JxlFrameHeader frame) =>
        frame.Type is JxlFrameType.Regular or JxlFrameType.SkipProgressive
        && (frame.CustomSizeOrOrigin || frame.Blending.Mode != JxlBlendMode.Replace
            || frame.ExtraChannelBlending.Any(blending => blending.Mode != JxlBlendMode.Replace));

    /// <summary>
    /// Blends the rendered <paramref name="color"/> and <paramref name="extra"/> planes of <paramref name="frame"/> onto
    /// their backgrounds into new planes of the image size: three colour planes, then the extra channels. A null
    /// background is zeros. Pixels outside the frame keep the background.
    /// </summary>
    public static JxlPlane[] BlendFrame(JxlFrameHeader frame, JxlPlane[] color, JxlPlane[] extra, int width, int height,
        IReadOnlyList<JxlPlane>? colorBackground, IReadOnlyList<JxlPlane>?[] extraBackgrounds,
        IReadOnlyList<JxlExtraChannelInfo> info, CancellationToken cancellationToken, int parallelism, JxlBufferScope? buffers)
    {
        int channels = 3 + info.Count;
        var blendings = new JxlPatchBlending[1 + info.Count];
        blendings[0] = ToPatchBlending(frame.Blending, info.Count);
        for (int i = 0; i < info.Count; i++)
        {
            blendings[1 + i] = ToPatchBlending(frame.ExtraChannelBlending[i], info.Count);
        }

        JxlPlane[] fg = [color[0], color[color.Length == 3 ? 1 : 0], color[^1], .. extra];
        var bg = new JxlPlane?[channels];
        for (int c = 0; c < channels; c++)
        {
            bg[c] = c < 3 ? colorBackground?[c] : extraBackgrounds[c - 3]?[c - 3];
        }

        var output = new JxlPlane[channels];
        for (int c = 0; c < channels; c++)
        {
            output[c] = new JxlPlane(width, height, buffers: buffers);
        }

        bool hasAlpha = info.Any(channel => channel.Type == JxlExtraChannelType.Alpha);
        // The frame's columns inside the image; libjxl clips the frame (its origin may be negative) to the image.
        int x0 = frame.X0, y0 = frame.Y0;
        int start = Math.Max(x0, 0);
        int end = (int)Math.Min((long)x0 + frame.Width, width);
        JxlParallel.ForBands(height, 16, parallelism, cancellationToken, (first, last) =>
        {
            float[] b = new float[channels], f = new float[channels], result = new float[channels];
            for (int y = first; y < last; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int c = 0; c < channels; c++)
                {
                    Span<float> row = output[c].Row(y);
                    if (bg[c] is { } plane) plane.Row(y).CopyTo(row);
                    else row.Clear();
                }

                long fy = (long)y - y0;
                if (fy < 0 || fy >= frame.Height || start >= end) continue;
                for (int x = start; x < end; x++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        b[c] = output[c].Row(y)[x];
                        f[c] = fg[c].Row((int)fy)[x - x0];
                    }

                    Blend(b, f, result, blendings, info, hasAlpha);
                    for (int c = 0; c < channels; c++)
                    {
                        output[c].Row(y)[x] = result[c];
                    }
                }
            }
        });

        return output;
    }

    // BlendingStage's mapping of a frame's blending to the patch modes.
    private static JxlPatchBlending ToPatchBlending(JxlBlendingInfo info, int extraChannels)
    {
        JxlPatchBlendMode mode = info.Mode switch
        {
            JxlBlendMode.Add => JxlPatchBlendMode.Add,
            JxlBlendMode.Mul => JxlPatchBlendMode.Mul,
            JxlBlendMode.Blend => JxlPatchBlendMode.BlendAbove,
            JxlBlendMode.AlphaWeightedAdd => JxlPatchBlendMode.AlphaWeightedAddAbove,
            _ => JxlPatchBlendMode.Replace,
        };
        if (mode is JxlPatchBlendMode.BlendAbove or JxlPatchBlendMode.AlphaWeightedAddAbove && extraChannels > 0
            && info.AlphaChannel >= extraChannels)
        {
            throw JxlDescriptor.Corrupt("A frame blends with an alpha channel that does not exist.");
        }

        return new JxlPatchBlending(mode, (int)info.AlphaChannel, info.Clamp);
    }

    /// <summary>PerformBlending for one pixel: extra channels first (with the alpha before blending), then colour.</summary>
    public static void Blend(float[] bg, float[] fg, float[] result, IReadOnlyList<JxlPatchBlending> blendings, IReadOnlyList<JxlExtraChannelInfo> info, bool hasAlpha)
    {
        for (int i = 0; i < info.Count; i++)
        {
            JxlPatchBlending blending = blendings[1 + i];
            int c = 3 + i;
            int a = 3 + blending.AlphaChannel;
            result[c] = blending.Mode switch
            {
                JxlPatchBlendMode.Add => bg[c] + fg[c],
                JxlPatchBlendMode.BlendAbove => AlphaBlend(bg[c], bg[a], fg[c], fg[a], c == a, info[blending.AlphaChannel].AlphaAssociated, blending.Clamp),
                JxlPatchBlendMode.BlendBelow => AlphaBlend(fg[c], fg[a], bg[c], bg[a], c == a, info[blending.AlphaChannel].AlphaAssociated, blending.Clamp),
                JxlPatchBlendMode.AlphaWeightedAddAbove => c == a ? bg[c] : bg[c] + fg[c] * Clamp(fg[a], blending.Clamp),
                JxlPatchBlendMode.AlphaWeightedAddBelow => c == a ? fg[c] : fg[c] + bg[c] * Clamp(bg[a], blending.Clamp),
                JxlPatchBlendMode.Mul => bg[c] * Clamp(fg[c], blending.Clamp),
                JxlPatchBlendMode.Replace => fg[c],
                _ => bg[c],
            };
        }

        JxlPatchBlending color = blendings[0];
        int alpha = 3 + color.AlphaChannel;
        switch (color.Mode)
        {
            case JxlPatchBlendMode.Add:
            case JxlPatchBlendMode.AlphaWeightedAddAbove when !hasAlpha:
            case JxlPatchBlendMode.AlphaWeightedAddBelow when !hasAlpha:
                for (int c = 0; c < 3; c++)
                {
                    result[c] = bg[c] + fg[c];
                }

                break;
            case JxlPatchBlendMode.AlphaWeightedAddAbove:
                for (int c = 0; c < 3; c++)
                {
                    result[c] = bg[c] + fg[c] * Clamp(fg[alpha], color.Clamp);
                }

                break;
            case JxlPatchBlendMode.AlphaWeightedAddBelow:
                for (int c = 0; c < 3; c++)
                {
                    result[c] = fg[c] + bg[c] * Clamp(bg[alpha], color.Clamp);
                }

                break;
            case JxlPatchBlendMode.BlendAbove when hasAlpha:
                BlendColor(bg, fg, result, alpha, info[color.AlphaChannel].AlphaAssociated, color.Clamp);
                break;
            case JxlPatchBlendMode.BlendBelow when hasAlpha:
                BlendColor(fg, bg, result, alpha, info[color.AlphaChannel].AlphaAssociated, color.Clamp);
                break;
            case JxlPatchBlendMode.Mul:
                for (int c = 0; c < 3; c++)
                {
                    result[c] = bg[c] * Clamp(fg[c], color.Clamp);
                }

                break;
            case JxlPatchBlendMode.None:
                Array.Copy(bg, result, 3);
                break;
            default:
                Array.Copy(fg, result, 3);
                break;
        }
    }

    private static float Clamp(float value, bool clamp) => clamp ? Math.Clamp(value, 0.0f, 1.0f) : value;

    // PerformAlphaBlending of a single channel; the alpha channel itself blends as 1 - (1 - fa)(1 - ba).
    private static float AlphaBlend(float bg, float bga, float fg, float fga, bool isAlpha, bool premultiplied, bool clamp)
    {
        float fa = Clamp(fga, clamp);
        if (isAlpha)
        {
            return 1.0f - (1.0f - fa) * (1.0f - bga);
        }

        if (premultiplied)
        {
            return fg + bg * (1.0f - fa);
        }

        float newAlpha = 1.0f - (1.0f - fa) * (1.0f - bga);
        float inverse = newAlpha > 0 ? 1.0f / newAlpha : 0.0f;
        return (fg * fa + bg * bga * (1.0f - fa)) * inverse;
    }

    // PerformAlphaBlending of the colour channels together with the alpha channel of the colour blending.
    private static void BlendColor(float[] bottom, float[] top, float[] result, int alpha, bool premultiplied, bool clamp)
    {
        float fa = Clamp(top[alpha], clamp);
        float newAlpha = 1.0f - (1.0f - fa) * (1.0f - bottom[alpha]);
        float inverse = newAlpha > 0 ? 1.0f / newAlpha : 0.0f;
        for (int c = 0; c < 3; c++)
        {
            result[c] = premultiplied ? top[c] + bottom[c] * (1.0f - fa) : (top[c] * fa + bottom[c] * bottom[alpha] * (1.0f - fa)) * inverse;
        }

        result[alpha] = newAlpha;
    }
}
