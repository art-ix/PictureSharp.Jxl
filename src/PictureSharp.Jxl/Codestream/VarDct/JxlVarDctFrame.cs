using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Jpeg;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.VarDct;

/// <summary>
/// The VarDCT part of a frame: quantiser, chroma-from-luma and block context maps (LF global), quantised DC and AC
/// metadata (LF groups), adaptive DC smoothing, coefficient orders and histograms (HF global), then the AC
/// coefficients of every group, dequantised and transformed into three planes. Ported from libjxl v0.12.0
/// lib/jxl/dec_frame.cc, dec_group.cc, dec_modular.cc (DecodeVarDCTDC, DecodeAcMetadata), compressed_dc.cc,
/// quantizer.cc, quantizer-inl.h, chroma_from_luma.cc and passes_state.cc (BSD-3-Clause).
/// </summary>
internal sealed class JxlVarDctFrame
{
    private const int GroupBlocks = 32;
    private const int ColorTileBlocks = 8;
    private const int QuantMax = 256;
    private const int BlockSize = JxlAcStrategy.BlockSize;
    private const int DefaultColorFactor = 84;
    // Chroma-from-luma DC factors are stored as bytes around this value.
    private const int ColorFactorDcOffset = 128;

    // Channels are decoded Y first, then X and B; Modular images store them as Y, X, B.
    private static ReadOnlySpan<int> YFirstOrder => [1, 0, 2];

    private readonly JxlFrameHeader frame;
    private readonly CancellationToken cancellationToken;
    private readonly JxlQuantWeights matrices;
    private readonly float[] quantBiases;
    private readonly int bitDepth;
    private readonly bool recoverJpeg;
    private readonly bool is444;
    private readonly int[] hShift = new int[3];
    private readonly int[] vShift = new int[3];
    private readonly int xTiles;
    private readonly float xDmMultiplier;
    private readonly float bDmMultiplier;
    private readonly float[] mulDc = new float[3];
    private readonly float[] dcFactors = new float[3];
    private readonly float[][] dc;
    private int[][] orders = [];
    private JxlEntropyCode[] codes = [];
    private int histograms;
    private float invGlobalScale;
    private float colorScale = 1.0f / DefaultColorFactor;
    private float baseCorrelationX;
    private float baseCorrelationB = 1.0f;

    public JxlVarDctFrame(JxlFrameHeader frame, JxlImageMetadata metadata, JxlQuantWeights matrices, IReadOnlyList<float> quantBiases, CancellationToken cancellationToken = default, bool recoverJpeg = false)
    {
        this.frame = frame;
        this.recoverJpeg = recoverJpeg;
        this.cancellationToken = cancellationToken;
        this.matrices = matrices;
        this.quantBiases = [.. quantBiases];
        bitDepth = (int)metadata.BitDepth.BitsPerSample;
        XBlocks = (int)frame.XSizeBlocks;
        YBlocks = (int)frame.YSizeBlocks;
        for (int c = 0; c < 3; c++)
        {
            hShift[c] = frame.HShift(c);
            vShift[c] = frame.VShift(c);
        }

        is444 = frame.Is444;
        xTiles = DivCeil(XBlocks, ColorTileBlocks);
        int blocks = XBlocks * YBlocks;
        Varblocks = new JxlVarblock[blocks];
        Array.Fill(Varblocks, JxlVarblock.Unassigned);
        QuantField = new int[blocks];
        Sharpness = new byte[blocks];
        DcBuckets = new byte[blocks];
        YToX = new sbyte[xTiles * DivCeil(YBlocks, ColorTileBlocks)];
        YToB = new sbyte[YToX.Length];
        dc = [new float[blocks], new float[blocks], new float[blocks]];
        xDmMultiplier = MathF.Pow(1 / 1.25f, frame.XQmScale - 2.0f);
        bDmMultiplier = MathF.Pow(1 / 1.25f, frame.BQmScale - 2.0f);
        Planes = new JxlPlane[3];
        for (int c = 0; c < 3 && !recoverJpeg; c++)
        {
            Planes[c] = new JxlPlane(DivCeil((int)frame.XSize, 1 << hShift[c]), DivCeil((int)frame.YSize, 1 << vShift[c]),
                (XBlocks >> hShift[c]) * JxlFrameHeader.BlockDim, (YBlocks >> vShift[c]) * JxlFrameHeader.BlockDim);
        }
    }

