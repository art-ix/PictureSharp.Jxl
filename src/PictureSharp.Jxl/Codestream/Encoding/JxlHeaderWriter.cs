using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// Writes the codestream headers, the counterpart of the IO readers: signature, SizeHeader, ImageMetadata (all fields
/// the readers model, without extensions), the default CustomTransformData, the frame header of a lossless Modular
/// frame and the TOC. Field order from libjxl v0.12.0 lib/jxl/headers.cc, image_metadata.cc,
/// color_encoding_internal.cc, frame_header.cc and toc.cc (BSD-3-Clause).
/// </summary>
internal static class JxlHeaderWriter
{
    public static void Signature(JxlBitWriter writer) => writer.Write(16, JxlDescriptor.CodestreamMarker);

    public static void Size(JxlBitWriter writer, JxlSizeHeader size)
    {
        (uint width, uint height) = (size.Width, size.Height);
        uint ratio = Ratio(width, height);
        bool small = height % 8 == 0 && height is >= 8 and <= 256 && (ratio != 0 || width % 8 == 0 && width is >= 8 and <= 256);
        writer.Write(small);
        if (small) writer.Write(5, height / 8 - 1);
        else Dimension(writer, height);
        writer.Write(3, ratio);
        if (ratio != 0) return;
        if (small) writer.Write(5, width / 8 - 1);
        else Dimension(writer, width);
    }

    private static uint Ratio(uint width, uint height)
    {
        for (uint ratio = 1; ratio <= 7; ratio++)
        {
            if (JxlAspectRatio.Width(ratio, height) == width) return ratio;
        }

        return 0;
    }

    private static void Dimension(JxlBitWriter writer, uint value) =>
        JxlFieldWriter.U32(writer, value, BitsOffset(9, 1), BitsOffset(13, 1), BitsOffset(18, 1), BitsOffset(30, 1));

    public static void ImageMetadata(JxlBitWriter writer, JxlImageMetadata metadata)
    {
        if (metadata.Extensions != 0) throw JxlDescriptor.Unsupported("image metadata extensions cannot be written.");
        bool extraFields = metadata.Orientation != 1 || metadata.IntrinsicSize is not null || metadata.Preview is not null
            || metadata.Animation is not null || metadata.ToneMapping != JxlToneMapping.Default;
        bool allDefault = !extraFields && metadata.BitDepth == JxlBitDepth.Default && metadata.Modular16BitBufferSufficient
            && metadata.ExtraChannels.Count == 0 && metadata.XybEncoded && IsDefault(metadata.Color);
        writer.Write(allDefault);
        if (allDefault) return;
        writer.Write(extraFields);
        if (extraFields)
        {
            writer.Write(3, metadata.Orientation - 1);
            writer.Write(metadata.IntrinsicSize is not null);
            if (metadata.IntrinsicSize is { } intrinsic) Size(writer, intrinsic);
            writer.Write(metadata.Preview is not null);
            if (metadata.Preview is { } preview) Preview(writer, preview);
            writer.Write(metadata.Animation is not null);
            if (metadata.Animation is { } animation)
            {
                JxlFieldWriter.U32(writer, animation.TicksNumerator, Val(100), Val(1000), BitsOffset(10, 1), BitsOffset(30, 1));
                JxlFieldWriter.U32(writer, animation.TicksDenominator, Val(1), Val(1001), BitsOffset(8, 1), BitsOffset(10, 1));
                JxlFieldWriter.U32(writer, animation.Loops, Val(0), Bits32(3), Bits32(16), Bits32(32));
                writer.Write(animation.HaveTimecodes);
            }
        }

        BitDepth(writer, metadata.BitDepth);
        writer.Write(metadata.Modular16BitBufferSufficient);
        JxlFieldWriter.U32(writer, (uint)metadata.ExtraChannels.Count, Val(0), Val(1), BitsOffset(4, 2), BitsOffset(12, 1));
        foreach (JxlExtraChannelInfo channel in metadata.ExtraChannels) ExtraChannel(writer, channel);
        writer.Write(metadata.XybEncoded);
        ColorEncoding(writer, metadata.Color);
        if (extraFields) ToneMapping(writer, metadata.ToneMapping);
        JxlFieldWriter.NoExtensions(writer);
    }

