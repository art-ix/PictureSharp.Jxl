using static PictureSharp.Formats.Jxl.Codestream.JxlU32;

namespace PictureSharp.Formats.Jxl.Codestream;

// Frame headers. Ported from libjxl v0.12.0 lib/jxl/frame_header.cc, frame_header.h, frame_dimensions.h and
// loop_filter.cc (BSD-3-Clause).

internal enum JxlFrameType : uint { Regular = 0, Dc = 1, ReferenceOnly = 2, SkipProgressive = 3 }

internal enum JxlFrameEncoding { VarDct, Modular }

internal enum JxlColorTransform { Xyb, None, YCbCr }

internal enum JxlBlendMode : uint { Replace = 0, Add = 1, Blend = 2, AlphaWeightedAdd = 3, Mul = 4 }

[Flags]
internal enum JxlFrameFlags : ulong
{
    None = 0,
    Noise = 1,
    Patches = 2,
    Splines = 16,
    UseDcFrame = 32,
    SkipAdaptiveDcSmoothing = 128,
}

/// <summary>How a frame combines with a saved reference frame.</summary>
internal sealed record JxlBlendingInfo(JxlBlendMode Mode, uint AlphaChannel, bool Clamp, uint Source)
{
    public static JxlBlendingInfo Read(ref JxlBitReader reader, int extraChannels, bool partial)
    {
        uint mode = JxlFields.U32(ref reader, Val(0), Val(1), Val(2), BitsOffset(2, 3));
        if (mode > (uint)JxlBlendMode.Mul)
        {
            throw JxlDescriptor.Corrupt("Invalid blend mode.");
        }

        var blend = (JxlBlendMode)mode;
        bool usesAlpha = extraChannels > 0 && blend is JxlBlendMode.Blend or JxlBlendMode.AlphaWeightedAdd;
        uint alpha = 0;
        if (usesAlpha)
        {
            alpha = JxlFields.U32(ref reader, Val(0), Val(1), Val(2), BitsOffset(3, 3));
            if (alpha >= extraChannels)
            {
                throw JxlDescriptor.Corrupt("Invalid alpha channel for blending.");
            }
        }

        bool clamp = (usesAlpha || blend == JxlBlendMode.Mul) && reader.ReadBool();
        uint source = blend != JxlBlendMode.Replace || partial ? JxlFields.U32(ref reader, Val(0), Val(1), Val(2), Val(3)) : 0;
        return new JxlBlendingInfo(blend, alpha, clamp, source);
    }
}

/// <summary>Progressive passes: shifts of the VarDCT coefficients and the downsampling reached after some passes.</summary>
internal sealed record JxlPasses(uint Count, uint[] Shift, uint[] Downsample, uint[] LastPass)
{
    public const int MaxPasses = 11;

    public static JxlPasses Single { get; } = new(1, [0], [], []);

    public static JxlPasses Read(ref JxlBitReader reader)
    {
        uint count = JxlFields.U32(ref reader, Val(1), Val(2), Val(3), BitsOffset(3, 4));
        if (count == 1)
        {
            return Single;
        }

        uint downsamples = JxlFields.U32(ref reader, Val(0), Val(1), Val(2), BitsOffset(1, 3));
        if (downsamples > count)
        {
            throw JxlDescriptor.Corrupt("More downsampling steps than passes.");
        }

        uint[] shift = new uint[count];
        for (int i = 0; i < count - 1; i++)
        {
            shift[i] = reader.ReadBits(2);
        }

        uint[] downsample = new uint[downsamples];
        for (int i = 0; i < downsample.Length; i++)
        {
            downsample[i] = JxlFields.U32(ref reader, Val(1), Val(2), Val(4), Val(8));
            if (i > 0 && downsample[i] >= downsample[i - 1])
            {
                throw JxlDescriptor.Corrupt("The downsampling sequence must decrease.");
            }
        }

        uint[] lastPass = new uint[downsamples];
        for (int i = 0; i < lastPass.Length; i++)
        {
            lastPass[i] = JxlFields.U32(ref reader, Val(0), Val(1), Val(2), Bits32(3));
            if ((i > 0 && lastPass[i] <= lastPass[i - 1]) || lastPass[i] >= count)
            {
                throw JxlDescriptor.Corrupt("Invalid last pass of a downsampling step.");
            }
        }

        return new JxlPasses(count, shift, downsample, lastPass);
    }

