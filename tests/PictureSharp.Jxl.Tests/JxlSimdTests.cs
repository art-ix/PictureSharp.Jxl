using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Formats.Jxl.Integration;
using PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
using PictureSharp.Formats.Jxl.Codestream.Rendering;
using PictureSharp.Formats.Jxl.Codestream.VarDct;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>Differential tests against the frozen 0.7.10 formulas, also run with runtime ISA masks.</summary>
public sealed class JxlSimdTests
{
    internal static readonly int[] Widths = [1, 2, 3, 4, 7, 8, 9, 15, 16, 17, 31, 32, 33];

    [Test]
    public async Task Capabilities_ReportActualDispatch()
    {
        Console.WriteLine($"JXL SIMD: AVX2={Avx2.IsSupported}; SSE4.1={Sse41.IsSupported}; FMA={Fma.IsSupported}; DCT AVX2/FMA={JxlDct.CanTransformAvx2}");
        await Assert.That(JxlDct.CanTransformAvx2).IsEqualTo(Avx2.IsSupported && Fma.IsSupported);
        string actual = $"{Avx2.IsSupported},{Sse41.IsSupported},{Fma.IsSupported}";
        if (Environment.GetEnvironmentVariable("JXL_TEST_CAPABILITIES") is { } expected)
            await Assert.That(actual).IsEqualTo(expected);
        await Assert.That(JxlDct.SelectFusedAvx2(true, false)).IsFalse();
        await Assert.That(JxlDct.SelectFusedAvx2(false, true)).IsFalse();
        await Assert.That(JxlDct.SelectFusedAvx2(false, false)).IsFalse();
        await Assert.That(JxlDct.SelectFusedAvx2(true, true)).IsTrue();
    }

    [Test]
    public void DctColumns_ExactAcrossSizesTailsAndOffsets()
    {
        foreach (int n in new[] { 1, 2, 4, 8, 16, 32, 64, 128, 256 })
        foreach (int width in Widths)
        foreach (bool inverse in new[] { false, true })
        {
            float[] expected = Samples(n * width + 6), actual = (float[])expected.Clone();
            float[] scratch = new float[JxlDct.ScratchSize(n * width)];
            if (inverse)
            {
                JxlDctScalar.IdctColumns(n, width, expected.AsSpan(3, n * width), scratch);
                JxlDct.IdctColumns(n, width, actual.AsSpan(3, n * width), scratch);
            }
            else
            {
                JxlDctScalar.DctColumns(n, width, expected.AsSpan(3, n * width), scratch);
                JxlDct.DctColumns(n, width, actual.AsSpan(3, n * width), scratch);
            }
            Exact(expected, actual, $"columns n={n} width={width} inverse={inverse}");
        }
    }

    [Test]
    public void Dct1D_ExactWithStridesTailsAndSentinels()
    {
        foreach (int n in new[] { 2, 4, 8, 16, 32, 64, 128, 256 })
        foreach (int width in Widths)
        foreach (bool inverse in new[] { false, true })
        {
            int stride = width + 5;
            float[] source = Samples(n * stride + 6), scratch = new float[JxlDct.ScratchSize(n * width)];
            float[] expected = new float[n * stride + 6], actual = new float[n * stride + 6];
            if (inverse)
            {
                float[] from = source[..(n * width)], copy = (float[])from.Clone();
                Array.Fill(expected, 7.0f); Array.Fill(actual, 7.0f);
                JxlDctScalar.Idct1D(n, width, from, width, expected.AsSpan(3), stride, scratch);
                JxlDct.Idct1D(n, width, copy, width, actual.AsSpan(3), stride, scratch);
            }
            else
            {
                expected = new float[n * width + 6]; actual = new float[n * width + 6];
                Array.Fill(expected, 7.0f); Array.Fill(actual, 7.0f);
                JxlDctScalar.Dct1D(n, width, source.AsSpan(3), stride, expected.AsSpan(3), width, scratch);
                JxlDct.Dct1D(n, width, source.AsSpan(3), stride, actual.AsSpan(3), width, scratch);
            }
            Exact(expected, actual, $"1D n={n} width={width} inverse={inverse}");
        }
    }