    private static void Preview(JxlBitWriter writer, JxlPreviewHeader preview)
    {
        uint ratio = Ratio(preview.Width, preview.Height);
        bool div8 = preview.Height % 8 == 0 && (ratio != 0 || preview.Width % 8 == 0);
        writer.Write(div8);
        PreviewDimension(writer, preview.Height, div8);
        writer.Write(3, ratio);
        if (ratio == 0) PreviewDimension(writer, preview.Width, div8);
    }

    private static void PreviewDimension(JxlBitWriter writer, uint value, bool div8)
    {
        if (div8) JxlFieldWriter.U32(writer, value / 8, Val(16), Val(32), BitsOffset(5, 1), BitsOffset(9, 33));
        else JxlFieldWriter.U32(writer, value, BitsOffset(6, 1), BitsOffset(8, 65), BitsOffset(10, 321), BitsOffset(12, 1345));
    }

    private static void BitDepth(JxlBitWriter writer, JxlBitDepth depth)
    {
        writer.Write(depth.FloatingPoint);
        if (!depth.FloatingPoint)
        {
            JxlFieldWriter.U32(writer, depth.BitsPerSample, Val(8), Val(10), Val(12), BitsOffset(6, 1));
            return;
        }

        JxlFieldWriter.U32(writer, depth.BitsPerSample, Val(32), Val(16), Val(24), BitsOffset(6, 1));
        writer.Write(4, depth.ExponentBits - 1);
    }

    private static void ExtraChannel(JxlBitWriter writer, JxlExtraChannelInfo channel)
    {
        bool allDefault = channel == JxlExtraChannelInfo.Default;
        writer.Write(allDefault);
        if (allDefault) return;
        JxlFieldWriter.Enum(writer, (uint)channel.Type);
        BitDepth(writer, channel.BitDepth);
        JxlFieldWriter.U32(writer, channel.DimShift, Val(0), Val(3), Val(4), BitsOffset(3, 1));
        JxlFieldWriter.Name(writer, channel.Name);
        switch (channel.Type)
        {
            case JxlExtraChannelType.Alpha:
                writer.Write(channel.AlphaAssociated);
                break;
            case JxlExtraChannelType.SpotColor:
                foreach (float value in channel.SpotColor ?? [0, 0, 0, 0]) JxlFieldWriter.F16(writer, value);
                break;
            case JxlExtraChannelType.Cfa:
                JxlFieldWriter.U32(writer, channel.CfaChannel, Val(1), Bits32(2), BitsOffset(4, 3), BitsOffset(8, 19));
                break;
        }
    }

    private static bool IsDefault(JxlColorEncoding color) => color == JxlColorEncoding.Srgb;

    public static void ColorEncoding(JxlBitWriter writer, JxlColorEncoding color)
    {
        bool allDefault = IsDefault(color);
        writer.Write(allDefault);
        if (allDefault) return;
        writer.Write(color.WantIcc);
        JxlFieldWriter.Enum(writer, (uint)color.ColorSpace);
        if (color.WantIcc) return;
        if (color.ColorSpace != JxlColorSpace.Xyb)
        {
            JxlFieldWriter.Enum(writer, (uint)color.WhitePoint);
            if (color.WhitePoint == JxlWhitePoint.Custom) Chromaticity(writer, color.CustomWhitePoint);
        }

        if (color.HasPrimaries)
        {
            JxlFieldWriter.Enum(writer, (uint)color.Primaries);
            if (color.Primaries == JxlPrimaries.Custom)
            {
                Chromaticity(writer, color.CustomPrimaries.Red);
                Chromaticity(writer, color.CustomPrimaries.Green);
                Chromaticity(writer, color.CustomPrimaries.Blue);
            }
        }

        if (color.ColorSpace != JxlColorSpace.Xyb)
        {
            writer.Write(color.Gamma != 0);
            if (color.Gamma != 0) writer.Write(24, color.Gamma);
            else JxlFieldWriter.Enum(writer, (uint)color.TransferFunction);
        }

        JxlFieldWriter.Enum(writer, (uint)color.RenderingIntent);
    }

