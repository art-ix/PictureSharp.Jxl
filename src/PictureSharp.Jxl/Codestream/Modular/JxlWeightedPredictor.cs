using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Modular;

/// <summary>
/// The weighted predictor: four sub-predictions mixed by their recent errors, with errors of two rows kept. Values
/// carry three extra fractional bits. Ported from libjxl v0.12.0 lib/jxl/modular/encoding/context_predict.h
/// (weighted::State), BSD-3-Clause; the integer widths and wrap-arounds follow libjxl.
/// </summary>
/// <remarks>
/// The per-pixel arithmetic is branch-free (sign masks instead of comparisons): the JIT emits no conditional moves, and
/// the comparisons are unpredictable on photos (0.8.4: 2.1× faster prediction). The errors of the four sub-predictors
/// are interleaved per position and read without bounds checks after <c>x</c> is checked against the width.
/// </remarks>
internal sealed class JxlWeightedPredictor : IDisposable
{
    private const int ExtraBits = 3;
    private const long Round = ((1 << ExtraBits) >> 1) - 1;

    // (1 << 24) / (i + 1): division by 1–64 as a multiplication.
    private static readonly uint[] DivLookup = [.. Enumerable.Range(0, 64).Select(i => (uint)((1 << 24) / (i + 1)))];

    private readonly JxlBufferScope buffers = new();
    private readonly int width;
    // The header's maximum weights and coefficients, copied out of it for the per-pixel loop.
    private readonly uint maxWeight0;
    private readonly uint maxWeight1;
    private readonly uint maxWeight2;
    private readonly uint maxWeight3;
    private readonly long p1C;
    private readonly long p2C;
    private readonly long p3Ca;
    private readonly long p3Cb;
    private readonly long p3Cc;
    private readonly long p3Cd;
    private readonly long p3Ce;
    // Two rows of errors, each padded by one position on both sides; the four sub-predictors' errors are interleaved,
    // so position p holds predictionErrors[4p..4p+3].
    private readonly int rowStride;
    private readonly uint[] predictionErrors;
    private readonly int[] errors;
    private long prediction0;
    private long prediction1;
    private long prediction2;
    private long prediction3;
    private long prediction;

    public JxlWeightedPredictor(JxlWeightedHeader header, int width)
    {
        this.width = width;
        rowStride = checked(width + 2);
        maxWeight0 = header.Weights[0];
        maxWeight1 = header.Weights[1];
        maxWeight2 = header.Weights[2];
        maxWeight3 = header.Weights[3];
        (p1C, p2C, p3Ca, p3Cb, p3Cc, p3Cd, p3Ce) = (header.P1C, header.P2C, header.P3Ca, header.P3Cb, header.P3Cc, header.P3Cd, header.P3Ce);
        try
        {
            predictionErrors = buffers.Rent<uint>(checked(rowStride * 2 * 4));
            errors = buffers.Rent<int>(checked(rowStride * 2));
        }
        catch { buffers.Dispose(); throw; }
    }
    public void Dispose() => buffers.Dispose();

