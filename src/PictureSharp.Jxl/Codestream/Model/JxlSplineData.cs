namespace PictureSharp.Formats.Jxl.Codestream.Model;

/// <summary>One spline as coded: double-delta control points after the starting point, colour (X, Y, B) and sigma DCTs.</summary>
internal sealed record JxlQuantizedSpline((int X, int Y)[] ControlPoints, int[][] ColorDct, int[] SigmaDct);

/// <summary>The splines of a frame as read from the LF global section (libjxl v0.12.0 splines.h; BSD-3-Clause).</summary>
internal sealed record JxlSplineData((long X, long Y)[] StartingPoints, int QuantizationAdjustment, JxlQuantizedSpline[] Splines)
{
    private const long PositionLimit = 1 << 23;

    /// <summary>libjxl's ValidateSplinePointPos: coordinates and deltas stay within ±2^23.</summary>
    public static void ValidatePosition(long x, long y)
    {
        if (x >= PositionLimit || x <= -PositionLimit || y >= PositionLimit || y <= -PositionLimit)
        {
            throw JxlDescriptor.Corrupt("Spline coordinates out of bounds.");
        }
    }
}