    /// <summary>The range of channel shifts (min of horizontal and vertical) that Modular pass <paramref name="pass"/> codes.</summary>
    public (int MinShift, int MaxShift) DownsamplingBracket(int pass)
    {
        int maxShift = 2;
        int minShift = 3;
        for (int i = 0; ; i++)
        {
            for (int j = 0; j < Downsample.Length; j++)
            {
                if (i == LastPass[j])
                {
                    minShift = Downsample[j] switch { 8 => 3, 4 => 2, 2 => 1, _ => 0 };
                }
            }

            if (i == Count - 1)
            {
                minShift = 0;
            }

            if (i == pass)
            {
                return (minShift, maxShift);
            }

            maxShift = minShift - 1;
        }
    }
}

/// <summary>The restoration filters: Gaborish smoothing and the edge-preserving filter.</summary>
internal sealed record JxlLoopFilter(bool Gaborish, uint EpfIterations)
{
    public static JxlLoopFilter Default { get; } = new(true, 2);

    public static JxlLoopFilter Read(ref JxlBitReader reader, bool modular)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        bool gaborish = reader.ReadBool();
        if (gaborish && reader.ReadBool())
        {
            for (int channel = 0; channel < 3; channel++)
            {
                float weight1 = JxlFields.F16(ref reader);
                float weight2 = JxlFields.F16(ref reader);
                if (Math.Abs(1.0f + (weight1 + weight2) * 4) < 1e-8)
                {
                    throw JxlDescriptor.Corrupt("Gaborish weights lead to a zero kernel.");
                }
            }
        }

        uint iterations = reader.ReadBits(2);
        if (iterations > 0)
        {
            if (!modular && reader.ReadBool())
            {
                SkipF16(ref reader, 8);               // sharpness LUT
            }

            if (reader.ReadBool())
            {
                SkipF16(ref reader, 5);               // channel scales and zero-flush thresholds
            }

            if (reader.ReadBool())
            {
                SkipF16(ref reader, modular ? 3 : 4); // sigma parameters
            }

            if (modular && JxlFields.F16(ref reader) < 1e-8)
            {
                throw JxlDescriptor.Corrupt("EPF sigma for Modular is too small.");
            }
        }

        JxlFields.SkipExtensions(ref reader);
        return new JxlLoopFilter(gaborish, iterations);
    }

    private static void SkipF16(ref JxlBitReader reader, int count)
    {
        for (int i = 0; i < count; i++)
        {
            JxlFields.F16(ref reader);
        }
    }
}

/// <summary>A frame header with the dimensions derived from it (FrameHeader and FrameDimensions in libjxl).</summary>
internal sealed record JxlFrameHeader
{
    public const int BlockDim = 8;
    private const int GroupDim = 256;
    private static ReadOnlySpan<byte> HShifts => [0, 1, 1, 0];
    private static ReadOnlySpan<byte> VShifts => [0, 1, 0, 1];

    public JxlFrameType Type { get; init; }

    public JxlFrameEncoding Encoding { get; init; }

    public JxlFrameFlags Flags { get; init; }

    public JxlColorTransform ColorTransform { get; init; }

    /// <summary>Chroma subsampling mode (0–3) of the Y, Cb, Cr channels, used with YCbCr only.</summary>
    public uint[] ChromaModes { get; init; } = [0, 0, 0];

    public uint Upsampling { get; init; } = 1;

    /// <summary>Per extra channel upsampling, including its dim_shift.</summary>
    public uint[] ExtraChannelUpsampling { get; init; } = [];

    public int GroupSizeShift { get; init; } = 1;

