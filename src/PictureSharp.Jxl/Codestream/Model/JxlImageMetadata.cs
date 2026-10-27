namespace PictureSharp.Formats.Jxl.Codestream.Model;

// Image metadata bundles. Ported from libjxl v0.12.0 lib/jxl/image_metadata.cc (BitDepth, ExtraChannelInfo,
// ToneMapping and ImageMetadata VisitFields), BSD-3-Clause.

/// <summary>Sample precision: unsigned integers of 1–31 bits, or floats with 2–8 exponent and 2–23 mantissa bits.</summary>
internal readonly record struct JxlBitDepth
{
    public static JxlBitDepth Default { get; } = new(false, 8, 0);

    public JxlBitDepth(bool floatingPoint, uint bitsPerSample, uint exponentBits)
    {
        FloatingPoint = floatingPoint;
        BitsPerSample = bitsPerSample;
        ExponentBits = exponentBits;
        Validate();
    }

    public bool FloatingPoint { get; }
    public uint BitsPerSample { get; }
    public uint ExponentBits { get; }

    public void Validate()
    {
        long mantissa = (long)BitsPerSample - ExponentBits - 1;
        if (FloatingPoint ? ExponentBits is < 2 or > 8 || mantissa is < 2 or > 23
            : BitsPerSample is < 1 or > 31 || ExponentBits != 0)
            throw JxlDescriptor.Corrupt("Invalid sample precision.");
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

    /// <summary>The upsampling factor implied by <see cref="DimShift"/>.</summary>
    public uint Upsampling => 1u << (int)DimShift;

    public string Name { get; init; } = "";

    public bool AlphaAssociated { get; init; }

    /// <summary>Linear RGB and opacity of a spot colour channel.</summary>
    public IReadOnlyList<float>? SpotColor { get; init => field = value?.Snapshot(); }

    public uint CfaChannel { get; init; } = 1;
}

/// <summary>Luminance hints: peak intensity in nits, minimum nits and the linear segment for tone mapping.</summary>
internal sealed record JxlToneMapping(float IntensityTarget, float MinNits, bool RelativeToMaxDisplay, float LinearBelow)
{
    public static JxlToneMapping Default { get; } = new(255f, 0f, false, 0f);
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

    public IReadOnlyList<JxlExtraChannelInfo> ExtraChannels { get; init => field = value.Snapshot(); } = [];

    /// <summary>Whether the colour channels are stored in XYB (lossy VarDCT/Modular), not in <see cref="Color"/>.</summary>
    public bool XybEncoded { get; init; } = true;

    public JxlColorEncoding Color { get; init; } = JxlColorEncoding.Srgb;

    public JxlToneMapping ToneMapping { get; init; } = JxlToneMapping.Default;

    /// <summary>The extension bits present; all are skipped, as libjxl v0.12.0 defines none for this bundle.</summary>
    public ulong Extensions { get; init; }

    public bool HasAlpha => ExtraChannels.Any(channel => channel.Type == JxlExtraChannelType.Alpha);
}