    [Test]
    public void Symmetric5_ExactAcrossWidthsAndBorders()
    {
        var weights = new JxlSymmetric5Weights(0.31f, -0.07f, 0.013f, -0.029f, 0.0021f, 0.0047f);
        foreach (int width in Widths.Concat([5, 6, 10, 11, 12, 13, 18, 19, 20, 21, 64]))
        foreach (int height in new[] { 1, 2, 3, 5, 9 })
        {
            float[] input = Samples(width * height);
            Exact(JxlConvolutionScalar.Symmetric5(input, width, height, weights, default),
                JxlConvolution.Symmetric5(input, width, height, weights, default), $"symmetric5 {width}x{height}");
        }
    }

    [Test]
    public void EpfSharpnessBlockErrors_ExactAcrossBlockGroupsAndEdges()
    {
        foreach (int width in new[] { 8, 57, 64, 65, 127, 128, 200 })
        foreach (int height in new[] { 1, 8, 13 })
        {
            int xBlocks = (width + 7) / 8, yBlocks = (height + 7) / 8;
            var original = new JxlImage3F(xBlocks * 8, yBlocks * 8);
            for (int c = 0; c < 3; c++) Samples(original.Planes[c].Length + c).AsSpan(c, original.Planes[c].Length).CopyTo(original.Planes[c]);
            float[] mask = [.. Samples(xBlocks * 8 * yBlocks * 8 + 5).Skip(5).Select(v => v * 3)];
            JxlPlane[] decoded = Planes(width, height);
            Exact(JxlBlockErrorsScalar.BlockErrors(xBlocks, yBlocks, original, decoded, mask),
                JxlEpfSharpnessSearch.BlockErrors(xBlocks, yBlocks, original, decoded, mask), $"block errors {width}x{height}");
        }
    }

    [Test]
    public void Dct_AllStrategiesExactWithOddStride()
    {
        foreach (JxlAcStrategyType strategy in Enum.GetValues<JxlAcStrategyType>())
        {
            int width = strategy.BlocksX * 8, height = strategy.BlocksY * 8, size = width * height;
            int stride = width + 3;
            float[] pixels = Samples(stride * height + 6), coefficients = new float[size];
            float[] expected = new float[size], scratch = new float[JxlDct.ScratchSize(size)];
            JxlDctScalar.TransformFromPixels(strategy, pixels.AsSpan(3), stride, expected, scratch);
            JxlDct.TransformFromPixels(strategy, pixels.AsSpan(3), stride, coefficients, scratch);
            Exact(expected, coefficients, $"forward {strategy}");
            float[] a = Samples(stride * height + 6), b = (float[])a.Clone();
            JxlDctScalar.TransformToPixels(strategy, expected, a.AsSpan(3), stride, scratch);
            JxlDct.TransformToPixels(strategy, coefficients, b.AsSpan(3), stride, scratch);
            Exact(a, b, $"inverse {strategy}");
        }
    }

    [Test]
    public void Color_ExactAcrossSignsThresholdsAndTails()
    {
        foreach (int width in Widths)
        foreach (JxlColorTransform transform in new[] { JxlColorTransform.Xyb, JxlColorTransform.YCbCr })
        foreach (bool gray in new[] { false, true })
        foreach (JxlTransferFunction transfer in new[] { JxlTransferFunction.Srgb, JxlTransferFunction.Linear, JxlTransferFunction.Bt709 })
        foreach (JxlPrimaries primaries in new[] { JxlPrimaries.Srgb, JxlPrimaries.P3 })
        {
            var metadata = JxlImageMetadata.Default with { Color = JxlColorEncoding.Srgb with
                { ColorSpace = gray ? JxlColorSpace.Gray : JxlColorSpace.Rgb, TransferFunction = transfer, Primaries = primaries } };
            JxlPlane[] a = Planes(width, 3), b = [.. a.Select(p => p.Clone())];
            new JxlColorOutputScalar(metadata, JxlCustomTransformData.Default, transform).Apply(a);
            new JxlColorOutput(metadata, JxlCustomTransformData.Default, transform).Apply(b);
            for (int c = 0; c < 3; c++)
            for (int y = 0; y < 3; y++) Exact(a[c].Row(y), b[c].Row(y), $"colour {transform} {transfer} {primaries} gray={gray} width={width}");
        }
    }

