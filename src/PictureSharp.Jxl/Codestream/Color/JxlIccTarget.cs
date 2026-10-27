using System.Buffers.Binary;

namespace PictureSharp.Formats.Jxl.Codestream.Color;

/// <summary>
/// An ICC profile as an output colour space for XYB images: a matrix/TRC RGB profile (colourants and tone curves) or a
/// gray profile with a gray tone curve, the profiles monitor and camera images use. libjxl converts to any profile
/// with a colour management system; profiles with lookup tables (A2B/B2A) are not handled here.
/// </summary>
/// <remarks>Implements ICC.1:2022 sections 7 (header and tag table) and 10.6, 10.18 and 10.31 (curv, para, XYZ).</remarks>
internal sealed class JxlIccTarget
{
    private JxlIccTarget(bool gray, float[] toXyzD50, float[] fromXyzD50, JxlToneCurve[] curves)
    {
        IsGray = gray;
        ToXyzD50 = toXyzD50;
        FromXyzD50 = fromXyzD50;
        Curves = curves;
    }

    public bool IsGray { get; }
    /// <summary>Linear device RGB to XYZ (D50): the colourants as columns, row-major; unused for gray.</summary>
    public float[] ToXyzD50 { get; }
    /// <summary>XYZ (D50, the profile connection space) to linear device RGB, row-major; unused for gray.</summary>
    public float[] FromXyzD50 { get; }
    /// <summary>The inverse tone curves, one per output channel.</summary>
    public JxlToneCurve[] Curves { get; }

    /// <summary>The output colour space, or null when the profile is not a usable matrix/TRC or gray profile.</summary>
    public static JxlIccTarget? TryCreate(ReadOnlySpan<byte> icc, bool gray)
    {
        if (icc.Length < 132)
        {
            return null;
        }

        uint declared = BinaryPrimitives.ReadUInt32BigEndian(icc);
        if (declared < 132 || declared > icc.Length)
        {
            return null;
        }

        icc = icc[..(int)declared];
        if (!icc[36..40].SequenceEqual("acsp"u8) || !icc[20..24].SequenceEqual("XYZ "u8)
            || !icc[16..20].SequenceEqual(gray ? "GRAY"u8 : "RGB "u8))
        {
            return null;
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(icc[128..]);
        if (count > (uint)(icc.Length - 132) / 12)
        {
            return null;
        }

        var tags = new Dictionary<uint, (int Offset, int Size)>();
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = icc.Slice(132 + i * 12, 12);
            uint signature = BinaryPrimitives.ReadUInt32BigEndian(entry);
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            // Lookup-table transforms take precedence over the matrix in colour management systems.
            if ((signature >> 8) is 0x413242 or 0x423241 or 0x443242 or 0x423244)
            {
                return null;
            }

            if (offset > icc.Length || size > icc.Length - offset)
            {
                return null;
            }

            tags.TryAdd(signature, ((int)offset, (int)size));
        }

        byte[] profile = icc.ToArray();
        ReadOnlySpan<byte> Data(string name) =>
            tags.TryGetValue(Signature(name), out var tag) ? profile.AsSpan(tag.Offset, tag.Size) : default;

        if (gray)
        {
            return JxlToneCurve.TryCreate(Data("kTRC")) is { } curve ? new JxlIccTarget(true, [], [], [curve]) : null;
        }

        float[]? red = Xyz(Data("rXYZ"));
        float[]? green = Xyz(Data("gXYZ"));
        float[]? blue = Xyz(Data("bXYZ"));
        if (red is null || green is null || blue is null
            || JxlToneCurve.TryCreate(Data("rTRC")) is not { } r || JxlToneCurve.TryCreate(Data("gTRC")) is not { } g
            || JxlToneCurve.TryCreate(Data("bTRC")) is not { } b)
        {
            return null;
        }

        double[] m = [red[0], green[0], blue[0], red[1], green[1], blue[1], red[2], green[2], blue[2]];
        float[]? inverse = Invert(m);
        return inverse is null ? null : new JxlIccTarget(false, [.. m.Select(value => (float)value)], inverse, [r, g, b]);
    }

    private static float[]? Xyz(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 20 || !tag[..4].SequenceEqual("XYZ "u8))
        {
            return null;
        }

        return [S15Fixed16(tag[8..]), S15Fixed16(tag[12..]), S15Fixed16(tag[16..])];
    }

    internal static float S15Fixed16(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadInt32BigEndian(data) / 65536.0f;

    private static uint Signature(string name) => (uint)(name[0] << 24 | name[1] << 16 | name[2] << 8 | name[3]);

    private static float[]? Invert(double[] m)
    {
        double[] t = new double[9];
        double determinant = JxlMatrix3.Adjugate(m, t);
        if (!(Math.Abs(determinant) > 1e-6))
        {
            return null;
        }

        float[] inverse = [.. t.Select(value => (float)(value / determinant))];
        return inverse.All(float.IsFinite) ? inverse : null;
    }
}

