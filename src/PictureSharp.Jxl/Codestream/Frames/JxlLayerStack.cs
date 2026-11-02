using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Rendering;

namespace PictureSharp.Formats.Jxl.Codestream.Frames;

/// <summary>
/// The layers of a still image whose displayed frames form a simple stack (docs/design/0.8.11-layers.md): each layer
/// blends onto the result of the one before it, the first onto an empty slot. Reference-only, DC and patch-source
/// frames are not layers.
/// </summary>
internal static class JxlLayerStack
{
    private const string UseImageLoad = "use Image.Load for the flattened image.";

    /// <summary>The displayed frames in order (bottom first); throws NotSupportedException when they are not a stack.</summary>
    public static IReadOnlyList<JxlFrameHeader> Select(IReadOnlyList<JxlFrameHeader> frames, JxlImageMetadata metadata, int alpha)
    {
        if (metadata.Animation is not null)
        {
            throw JxlDescriptor.Unsupported("animations have frames, not layers; " + UseImageLoad);
        }

        // Which layer each reference slot holds: -1 for an empty slot, -2 for a frame that is not a layer.
        int[] owner = [-1, -1, -1, -1];
        var layers = new List<JxlFrameHeader>();
        foreach (JxlFrameHeader frame in frames)
        {
            if (frame.Type == JxlFrameType.Dc)
            {
                continue;
            }

            if (frame.Type == JxlFrameType.ReferenceOnly)
            {
                owner[frame.SaveAsReference] = -2;
                continue;
            }

            if (frame.SaveBeforeColorTransform)
            {
                throw NotAStack("a displayed frame is saved before the colour transform");
            }

            if (JxlBlending.NeedsBlending(frame))
            {
                int previous = layers.Count == 0 ? -1 : layers.Count - 1;
                if (owner[frame.Blending.Source] != previous)
                {
                    throw NotAStack(layers.Count == 0
                        ? "the first layer blends onto a slot that is not empty"
                        : "a layer does not blend onto the previous layer");
                }

                for (int i = 0; i < frame.ExtraChannelBlending.Count; i++)
                {
                    JxlBlendingInfo channel = frame.ExtraChannelBlending[i];
                    if (channel.Source != frame.Blending.Source)
                    {
                        throw NotAStack("an extra channel blends onto another slot than the colour");
                    }

                    if (i == alpha && (channel.Mode != frame.Blending.Mode || channel.Clamp != frame.Blending.Clamp))
                    {
                        throw NotAStack("the alpha channel blends differently from the colour");
                    }
                }

                if (alpha >= 0 && frame.Blending.Mode is JxlBlendMode.Blend or JxlBlendMode.AlphaWeightedAdd
                    && frame.Blending.AlphaChannel != alpha)
                {
                    throw NotAStack("a layer blends with another alpha channel than the image's");
                }
            }

            if (!frame.IsLast)
            {
                owner[frame.SaveAsReference] = layers.Count;
            }

            layers.Add(frame);
        }

        return layers;
    }

    private static NotSupportedException NotAStack(string reason) =>
        JxlDescriptor.Unsupported($"the frames are not a simple layer stack ({reason}); " + UseImageLoad);
}