    [Test]
    public void Filters_ExactAtMirroredEdgesSigmaBoundariesAndAllStages()
    {
        foreach (int width in Widths)
        foreach (int height in new[] { 1, 2, 7, 8, 9, 17 })
        foreach (int stage in new[] { -1, 0, 1, 2 })
        {
            JxlPlane[] a = Planes(width, height), b = [.. a.Select(p => p.Clone())];
            var filter = JxlLoopFilter.Default;
            int xBlocks = (width + 7) / 8;
            float[] sigma = [.. Enumerable.Range(0, xBlocks * ((height + 7) / 8)).Select(i => i % 3 == 0 ? -4.0f : i % 3 == 1 ? -0.03f : -3.9f)];
            switch (stage)
            {
                case -1: JxlRenderPipelineScalar.Gaborish(a, filter); JxlRenderPipeline.Gaborish(b, filter); break;
                case 0: a = JxlRenderPipelineScalar.Epf0(a, filter, sigma, xBlocks); b = JxlRenderPipeline.Epf0(b, filter, sigma, xBlocks); break;
                case 1: a = JxlRenderPipelineScalar.Epf1(a, filter, sigma, xBlocks); b = JxlRenderPipeline.Epf1(b, filter, sigma, xBlocks); break;
                case 2: a = JxlRenderPipelineScalar.Epf2(a, filter, sigma, xBlocks); b = JxlRenderPipeline.Epf2(b, filter, sigma, xBlocks); break;
            }
            for (int c = 0; c < 3; c++)
            for (int y = 0; y < height; y++) Exact(a[c].Row(y), b[c].Row(y), $"filter {stage} {width}x{height} c={c} y={y}");
        }
    }

