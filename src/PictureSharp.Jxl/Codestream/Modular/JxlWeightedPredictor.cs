using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Modular;

/// <summary>
/// The weighted predictor: four sub-predictions mixed by their recent errors, with errors of two rows kept. Values
/// carry three extra fractional bits. Ported from libjxl v0.12.0 lib/jxl/modular/encoding/context_predict.h
/// (weighted::State), BSD-3-Clause; the integer widths and wrap-arounds follow libjxl.
/// </summary>
internal sealed class JxlWeightedPredictor(JxlWeightedHeader header, int width)
{
    private const int ExtraBits = 3;
    private const long Round = ((1 << ExtraBits) >> 1) - 1;

    // (1 << 24) / (i + 1): division by 1–64 as a multiplication.
    private static readonly uint[] DivLookup = [.. Enumerable.Range(0, 64).Select(i => (uint)((1 << 24) / (i + 1)))];

    private readonly int width = width;
    // The header's maximum weights, copied out of its read-only list for the per-pixel loop.
    private readonly uint maxWeight0 = header.Weights[0];
    private readonly uint maxWeight1 = header.Weights[1];
    private readonly uint maxWeight2 = header.Weights[2];
    private readonly uint maxWeight3 = header.Weights[3];
    // Two rows of errors, each padded by one position on both sides.
    private readonly int rowStride = width + 2;
    private readonly uint[] predictionErrors0 = ErrorRows(width);
    private readonly uint[] predictionErrors1 = ErrorRows(width);
    private readonly uint[] predictionErrors2 = ErrorRows(width);
    private readonly uint[] predictionErrors3 = ErrorRows(width);
    private readonly int[] errors = new int[(width + 2) * 2];
    private long prediction0;
    private long prediction1;
    private long prediction2;
    private long prediction3;
    private long prediction;

    /// <summary>Predicts the pixel at (x, y) from its neighbours; <paramref name="maxError"/> is the WP property.</summary>
    public long Predict(int x, int y, in JxlNeighbours neighbours, out int maxError)
    {
        int current = (y & 1) != 0 ? 0 : rowStride;
        int previous = (y & 1) != 0 ? rowStride : 0;
        int positionN = previous + x;
        int positionNe = x < width - 1 ? positionN + 1 : positionN;
        int positionNw = x > 0 ? positionN - 1 : positionN;
        uint weight0 = ErrorWeight(predictionErrors0, positionN, positionNe, positionNw, maxWeight0);
        uint weight1 = ErrorWeight(predictionErrors1, positionN, positionNe, positionNw, maxWeight1);
        uint weight2 = ErrorWeight(predictionErrors2, positionN, positionNe, positionNw, maxWeight2);
        uint weight3 = ErrorWeight(predictionErrors3, positionN, positionNe, positionNw, maxWeight3);

        long n = AddBits(neighbours.N);
        long w = AddBits(neighbours.W);
        long ne = AddBits(neighbours.NE);
        long nw = AddBits(neighbours.NW);
        long nn = AddBits(neighbours.NN);

        long errorW = x == 0 ? 0 : errors[current + x - 1];
        long errorN = errors[positionN];
        long errorNw = errors[positionNw];
        long sumWn = errorN + errorW;
        long errorNe = errors[positionNe];

        long property = errorW;
        if (Math.Abs(errorN) > Math.Abs(property))
        {
            property = errorN;
        }

        if (Math.Abs(errorNw) > Math.Abs(property))
        {
            property = errorNw;
        }

        if (Math.Abs(errorNe) > Math.Abs(property))
        {
            property = errorNe;
        }

        maxError = (int)property;
        prediction0 = w + ne - n;
        prediction1 = n - (((sumWn + errorNe) * header.P1C) >> 5);
        prediction2 = w - (((sumWn + errorNw) * header.P2C) >> 5);
        prediction3 = n - ((errorNw * header.P3Ca + errorN * header.P3Cb + errorNe * header.P3Cc + (nn - n) * header.P3Cd
            + (nw - w) * header.P3Ce) >> 5);
        prediction = WeightedAverage(weight0, weight1, weight2, weight3);

        // When the three errors have different signs, clamp to the range of W, NE and N.
        if (((errorN ^ errorW) | (errorN ^ errorNw)) <= 0)
        {
            prediction = Math.Max(Math.Min(w, Math.Min(ne, n)), Math.Min(Math.Max(w, Math.Max(ne, n)), prediction));
        }

        return (prediction + Round) >> ExtraBits;
    }

