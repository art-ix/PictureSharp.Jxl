namespace PictureSharp.Formats.Jxl.Codestream.Model;

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
internal sealed record JxlBlendingInfo(JxlBlendMode Mode, uint AlphaChannel, bool Clamp, uint Source);

/// <summary>Progressive passes: shifts of the VarDCT coefficients and the downsampling reached after some passes.</summary>
internal sealed class JxlPasses(uint count, uint[] shift, uint[] downsample, uint[] lastPass)
{
    public uint Count { get; } = count;
    public IReadOnlyList<uint> Shift { get; } = Array.AsReadOnly((uint[])shift.Clone());
    public IReadOnlyList<uint> Downsample { get; } = Array.AsReadOnly((uint[])downsample.Clone());
    public IReadOnlyList<uint> LastPass { get; } = Array.AsReadOnly((uint[])lastPass.Clone());

    public const int MaxPasses = 11;

    public static JxlPasses Single { get; } = new(1, [0], [], []);

    /// <summary>The range of channel shifts (min of horizontal and vertical) that Modular pass <paramref name="pass"/> codes.</summary>
    public (int MinShift, int MaxShift) DownsamplingBracket(int pass)
    {
        int maxShift = 2;
        int minShift = 3;
        for (int i = 0; ; i++)
        {
            for (int j = 0; j < Downsample.Count; j++)
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

/// <summary>The restoration filters: Gaborish smoothing and the edge-preserving filter (EPF) with its parameters.</summary>
internal sealed record JxlLoopFilter
{
    public const int SharpnessEntries = 8;

    public static JxlLoopFilter Default { get; } = new();

    public bool Gaborish { get; init; } = true;

    /// <summary>The side and corner weights of the X, Y and B Gaborish kernels (before normalisation).</summary>
    public IReadOnlyList<float> GaborishWeights { get; init => field = Array.AsReadOnly(value.ToArray()); } = Array.AsReadOnly<float>([.. Enumerable.Repeat(new[] { GaborishSide, GaborishCorner }, 3).SelectMany(w => w)]);

    /// <summary>0 disables EPF; 1 runs stage one, 2 stages one and two, 3 stage zero first.</summary>
    public uint EpfIterations { get; init; } = 2;

    public IReadOnlyList<float> SharpnessLut { get; init => field = Array.AsReadOnly(value.ToArray()); } = Array.AsReadOnly<float>([.. Enumerable.Range(0, SharpnessEntries).Select(i => (float)i / (SharpnessEntries - 1))]);

    public IReadOnlyList<float> ChannelScale { get; init => field = Array.AsReadOnly(value.ToArray()); } = Array.AsReadOnly<float>([40.0f, 5.0f, 3.5f]);

    public float Pass1ZeroFlush { get; init; } = 0.45f;

    public float Pass2ZeroFlush { get; init; } = 0.6f;

    public float QuantMul { get; init; } = 0.46f;

    public float Pass0SigmaScale { get; init; } = 0.9f;

    public float Pass2SigmaScale { get; init; } = 6.5f;

    public float BorderSadMul { get; init; } = 0.6666666666666666f;

    public float SigmaForModular { get; init; } = 1.0f;

    private static float GaborishSide => (float)(1.1 * 0.104699568f);

    private static float GaborishCorner => (float)(1.1 * 0.055680538f);
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

    /// <summary>Chroma subsampling mode (0–3) of channels 0–2 (Cb, Y, Cr), used with YCbCr only.</summary>
    public IReadOnlyList<uint> ChromaModes { get; init => field = Array.AsReadOnly(value.ToArray()); } = Array.AsReadOnly<uint>([0, 0, 0]);

    public uint Upsampling { get; init; } = 1;

    /// <summary>Per extra channel upsampling, including its dim_shift.</summary>
    public IReadOnlyList<uint> ExtraChannelUpsampling { get; init => field = Array.AsReadOnly(value.ToArray()); } = [];

    public int GroupSizeShift { get; init; } = 1;

    /// <summary>Scales of the X and B quantisation matrices (VarDCT XYB frames; 2 means no change).</summary>
    public uint XQmScale { get; init; } = 3;

    public uint BQmScale { get; init; } = 2;

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

    public IReadOnlyList<JxlBlendingInfo> ExtraChannelBlending { get; init => field = Array.AsReadOnly(value.ToArray()); } = [];

    public uint Duration { get; init; }

    public bool IsLast { get; init; } = true;

    public uint SaveAsReference { get; init; }

    public bool SaveBeforeColorTransform { get; init; }

    public string Name { get; init; } = "";

    public JxlLoopFilter LoopFilter { get; init; } = JxlLoopFilter.Default;

    // FrameDimensions.
    public int MaxHShift => ChromaModes.Max(mode => HShifts[(int)mode]);

    public int MaxVShift => ChromaModes.Max(mode => VShifts[(int)mode]);

    /// <summary>The horizontal subsampling shift of colour channel <paramref name="c"/> (0 = Cb or X, 1 = Y, 2 = Cr or B).</summary>
    public int HShift(int c) => MaxHShift - HShifts[(int)ChromaModes[c]];

    public int VShift(int c) => MaxVShift - VShifts[(int)ChromaModes[c]];

    public bool Is444 => Enumerable.Range(0, 3).All(c => HShift(c) == 0 && VShift(c) == 0);

    /// <summary>Group size: 128, 256, 512 or 1024; LF groups cover 8 times as much.</summary>
    public int GroupDimension => (GroupDim >> 1) << GroupSizeShift;

    public long XSize => DivCeil(Width, Upsampling);

    public long YSize => DivCeil(Height, Upsampling);

    public long XSizeBlocks => DivCeil(XSize, BlockDim << MaxHShift) << MaxHShift;

    public long YSizeBlocks => DivCeil(YSize, BlockDim << MaxVShift) << MaxVShift;

    /// <summary>The size VarDCT blocks cover; Modular frames have no padding.</summary>
    public long XSizePadded => Encoding == JxlFrameEncoding.VarDct ? XSizeBlocks * BlockDim : XSize;

    public long YSizePadded => Encoding == JxlFrameEncoding.VarDct ? YSizeBlocks * BlockDim : YSize;

    public long XGroups => DivCeil(XSize, GroupDimension);

    public long YGroups => DivCeil(YSize, GroupDimension);

    public long XDcGroups => DivCeil(XSizeBlocks, GroupDimension);

    public long YDcGroups => DivCeil(YSizeBlocks, GroupDimension);

    public long Groups => XGroups * YGroups;

    public long DcGroups => XDcGroups * YDcGroups;

    /// <summary>LF global, LF groups, HF global, then the pass groups; a single entry for one group and pass.</summary>
    public long TocEntries => Groups == 1 && Passes.Count == 1 ? 1 : 2 + DcGroups + Groups * Passes.Count;

    private static long DivCeil(long value, long divisor) => (value + divisor - 1) / divisor;
}

/// <summary>
/// The table of contents: the byte range of every section in logical order, relative to the end of the TOC. Ported
/// from libjxl v0.12.0 lib/jxl/toc.cc and the permutation decoding of lib/jxl/coeff_order.cc (BSD-3-Clause).
/// </summary>
internal sealed class JxlToc(long[] offsets, uint[] sizes, long totalSize)
{
    public long TotalSize { get; } = totalSize;
    public IReadOnlyList<long> Offsets { get; } = Array.AsReadOnly((long[])offsets.Clone());
    public IReadOnlyList<uint> Sizes { get; } = Array.AsReadOnly((uint[])sizes.Clone());

    public const int MaxEntries = 65536;
}
