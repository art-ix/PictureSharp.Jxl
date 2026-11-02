using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

/// <summary>
/// The image features a decoded frame draws on top of its pixels: splines before upsampling, noise after it. The
/// frame indices seed the noise as libjxl's visible_frame_index and nonvisible_frame_index; the ratios are the colour
/// correlation base (YtoXRatio(0), YtoBRatio(0)). Encoder-side renders pass none.
/// </summary>
internal sealed record JxlRenderFeatures(JxlNoiseParameters? Noise, JxlSplines? Splines, int VisibleIndex, int NonvisibleIndex,
    float YToX, float YToB)
{
    public bool Any => Noise is not null || Splines is not null;
}
