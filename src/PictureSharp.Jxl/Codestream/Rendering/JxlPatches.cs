using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

/// <summary>
/// The patch dictionary of a frame and its application. Ported from libjxl v0.12.0
/// lib/jxl/dec_patch_dictionary.cc (BSD-3-Clause); the blending itself is <see cref="JxlBlending"/>.
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

                    JxlBlending.Blend(bg, fg, result, patch.Blendings, info, hasAlpha);
                    for (int c = 0; c < channels; c++)
                    {
                        planes[c].Row(patch.Y + iy)[patch.X + ix] = result[c];
                    }
                }
            }
        }
    }
}
