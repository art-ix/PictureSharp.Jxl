using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.VarDct;

/// <summary>
/// The VarDCT inverse transforms (TransformToPixels), the forward DCT that turns DC values into the lowest
/// coefficients of large varblocks (LowestFrequenciesFromDC), and the encoder's forward transforms
/// (TransformFromPixels, DCFromLowestFrequencies). Scalar ports, all columns of a 1D pass at once, of libjxl v0.12.0
/// lib/jxl/dct-inl.h, lib/jxl/dec_transforms-inl.h, lib/jxl/enc_transforms-inl.h and lib/jxl/transpose-inl.h
/// (BSD-3-Clause), with the same operation order and fused multiply-adds.
/// </summary>
internal static partial class JxlDct
{
    internal const float Sqrt2 = 1.41421356237f;

    /// <summary>Scratch floats a transform of a varblock of <paramref name="coefficients"/> coefficients needs.</summary>
    public static int ScratchSize(int coefficients) => 4 * coefficients + 4 * 256 + 64;

    /// <summary>
    /// Writes the pixels of a varblock: <paramref name="coefficients"/> (destroyed) are laid out with the smaller
    /// dimension as rows; <paramref name="pixels"/> starts at the top-left pixel with the given stride.
    /// </summary>
    public static void TransformToPixels(JxlAcStrategyType strategy, Span<float> coefficients, Span<float> pixels, int stride,
        Span<float> scratch)
    {
        switch (strategy)
        {
            case JxlAcStrategyType.Identity:
                Identity(coefficients, pixels, stride);
                break;
            case JxlAcStrategyType.Dct8X4:
            case JxlAcStrategyType.Dct4X8:
                Dct4X8(strategy == JxlAcStrategyType.Dct8X4, coefficients, pixels, stride, scratch);
                break;
            case JxlAcStrategyType.Dct4X4:
                Dct4X4(coefficients, pixels, stride, scratch);
                break;
            case JxlAcStrategyType.Dct2X2:
                Dct2X2(coefficients, pixels, stride);
                break;
            case JxlAcStrategyType.Afv0 or JxlAcStrategyType.Afv1 or JxlAcStrategyType.Afv2 or JxlAcStrategyType.Afv3:
                Afv(strategy - JxlAcStrategyType.Afv0, coefficients, pixels, stride, scratch);
                break;
            default:
                int rows = 8 * strategy.BlocksY;
                int columns = 8 * strategy.BlocksX;
                ScaledIdct(rows, columns, coefficients, pixels, stride, scratch);
                break;
        }
    }

