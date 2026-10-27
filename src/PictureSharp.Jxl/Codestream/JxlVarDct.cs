using System.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream;

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
    private const byte Invalid = 0xFF;

    private readonly JxlFrameHeader frame;
    private readonly JxlQuantWeights matrices;
    private readonly float[] quantBiases;
    private readonly int bitDepth;
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
    private float colorScale = 1.0f / 84;
    private float baseCorrelationX;
    private float baseCorrelationB = 1.0f;

    public JxlVarDctFrame(JxlFrameHeader frame, JxlImageMetadata metadata, JxlQuantWeights matrices, float[] quantBiases)
    {
        this.frame = frame;
        this.matrices = matrices;
        this.quantBiases = quantBiases;
        bitDepth = (int)metadata.BitDepth.BitsPerSample;
        XBlocks = (int)frame.XSizeBlocks;
        YBlocks = (int)frame.YSizeBlocks;
        for (int c = 0; c < 3; c++)
        {
            hShift[c] = frame.HShift(c);
            vShift[c] = frame.VShift(c);
        }

        xTiles = DivCeil(XBlocks, ColorTileBlocks);
        int blocks = XBlocks * YBlocks;
        Strategies = new byte[blocks];
        Array.Fill(Strategies, Invalid);
        QuantField = new int[blocks];
        Sharpness = new byte[blocks];
        DcBuckets = new byte[blocks];
        YToX = new sbyte[xTiles * DivCeil(YBlocks, ColorTileBlocks)];
        YToB = new sbyte[YToX.Length];
        dc = [new float[blocks], new float[blocks], new float[blocks]];
        xDmMultiplier = MathF.Pow(1 / 1.25f, frame.XQmScale - 2.0f);
        bDmMultiplier = MathF.Pow(1 / 1.25f, frame.BQmScale - 2.0f);
        Planes = new JxlPlane[3];
        for (int c = 0; c < 3; c++)
        {
            Planes[c] = new JxlPlane(DivCeil((int)frame.XSize, 1 << hShift[c]), DivCeil((int)frame.YSize, 1 << vShift[c]),
                (XBlocks >> hShift[c]) * 8, (YBlocks >> vShift[c]) * 8);
        }
    }

    public int XBlocks { get; }

    public int YBlocks { get; }

    /// <summary>Per block: strategy &lt;&lt; 1 | (first block of its varblock), or 0xFF before AC metadata.</summary>
    public byte[] Strategies { get; }

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

    public uint UsedStrategies { get; private set; }

    /// <summary>Reads the quantiser, the block context map and the DC chroma-from-luma factors (DecodeGlobalDCInfo).</summary>
    public void ReadGlobal(ref JxlBitReader reader)
    {
        uint globalScale = JxlFields.U32(ref reader, JxlU32.BitsOffset(11, 1), JxlU32.BitsOffset(11, 2049), JxlU32.BitsOffset(12, 4097),
            JxlU32.BitsOffset(16, 8193));
        uint quantDc = JxlFields.U32(ref reader, JxlU32.Val(16), JxlU32.BitsOffset(5, 1), JxlU32.BitsOffset(8, 1), JxlU32.BitsOffset(16, 1));
        QuantScale = (float)(globalScale * (1.0 / 65536));
        invGlobalScale = (float)(1.0 * 65536 / globalScale);
        float invQuantDc = invGlobalScale / quantDc;
        for (int c = 0; c < 3; c++)
        {
            mulDc[c] = invQuantDc * matrices.DcQuant[c];
        }

        BlockContexts = JxlBlockContextMap.Read(ref reader);
        int yToXDc = 0;
        int yToBDc = 0;
        if (!reader.ReadBool())
        {
            uint colorFactor = JxlFields.U32(ref reader, JxlU32.Val(84), JxlU32.Val(256), JxlU32.BitsOffset(8, 2), JxlU32.BitsOffset(16, 258));
            colorScale = 1.0f / colorFactor;
            baseCorrelationX = JxlFields.F16(ref reader);
            baseCorrelationB = JxlFields.F16(ref reader);
            if (Math.Abs(baseCorrelationX) > 4.0f || Math.Abs(baseCorrelationB) > 4.0f)
            {
                throw JxlDescriptor.Corrupt("Chroma-from-luma base correlation is out of range.");
            }

            yToXDc = (int)reader.ReadBits(8) - 128;
            yToBDc = (int)reader.ReadBits(8) - 128;
        }

        dcFactors[0] = YToXRatio(yToXDc);
        dcFactors[2] = YToBRatio(yToBDc);
    }

    public JxlBlockContextMap BlockContexts { get; private set; } = JxlBlockContextMap.Default;

    private float YToXRatio(int factor) => baseCorrelationX + factor * colorScale;

    private float YToBRatio(int factor) => baseCorrelationB + factor * colorScale;

    private (int X0, int Y0, int Width, int Height) DcGroupRect(int group)
    {
        int dimension = frame.GroupDimension;
        int x0 = (int)(group % frame.XDcGroups) * dimension;
        int y0 = (int)(group / frame.XDcGroups) * dimension;
        return (x0, y0, Math.Min(dimension, XBlocks - x0), Math.Min(dimension, YBlocks - y0));
    }

    /// <summary>Takes the DC of the frame from a decoded DC frame (kUseDcFrame) instead of LF groups.</summary>
    public void UseDcFrame(JxlPlane[] planes)
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
            channels[c < 2 ? c ^ 1 : c] = new JxlChannel(width >> hShift[c], height >> vShift[c]);
        }

        var image = new JxlModularImage(bitDepth, [.. channels]);
        JxlModularStream.Decode(ref reader, image, 1 + group, tree, undoTransforms: true);
        if (hShift.All(shift => shift == 0) && vShift.All(shift => shift == 0))
        {
            float facX = mulDc[0] * mul;
            float facY = mulDc[1] * mul;
            float facB = mulDc[2] * mul;
            for (int y = 0; y < height; y++)
            {
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
            foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
            {
                float factor = mulDc[c] * mul;
                JxlChannel channel = channels[c < 2 ? c ^ 1 : c];
                for (int y = 0; y < channel.Height; y++)
                {
                    Span<int> q = channel.Row(y);
                    int row = ((y0 >> vShift[c]) + y) * XBlocks + (x0 >> hShift[c]);
                    for (int x = 0; x < channel.Width; x++)
                    {
                        dc[c][row + x] = q[x] * factor;
                    }
                }
            }
        }

        for (int y = 0; y < height; y++)
        {
            Span<int> qx = channels[1].Row(y >> vShift[0]);
            Span<int> qy = channels[0].Row(y >> vShift[1]);
            Span<int> qb = channels[2].Row(y >> vShift[2]);
            int row = (y0 + y) * XBlocks + x0;
            for (int x = 0; x < width; x++)
            {
                DcBuckets[row + x] = BlockContexts.DcContexts <= 1 ? (byte)0
                    : (byte)BlockContexts.DcBucket(qx[x >> hShift[0]], qy[x >> hShift[1]], qb[x >> hShift[2]]);
            }
        }
    }

    /// <summary>Decodes the chroma-from-luma map, AC strategies, quantisation field and EPF sharpness of an LF group.</summary>
    public void DecodeAcMetadata(ref JxlBitReader reader, int group, JxlModularTree? tree)
    {
        (int x0, int y0, int width, int height) = DcGroupRect(group);
        int count = (int)reader.ReadBits(CeilLog2(width * height)) + 1;
        int tileX0 = x0 >> 3;
        int tileY0 = y0 >> 3;
        int tileWidth = (width + 7) >> 3;
        int tileHeight = (height + 7) >> 3;
        var image = new JxlModularImage(bitDepth,
        [
            new JxlChannel(tileWidth, tileHeight, 3, 3), new JxlChannel(tileWidth, tileHeight, 3, 3), new JxlChannel(count, 2),
            new JxlChannel(width, height),
        ]);
        JxlModularStream.Decode(ref reader, image, (int)(1 + 2 * frame.DcGroups + group), tree, undoTransforms: true);
        for (int y = 0; y < tileHeight; y++)
        {
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
        bool is444 = hShift.All(shift => shift == 0) && vShift.All(shift => shift == 0);
        int xLimit = Math.Min(XBlocks, x0 + width);
        int yLimit = Math.Min(YBlocks, y0 + height);
        int num = 0;
        uint used = 0;
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
                if (Strategies[index] != Invalid)
                {
                    continue;
                }

                if (num >= count || types[num] is < 0 or >= JxlAcStrategy.Count)
                {
                    throw JxlDescriptor.Corrupt("Invalid AC strategy.");
                }

                var type = (JxlAcStrategyType)types[num];
                used |= 1u << (int)type;
                int blocksX = JxlAcStrategy.BlocksX(type);
                int blocksY = JxlAcStrategy.BlocksY(type);
                if ((blocksX > 1 || blocksY > 1) && !is444)
                {
                    throw JxlDescriptor.Corrupt("AC strategy not compatible with chroma subsampling.");
                }

                if (x + blocksX > (x / GroupBlocks + 1) * GroupBlocks || x + blocksX > xLimit
                    || y + blocksY > (y / GroupBlocks + 1) * GroupBlocks || y + blocksY > yLimit)
                {
                    throw JxlDescriptor.Corrupt("AC strategy overflows its group.");
                }

                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        int position = index + by * XBlocks + bx;
                        if (Strategies[position] != Invalid)
                        {
                            throw JxlDescriptor.Corrupt("Invalid AC strategy: block overlap.");
                        }

                        Strategies[position] = (byte)(((int)type << 1) | (by == 0 && bx == 0 ? 1 : 0));
                    }
                }

                QuantField[index] = 1 + Math.Max(0, Math.Min(QuantMax - 1, quants[num]));
                num++;
            }
        }

        UsedStrategies |= used;
    }

    /// <summary>Adaptive DC smoothing, between the last LF group and the first pass group (AdaptiveDCSmoothing).</summary>
    public void SmoothDc()
    {
        if ((frame.Flags & (JxlFrameFlags.SkipAdaptiveDcSmoothing | JxlFrameFlags.UseDcFrame)) != 0 || XBlocks <= 2 || YBlocks <= 2)
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
        int x0 = (int)(group % frame.XGroups) * GroupBlocks;
        int y0 = (int)(group / frame.XGroups) * GroupBlocks;
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
            int row = (y0 + by) * XBlocks + x0;
            for (int bx = 0; bx < width; bx++)
            {
                byte strategy = Strategies[row + bx];
                if ((strategy & 1) == 0)
                {
                    continue;
                }

                var type = (JxlAcStrategyType)(strategy >> 1);
                int log2Covered = JxlAcStrategy.Log2Blocks(type);
                int covered = 1 << log2Covered;
                int size = covered * BlockSize;
                int bucket = JxlAcStrategy.Order(type);
                foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
                {
                    int sbx = bx >> hShift[c];
                    int sby = by >> vShift[c];
                    if (sbx << hShift[c] != bx || sby << vShift[c] != by)
                    {
                        continue;
                    }

                    int[] counts = nonZeros[c];
                    int predicted = sbx == 0 ? (sby == 0 ? 32 : counts[(sby - 1) * GroupBlocks])
                        : sby == 0 ? counts[sby * GroupBlocks + sbx - 1]
                        : (counts[(sby - 1) * GroupBlocks + sbx] + counts[sby * GroupBlocks + sbx - 1] + 1) / 2;
                    int blockContext = contexts.Context(DcBuckets[row + bx], QuantField[(y0 + by) * XBlocks + x0 + sbx], bucket, c);
                    int nonZero = (int)symbols.Read(contexts.NonZeroContext(predicted, blockContext) + contextOffset, ref reader);
                    if (nonZero < 0 || nonZero > size - covered)
                    {
                        throw JxlDescriptor.Corrupt("Invalid AC: too many non-zero coefficients.");
                    }

                    for (int y = 0; y < JxlAcStrategy.BlocksY(type); y++)
                    {
                        for (int x = 0; x < JxlAcStrategy.BlocksX(type); x++)
                        {
                            counts[(sby + y) * GroupBlocks + sbx + x] = (nonZero + covered - 1) >> log2Covered;
                        }
                    }

                    int histogramOffset = contextOffset + contexts.ZeroDensityContextsOffset(blockContext);
                    int coefficientOffset = JxlAcStrategy.OrderOffset(bucket, c);
                    Span<int> block = coefficients.AsSpan(c * area + offset, size);
                    int previous = nonZero > size / 16 ? 0 : 1;
                    for (int k = covered; k < size && nonZero != 0; k++)
                    {
                        int context = histogramOffset + JxlBlockContextMap.ZeroDensityContext(nonZero, k, log2Covered, previous);
                        uint token = symbols.Read(context, ref reader);
                        int value = (int)(token >> 1) ^ -(int)(token & 1);
                        block[order[coefficientOffset + k]] += unchecked(value << shift);
                        previous = token != 0 ? 1 : 0;
                        nonZero -= previous;
                    }

                    if (nonZero != 0)
                    {
                        throw JxlDescriptor.Corrupt("Invalid AC: non-zero coefficients left at the end of a block.");
                    }
                }

                offset += size;
            }
        }

        if (!symbols.IsFinalStateValid)
        {
            throw JxlDescriptor.Corrupt("Invalid ANS stream of AC coefficients.");
        }
    }

    /// <summary>Dequantises the varblocks of a group, adds chroma from luma and the DC, and transforms them to pixels.</summary>
    public void RenderGroup(int group, int[] coefficients)
    {
        (int x0, int y0, int width, int height) = BlockGroupRect(group);
        int area = width * height * BlockSize;
        int maxSize = 0;
        for (int s = 0; s < JxlAcStrategy.Count; s++)
        {
            if ((UsedStrategies & (1u << s)) != 0)
            {
                maxSize = Math.Max(maxSize, BlockSize << JxlAcStrategy.Log2Blocks((JxlAcStrategyType)s));
            }
        }

        float[] block = new float[3 * maxSize];
        float[] scratch = new float[JxlDct.ScratchSize(maxSize)];
        int offset = 0;
        for (int by = 0; by < height; by++)
        {
            int absoluteY = y0 + by;
            int row = absoluteY * XBlocks + x0;
            int tileRow = absoluteY / ColorTileBlocks * xTiles;
            for (int bx = 0; bx < width; bx++)
            {
                byte strategy = Strategies[row + bx];
                if ((strategy & 1) == 0)
                {
                    continue;
                }

                var type = (JxlAcStrategyType)(strategy >> 1);
                int size = BlockSize << JxlAcStrategy.Log2Blocks(type);
                int tile = tileRow + (x0 + bx) / ColorTileBlocks;
                Dequantize(type, QuantField[row + bx], YToXRatio(YToX[tile]), YToBRatio(YToB[tile]), coefficients, area, offset, size, block);
                for (int c = 0; c < 3; c++)
                {
                    int dcIndex = ((y0 >> vShift[c]) + (by >> vShift[c])) * XBlocks + (x0 >> hShift[c]) + (bx >> hShift[c]);
                    JxlDct.LowestFrequenciesFromDc(type, dc[c].AsSpan(dcIndex), XBlocks, block.AsSpan(c * size, size), scratch);
                }

                foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
                {
                    int sbx = bx >> hShift[c];
                    int sby = by >> vShift[c];
                    if (sbx << hShift[c] != bx || sby << vShift[c] != by)
                    {
                        continue;
                    }

                    JxlPlane plane = Planes[c];
                    int pixel = plane.Offset(((x0 >> hShift[c]) + sbx) * 8, ((y0 >> vShift[c]) + sby) * 8);
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

    private static int DivCeil(int value, int divisor) => (value + divisor - 1) / divisor;

    public static int CeilLog2(long value) => value <= 1 ? 0 : 64 - BitOperations.LeadingZeroCount((ulong)(value - 1));
}
