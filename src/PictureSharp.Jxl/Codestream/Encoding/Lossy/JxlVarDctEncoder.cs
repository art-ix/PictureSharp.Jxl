using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
using PictureSharp.Formats.Jxl.Codestream.VarDct;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// Encodes an XYB image as one VarDCT frame, following libjxl v0.12.0's lossy pipeline (lib/jxl/enc_frame.cc,
/// enc_heuristics.cc, enc_cache.cc, enc_modular.cc; BSD-3-Clause): adaptive quantization, inverse Gaborish,
/// chroma from luma, the transform search, quantization, then the DC and control fields as Modular streams and the AC
/// coefficients with their orders and histograms. An alpha channel is stored losslessly in Modular streams.
/// </summary>
internal static class JxlVarDctEncoder
{
    /// <summary>
    /// Writes the frame of a <paramref name="width"/>×<paramref name="height"/> image: <paramref name="xyb"/> holds
    /// its XYB pixels (at least that size), <paramref name="alpha"/> the 8-bit alpha samples when the metadata has an
    /// alpha channel. The writer must be at a byte boundary and stays at one.
    /// </summary>
    public static void WriteFrame(JxlBitWriter writer, JxlImage3F xyb, int width, int height, int[]? alpha, JxlImageMetadata metadata,
        JxlLossySettings settings, CancellationToken cancellationToken)
    {
        var header = new JxlFrameHeader
        {
            Encoding = JxlFrameEncoding.VarDct,
            ColorTransform = JxlColorTransform.Xyb,
            Width = (uint)width,
            Height = (uint)height,
            Upsampling = (uint)settings.Resampling,
            ExtraChannelUpsampling = alpha is null ? [] : [(uint)settings.Resampling],
            LoopFilter = new JxlLoopFilter { Gaborish = settings.Gaborish, EpfIterations = (uint)settings.EpfIterations },
        };
        if (header.TocEntries > JxlToc.MaxEntries) throw JxlDescriptor.Unsupported("the image has too many groups to encode.");
        int frameWidth = (int)header.XSize;
        int frameHeight = (int)header.YSize;
        if (alpha is not null) SimplifyInvisible(xyb, width, height, alpha);
        JxlImage3F image = xyb.PaddedToBlocks(width, height);
        xyb.Release();
        (uint xQmScale, uint bQmScale) = ChromacityAdjustments(image, settings);
        if (settings.Resampling == 2) image = JxlDownsampling.Sharper(image, cancellationToken).PaddedToBlocks(frameWidth, frameHeight);
        JxlImage3F? original = settings.EpfSharpnessSearch ? image.Clone() : null;
        JxlPatchEncoder? patches = settings.Patches ? JxlPatchEncoder.Create(image, frameWidth, frameHeight, settings.Distance, cancellationToken) : null;
        if (patches is not null)
        {
            patches.WriteReferenceFrame(writer, metadata, cancellationToken);
            patches.SubtractFrom(image);
        }

        var frame = new JxlLossyFrame(header with
        {
            XQmScale = xQmScale, BQmScale = bQmScale, Flags = patches is null ? JxlFrameFlags.None : JxlFrameFlags.Patches,
        }, settings, xyb.Buffers);

        JxlAqMaps maps = ChooseParameters(frame, image, cancellationToken);
        // Each group writes only its own coefficients, quantization and DC; quantizers hold per-worker scratch.
        JxlParallel.ForWorkers((int)header.Groups, settings.Parallelism, cancellationToken, () => new JxlCoefficientQuantizer(frame, image),
            (quantizer, group) => quantizer.ComputeGroup(group));
        // The coefficients replace the image (0.8.6): its pooled planes go back before the sharpness search, whose
        // reconstruction rents planes of the same size.
        image.Release();

        using var streams = new JxlModularStreamSet();
        float[][] dequantizedDc = [new float[frame.Dc[0].Length], new float[frame.Dc[0].Length], new float[frame.Dc[0].Length]];
        for (int group = 0; group < header.DcGroups; group++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<JxlChannel> dc = QuantizeDc(frame, group);
            DequantizeDc(frame, group, dc, dequantizedDc);
            streams.Add(JxlFrameHeader.VarDctDcStream(group), dc, JxlTreeKind.WeightedFixedDc);
        }

        if (original is not null)
        {
            int extraChannels = metadata.ExtraChannels.Count;
            JxlEpfSharpnessSearch.Choose(frame, original, maps.Mask1X1, dequantizedDc, patches?.Patches(extraChannels) ?? [],
                patches?.References(extraChannels) ?? new JxlReferenceFrames(), cancellationToken);
            original.Release();
        }
        for (int group = 0; group < header.DcGroups; group++)
        {
            streams.Add(header.AcMetadataStream(group), AcMetadata(frame, group),
                settings.SpeedTier >= JxlLossySettings.Falcon ? JxlTreeKind.LeftPredictor : JxlTreeKind.AcMetadata);
        }

        if (alpha is not null) AddAlpha(streams, header, alpha, frameWidth, frameHeight, settings.Resampling);
        streams.Build([0, JxlFrameHeader.VarDctDcStream(0), header.ModularDcStream(0), header.AcMetadataStream(0), header.QuantTableStream(0),
            header.ModularAcStream(0, 0), header.ModularAcStream(0, header.Groups)], cancellationToken, settings.Parallelism);

        WritePrepared(writer, frame, metadata, streams, alpha is not null, patches, cancellationToken);
    }

