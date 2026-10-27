using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Model;

// Frame headers. Ported from libjxl v0.12.0 lib/jxl/frame_header.cc, frame_header.h, frame_dimensions.h, dec_modular.h and
// toc.cc (BSD-3-Clause).

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
    /// <summary>The default: the frame replaces what is below it.</summary>
    public static JxlBlendingInfo Replace { get; } = new(JxlBlendMode.Replace, 0, false, 0);
}

/// <summary>A frame header with the dimensions derived from it (FrameHeader and FrameDimensions in libjxl).</summary>
internal sealed record JxlFrameHeader
{
    public const int BlockDim = 8;

    /// <summary>The number of raw quantisation tables, each with its own Modular stream.</summary>
    public const int QuantTables = 17;

    private const int GroupDim = 256;
    private static ReadOnlySpan<byte> HShifts => [0, 1, 1, 0];
    private static ReadOnlySpan<byte> VShifts => [0, 1, 0, 1];

    public JxlFrameType Type { get; init; }

    public JxlFrameEncoding Encoding { get; init; }

    public JxlFrameFlags Flags { get; init; }

    public JxlColorTransform ColorTransform { get; init; }

    /// <summary>Chroma subsampling mode (0–3) of channels 0–2 (Cb, Y, Cr), used with YCbCr only.</summary>
    public IReadOnlyList<uint> ChromaModes { get; init => field = value.Snapshot(); } = Array.AsReadOnly<uint>([0, 0, 0]);

    public uint Upsampling { get; init; } = 1;

    /// <summary>Per extra channel upsampling, including its dim_shift.</summary>
    public IReadOnlyList<uint> ExtraChannelUpsampling { get; init => field = value.Snapshot(); } = [];

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

    public JxlBlendingInfo Blending { get; init; } = JxlBlendingInfo.Replace;

    public IReadOnlyList<JxlBlendingInfo> ExtraChannelBlending { get; init => field = value.Snapshot(); } = [];

    public uint Duration { get; init; }

    public bool IsLast { get; init; } = true;

    public uint SaveAsReference { get; init; }

    public bool SaveBeforeColorTransform { get; init; }

    public string Name { get; init; } = "";

    public JxlLoopFilter LoopFilter { get; init; } = JxlLoopFilter.Default;

    public bool HasNoise => Flags.HasFlag(JxlFrameFlags.Noise);

    public bool HasPatches => Flags.HasFlag(JxlFrameFlags.Patches);

    public bool HasSplines => Flags.HasFlag(JxlFrameFlags.Splines);

    /// <summary>Whether the DC comes from a decoded DC frame instead of the LF groups.</summary>
    public bool UsesDcFrame => Flags.HasFlag(JxlFrameFlags.UseDcFrame);

    public bool SkipsAdaptiveDcSmoothing => Flags.HasFlag(JxlFrameFlags.SkipAdaptiveDcSmoothing);

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

    /// <summary>The top-left corner of group <paramref name="group"/> in cells of <paramref name="size"/> (row-major).</summary>
    public (int X, int Y) GroupOrigin(long group, int size) => ((int)(group % XGroups) * size, (int)(group / XGroups) * size);

    /// <summary>The top-left corner of LF group <paramref name="group"/> in cells of <paramref name="size"/> (row-major).</summary>
    public (int X, int Y) DcGroupOrigin(long group, int size) => ((int)(group % XDcGroups) * size, (int)(group / XDcGroups) * size);

    // TOC sections in logical order: LF global, LF groups, HF global, then the groups of each pass.
    public static long LfGlobalSection => 0;

    public static long LfGroupSection(long group) => 1 + group;

    public long HfGlobalSection => 1 + DcGroups;

    public long PassGroupSection(int pass, long group) => 2 + DcGroups + pass * Groups + group;

    // Modular stream ids, which seed the stream property of MA trees (libjxl ModularStreamId::ID).
    public static int GlobalStream => 0;

    public static int VarDctDcStream(long group) => (int)(1 + group);

    public int ModularDcStream(long group) => (int)(1 + DcGroups + group);

    public int AcMetadataStream(long group) => (int)(1 + 2 * DcGroups + group);

    public int QuantTableStream(int index) => (int)(1 + 3 * DcGroups + index);

    public int ModularAcStream(int pass, long group) => (int)(1 + 3 * DcGroups + QuantTables + Groups * pass + group);
}

