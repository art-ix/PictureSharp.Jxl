using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

namespace PictureSharp.Formats.Jxl.Integration;

internal sealed partial class JxlEncoder
{
    // The XYB planes of the pixels; gray pixels have equal channels.
    private static JxlImage3F ToXyb(ReadOnlySpan<Rgba64> pixels, int width, int height, JxlXybInput input, CancellationToken cancellationToken, JxlBufferScope? buffers = null)
    {
        var xyb = new JxlImage3F(width, height, buffers);
        for (int y = 0; y < height; y++)
        {
            if ((y & 0xF) == 0) cancellationToken.ThrowIfCancellationRequested();
            Span<float> r = xyb.Row(0, y);
            Span<float> g = xyb.Row(1, y);
            Span<float> b = xyb.Row(2, y);
            ReadOnlySpan<Rgba64> row = pixels.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                r[x] = row[x].R;
                g[x] = row[x].G;
                b[x] = row[x].B;
            }

            input.Convert(r, g, b);
        }

        return xyb;
    }

    private static int[] AlphaPlane(ReadOnlySpan<Rgba64> pixels, CancellationToken cancellationToken)
    {
        int[] alpha = new int[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();
            alpha[i] = pixels[i].A;
        }

        return alpha;
    }

    // One integer plane per colour channel (one for gray) and an alpha plane when requested.
    private static (int[][] Color, int[]? Alpha) SplitPlanes(ReadOnlySpan<Rgba64> pixels, bool gray, bool alpha, CancellationToken cancellationToken)
    {
        int count = pixels.Length;
        int[][] color = gray ? [new int[count]] : [new int[count], new int[count], new int[count]];
        int[]? alphaPlane = alpha ? new int[count] : null;
        for (int i = 0; i < count; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();
            Rgba64 pixel = pixels[i];
            color[0][i] = pixel.R;
            if (!gray)
            {
                color[1][i] = pixel.G;
                color[2][i] = pixel.B;
            }

            if (alphaPlane is not null) alphaPlane[i] = pixel.A;
        }

        return (color, alphaPlane);
    }

    private static bool IsGray(ReadOnlySpan<Rgba64> pixels)
    {
        foreach (Rgba64 pixel in pixels)
        {
            if (pixel.R != pixel.G || pixel.G != pixel.B) return false;
        }

        return true;
    }
}
