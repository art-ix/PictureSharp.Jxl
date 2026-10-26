namespace PictureSharp.Formats.Jxl.Codestream;

/// <summary>
/// Custom XYB inverse matrix and upsampling weights. Null arrays mean the defaults of the standard, which the pixel
/// decoders of later versions supply. Ported from libjxl v0.12.0 lib/jxl/image_metadata.cc (CustomTransformData,
/// OpsinInverseMatrix), BSD-3-Clause.
/// </summary>
internal sealed record JxlCustomTransformData
{
    public static JxlCustomTransformData Default { get; } = new();

    /// <summary>Row-major 3×3 inverse opsin matrix, or null for the default.</summary>
    public float[]? OpsinInverseMatrix { get; init; }

    /// <summary>Opsin biases (3) and quantization biases (4), present with <see cref="OpsinInverseMatrix"/>.</summary>
    public float[]? OpsinBiases { get; init; }

    public float[]? QuantBiases { get; init; }

    /// <summary>Custom 2×, 4× and 8× upsampling weights (15, 55 and 210 values), or null for the defaults.</summary>
    public float[]? Upsampling2Weights { get; init; }

    public float[]? Upsampling4Weights { get; init; }

    public float[]? Upsampling8Weights { get; init; }

    public static JxlCustomTransformData Read(ref JxlBitReader reader, bool xybEncoded)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        var data = Default;
        if (xybEncoded && !reader.ReadBool())
        {
            data = data with
            {
                OpsinInverseMatrix = Floats(ref reader, 9),
                OpsinBiases = Floats(ref reader, 3),
                QuantBiases = Floats(ref reader, 4)
            };
        }

        uint mask = reader.ReadBits(3);
        return data with
        {
            Upsampling2Weights = (mask & 1) != 0 ? Floats(ref reader, 15) : null,
            Upsampling4Weights = (mask & 2) != 0 ? Floats(ref reader, 55) : null,
            Upsampling8Weights = (mask & 4) != 0 ? Floats(ref reader, 210) : null
        };
    }

    private static float[] Floats(ref JxlBitReader reader, int count)
    {
        float[] values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = JxlFields.F16(ref reader);
        }

        return values;
    }
}
