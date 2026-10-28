using PictureSharp.Formats.Jxl.Codestream.Buffers;
namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// An approximate inverse of the decoder's Gaborish smoothing, applied to XYB before the transforms. Ported from libjxl
/// v0.12.0 lib/jxl/enc_gaborish.cc (BSD-3-Clause).
/// </summary>
internal static class JxlGaborish
{
    public static void Inverse(JxlImage3F image, CancellationToken cancellationToken)
    {
        // Optimized with butteraugli on the whole system rather than as a mathematical inverse.
        ReadOnlySpan<float> k = [-0.09495815671340026f, -0.041031725066768575f, 0.013710004822696948f, 0.006510206083837737f,
            -0.0014789063378272242f];
        double sum = Math.Max(1e-5, 1.0 + 4 * (k[0] + k[1] + k[2] + k[4] + 2 * k[3]));
        float normalize = (float)(1.0 / sum);
        var weights = new JxlSymmetric5Weights(normalize, normalize * k[0], normalize * k[2], normalize * k[1], normalize * k[4], normalize * k[3]);
        for (int c = 0; c < 3; c++)
        {
            using var buffers = new JxlBufferScope();
            JxlConvolution.Symmetric5(image.Planes[c], image.Width, image.Height, weights, cancellationToken, buffers)
                .AsSpan(0, image.Width * image.Height).CopyTo(image.Planes[c]);
        }
    }
}