    public JxlPasses Passes { get; init; } = JxlPasses.Single;

    public uint DcLevel { get; init; }

    public bool CustomSizeOrOrigin { get; init; }

    public int X0 { get; init; }

    public int Y0 { get; init; }

    /// <summary>The frame size in pixels before upsampling (the image or preview size unless customised).</summary>
    public uint Width { get; init; }

    public uint Height { get; init; }

    public bool IsPartial { get; init; }

    public JxlBlendingInfo Blending { get; init; } = new(JxlBlendMode.Replace, 0, false, 0);

    public JxlBlendingInfo[] ExtraChannelBlending { get; init; } = [];

    public uint Duration { get; init; }

    public bool IsLast { get; init; } = true;

    public uint SaveAsReference { get; init; }

    public bool SaveBeforeColorTransform { get; init; }

    public string Name { get; init; } = "";

    public JxlLoopFilter LoopFilter { get; init; } = JxlLoopFilter.Default;

    // FrameDimensions.
    public int MaxHShift => ChromaModes.Max(mode => HShifts[(int)mode]);

    public int MaxVShift => ChromaModes.Max(mode => VShifts[(int)mode]);

    /// <summary>Group size: 128, 256, 512 or 1024; LF groups cover 8 times as much.</summary>
    public int GroupDimension => (GroupDim >> 1) << GroupSizeShift;

    public long XSize => DivCeil(Width, Upsampling);

    public long YSize => DivCeil(Height, Upsampling);

    public long XSizeBlocks => DivCeil(XSize, BlockDim << MaxHShift) << MaxHShift;

    public long YSizeBlocks => DivCeil(YSize, BlockDim << MaxVShift) << MaxVShift;

    public long XGroups => DivCeil(XSize, GroupDimension);

    public long YGroups => DivCeil(YSize, GroupDimension);

    public long XDcGroups => DivCeil(XSizeBlocks, GroupDimension);

    public long YDcGroups => DivCeil(YSizeBlocks, GroupDimension);

    public long Groups => XGroups * YGroups;

    public long DcGroups => XDcGroups * YDcGroups;

    /// <summary>LF global, LF groups, HF global, then the pass groups; a single entry for one group and pass.</summary>
    public long TocEntries => Groups == 1 && Passes.Count == 1 ? 1 : 2 + DcGroups + Groups * Passes.Count;