    public int XBlocks { get; }

    public int YBlocks { get; }

    /// <summary>Per block: its varblock, or unassigned before the AC metadata.</summary>
    public JxlVarblock[] Varblocks { get; }

    /// <summary>The AC quantisation of the first block of every varblock, 1 to 256.</summary>
    public int[] QuantField { get; }

    public byte[] Sharpness { get; }

    public byte[] DcBuckets { get; }

    /// <summary>Chroma-from-luma factors per 64x64 tile.</summary>
    public sbyte[] YToX { get; }

    public sbyte[] YToB { get; }

    /// <summary>The rendered colour planes (X, Y, B or Cb, Y, Cr), each at its own subsampling.</summary>
    public JxlPlane[] Planes { get; }

    /// <summary>The quantiser scale (global scale / 65536) that EPF needs.</summary>
    public float QuantScale { get; private set; }

    public JxlAcStrategySet UsedStrategies { get; private set; }

    /// <summary>Reads the quantiser, the block context map and the DC chroma-from-luma factors (DecodeGlobalDCInfo).</summary>
    public void ReadGlobal(ref JxlBitReader reader)
    {
        uint globalScale = JxlFields.U32(ref reader, JxlU32.BitsOffset(11, 1), JxlU32.BitsOffset(11, 2049), JxlU32.BitsOffset(12, 4097),
            JxlU32.BitsOffset(16, 8193));
        uint quantDc = JxlFields.U32(ref reader, JxlU32.Val(16), JxlU32.BitsOffset(5, 1), JxlU32.BitsOffset(8, 1), JxlU32.BitsOffset(16, 1));
        SetQuantizer(globalScale, quantDc);
        BlockContexts = JxlBlockContextMap.Read(ref reader);
        int yToXDc = 0;
        int yToBDc = 0;
        if (!reader.ReadBool())
        {
            uint colorFactor = JxlFields.U32(ref reader, JxlU32.Val(DefaultColorFactor), JxlU32.Val(256), JxlU32.BitsOffset(8, 2),
                JxlU32.BitsOffset(16, 258));
            colorScale = 1.0f / colorFactor;
            baseCorrelationX = JxlFields.F16(ref reader);
            baseCorrelationB = JxlFields.F16(ref reader);
            if (Math.Abs(baseCorrelationX) > 4.0f || Math.Abs(baseCorrelationB) > 4.0f)
            {
                throw JxlDescriptor.Corrupt("Chroma-from-luma base correlation is out of range.");
            }

            yToXDc = (int)reader.ReadBits(8) - ColorFactorDcOffset;
            yToBDc = (int)reader.ReadBits(8) - ColorFactorDcOffset;
        }

        dcFactors[0] = YToXRatio(yToXDc);
        dcFactors[2] = YToBRatio(yToBDc);
        if (recoverJpeg)
        {
            if (baseCorrelationX != 0 || baseCorrelationB != 0 || yToXDc != 0 || yToBDc != 0 || colorScale != 1.0f / DefaultColorFactor)
                throw JxlDescriptor.Corrupt("The chroma correlation is not JPEG reconstruction compatible.");
            Array.Fill(mulDc, 1.0f);
        }
    }

    public JxlBlockContextMap BlockContexts { get; private set; } = JxlBlockContextMap.Default;

    /// <summary>The quantiser of the frame (QuantizerParams), set by <see cref="ReadGlobal"/> or by an encoder.</summary>
    public void SetQuantizer(uint globalScale, uint quantDc)
    {
        QuantScale = (float)(globalScale * (1.0 / 65536));
        invGlobalScale = (float)(1.0 * 65536 / globalScale);
        float invQuantDc = invGlobalScale / quantDc;
        for (int c = 0; c < 3; c++)
        {
            mulDc[c] = invQuantDc * matrices.DcQuant[c];
        }
    }