    private static void Chromaticity(JxlBitWriter writer, JxlChromaticity xy)
    {
        Coordinate(writer, xy.X);
        Coordinate(writer, xy.Y);
    }

    private static void Coordinate(JxlBitWriter writer, int value) => JxlFieldWriter.U32(writer, PackSigned(value),
        Bits32(19), BitsOffset(19, 524288), BitsOffset(20, 1048576), BitsOffset(21, 2097152));

    private static void ToneMapping(JxlBitWriter writer, JxlToneMapping tone)
    {
        bool allDefault = tone == JxlToneMapping.Default;
        writer.Write(allDefault);
        if (allDefault) return;
        JxlFieldWriter.F16(writer, tone.IntensityTarget);
        JxlFieldWriter.F16(writer, tone.MinNits);
        writer.Write(tone.RelativeToMaxDisplay);
        JxlFieldWriter.F16(writer, tone.LinearBelow);
    }

    /// <summary>All-default CustomTransformData: default opsin matrix and upsampling weights.</summary>
    public static void DefaultTransformData(JxlBitWriter writer) => writer.Write(true);

    /// <summary>
    /// The header of the only frame of a still image: regular, Modular, no YCbCr, no upsampling, the given group size,
    /// one pass, replace blending, last, unnamed, without loop filters. The image metadata must not be XYB-encoded.
    /// </summary>
    public static void LosslessModularFrame(JxlBitWriter writer, int extraChannels, int groupSizeShift)
    {
        writer.Write(false);              // all_default (VarDCT)
        writer.Write(2, 0);               // frame type: regular
        writer.Write(true);               // Modular
        JxlFieldWriter.U64(writer, 0);    // flags
        writer.Write(false);              // do_YCbCr
        writer.Write(2, 0);               // upsampling 1
        for (int i = 0; i < extraChannels; i++) writer.Write(2, 0);
        writer.Write(2, (uint)groupSizeShift);
        writer.Write(2, 0);               // one pass
        writer.Write(false);              // custom size or origin
        for (int i = 0; i <= extraChannels; i++) writer.Write(2, 0); // blend mode: replace
        writer.Write(true);               // is_last
        writer.Write(2, 0);               // name: empty
        writer.Write(false);              // loop filter not all default
        writer.Write(false);              // no Gaborish
        writer.Write(2, 0);               // no edge-preserving filter
        JxlFieldWriter.NoExtensions(writer);
        JxlFieldWriter.NoExtensions(writer);
    }

