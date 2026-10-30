using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

/// <summary>
/// The last stages of a YCbCr frame with subsampled chroma and no later stage (no restoration filters, patches or
/// upsampling), rendered on demand row by row (0.8.6): the pixel writer asks each row band for its RGB rows instead
/// of the renderer keeping full-resolution chroma planes. Chroma rows are upsampled with the operations of
/// <see cref="JxlRenderPipeline.UpsampleChromaHorizontally"/> and <see cref="JxlRenderPipeline.UpsampleChromaVertically"/>
/// (horizontally first) and converted by <see cref="JxlColorOutput.YCbCrToRgbRow"/>, so the samples are those of
/// whole-plane rendering. Bands only read the planes.
/// </summary>
internal sealed class JxlYCbCrRows : JxlColorRows
{
    private readonly JxlPlane[] planes;
    private readonly int[] hShift;
    private readonly int[] vShift;

    public JxlYCbCrRows(JxlPlane[] color, int[] hShift, int[] vShift, int width, int height, CancellationToken cancellationToken = default)
    {
        (planes, this.hShift, this.vShift, Width, Height) = ([.. color], hShift, vShift, width, height);
        // The horizontal stage reads one mirrored column on each side, as UpsampleChromaHorizontally mirrors it.
        for (int c = 0; c < 3; c++)
        {
            if (hShift[c] != 0) planes[c].Mirror(1, 0, cancellationToken);
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Whether a frame's chroma can be rendered this way.</summary>
    public static bool Applies(ReadOnlySpan<int> hShift, ReadOnlySpan<int> vShift)
    {
        bool subsampled = false;
        for (int c = 0; c < 3; c++)
        {
            if (hShift[c] > 1 || vShift[c] > 1) return false;
            subsampled |= hShift[c] != 0 || vShift[c] != 0;
        }

        return subsampled;
    }

    /// <summary>The state of one row band: cached horizontally upsampled chroma rows.</summary>
    public override JxlColorRows.Band Open() => new YCbCrBand(this);

    private sealed class YCbCrBand(JxlYCbCrRows rows) : JxlColorRows.Band
    {
        // Per channel, the last two horizontally upsampled source rows and their indices.
        private readonly float[][][] cache = [[[], []], [[], []], [[], []]];
        private readonly int[][] cached = [[-1, -1], [-1, -1], [-1, -1]];
        private readonly int[] used = new int[3];

        /// <summary>Writes row <paramref name="y"/> as R, G and B (each <see cref="Width"/> samples).</summary>
        public override void Render(int y, Span<float> red, Span<float> green, Span<float> blue)
        {
            ChromaRow(0, y, red);
            ChromaRow(1, y, green);
            ChromaRow(2, y, blue);
            JxlColorOutput.YCbCrToRgbRow(red, green, blue);
        }

        // Row y of channel c at full resolution.
        private void ChromaRow(int c, int y, Span<float> output)
        {
            JxlPlane plane = rows.planes[c];
            if (rows.vShift[c] == 0)
            {
                Source(c, plane, y, -1).CopyTo(output);
                return;
            }

            // Output rows 2k and 2k + 1 mix source row k with row k - 1 or k + 1, mirrored at the edges.
            int k = y >> 1;
            int neighbour = (y & 1) == 0 ? Math.Max(k - 1, 0) : Math.Min(k + 1, plane.Height - 1);
            ReadOnlySpan<float> middle = Source(c, plane, k, -1);
            ReadOnlySpan<float> other = Source(c, plane, neighbour, k);
            for (int x = 0; x < output.Length; x++)
            {
                output[x] = JxlFastMath.Fma(other[x], 0.25f, middle[x] * 0.75f);
            }
        }

        // Source row r of channel c, horizontally upsampled when it is subsampled (Width samples); a new row replaces
        // the cached one that is not row `keep`, whose span the caller still holds.
        private ReadOnlySpan<float> Source(int c, JxlPlane plane, int r, int keep)
        {
            if (rows.hShift[c] == 0) return plane.Row(r)[..rows.Width];
            int slot = cached[c][0] == r ? 0 : cached[c][1] == r ? 1 : -1;
            if (slot < 0)
            {
                slot = cached[c][0] == keep ? 1 : cached[c][1] == keep ? 0 : 1 - used[c];
                if (cache[c][slot].Length < 2 * plane.Width) cache[c][slot] = new float[2 * plane.Width];
                float[] target = cache[c][slot];
                float[] src = plane.Data;
                int i = plane.Offset(0, r);
                for (int x = 0; x < plane.Width; x++, i++)
                {
                    float current = src[i] * 0.75f;
                    target[2 * x] = JxlFastMath.Fma(0.25f, src[i - 1], current);
                    target[2 * x + 1] = JxlFastMath.Fma(0.25f, src[i + 1], current);
                }

                cached[c][slot] = r;
            }

            used[c] = slot;
            return cache[c][slot].AsSpan(0, rows.Width);
        }
    }
}