/// <summary>
/// The inverse of an ICC tone curve (linear light to encoded values on [0, 1]): a power function exactly, a sampled
/// or parametric curve through its forward values at 4096 or the table's points, interpolated linearly between them.
/// </summary>
internal sealed class JxlToneCurve
{
    private const int Samples = 4096;
    private const int Buckets = 1024;
    private readonly float gamma;
    private readonly float[]? forward;
    private readonly int[]? buckets;

    private JxlToneCurve(float gamma, float[]? forward)
    {
        this.gamma = gamma;
        this.forward = forward;
        if (forward is null)
        {
            return;
        }

        // buckets[k] is the last sample whose value is at most k / Buckets, to start the search there.
        buckets = new int[Buckets + 1];
        int index = 0;
        for (int k = 0; k <= Buckets; k++)
        {
            float bound = (float)k / Buckets;
            while (index + 1 < forward.Length && forward[index + 1] <= bound) index++;
            buckets[k] = index;
        }
    }

    /// <summary>The encoded value of linear <paramref name="value"/>, clamped to [0, 1].</summary>
    public float Encode(float value)
    {
        value = Math.Clamp(value, 0.0f, 1.0f);
        if (forward is null)
        {
            return gamma == 1.0f ? value : MathF.Pow(value, 1.0f / gamma);
        }

        if (value <= forward[0]) return 0.0f;
        int last = forward.Length - 1;
        if (value >= forward[last]) return 1.0f;
        int i = buckets![(int)(value * Buckets)];
        while (i < last - 1 && forward[i + 1] <= value) i++;
        float low = forward[i];
        float high = forward[i + 1];
        float fraction = high > low ? (value - low) / (high - low) : 0.0f;
        return (i + fraction) / last;
    }

    /// <summary>The linear value of encoded <paramref name="value"/> on [0, 1]: the curve itself, interpolated.</summary>
    public float Decode(float value)
    {
        value = Math.Clamp(value, 0.0f, 1.0f);
        if (forward is null)
        {
            return gamma == 1.0f ? value : MathF.Pow(value, gamma);
        }

        float position = value * (forward.Length - 1);
        int i = Math.Min((int)position, forward.Length - 2);
        float fraction = position - i;
        return forward[i] + (forward[i + 1] - forward[i]) * fraction;
    }

    public static JxlToneCurve? TryCreate(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 12)
        {
            return null;
        }

        if (tag[..4].SequenceEqual("curv"u8))
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
            if (count > (uint)(tag.Length - 12) / 2)
            {
                return null;
            }

            if (count <= 1)
            {
                float exponent = count == 0 ? 1.0f : BinaryPrimitives.ReadUInt16BigEndian(tag[12..]) / 256.0f;
                return exponent > 0 ? new JxlToneCurve(exponent, null) : null;
            }

            float[] table = new float[count];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = BinaryPrimitives.ReadUInt16BigEndian(tag[(12 + i * 2)..]) / 65535.0f;
            }

            return Monotonic(table);
        }

        if (!tag[..4].SequenceEqual("para"u8))
        {
            return null;
        }

        int type = BinaryPrimitives.ReadUInt16BigEndian(tag[8..]);
        int parameters = type switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };
        if (parameters < 0 || tag.Length < 12 + parameters * 4)
        {
            return null;
        }

        double[] p = new double[7];
        for (int i = 0; i < parameters; i++)
        {
            p[i] = JxlIccTarget.S15Fixed16(tag[(12 + i * 4)..]);
        }

        if (!(p[0] > 0))
        {
            return null;
        }

        if (type == 0)
        {
            return new JxlToneCurve((float)p[0], null);
        }

        float[] samples = new float[Samples + 1];
        for (int i = 0; i <= Samples; i++)
        {
            samples[i] = (float)Math.Clamp(Parametric(type, p, (double)i / Samples), 0.0, 1.0);
        }

        return Monotonic(samples);
    }

    // ICC.1:2022 table 68.
    private static double Parametric(int type, double[] p, double x)
    {
        (double g, double a, double b, double c, double d, double e, double f) = (p[0], p[1], p[2], p[3], p[4], p[5], p[6]);
        double Power(double v) => v > 0 ? Math.Pow(v, g) : 0;
        return type switch
        {
            1 => x >= -b / a ? Power(a * x + b) : 0,
            2 => x >= -b / a ? Power(a * x + b) + c : c,
            3 => x >= d ? Power(a * x + b) : c * x,
            _ => x >= d ? Power(a * x + b) + e : c * x + f,
        };
    }

    private static JxlToneCurve? Monotonic(float[] forward)
    {
        for (int i = 1; i < forward.Length; i++)
        {
            if (!(forward[i] >= forward[i - 1]))
            {
                return null;
            }
        }

        return forward[^1] > forward[0] ? new JxlToneCurve(0, forward) : null;
    }
}
