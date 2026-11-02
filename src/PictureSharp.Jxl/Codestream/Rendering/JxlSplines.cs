using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlFastMath;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

/// <summary>
/// Splines drawn onto the colour channels before upsampling: dequantized, traced as centripetal Catmull-Rom curves,
/// sampled every pixel of arc length and splatted as Gaussian segments. Ported from libjxl v0.12.0 splines.cc and
/// render_pipeline/stage_splines.cc (BSD-3-Clause); the AVX2 operation order (fused multiply-adds, 8-lane sums) is kept.
/// Each pixel adds its segments in segment order, so rows can be drawn in any order and on any thread.
/// </summary>
internal sealed class JxlSplines
{
    private const float DesiredRenderingDistance = 1.0f;
    // libjxl's JXL_HIGH_PRECISION build draws segments out to 0.1^5 of their peak.
    private const float DistanceExp = 5;
    private const int CatmullRomPoints = 16;
    private const int ChargeBlock = 4096;
    private const int MinBandRows = 16;
    private const float Sqrt2 = 1.41421356237f;
    private const float Sqrt0_5 = 0.70710678118f;
    private static readonly float[] ChannelWeight = [0.0042f, 0.075f, 0.07f, .3333f];
    private static readonly float[] IdctMultipliers = [.. Enumerable.Range(0, 32).Select(i => (float)(Math.PI / 32 * i))];

    private readonly Segment[] segments;
    private readonly long[] rowStart;
    private readonly int[] indices;

    private JxlSplines(Segment[] segments, long[] rowStart, int[] indices) =>
        (this.segments, this.rowStart, this.indices) = (segments, rowStart, indices);

    private readonly record struct Point(float X, float Y);

    private readonly record struct Segment(float CenterX, float CenterY, float MaximumDistance, float InvSigma,
        float SigmaOver4TimesIntensity, float Color0, float Color1, float Color2);

    /// <summary>
    /// Dequantizes <paramref name="data"/> and builds the segments for an upsampled frame of
    /// <paramref name="width"/> × <paramref name="height"/> (InitializeDrawCache). Memory is charged to the working budget.
    /// </summary>
    public static JxlSplines Prepare(JxlSplineData data, long width, long height, float yToX, float yToB, CancellationToken cancellationToken)
    {
        ulong imageSize = (ulong)width * (ulong)height;
        ulong areaLimit = Math.Min(1024 * imageSize + (1ul << 32), 1ul << 42);
        ulong totalArea = 0;
        var splines = new (Point[] Points, float[][] Color, float[] Sigma)[data.Splines.Length];
        for (int i = 0; i < splines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            splines[i] = Dequantize(data.Splines[i], data.StartingPoints[i], data.QuantizationAdjustment, yToX, yToB, areaLimit, ref totalArea);
            for (int p = 1; p < splines[i].Points.Length; p++)
            {
                if (splines[i].Points[p] == splines[i].Points[p - 1]) throw JxlDescriptor.Corrupt("Identical successive spline control points.");
            }
        }

        var segments = new List<Segment>();
        var spans = new List<(int Start, int End)>();
        var curve = new List<Point>();
        var toDraw = new List<(Point Point, float Multiplier)>();
        foreach ((Point[] points, float[][] color, float[] sigma) in splines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            curve.Clear();
            toDraw.Clear();
            CatmullRom(points, curve);
            ForEachEquallySpacedPoint(curve, toDraw, cancellationToken);
            float arcLength = (toDraw.Count - 2) * DesiredRenderingDistance + toDraw[^1].Multiplier;
            if (arcLength <= 0f) continue;
            SegmentsFromPoints((int)Math.Min(height, int.MaxValue), color, sigma, toDraw, arcLength, segments, spans,
                cancellationToken);
        }

        // Rows covered by each segment, as a prefix sum over rows and the segment indices of each row in segment order.
        int rows = (int)Math.Min(height, int.MaxValue);
        JxlWorkBudget.Charge((rows + 1L) * sizeof(long));
        long[] rowStart = new long[rows + 1];
        foreach ((int start, int end) in spans)
        {
            rowStart[start + 1]++;
            if (end < rows) rowStart[end + 1]--;
        }

        long coverage = 0;
        long total = 0;
        for (int y = 0; y < rows; y++)
        {
            coverage += rowStart[y + 1];
            rowStart[y + 1] = total + coverage;
            total += coverage;
        }

        if (total > Array.MaxLength) throw JxlDescriptor.Corrupt("Decoding needs more working memory than MaxPixels allows.");
        JxlWorkBudget.Charge(total * sizeof(int));
        int[] indices = new int[total];
        long[] next = new long[rows];
        for (int y = 0; y < rows; y++) next[y] = rowStart[y];
        for (int i = 0; i < spans.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int y = spans[i].Start; y < spans[i].End; y++) indices[next[y]++] = i;
        }