    /// <summary>Records the actual value of the pixel; returns its error, which later predictions read.</summary>
    public int Update(long value, int x, int y)
    {
        int current = (y & 1) != 0 ? 0 : rowStride;
        int previous = (y & 1) != 0 ? rowStride : 0;
        value = AddBits(value);
        int error = unchecked((int)(prediction - value));
        errors[current + x] = error;
        UpdateErrors(predictionErrors0, prediction0, value, current + x, previous + x + 1);
        UpdateErrors(predictionErrors1, prediction1, value, current + x, previous + x + 1);
        UpdateErrors(predictionErrors2, prediction2, value, current + x, previous + x + 1);
        UpdateErrors(predictionErrors3, prediction3, value, current + x, previous + x + 1);
        return error;
    }

    /// <summary>
    /// The prediction <see cref="Predict"/> returned for a pixel of <paramref name="value"/> whose error was
    /// <paramref name="error"/>, assuming the error did not wrap around.
    /// </summary>
    public static long FromError(int error, long value) => (error + AddBits(value) + Round) >> ExtraBits;

    /// <summary>
    /// The weighted error property of the pixel at (x, y) from the errors <see cref="Update"/> returned for the
    /// previous pixels, as <see cref="Predict"/> computes it; <paramref name="row"/> and <paramref name="previous"/>
    /// are the errors of row y and y − 1 (zeros for the first row).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static int MaxError(ReadOnlySpan<int> row, ReadOnlySpan<int> previous, int x)
    {
        int property = x == 0 ? 0 : row[x - 1];
        int errorN = previous[x];
        int errorNw = x > 0 ? previous[x - 1] : errorN;
        int errorNe = x < previous.Length - 1 ? previous[x + 1] : errorN;
        if (Math.Abs((long)errorN) > Math.Abs((long)property)) property = errorN;
        if (Math.Abs((long)errorNw) > Math.Abs((long)property)) property = errorNw;
        if (Math.Abs((long)errorNe) > Math.Abs((long)property)) property = errorNe;
        return property;
    }

    private static void UpdateErrors(uint[] errorsOf, long predicted, long value, int position, int positionNe)
    {
        uint error = unchecked((uint)((Math.Abs(predicted - value) + Round) >> ExtraBits));
        errorsOf[position] = error;
        // Adding the error to the NE position makes it count for the E and EE pixels of the next row.
        errorsOf[positionNe] = unchecked(errorsOf[positionNe] + error);
    }

    private static uint[] ErrorRows(int width) => new uint[(width + 2) * 2];

    private static long AddBits(long value) => unchecked(value << ExtraBits);

    private static uint ErrorWeight(uint[] errorsOf, int positionN, int positionNe, int positionNw, uint maxWeight) =>
        ErrorWeight(unchecked(errorsOf[positionN] + errorsOf[positionNe] + errorsOf[positionNw]), maxWeight);

    // 4 + (maxWeight << 24) / (x + 1), approximately and without division.
    private static uint ErrorWeight(uint x, uint maxWeight)
    {
        int shift = Math.Max(63 - System.Numerics.BitOperations.LeadingZeroCount(x + 1UL) - 5, 0);
        return 4 + ((maxWeight * DivLookup[x >> shift]) >> shift);
    }

    private long WeightedAverage(uint weight0, uint weight1, uint weight2, uint weight3)
    {
        int shift = Log2Floor(unchecked(weight0 + weight1 + weight2 + weight3)) - 4;
        weight0 >>= shift;
        weight1 >>= shift;
        weight2 >>= shift;
        weight3 >>= shift;
        uint sum = weight0 + weight1 + weight2 + weight3;
        long total = unchecked((sum >> 1) - 1);
        total = unchecked(total + prediction0 * weight0);
        total = unchecked(total + prediction1 * weight1);
        total = unchecked(total + prediction2 * weight2);
        total = unchecked(total + prediction3 * weight3);
        return unchecked(total * DivLookup[sum - 1]) >> 24;
    }
}
