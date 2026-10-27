using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Modular;

/// <summary>
/// The weighted predictor: four sub-predictions mixed by their recent errors, with errors of two rows kept. Values
/// carry three extra fractional bits. Ported from libjxl v0.12.0 lib/jxl/modular/encoding/context_predict.h
/// (weighted::State), BSD-3-Clause; the integer widths and wrap-arounds follow libjxl.
/// </summary>
internal sealed class JxlWeightedPredictor
{
    private const int ExtraBits = 3;
    private const long Round = ((1 << ExtraBits) >> 1) - 1;

    // (1 << 24) / (i + 1): division by 1–64 as a multiplication.
    private static readonly uint[] DivLookup = [.. Enumerable.Range(0, 64).Select(i => (uint)((1 << 24) / (i + 1)))];

    private readonly JxlWeightedHeader header;
    private readonly uint[][] predictionErrors;
    private readonly int[] errors;
    private readonly long[] predictions = new long[4];
    private long prediction;

    public JxlWeightedPredictor(JxlWeightedHeader header, int width)
    {
        this.header = header;
        predictionErrors = [new uint[(width + 2) * 2], new uint[(width + 2) * 2], new uint[(width + 2) * 2], new uint[(width + 2) * 2]];
        errors = new int[(width + 2) * 2];
    }

    /// <summary>Predicts the pixel at (x, y) from its neighbours; <paramref name="maxError"/> is the WP property.</summary>
    public long Predict(int x, int y, int width, long n, long w, long ne, long nw, long nn, out int maxError)
    {
        int current = (y & 1) != 0 ? 0 : width + 2;
        int previous = (y & 1) != 0 ? width + 2 : 0;
        int positionN = previous + x;
        int positionNe = x < width - 1 ? positionN + 1 : positionN;
        int positionNw = x > 0 ? positionN - 1 : positionN;
        Span<uint> weights = stackalloc uint[4];
        for (int i = 0; i < 4; i++)
        {
            uint[] errorsOf = predictionErrors[i];
            weights[i] = ErrorWeight(unchecked(errorsOf[positionN] + errorsOf[positionNe] + errorsOf[positionNw]), header.Weights[i]);
        }

        n = AddBits(n);
        w = AddBits(w);
        ne = AddBits(ne);
        nw = AddBits(nw);
        nn = AddBits(nn);

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
        predictions[0] = w + ne - n;
        predictions[1] = n - (((sumWn + errorNe) * header.P1C) >> 5);
        predictions[2] = w - (((sumWn + errorNw) * header.P2C) >> 5);
        predictions[3] = n - ((errorNw * header.P3Ca + errorN * header.P3Cb + errorNe * header.P3Cc + (nn - n) * header.P3Cd
            + (nw - w) * header.P3Ce) >> 5);
        prediction = WeightedAverage(weights);

        // When the three errors have different signs, clamp to the range of W, NE and N.
        if (((errorN ^ errorW) | (errorN ^ errorNw)) <= 0)
        {
            prediction = Math.Max(Math.Min(w, Math.Min(ne, n)), Math.Min(Math.Max(w, Math.Max(ne, n)), prediction));
        }

        return (prediction + Round) >> ExtraBits;
    }

    public void Update(long value, int x, int y, int width)
    {
        int current = (y & 1) != 0 ? 0 : width + 2;
        int previous = (y & 1) != 0 ? width + 2 : 0;
        value = AddBits(value);
        errors[current + x] = unchecked((int)(prediction - value));
        for (int i = 0; i < 4; i++)
        {
            uint error = unchecked((uint)((Math.Abs(predictions[i] - value) + Round) >> ExtraBits));
            predictionErrors[i][current + x] = error;
            // Adding the error to the NE position makes it count for the E and EE pixels of the next row.
            predictionErrors[i][previous + x + 1] = unchecked(predictionErrors[i][previous + x + 1] + error);
        }
    }

    private static long AddBits(long value) => unchecked(value << ExtraBits);

    // 4 + (maxWeight << 24) / (x + 1), approximately and without division.
    private static uint ErrorWeight(uint x, uint maxWeight)
    {
        int shift = Math.Max(63 - System.Numerics.BitOperations.LeadingZeroCount(x + 1UL) - 5, 0);
        return 4 + ((maxWeight * DivLookup[x >> shift]) >> shift);
    }

    private long WeightedAverage(Span<uint> weights)
    {
        uint sum = 0;
        foreach (uint weight in weights)
        {
            sum = unchecked(sum + weight);
        }

        int log = JxlPrefixCode.Log2Floor(sum);
        sum = 0;
        for (int i = 0; i < 4; i++)
        {
            weights[i] >>= log - 4;
            sum += weights[i];
        }

        long total = unchecked((sum >> 1) - 1);
        for (int i = 0; i < 4; i++)
        {
            total = unchecked(total + predictions[i] * weights[i]);
        }

        return unchecked(total * DivLookup[sum - 1]) >> 24;
    }
}
