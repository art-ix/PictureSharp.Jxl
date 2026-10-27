using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlFrameHeader using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlFrameHeaderReader
{
    /// <summary>Reads a frame header; <paramref name="preview"/> selects the preview size as the default size.</summary>
    public static JxlFrameHeader Read(ref JxlBitReader reader, JxlHeaders headers, bool preview)
    {
        JxlImageMetadata metadata = headers.Metadata;
        (uint defaultWidth, uint defaultHeight) = preview && metadata.Preview is { } p
            ? (p.Width, p.Height)
            : (headers.Size.Width, headers.Size.Height);
        if (reader.ReadBool())
        {
            return DefaultFrame(metadata, defaultWidth, defaultHeight);
        }

        var type = (JxlFrameType)JxlFields.U32(ref reader, Val(0), Val(1), Val(2), Val(3));
        if (preview && type != JxlFrameType.Regular)
        {
            throw JxlDescriptor.Corrupt("Only a regular frame can be a preview.");
        }

        var encoding = reader.ReadBool() ? JxlFrameEncoding.Modular : JxlFrameEncoding.VarDct;
        var flags = (JxlFrameFlags)JxlFields.U64(ref reader);
        JxlColorTransform transform = metadata.XybEncoded ? JxlColorTransform.Xyb
            : reader.ReadBool() ? JxlColorTransform.YCbCr : JxlColorTransform.None;
        bool dcFrame = flags.HasFlag(JxlFrameFlags.UseDcFrame);
        uint[] chroma = transform == JxlColorTransform.YCbCr && !dcFrame
            ? [reader.ReadBits(2), reader.ReadBits(2), reader.ReadBits(2)]
            : [0, 0, 0];
        (uint upsampling, uint[] ecUpsampling) = dcFrame
            ? (1u, [.. metadata.ExtraChannels.Select(channel => channel.Upsampling)])
            : ReadUpsampling(ref reader, metadata);

        int groupSizeShift = encoding == JxlFrameEncoding.Modular ? (int)reader.ReadBits(2) : 1;
        (uint xQmScale, uint bQmScale) = encoding == JxlFrameEncoding.VarDct && transform == JxlColorTransform.Xyb
            ? (reader.ReadBits(3), reader.ReadBits(3))
            : (2u, 2u);
        JxlPasses passes = type != JxlFrameType.ReferenceOnly ? JxlPassesReader.Read(ref reader) : JxlPasses.Single;
        uint dcLevel = type == JxlFrameType.Dc ? JxlFields.U32(ref reader, Val(1), Val(2), Val(3), Val(4)) : 0;

        bool positioned = type is JxlFrameType.Regular or JxlFrameType.SkipProgressive;
        FrameCrop crop = type != JxlFrameType.Dc
            ? ReadCrop(ref reader, positioned, defaultWidth, defaultHeight)
            : FrameCrop.None(defaultWidth, defaultHeight);
        FramePlacement placement = positioned
            ? ReadPlacement(ref reader, metadata, crop, preview)
            : FramePlacement.Default;

        uint saveAsReference = type != JxlFrameType.Dc && !placement.IsLast ? JxlFields.U32(ref reader, Val(0), Val(1), Val(2), Val(3)) : 0;
        bool saveBefore = type == JxlFrameType.Dc
            || ReadSaveBeforeColorTransform(ref reader, headers.Size, type, positioned, crop, placement, saveAsReference);

        string name = JxlFields.Name(ref reader);
        JxlLoopFilter loopFilter = JxlLoopFilterReader.Read(ref reader, encoding == JxlFrameEncoding.Modular);
        JxlFields.SkipExtensions(ref reader);
        return new JxlFrameHeader
        {
            Type = type,
            Encoding = encoding,
            Flags = flags,
            ColorTransform = transform,
            ChromaModes = chroma,
            Upsampling = upsampling,
            ExtraChannelUpsampling = ecUpsampling,
            GroupSizeShift = groupSizeShift,
            XQmScale = xQmScale,
            BQmScale = bQmScale,
            Passes = passes,
            DcLevel = dcLevel,
            CustomSizeOrOrigin = crop.Custom,
            X0 = crop.X0,
            Y0 = crop.Y0,
            Width = DcScaled(crop.Width, dcLevel),
            Height = DcScaled(crop.Height, dcLevel),
            IsPartial = crop.IsPartial,
            Blending = placement.Blending,
            ExtraChannelBlending = placement.ExtraChannelBlending,
            Duration = placement.Duration,
            IsLast = placement.IsLast,
            SaveAsReference = saveAsReference,
            SaveBeforeColorTransform = saveBefore,
            Name = name,
            LoopFilter = loopFilter,
        };
    }

    // all_default: a VarDCT frame of the default size that is the last one.
    private static JxlFrameHeader DefaultFrame(JxlImageMetadata metadata, uint width, uint height) => new()
    {
        Encoding = JxlFrameEncoding.VarDct,
        ColorTransform = metadata.XybEncoded ? JxlColorTransform.Xyb : JxlColorTransform.None,
        XQmScale = metadata.XybEncoded ? 3u : 2u,
        ExtraChannelUpsampling = [.. metadata.ExtraChannels.Select(channel => channel.Upsampling)],
        ExtraChannelBlending = [.. Enumerable.Repeat(JxlBlendingInfo.Replace, metadata.ExtraChannels.Count)],
        Width = width,
        Height = height,
    };

    // The colour upsampling, then each extra channel's, which includes its dim_shift and is at least the colour one.
    private static (uint Upsampling, uint[] ExtraChannels) ReadUpsampling(ref JxlBitReader reader, JxlImageMetadata metadata)
    {
        uint upsampling = UpsamplingFactor(ref reader);
        uint[] ecUpsampling = new uint[metadata.ExtraChannels.Count];
        for (int i = 0; i < ecUpsampling.Length; i++)
        {
            ecUpsampling[i] = UpsamplingFactor(ref reader) << (int)metadata.ExtraChannels[i].DimShift;
            if (ecUpsampling[i] < upsampling || ecUpsampling[i] > 8)
            {
                throw JxlDescriptor.Corrupt($"Invalid extra channel upsampling {ecUpsampling[i]}.");
            }
        }

        return (upsampling, ecUpsampling);
    }

    private static FrameCrop ReadCrop(ref JxlBitReader reader, bool positioned, uint defaultWidth, uint defaultHeight)
    {
        if (!reader.ReadBool())
        {
            return FrameCrop.None(defaultWidth, defaultHeight);
        }

        (int x0, int y0) = positioned ? (UnpackSigned(FrameCoordinate(ref reader)), UnpackSigned(FrameCoordinate(ref reader))) : (0, 0);
        uint width = FrameCoordinate(ref reader);
        uint height = FrameCoordinate(ref reader);
        if (width == 0 || height == 0)
        {
            throw JxlDescriptor.Corrupt("Invalid frame crop: zero width or height.");
        }

        bool partial = positioned && (x0 > 0 || y0 > 0 || width + (long)x0 < defaultWidth || height + (long)y0 < defaultHeight);
        return new FrameCrop(true, x0, y0, width, height, partial);
    }

    // Blending of the colour and extra channels, the animation duration (and timecode) and the last-frame flag.
    private static FramePlacement ReadPlacement(ref JxlBitReader reader, JxlImageMetadata metadata, FrameCrop crop, bool preview)
    {
        int extraChannels = metadata.ExtraChannels.Count;
        JxlBlendingInfo blending = JxlBlendingInfoReader.Read(ref reader, extraChannels, crop.IsPartial);
        var ecBlending = new JxlBlendingInfo[extraChannels];
        for (int i = 0; i < ecBlending.Length; i++)
        {
            ecBlending[i] = JxlBlendingInfoReader.Read(ref reader, extraChannels, crop.IsPartial);
        }

        if (preview && (crop.Custom || blending.Mode != JxlBlendMode.Replace || ecBlending.Any(b => b.Mode != JxlBlendMode.Replace)))
        {
            throw JxlDescriptor.Corrupt("A preview frame cannot be blended.");
        }

        uint duration = 0;
        if (metadata.Animation is { } animation)
        {
            duration = JxlFields.U32(ref reader, Val(0), Val(1), Bits32(8), Bits32(32));
            if (animation.HaveTimecodes)
            {
                reader.ReadBits(32);
            }
        }

        return new FramePlacement(blending, ecBlending, duration, reader.ReadBool());
    }

    private static bool ReadSaveBeforeColorTransform(ref JxlBitReader reader, JxlSizeHeader size, JxlFrameType type, bool positioned,
        FrameCrop crop, FramePlacement placement, uint saveAsReference)
    {
        bool referenced = !placement.IsLast && (placement.Duration == 0 || saveAsReference != 0);
        if (referenced && placement.Blending.Mode == JxlBlendMode.Replace && !crop.IsPartial && positioned)
        {
            return reader.ReadBool();
        }

        if (type != JxlFrameType.ReferenceOnly)
        {
            return false;
        }

        bool saveBefore = reader.ReadBool();
        if (!saveBefore && (crop.Width < size.Width || crop.Height < size.Height || crop.X0 != 0 || crop.Y0 != 0))
        {
            throw JxlDescriptor.Corrupt("A reference frame that is not a patch source must cover the image.");
        }

        return saveBefore;
    }

    // A DC frame of level n holds the image downsampled by 8^n.
    private static uint DcScaled(uint size, uint dcLevel) => dcLevel != 0 ? (uint)DivCeil(size, 1L << (int)(3 * dcLevel)) : size;

    private static uint UpsamplingFactor(ref JxlBitReader reader) => JxlFields.U32(ref reader, Val(1), Val(2), Val(4), Val(8));

    private static uint FrameCoordinate(ref JxlBitReader reader) =>
        JxlFields.U32(ref reader, Bits32(8), BitsOffset(11, 256), BitsOffset(14, 2304), BitsOffset(30, 18688));

    private readonly record struct FrameCrop(bool Custom, int X0, int Y0, uint Width, uint Height, bool IsPartial)
    {
        public static FrameCrop None(uint width, uint height) => new(false, 0, 0, width, height, false);
    }

    private readonly record struct FramePlacement(JxlBlendingInfo Blending, JxlBlendingInfo[] ExtraChannelBlending, uint Duration, bool IsLast)
    {
        public static FramePlacement Default { get; } = new(JxlBlendingInfo.Replace, [], 0, false);
    }
}