    /// <summary>Predicts the pixel at (x, y) from its neighbours; <paramref name="maxError"/> is the WP property.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Predict(int x, int y, in JxlNeighbours neighbours, out int maxError)
    {
        // Every position below lies within the two padded rows once x is in [0, width).
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)x, (uint)width, nameof(x));
        int current = (y & 1) != 0 ? 0 : rowStride;
        int positionN = ((y & 1) != 0 ? rowStride : 0) + x;
        int positionNe = x < width - 1 ? positionN + 1 : positionN;
        int positionNw = x > 0 ? positionN - 1 : positionN;
        ref uint errorsOf = ref MemoryMarshal.GetArrayDataReference(predictionErrors);
        ref uint errorsN = ref Unsafe.Add(ref errorsOf, positionN * 4);
        ref uint errorsNe = ref Unsafe.Add(ref errorsOf, positionNe * 4);
        ref uint errorsNw = ref Unsafe.Add(ref errorsOf, positionNw * 4);
        uint weight0 = ErrorWeight(unchecked(errorsN + errorsNe + errorsNw), maxWeight0);
        uint weight1 = ErrorWeight(unchecked(Unsafe.Add(ref errorsN, 1) + Unsafe.Add(ref errorsNe, 1) + Unsafe.Add(ref errorsNw, 1)), maxWeight1);
        uint weight2 = ErrorWeight(unchecked(Unsafe.Add(ref errorsN, 2) + Unsafe.Add(ref errorsNe, 2) + Unsafe.Add(ref errorsNw, 2)), maxWeight2);
        uint weight3 = ErrorWeight(unchecked(Unsafe.Add(ref errorsN, 3) + Unsafe.Add(ref errorsNe, 3) + Unsafe.Add(ref errorsNw, 3)), maxWeight3);

        long n = AddBits(neighbours.N);
        long w = AddBits(neighbours.W);
        long ne = AddBits(neighbours.NE);
        long nw = AddBits(neighbours.NW);
        long nn = AddBits(neighbours.NN);

        ref int error = ref MemoryMarshal.GetArrayDataReference(errors);
        long errorW = x == 0 ? 0 : Unsafe.Add(ref error, current + x - 1);
        long errorN = Unsafe.Add(ref error, positionN);
        long errorNw = Unsafe.Add(ref error, positionNw);
        long sumWn = errorN + errorW;
        long errorNe = Unsafe.Add(ref error, positionNe);

        // The error of largest magnitude, the first on ties (W, N, NW, NE). The errors are 32-bit, so the differences
        // of their magnitudes cannot overflow.
        long property = errorW;
        long largest = SmallAbs(errorW);
        long magnitude = SmallAbs(errorN);
        long larger = (largest - magnitude) >> 63;
        property ^= (property ^ errorN) & larger;
        largest ^= (largest ^ magnitude) & larger;
        magnitude = SmallAbs(errorNw);
        larger = (largest - magnitude) >> 63;
        property ^= (property ^ errorNw) & larger;
        largest ^= (largest ^ magnitude) & larger;
        magnitude = SmallAbs(errorNe);
        larger = (largest - magnitude) >> 63;
        property ^= (property ^ errorNe) & larger;

        maxError = (int)property;
        prediction0 = w + ne - n;
        prediction1 = n - (((sumWn + errorNe) * p1C) >> 5);
        prediction2 = w - (((sumWn + errorNw) * p2C) >> 5);
        prediction3 = n - ((errorNw * p3Ca + errorN * p3Cb + errorNe * p3Cc + (nn - n) * p3Cd + (nw - w) * p3Ce) >> 5);
        long average = WeightedAverage(weight0, weight1, weight2, weight3);

        // When the three errors have different signs, clamp to the range of W, NE and N. The neighbours are 32-bit
        // values with three extra bits and the average a 64-bit product shifted right by 24 bits, so none of the
        // differences overflows.
        long clamped = SmallMax(SmallMin(w, SmallMin(ne, n)), SmallMin(SmallMax(w, SmallMax(ne, n)), average));
        long clamp = (((errorN ^ errorW) | (errorN ^ errorNw)) - 1) >> 63;
        prediction = average ^ ((average ^ clamped) & clamp);
        return (prediction + Round) >> ExtraBits;
    }

    /// <summary>Records the actual value of the pixel; returns its error, which later predictions read.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Update(long value, int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)x, (uint)width, nameof(x));
        int position = ((y & 1) != 0 ? 0 : rowStride) + x;
        int positionNe = ((y & 1) != 0 ? rowStride : 0) + x + 1;
        value = AddBits(value);
        int error = unchecked((int)(prediction - value));
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(errors), position) = error;
        ref uint errorsOf = ref MemoryMarshal.GetArrayDataReference(predictionErrors);
        ref uint at = ref Unsafe.Add(ref errorsOf, position * 4);
        // Adding the error to the NE position makes it count for the E and EE pixels of the next row.
        ref uint atNe = ref Unsafe.Add(ref errorsOf, positionNe * 4);
        uint error0 = SubpredictionError(prediction0, value);
        uint error1 = SubpredictionError(prediction1, value);
        uint error2 = SubpredictionError(prediction2, value);
        uint error3 = SubpredictionError(prediction3, value);
        at = error0;
        Unsafe.Add(ref at, 1) = error1;
        Unsafe.Add(ref at, 2) = error2;
        Unsafe.Add(ref at, 3) = error3;
        atNe = unchecked(atNe + error0);
        Unsafe.Add(ref atNe, 1) = unchecked(Unsafe.Add(ref atNe, 1) + error1);
        Unsafe.Add(ref atNe, 2) = unchecked(Unsafe.Add(ref atNe, 2) + error2);
        Unsafe.Add(ref atNe, 3) = unchecked(Unsafe.Add(ref atNe, 3) + error3);
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

    // The sub-predictions stay within about 2^41, so the difference has a magnitude.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SubpredictionError(long predicted, long value) =>
        unchecked((uint)((SmallAbs(predicted - value) + Round) >> ExtraBits));

    private static long AddBits(long value) => unchecked(value << ExtraBits);

    // 4 + (maxWeight << 24) / (x + 1), approximately and without division; x >> shift is below 64.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ErrorWeight(uint x, uint maxWeight)
    {
        int shift = 58 - System.Numerics.BitOperations.LeadingZeroCount(x + 1UL);
        shift &= ~(shift >> 31);
        return 4 + ((maxWeight * Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(DivLookup), (int)(x >> shift))) >> shift);
    }

    // Branch-free helpers; they subtract their arguments, whose difference must not overflow.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long SmallAbs(long value)
    {
        long sign = value >> 63;
        return unchecked((value ^ sign) - sign);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long SmallMin(long a, long b)
    {
        long difference = a - b;
        return b + (difference & (difference >> 63));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long SmallMax(long a, long b)
    {
        long difference = a - b;
        return a - (difference & (difference >> 63));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