    [Test]
    public void Pixels_AllBackendsExactWithAlphaGrayWrapAndSentinels()
    {
        foreach (int width in Widths.Append(65536))
        foreach (bool gray in new[] { false, true })
        foreach (bool alpha in new[] { false, true })
        foreach (int backend in new[] { 0, 1, 2 })
        {
            if (backend == 1 && !Sse41.IsSupported || backend == 2 && !Avx2.IsSupported) continue;
            float[] r = Samples(width + 6), g = Samples(width + 7), b = Samples(width + 8), a = Samples(width + 9);
            if (width == 65536)
                for (int i = 0; i < width; i++) { r[i + 3] = i * (1.0f / 65535); g[i + 3] = (i + 0.5f) / 65535; b[i + 3] = (i - 0.5f) / 65535; a[i + 3] = (65535 - i) * (1.0f / 65535); }
            else
            {
                float[] special = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0.0f, 0, 1, float.Epsilon];
                special.AsSpan(0, Math.Min(width, special.Length)).CopyTo(r.AsSpan(3));
            }
            Rgba64 sentinel = new(113, 227, 331, 449);
            Rgba64[] expected = Enumerable.Repeat(sentinel, width + 6).ToArray(), actual = (Rgba64[])expected.Clone();
            ReadOnlySpan<float> rr = r.AsSpan(3, width), gg = g.AsSpan(3, width), bb = b.AsSpan(3, width), aa = alpha ? a.AsSpan(3, width) : default;
            JxlPixelWriterScalar.WriteWideRow(rr, gg, bb, aa, expected.AsSpan(3, width), gray);
            int done = backend switch
            {
                1 => JxlPixelWriter.WriteWideRowSse41(rr, gg, bb, aa, actual.AsSpan(3, width), gray),
                2 => JxlPixelWriter.WriteWideRowAvx2(rr, gg, bb, aa, actual.AsSpan(3, width), gray),
                _ => WriteWide(rr, gg, bb, aa, actual.AsSpan(3, width), gray)
            };
            PixelsExact(expected.AsSpan(3, done), actual.AsSpan(3, done), $"wide backend={backend} width={width} gray={gray} alpha={alpha}");
            if (actual.AsSpan(0, 3).ContainsAnyExcept(sentinel) || actual.AsSpan(3 + done).ContainsAnyExcept(sentinel))
                throw new InvalidOperationException("Wide output wrote outside its processed range.");
            foreach (int y in new[] { 0, 1, 13, 31, 32, 33 })
            {
                Rgba32 s32 = new(11, 22, 33, 44);
                Rgba32[] e = Enumerable.Repeat(s32, width + 6).ToArray(), v = (Rgba32[])e.Clone();
                if (gray) JxlPixelWriterScalar.WriteGrayRow(rr, aa, e.AsSpan(3, width), y);
                else JxlPixelWriterScalar.WriteColorRow(rr, gg, bb, aa, e.AsSpan(3, width), y);
                int count = backend switch
                {
                    1 => JxlPixelWriter.WriteNarrowRowSse41(rr, gg, bb, aa, v.AsSpan(3, width), gray, y),
                    2 => JxlPixelWriter.WriteNarrowRowAvx2(rr, gg, bb, aa, v.AsSpan(3, width), gray, y),
                    _ => WriteNarrow(rr, gg, bb, aa, v.AsSpan(3, width), gray, y)
                };
                PixelsExact(e.AsSpan(3, count), v.AsSpan(3, count), $"narrow backend={backend} width={width} gray={gray} alpha={alpha} y={y}");
                if (v.AsSpan(0, 3).ContainsAnyExcept(s32) || v.AsSpan(3 + count).ContainsAnyExcept(s32))
                    throw new InvalidOperationException("Narrow output wrote outside its processed range.");
            }
        }
    }

    private static int WriteWide(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b,
        ReadOnlySpan<float> a, Span<Rgba64> output, bool gray)
    { JxlPixelWriter.WriteWideRow(r, g, b, a, output, gray); return output.Length; }

    private static int WriteNarrow(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b,
        ReadOnlySpan<float> a, Span<Rgba32> output, bool gray, int y)
    {
        if (gray) JxlPixelWriter.WriteGrayRow(r, a, output, y);
        else JxlPixelWriter.WriteColorRow(r, g, b, a, output, y);
        return output.Length;
    }

    private static void PixelsExact<T>(Span<T> expected, Span<T> actual, string context) where T : struct
    {
        if (!MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)))
            throw new InvalidOperationException(context);
    }

    [Test]
    public void QuantBias_ExactForZerosSignsAndIntegerExtremes()
    {
        if (!JxlDct.CanTransformAvx2) { Console.WriteLine("Quant bias AVX2/FMA unavailable: direct backend not executed."); return; }
        int[] integers = [0, 1, -1, 2, -2, 127, -128, 65535, -65535, int.MinValue, int.MaxValue, 16777215, 16777217];
        foreach (float bias in new[] { 0.0f, 0.9375f, -0.75f })
        for (int start = 0; start < integers.Length; start++)
        {
            float[] values = [.. Enumerable.Range(0, 8).Select(i => (float)integers[(start + i) % integers.Length])];
            float[] expected = [.. values.Select(q => Math.Abs(q) < 1.125f ? Math.Abs(q) > 0 ? MathF.CopySign(bias, q) : 0 : MathF.FusedMultiplyAdd(-0.145f, 1.0f / q, q))];
            float[] actual = new float[8];
            JxlVarDctFrame.AdjustQuantBiasAvx2(Vector256.Create(values), bias, 0.145f).CopyTo(actual);
            Exact(expected, actual, "quant bias");
        }
    }

    [Test]
    public async Task EncoderDequantizeY_ExactBiasesAndChromaFromLuma()
    {
        float[] biases = [.. JxlCustomTransformData.Default.EffectiveQuantBiases];
        // Frozen 0.7.10 AdjustQuantBias (JxlCoefficientQuantizer, 6cd7f9b).
        float Bias(int q)
        {
            float value = q;
            float magnitude = Math.Abs(value);
            if (magnitude < 1.125f) return magnitude > 0 ? MathF.CopySign(biases[1], value) : 0;
            return MathF.FusedMultiplyAdd(-biases[3], 1.0f / value, value);
        }

        foreach (int size in new[] { 64, 128, 256, 1024 })
        {
            var random = new Random(size);
            int[] quantized = [.. Enumerable.Range(0, size).Select(i => i % 5 == 0 ? 0 : i % 7 == 0 ? random.Next(-2, 3) : random.Next(-3000, 3000))];
            quantized[1] = int.MaxValue / 2;
            quantized[2] = -int.MaxValue / 2;
            float[] dequant = Samples(size + 1)[1..], block = Samples(3 * size + 2)[2..], expected = (float[])block.Clone();
            const float inverseQuant = 0.0371f, xFactor = 0.083f, bFactor = -0.91f;
            for (int k = 0; k < size; k++) expected[size + k] = Bias(quantized[k]) * dequant[k] * inverseQuant;
            for (int k = 0; k < size; k++)
            {
                float y = expected[size + k];
                expected[k] = MathF.FusedMultiplyAdd(-xFactor, y, expected[k]);
                expected[2 * size + k] = MathF.FusedMultiplyAdd(-bFactor, y, expected[2 * size + k]);
            }

            int done = JxlCoefficientQuantizer.DequantizeYAndSubtractAvx2(quantized, dequant, block, size, inverseQuant, xFactor, bFactor, biases[1], biases[3]);
            await Assert.That(done).IsEqualTo(Avx2.IsSupported && Fma.IsSupported ? size : 0);
            if (done == size) Exact(expected, block, $"dequantize Y size={size}");
        }
    }

    [Test]
    public void EncoderQuantization_ExactAtThresholdsTiesAndOverflow()
    {
        foreach (int width in Widths.Concat(new[] { 64, 128, 256 }))
        foreach (int backend in new[] { 0, 1, 2 })
        {
            if (backend == 1 && !Sse41.IsSupported || backend == 2 && !Avx2.IsSupported) continue;
            float[] weights = Samples(width + 6), input = Samples(width + 6);
            float[] values = [-0.0f, 0, 0.5f, -0.5f, 1.5f, -1.5f, 0.58f, 0.62f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 2147483648.0f, -2147483648.0f];
            for (int i = 0; i < Math.Min(width, values.Length); i++) { weights[3 + i] = 1; input[3 + i] = values[i]; }
            int[] actual = Enumerable.Repeat(12345, width + 6).ToArray();
            ReadOnlySpan<float> w = weights.AsSpan(3, width), v = input.AsSpan(3, width);
            int done = backend switch
            {
                1 => JxlCoefficientQuantizer.QuantizeRowSse41(w, v, actual.AsSpan(3, width), 1, 0.58f, 0.62f),
                2 => JxlCoefficientQuantizer.QuantizeRowAvx2(w, v, actual.AsSpan(3, width), 1, 0.58f, 0.62f),
                _ => JxlCoefficientQuantizer.QuantizeRowSimd(w, v, actual.AsSpan(3, width), 1, 0.58f, 0.62f)
            };
            for (int i = 0; i < done; i++)
            {
                float value = w[i] * 1.0f * v[i];
                int expected = Math.Abs(value) >= (i >= width / 2 ? 0.62f : 0.58f) ? (int)MathF.Round(value) : 0;
                if (actual[3 + i] != expected) throw new InvalidOperationException($"Quantization backend={backend} width={width} index={i}: {actual[3 + i]} != {expected}");
            }
            if (actual.AsSpan(0, 3).ContainsAnyExcept(12345) || actual.AsSpan(3 + done).ContainsAnyExcept(12345))
                throw new InvalidOperationException("Quantization wrote outside its processed range.");
        }
    }

    [Test]
    public void CflDerivatives_PreserveOrderedAccumulationAndSkipThreshold()
    {
        if (!JxlDct.CanTransformAvx2) { Console.WriteLine("CfL AVX2/FMA unavailable: direct backend not executed."); return; }
        var scalar = new JxlChromaFromLumaSearchScalar(new JxlImage3F(8, 8), new JxlQuantWeights());
        foreach (int count in Widths.Append(4096).Where(n => n >= 8))
        foreach (float x in new[] { -100.0f, -1.0f, -0.0f, 1.0f, 100.0f })
        {
            // Compare a complete vector prefix with its scalar accumulation, independent of the scalar tail.
            int n = count & ~7;
            float[] slopes = Samples(n), offsets = Samples(n + 3);
            offsets[3] = 100; slopes[0] = 0; offsets[4] = -100; slopes[1] = 0;
            offsets[5] = MathF.BitDecrement(100); slopes[2] = 0;
            scalar.SetCoefficients(slopes, offsets.AsSpan(3, n));
            float e = scalar.Derivatives(n, x, 100, out float ep, out float em);
            float sum = 0, plus = 0, minus = 0;
            int done = JxlChromaFromLumaSearch.DerivativesAvx2(slopes, offsets.AsSpan(3, n), x, x + 100, x - 100, ref sum, ref plus, ref minus);
            if (done != n) throw new InvalidOperationException("CfL backend did not execute.");
            Exact(new[] { e, ep, em }, new[] { 2 * 1e-9f * n * x + sum, 2 * 1e-9f * n * (x + 100) + plus,
                2 * 1e-9f * n * (x - 100) + minus }, "CfL ordered derivatives");
        }
    }

    [Test]
    public void CflDerivatives_ExactForEverySkipMaskAndNonzeroInitialSums()
    {
        if (!JxlDct.CanTransformAvx2) return;
        for (int mask = 0; mask < 256; mask++)
        {
            float[] slopes = Samples(8), offsets = new float[8];
            for (int i = 0; i < 8; i++) offsets[i] = (mask & (1 << i)) != 0 ? 200 : (i - 4) * 0.125f;
            float sum = 0.125f, plus = -0.75f, minus = 123.5f;
            float e = sum, ep = plus, em = minus;
            for (int i = 0; i < 8; i++)
            {
                float v = MathF.FusedMultiplyAdd(slopes[i], 1, offsets[i]);
                if (Math.Abs(v) >= 100) continue;
                float vp = MathF.FusedMultiplyAdd(slopes[i], 2, offsets[i]);
                float vm = MathF.FusedMultiplyAdd(slopes[i], 0, offsets[i]);
                float scale = (2.0f / 3) * slopes[i];
                float d = scale * (Math.Abs(v) + 1), dp = scale * (Math.Abs(vp) + 1), dm = scale * (Math.Abs(vm) + 1);
                e += v < 0 ? -d : d;
                ep += vp < 0 ? -dp : dp;
                em += vm < 0 ? -dm : dm;
            }
            JxlChromaFromLumaSearch.DerivativesAvx2(slopes, offsets, 1, 2, 0, ref sum, ref plus, ref minus);
            Exact(new[] { e, ep, em }, new[] { sum, plus, minus }, $"CfL skip mask {mask}");
        }
    }

    [Test]
    public async Task Cbrt_VectorLanesMatchScalarOrDeferToIt()
    {
        if (!Avx2.IsSupported) { Console.WriteLine("Cbrt AVX2 unavailable: direct backend not executed."); return; }
        long normal = 0, deferred = 0;
        uint[] lanes = new uint[4];
        void Check(uint a, uint b, uint c, uint d)
        {
            (lanes[0], lanes[1], lanes[2], lanes[3]) = (a, b, c, d);
            Vector128<float> result = JxlCbrt.CbrtAvx2(Vector128.Create(a, b, c, d).AsSingle(), out int scalar);
            for (int k = 0; k < 4; k++)
            {
                float x = BitConverter.UInt32BitsToSingle(lanes[k]);
                bool positiveNormal = float.IsNormal(x) && x > 0;
                normal += positiveNormal ? 1 : 0;
                if ((scalar >> k & 1) != 0) { deferred += positiveNormal ? 1 : 0; continue; }
                if (BitConverter.SingleToUInt32Bits(result.GetElement(k)) != BitConverter.SingleToUInt32Bits(JxlCbrt.Cbrt(x)))
                    throw new InvalidOperationException($"cbrt({x:R}) = {result.GetElement(k):R}, scalar {JxlCbrt.Cbrt(x):R}");
            }
        }

        uint[] specials = [0, 0x80000000, 1, 0x007FFFFF, 0x00800000, 0x00800001, 0x3F800000, 0x3F800001, 0x7F7FFFFF, 0x7F800000,
            0x7FC00000, 0xFF800000, 0xBF800000, 0x3B788F5D, 0x41000000, 0x4B000000];
        for (int i = 0; i < specials.Length; i += 4) Check(specials[i], specials[i + 1], specials[i + 2], specials[i + 3]);
        for (ulong bits = 0; bits + 3 * 61 <= uint.MaxValue; bits += 4 * 61)
            Check((uint)bits, (uint)(bits + 61), (uint)(bits + 122), (uint)(bits + 183));
        // Perfect cubes and their neighbours, the exact cases of the scalar correction.
        for (int i = 1; i < 1 << 16; i++)
        {
            uint cube = BitConverter.SingleToUInt32Bits((float)i * i * i);
            Check(cube - 1, cube, cube + 1, BitConverter.SingleToUInt32Bits(i / 8.0f * (i / 8.0f) * (i / 8.0f)));
        }

        // Only lanes near a rounding boundary need the scalar correction: a few percent of positive normal inputs.
        Console.WriteLine($"Cbrt AVX2: {deferred} of {normal} positive normal lanes deferred to scalar.");
        await Assert.That(deferred).IsLessThan(normal * 5 / 100);
    }

    [Test]
    public void XybInput_ExactAcrossEncodingsDepthsAndTails()
    {
        JxlColorEncoding[] encodings =
        [
            JxlColorEncoding.Srgb,
            JxlColorEncoding.Srgb with { TransferFunction = JxlTransferFunction.Linear },
            JxlColorEncoding.Srgb with { Primaries = JxlPrimaries.P3 },
            JxlColorEncoding.Srgb with { TransferFunction = JxlTransferFunction.Pq, Primaries = JxlPrimaries.Bt2100 },
            JxlColorEncoding.Srgb with { ColorSpace = JxlColorSpace.Gray },
        ];
        var random = new Random(1029);
        foreach (JxlColorEncoding color in encodings)
        foreach (int depth in new[] { 8, 16 })
        foreach (int width in Widths.Append(1000))
        {
            float intensity = color.TransferFunction == JxlTransferFunction.Pq ? 10000 : 255;
            int max = (1 << depth) - 1;
            float[][] input = [.. Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, width)
                .Select(i => (float)(i % 13 == 0 ? (i % 2 == 0 ? 0 : max) : random.Next(max + 1))).ToArray())];
            float[][] expected = [.. input.Select(a => a.ToArray())], actual = [.. input.Select(a => a.ToArray())];
            JxlXybInputScalar.TryCreate(color, null, intensity, depth)!.Convert(expected[0], expected[1], expected[2]);
            JxlXybInput.TryCreate(color, null, intensity, depth)!.Convert(actual[0], actual[1], actual[2]);
            for (int c = 0; c < 3; c++) Exact(expected[c], actual[c], $"XYB input {color.TransferFunction} {color.Primaries} {color.ColorSpace} depth={depth} width={width}");
        }
    }

    internal static JxlPlane[] Planes(int width, int height)
    {
        JxlPlane[] result = [new(width, height), new(width, height), new(width, height)];
        for (int c = 0; c < 3; c++)
        {
            float[] samples = Samples(width * height + c);
            for (int y = 0; y < height; y++) samples.AsSpan(c + y * width, width).CopyTo(result[c].Row(y));
        }
        return result;
    }

    internal static float[] Samples(int count)
    {
        var random = new Random(2908);
        return [.. Enumerable.Range(0, count).Select(i => i % 17 == 0 ? -0.0f : (float)(random.NextDouble() * 4 - 2))];
    }

    internal static void Exact(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, string context)
    {
        if (!MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)))
        {
            int i = 0;
            while (i < expected.Length && BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.SingleToInt32Bits(actual[i])) i++;
            throw new InvalidOperationException($"{context}: sample {i}: {expected[i]:R} != {actual[i]:R}");
        }
    }
}
