using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.VarDct;

internal enum JxlQuantMode { Library, Identity, Dct2, Dct4, Dct4X8, Afv, Dct, Raw }

/// <summary>Distance bands of a DCT quantisation weight table (DctQuantWeightParams).</summary>
internal sealed record JxlDctQuantParams(float[][] Bands)
{
    public const int MaxBands = 17;

    public int Count => Bands[0].Length;

    public static JxlDctQuantParams Read(ref JxlBitReader reader)
    {
        int count = (int)reader.ReadBits(4) + 1;
        float[][] bands = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            bands[c] = new float[count];
            for (int i = 0; i < count; i++)
            {
                bands[c][i] = JxlFields.F16(ref reader);
            }

            if (bands[c][0] < JxlQuantWeights.AlmostZero)
            {
                throw JxlDescriptor.Corrupt("Distance band seed is too small.");
            }

            bands[c][0] *= 64.0f;
        }

        return new JxlDctQuantParams(bands);
    }
}

/// <summary>How one quantisation table is defined (QuantEncoding); the arrays used depend on the mode.</summary>
internal sealed record JxlQuantEncoding(JxlQuantMode Mode)
{
    public JxlDctQuantParams? DctParams { get; init; }

    public JxlDctQuantParams? Afv4X4Params { get; init; }

    /// <summary>Per channel: identity, DCT2, DCT4 (2), DCT4×8 (1) or AFV (9) weights or multipliers.</summary>
    public float[][] Weights { get; init; } = [];

    public int[]? RawTable { get; init; }

    public float RawDenominator { get; init; } = 1.0f / (8 * 255);
}

/// <summary>
/// The dequantisation matrices of a frame: the DC quantisation steps and, per quantisation table, the AC weights
/// (DequantMatrices). Ported from libjxl v0.12.0 lib/jxl/quant_weights.cc and quant_weights.h (BSD-3-Clause).
/// </summary>
internal sealed class JxlQuantWeights
{
    public const float AlmostZero = 1e-8f;
    public const int Tables = JxlFrameHeader.QuantTables;
    private const int N = JxlFrameHeader.BlockDim;
    // A placeholder for the DC weight, which AC dequantisation never uses.
    private const float UnusedWeight = 0xBAD;

    // Blocks covered by each quantisation table, in the first and second dimension of its weights.
    private static ReadOnlySpan<byte> RequiredSizeX => [1, 1, 1, 1, 2, 4, 1, 1, 2, 1, 1, 8, 4, 16, 8, 32, 16];

    private static ReadOnlySpan<byte> RequiredSizeY => [1, 1, 1, 1, 2, 4, 2, 4, 4, 1, 1, 8, 8, 16, 16, 32, 32];

    // AcStrategy → quantisation table.
    private static ReadOnlySpan<byte> StrategyTable => [0, 1, 2, 3, 4, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 10, 10, 11, 12, 12, 13, 14, 14, 15, 16, 16];

    private static readonly JxlQuantEncoding[] Library = CreateLibrary();

    private readonly float[]?[] tables = new float[Tables][];
    private readonly float[]?[] inverseTables = new float[Tables][];
    private JxlQuantEncoding[] encodings = LibraryEncodings();

    public int[] JpegTable => encodings[0] is { Mode: JxlQuantMode.Raw, RawTable: { } table } encoding
        && Math.Abs(encoding.RawDenominator - 1.0f / (8 * 255)) <= 1e-8f
        ? table : throw JxlDescriptor.Corrupt("The reconstruction quantization table is not a JPEG table.");

    public float[] DcQuant { get; } = [1.0f / 4096.0f, 1.0f / 512.0f, 1.0f / 256.0f];

    /// <summary>Reads the DC quantisation steps from the LF global section (DequantMatrices::DecodeDC).</summary>
    public void ReadDc(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return;
        }

