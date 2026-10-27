namespace PictureSharp.Formats.Jxl.Codestream.Model;

// Ported from libjxl v0.12.0 lib/jxl/frame_header.h (Passes) and dec_modular.cc (BSD-3-Clause).

/// <summary>Progressive passes: shifts of the VarDCT coefficients and the downsampling reached after some passes.</summary>
internal sealed class JxlPasses(uint count, uint[] shift, uint[] downsample, uint[] lastPass)
{
    public const int MaxPasses = 11;

    public uint Count { get; } = count;

    public IReadOnlyList<uint> Shift { get; } = shift.Snapshot();

    public IReadOnlyList<uint> Downsample { get; } = downsample.Snapshot();

    public IReadOnlyList<uint> LastPass { get; } = lastPass.Snapshot();

    public static JxlPasses Single { get; } = new(1, [0], [], []);

    /// <summary>The range of channel shifts (min of horizontal and vertical) that Modular pass <paramref name="pass"/> codes.</summary>
    public (int MinShift, int MaxShift) DownsamplingBracket(int pass)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)pass, Count);
        int maxShift = 2;
        int minShift = 3;
        for (int i = 0; ; i++)
        {
            for (int j = 0; j < Downsample.Count; j++)
            {
                if (i == LastPass[j])
                {
                    minShift = Downsample[j] switch { 8 => 3, 4 => 2, 2 => 1, _ => 0 };
                }
            }

            if (i == Count - 1)
            {
                minShift = 0;
            }

            if (i == pass)
            {
                return (minShift, maxShift);
            }

            maxShift = minShift - 1;
        }
    }
}
