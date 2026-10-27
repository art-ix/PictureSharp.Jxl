using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Frames;

/// <summary>Current still-image support and frame consistency rules.</summary>
internal static class JxlDecodeSupportPolicy
{
    /// <summary>Checks the image-level features and returns the index of the alpha extra channel, or -1.</summary>
    public static int EnsureSupported(JxlImageMetadata metadata)
    {
        metadata.BitDepth.Validate();
        foreach (JxlExtraChannelInfo channel in metadata.ExtraChannels) channel.BitDepth.Validate();
        if (metadata.Animation is not null)
        {
            throw JxlDescriptor.Unsupported("animations are not decoded yet.");
        }

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

    public static void EnsureSupported(JxlFrameHeader frame, JxlHeaders headers, JxlReferenceFrames references)
    {
        if (frame.Type == JxlFrameType.SkipProgressive || (frame.Type == JxlFrameType.Regular && !frame.IsLast))
        {
            throw JxlDescriptor.Unsupported("images of several displayed frames (layers) are not decoded yet.");
        }

        if (frame.Type != JxlFrameType.Regular && frame.IsLast)
        {
            throw JxlDescriptor.Corrupt("The last frame is not a regular frame.");
        }

        if ((frame.Flags & (JxlFrameFlags.Noise | JxlFrameFlags.Splines)) != 0)
        {
            throw JxlDescriptor.Unsupported("noise and splines are not rendered yet.");
        }

        if ((frame.Flags & JxlFrameFlags.Patches) != 0 && frame.Upsampling != 1
            && frame.ExtraChannelUpsampling.Any(factor => factor != frame.Upsampling))
        {
            throw JxlDescriptor.Corrupt("Patches need extra channels upsampled like the colour channels.");
        }

        if ((frame.Flags & JxlFrameFlags.UseDcFrame) != 0 && !references.HasDc(frame.DcLevel))
        {
            throw JxlDescriptor.Corrupt("A frame uses a DC frame that was not decoded.");
        }

        if (frame.Type == JxlFrameType.Regular
            && (frame.X0 != 0 || frame.Y0 != 0 || frame.Width != headers.Size.Width || frame.Height != headers.Size.Height
                || frame.Blending.Mode != JxlBlendMode.Replace || frame.ExtraChannelBlending.Any(b => b.Mode != JxlBlendMode.Replace)))
        {
            throw JxlDescriptor.Unsupported("cropped or blended frames are not decoded yet.");
        }

        if (frame.Encoding == JxlFrameEncoding.VarDct && !frame.Is444 && (frame.Flags & JxlFrameFlags.SkipAdaptiveDcSmoothing) == 0)
        {
            throw JxlDescriptor.Corrupt("Chroma subsampling requires adaptive DC smoothing to be disabled.");
        }
    }
}