        for (int c = 0; c < 3; c++)
        {
            DcQuant[c] = JxlFields.F16(ref reader) * (1.0f / 128.0f);
            if (DcQuant[c] < AlmostZero)
            {
                throw JxlDescriptor.Corrupt("Invalid DC quantisation factor.");
            }

        }
    }

    /// <summary>
    /// Reads the AC table encodings from the HF global section; <paramref name="readRaw"/> decodes the Modular image of
    /// a raw table (table index, width, height) into three planes.
    /// </summary>
    public void Read(ref JxlBitReader reader, RawTableReader readRaw)
    {
        encodings = LibraryEncodings();
        Array.Clear(tables);
        Array.Clear(inverseTables);
        if (reader.ReadBool())
        {
            return;
        }

        for (int i = 0; i < Tables; i++)
        {
            encodings[i] = ReadEncoding(ref reader, i, readRaw);
        }
    }

    // Every table taken from the library.
    private static JxlQuantEncoding[] LibraryEncodings() => [.. Enumerable.Repeat(new JxlQuantEncoding(JxlQuantMode.Library), Tables)];

    public delegate int[][] RawTableReader(ref JxlBitReader reader, int index, int width, int height);

    /// <summary>The weights of <paramref name="strategy"/> for its three channels, each covering the whole varblock.</summary>
    public float[] Matrix(JxlAcStrategyType strategy)
    {
        int table = StrategyTable[(int)strategy];
        return tables[table] ??= Compute(encodings[table].Mode == JxlQuantMode.Library ? Library[table] : encodings[table], table);
    }

    /// <summary>
    /// The quantization weights of <paramref name="strategy"/> (the reciprocals of <see cref="Matrix"/>, InvMatrix), with
    /// zeros at the lowest frequencies, which the DC codes.
    /// </summary>
    public float[] InverseMatrix(JxlAcStrategyType strategy)
    {
        int table = StrategyTable[(int)strategy];
        if (inverseTables[table] is { } cached) return cached;
        float[] weights = Weights(encodings[table].Mode == JxlQuantMode.Library ? Library[table] : encodings[table], table);
        int xs = Math.Max(RequiredSizeX[table], RequiredSizeY[table]);
        int ys = Math.Min(RequiredSizeX[table], RequiredSizeY[table]);
        int size = weights.Length / 3;
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < ys; y++)
            {
                weights.AsSpan(c * size + y * N * xs, xs).Clear();
            }
        }

        return inverseTables[table] = weights;
    }

    private static JxlQuantEncoding ReadEncoding(ref JxlBitReader reader, int index, RawTableReader readRaw)
    {
        int blocks = RequiredSizeX[index] * RequiredSizeY[index];
        var mode = (JxlQuantMode)reader.ReadBits(3);
        if (mode is JxlQuantMode.Identity or JxlQuantMode.Dct2 or JxlQuantMode.Dct4 or JxlQuantMode.Dct4X8 or JxlQuantMode.Afv && blocks != 1)
        {
            throw JxlDescriptor.Corrupt("Invalid quantisation table mode.");
        }

        switch (mode)
        {
            case JxlQuantMode.Library:
                return new JxlQuantEncoding(mode);
            case JxlQuantMode.Identity or JxlQuantMode.Dct2:
                return new JxlQuantEncoding(mode) { Weights = ReadWeights(ref reader, mode == JxlQuantMode.Identity ? 3 : 6, check: true, scaled: 6) };
            case JxlQuantMode.Dct4 or JxlQuantMode.Dct4X8:
            {
                float[][] multipliers = ReadWeights(ref reader, mode == JxlQuantMode.Dct4 ? 2 : 1, check: true, scaled: 0);
                return new JxlQuantEncoding(mode) { Weights = multipliers, DctParams = JxlDctQuantParams.Read(ref reader) };
            }
            case JxlQuantMode.Afv:
            {
                float[][] weights = ReadWeights(ref reader, 9, check: false, scaled: 6);
                JxlDctQuantParams dct = JxlDctQuantParams.Read(ref reader);
                return new JxlQuantEncoding(mode) { Weights = weights, DctParams = dct, Afv4X4Params = JxlDctQuantParams.Read(ref reader) };
            }
            case JxlQuantMode.Dct:
                return new JxlQuantEncoding(mode) { DctParams = JxlDctQuantParams.Read(ref reader) };
            default:
            {
                float denominator = JxlFields.F16(ref reader);
                if (denominator < AlmostZero)
                {
                    throw JxlDescriptor.Corrupt("Invalid raw quantisation table denominator.");
                }

                int width = 8 * RequiredSizeX[index];
                int height = 8 * RequiredSizeY[index];
                int[][] planes = readRaw(ref reader, index, width, height);
                int[] table = new int[3 * width * height];
                for (int c = 0; c < 3; c++)
                {
                    planes[c].CopyTo(table, c * width * height);
                }

                return table.Any(value => value <= 0)
                    ? throw JxlDescriptor.Corrupt("Invalid raw quantisation table.")
                    : new JxlQuantEncoding(mode) { RawTable = table, RawDenominator = denominator };
            }
        }
    }

    private static float[][] ReadWeights(ref JxlBitReader reader, int count, bool check, int scaled)
    {
        float[][] weights = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            weights[c] = new float[count];
            for (int i = 0; i < count; i++)
            {
                weights[c][i] = JxlFields.F16(ref reader);
                if (check && Math.Abs(weights[c][i]) < AlmostZero)
                {
                    throw JxlDescriptor.Corrupt("A quantisation weight is too small.");
                }

                if (i < scaled)
                {
                    weights[c][i] *= 64;
                }
            }
        }

        return weights;
    }

    /// <summary>The dequantisation multipliers (1 / weight) of a table (ComputeQuantTable).</summary>
    private static float[] Compute(JxlQuantEncoding encoding, int table) => Invert(Weights(encoding, table));

    private static float[] Weights(JxlQuantEncoding encoding, int table)
    {
        int rows = N * RequiredSizeX[table];
        int columns = N * RequiredSizeY[table];
        float[] weights = new float[3 * rows * columns];
        switch (encoding.Mode)
        {
            case JxlQuantMode.Identity:
                IdentityWeights(encoding.Weights, weights);
                break;
            case JxlQuantMode.Dct2:
                Dct2Weights(encoding.Weights, weights);
                break;
            case JxlQuantMode.Dct4:
                Dct4Weights(encoding, weights);
                break;
            case JxlQuantMode.Dct4X8:
                Dct4X8Weights(encoding, weights);
                break;
            case JxlQuantMode.Dct:
                DctWeights(rows, columns, encoding.DctParams!).CopyTo(weights, 0);
                break;
            case JxlQuantMode.Raw:
                RawWeights(encoding, weights);
                break;
            case JxlQuantMode.Afv:
                AfvWeights(encoding, weights, rows * columns);
                break;
            default:
                throw new InvalidOperationException("Library encodings are resolved before computing a table.");
        }

        return weights;
    }

    private static float[] Invert(float[] weights)
    {
        float[] result = new float[weights.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] >= 1.0f / AlmostZero || weights[i] < AlmostZero)
            {
                throw JxlDescriptor.Corrupt("Invalid quantisation table.");
            }

            result[i] = 1.0f / weights[i];
        }

        return result;
    }

    private static void IdentityWeights(float[][] identity, float[] weights)
    {
        for (int c = 0; c < 3; c++)
        {
            weights.AsSpan(64 * c, 64).Fill(identity[c][0]);
            weights[64 * c + 1] = identity[c][1];
            weights[64 * c + 8] = identity[c][1];
            weights[64 * c + 9] = identity[c][2];
        }
    }

    // A 4×4 DCT table upsampled to 8×8, with the lowest frequencies divided by the multipliers.
    private static void Dct4Weights(JxlQuantEncoding encoding, float[] weights)
    {
        const int num = N * N;
        float[] weights4 = DctWeights(4, 4, encoding.DctParams!);
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    weights[c * num + y * N + x] = weights4[c * 16 + (y / 2) * 4 + (x / 2)];
                }
            }

            weights[c * num + 1] /= encoding.Weights[c][0];
            weights[c * num + N] /= encoding.Weights[c][0];
            weights[c * num + N + 1] /= encoding.Weights[c][1];
        }
    }

    // A 4×8 DCT table stretched vertically to 8×8, with the vertical lowest frequency divided by the multiplier.
    private static void Dct4X8Weights(JxlQuantEncoding encoding, float[] weights)
    {
        const int num = N * N;
        float[] weights4X8 = DctWeights(4, 8, encoding.DctParams!);
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    weights[c * num + y * N + x] = weights4X8[c * 32 + (y / 2) * 8 + x];
                }
            }

            weights[c * num + N] /= encoding.Weights[c][0];
        }
    }

    private static void RawWeights(JxlQuantEncoding encoding, float[] weights)
    {
        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] = 1.0f / (encoding.RawDenominator * encoding.RawTable![i]);
        }
    }

    private static void Dct2Weights(float[][] dct2, float[] weights)
    {
        for (int c = 0; c < 3; c++)
        {
            int start = c * 64;
            weights[start] = UnusedWeight;
            weights[start + 1] = weights[start + 8] = dct2[c][0];
            weights[start + 9] = dct2[c][1];
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    weights[start + y * 8 + x + 2] = dct2[c][2];
                    weights[start + (y + 2) * 8 + x] = dct2[c][2];
                    weights[start + (y + 2) * 8 + x + 2] = dct2[c][3];
                }
            }

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    weights[start + y * 8 + x + 4] = dct2[c][4];
                    weights[start + (y + 4) * 8 + x] = dct2[c][4];
                    weights[start + (y + 4) * 8 + x + 4] = dct2[c][5];
                }
            }
        }
    }

    private static float Mult(float v) => v > 0.0f ? 1.0f + v : 1.0f / (1.0f - v);

    /// <summary>Weights of a rows×columns DCT interpolated between distance bands (GetQuantWeights).</summary>
    private static float[] DctWeights(int rows, int columns, JxlDctQuantParams parameters)
    {
        int numBands = parameters.Count;
        float[] output = new float[3 * rows * columns];
        Span<float> bands = stackalloc float[JxlDctQuantParams.MaxBands];
        for (int c = 0; c < 3; c++)
        {
            bands.Clear();
            bands[0] = parameters.Bands[c][0];
            if (bands[0] < AlmostZero)
            {
                throw JxlDescriptor.Corrupt("Invalid distance bands.");
            }

            for (int i = 1; i < numBands; i++)
            {
                bands[i] = bands[i - 1] * Mult(parameters.Bands[c][i]);
                if (bands[i] < AlmostZero)
                {
                    throw JxlDescriptor.Corrupt("Invalid distance bands.");
                }
            }

            float scale = (numBands - 1) / (JxlDct.Sqrt2 + 1e-6f);
            float columnScale = scale / (columns - 1);
            float rowScale = scale / (rows - 1);
            for (int y = 0; y < rows; y++)
            {
                float dy = y * rowScale;
                float dy2 = dy * dy;
                for (int x = 0; x < columns; x++)
                {
                    float dx = x * columnScale;
                    float distance = MathF.Sqrt(JxlFastMath.Fma(dx, dx, dy2));
                    float weight = bands[0];
                    if (numBands != 1)
                    {
                        int index = (int)distance;
                        float a = bands[index];
                        weight = a * JxlFastMath.FastPowf(bands[index + 1] / a, distance - index);
                    }

                    output[c * columns * rows + y * columns + x] = weight;
                }
            }
        }

        return output;
    }

    private static void AfvWeights(JxlQuantEncoding encoding, float[] weights, int num)
    {
        ReadOnlySpan<float> frequencies =
        [
            0xBAD, 0xBAD, (float)0.8517778890324296, (float)5.37778436506804, 0xBAD, 0xBAD, (float)4.734747904497923,
            (float)5.449245381693219, (float)1.6598270267479331, 4, (float)7.275749096817861, (float)10.423227632456525,
            (float)2.662932286148962, (float)7.630657783650829, (float)8.962388608184032, (float)12.97166202570235,
        ];
        float[] weights4X8 = DctWeights(4, 8, encoding.DctParams!);
        float[] weights4X4 = DctWeights(4, 4, encoding.Afv4X4Params!);
        const float lo = (float)0.8517778890324296;
        const float hi = 12.97166202570235f - lo + 1e-6f;
        Span<float> bands = stackalloc float[4];
        for (int c = 0; c < 3; c++)
        {
            float[] afv = encoding.Weights[c];
            bands[0] = afv[5];
            if (bands[0] < AlmostZero)
            {
                throw JxlDescriptor.Corrupt("Invalid AFV bands.");
            }

            for (int i = 1; i < 4; i++)
            {
                bands[i] = bands[i - 1] * Mult(afv[i + 5]);
                if (bands[i] < AlmostZero)
                {
                    throw JxlDescriptor.Corrupt("Invalid AFV bands.");
                }
            }

            int start = c * 64;
            weights[start] = 1;
            weights[start + 8] = afv[0];      // (0, 1)
            weights[start + 1] = afv[1];      // (1, 0)
            weights[start + 16] = afv[2];     // (0, 2)
            weights[start + 2] = afv[3];      // (2, 0)
            weights[start + 18] = afv[4];     // (2, 2)
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    if (x < 2 && y < 2)
                    {
                        continue;
                    }

                    float position = (frequencies[y * 4 + x] - lo) * 3 / hi;
                    int index = (int)position;
                    if (index + 1 >= 4)
                    {
                        throw JxlDescriptor.Corrupt("Invalid AFV interpolation.");
                    }

                    weights[start + 2 * y * 8 + 2 * x] = bands[index] * JxlFastMath.FastPowf(bands[index + 1] / bands[index], position - index);
                }
            }

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    if (x != 0 || y != 0)
                    {
                        weights[c * num + (2 * y + 1) * 8 + x] = weights4X8[c * 32 + y * 8 + x];
                    }
                }
            }

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    if (x != 0 || y != 0)
                    {
                        weights[c * num + 2 * y * 8 + 2 * x + 1] = weights4X4[c * 16 + y * 4 + x];
                    }
                }
            }
        }
    }

    private static JxlQuantEncoding Dct(int count, params double[] bands) => new(JxlQuantMode.Dct)
    {
        DctParams = Params(count, bands),
    };

    private static JxlDctQuantParams Params(int count, double[] bands) =>
        new([.. Enumerable.Range(0, 3).Select(c => bands.Skip(c * count).Take(count).Select(v => (float)v).ToArray())]);

    private static float[][] Rows(int count, params double[] values) =>
        [.. Enumerable.Range(0, 3).Select(c => values.Skip(c * count).Take(count).Select(v => (float)v).ToArray())];

    /// <summary>The default tables (DequantMatricesLibraryDef), with values rounded to float as libjxl's V().</summary>
    private static JxlQuantEncoding[] CreateLibrary()
    {
        JxlDctQuantParams dct4X4 = Params(4, [2200.0, 0.0, 0.0, 0.0, 392.0, 0.0, 0.0, 0.0, 112.0, -0.25, -0.25, -0.5]);
        JxlDctQuantParams dct4X8 = Params(4,
        [
            2198.050556016380522, -0.96269623020744692, -0.76194253026666783, -0.6551140670773547,
            764.3655248643528689, -0.92630200888366945, -0.9675229603596517, -0.27845290869168118,
            527.107573587542228, -1.4594385811273854, -1.450082094097871593, -1.5843722511996204,
        ]);
        double[] large =
        [
            -1.025, -0.78, -0.65012, -0.19041574084286472, -0.20819395464, -0.421064, -0.32733845535848671,
            -0.3041958212306401, -0.3633036457487539, -0.35660379990111464, -0.3443074455424403, -0.33699592683512467,
            -0.30180866526242109, -0.27321683125358037,
            -1.2, -1.2, -0.8, -0.7, -0.7, -0.4, -0.5,
        ];
        JxlQuantEncoding Large(double x, double y, double b) =>
            Dct(8, [x, .. large[..7], y, .. large[7..14], b, .. large[14..]]);

        return
        [
            Dct(6, 3150.0, 0.0, -0.4, -0.4, -0.4, -2.0, 560.0, 0.0, -0.3, -0.3, -0.3, -0.3, 512.0, -2.0, -1.0, 0.0, -1.0, -2.0),
            new JxlQuantEncoding(JxlQuantMode.Identity) { Weights = Rows(3, 280.0, 3160.0, 3160.0, 60.0, 864.0, 864.0, 18.0, 200.0, 200.0) },
            new JxlQuantEncoding(JxlQuantMode.Dct2)
            {
                Weights = Rows(6, 3840.0, 2560.0, 1280.0, 640.0, 480.0, 300.0, 960.0, 640.0, 320.0, 180.0, 140.0, 120.0, 640.0,
                    320.0, 128.0, 64.0, 32.0, 16.0),
            },
            new JxlQuantEncoding(JxlQuantMode.Dct4) { DctParams = dct4X4, Weights = Rows(2, 1, 1, 1, 1, 1, 1) },
            Dct(7,
                8996.8725711814115328, -1.3000777393353804, -0.49424529824571225, -0.439093774457103443, -0.6350101832695744,
                -0.90177264050827612, -1.6162099239887414,
                3191.48366296844234752, -0.67424582104194355, -0.80745813428471001, -0.44925837484843441, -0.35865440981033403,
                -0.31322389111877305, -0.37615025315725483,
                1157.50408145487200256, -2.0531423165804414, -1.4, -0.50687130033378396, -0.42708730624733904,
                -1.4856834539296244, -4.9209142884401604),
            Dct(8,
                15718.40830982518931456, -1.025, -0.98, -0.9012, -0.4, -0.48819395464, -0.421064, -0.27,
                7305.7636810695983104, -0.8041958212306401, -0.7633036457487539, -0.55660379990111464, -0.49785304658857626,
                -0.43699592683512467, -0.40180866526242109, -0.27321683125358037,
                3803.53173721215041536, -3.060733579805728, -2.0413270132490346, -2.0235650159727417, -0.5495389509954993, -0.4,
                -0.4, -0.3),
            Dct(7,
                7240.7734393502, -0.7, -0.7, -0.2, -0.2, -0.2, -0.5,
                1448.15468787004, -0.5, -0.5, -0.5, -0.2, -0.2, -0.2,
                506.854140754517, -1.4, -0.2, -0.5, -0.5, -1.5, -3.6),
            Dct(8,
                16283.2494710648897, -1.7812845336559429, -1.6309059012653515, -1.0382179034313539, -0.85, -0.7, -0.9,
                -1.2360638576849587,
                5089.15750884921511936, -0.320049391452786891, -0.35362849922161446, -0.30340000000000003, -0.61, -0.5, -0.5,
                -0.6,
                3397.77603275308720128, -0.321327362693153371, -0.34507619223117997, -0.70340000000000003, -0.9, -1.0, -1.0,
                -1.1754605576265209),
            Dct(8,
                13844.97076442300573, -0.97113799999999995, -0.658, -0.42026, -0.22712, -0.2206, -0.226, -0.6,
                4798.964084220744293, -0.61125308982767057, -0.83770786552491361, -0.79014862079498627, -0.2692727459704829,
                -0.38272769465388551, -0.22924222653091453, -0.20719098826199578,
                1807.236946760964614, -1.2, -1.2, -0.7, -0.7, -0.7, -0.4, -0.5),
            new JxlQuantEncoding(JxlQuantMode.Dct4X8) { DctParams = dct4X8, Weights = Rows(1, 1.0, 1.0, 1.0) },
            new JxlQuantEncoding(JxlQuantMode.Afv)
            {
                DctParams = dct4X8,
                Afv4X4Params = dct4X4,
                Weights = Rows(9,
                    3072.0, 3072.0, 256.0, 256.0, 256.0, 414.0, 0.0, 0.0, 0.0,
                    1024.0, 1024.0, 50, 50, 50, 58.0, 0.0, 0.0, 0.0,
                    384.0, 384.0, 12.0, 12.0, 12.0, 22.0, -0.25, -0.25, -0.25),
            },
            Large(0.9 * 26629.073922049845, 0.9 * 9311.3238710010046, 0.9 * 4992.2486445538634),
            Large(0.65 * 23629.073922049845, 0.65 * 8611.3238710010046, 0.65 * 4492.2486445538634),
            Large(1.8 * 26629.073922049845, 1.8 * 9311.3238710010046, 1.8 * 4992.2486445538634),
            Large(1.3 * 23629.073922049845, 1.3 * 8611.3238710010046, 1.3 * 4492.2486445538634),
            Large(3.6 * 26629.073922049845, 3.6 * 9311.3238710010046, 3.6 * 4992.2486445538634),
            Large(2.6 * 23629.073922049845, 2.6 * 8611.3238710010046, 2.6 * 4492.2486445538634),
        ];
    }
}
