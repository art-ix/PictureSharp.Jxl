using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Frames;

/// <summary>Quantized spline parsing from libjxl v0.12.0 splines.cc (Splines::Decode, QuantizedSpline::Decode; BSD-3-Clause).</summary>
internal static class JxlSplineReader
{
    private const int Contexts = 6;
    private const int QuantizationAdjustmentContext = 0;
    private const int StartingPositionContext = 1;
    private const int NumSplinesContext = 2;
    private const int NumControlPointsContext = 3;
    private const int ControlPointsContext = 4;
    private const int DctContext = 5;
    private const long MaxControlPoints = 1 << 20;
    private const long MaxControlPointsPerPixelRatio = 2;
    private const long DeltaLimit = 1 << 30;

    /// <summary>Reads the splines of a frame of <paramref name="pixels"/> (not upsampled) pixels.</summary>
    public static JxlSplineData Read(ref JxlBitReader reader, long pixels)
    {
        JxlEntropyCode code = JxlEntropyCode.Read(ref reader, Contexts);
        using var symbols = new JxlSymbolReader(code, ref reader);
        long count = symbols.Read(NumSplinesContext, ref reader);
        long maxControlPoints = Math.Min(MaxControlPoints, pixels / MaxControlPointsPerPixelRatio);
        if (count + 1 > maxControlPoints)
        {
            throw JxlDescriptor.Corrupt("Too many splines.");
        }

        count++;
        var starts = new (long X, long Y)[count];
        for (int i = 0; i < count; i++)
        {
            uint dx = symbols.Read(StartingPositionContext, ref reader);
            uint dy = symbols.Read(StartingPositionContext, ref reader);
            starts[i] = i == 0 ? (dx, dy) : (UnpackSigned(dx) + starts[i - 1].X, UnpackSigned(dy) + starts[i - 1].Y);
            JxlSplineData.ValidatePosition(starts[i].X, starts[i].Y);
        }

        int adjustment = UnpackSigned(symbols.Read(QuantizationAdjustmentContext, ref reader));
        var splines = new JxlQuantizedSpline[count];
        long controlPoints = count;
        for (int i = 0; i < count; i++)
        {
            long points = symbols.Read(NumControlPointsContext, ref reader);
            controlPoints += points;
            if (points > maxControlPoints || controlPoints > maxControlPoints)
            {
                throw JxlDescriptor.Corrupt("Too many spline control points.");
            }

            var deltas = new (int X, int Y)[points];
            for (int p = 0; p < points; p++)
            {
                int x = UnpackSigned(symbols.Read(ControlPointsContext, ref reader));
                int y = UnpackSigned(symbols.Read(ControlPointsContext, ref reader));
                if (Math.Abs((long)x) >= DeltaLimit || Math.Abs((long)y) >= DeltaLimit)
                {
                    throw JxlDescriptor.Corrupt("A spline delta-delta is out of bounds.");
                }

                deltas[p] = (x, y);
            }

            int[][] color = [ReadDct(symbols, ref reader), ReadDct(symbols, ref reader), ReadDct(symbols, ref reader)];
            splines[i] = new JxlQuantizedSpline(deltas, color, ReadDct(symbols, ref reader));
        }

        return symbols.IsFinalStateValid
            ? new JxlSplineData(starts, adjustment, splines)
            : throw JxlDescriptor.Corrupt("Invalid ANS stream of the splines.");
    }

    private static int[] ReadDct(JxlSymbolReader symbols, ref JxlBitReader reader)
    {
        int[] dct = new int[32];
        for (int i = 0; i < dct.Length; i++)
        {
            dct[i] = UnpackSigned(symbols.Read(DctContext, ref reader));
            if (dct[i] == int.MinValue) throw JxlDescriptor.Corrupt("Invalid spline DCT coefficient.");
        }

        return dct;
    }
}
