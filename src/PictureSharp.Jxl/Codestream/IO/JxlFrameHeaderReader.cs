using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlFrameHeader;

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
        int extraChannels = metadata.ExtraChannels.Count;
        if (reader.ReadBool())
        {
            return new JxlFrameHeader
            {
                Encoding = JxlFrameEncoding.VarDct,
                ColorTransform = metadata.XybEncoded ? JxlColorTransform.Xyb : JxlColorTransform.None,
                XQmScale = metadata.XybEncoded ? 3u : 2u,
                ExtraChannelUpsampling = [.. metadata.ExtraChannels.Select(channel => 1u << (int)channel.DimShift)],
                ExtraChannelBlending = [.. Enumerable.Repeat(new JxlBlendingInfo(JxlBlendMode.Replace, 0, false, 0), extraChannels)],
                Width = defaultWidth,
                Height = defaultHeight,
            };
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
        bool dcFrame = (flags & JxlFrameFlags.UseDcFrame) != 0;
        uint[] chroma = [0, 0, 0];
        if (transform == JxlColorTransform.YCbCr && !dcFrame)
        {
            chroma = [reader.ReadBits(2), reader.ReadBits(2), reader.ReadBits(2)];
        }

        uint upsampling = 1;
        uint[] ecUpsampling = [.. metadata.ExtraChannels.Select(channel => 1u << (int)channel.DimShift)];
        if (!dcFrame)
        {
            upsampling = JxlFields.U32(ref reader, Val(1), Val(2), Val(4), Val(8));
            for (int i = 0; i < extraChannels; i++)
            {
                ecUpsampling[i] = JxlFields.U32(ref reader, Val(1), Val(2), Val(4), Val(8)) << (int)metadata.ExtraChannels[i].DimShift;
                if (ecUpsampling[i] < upsampling || ecUpsampling[i] > 8)
                {
                    throw JxlDescriptor.Corrupt($"Invalid extra channel upsampling {ecUpsampling[i]}.");
                }
            }
        }

        int groupSizeShift = encoding == JxlFrameEncoding.Modular ? (int)reader.ReadBits(2) : 1;
        (uint xQmScale, uint bQmScale) = (2, 2);
        if (encoding == JxlFrameEncoding.VarDct && transform == JxlColorTransform.Xyb)
        {
            (xQmScale, bQmScale) = (reader.ReadBits(3), reader.ReadBits(3));
        }

        JxlPasses passes = type != JxlFrameType.ReferenceOnly ? JxlPassesReader.Read(ref reader) : JxlPasses.Single;
        uint dcLevel = type == JxlFrameType.Dc ? JxlFields.U32(ref reader, Val(1), Val(2), Val(3), Val(4)) : 0;

        bool custom = false;
        int x0 = 0;
        int y0 = 0;
        uint width = defaultWidth;
        uint height = defaultHeight;
        bool partial = false;
        bool positioned = type is JxlFrameType.Regular or JxlFrameType.SkipProgressive;
        if (type != JxlFrameType.Dc)
        {
            custom = reader.ReadBool();
            if (custom)
            {
                if (positioned)
                {
                    x0 = UnpackSigned(FrameCoordinate(ref reader));
                    y0 = UnpackSigned(FrameCoordinate(ref reader));
                }

                width = FrameCoordinate(ref reader);
                height = FrameCoordinate(ref reader);
                if (width == 0 || height == 0)
                {
                    throw JxlDescriptor.Corrupt("Invalid frame crop: zero width or height.");
                }

                partial = positioned && (x0 > 0 || y0 > 0 || width + (long)x0 < defaultWidth || height + (long)y0 < defaultHeight);
            }
        }

        var blending = new JxlBlendingInfo(JxlBlendMode.Replace, 0, false, 0);
        JxlBlendingInfo[] ecBlending = [];
        uint duration = 0;
        bool isLast = false;
        if (positioned)
        {
            blending = JxlBlendingInfoReader.Read(ref reader, extraChannels, partial);
            ecBlending = new JxlBlendingInfo[extraChannels];
            for (int i = 0; i < ecBlending.Length; i++)
            {
                ecBlending[i] = JxlBlendingInfoReader.Read(ref reader, extraChannels, partial);
            }

            if (preview && (custom || blending.Mode != JxlBlendMode.Replace || ecBlending.Any(b => b.Mode != JxlBlendMode.Replace)))
            {
                throw JxlDescriptor.Corrupt("A preview frame cannot be blended.");
            }

            if (metadata.Animation is { } animation)
            {
                duration = JxlFields.U32(ref reader, Val(0), Val(1), Bits32(8), Bits32(32));
                if (animation.HaveTimecodes)
                {
                    reader.ReadBits(32);
                }
            }

            isLast = reader.ReadBool();
        }

        uint saveAsReference = 0;
        if (type != JxlFrameType.Dc && !isLast)
        {
            saveAsReference = JxlFields.U32(ref reader, Val(0), Val(1), Val(2), Val(3));
        }

        bool saveBefore = true;
        if (type != JxlFrameType.Dc)
        {
            bool referenced = !isLast && (duration == 0 || saveAsReference != 0);
            if (referenced && blending.Mode == JxlBlendMode.Replace && !partial && positioned)
            {
                saveBefore = reader.ReadBool();
            }
            else if (type == JxlFrameType.ReferenceOnly)
            {
                saveBefore = reader.ReadBool();
                if (!saveBefore && (width < headers.Size.Width || height < headers.Size.Height || x0 != 0 || y0 != 0))
                {
                    throw JxlDescriptor.Corrupt("A reference frame that is not a patch source must cover the image.");
                }
            }
            else
            {
                saveBefore = false;
            }
        }

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
            CustomSizeOrOrigin = custom,
            X0 = x0,
            Y0 = y0,
            Width = dcLevel != 0 ? (uint)DivCeil(width, 1L << (int)(3 * dcLevel)) : width,
            Height = dcLevel != 0 ? (uint)DivCeil(height, 1L << (int)(3 * dcLevel)) : height,
            IsPartial = partial,
            Blending = blending,
            ExtraChannelBlending = ecBlending,
            Duration = duration,
            IsLast = isLast,
            SaveAsReference = saveAsReference,
            SaveBeforeColorTransform = saveBefore,
            Name = name,
            LoopFilter = loopFilter,
        };
    }

    private static uint FrameCoordinate(ref JxlBitReader reader) =>
        JxlFields.U32(ref reader, Bits32(8), BitsOffset(11, 256), BitsOffset(14, 2304), BitsOffset(30, 18688));

    private static int UnpackSigned(uint value) => (int)((value >> 1) ^ (((~value) & 1) - 1));

    private static long DivCeil(long value, long divisor) => (value + divisor - 1) / divisor;
}
