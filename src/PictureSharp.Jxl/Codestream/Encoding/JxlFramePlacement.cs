using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// Where an animation frame goes: its rectangle on a <paramref name="CanvasWidth"/> × <paramref name="CanvasHeight"/>
/// canvas, its duration in ticks and whether it is the last frame. A frame that does not cover the canvas replaces its
/// rectangle of what <paramref name="Slot"/> holds; every frame but the last is saved to <paramref name="Slot"/> after
/// blending. The slot must not be 0: a frame with a duration is saved only to a slot other than 0 (CanBeReferenced).
/// </summary>
internal sealed record JxlFramePlacement(int X0, int Y0, uint CanvasWidth, uint CanvasHeight, uint Duration, bool IsLast, uint Slot)
{
    /// <summary><paramref name="header"/> (of the frame's own size) placed on the canvas.</summary>
    public JxlFrameHeader ApplyTo(JxlFrameHeader header)
    {
        bool partial = X0 > 0 || Y0 > 0 || X0 + (long)header.Width < CanvasWidth || Y0 + (long)header.Height < CanvasHeight;
        return header with
        {
            CustomSizeOrOrigin = partial,
            X0 = X0,
            Y0 = Y0,
            IsPartial = partial,
            Blending = JxlBlendingInfo.Replace with { Source = Slot },
            Duration = Duration,
            IsLast = IsLast,
            SaveAsReference = IsLast ? 0 : Slot,
        };
    }
}