    /// <summary>
    /// Fills the lowest-frequency coefficients of a varblock from its DC values (one per covered block, read from
    /// <paramref name="dc"/> with <paramref name="dcStride"/>).
    /// </summary>
    public static void LowestFrequenciesFromDc(JxlAcStrategyType strategy, ReadOnlySpan<float> dc, int dcStride, Span<float> llf,
        Span<float> scratch)
    {
        int rows = strategy.BlocksY;
        int columns = strategy.BlocksX;
        if (rows * columns == 1)
        {
            llf[0] = dc[0];
            return;
        }

        // ReinterpretingDCT: a rows×columns DCT of the DC values, rescaled to the size of the varblock.
        Span<float> block = scratch[..(rows * columns)];
        ScaledDct(rows, columns, dc, dcStride, block, scratch[(rows * columns)..]);
        ReadOnlySpan<float> rowScales = ResampleScales(rows);
        ReadOnlySpan<float> columnScales = ResampleScales(columns);
        int outputStride = 8 * Math.Max(rows, columns);
        if (rows < columns)
        {
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < columns; x++)
                {
                    llf[y * outputStride + x] = block[y * columns + x] * rowScales[y] * columnScales[x];
                }
            }
        }
        else
        {
            for (int y = 0; y < columns; y++)
            {
                for (int x = 0; x < rows; x++)
                {
                    llf[y * outputStride + x] = block[y * rows + x] * columnScales[y] * rowScales[x];
                }
            }
        }
    }

    /// <summary>
    /// The coefficients of a varblock (TransformFromPixels), laid out as <see cref="TransformToPixels"/> reads them:
    /// <paramref name="pixels"/> starts at the top-left pixel with the given stride.
    /// </summary>
    public static void TransformFromPixels(JxlAcStrategyType strategy, ReadOnlySpan<float> pixels, int stride, Span<float> coefficients,
        Span<float> scratch)
    {
        switch (strategy)
        {
            case JxlAcStrategyType.Identity:
                ForwardIdentity(pixels, stride, coefficients);
                break;
            case JxlAcStrategyType.Dct8X4:
            case JxlAcStrategyType.Dct4X8:
                ForwardDct4X8(strategy == JxlAcStrategyType.Dct8X4, pixels, stride, coefficients, scratch);
                break;
            case JxlAcStrategyType.Dct4X4:
                ForwardDct4X4(pixels, stride, coefficients, scratch);
                break;
            case JxlAcStrategyType.Dct2X2:
                ForwardDct2X2(pixels, stride, coefficients);
                break;
            case JxlAcStrategyType.Afv0 or JxlAcStrategyType.Afv1 or JxlAcStrategyType.Afv2 or JxlAcStrategyType.Afv3:
                ForwardAfv(strategy - JxlAcStrategyType.Afv0, pixels, stride, coefficients, scratch);
                break;
            default:
                ScaledDct(8 * strategy.BlocksY, 8 * strategy.BlocksX, pixels, stride, coefficients, scratch);
                break;
        }
    }

    /// <summary>
    /// The DC values of the blocks a varblock covers, from its lowest-frequency coefficients (DCFromLowestFrequencies):
    /// the inverse of <see cref="LowestFrequenciesFromDc"/>. <paramref name="dc"/> is written with <paramref name="dcStride"/>.
    /// </summary>
    public static void DcFromLowestFrequencies(JxlAcStrategyType strategy, ReadOnlySpan<float> block, Span<float> dc, int dcStride,
        Span<float> scratch)
    {
        int rows = strategy.BlocksY;
        int columns = strategy.BlocksX;
        if (rows * columns == 1)
        {
            dc[0] = block[0];
            return;
        }

        // ReinterpretingIDCT: the lowest coefficients rescaled to a rows×columns DCT, then its IDCT.
        Span<float> lowest = scratch[..(rows * columns)];
        Span<float> rest = scratch[(rows * columns)..];
        ReadOnlySpan<float> rowScales = DownsampleScales(rows);
        ReadOnlySpan<float> columnScales = DownsampleScales(columns);
        int inputStride = 8 * Math.Max(rows, columns);
        if (rows < columns)
        {
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < columns; x++)
                {
                    lowest[y * columns + x] = block[y * inputStride + x] * rowScales[y] * columnScales[x];
                }
            }
        }
        else
        {
            for (int y = 0; y < columns; y++)
            {
                for (int x = 0; x < rows; x++)
                {
                    lowest[y * rows + x] = block[y * inputStride + x] * columnScales[y] * rowScales[x];
                }
            }
        }

        ScaledIdct(rows, columns, lowest, dc, dcStride, rest);
    }

    // DCTResampleScales from 8n to n coefficients: the reciprocals of ResampleScales.
    private static ReadOnlySpan<float> DownsampleScales(int blocks) => blocks switch
    {
        1 => DownsampleTables.Value[0],
        2 => DownsampleTables.Value[1],
        4 => DownsampleTables.Value[2],
        8 => DownsampleTables.Value[3],
        16 => DownsampleTables.Value[4],
        _ => DownsampleTables.Value[5],
    };

    private static readonly Lazy<float[][]> DownsampleTables = new(() =>
        [.. new[] { 1, 2, 4, 8, 16, 32 }.Select(n => ResampleScales(n).ToArray().Select(scale => (float)(1.0 / scale)).ToArray())]);

    private static void ForwardIdentity(ReadOnlySpan<float> pixels, int stride, Span<float> coefficients)
    {
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                float blockDc = 0;
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        blockDc += pixels[(y * 4 + iy) * stride + x * 4 + ix];
                    }
                }

                blockDc *= 1.0f / 16;
                float center = pixels[(y * 4 + 1) * stride + x * 4 + 1];
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        if (ix == 1 && iy == 1)
                        {
                            continue;
                        }

                        coefficients[(y + iy * 2) * 8 + x + ix * 2] = pixels[(y * 4 + iy) * stride + x * 4 + ix] - center;
                    }
                }

                coefficients[(y + 2) * 8 + x + 2] = coefficients[y * 8 + x];
                coefficients[y * 8 + x] = blockDc;
            }
        }

        ForwardQuarterDcs(coefficients);
    }

    // The top-left 2x2 coefficients from the DCs of the four quarters (the inverse of QuarterDcs).
    private static void ForwardQuarterDcs(Span<float> coefficients)
    {
        float block00 = coefficients[0];
        float block01 = coefficients[1];
        float block10 = coefficients[8];
        float block11 = coefficients[9];
        coefficients[0] = (block00 + block01 + block10 + block11) * 0.25f;
        coefficients[1] = (block00 + block01 - block10 - block11) * 0.25f;
        coefficients[8] = (block00 - block01 + block10 - block11) * 0.25f;
        coefficients[9] = (block00 - block01 - block10 + block11) * 0.25f;
    }

    private static void ForwardDct4X8(bool sideBySide, ReadOnlySpan<float> pixels, int stride, Span<float> coefficients, Span<float> scratch)
    {
        Span<float> block = stackalloc float[32];
        for (int half = 0; half < 2; half++)
        {
            if (sideBySide)
            {
                ScaledDct(8, 4, pixels[(half * 4)..], stride, block, scratch);
            }
            else
            {
                ScaledDct(4, 8, pixels[(half * 4 * stride)..], stride, block, scratch);
            }

            for (int iy = 0; iy < 4; iy++)
            {
                for (int ix = 0; ix < 8; ix++)
                {
                    coefficients[(half + iy * 2) * 8 + ix] = block[iy * 8 + ix];
                }
            }
        }

        float block0 = coefficients[0];
        float block1 = coefficients[8];
        coefficients[0] = (block0 + block1) * 0.5f;
        coefficients[8] = (block0 - block1) * 0.5f;
    }

    private static void ForwardDct4X4(ReadOnlySpan<float> pixels, int stride, Span<float> coefficients, Span<float> scratch)
    {
        Span<float> block = stackalloc float[16];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                ScaledDct(4, 4, pixels[(y * 4 * stride + x * 4)..], stride, block, scratch);
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        coefficients[(y + iy * 2) * 8 + x + ix * 2] = block[iy * 4 + ix];
                    }
                }
            }
        }

        ForwardQuarterDcs(coefficients);
    }

    private static void ForwardDct2X2(ReadOnlySpan<float> pixels, int stride, Span<float> coefficients)
    {
        if (ForwardDct2X2Sse41(pixels, stride, coefficients)) return;
        Span<float> block = stackalloc float[64];
        for (int y = 0; y < 8; y++)
        {
            pixels.Slice(y * stride, 8).CopyTo(block[(y * 8)..]);
        }

        Dct2TopBlock(8, block);
        Dct2TopBlock(4, block);
        Dct2TopBlock(2, block);
        block.CopyTo(coefficients);
    }

    // One level of the 2x2 Haar pyramid on the top-left size×size corner (DCT2TopBlock).
    private static void Dct2TopBlock(int size, Span<float> block)
    {
        Span<float> temp = stackalloc float[64];
        int count = size / 2;
        for (int y = 0; y < count; y++)
        {
            for (int x = 0; x < count; x++)
            {
                float c00 = block[y * 2 * 8 + x * 2];
                float c01 = block[y * 2 * 8 + x * 2 + 1];
                float c10 = block[(y * 2 + 1) * 8 + x * 2];
                float c11 = block[(y * 2 + 1) * 8 + x * 2 + 1];
                temp[y * 8 + x] = (c00 + c01 + c10 + c11) * 0.25f;
                temp[y * 8 + count + x] = (c00 + c01 - c10 - c11) * 0.25f;
                temp[(y + count) * 8 + x] = (c00 - c01 + c10 - c11) * 0.25f;
                temp[(y + count) * 8 + count + x] = (c00 - c01 - c10 + c11) * 0.25f;
            }
        }

        for (int y = 0; y < size; y++)
        {
            temp.Slice(y * 8, size).CopyTo(block[(y * 8)..]);
        }
    }

    private static void ForwardAfv(int afvKind, ReadOnlySpan<float> pixels, int stride, Span<float> coefficients, Span<float> scratch)
    {
        int afvX = afvKind & 1;
        int afvY = afvKind / 2;
        Span<float> block = stackalloc float[32];
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                block[(afvY == 1 ? 3 - iy : iy) * 4 + (afvX == 1 ? 3 - ix : ix)] = pixels[(iy + 4 * afvY) * stride + ix + 4 * afvX];
            }
        }

        // AFVDCT4x4: the transpose of the inverse basis.
        ReadOnlySpan<float> basis = JxlDctTables.AfvBasis;
        Span<float> coeff = stackalloc float[16];
        for (int i = AfvBasisAvx2(block, AfvBasisTransposed, coeff); i < 16; i++)
        {
            float sum = 0;
            for (int j = 0; j < 16; j++)
            {
                sum = JxlFastMath.Fma(block[j], basis[i * 16 + j], sum);
            }

            coeff[i] = sum;
        }

        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                coefficients[iy * 2 * 8 + ix * 2] = coeff[iy * 4 + ix];
            }
        }

        ScaledDct(4, 4, pixels[(afvY * 4 * stride + (afvX == 1 ? 0 : 4))..], stride, block, scratch);
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                coefficients[iy * 2 * 8 + ix * 2 + 1] = block[iy * 4 + ix];
            }
        }

        ScaledDct(4, 8, pixels[((afvY == 1 ? 0 : 4) * stride)..], stride, block, scratch);
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 8; ix++)
            {
                coefficients[(1 + iy * 2) * 8 + ix] = block[iy * 8 + ix];
            }
        }

        float block00 = coefficients[0] * 0.25f;
        float block01 = coefficients[1];
        float block10 = coefficients[8];
        coefficients[0] = (block00 + block01 + 2 * block10) * 0.25f;
        coefficients[1] = (block00 - block01) * 0.5f;
        coefficients[8] = (block00 + block01 - 2 * block10) * 0.25f;
    }

    private static ReadOnlySpan<float> ResampleScales(int blocks) => blocks switch
    {
        1 => JxlDctTables.ResampleScales1To8,
        2 => JxlDctTables.ResampleScales2To16,
        4 => JxlDctTables.ResampleScales4To32,
        8 => JxlDctTables.ResampleScales8To64,
        16 => JxlDctTables.ResampleScales16To128,
        _ => JxlDctTables.ResampleScales32To256,
    };

    private static ReadOnlySpan<float> Multipliers(int n) => n switch
    {
        4 => JxlDctTables.Multipliers4,
        8 => JxlDctTables.Multipliers8,
        16 => JxlDctTables.Multipliers16,
        32 => JxlDctTables.Multipliers32,
        64 => JxlDctTables.Multipliers64,
        128 => JxlDctTables.Multipliers128,
        _ => JxlDctTables.Multipliers256,
    };

    /// <summary>
    /// The rows×columns IDCT (ComputeScaledIDCT): <paramref name="from"/> holds min×max coefficients (smaller
    /// dimension first) and is used as scratch.
    /// </summary>
    private static void ScaledIdct(int rows, int columns, Span<float> from, Span<float> to, int toStride, Span<float> scratch)
    {
        if (rows == 8 && columns == 8 && ScaledIdct8x8Avx2(from, to, toStride)) return;
        Span<float> block = scratch[..(rows * columns)];
        Span<float> tmp = scratch[(rows * columns)..];
        if (rows < columns)
        {
            Transpose(rows, columns, from, columns, block, rows);
            Idct1D(columns, rows, block, rows, from, rows, tmp);
            Transpose(columns, rows, from, rows, block, columns);
            Idct1D(rows, columns, block, columns, to, toStride, tmp);
        }
        else
        {
            Idct1D(columns, rows, from, rows, block, rows, tmp);
            Transpose(columns, rows, block, rows, from, columns);
            Idct1D(rows, columns, from, columns, to, toStride, tmp);
        }
    }

    /// <summary>The scaled rows×columns DCT (ComputeScaledDCT) of a matrix read with <paramref name="fromStride"/>.</summary>
    private static void ScaledDct(int rows, int columns, ReadOnlySpan<float> from, int fromStride, Span<float> to, Span<float> scratch)
    {
        if (rows == 8 && columns == 8 && ScaledDct8x8Avx2(from, fromStride, to)) return;
        Span<float> block = scratch[..(rows * columns)];
        Span<float> tmp = scratch[(rows * columns)..];
        if (rows < columns)
        {
            Dct1D(rows, columns, from, fromStride, block, columns, tmp);
            Transpose(rows, columns, block, columns, to, rows);
            Dct1D(columns, rows, to, rows, block, rows, tmp);
            Transpose(columns, rows, block, rows, to, columns);
        }
        else
        {
            Dct1D(rows, columns, from, fromStride, to, columns, tmp);
            Transpose(rows, columns, to, columns, block, rows);
            Dct1D(columns, rows, block, rows, to, rows, tmp);
        }
    }

    /// <summary>to(c, r) = from(r, c) for a rows×columns matrix.</summary>
    private static void Transpose(int rows, int columns, ReadOnlySpan<float> from, int fromStride, Span<float> to, int toStride)
    {
        if (TransposeSimd(rows, columns, from, fromStride, to, toStride)) return;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                to[c * toStride + r] = from[r * fromStride + c];
            }
        }
    }

    /// <summary>
    /// The n-point IDCT down each of <paramref name="count"/> columns. <paramref name="from"/> is contiguous
    /// (<paramref name="fromStride"/> = <paramref name="count"/>) and destroyed: the columns are transformed in place,
    /// all at once row by row, then copied to <paramref name="to"/>.
    /// </summary>
    internal static void Idct1D(int n, int count, Span<float> from, int fromStride, Span<float> to, int toStride, Span<float> scratch)
    {
        System.Diagnostics.Debug.Assert(fromStride == count);
        Span<float> data = from[..(n * count)];
        if (n == 8)
        {
            // The leaf kernel writes straight to the destination rows instead of in place and a copy.
            int done = TransformColumnsSimd(8, count, from, fromStride, to, toStride, true, 1.0f);
            for (int c = done; c < count; c++)
            {
                Idct8(data, c, count, to, c, toStride);
            }

            return;
        }

        if (n == 16 && IdctColumns16Avx2(count, data, to, toStride, scratch)) return;
        IdctColumns(n, count, data, scratch);
        for (int i = 0; i < n; i++)
        {
            data.Slice(i * count, count).CopyTo(to[(i * toStride)..]);
        }
    }

    /// <summary>
    /// The n-point DCT down each of <paramref name="count"/> columns, scaled by 1/n (DCT1DWrapper); <paramref name="to"/>
    /// is contiguous (<paramref name="toStride"/> = <paramref name="count"/>) and must not overlap <paramref name="from"/>.
    /// </summary>
    internal static void Dct1D(int n, int count, ReadOnlySpan<float> from, int fromStride, Span<float> to, int toStride, Span<float> scratch)
    {
        System.Diagnostics.Debug.Assert(toStride == count);
        Span<float> data = to[..(n * count)];
        float scale = 1.0f / n;
        if (n == 8)
        {
            // The leaf kernel reads the source rows and stores scaled results, without the copy and scaling passes.
            int done = TransformColumnsSimd(8, count, from, fromStride, data, toStride, false, scale);
            for (int c = done; c < count; c++)
            {
                Dct8(from, c, fromStride, data, c, count, scale);
            }

            return;
        }

        if (n == 16 && DctColumns16Avx2(count, from, fromStride, data, true, scale, scratch)) return;
        for (int i = 0; i < n; i++)
        {
            from.Slice(i * fromStride, count).CopyTo(data[(i * count)..]);
        }

        DctColumns(n, count, data, scratch);
        for (int i = ScaleRowAvx2(data, scale); i < data.Length; i++)
        {
            data[i] = scale * data[i];
        }
    }

    // IDCT1DImpl on every column of the n×count matrix at once (row i holds element i of each column): even/odd
    // split, recursive halves, then the butterflies, each column with the same operations in the same order as a
    // single-column transform. Sizes 4 and 8 (the leaves of every larger size) are unrolled per column.
    internal static void IdctColumns(int n, int count, Span<float> data, Span<float> scratch)
    {
        int done = TransformColumnsSimd(n, count, data, count, data, count, true, 1.0f);
        switch (n)
        {
            case 1:
                return;
            case 2:
                Span<float> row0 = data[..count];
                Span<float> row1 = data.Slice(count, count);
                for (int c = done; c < count; c++)
                {
                    (float a, float b) = (row0[c], row1[c]);
                    row0[c] = a + b;
                    row1[c] = a - b;
                }

                return;
            case 4:
                for (int c = done; c < count; c++)
                {
                    Idct4(data[c], data[count + c], data[2 * count + c], data[3 * count + c],
                        out data[c], out data[count + c], out data[2 * count + c], out data[3 * count + c]);
                }

                return;
            case 8:
                for (int c = done; c < count; c++)
                {
                    Idct8(data, c, count);
                }

                return;
        }

        if (n == 16 && IdctColumns16Avx2(count, data, data, count, scratch)) return;
        int half = n / 2;
        int halfSize = half * count;
        Span<float> even = scratch[..halfSize];
        Span<float> odd = scratch.Slice(halfSize, halfSize);
        Span<float> rest = scratch[(2 * halfSize)..];
        for (int i = 0; i < half; i++)
        {
            data.Slice(2 * i * count, count).CopyTo(even[(i * count)..]);
            data.Slice((2 * i + 1) * count, count).CopyTo(odd[(i * count)..]);
        }

        IdctColumns(half, count, even, rest);
        for (int i = half - 1; i > 0; i--)
        {
            Span<float> row = odd.Slice(i * count, count);
            ReadOnlySpan<float> previous = odd.Slice((i - 1) * count, count);
            for (int c = AddRowAvx2(row, previous); c < count; c++)
            {
                row[c] += previous[c];
            }
        }

        Span<float> first = odd[..count];
        for (int c = ScaleRowAvx2(first, Sqrt2); c < count; c++)
        {
            first[c] *= Sqrt2;
        }

        IdctColumns(half, count, odd, rest);
        ReadOnlySpan<float> multipliers = Multipliers(n);
        for (int i = 0; i < half; i++)
        {
            float multiplier = multipliers[i];
            ReadOnlySpan<float> evenRow = even.Slice(i * count, count);
            ReadOnlySpan<float> oddRow = odd.Slice(i * count, count);
            Span<float> top = data.Slice(i * count, count);
            Span<float> bottom = data.Slice((n - i - 1) * count, count);
            for (int c = MergeRowsAvx2(evenRow, oddRow, top, bottom, multiplier); c < count; c++)
            {
                top[c] = JxlFastMath.Fma(multiplier, oddRow[c], evenRow[c]);
                bottom[c] = JxlFastMath.Fma(-multiplier, oddRow[c], evenRow[c]);
            }
        }
    }

    // The 8-point IDCT of the column starting at data[offset] with the given stride, in place.
    private static void Idct8(Span<float> data, int offset, int stride) => Idct8(data, offset, stride, data, offset, stride);

    // The 8-point IDCT of the column at from[fromOffset] (step fromStride), stored at to[toOffset] (step toStride).
    private static void Idct8(ReadOnlySpan<float> from, int fromOffset, int fromStride, Span<float> to, int toOffset, int toStride)
    {
        float in0 = from[fromOffset], in1 = from[fromOffset + fromStride], in2 = from[fromOffset + 2 * fromStride], in3 = from[fromOffset + 3 * fromStride];
        float in4 = from[fromOffset + 4 * fromStride], in5 = from[fromOffset + 5 * fromStride], in6 = from[fromOffset + 6 * fromStride];
        float in7 = from[fromOffset + 7 * fromStride];
        Idct4(in0, in2, in4, in6, out float e0, out float e1, out float e2, out float e3);
        Idct4(in1 * Sqrt2, in3 + in1, in5 + in3, in7 + in5, out float o0, out float o1, out float o2, out float o3);
        ReadOnlySpan<float> m = JxlDctTables.Multipliers8;
        to[toOffset + 7 * toStride] = JxlFastMath.Fma(-m[0], o0, e0);
        to[toOffset] = JxlFastMath.Fma(m[0], o0, e0);
        to[toOffset + 6 * toStride] = JxlFastMath.Fma(-m[1], o1, e1);
        to[toOffset + toStride] = JxlFastMath.Fma(m[1], o1, e1);
        to[toOffset + 5 * toStride] = JxlFastMath.Fma(-m[2], o2, e2);
        to[toOffset + 2 * toStride] = JxlFastMath.Fma(m[2], o2, e2);
        to[toOffset + 4 * toStride] = JxlFastMath.Fma(-m[3], o3, e3);
        to[toOffset + 3 * toStride] = JxlFastMath.Fma(m[3], o3, e3);
    }

    private static void Idct4(float in0, float in1, float in2, float in3, out float out0, out float out1, out float out2, out float out3)
    {
        float e0 = in0 + in2;
        float e1 = in0 - in2;
        float odd0 = in1 * Sqrt2;
        float odd1 = in3 + in1;
        float p0 = odd0 + odd1;
        float p1 = odd0 - odd1;
        ReadOnlySpan<float> m = JxlDctTables.Multipliers4;
        out0 = JxlFastMath.Fma(m[0], p0, e0);
        out3 = JxlFastMath.Fma(-m[0], p0, e0);
        out1 = JxlFastMath.Fma(m[1], p1, e1);
        out2 = JxlFastMath.Fma(-m[1], p1, e1);
    }

    // DCT1DImpl on every column of the n×count matrix at once, in place; see IdctColumns.
    internal static void DctColumns(int n, int count, Span<float> data, Span<float> scratch)
    {
        int done = TransformColumnsSimd(n, count, data, count, data, count, false, 1.0f);
        switch (n)
        {
            case 1:
                return;
            case 2:
                Span<float> row0 = data[..count];
                Span<float> row1 = data.Slice(count, count);
                for (int c = done; c < count; c++)
                {
                    (float a, float b) = (row0[c], row1[c]);
                    row0[c] = a + b;
                    row1[c] = a - b;
                }

                return;
            case 4:
                for (int c = done; c < count; c++)
                {
                    Dct4(data[c], data[count + c], data[2 * count + c], data[3 * count + c],
                        out data[c], out data[count + c], out data[2 * count + c], out data[3 * count + c]);
                }

                return;
            case 8:
                for (int c = done; c < count; c++)
                {
                    Dct8(data, c, count);
                }

                return;
        }

        if (n == 16 && DctColumns16Avx2(count, data, count, data, false, 1.0f, scratch)) return;
        int half = n / 2;
        int halfSize = half * count;
        Span<float> even = scratch[..halfSize];
        Span<float> odd = scratch.Slice(halfSize, halfSize);
        Span<float> rest = scratch[(2 * halfSize)..];
        ReadOnlySpan<float> multipliers = Multipliers(n);
        for (int i = 0; i < half; i++)
        {
            float multiplier = multipliers[i];
            ReadOnlySpan<float> top = data.Slice(i * count, count);
            ReadOnlySpan<float> bottom = data.Slice((n - 1 - i) * count, count);
            Span<float> evenRow = even.Slice(i * count, count);
            Span<float> oddRow = odd.Slice(i * count, count);
            for (int c = SplitRowsAvx2(top, bottom, evenRow, oddRow, multiplier); c < count; c++)
            {
                evenRow[c] = top[c] + bottom[c];
                oddRow[c] = (top[c] - bottom[c]) * multiplier;
            }
        }

        DctColumns(half, count, even, rest);
        DctColumns(half, count, odd, rest);
        Span<float> first = odd[..count];
        ReadOnlySpan<float> second = odd.Slice(count, count);
        for (int c = CombineFirstAvx2(first, second); c < count; c++)
        {
            first[c] = JxlFastMath.Fma(first[c], Sqrt2, second[c]);
        }

        for (int i = 1; i + 1 < half; i++)
        {
            Span<float> row = odd.Slice(i * count, count);
            ReadOnlySpan<float> next = odd.Slice((i + 1) * count, count);
            for (int c = AddRowAvx2(row, next); c < count; c++)
            {
                row[c] += next[c];
            }
        }

        for (int i = 0; i < half; i++)
        {
            even.Slice(i * count, count).CopyTo(data[(2 * i * count)..]);
            odd.Slice(i * count, count).CopyTo(data[((2 * i + 1) * count)..]);
        }
    }

    // The 8-point DCT of the column at from[fromOffset] (step fromStride), each result times scale stored at
    // to[toOffset] (step toStride): the same values as Dct8 below followed by a separate scaling pass.
    private static void Dct8(ReadOnlySpan<float> from, int fromOffset, int fromStride, Span<float> to, int toOffset, int toStride, float scale)
    {
        float m0 = from[fromOffset], m1 = from[fromOffset + fromStride], m2 = from[fromOffset + 2 * fromStride], m3 = from[fromOffset + 3 * fromStride];
        float m4 = from[fromOffset + 4 * fromStride], m5 = from[fromOffset + 5 * fromStride], m6 = from[fromOffset + 6 * fromStride];
        float m7 = from[fromOffset + 7 * fromStride];
        ReadOnlySpan<float> m = JxlDctTables.Multipliers8;
        Dct4(m0 + m7, m1 + m6, m2 + m5, m3 + m4, out float t0, out float t2, out float t4, out float t6);
        Dct4((m0 - m7) * m[0], (m1 - m6) * m[1], (m2 - m5) * m[2], (m3 - m4) * m[3], out float u0, out float u1, out float u2, out float u3);
        to[toOffset] = scale * t0;
        to[toOffset + toStride] = scale * JxlFastMath.Fma(u0, Sqrt2, u1);
        to[toOffset + 2 * toStride] = scale * t2;
        to[toOffset + 3 * toStride] = scale * (u1 + u2);
        to[toOffset + 4 * toStride] = scale * t4;
        to[toOffset + 5 * toStride] = scale * (u2 + u3);
        to[toOffset + 6 * toStride] = scale * t6;
        to[toOffset + 7 * toStride] = scale * u3;
    }

    // The 8-point DCT of the column starting at data[offset] with the given stride, in place.
    private static void Dct8(Span<float> data, int offset, int stride)
    {
        float m0 = data[offset], m1 = data[offset + stride], m2 = data[offset + 2 * stride], m3 = data[offset + 3 * stride];
        float m4 = data[offset + 4 * stride], m5 = data[offset + 5 * stride], m6 = data[offset + 6 * stride], m7 = data[offset + 7 * stride];
        ReadOnlySpan<float> m = JxlDctTables.Multipliers8;
        Dct4(m0 + m7, m1 + m6, m2 + m5, m3 + m4, out data[offset], out data[offset + 2 * stride], out data[offset + 4 * stride], out data[offset + 6 * stride]);
        Dct4((m0 - m7) * m[0], (m1 - m6) * m[1], (m2 - m5) * m[2], (m3 - m4) * m[3], out float u0, out float u1, out float u2, out float u3);
        data[offset + stride] = JxlFastMath.Fma(u0, Sqrt2, u1);
        data[offset + 3 * stride] = u1 + u2;
        data[offset + 5 * stride] = u2 + u3;
        data[offset + 7 * stride] = u3;
    }

    private static void Dct4(float m0, float m1, float m2, float m3, out float out0, out float out1, out float out2, out float out3)
    {
        float t0 = m0 + m3;
        float t1 = m1 + m2;
        ReadOnlySpan<float> m = JxlDctTables.Multipliers4;
        float u0 = (m0 - m3) * m[0];
        float u1 = (m1 - m2) * m[1];
        float v0 = u0 + u1;
        float v1 = u0 - u1;
        out0 = t0 + t1;
        out1 = JxlFastMath.Fma(v0, Sqrt2, v1);
        out2 = t0 - t1;
        out3 = v1;
    }

    private static void Identity(ReadOnlySpan<float> coefficients, Span<float> pixels, int stride)
    {
        Span<float> dcs = stackalloc float[4];
        QuarterDcs(coefficients, dcs);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                float residualSum = 0;
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        if (ix != 0 || iy != 0)
                        {
                            residualSum += coefficients[(y + iy * 2) * 8 + x + ix * 2];
                        }
                    }
                }

                int center = (4 * y + 1) * stride + 4 * x + 1;
                pixels[center] = dcs[y * 2 + x] - residualSum * (1.0f / 16);
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        if (ix != 1 || iy != 1)
                        {
                            pixels[(y * 4 + iy) * stride + x * 4 + ix] = coefficients[(y + iy * 2) * 8 + x + ix * 2] + pixels[center];
                        }
                    }
                }

                pixels[y * 4 * stride + x * 4] = coefficients[(y + 2) * 8 + x + 2] + pixels[center];
            }
        }
    }

    // DCT8X4 splits the block into two 8x4 halves side by side, DCT4X8 into two 4x8 halves stacked.
    private static void Dct4X8(bool sideBySide, ReadOnlySpan<float> coefficients, Span<float> pixels, int stride, Span<float> scratch)
    {
        float block0 = coefficients[0];
        float block1 = coefficients[8];
        Span<float> dcs = [block0 + block1, block0 - block1];
        Span<float> block = stackalloc float[32];
        for (int half = 0; half < 2; half++)
        {
            Gather(coefficients, half * 8, 16, 1, 4, 8, dcs[half], block);

            if (sideBySide)
            {
                ScaledIdct(8, 4, block, pixels[(half * 4)..], stride, scratch);
            }
            else
            {
                ScaledIdct(4, 8, block, pixels[(half * 4 * stride)..], stride, scratch);
            }
        }
    }

    private static void Dct4X4(ReadOnlySpan<float> coefficients, Span<float> pixels, int stride, Span<float> scratch)
    {
        Span<float> dcs = stackalloc float[4];
        QuarterDcs(coefficients, dcs);
        Span<float> block = stackalloc float[16];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                Gather(coefficients, y * 8 + x, 16, 2, 4, 4, dcs[y * 2 + x], block);

                ScaledIdct(4, 4, block, pixels[(y * 4 * stride + x * 4)..], stride, scratch);
            }
        }
    }

    private static void Dct2X2(ReadOnlySpan<float> coefficients, Span<float> pixels, int stride)
    {
        if (Dct2X2Sse41(coefficients, pixels, stride)) return;
        Span<float> block = stackalloc float[64];
        coefficients[..64].CopyTo(block);
        Idct2TopBlock(2, block);
        Idct2TopBlock(4, block);
        Idct2TopBlock(8, block);
        for (int y = 0; y < 8; y++)
        {
            block.Slice(y * 8, 8).CopyTo(pixels[(y * stride)..]);
        }
    }

    // Undoes one level of the 2x2 Haar pyramid in the top-left size×size corner.
    private static void Idct2TopBlock(int size, Span<float> block)
    {
        Span<float> temp = stackalloc float[64];
        int count = size / 2;
        for (int y = 0; y < count; y++)
        {
            for (int x = 0; x < count; x++)
            {
                float c00 = block[y * 8 + x];
                float c01 = block[y * 8 + count + x];
                float c10 = block[(y + count) * 8 + x];
                float c11 = block[(y + count) * 8 + count + x];
                temp[y * 16 + x * 2] = c00 + c01 + c10 + c11;
                temp[y * 16 + x * 2 + 1] = c00 + c01 - c10 - c11;
                temp[(y * 2 + 1) * 8 + x * 2] = c00 - c01 + c10 - c11;
                temp[(y * 2 + 1) * 8 + x * 2 + 1] = c00 - c01 - c10 + c11;
            }
        }

        for (int y = 0; y < size; y++)
        {
            temp.Slice(y * 8, size).CopyTo(block[(y * 8)..]);
        }
    }

    // AFV: a 4x4 corner (afvKind: 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right) with its own basis, a 4x4
    // DCT beside it and a 4x8 DCT in the other half.
    private static void Afv(int afvKind, ReadOnlySpan<float> coefficients, Span<float> pixels, int stride, Span<float> scratch)
    {
        int afvX = afvKind & 1;
        int afvY = afvKind / 2;
        float block00 = coefficients[0];
        float block01 = coefficients[1];
        float block10 = coefficients[8];
        float dc0 = (block00 + block10 + block01) * 4.0f;
        float dc1 = block00 + block10 - block01;
        float dc2 = block00 - block10;
        Span<float> coeff = stackalloc float[16];
        Gather(coefficients, 0, 16, 2, 4, 4, dc0, coeff);

        Span<float> block = stackalloc float[32];
        ReadOnlySpan<float> basis = JxlDctTables.AfvBasis;
        for (int i = AfvBasisAvx2(coeff, basis, block); i < 16; i++)
        {
            float pixel = 0;
            for (int j = 0; j < 16; j++)
            {
                pixel = JxlFastMath.Fma(coeff[j], basis[j * 16 + i], pixel);
            }

            block[i] = pixel;
        }

        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                pixels[(iy + afvY * 4) * stride + afvX * 4 + ix] = block[(afvY == 1 ? 3 - iy : iy) * 4 + (afvX == 1 ? 3 - ix : ix)];
            }
        }

        Gather(coefficients, 1, 16, 2, 4, 4, dc1, block);

        ScaledIdct(4, 4, block, pixels[(afvY * 4 * stride + (afvX == 1 ? 0 : 4))..], stride, scratch);
        Gather(coefficients, 8, 16, 1, 4, 8, dc2, block);

        ScaledIdct(4, 8, block, pixels[((afvY == 1 ? 0 : 4) * stride)..], stride, scratch);
    }

    // The DCs of the four 4×4 quarters from the top-left 2×2 coefficients.
    private static void QuarterDcs(ReadOnlySpan<float> coefficients, Span<float> dcs)
    {
        float block00 = coefficients[0];
        float block01 = coefficients[1];
        float block10 = coefficients[8];
        float block11 = coefficients[9];
        dcs[0] = block00 + block01 + block10 + block11;
        dcs[1] = block00 + block01 - block10 - block11;
        dcs[2] = block00 - block01 + block10 - block11;
        dcs[3] = block00 - block01 - block10 + block11;
    }

    // A rows×columns sub-block: the DC first, then the interleaved coefficients at start + iy·rowStep + ix·columnStep.
    private static void Gather(ReadOnlySpan<float> coefficients, int start, int rowStep, int columnStep, int rows, int columns, float dc,
        Span<float> block)
    {
        if (rowStep == 16 && rows == 4 && GatherAvx2(coefficients, start, columnStep, columns, dc, block)) return;
        block[0] = dc;
        for (int iy = 0; iy < rows; iy++)
        {
            for (int ix = 0; ix < columns; ix++)
            {
                if (ix != 0 || iy != 0)
                {
                    block[iy * columns + ix] = coefficients[start + iy * rowStep + ix * columnStep];
                }
            }
        }
    }
}
