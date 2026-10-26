using static PictureSharp.Formats.Jxl.Codestream.JxlU32;

namespace PictureSharp.Formats.Jxl.Codestream;

// Image metadata bundles. Ported from libjxl v0.12.0 lib/jxl/image_metadata.cc (BitDepth, ExtraChannelInfo,
// ToneMapping and ImageMetadata VisitFields), BSD-3-Clause.

/// <summary>Sample precision: unsigned integers of 1–31 bits, or floats with 2–8 exponent and 2–23 mantissa bits.</summary>
internal sealed record JxlBitDepth(bool FloatingPoint, uint BitsPerSample, uint ExponentBits)
{
    public static JxlBitDepth Default { get; } = new(false, 8, 0);

    public static JxlBitDepth Read(ref JxlBitReader reader)
    {
        if (!reader.ReadBool())
        {
            uint bits = JxlFields.U32(ref reader, Val(8), Val(10), Val(12), BitsOffset(6, 1));
            return bits <= 31 ? new JxlBitDepth(false, bits, 0) : throw JxlDescriptor.Corrupt($"Invalid bits per sample {bits}.");
        }

        uint floatBits = JxlFields.U32(ref reader, Val(32), Val(16), Val(24), BitsOffset(6, 1));
        uint exponent = reader.ReadBits(4) + 1;
        int mantissa = (int)floatBits - (int)exponent - 1;
        if (exponent is < 2 or > 8 || mantissa is < 2 or > 23)
        {
            throw JxlDescriptor.Corrupt($"Invalid float sample format {floatBits}/{exponent}.");
        }

        return new JxlBitDepth(true, floatBits, exponent);
    }
}

internal enum JxlExtraChannelType : uint
{
    Alpha = 0,
    Depth = 1,
    SpotColor = 2,
    SelectionMask = 3,
    Black = 4,
    Cfa = 5,
    Thermal = 6,
    Optional = 16
}

/// <summary>An extra channel. Reserved and "unknown" types are rejected, as in libjxl.</summary>
internal sealed record JxlExtraChannelInfo
{
    public static JxlExtraChannelInfo Default { get; } = new();

    public JxlExtraChannelType Type { get; init; }

    public JxlBitDepth BitDepth { get; init; } = JxlBitDepth.Default;

    /// <summary>log2 of the downsampling factor, 0–3.</summary>
    public uint DimShift { get; init; }

    public string Name { get; init; } = "";

    public bool AlphaAssociated { get; init; }

    /// <summary>Linear RGB and opacity of a spot colour channel.</summary>
    public float[]? SpotColor { get; init; }

    public uint CfaChannel { get; init; } = 1;

    public static JxlExtraChannelInfo Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        var type = (JxlExtraChannelType)JxlFields.Enum(ref reader, JxlFields.Bits(0, 1, 2, 3, 4, 5, 6, 16), "extra channel type");
        JxlBitDepth bitDepth = JxlBitDepth.Read(ref reader);
        uint dimShift = JxlFields.U32(ref reader, Val(0), Val(3), Val(4), BitsOffset(3, 1));
        if (dimShift > 3)
        {
            throw JxlDescriptor.Corrupt($"Extra channel dim_shift {dimShift} is too large.");
        }

        var info = new JxlExtraChannelInfo { Type = type, BitDepth = bitDepth, DimShift = dimShift, Name = JxlFields.Name(ref reader) };
        switch (type)
        {
            case JxlExtraChannelType.Alpha:
                return info with { AlphaAssociated = reader.ReadBool() };
            case JxlExtraChannelType.SpotColor:
                float[] color = new float[4];
                for (int i = 0; i < color.Length; i++)
                {
                    color[i] = JxlFields.F16(ref reader);
                }

                return info with { SpotColor = color };
            case JxlExtraChannelType.Cfa:
                return info with { CfaChannel = JxlFields.U32(ref reader, Val(1), Bits32(2), BitsOffset(4, 3), BitsOffset(8, 19)) };
            default:
                return info;
        }
    }
}