    /// <summary>
    /// Sets the state an encoder chose, so <see cref="RenderGroup"/> reconstructs its frame: the dequantised DC of
    /// every block (X, Y, B) and the strategies in use; the varblocks, quantisation and chroma-from-luma maps are
    /// written into their arrays by the caller.
    /// </summary>
    public void SetEncoded(float[][] dequantizedDc, JxlAcStrategySet usedStrategies)
    {
        for (int c = 0; c < 3; c++)
        {
            dequantizedDc[c].CopyTo(dc[c], 0);
        }

        UsedStrategies = usedStrategies;
    }

    /// <summary>The dequantisation step of quantised DC values of channel <paramref name="c"/> (X, Y, B).</summary>
    public float DcStep(int c) => mulDc[c];

    private float YToXRatio(int factor) => baseCorrelationX + factor * colorScale;

    private float YToBRatio(int factor) => baseCorrelationB + factor * colorScale;

    // The index of colour channel c (X, Y, B) in a Modular image, which stores Y first.
    private static int CodedIndex(int c) => c < 2 ? c ^ 1 : c;

    // The block of channel c at (bx, by) of a group in its subsampled grid; false where the channel has no block.
    private bool TryChannelBlock(int c, int bx, int by, out int sbx, out int sby)
    {
        sbx = bx >> hShift[c];
        sby = by >> vShift[c];
        return sbx << hShift[c] == bx && sby << vShift[c] == by;
    }

    private (int X0, int Y0, int Width, int Height) DcGroupRect(int group)
    {
        int dimension = frame.GroupDimension;
        (int x0, int y0) = frame.DcGroupOrigin(group, dimension);
        return (x0, y0, Math.Min(dimension, XBlocks - x0), Math.Min(dimension, YBlocks - y0));
    }

