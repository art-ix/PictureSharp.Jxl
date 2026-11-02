using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlFastMath;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

/// <summary>
/// Synthetic noise added to the colour channels after upsampling, before the colour transform. Ported from libjxl
/// v0.12.0 dec_noise.cc (PrepareNoiseInput, RandomImage) and render_pipeline/stage_noise.cc (ConvolveNoiseStage,
/// AddNoiseStage), BSD-3-Clause. The random field depends only on the frame indices and the position of each group,
/// so the result does not depend on the degree of parallelism.
/// </summary>
internal static partial class JxlNoise
{
    private const int FloatsPerBatch = JxlXorshift128Plus.Lanes * 2;
    private const int MinBandRows = 16;
    private const float NormConst = 0.22f;
    private const float RgCorrelation = 0.9921875f;
    private const float RgNoCorrelation = 0.0078125f;

    /// <summary>
    /// Adds the noise of <paramref name="noise"/> to the three colour planes of the upsampled frame (X, Y, B for XYB
    /// frames, else the coded colour channels).
    /// </summary>
    public static void Apply(JxlPlane[] color, JxlFrameHeader frame, JxlNoiseParameters noise, int visibleIndex, int nonvisibleIndex,
        float yToX, float yToB, int parallelism, CancellationToken cancellationToken, JxlBufferScope? buffers = null)
    {
        if (!noise.HasAny) return;
        int width = color[0].Width;
        int height = color[0].Height;
        JxlPlane[] random = [.. Enumerable.Range(0, 3).Select(_ => new JxlPlane(width, height, buffers: buffers))];
        FillRandom(random, frame, (uint)visibleIndex, (uint)nonvisibleIndex, parallelism, cancellationToken);
        foreach (JxlPlane plane in random) plane.Mirror(2, 2, cancellationToken);
        JxlParallel.ForBands(height, MinBandRows, parallelism, cancellationToken, (start, end) =>
        {
            float[] convolved = new float[3 * width];
            for (int y = start; y < end; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int c = 0; c < 3; c++) Convolve(random[c], y, convolved.AsSpan(c * width, width));
                AddNoise(noise.Lut, color[0].Row(y), color[1].Row(y), color[2].Row(y), convolved, width, yToX, yToB);
            }
        });
    }

    // One generator per group_dim square of the upsampled frame (upsampling² per group), seeded with its position.
    private static void FillRandom(JxlPlane[] random, JxlFrameHeader frame, uint visibleIndex, uint nonvisibleIndex, int parallelism,
        CancellationToken cancellationToken)
    {
        int groupDim = frame.GroupDimension;
        int upsampling = (int)frame.Upsampling;
        int xGroups = (int)frame.XGroups;
        int squares = (int)(frame.Groups * upsampling * upsampling);
        JxlParallel.For(squares, parallelism, cancellationToken, index =>
        {
            int group = index / (upsampling * upsampling);
            int sub = index % (upsampling * upsampling);
            int x0 = ((group % xGroups) * upsampling + sub % upsampling) * groupDim;
            int y0 = ((group / xGroups) * upsampling + sub / upsampling) * groupDim;
            int width = Math.Min(groupDim, random[0].Width - x0);
            int height = Math.Min(groupDim, random[0].Height - y0);
            if (width <= 0 || height <= 0) return;
            var generator = new JxlXorshift128Plus(visibleIndex, nonvisibleIndex, (uint)x0, (uint)y0);
            Span<ulong> batch = stackalloc ulong[JxlXorshift128Plus.Lanes];
            foreach (JxlPlane plane in random)
            {
                for (int y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Span<float> row = plane.Row(y0 + y).Slice(x0, width);
                    int x = 0;
                    // Whole batches while more than a batch remains, then one batch for the rest (RandomImage).
                    for (; x + FloatsPerBatch < width; x += FloatsPerBatch)
                    {
                        generator.Fill(batch);
                        ToFloats(batch, row.Slice(x, FloatsPerBatch));
                    }

                    generator.Fill(batch);
                    ToFloats(batch, row[x..]);
                }
            }
        });
    }

    // 1.0 + 23 random mantissa bits = [1, 2), from the 32-bit halves of the lanes in memory order.
    internal static void ToFloats(ReadOnlySpan<ulong> batch, Span<float> output)
    {
        for (int i = ToFloatsAvx2(batch, output); i < output.Length; i++)
        {
            uint bits = (uint)(batch[i >> 1] >> (32 * (i & 1)));
            output[i] = BitConverter.UInt32BitsToSingle((bits >> 9) | 0x3F800000u);
        }
    }

    // 4 × (1 − box kernel) over 5×5: 0.16 × (sum of the 24 neighbours) − 3.84 × centre, summed in libjxl's order.
    internal static void Convolve(JxlPlane plane, int y, Span<float> output)
    {
        float[] data = plane.Data;
        int r0 = plane.Offset(0, y - 2);
        int r1 = plane.Offset(0, y - 1);
        int r2 = plane.Offset(0, y);
        int r3 = plane.Offset(0, y + 1);
        int r4 = plane.Offset(0, y + 2);
        for (int x = ConvolveAvx2(data, r0, r1, r2, r3, r4, output); x < output.Length; x++)
        {
            float others = 0;
            for (int i = -2; i <= 2; i++)
            {
                others += data[r0 + x + i];
                others += data[r1 + x + i];
                others += data[r3 + x + i];
                others += data[r4 + x + i];
            }

            others += data[r2 + x - 2];
            others += data[r2 + x - 1];
            others += data[r2 + x + 1];
            others += data[r2 + x + 2];
            output[x] = Fma(others, 0.16f, data[r2 + x] * -3.84f);
        }
    }

    internal static void AddNoise(float[] lut, Span<float> rowX, Span<float> rowY, Span<float> rowB, float[] convolved, int width,
        float yToX, float yToB)
    {
        for (int x = AddNoiseAvx2(lut, rowX, rowY, rowB, convolved, width, yToX, yToB); x < width; x++)
        {
            float vx = rowX[x];
            float vy = rowY[x];
            float strengthG = Strength(lut, (vy - vx) * 0.5f);
            float strengthR = Strength(lut, (vy + vx) * 0.5f);
            float randomR = convolved[x] * NormConst;
            float randomG = convolved[width + x] * NormConst;
            float randomC = convolved[2 * width + x] * NormConst;
            float red = strengthR * Fma(RgNoCorrelation, randomR, RgCorrelation * randomC);
            float green = strengthG * Fma(RgNoCorrelation, randomG, RgCorrelation * randomC);
            float sum = red + green;
            rowX[x] = Fma(yToX, sum, red - green) + vx;
            rowY[x] = vy + sum;
            rowB[x] = Fma(yToB, sum, rowB[x]);
        }
    }

    // The LUT interpolated over [0, 1] in 6 steps (the last entry is reached at 7/6), clamped to [0, 1].
    private static float Strength(float[] lut, float value)
    {
        const int Scale = JxlNoiseParameters.Points - 2;
        float scaled = value * Scale;
        if (!(scaled > 0)) scaled = 0;
        float floor = MathF.Floor(scaled);
        float fraction = scaled - floor;
        if (scaled >= Scale + 1)
        {
            floor = Scale;
            fraction = 1;
        }

        int index = (int)floor;
        float low = lut[index];
        float strength = Fma(lut[index + 1] - low, fraction, low);
        return strength > 1 ? 1 : strength > 0 ? strength : 0;
    }
}
