using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Frames;

/// <summary>
/// Current support (still images, layers blended since 0.8.10, animations since 0.8.12, noise and splines since 0.8.15,
/// extra channels since 0.8.16) and frame consistency rules.
/// </summary>
internal static class JxlDecodeSupportPolicy
{
    /// <summary>
    /// Checks the image-level features and returns the index of the alpha extra channel (the first one), or -1.
    /// Premultiplied alpha and spot colours are rendered; every other extra channel type is decoded and dropped. CMYK and
    /// float samples need PictureSharp core support (a CMS for CMYK profiles, a float pixel type).
    /// </summary>
    public static int EnsureImageSupported(JxlImageMetadata metadata)
    {
        metadata.BitDepth.Validate();
        foreach (JxlExtraChannelInfo channel in metadata.ExtraChannels) channel.BitDepth.Validate();
        if (metadata.ExtraChannels.Any(channel => channel.Type == JxlExtraChannelType.Black))
        {
            throw JxlDescriptor.Unsupported(
                "CMYK images (a Black extra channel) need colour management for CMYK profiles, which PictureSharp does not provide yet.");
        }

        if ((metadata.BitDepth.FloatingPoint && !metadata.XybEncoded) || metadata.ExtraChannels.Any(channel => channel.BitDepth.FloatingPoint))
        {
            throw JxlDescriptor.Unsupported("floating-point samples need a float pixel type, which PictureSharp does not provide yet.");
        }

        for (int i = 0; i < metadata.ExtraChannels.Count; i++)
        {
            if (metadata.ExtraChannels[i].Type == JxlExtraChannelType.Alpha) return i;
        }

        return -1;
    }

    public static void EnsureFrameSupported(JxlFrameHeader frame, JxlReferenceFrames references)
    {
        // Only regular and skip-progressive frames are displayed; libjxl reads is_last for no other type.
        if (frame.Type is not (JxlFrameType.Regular or JxlFrameType.SkipProgressive) && frame.IsLast)
        {
            throw JxlDescriptor.Corrupt("The last frame is not a displayed frame.");
        }

        if (frame.HasPatches && frame.Upsampling != 1
            && frame.ExtraChannelUpsampling.Any(factor => factor != frame.Upsampling))
        {
            throw JxlDescriptor.Corrupt("Patches need extra channels upsampled like the colour channels.");
        }

        if (frame.UsesDcFrame && !references.HasDc(frame.DcLevel))
        {
            throw JxlDescriptor.Corrupt("A frame uses a DC frame that was not decoded.");
        }

        if (frame.Encoding == JxlFrameEncoding.VarDct && !frame.Is444 && !frame.SkipsAdaptiveDcSmoothing)
        {
            throw JxlDescriptor.Corrupt("Chroma subsampling requires adaptive DC smoothing to be disabled.");
        }
    }
}