/// <summary>Luminance hints: peak intensity in nits, minimum nits and the linear segment for tone mapping.</summary>
internal sealed record JxlToneMapping(float IntensityTarget, float MinNits, bool RelativeToMaxDisplay, float LinearBelow)
{
    public static JxlToneMapping Default { get; } = new(255f, 0f, false, 0f);

    public static JxlToneMapping Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        float intensity = JxlFields.F16(ref reader);
        if (intensity <= 0)
        {
            throw JxlDescriptor.Corrupt("Invalid intensity target.");
        }

        float minNits = JxlFields.F16(ref reader);
        if (minNits < 0 || minNits > intensity)
        {
            throw JxlDescriptor.Corrupt("Invalid minimum luminance.");
        }

        bool relative = reader.ReadBool();
        float linearBelow = JxlFields.F16(ref reader);
        if (linearBelow < 0 || (relative && linearBelow > 1))
        {
            throw JxlDescriptor.Corrupt("Invalid linear_below value.");
        }

        return new JxlToneMapping(intensity, minNits, relative, linearBelow);
    }
}

/// <summary>The ImageMetadata bundle of the codestream header.</summary>
internal sealed record JxlImageMetadata
{
    public static JxlImageMetadata Default { get; } = new();

    /// <summary>EXIF-style orientation, 1–8.</summary>
    public uint Orientation { get; init; } = 1;

    public JxlSizeHeader? IntrinsicSize { get; init; }

    public JxlPreviewHeader? Preview { get; init; }

    public JxlAnimationHeader? Animation { get; init; }

    public JxlBitDepth BitDepth { get; init; } = JxlBitDepth.Default;

    public bool Modular16BitBufferSufficient { get; init; } = true;

    public JxlExtraChannelInfo[] ExtraChannels { get; init; } = [];

    /// <summary>Whether the colour channels are stored in XYB (lossy VarDCT/Modular), not in <see cref="Color"/>.</summary>
    public bool XybEncoded { get; init; } = true;

    public JxlColorEncoding Color { get; init; } = JxlColorEncoding.Srgb;

    public JxlToneMapping ToneMapping { get; init; } = JxlToneMapping.Default;

    /// <summary>The extension bits present; all are skipped, as libjxl v0.12.0 defines none for this bundle.</summary>
    public ulong Extensions { get; init; }

    public bool HasAlpha => ExtraChannels.Any(channel => channel.Type == JxlExtraChannelType.Alpha);

    public static JxlImageMetadata Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        var metadata = Default;
        bool extraFields = reader.ReadBool();
        if (extraFields)
        {
            metadata = metadata with { Orientation = reader.ReadBits(3) + 1 };
            if (reader.ReadBool())
            {
                metadata = metadata with { IntrinsicSize = JxlSizeHeader.Read(ref reader) };
            }

            if (reader.ReadBool())
            {
                metadata = metadata with { Preview = JxlPreviewHeader.Read(ref reader) };
            }

            if (reader.ReadBool())
            {
                metadata = metadata with { Animation = JxlAnimationHeader.Read(ref reader) };
            }
        }

        metadata = metadata with { BitDepth = JxlBitDepth.Read(ref reader), Modular16BitBufferSufficient = reader.ReadBool() };
        uint count = JxlFields.U32(ref reader, Val(0), Val(1), BitsOffset(4, 2), BitsOffset(12, 1));
        // Each channel takes at least one bit, so a count beyond the remaining input is truncated, not allocated.
        if (count > reader.RemainingBits)
        {
            throw JxlDescriptor.Corrupt("Truncated codestream.");
        }

        var channels = new JxlExtraChannelInfo[count];
        for (int i = 0; i < channels.Length; i++)
        {
            channels[i] = JxlExtraChannelInfo.Read(ref reader);
        }

        metadata = metadata with { ExtraChannels = channels, XybEncoded = reader.ReadBool(), Color = JxlColorEncoding.Read(ref reader) };
        if (extraFields)
        {
            metadata = metadata with { ToneMapping = JxlToneMapping.Read(ref reader) };
        }

        return metadata with { Extensions = JxlFields.SkipExtensions(ref reader) };
    }
}