    /// <summary>Reads a frame header; <paramref name="preview"/> selects the preview size as the default size.</summary>
    public static JxlFrameHeader Read(ref JxlBitReader reader, JxlHeaders headers, bool preview)
    {
        JxlImageMetadata metadata = headers.Metadata;
        (uint defaultWidth, uint defaultHeight) = preview && metadata.Preview is { } p
            ? (p.Width, p.Height)
            : (headers.Size.Width, headers.Size.Height);
        int extraChannels = metadata.ExtraChannels.Length;
        if (reader.ReadBool())
        {
            return new JxlFrameHeader
            {
                Encoding = JxlFrameEncoding.VarDct,
                ColorTransform = metadata.XybEncoded ? JxlColorTransform.Xyb : JxlColorTransform.None,
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
        if (encoding == JxlFrameEncoding.VarDct && transform == JxlColorTransform.Xyb)
        {
            reader.ReadBits(6);   // x_qm_scale and b_qm_scale
        }

        JxlPasses passes = type != JxlFrameType.ReferenceOnly ? JxlPasses.Read(ref reader) : JxlPasses.Single;
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
            blending = JxlBlendingInfo.Read(ref reader, extraChannels, partial);
            ecBlending = new JxlBlendingInfo[extraChannels];
            for (int i = 0; i < ecBlending.Length; i++)
            {
                ecBlending[i] = JxlBlendingInfo.Read(ref reader, extraChannels, partial);
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
        JxlLoopFilter loopFilter = JxlLoopFilter.Read(ref reader, encoding == JxlFrameEncoding.Modular);
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

/// <summary>
/// The table of contents: the byte range of every section in logical order, relative to the end of the TOC. Ported
/// from libjxl v0.12.0 lib/jxl/toc.cc and the permutation decoding of lib/jxl/coeff_order.cc (BSD-3-Clause).
/// </summary>
internal sealed record JxlToc(long[] Offsets, uint[] Sizes, long TotalSize)
{
    private const int MaxEntries = 65536;
    private const int PermutationContexts = 8;

    public static JxlToc Read(ref JxlBitReader reader, long entries)
    {
        if (entries > MaxEntries)
        {
            throw JxlDescriptor.Corrupt("Too many TOC entries.");
        }

        int count = (int)entries;
        // Every entry takes at least 12 bits, so a TOC larger than the input is truncated, not allocated.
        if (count * 12L > reader.RemainingBits)
        {
            throw JxlDescriptor.Corrupt("Truncated codestream.");
        }

        int[]? permutation = reader.ReadBool() ? ReadPermutation(ref reader, count) : null;
        reader.JumpToByteBoundary();
        uint[] sizes = new uint[count];
        for (int i = 0; i < count; i++)
        {
            sizes[i] = JxlFields.U32(ref reader, Bits32(10), BitsOffset(14, 1024), BitsOffset(22, 17408), BitsOffset(30, 4211712));
        }

        reader.JumpToByteBoundary();
        long[] offsets = new long[count];
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            offsets[i] = total;
            total += sizes[i];
        }

        if (permutation is null)
        {
            return new JxlToc(offsets, sizes, total);
        }

        return new JxlToc([.. permutation.Select(index => offsets[index])], [.. permutation.Select(index => sizes[index])], total);
    }

    private static int[] ReadPermutation(ref JxlBitReader reader, int size)
    {
        JxlEntropyCode code = JxlEntropyCode.Read(ref reader, PermutationContexts);
        uint[] lehmer = new uint[size];
        using (var symbols = new JxlSymbolReader(code, ref reader))
        {
            uint end = symbols.Read(Context((uint)size), ref reader);
            if (end > size)
            {
                throw JxlDescriptor.Corrupt("Invalid permutation size.");
            }

            uint last = 0;
            for (int i = 0; i < end; i++)
            {
                lehmer[i] = symbols.Read(Context(last), ref reader);
                last = lehmer[i];
                if (lehmer[i] >= size - i)
                {
                    throw JxlDescriptor.Corrupt("Invalid Lehmer code.");
                }
            }

            if (!symbols.IsFinalStateValid)
            {
                throw JxlDescriptor.Corrupt("Invalid ANS stream.");
            }
        }

        return DecodeLehmerCode(lehmer);
    }

    // The token of HybridUintConfig(0, 0, 0), capped: 0 for 0, else 1 + floor(log2(value)).
    private static int Context(uint value) => Math.Min(value == 0 ? 0 : JxlPrefixCode.Log2Floor(value) + 1, PermutationContexts - 1);

    /// <summary>Lehmer code to permutation with an implicit order-statistics tree (lib/jxl/lehmer_code.h).</summary>
    private static int[] DecodeLehmerCode(uint[] code)
    {
        int n = code.Length;
        int log2 = n <= 1 ? 0 : JxlPrefixCode.Log2Floor((uint)n - 1) + 1;
        int padded = 1 << log2;
        uint[] tree = new uint[padded];
        for (int i = 0; i < padded; i++)
        {
            tree[i] = (uint)((i + 1) & -(i + 1));
        }

        int[] permutation = new int[n];
        for (int i = 0; i < n; i++)
        {
            uint rank = code[i] + 1;
            int bit = padded;
            int next = 0;
            for (int b = 0; b <= log2; b++)
            {
                int candidate = next + bit;
                bit >>= 1;
                if (candidate <= padded && tree[candidate - 1] < rank)
                {
                    next = candidate;
                    rank -= tree[candidate - 1];
                }
            }

            permutation[i] = next;
            for (next++; next <= padded; next += next & -next)
            {
                tree[next - 1]--;
            }
        }

        return permutation;
    }
}