    /// <summary>
    /// A frame header as <see cref="IO.JxlFrameHeaderReader"/> reads it, for the frames this encoder writes: regular
    /// or reference-only, one pass, no animation, replace blending, an optional custom size without an origin, and
    /// loop filters with default parameters.
    /// </summary>
    public static void Frame(JxlBitWriter writer, JxlFrameHeader frame, JxlImageMetadata metadata)
    {
        if (frame.Type is not (JxlFrameType.Regular or JxlFrameType.ReferenceOnly) || frame.Passes.Count != 1 || frame.X0 != 0 || frame.Y0 != 0
            || metadata.Animation is not null || frame.Name.Length != 0)
        {
            throw new ArgumentException("The frame header cannot be written.", nameof(frame));
        }

        writer.Write(false);                                      // all_default
        writer.Write(2, (uint)frame.Type);
        writer.Write(frame.Encoding == JxlFrameEncoding.Modular);
        JxlFieldWriter.U64(writer, (ulong)frame.Flags);
        if (!metadata.XybEncoded) writer.Write(frame.ColorTransform == JxlColorTransform.YCbCr);
        if (frame.ColorTransform == JxlColorTransform.YCbCr)
            foreach (uint mode in frame.ChromaModes) writer.Write(2, mode);
        writer.Write(2, UpsamplingSelector(frame.Upsampling));
        for (int i = 0; i < metadata.ExtraChannels.Count; i++)
        {
            writer.Write(2, UpsamplingSelector(frame.ExtraChannelUpsampling[i] >> (int)metadata.ExtraChannels[i].DimShift));
        }

        if (frame.Encoding == JxlFrameEncoding.Modular) writer.Write(2, (uint)frame.GroupSizeShift);
        if (frame.Encoding == JxlFrameEncoding.VarDct && metadata.XybEncoded)
        {
            writer.Write(3, frame.XQmScale);
            writer.Write(3, frame.BQmScale);
        }

        if (frame.Type != JxlFrameType.ReferenceOnly) writer.Write(2, 0);   // one pass
        writer.Write(frame.CustomSizeOrOrigin);
        bool positioned = frame.Type == JxlFrameType.Regular;
        if (frame.CustomSizeOrOrigin)
        {
            if (positioned)
            {
                FrameCoordinate(writer, 0);
                FrameCoordinate(writer, 0);
            }

            FrameCoordinate(writer, frame.Width);
            FrameCoordinate(writer, frame.Height);
        }

        if (positioned)
        {
            for (int i = 0; i <= metadata.ExtraChannels.Count; i++) writer.Write(2, 0);   // blend mode: replace
            writer.Write(frame.IsLast);
        }

        bool last = positioned && frame.IsLast;
        if (!last) writer.Write(2, frame.SaveAsReference);
        if (!last && (positioned ? !frame.IsPartial : frame.Type == JxlFrameType.ReferenceOnly))
        {
            writer.Write(frame.SaveBeforeColorTransform);
        }

        writer.Write(2, 0);                                       // name: empty
        LoopFilter(writer, frame.LoopFilter, frame.Encoding == JxlFrameEncoding.Modular);
        JxlFieldWriter.NoExtensions(writer);
    }

    private static uint UpsamplingSelector(uint upsampling) => upsampling switch
    {
        1 => 0,
        2 => 1,
        4 => 2,
        8 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(upsampling)),
    };

    private static void FrameCoordinate(JxlBitWriter writer, uint value) =>
        JxlFieldWriter.U32(writer, value, Bits32(8), BitsOffset(11, 256), BitsOffset(14, 2304), BitsOffset(30, 18688));

    // Gaborish and EPF with their default weights and parameters.
    private static void LoopFilter(JxlBitWriter writer, JxlLoopFilter filter, bool modular)
    {
        bool allDefault = filter.Gaborish && filter.EpfIterations == 2 && !modular;
        writer.Write(allDefault);
        if (allDefault) return;
        writer.Write(filter.Gaborish);
        if (filter.Gaborish) writer.Write(false);                 // default Gaborish weights
        writer.Write(2, filter.EpfIterations);
        if (filter.EpfIterations > 0)
        {
            if (modular) throw new ArgumentException("EPF in Modular frames cannot be written.", nameof(filter));
            writer.Write(false);                                  // default sharpness LUT
            writer.Write(false);                                  // default channel scales
            writer.Write(false);                                  // default sigma parameters
        }

        JxlFieldWriter.NoExtensions(writer);
    }

    /// <summary>A TOC without permutation; the writer is byte-aligned afterwards.</summary>
    public static void Toc(JxlBitWriter writer, IReadOnlyList<int> sizes)
    {
        writer.Write(false);
        writer.ZeroPadToByte();
        foreach (int size in sizes)
        {
            JxlFieldWriter.U32(writer, (uint)size, Bits32(10), BitsOffset(14, 1024), BitsOffset(22, 17408), BitsOffset(30, 4211712));
        }

        writer.ZeroPadToByte();
    }
}