    internal static void WritePrepared(JxlBitWriter writer, JxlLossyFrame frame, JxlImageMetadata metadata, JxlModularStreamSet streams,
        bool alpha, JxlPatchEncoder? patches, CancellationToken cancellationToken)
    {
        JxlFrameHeader header = frame.Header;
        (int[] orders, uint codedOrders) = JxlCoefficientOrders.Compute(frame);
        using var tokenBuffers = new JxlBufferScope();
        // The token streams are kept in group order: histograms and sections see them as before.
        var groupTokens = new JxlTokens[header.Groups];
        JxlParallel.For(groupTokens.Length, frame.Settings.Parallelism, cancellationToken,
            group => groupTokens[group] = JxlAcTokenizer.Tokenize(frame, group, orders, tokenBuffers));
        List<JxlTokens> acTokens = [.. groupTokens];
        JxlEntropyEncoder acCode = JxlEntropyEncoder.Build(frame.BlockContexts.AcContexts, acTokens, cancellationToken);
        WriteSections(writer, frame, metadata, streams, alpha, patches, orders, codedOrders, acCode, acTokens, cancellationToken);
    }

    // LossyFrameHeuristics: quantization field, chroma from luma and varblocks.
    private static JxlAqMaps ChooseParameters(JxlLossyFrame frame, JxlImage3F image, CancellationToken cancellationToken)
    {
        JxlLossySettings settings = frame.Settings;
        float distance = settings.Distance;
        int blocks = frame.XBlocks * frame.YBlocks;
        JxlAqMaps maps;
        if (settings.AdaptiveQuantization)
        {
            maps = JxlAdaptiveQuantization.Compute(settings.Gaborish ? distance : distance * 0.62f, image, cancellationToken, settings.Parallelism,
                image.Buffers);
            frame.Quantizer.ComputeGlobalScaleAndQuant(settings.InitialQuantDc, 0.39f / distance, 0);
        }
        else
        {
            maps = JxlAdaptiveQuantization.Constant(distance, blocks, image.Width * image.Height);
            frame.Quantizer.ComputeGlobalScaleAndQuant(settings.InitialQuantDc, 0.79f / distance, 0);
        }

        if (settings.Gaborish) JxlGaborish.Inverse(image, cancellationToken, settings.Parallelism);
        if (settings.InitialChromaFromLuma) ChromaFromLuma(frame, image, null, cancellationToken);
        if (settings.SearchAcStrategy)
        {
            // A tile reads and writes only its own varblocks; searches hold per-worker scratch.
            JxlParallel.ForWorkers(frame.YTiles * frame.XTiles, settings.Parallelism, cancellationToken,
                () => new JxlAcStrategySearch(image, maps.QuantField, maps.Mask1X1, frame.Matrices, frame.Strategies, distance, settings.SpeedTier),
                (search, tile) =>
                {
                    int bx = tile % frame.XTiles * JxlLossyFrame.TileBlocks;
                    int by = tile / frame.XTiles * JxlLossyFrame.TileBlocks;
                    search.ProcessTile(bx, by, Math.Min(JxlLossyFrame.TileBlocks, frame.XBlocks - bx), Math.Min(JxlLossyFrame.TileBlocks, frame.YBlocks - by),
                        JxlLossyFrame.YToXRatio(frame.YToX[tile]), JxlLossyFrame.YToBRatio(frame.YToB[tile]));
                });
        }
        else
        {
            JxlAcStrategySearch.FillDct8(frame.Strategies);
        }

        JxlAdaptiveQuantization.AdjustQuantField(frame.Strategies, frame.XBlocks, frame.YBlocks, distance, maps.QuantField);
        for (int i = 0; i < blocks; i++) frame.QuantField[i] = frame.Quantizer.Quantize(maps.QuantField[i]);
        if (settings.ChromaFromLuma) ChromaFromLuma(frame, image, frame.Strategies, cancellationToken);
        frame.BlockContexts = JxlAcTokenizer.BlockEntropyModel(frame);
        return maps;
    }