        return new JxlSplines([.. segments], rowStart, indices);
    }

    /// <summary>Adds the splines to the three colour planes of the frame (before upsampling).</summary>
    public void Draw(JxlPlane[] color, int parallelism, CancellationToken cancellationToken)
    {
        if (segments.Length == 0) return;
        int width = color[0].Width;
        int rows = Math.Min(color[0].Height, rowStart.Length - 1);
        JxlParallel.ForBands(rows, MinBandRows, parallelism, cancellationToken, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<float> row0 = color[0].Row(y);
                Span<float> row1 = color[1].Row(y);
                Span<float> row2 = color[2].Row(y);
                for (long i = rowStart[y]; i < rowStart[y + 1]; i++)
                {
                    DrawSegment(segments[indices[i]], y, width, row0, row1, row2);
                }
            }
        });
    }

    private static void DrawSegment(in Segment segment, int y, int width, Span<float> row0, Span<float> row1, Span<float> row2)
    {
        long start = LRound(segment.CenterX - segment.MaximumDistance);
        long end = LRound(segment.CenterX + segment.MaximumDistance);
        if (end < 0 || start >= width) return;
        int x0 = (int)Math.Max(0, start);
        int x1 = (int)Math.Min(width, end + 1);
        const float Half = 0.5f;
        const float OneOver2S2 = 0.353553391f;
        float dy = y - segment.CenterY;
        float dy2 = dy * dy;
        for (int x = x0; x < x1; x++)
        {
            float dx = x - segment.CenterX;
            float distance = MathF.Sqrt(Fma(dx, dx, dy2));
            float factor = FastErff(Fma(distance, Half, OneOver2S2) * segment.InvSigma)
                - FastErff(Fma(distance, Half, -OneOver2S2) * segment.InvSigma);
            float intensity = segment.SigmaOver4TimesIntensity * (factor * factor);
            row0[x] = Fma(segment.Color0, intensity, row0[x]);
            row1[x] = Fma(segment.Color1, intensity, row1[x]);
            row2[x] = Fma(segment.Color2, intensity, row2[x]);
        }
    }

    private static (Point[] Points, float[][] Color, float[] Sigma) Dequantize(JxlQuantizedSpline spline, (long X, long Y) start,
        int adjustment, float yToX, float yToB, ulong areaLimit, ref ulong totalArea)
    {
        float px = MathF.Round(start.X, MidpointRounding.AwayFromZero);
        float py = MathF.Round(start.Y, MidpointRounding.AwayFromZero);
        JxlSplineData.ValidatePosition((long)px, (long)py);
        long currentX = (long)px;
        long currentY = (long)py;
        var points = new Point[spline.ControlPoints.Length + 1];
        points[0] = new Point(currentX, currentY);
        long deltaX = 0;
        long deltaY = 0;
        ulong manhattan = 0;
        for (int i = 0; i < spline.ControlPoints.Length; i++)
        {
            deltaX += spline.ControlPoints[i].X;
            deltaY += spline.ControlPoints[i].Y;
            manhattan += (ulong)(Math.Abs(deltaX) + Math.Abs(deltaY));
            if (manhattan > areaLimit) throw JxlDescriptor.Corrupt("Too large spline Manhattan distance.");
            JxlSplineData.ValidatePosition(deltaX, deltaY);
            currentX += deltaX;
            currentY += deltaY;
            JxlSplineData.ValidatePosition(currentX, currentY);
            points[i + 1] = new Point(currentX, currentY);
        }

        float invQuant = adjustment >= 0 ? 1f / (1f + .125f * adjustment) : 1f - .125f * adjustment;
        float[][] color = [new float[32], new float[32], new float[32]];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < 32; i++)
            {
                float invDctFactor = i == 0 ? Sqrt0_5 : 1.0f;
                color[c][i] = spline.ColorDct[c][i] * invDctFactor * ChannelWeight[c] * invQuant;
            }
        }

        for (int i = 0; i < 32; i++)
        {
            color[0][i] += yToX * color[1][i];
            color[2][i] += yToB * color[1][i];
        }

        ulong[] colorWeight = new ulong[3];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < 32; i++) colorWeight[c] += (ulong)MathF.Ceiling(invQuant * Math.Abs(spline.ColorDct[c][i]));
        }

        colorWeight[0] += (ulong)MathF.Ceiling(MathF.Abs(yToX)) * colorWeight[1];
        colorWeight[2] += (ulong)MathF.Ceiling(MathF.Abs(yToB)) * colorWeight[1];
        ulong maxColor = Math.Max(colorWeight[1], Math.Max(colorWeight[0], colorWeight[2]));
        ulong logColor = Math.Max(1ul, (ulong)CeilLog2((long)(1 + maxColor)));
        float weightLimit = MathF.Ceiling(MathF.Sqrt((float)areaLimit / logColor / Math.Max(1ul, manhattan)));
        float[] sigma = new float[32];
        ulong widthEstimate = 0;
        for (int i = 0; i < 32; i++)
        {
            float invDctFactor = i == 0 ? Sqrt0_5 : 1.0f;
            sigma[i] = spline.SigmaDct[i] * invDctFactor * ChannelWeight[3] * invQuant;
            float weightF = MathF.Ceiling(invQuant * Math.Abs(spline.SigmaDct[i]));
            ulong weight = (ulong)MathF.Min(weightLimit, MathF.Max(1.0f, weightF));
            widthEstimate += weight * weight * logColor;
        }

        totalArea += widthEstimate * manhattan;
        if (totalArea > areaLimit) throw JxlDescriptor.Corrupt("Too large total spline area.");
        return (points, color, sigma);
    }

    // Centripetal Catmull-Rom through the control points, with mirrored end points (DrawCentripetalCatmullRomSpline).
    private static void CatmullRom(Point[] input, List<Point> result)
    {
        if (input.Length == 1)
        {
            result.Add(input[0]);
            return;
        }

        JxlWorkBudget.Charge(((input.Length - 1L) * CatmullRomPoints + 1) * 8);
        var points = new Point[input.Length + 2];
        input.CopyTo(points, 1);
        points[0] = Add(input[0], Sub(input[0], input[1]));
        points[^1] = Add(input[^1], Sub(input[^1], input[^2]));
        Span<float> d = stackalloc float[3];
        Span<float> t = stackalloc float[4];
        Span<Point> a = stackalloc Point[3];
        Span<Point> b = stackalloc Point[2];
        for (int start = 0; start < points.Length - 3; start++)
        {
            ReadOnlySpan<Point> p = points.AsSpan(start, 4);
            result.Add(p[1]);
            t[0] = 0;
            for (int k = 0; k < 3; k++)
            {
                d[k] = MathF.Sqrt(Hypot(p[k + 1].X - p[k].X, p[k + 1].Y - p[k].Y));
                t[k + 1] = t[k] + d[k];
            }

            for (int i = 1; i < CatmullRomPoints; i++)
            {
                float tt = d[0] + ((float)i / CatmullRomPoints) * d[1];
                for (int k = 0; k < 3; k++) a[k] = Add(p[k], Scale((tt - t[k]) / d[k], Sub(p[k + 1], p[k])));
                for (int k = 0; k < 2; k++) b[k] = Add(a[k], Scale((tt - t[k]) / (d[k] + d[k + 1]), Sub(a[k + 1], a[k])));
                result.Add(Add(b[0], Scale((tt - t[1]) / d[1], Sub(b[1], b[0]))));
            }
        }

        result.Add(points[^2]);
    }

    // Points one pixel of arc length apart; each has the arc length since the previous one (ForEachEquallySpacedPoint).
    private static void ForEachEquallySpacedPoint(List<Point> points, List<(Point Point, float Multiplier)> output,
        CancellationToken cancellationToken)
    {
        Point current = points[0];
        Add(output, (current, DesiredRenderingDistance), cancellationToken);
        int next = 0;
        while (next != points.Count)
        {
            Point previous = current;
            float fromPrevious = 0f;
            while (true)
            {
                if (next == points.Count)
                {
                    Add(output, (previous, fromPrevious), cancellationToken);
                    return;
                }

                Point step = Sub(points[next], previous);
                float toNext = MathF.Sqrt(step.X * step.X + step.Y * step.Y);
                if (fromPrevious + toNext >= DesiredRenderingDistance)
                {
                    current = Add(previous, Scale((DesiredRenderingDistance - fromPrevious) / toNext, step));
                    Add(output, (current, DesiredRenderingDistance), cancellationToken);
                    break;
                }

                fromPrevious += toNext;
                previous = points[next];
                next++;
            }
        }
    }

    private static void SegmentsFromPoints(int height, float[][] color, float[] sigmaDct, List<(Point Point, float Multiplier)> points,
        float arcLength, List<Segment> segments, List<(int Start, int End)> spans, CancellationToken cancellationToken)
    {
        float invArcLength = 1.0f / arcLength;
        Span<float> pointColor = stackalloc float[3];
        for (int k = 0; k < points.Count; k++)
        {
            float progress = MathF.Min(1f, (k * DesiredRenderingDistance) * invArcLength);
            for (int c = 0; c < 3; c++) pointColor[c] = ContinuousIdct(color[c], (32 - 1) * progress);
            float sigma = ContinuousIdct(sigmaDct, (32 - 1) * progress);
            (Point center, float intensity) = points[k];
            if (!(float.IsFinite(sigma) && sigma != 0.0f && float.IsFinite(1.0f / sigma) && float.IsFinite(intensity))) continue;
            float maxColor = 0.01f;
            for (int c = 0; c < 3; c++) maxColor = MathF.Max(maxColor, MathF.Abs(pointColor[c] * intensity));
            float maximumDistance = MathF.Sqrt(-2.0f * sigma * sigma * (MathF.Log(0.1f) * DistanceExp - MathF.Log(maxColor)));
            long y0 = Math.Max(LRound(center.Y - maximumDistance), 0);
            long y1 = Math.Min(LRound(center.Y + maximumDistance) + 1, height);
            if (y1 <= y0) continue;
            if (segments.Count % ChargeBlock == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JxlWorkBudget.Charge(ChargeBlock * (32L + 8));
            }

            segments.Add(new Segment(center.X, center.Y, maximumDistance, 1.0f / sigma, .25f * sigma * intensity,
                pointColor[0], pointColor[1], pointColor[2]));
            spans.Add(((int)y0, (int)y1));
        }
    }

    // The continuous inverse DCT at t in [0, 31], summed like libjxl's AVX2 code: 8 lanes of fused multiply-adds, then
    // the lanes as ((0+4)+(3+7))+((1+5)+(2+6)).
    private static float ContinuousIdct(float[] dct, float t)
    {
        Span<float> lanes = stackalloc float[8];
        lanes.Clear();
        float tAndHalf = t + 0.5f;
        for (int i = 0; i < 32; i++)
        {
            float cos = FastCosf(IdctMultipliers[i] * tAndHalf);
            lanes[i & 7] = Fma(Sqrt2, dct[i] * cos, lanes[i & 7]);
        }

        return ((lanes[0] + lanes[4]) + (lanes[3] + lanes[7])) + ((lanes[1] + lanes[5]) + (lanes[2] + lanes[6]));
    }

    private static void Add(List<(Point, float)> output, (Point, float) point, CancellationToken cancellationToken)
    {
        if (output.Count % ChargeBlock == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JxlWorkBudget.Charge(ChargeBlock * 12L);
        }

        output.Add(point);
    }


    // std::llround: round half away from zero; NaN and values out of range give LLONG_MIN as on x86.
    private static long LRound(float value) =>
        float.IsNaN(value) || MathF.Abs(value) >= 9.2233720368547758e18f ? long.MinValue : (long)MathF.Round(value, MidpointRounding.AwayFromZero);

    // glibc hypotf: computed in double and rounded once.
    private static float Hypot(float x, float y) => (float)Math.Sqrt((double)x * x + (double)y * y);

    private static Point Add(Point p, Point v) => new(p.X + v.X, p.Y + v.Y);

    private static Point Sub(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);

    private static Point Scale(float k, Point v) => new(k * v.X, k * v.Y);
}
