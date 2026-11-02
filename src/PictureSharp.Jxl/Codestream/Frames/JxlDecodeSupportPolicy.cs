using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Frames;

/// <summary>Current support (still images, layers blended since 0.8.10, animations since 0.8.12) and frame consistency rules.</summary>
internal static class JxlDecodeSupportPolicy
{
    /// <summary>Checks the image-level features and returns the index of the alpha extra channel, or -1.</summary>
    public static int EnsureImageSupported(JxlImageMetadata metadata)
    {
        metadata.BitDepth.Validate();
        foreach (JxlExtraChannelInfo channel in metadata.ExtraChannels) channel.BitDepth.Validate();
        if (metadata.BitDepth.FloatingPoint && !metadata.XybEncoded)
        {
            throw JxlDescriptor.Unsupported("floating-point samples are not decoded yet.");
        }

        int alpha = -1;
        for (int i = 0; i < metadata.ExtraChannels.Count; i++)
        {
            JxlExtraChannelInfo channel = metadata.ExtraChannels[i];
            switch (channel.Type)
            {
                case JxlExtraChannelType.Black or JxlExtraChannelType.SpotColor:
                    throw JxlDescriptor.Unsupported($"{channel.Type} extra channels are not decoded yet.");
                case JxlExtraChannelType.Alpha when alpha < 0:
                    if (channel.BitDepth.FloatingPoint || channel.AlphaAssociated)
                    {
                        throw JxlDescriptor.Unsupported("float or premultiplied alpha is not decoded yet.");
                    }

                    alpha = i;
                    break;
            }

            if (channel.BitDepth.FloatingPoint)
            {
                throw JxlDescriptor.Unsupported("floating-point extra channels are not decoded yet.");
            }
        }

        return alpha;
    }

    public static void EnsureFrameSupported(JxlFrameHeader frame, JxlReferenceFrames references)
    {
        // Only regular and skip-progressive frames are displayed; libjxl reads is_last for no other type.
        if (frame.Type is not (JxlFrameType.Regular or JxlFrameType.SkipProgressive) && frame.IsLast)
        {
            throw JxlDescriptor.Corrupt("The last frame is not a displayed frame.");
        }

        if (frame.HasNoise || frame.HasSplines)
        {
            throw JxlDescriptor.Unsupported("noise and splines are not rendered yet.");
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
