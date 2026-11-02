namespace PictureSharp.Formats.Jxl.Codestream.Model;

/// <summary>
/// The noise LUT of a frame: noise strength at 8 intensities. Ported from libjxl v0.12.0 noise.h and dec_noise.cc
/// (BSD-3-Clause).
/// </summary>
internal sealed class JxlNoiseParameters
{
    public const int Points = 8;
    public const float Precision = 1024.0f;

    public JxlNoiseParameters(float[] lut)
    {
        if (lut.Length != Points) throw new ArgumentException("The noise LUT has 8 entries.", nameof(lut));
        Lut = lut;
    }

    public float[] Lut { get; }

    /// <summary>Whether any entry is large enough to be rendered (libjxl skips the stage otherwise).</summary>
    public bool HasAny => Lut.Any(value => MathF.Abs(value) > 1e-3f);
}
