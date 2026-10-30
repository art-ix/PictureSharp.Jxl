using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.Jxl.Codestream.Modular;

// The vocabulary MA trees share between decoding (JxlMaTree) and encoding (JxlModularEncoder). Ported from libjxl
// v0.12.0 lib/jxl/modular/encoding/context_predict.h and dec_ma.cc (BSD-3-Clause).

/// <summary>The contexts of the entropy code that codes an MA tree.</summary>
internal enum JxlMaTreeContext { SplitValue = 0, Property = 1, Predictor = 2, Offset = 3, MultiplierLog = 4, MultiplierBits = 5 }

/// <summary>
/// Indices of the pixel properties an MA tree decides on. N, W, NW, NE, NN and WW are the neighbours of
/// <see cref="JxlNeighbours"/>; properties from earlier channels follow <see cref="WeightedMaxError"/>.
/// </summary>
internal static class JxlMaProperty
{
    public const int Channel = 0;
    public const int Stream = 1;
    public const int Y = 2;
    public const int X = 3;
    public const int AbsN = 4;
    public const int AbsW = 5;
    public const int N = 6;
    public const int W = 7;
    /// <summary>W minus <see cref="Gradient"/> of the previous pixel in the row.</summary>
    public const int WMinusPreviousGradient = 8;
    /// <summary>W + N − NW, unclamped.</summary>
    public const int Gradient = 9;
    public const int WMinusNW = 10;
    public const int NWMinusN = 11;
    public const int NMinusNE = 12;
    public const int NMinusNN = 13;
    public const int WMinusWW = 14;
    /// <summary>The weighted predictor's largest recent error.</summary>
    public const int WeightedMaxError = 15;

    /// <summary>Properties 0–15, which do not refer to other channels.</summary>
    public const int Local = WeightedMaxError + 1;

    /// <summary>Sets the properties that are constant along row <paramref name="y"/> and resets the running gradient.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StartRow(Span<int> properties, int channel, int stream, int y)
    {
        properties[Channel] = channel;
        properties[Stream] = stream;
        properties[Y] = y;
        properties[Gradient] = 0;
    }

    /// <summary>Sets properties 3–14 of the pixel at <paramref name="x"/>; the weighted error is set by the predictor.</summary>
    /// <remarks>The neighbours are passed by value: a reference to the struct keeps the JIT from enregistering them.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLocal(Span<int> properties, int x, JxlNeighbours n)
    {
        (long w, long north, long nw, long ne, long ww, long nn) = (n.W, n.N, n.NW, n.NE, n.WW, n.NN);
        unchecked
        {
            properties[X] = x;
            properties[AbsN] = (int)(north > 0 ? north : -north);
            properties[AbsW] = (int)(w > 0 ? w : -w);
            properties[N] = (int)north;
            properties[W] = (int)w;
            // Uses the gradient of the previous pixel, so it is set before the gradient is updated.
            properties[WMinusPreviousGradient] = (int)(w - properties[Gradient]);
            properties[Gradient] = (int)(w + north - nw);
            properties[WMinusNW] = (int)(w - nw);
            properties[NWMinusN] = (int)(nw - north);
            properties[NMinusNE] = (int)(north - ne);
            properties[NMinusNN] = (int)(north - nn);
            properties[WMinusWW] = (int)(w - ww);
        }
    }

    /// <summary>Sets properties 9–14 only, for trees that decide on no other local property but the weighted error.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetDifferences(Span<int> properties, JxlNeighbours n)
    {
        (long w, long north, long nw, long ne, long ww, long nn) = (n.W, n.N, n.NW, n.NE, n.WW, n.NN);
        unchecked
        {
            properties[Gradient] = (int)(w + north - nw);
            properties[WMinusNW] = (int)(w - nw);
            properties[NWMinusN] = (int)(nw - north);
            properties[NMinusNE] = (int)(north - ne);
            properties[NMinusNN] = (int)(north - nn);
            properties[WMinusWW] = (int)(w - ww);
        }
    }

    /// <summary>W + N − NW clamped to the range of W and N (the gradient predictor).</summary>
    public static int ClampedGradient(int w, int n, int nw)
    {
        // Branch-free clamp of the exact gradient to [min, max]: nw above max gives min, below min gives max, as in
        // libjxl; photo data makes the branches unpredictable.
        long min = Math.Min(w, n);
        long max = Math.Max(w, n);
        long gradient = (long)w + n - nw;
        return (int)Math.Min(Math.Max(gradient, min), max);
    }
}

/// <summary>
/// The decoded neighbours of a pixel: west, north, north-west, north-east, west-west, north-north and
/// north-east-east, with libjxl's substitutes at the image edges.
/// </summary>
internal readonly record struct JxlNeighbours(long W, long N, long NW, long NE, long WW, long NN, long NEE)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JxlNeighbours At(int[] pixels, int width, int x, int y)
    {
        int i = y * width + x;
        long w = x > 0 ? pixels[i - 1] : y > 0 ? pixels[i - width] : 0;
        long n = y > 0 ? pixels[i - width] : w;
        long nw = x > 0 && y > 0 ? pixels[i - 1 - width] : w;
        long ne = x + 1 < width && y > 0 ? pixels[i + 1 - width] : n;
        long ww = x > 1 ? pixels[i - 2] : w;
        long nn = y > 1 ? pixels[i - 2 * width] : n;
        long nee = x + 2 < width && y > 0 ? pixels[i + 2 - width] : ne;
        return new JxlNeighbours(w, n, nw, ne, ww, nn, nee);
    }
}