    // Each tile's factors depend only on its own pixels and varblocks; searches hold per-worker scratch.
    private static void ChromaFromLuma(JxlLossyFrame frame, JxlImage3F image, JxlVarblock[]? strategies, CancellationToken cancellationToken)
    {
        bool fast = strategies is not null && frame.Settings.FastChromaFromLuma;
        JxlParallel.ForWorkers(frame.YTiles * frame.XTiles, frame.Settings.Parallelism, cancellationToken,
            () => new JxlChromaFromLumaSearch(image, frame.Matrices),
            (search, tile) =>
            {
                (sbyte yToX, sbyte yToB) = search.ComputeTile(tile % frame.XTiles, tile / frame.XTiles, strategies,
                    strategies is null ? null : frame.QuantField, frame.Quantizer.Scale, fast);
                frame.YToX[tile] = yToX;
                frame.YToB[tile] = yToB;
            });
    }

    // SimplifyInvisible: fully transparent pixels become a smear of their left, upper-right and visible neighbours,
    // which compresses better and keeps edges clean; their colour is not kept.
    private static void SimplifyInvisible(JxlImage3F image, int width, int height, int[] alpha)
    {
        int stride = image.Width;
        for (int c = 0; c < 3; c++)
        {
            float[] p = image.Planes[c];
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                int a = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (alpha[a + x] != 0) continue;
                    float d = 0;
                    float v = 0;
                    if (x > 0)
                    {
                        v += p[row + x - 1];
                        d++;
                        if (alpha[a + x - 1] > 0)
                        {
                            v += p[row + x - 1];
                            d++;
                        }
                    }

                    if (x + 1 < width)
                    {
                        if (y > 0)
                        {
                            v += p[row - stride + x + 1];
                            d++;
                        }

                        if (alpha[a + x + 1] > 0)
                        {
                            v += 2 * p[row + x + 1];
                            d += 2;
                        }

                        if (y > 0 && alpha[a - width + x + 1] > 0)
                        {
                            v += 2 * p[row - stride + x + 1];
                            d += 2;
                        }

                        if (y + 1 < height && alpha[a + width + x + 1] > 0)
                        {
                            v += 2 * p[row + stride + x + 1];
                            d += 2;
                        }
                    }

                    if (y > 0 && alpha[a - width + x] > 0)
                    {
                        v += 2 * p[row - stride + x];
                        d += 2;
                    }

                    if (y + 1 < height && alpha[a + width + x] > 0)
                    {
                        v += 2 * p[row + stride + x];
                        d += 2;
                    }

                    p[row + x] = d > 1 ? v / d : v;
                }
            }
        }
    }

    // ComputeChromacityAdjustments: coarser X and B matrices at high distances and for pixelated chroma.
    private static (uint XQmScale, uint BQmScale) ChromacityAdjustments(JxlImage3F image, JxlLossySettings settings)
    {
        uint xQmScale = 3;
        foreach (float step in (ReadOnlySpan<float>)[2.5f, 5.5f, 9.5f])
        {
            if (settings.OriginalDistance > step) xQmScale++;
        }

        if (!settings.ChromacityStatistics) return (Math.Max(xQmScale, 2), 2);
        float dx = MaxNeighbourDifference(image.Planes[0], image.Width, image.Height);
        (float db, float exposedBlue) = BlueStatistics(image);
        int xPixelized = dx >= 0.026f ? 3 : dx >= 0.022f ? 2 : dx >= 0.015f ? 1 : 0;
        int add = exposedBlue >= 0.13f ? 1 : 0;
        int bPixelized = db > 0.38f ? 2 + add : db > 0.33f ? 1 + add : db > 0.28f ? add : 0;
        return (Math.Max(xQmScale, (uint)(2 + xPixelized)), (uint)(2 + bPixelized));
    }

    private static float MaxNeighbourDifference(float[] plane, int width, int height)
    {
        float max = 0;
        for (int y = 1; y < height; y++)
        {
            for (int x = 1; x < width; x++)
            {
                float current = plane[y * width + x];
                max = Math.Max(max, Math.Max(Math.Abs(current - plane[y * width + x - 1]), Math.Abs(current - plane[(y - 1) * width + x])));
            }
        }

        return max;
    }

    private static (float Db, float ExposedBlue) BlueStatistics(JxlImage3F image)
    {
        float exposed = 0;
        float max = 0;
        int width = image.Width;
        float[] py = image.Planes[1];
        float[] pb = image.Planes[2];
        for (int y = 1; y < image.Height; y++)
        {
            for (int x = 1; x < width; x++)
            {
                int i = y * width + x;
                float curY = py[i];
                float curB = pb[i];
                float exposedB = curB - curY * 1.2f;
                float diffB = curB - curY;
                float above = pb[i - width];
                float left = pb[i - 1];
                float diffAbove = above - py[i - width];
                float diffLeft = left - py[i - 1];
                max = Math.Max(max, Math.Max(Math.Abs(diffB - diffLeft), Math.Abs(diffB - diffAbove)));
                if (exposedB >= 0)
                {
                    exposedB *= Math.Abs(curB - left) + Math.Abs(curB - above);
                    exposed = Math.Max(exposed, exposedB);
                }
            }
        }

        return (max, exposed);
    }

    // AddVarDCTDC: the DC of a DC group quantized as Y, X, B; at the slower efforts against the weighted predictor's
    // guess with one bit more precision.
    private static List<JxlChannel> QuantizeDc(JxlLossyFrame frame, int group)
    {
        (int x0, int y0, int width, int height) = frame.DcGroupRect(group);
        bool nonlinear = frame.Settings.NonlinearDc;
        float mul = nonlinear ? 2 : 1;
        JxlEncoderQuantizer quantizer = frame.Quantizer;
        var channels = new JxlChannel[3];
        foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
        {
            var channel = new JxlChannel(width, height);
            channels[c < 2 ? c ^ 1 : c] = channel;
            float inverseFactor = quantizer.InverseDcStep(c) * mul;
            float yFactor = quantizer.DcStep(1) / mul;
            // The DC chroma-from-luma factors keep their defaults: 0 for X, 1 for B.
            float cflFactor = c == 2 ? 1.0f : 0.0f;
            int[] pixels = channel.Pixels;
            int[] luma = channels[0].Pixels;
            float[] dc = frame.Dc[c];
            using var weighted = nonlinear ? new JxlWeightedPredictor(JxlWeightedHeader.Default, width) : null;
            for (int y = 0; y < height; y++)
            {
                int row = (y0 + y) * frame.XBlocks + x0;
                for (int x = 0; x < width; x++)
                {
                    int at = y * width + x;
                    float value = c == 1 ? dc[row + x] : dc[row + x] - luma[at] * (yFactor * cflFactor);
                    if (weighted is null)
                    {
                        pixels[at] = (int)MathF.Round(value * inverseFactor, MidpointRounding.AwayFromZero);
                        continue;
                    }

                    var neighbours = JxlNeighbours.At(pixels, width, x, y);
                    long guess = weighted.Predict(x, y, neighbours, out _);
                    pixels[at] = (int)(QuantizeResidual(value * inverseFactor - guess) + guess);
                    weighted.Update(pixels[at], x, y);
                }
            }
        }

        return [.. channels];
    }

    // DequantDC: the DC values the decoder reconstructs from the quantized Y, X, B channels of a DC group.
    private static void DequantizeDc(JxlLossyFrame frame, int group, List<JxlChannel> dc, float[][] target)
    {
        (int x0, int y0, int width, int height) = frame.DcGroupRect(group);
        float mul = frame.Settings.NonlinearDc ? 0.5f : 1.0f;
        float facX = frame.Quantizer.DcStep(0) * mul;
        float facY = frame.Quantizer.DcStep(1) * mul;
        float facB = frame.Quantizer.DcStep(2) * mul;
        for (int y = 0; y < height; y++)
        {
            int row = (y0 + y) * frame.XBlocks + x0;
            for (int x = 0; x < width; x++)
            {
                int at = y * width + x;
                float luma = dc[0].Pixels[at] * facY;
                target[1][row + x] = luma;
                target[0][row + x] = dc[1].Pixels[at] * facX;
                target[2][row + x] = MathF.FusedMultiplyAdd(luma, 1.0f, dc[2].Pixels[at] * facB);
            }
        }
    }

    // QuantizeWP: a dead zone around the prediction, and residuals beyond ±2 rounded to even values.
    private static long QuantizeResidual(float residual)
    {
        const float deadZone = 0.62f;
        if (residual > -deadZone && residual < deadZone) residual = 0;
        if (float.IsNaN(residual) || residual > int.MaxValue || residual < int.MinValue)
        {
            throw JxlDescriptor.Unsupported("the DC values are out of range.");
        }

        long rounded = (long)MathF.Round(residual, MidpointRounding.AwayFromZero);
        return rounded is > 2 or < -2 ? (long)MathF.Round(residual * 0.5f, MidpointRounding.AwayFromZero) * 2 : rounded;
    }

    // AddACMetadata: chroma from luma per tile, the varblocks with their quantization, and the EPF sharpness.
    internal static List<JxlChannel> AcMetadata(JxlLossyFrame frame, int group)
    {
        (int x0, int y0, int width, int height) = frame.DcGroupRect(group);
        int tileX0 = x0 >> 3;
        int tileY0 = y0 >> 3;
        int tileWidth = (width + 7) >> 3;
        int tileHeight = (height + 7) >> 3;
        var yToX = new JxlChannel(tileWidth, tileHeight, 3, 3);
        var yToB = new JxlChannel(tileWidth, tileHeight, 3, 3);
        for (int y = 0; y < tileHeight; y++)
        {
            for (int x = 0; x < tileWidth; x++)
            {
                int tile = (tileY0 + y) * frame.XTiles + tileX0 + x;
                yToX.Row(y)[x] = frame.YToX[tile];
                yToB.Row(y)[x] = frame.YToB[tile];
            }
        }

        var types = new List<int>();
        var quants = new List<int>();
        var sharpness = new JxlChannel(width, height);
        for (int y = 0; y < height; y++)
        {
            int row = (y0 + y) * frame.XBlocks + x0;
            for (int x = 0; x < width; x++)
            {
                sharpness.Row(y)[x] = frame.Sharpness[row + x];
                JxlVarblock varblock = frame.Strategies[row + x];
                if (!varblock.IsFirst) continue;
                types.Add((int)varblock.Type);
                quants.Add(frame.QuantField[row + x] - 1);
            }
        }

        var strategies = new JxlChannel(types.Count, 2);
        types.CopyTo(strategies.Row(0));
        quants.CopyTo(strategies.Row(1));
        return [yToX, yToB, strategies, sharpness];
    }

    // The alpha channel: in the global stream when it fits a group, else cropped into the groups.
    private static void AddAlpha(JxlModularStreamSet streams, JxlFrameHeader header, int[] alpha, int width, int height, int resampling)
    {
        if (resampling != 1) throw new ArgumentException("Alpha is stored at full resolution, which requires frames that are not resampled.");
        var channel = new JxlChannel(width, height);
        alpha.AsSpan(0, width * height).CopyTo(channel.Pixels);
        int groupDim = header.GroupDimension;
        if (width <= groupDim && height <= groupDim)
        {
            streams.Add(JxlFrameHeader.GlobalStream, [channel], JxlTreeKind.Learn);
            return;
        }

        for (int group = 0; group < header.Groups; group++)
        {
            (int gx, int gy) = header.GroupOrigin(group, groupDim);
            int w = Math.Min(groupDim, width - gx);
            int h = Math.Min(groupDim, height - gy);
            var crop = new JxlChannel(w, h);
            for (int y = 0; y < h; y++) channel.Row(gy + y).Slice(gx, w).CopyTo(crop.Row(y));
            streams.Add(header.ModularAcStream(0, group), [crop], JxlTreeKind.Learn);
        }
    }

    private static void WriteSections(JxlBitWriter writer, JxlLossyFrame frame, JxlImageMetadata metadata, JxlModularStreamSet streams, bool alpha,
        JxlPatchEncoder? patches, int[] orders, uint codedOrders, JxlEntropyEncoder acCode, List<JxlTokens> acTokens, CancellationToken cancellationToken)
    {
        JxlFrameHeader header = frame.Header;
        var global = new JxlBitWriter(writer.Buffers);
        patches?.WriteDictionary(global, metadata.ExtraChannels.Count);
        global.Write(!frame.Jpeg);                                // DC quantization weights
        if (frame.Jpeg)
            for (int c = 0; c < 3; c++) JxlFieldWriter.F16(global, frame.Matrices.DcQuant[c] * 128);
        JxlFieldWriter.U32(global, (uint)frame.Quantizer.GlobalScale, IO.JxlU32.BitsOffset(11, 1), IO.JxlU32.BitsOffset(11, 2049),
            IO.JxlU32.BitsOffset(12, 4097), IO.JxlU32.BitsOffset(16, 8193));
        JxlFieldWriter.U32(global, (uint)frame.Quantizer.QuantDc, IO.JxlU32.Val(16), IO.JxlU32.BitsOffset(5, 1), IO.JxlU32.BitsOffset(8, 1),
            IO.JxlU32.BitsOffset(16, 1));
        BlockContextMap(global, frame.BlockContexts);
        global.Write(!frame.Jpeg);                                // chroma from luma for DC
        if (frame.Jpeg)
        {
            JxlFieldWriter.U32(global, 84, IO.JxlU32.Val(84), IO.JxlU32.Val(256), IO.JxlU32.BitsOffset(8, 2), IO.JxlU32.BitsOffset(16, 258));
            JxlFieldWriter.F16(global, 0); JxlFieldWriter.F16(global, 0);
            global.Write(8, 128); global.Write(8, 128);
        }
        streams.WriteGlobal(global);
        streams.WriteStream(global, JxlFrameHeader.GlobalStream, header: alpha);

        var sections = new List<JxlBitWriter> { global };
        for (int group = 0; group < header.DcGroups; group++)
        {
            var section = new JxlBitWriter(writer.Buffers);
            section.Write(2, !frame.Jpeg && frame.Settings.NonlinearDc ? 1u : 0u);  // extra DC precision
            streams.WriteStream(section, JxlFrameHeader.VarDctDcStream(group));
            streams.WriteStream(section, header.ModularDcStream(group));
            (_, _, int width, int height) = frame.DcGroupRect(group);
            int bits = CeilLog2(width * height);
            int count = 0;
            (int x0, int y0, _, _) = frame.DcGroupRect(group);
            for (int y = y0; y < y0 + height; y++)
            {
                for (int x = x0; x < x0 + width; x++) count += frame.Strategies[y * frame.XBlocks + x].IsFirst ? 1 : 0;
            }

            section.Write(bits, (uint)(count - 1));
            streams.WriteStream(section, header.AcMetadataStream(group));
            sections.Add(section);
        }

        var hfGlobal = new JxlBitWriter(writer.Buffers);
        hfGlobal.Write(!frame.Jpeg);                               // AC quantization tables
        if (frame.Jpeg)
        {
            for (int table = 0; table < JxlFrameHeader.QuantTables; table++)
            {
                hfGlobal.Write(3, table == 0 ? 7u : 0u);
                if (table != 0) continue;
                JxlFieldWriter.F16(hfGlobal, 1.0f / (8 * 255));
                streams.WriteStream(hfGlobal, header.QuantTableStream(0));
            }
        }
        hfGlobal.Write(CeilLog2(header.Groups), 0);               // one histogram set
        JxlCoefficientOrders.Write(hfGlobal, orders, codedOrders);
        acCode.WriteHeader(hfGlobal);
        sections.Add(hfGlobal);
        // Every group section is coded on its own; they are appended in group order.
        var groupSections = new JxlBitWriter[header.Groups];
        JxlParallel.For(groupSections.Length, frame.Settings.Parallelism, cancellationToken, group =>
        {
            var section = new JxlBitWriter(writer.Buffers);
            acCode.WriteStream(section, acTokens[group], cancellationToken);
            streams.WriteStream(section, header.ModularAcStream(0, group));
            groupSections[group] = section;
        });
        sections.AddRange(groupSections);

        JxlHeaderWriter.Frame(writer, header, metadata);
        if (header.TocEntries == 1)
        {
            var single = new JxlBitWriter(writer.Buffers);
            foreach (JxlBitWriter section in sections) single.Append(section);
            JxlHeaderWriter.Toc(writer, [checked((int)((single.BitLength + 7) / 8))]);
            writer.WriteSection(single);
            return;
        }

        JxlHeaderWriter.Toc(writer, [.. sections.Select(section => checked((int)((section.BitLength + 7) / 8)))]);
        foreach (JxlBitWriter section in sections) writer.WriteSection(section);
    }

    private static void BlockContextMap(JxlBitWriter writer, JxlBlockContextMap map)
    {
        writer.Write(map.IsDefault);
        if (map.IsDefault) return;
        for (int c = 0; c < 3; c++)
        {
            writer.Write(4, (uint)map.DcThresholds[c].Length);
            foreach (int threshold in map.DcThresholds[c])
            {
                JxlFieldWriter.U32(writer, PackSigned(threshold), IO.JxlU32.Bits32(4), IO.JxlU32.BitsOffset(8, 16), IO.JxlU32.BitsOffset(16, 272),
                    IO.JxlU32.BitsOffset(32, 65808));
            }
        }

        writer.Write(4, (uint)map.QfThresholds.Count);
        foreach (uint threshold in map.QfThresholds)
        {
            JxlFieldWriter.U32(writer, threshold - 1, IO.JxlU32.Bits32(2), IO.JxlU32.BitsOffset(3, 4), IO.JxlU32.BitsOffset(5, 12), IO.JxlU32.BitsOffset(8, 44));
        }

        JxlEntropyEncoder.WriteContextMap(writer, [.. map.Map], map.Contexts);
    }
}
