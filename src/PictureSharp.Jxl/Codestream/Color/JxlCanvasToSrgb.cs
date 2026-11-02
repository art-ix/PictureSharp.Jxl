using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Color;

/// <summary>
/// Converts a blended XYB canvas from the image's colour encoding to sRGB in floating point (ColorConversion = ToSrgb,
/// 0.8.10). libjxl blends in the original encoding and cannot render blended XYB frames in another one; this keeps
/// one quantization, as <see cref="JxlColorTarget.Srgb"/> does for single frames. Handles the encodings the renderer
/// produces: enumerated ones and matrix/TRC or gray ICC profiles (<see cref="JxlIccTarget"/>).
/// </summary>
internal static class JxlCanvasToSrgb
{
    /// <summary>Converts <paramref name="color"/> (three planes) in place and returns the encoding of the result.</summary>
    public static JxlColorEncoding Convert(JxlPlane[] color, JxlColorEncoding encoding, JxlIccTarget? icc,
        CancellationToken cancellationToken, int parallelism)
    {
        bool gray = encoding.ColorSpace == JxlColorSpace.Gray;
        JxlColorEncoding srgb = JxlColorEncoding.Srgb with { ColorSpace = gray ? JxlColorSpace.Gray : JxlColorSpace.Rgb };
        if (!encoding.WantIcc && encoding.Gamma == 0 && encoding.TransferFunction == JxlTransferFunction.Srgb
            && (gray ? encoding.WhitePoint == JxlWhitePoint.D65 : encoding.Primaries == JxlPrimaries.Srgb && encoding.WhitePoint == JxlWhitePoint.D65))
        {
            return srgb;
        }

        Func<float, float>[] toLinear;
        float[]? matrix = null;
        if (encoding.WantIcc)
        {
            JxlIccTarget profile = icc ?? throw new InvalidOperationException("The canvas is in an ICC profile without a target.");
            toLinear = [.. profile.Curves.Select(curve => (Func<float, float>)curve.Decode)];
            if (!gray) matrix = JxlColorOutput.Invert(JxlColorOutput.Multiply(profile.FromXyzD50, JxlColorOutput.SrgbToXyzD50()));
        }
        else
        {
            Func<float, float> transfer = JxlXybInput.ToLinear(encoding);
            toLinear = [value => MathF.CopySign(transfer(Math.Abs(value)), value)];
            if (!gray && (encoding.Primaries != JxlPrimaries.Srgb || encoding.WhitePoint != JxlWhitePoint.D65))
            {
                matrix = JxlColorOutput.Invert(JxlColorOutput.SrgbToOriginal(encoding));
            }
        }

        JxlPlane[] planes = [.. color.Distinct()];
        JxlParallel.ForBands(color[0].Height, 16, parallelism, cancellationToken, (start, end) =>
        {
            Span<float> pixel = stackalloc float[3];
            for (int y = start; y < end; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (gray)
                {
                    foreach (JxlPlane plane in planes)
                    {
                        Span<float> row = plane.Row(y);
                        for (int x = 0; x < row.Length; x++) row[x] = JxlColorOutput.SrgbFromLinear(toLinear[0](row[x]));
                    }

                    continue;
                }

                Span<float> r = color[0].Row(y), g = color[1].Row(y), b = color[2].Row(y);
                for (int x = 0; x < r.Length; x++)
                {
                    pixel[0] = toLinear[0](r[x]);
                    pixel[1] = toLinear[Math.Min(1, toLinear.Length - 1)](g[x]);
                    pixel[2] = toLinear[^1](b[x]);
                    if (matrix is not null)
                    {
                        (float lr, float lg, float lb) = (pixel[0], pixel[1], pixel[2]);
                        pixel[0] = matrix[0] * lr + matrix[1] * lg + matrix[2] * lb;
                        pixel[1] = matrix[3] * lr + matrix[4] * lg + matrix[5] * lb;
                        pixel[2] = matrix[6] * lr + matrix[7] * lg + matrix[8] * lb;
                    }

                    r[x] = JxlColorOutput.SrgbFromLinear(pixel[0]);
                    g[x] = JxlColorOutput.SrgbFromLinear(pixel[1]);
                    b[x] = JxlColorOutput.SrgbFromLinear(pixel[2]);
                }
            }
        });

        return srgb;
    }
}
