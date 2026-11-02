using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

/// <summary>
/// The spot colour channels of an image, rendered into the colour as libjxl's SpotColorStage does
/// (render_pipeline/stage_spot.cc, v0.12.0, BSD-3-Clause): one mix per channel in extra-channel order,
/// <c>p = mix × colour + (1 − mix) × p</c> with <c>mix = opacity × sample</c> (<see cref="JxlColorOutput.MixSpotRow"/>).
/// XYB frames that are not blended mix in linear light inside <see cref="JxlColorOutput"/> instead.
/// </summary>
internal sealed class JxlSpotColors
{
    private const int MinBandRows = 16;

    private JxlSpotColors(int[] channels, float[][] colors)
    {
        Channels = channels;
        Colors = colors;
    }

    /// <summary>The extra-channel indices of the spot colour channels, in order.</summary>
    public int[] Channels { get; }

    /// <summary>Per spot channel: red, green, blue and opacity (the channel's F16 values).</summary>
    public float[][] Colors { get; }

    /// <summary>The image's spot colour channels, or null when it has none.</summary>
    public static JxlSpotColors? For(JxlImageMetadata metadata)
    {
        IReadOnlyList<JxlExtraChannelInfo> info = metadata.ExtraChannels;
        int[] channels = [.. Enumerable.Range(0, info.Count).Where(i => info[i].Type == JxlExtraChannelType.SpotColor)];
        return channels.Length == 0 ? null
            : new JxlSpotColors(channels, [.. channels.Select(i => info[i].SpotColor is { Count: 4 } color ? color.ToArray() : new float[4])]);
    }

    /// <summary>
    /// Mixes every spot channel into the colour planes in row bands (each pixel on its own, so identical at any degree
    /// of parallelism). A gray image (one plane, or one plane three times) is mixed once with the red value, as libjxl's
    /// gray output takes its first channel.
    /// </summary>
    public void Apply(IReadOnlyList<JxlPlane> color, IReadOnlyList<JxlPlane> extra, CancellationToken cancellationToken,
        int parallelism = JxlParallel.Sequential)
    {
        int planes = color.Count == 3 && color[1] != color[0] ? 3 : 1;
        JxlParallel.ForBands(color[0].Height, MinBandRows, parallelism, cancellationToken, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int s = 0; s < Channels.Length; s++)
                {
                    ReadOnlySpan<float> samples = extra[Channels[s]].Row(y);
                    for (int c = 0; c < planes; c++)
                    {
                        JxlColorOutput.MixSpotRow(color[c].Row(y), samples, Colors[s][c], Colors[s][3]);
                    }
                }
            }
        });
    }
}