    /// <summary>Takes the DC of the frame from a decoded DC frame (kUseDcFrame) instead of LF groups.</summary>
    public void UseDcFrame(IReadOnlyList<JxlPlane> planes)
    {
        for (int c = 0; c < 3; c++)
        {
            int width = XBlocks >> hShift[c];
            int height = YBlocks >> vShift[c];
            if (planes[c].Width < width || planes[c].Height < height)
            {
                throw JxlDescriptor.Corrupt("The DC frame is smaller than the frame.");
            }

            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                planes[c].Row(y)[..width].CopyTo(dc[c].AsSpan(y * XBlocks, width));
            }
        }
    }

    /// <summary>Decodes and dequantises the DC of an LF group (DecodeVarDCTDC, DequantDC).</summary>
    public void DecodeDc(ref JxlBitReader reader, int group, JxlModularTree? tree)
    {
        (int x0, int y0, int width, int height) = DcGroupRect(group);
        float mul = 1.0f / (1 << (int)reader.ReadBits(2));
        var channels = new JxlChannel[3];
        for (int c = 0; c < 3; c++)
        {
            channels[CodedIndex(c)] = new JxlChannel(width >> hShift[c], height >> vShift[c]);
        }

        var image = new JxlModularImage(bitDepth, [.. channels]);
        JxlModularStream.Decode(ref reader, image, JxlFrameHeader.VarDctDcStream(group), tree, undoTransforms: true, cancellationToken: cancellationToken);
        if (is444)
        {
            float facX = mulDc[0] * mul;
            float facY = mulDc[1] * mul;
            float facB = mulDc[2] * mul;
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<int> qx = channels[1].Row(y);
                Span<int> qy = channels[0].Row(y);
                Span<int> qb = channels[2].Row(y);
                int row = (y0 + y) * XBlocks + x0;
                for (int x = 0; x < width; x++)
                {
                    float inX = qx[x] * facX;
                    float inY = qy[x] * facY;
                    float inB = qb[x] * facB;
                    dc[1][row + x] = inY;
                    dc[0][row + x] = JxlFastMath.Fma(inY, dcFactors[0], inX);
                    dc[2][row + x] = JxlFastMath.Fma(inY, dcFactors[2], inB);
                }
            }
        }
        else
        {
            foreach (int c in YFirstOrder)
            {
                float factor = mulDc[c] * mul;
                JxlChannel channel = channels[CodedIndex(c)];
                for (int y = 0; y < channel.Height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Span<int> q = channel.Row(y);
                    int row = ((y0 >> vShift[c]) + y) * XBlocks + (x0 >> hShift[c]);
                    for (int x = 0; x < channel.Width; x++)
                    {
                        dc[c][row + x] = q[x] * factor;
                    }
                }
            }
        }

        // With a single DC context every bucket stays zero.
        if (BlockContexts.DcContexts <= 1)
        {
            return;
        }

        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<int> qx = channels[1].Row(y >> vShift[0]);
            Span<int> qy = channels[0].Row(y >> vShift[1]);
            Span<int> qb = channels[2].Row(y >> vShift[2]);
            int row = (y0 + y) * XBlocks + x0;
            for (int x = 0; x < width; x++)
            {
                DcBuckets[row + x] = (byte)BlockContexts.DcBucket(qx[x >> hShift[0]], qy[x >> hShift[1]], qb[x >> hShift[2]]);
            }
        }
    }

    /// <summary>Decodes the chroma-from-luma map, AC strategies, quantisation field and EPF sharpness of an LF group.</summary>
    public void DecodeAcMetadata(ref JxlBitReader reader, int group, JxlModularTree? tree)
    {
        (int x0, int y0, int width, int height) = DcGroupRect(group);
        int count = (int)reader.ReadBits(CeilLog2(width * height)) + 1;
        int tileX0 = x0 / ColorTileBlocks;
        int tileY0 = y0 / ColorTileBlocks;
        int tileWidth = DivCeil(width, ColorTileBlocks);
        int tileHeight = DivCeil(height, ColorTileBlocks);
        var image = new JxlModularImage(bitDepth,
        [
            new JxlChannel(tileWidth, tileHeight, 3, 3), new JxlChannel(tileWidth, tileHeight, 3, 3), new JxlChannel(count, 2),
            new JxlChannel(width, height),
        ]);
        JxlModularStream.Decode(ref reader, image, frame.AcMetadataStream(group), tree, undoTransforms: true, cancellationToken: cancellationToken);
        for (int y = 0; y < tileHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<int> x = image.Channels[0].Row(y);
            Span<int> b = image.Channels[1].Row(y);
            for (int i = 0; i < tileWidth; i++)
            {
                YToX[(tileY0 + y) * xTiles + tileX0 + i] = (sbyte)Math.Clamp(x[i], sbyte.MinValue, sbyte.MaxValue);
                YToB[(tileY0 + y) * xTiles + tileX0 + i] = (sbyte)Math.Clamp(b[i], sbyte.MinValue, sbyte.MaxValue);
            }
        }

        Span<int> types = image.Channels[2].Row(0);
        Span<int> quants = image.Channels[2].Row(1);
        int xLimit = Math.Min(XBlocks, x0 + width);
        int yLimit = Math.Min(YBlocks, y0 + height);
        int num = 0;
        JxlAcStrategySet used = default;
        for (int iy = 0; iy < height; iy++)
        {
            int y = y0 + iy;
            Span<int> sharpness = image.Channels[3].Row(iy);
            for (int ix = 0; ix < width; ix++)
            {
                int x = x0 + ix;
                int index = y * XBlocks + x;
                if (sharpness[ix] is < 0 or >= JxlLoopFilter.SharpnessEntries)
                {
                    throw JxlDescriptor.Corrupt("Corrupted EPF sharpness field.");
                }

                Sharpness[index] = (byte)sharpness[ix];
                if (Varblocks[index].IsAssigned)
                {
                    continue;
                }

                if (num >= count || types[num] is < 0 or >= JxlAcStrategy.Count)
                {
                    throw JxlDescriptor.Corrupt("Invalid AC strategy.");
                }

                var type = (JxlAcStrategyType)types[num];
                used = used.With(type);
                PlaceVarblock(type, x, y, xLimit, yLimit);
                QuantField[index] = 1 + Math.Clamp(quants[num], 0, QuantMax - 1);
                num++;
            }
        }

        UsedStrategies = UsedStrategies.Union(used);
    }

    // Marks the blocks a varblock covers; it must stay inside its group and the LF group and not overlap another.
    private void PlaceVarblock(JxlAcStrategyType type, int x, int y, int xLimit, int yLimit)
    {
        int blocksX = type.BlocksX;
        int blocksY = type.BlocksY;
        if ((blocksX > 1 || blocksY > 1) && !is444)
        {
            throw JxlDescriptor.Corrupt("AC strategy not compatible with chroma subsampling.");
        }

        if (x + blocksX > (x / GroupBlocks + 1) * GroupBlocks || x + blocksX > xLimit
            || y + blocksY > (y / GroupBlocks + 1) * GroupBlocks || y + blocksY > yLimit)
        {
            throw JxlDescriptor.Corrupt("AC strategy overflows its group.");
        }

        int index = y * XBlocks + x;
        for (int by = 0; by < blocksY; by++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int bx = 0; bx < blocksX; bx++)
            {
                int position = index + by * XBlocks + bx;
                if (Varblocks[position].IsAssigned)
                {
                    throw JxlDescriptor.Corrupt("Invalid AC strategy: block overlap.");
                }

                Varblocks[position] = JxlVarblock.Create(type, by == 0 && bx == 0);
            }
        }
    }

    /// <summary>Recovers integer JPEG DCT coefficients without IDCT or pixel rendering (libjxl dec_group.cc).</summary>
    public void RecoverJpeg(int group, int[] coefficients, JxlJpegData jpeg)
    {
        int[] table = matrices.JpegTable;
        (int x0, int y0, int width, int height) = BlockGroupRect(group);
        int area = width * height * 64;
        int[] map = jpeg.Gray ? [0, 0, 0] : [1, 0, 2];
        for (int by = 0; by < height; by++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int bx = 0; bx < width; bx++)
            {
                int index = (y0 + by) * XBlocks + x0 + bx;
                if (Varblocks[index].Type != JxlAcStrategyType.Dct) throw JxlDescriptor.Corrupt("JPEG reconstruction requires DCT-8 blocks.");
                foreach (int c in YFirstOrder)
                {
                    if (jpeg.Gray && c != 1 || !TryChannelBlock(c, bx, by, out int sbx, out int sby)) continue;
                    var component = jpeg.Components[map[c]];
                    int destination = (((y0 + by) >> vShift[c]) * component.WidthBlocks + ((x0 + bx) >> hShift[c])) * 64;
                    int source = c * area + (by * width + bx) * 64;
                    int tile = (y0 + by) / ColorTileBlocks * xTiles + (x0 + bx) / ColorTileBlocks;
                    int correlation = c == 0 ? YToX[tile] : c == 2 ? YToB[tile] : 0;
                    int scale = correlation * 2048 / DefaultColorFactor;
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            int transposed = x * 8 + y;
                            long value = coefficients[source + transposed];
                            if (is444 && c != 1 && scale != 0)
                            {
                                int ratio = 2048 * table[64 + transposed] / table[c * 64 + transposed];
                                long factor = ((long)scale * ratio + 1024) >> 11;
                                int luma = coefficients[area + (by * width + bx) * 64 + transposed];
                                value += (luma * factor + 1024) >> 11;
                            }
                            if (value is < -4095 or > 4095) throw JxlDescriptor.Corrupt("Reconstructed JPEG AC coefficient out of range.");
                            component.Coefficients[destination + y * 8 + x] = (short)value;
                        }
                    int dcIndex = ((y0 >> vShift[c]) + sby) * XBlocks + (x0 >> hShift[c]) + sbx;
                    float dcValue = dc[c][dcIndex];
                    if (dcValue is < -2047 or > 2047 || dcValue != MathF.Truncate(dcValue)) throw JxlDescriptor.Corrupt("Reconstructed JPEG DC coefficient out of range.");
                    component.Coefficients[destination] = (short)dcValue;
                }
            }
        }
    }

    /// <summary>Adaptive DC smoothing, between the last LF group and the first pass group (AdaptiveDCSmoothing).</summary>
    public void SmoothDc()
    {
        if (frame.SkipsAdaptiveDcSmoothing || frame.UsesDcFrame || XBlocks <= 2 || YBlocks <= 2)
        {
            return;
        }

        const float w1 = 0.20345139757231578f;
        const float w2 = 0.0334829185968739f;
        const float w0 = 1.0f - 4.0f * (w1 + w2);
        float[][] smoothed = [.. dc.Select(plane => (float[])plane.Clone())];
        int stride = XBlocks;
        Span<float> mc = stackalloc float[3];
        Span<float> sm = stackalloc float[3];
        for (int y = 1; y < YBlocks - 1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 1; x < XBlocks - 1; x++)
            {
                int i = y * stride + x;
                float gap = 0.5f;
                for (int c = 0; c < 3; c++)
                {
                    float[] p = dc[c];
                    float corner = (p[i - stride - 1] + p[i - stride + 1]) + (p[i + stride - 1] + p[i + stride + 1]);
                    float side = (p[i - 1] + p[i + 1]) + (p[i - stride] + p[i + stride]);
                    mc[c] = p[i];
                    sm[c] = JxlFastMath.Fma(corner, w2, JxlFastMath.Fma(side, w1, mc[c] * w0));
                    gap = Math.Max(gap, Math.Abs((mc[c] - sm[c]) / mulDc[c]));
                }

                float factor = Math.Max(0, JxlFastMath.Fma(-4.0f, gap, 3.0f));
                for (int c = 0; c < 3; c++)
                {
                    smoothed[c][i] = JxlFastMath.Fma(sm[c] - mc[c], factor, mc[c]);
                }
            }
        }

        for (int c = 0; c < 3; c++)
        {
            smoothed[c].CopyTo(dc[c], 0);
        }
    }

    /// <summary>
    /// Reads the HF global section: AC quantisation tables (raw ones through <paramref name="readRaw"/>), the number
    /// of histogram sets, then per pass the coefficient orders and the AC histograms.
    /// </summary>
    public void ReadHfGlobal(ref JxlBitReader reader, JxlQuantWeights.RawTableReader readRaw)
    {
        matrices.Read(ref reader, readRaw);
        histograms = 1 + (int)reader.ReadBits(CeilLog2(frame.Groups));
        int passes = (int)frame.Passes.Count;
        orders = new int[passes][];
        codes = new JxlEntropyCode[passes];
        for (int pass = 0; pass < passes; pass++)
        {
            uint usedOrders = JxlFields.U32(ref reader, JxlU32.Val(0x5F), JxlU32.Val(0x13), JxlU32.Val(0), JxlU32.Bits32(JxlAcStrategy.Orders));
            orders[pass] = JxlAcStrategy.ReadOrders(ref reader, usedOrders, UsedStrategies);
            codes[pass] = JxlEntropyCode.Read(ref reader, histograms * BlockContexts.AcContexts);
        }
    }

    private (int X0, int Y0, int Width, int Height) BlockGroupRect(int group)
    {
        (int x0, int y0) = frame.GroupOrigin(group, GroupBlocks);
        return (x0, y0, Math.Min(GroupBlocks, XBlocks - x0), Math.Min(GroupBlocks, YBlocks - y0));
    }

    /// <summary>Quantised coefficients of a group, accumulated over its passes in varblock order.</summary>
    public int[] NewGroupCoefficients(int group)
    {
        (_, _, int width, int height) = BlockGroupRect(group);
        return new int[3 * width * height * BlockSize];
    }

    /// <summary>Adds the coefficients one pass of a group codes (DecodeACVarBlock for each varblock).</summary>
    public void DecodePass(ref JxlBitReader reader, int group, int pass, int[] coefficients)
    {
        (int x0, int y0, int width, int height) = BlockGroupRect(group);
        int area = width * height * BlockSize;
        int selectorBits = CeilLog2(histograms);
        int histogram = selectorBits == 0 ? 0 : (int)reader.ReadBits(selectorBits);
        if (histogram >= histograms)
        {
            throw JxlDescriptor.Corrupt("Invalid histogram selector.");
        }

        JxlBlockContextMap contexts = BlockContexts;
        int contextOffset = histogram * contexts.AcContexts;
        int shift = (int)frame.Passes.Shift[pass];
        int[] order = orders[pass];
        int[][] nonZeros = [new int[GroupBlocks * GroupBlocks], new int[GroupBlocks * GroupBlocks], new int[GroupBlocks * GroupBlocks]];
        using var symbols = new JxlSymbolReader(codes[pass], ref reader);
        int offset = 0;
        for (int by = 0; by < height; by++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = (y0 + by) * XBlocks + x0;
            for (int bx = 0; bx < width; bx++)
            {
                JxlVarblock varblock = Varblocks[row + bx];
                if (!varblock.IsFirst)
                {
                    continue;
                }

                JxlAcStrategyType type = varblock.Type;
                int size = BlockSize << type.Log2Blocks;
                foreach (int c in YFirstOrder)
                {
                    if (!TryChannelBlock(c, bx, by, out int sbx, out int sby))
                    {
                        continue;
                    }

                    int blockContext = contexts.Context(DcBuckets[row + bx], QuantField[(y0 + by) * XBlocks + x0 + sbx], type.OrderBucket, c);
                    int nonZero = ReadNonZeros(ref reader, symbols, nonZeros[c], type, sbx, sby, contexts, blockContext, contextOffset);
                    ReadBlockCoefficients(ref reader, symbols, type, c, nonZero, contextOffset + contexts.ZeroDensityContextsOffset(blockContext),
                        order, shift, coefficients.AsSpan(c * area + offset, size));
                }

                offset += size;
            }
        }

        if (!symbols.IsFinalStateValid)
        {
            throw JxlDescriptor.Corrupt("Invalid ANS stream of AC coefficients.");
        }
    }

    // The number of non-zero coefficients, predicted from the blocks above and to the left; the count per covered block
    // becomes the prediction of later blocks.
    private int ReadNonZeros(ref JxlBitReader reader, JxlSymbolReader symbols, int[] counts, JxlAcStrategyType type, int sbx, int sby,
        JxlBlockContextMap contexts, int blockContext, int contextOffset)
    {
        int log2Covered = type.Log2Blocks;
        int covered = 1 << log2Covered;
        int predicted = sbx == 0 ? (sby == 0 ? 32 : counts[(sby - 1) * GroupBlocks])
            : sby == 0 ? counts[sby * GroupBlocks + sbx - 1]
            : (counts[(sby - 1) * GroupBlocks + sbx] + counts[sby * GroupBlocks + sbx - 1] + 1) / 2;
        int nonZero = (int)symbols.Read(contexts.NonZeroContext(predicted, blockContext) + contextOffset, ref reader);
        if (nonZero < 0 || nonZero > (BlockSize << log2Covered) - covered)
        {
            throw JxlDescriptor.Corrupt("Invalid AC: too many non-zero coefficients.");
        }

        for (int y = 0; y < type.BlocksY; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < type.BlocksX; x++)
            {
                counts[(sby + y) * GroupBlocks + sbx + x] = (nonZero + covered - 1) >> log2Covered;
            }
        }

        return nonZero;
    }

    // Adds the coefficients of one channel of a varblock in coefficient order, until all non-zeros are read.
    private static void ReadBlockCoefficients(ref JxlBitReader reader, JxlSymbolReader symbols, JxlAcStrategyType type, int c, int nonZero,
        int histogramOffset, int[] order, int shift, Span<int> block)
    {
        int log2Covered = type.Log2Blocks;
        int covered = 1 << log2Covered;
        int size = block.Length;
        int coefficientOffset = JxlAcStrategy.OrderOffset(type.OrderBucket, c);
        int previous = nonZero > size / 16 ? 0 : 1;
        for (int k = covered; k < size && nonZero != 0; k++)
        {
            if (nonZero > size - k) throw JxlDescriptor.Corrupt("AC non-zero count exceeds the remaining coefficients.");
            int context = histogramOffset + JxlBlockContextMap.ZeroDensityContext(nonZero, k, log2Covered, previous);
            uint token = symbols.Read(context, ref reader);
            block[order[coefficientOffset + k]] += unchecked(UnpackSigned(token) << shift);
            previous = token != 0 ? 1 : 0;
            nonZero -= previous;
        }

        if (nonZero != 0)
        {
            throw JxlDescriptor.Corrupt("Invalid AC: non-zero coefficients left at the end of a block.");
        }
    }

    /// <summary>Dequantises the varblocks of a group, adds chroma from luma and the DC, and transforms them to pixels.</summary>
    public void RenderGroup(int group, int[] coefficients)
    {
        (int x0, int y0, int width, int height) = BlockGroupRect(group);
        int area = width * height * BlockSize;
        int maxSize = UsedStrategies.MaxCoefficients;

        float[] block = new float[3 * maxSize];
        float[] scratch = new float[JxlDct.ScratchSize(maxSize)];
        int offset = 0;
        for (int by = 0; by < height; by++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int absoluteY = y0 + by;
            int row = absoluteY * XBlocks + x0;
            int tileRow = absoluteY / ColorTileBlocks * xTiles;
            for (int bx = 0; bx < width; bx++)
            {
                JxlVarblock varblock = Varblocks[row + bx];
                if (!varblock.IsFirst)
                {
                    continue;
                }

                JxlAcStrategyType type = varblock.Type;
                int size = BlockSize << type.Log2Blocks;
                int tile = tileRow + (x0 + bx) / ColorTileBlocks;
                Dequantize(type, QuantField[row + bx], YToXRatio(YToX[tile]), YToBRatio(YToB[tile]), coefficients, area, offset, size, block);
                for (int c = 0; c < 3; c++)
                {
                    int dcIndex = ((y0 >> vShift[c]) + (by >> vShift[c])) * XBlocks + (x0 >> hShift[c]) + (bx >> hShift[c]);
                    JxlDct.LowestFrequenciesFromDc(type, dc[c].AsSpan(dcIndex), XBlocks, block.AsSpan(c * size, size), scratch);
                }

                foreach (int c in YFirstOrder)
                {
                    if (!TryChannelBlock(c, bx, by, out int sbx, out int sby))
                    {
                        continue;
                    }

                    JxlPlane plane = Planes[c];
                    int pixel = plane.Offset(((x0 >> hShift[c]) + sbx) * JxlFrameHeader.BlockDim, ((y0 >> vShift[c]) + sby) * JxlFrameHeader.BlockDim);
                    JxlDct.TransformToPixels(type, block.AsSpan(c * size, size), plane.Data.AsSpan(pixel), plane.Stride, scratch);
                }

                offset += size;
            }
        }
    }

    // DequantBlock: matrix × quantisation step × bias-adjusted value, then X and B from Y through chroma from luma.
    private void Dequantize(JxlAcStrategyType type, int quant, float xCc, float bCc, int[] coefficients, int area, int offset, int size,
        float[] block)
    {
        float step = invGlobalScale / quant;
        float stepX = step * xDmMultiplier;
        float stepB = step * bDmMultiplier;
        float[] matrix = matrices.Matrix(type);
        ReadOnlySpan<int> qx = coefficients.AsSpan(offset, size);
        ReadOnlySpan<int> qy = coefficients.AsSpan(area + offset, size);
        ReadOnlySpan<int> qb = coefficients.AsSpan(2 * area + offset, size);
        for (int k = 0; k < size; k++)
        {
            float xMul = matrix[k] * stepX;
            float yMul = matrix[size + k] * step;
            float bMul = matrix[2 * size + k] * stepB;
            float x = AdjustQuantBias(0, qx[k]) * xMul;
            float y = AdjustQuantBias(1, qy[k]) * yMul;
            float b = AdjustQuantBias(2, qb[k]) * bMul;
            block[k] = JxlFastMath.Fma(xCc, y, x);
            block[size + k] = y;
            block[2 * size + k] = JxlFastMath.Fma(bCc, y, b);
        }
    }

    // The expected value of a coefficient quantised to q: 0, ±bias for |q| = 1, else q - bias / q. libjxl uses an
    // approximate reciprocal here; the exact one differs by about 2e-5 of the bias term.
    private float AdjustQuantBias(int c, int quantized)
    {
        float q = quantized;
        float magnitude = Math.Abs(q);
        if (magnitude < 1.125f)
        {
            return magnitude > 0 ? MathF.CopySign(quantBiases[c], q) : 0;
        }

        return JxlFastMath.Fma(-quantBiases[3], 1.0f / q, q);
    }
}
