using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

/// <summary>
/// The patch dictionary of a frame and its application. Ported from libjxl v0.12.0
/// lib/jxl/dec_patch_dictionary.cc, lib/jxl/blending.cc (PerformBlending) and lib/jxl/alpha.cc (BSD-3-Clause).
/// Patches are applied in dictionary order, which is the order libjxl's per-row patch tree yields for each pixel.
/// </summary>
internal static class JxlPatches
{
    public static void Apply(JxlPatch[] patches, JxlPlane[] color, JxlPlane[] extra, JxlReferenceFrames references, IReadOnlyList<JxlExtraChannelInfo> info, CancellationToken cancellationToken = default)
    {
        int channels = 3 + extra.Length;
        JxlPlane[] planes = [.. color, .. extra];
        bool hasAlpha = info.Any(channel => channel.Type == JxlExtraChannelType.Alpha);
        float[] bg = new float[channels];
        float[] fg = new float[channels];
        float[] result = new float[channels];
        foreach (JxlPatch patch in patches)
        {
            JxlReferenceFrame reference = references.GetPatch((uint)patch.Reference);
            JxlPlane[] source = [.. reference.Color, .. reference.Extra];
            int width = Math.Min(patch.Width, planes[0].Width - patch.X);
            int height = Math.Min(patch.Height, planes[0].Height - patch.Y);
            for (int iy = 0; iy < height; iy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int ix = 0; ix < width; ix++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        bg[c] = planes[c].Row(patch.Y + iy)[patch.X + ix];
                        fg[c] = source[c].Row(patch.ReferenceY + iy)[patch.ReferenceX + ix];
                    }

                    Blend(bg, fg, result, patch.Blendings, info, hasAlpha);
                    for (int c = 0; c < channels; c++)
                    {
                        planes[c].Row(patch.Y + iy)[patch.X + ix] = result[c];
                    }
                }
            }
        }
    }

    private static bool UsesAlpha(JxlPatchBlendMode mode) => mode is JxlPatchBlendMode.BlendAbove or JxlPatchBlendMode.BlendBelow
        or JxlPatchBlendMode.AlphaWeightedAddAbove or JxlPatchBlendMode.AlphaWeightedAddBelow;

    // PerformBlending for one pixel: extra channels first (with the alpha before blending), then colour.
    private static void Blend(float[] bg, float[] fg, float[] result, IReadOnlyList<JxlPatchBlending> blendings, IReadOnlyList<JxlExtraChannelInfo> info, bool hasAlpha)
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
