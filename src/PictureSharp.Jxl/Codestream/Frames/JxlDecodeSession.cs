using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Metadata;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Rendering;

namespace PictureSharp.Formats.Jxl.Codestream.Frames;

/// <summary>
/// Owns the reference/DC frames of one decode; never shared between calls. The ICC profile is limited to
/// <paramref name="maxIccBytes"/> and charged to <paramref name="reserveIcc"/> before it is allocated. With
/// <paramref name="renderSrgb"/> XYB frames are rendered as sRGB instead of the image's colour space. Groups and rendering
/// use up to <paramref name="parallelism"/> threads (1 = single-threaded, -1 = automatic). At most
/// <paramref name="maxFrames"/> frames are read, displayed or not. <paramref name="layerSink"/> receives every displayed
/// frame that is blended or saved, at its own size before blending (0.8.11 layers); the last frame on the still-image
/// path is not passed to it.
/// </summary>
internal sealed class JxlDecodeSession(JxlCodestream codestream, JxlHeaders headers, CancellationToken cancellationToken = default,
    long maxIccBytes = JxlIccStream.MaxProfileSize, Action<long>? reserveIcc = null, bool renderSrgb = false, JxlBufferScope? buffers = null,
    int parallelism = 1, int maxFrames = int.MaxValue, Action<JxlFrameHeader, JxlDecodedImage>? layerSink = null)
{
    private readonly JxlReferenceFrames references = new();
    // The image has spot colour channels, rendered into the displayed colour (0.8.16).
    private readonly bool spots = JxlSpotColors.For(headers.Metadata) is not null;
    private bool started;
    private bool finished;
    private int alpha;
    private int count;
    // libjxl's visible_frame_index and nonvisible_frame_index of the current frame, which seed the noise (InitFrame).
    private (int Visible, int Nonvisible) frameIndex;
    // The bit position of the next frame header, or -1 before Open.
    private long position = -1;

    /// <summary>The token of the current step; frame readers set it before every call (0.8.13).</summary>
    public CancellationToken CancellationToken
    {
        get => cancellationToken;
        set => cancellationToken = value;
    }

    /// <summary>The image's ICC profile after <see cref="Decode"/>, or null when it has none.</summary>
    public byte[]? IccProfile { get; private set; }

    /// <summary>The headers of every frame after the preview, in codestream order, up to the last one (at most <paramref name="maxFrames"/>).</summary>
    public static List<JxlFrameHeader> ReadFrameHeaders(JxlCodestream codestream, JxlHeaders headers, CancellationToken cancellationToken = default,
        int maxFrames = int.MaxValue)
    {
        var frames = new List<JxlFrameHeader>();
        var reader = JxlFrameSectionReader.FirstFrame(codestream, headers, cancellationToken, out _);
        while (frames.Count == 0 || !frames[^1].IsLast)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frames.Count == maxFrames) throw JxlDescriptor.Corrupt("JPEG XL exceeds MaxFrames.");
            JxlFrameHeader frame = JxlFrameHeaderReader.Read(ref reader, headers, preview: false);
            long start = JxlFrameSectionReader.ReadToc(ref reader, codestream, frame, out JxlToc toc);
            frames.Add(frame);
            reader = JxlFrameSectionReader.After(codestream, start, toc);
        }

        return frames;
    }

    /// <summary>The image: the last frame, blended onto the frames before it (a still image).</summary>
    public JxlDecodedImage Decode()
    {
        foreach ((_, JxlDecodedImage image) in Frames(animation: false)) return image;
        throw new InvalidOperationException("A still image has a last frame.");
    }

    /// <summary>
    /// Decodes an animation: every displayed frame (libjxl's coalescing: the last frame and every frame with a duration)
    /// goes to <paramref name="displayed"/> as a full canvas, which must consume it before returning. When it returns
    /// false no further frame is decoded, but the remaining frame headers are still read (MaxFrames, corrupt headers).
    /// </summary>
    public void DecodeAnimation(Func<JxlFrameHeader, JxlDecodedImage, bool> displayed)
    {
        foreach ((JxlFrameHeader frame, JxlDecodedImage image) in Frames(animation: true))
        {
            if (displayed(frame, image)) continue;
            SkipRemaining();
            return;
        }
    }

    /// <summary>A frame libjxl returns when coalescing: the last one, and in animations every frame with a duration.</summary>
    public static bool IsDisplayed(JxlFrameHeader frame) =>
        frame.Type is JxlFrameType.Regular or JxlFrameType.SkipProgressive && (frame.IsLast || frame.Duration > 0);

    // CanBeReferenced (frame_header.h): only such frames are saved to their slot.
    private static bool CanBeReferenced(JxlFrameHeader frame) =>
        !frame.IsLast && frame.Type != JxlFrameType.Dc && (frame.Duration == 0 || frame.SaveAsReference != 0);

    /// <summary>
    /// Checks the image and reads the ICC profile (<see cref="IccProfile"/>); <see cref="Frames"/> calls it when it was not
    /// called before. Frame readers (0.8.13) call it first to describe the sequence.
    /// </summary>
    public void Open()
    {
        if (position >= 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        headers.Size.Validate();
        alpha = JxlDecodeSupportPolicy.EnsureImageSupported(headers.Metadata);
        var reader = JxlFrameSectionReader.FirstFrame(codestream, headers, cancellationToken, out byte[]? icc, maxIccBytes, reserveIcc);
        IccProfile = icc;
        position = reader.BitPosition;
        // The preview frame counts as a frame that is not displayed.
        frameIndex = (0, headers.Metadata.Preview is null ? 0 : 1);
    }

    /// <summary>
    /// The displayed frames in order (0.8.13: resumable, so frame readers can pull them one at a time). Still images
    /// (<paramref name="animation"/> false) yield only the last frame. A yielded image is valid until the next
    /// <c>MoveNext</c> or disposal of the enumerator, which return its buffers; the reference slots, the retained charge
    /// and the position of the next frame are kept between calls. <see cref="CancellationToken"/> is read at every step.
    /// </summary>
    public IEnumerable<(JxlFrameHeader Frame, JxlDecodedImage Image)> Frames(bool animation)
    {
        if (started) throw new InvalidOperationException("A decode session can only be consumed once.");
        started = true;
        Open();
        return Iterate(animation);
    }

    /// <summary>
    /// After the enumeration of <see cref="Frames"/> stopped early (FirstFrameOnly): reads the headers and TOCs of the
    /// remaining frames without decoding them, as MaxFrames still applies to every stored frame.
    /// </summary>
    public void SkipRemaining()
    {
        if (finished) return;
        finished = true;
        var reader = Reader(position);
        SkipFrames(reader, count);
    }

    private IEnumerable<(JxlFrameHeader Frame, JxlDecodedImage Image)> Iterate(bool animation)
    {
        JxlImageMetadata metadata = headers.Metadata;
        bool gray = metadata.Color.ColorSpace == JxlColorSpace.Gray;
        JxlColorTarget original = JxlColorTarget.Profile(IccProfile, gray);
        JxlColorTarget target = renderSrgb ? JxlColorTarget.Srgb : original;
        // Reference, DC and layer frames come first; the last frame is the image. Like libjxl, frames are blended in
        // the image's colour encoding; with renderSrgb the blended canvas is converted to sRGB afterwards. In an
        // animation every frame with a duration is displayed as well (frames without one are layers of the next).
        // The bit reader is a ref struct: between frames (and yields) only its position is kept.
        long retained = 0;
        while (!finished)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > maxFrames) throw JxlDescriptor.Corrupt("JPEG XL exceeds MaxFrames.");
            (JxlFrameHeader frame, long start, JxlToc toc) = ReadFrame(position);
            frameIndex = IsDisplayed(frame) ? (frameIndex.Visible + 1, 0) : (frameIndex.Visible, frameIndex.Nonvisible + 1);
            // The next frame starts after this one's sections; SkipRemaining continues from there after a yield.
            position = (start + toc.TotalSize) * 8;
            finished = frame.IsLast;
            bool blend = JxlBlending.NeedsBlending(frame);
            bool shown = animation ? IsDisplayed(frame) : frame.IsLast;
            bool referenced = CanBeReferenced(frame);
            if (!animation && frame.IsLast && !blend)
            {
                // A full frame that replaces everything: the still-image path, unchanged since 0.8.9.
                var decoder = new JxlFrameDecoder(frame, headers, references, cancellationToken, target, buffers: buffers, parallelism: parallelism,
            frameIndex: frameIndex);
                JxlFrameSectionReader.DecodeSections(codestream, frame, start, toc, decoder);
                yield return (frame, decoder.Finish(alpha));
                yield break;
            }

            if (frame.Type == JxlFrameType.Dc || (frame.SaveBeforeColorTransform && !shown))
            {
                // Saved before the colour transform (patch sources, DC frames); a layer saved this way is not shown.
                Store(frame, start, toc, target);
            }
            else if (shown && !blend && (!referenced || frame.SaveBeforeColorTransform))
            {
                // An animation frame that replaces everything and is not saved as rendered: the still-image path with
                // the frame's own buffers, consumed before they are returned.
                long mark = JxlWorkBudget.Used - retained;
                using (var frameBuffers = new JxlBufferScope())
                {
                    var decoder = new JxlFrameDecoder(frame, headers, references, cancellationToken, target, buffers: frameBuffers,
                        parallelism: parallelism, frameIndex: frameIndex);
                    JxlFrameSectionReader.DecodeSections(codestream, frame, start, toc, decoder);
                    yield return (frame, decoder.Finish(alpha));
                }

                JxlWorkBudget.Rewind(mark + retained);
            }
            else
            {
                // Rendered in the image's encoding and blended (or copied) into planes that outlive the frame, whose
                // temporary memory is returned and no longer charged.
                long mark = JxlWorkBudget.Used - retained;
                JxlReferenceFrame canvas;
                // Spot colours (0.8.16) come after blending and after the frame is saved, where libjxl has already left
                // linear light (it adds FromLinear before both): the displayed canvas gets them, the slot keeps it
                // without. A layer is rendered alone (no coalescing): for XYB in linear light, as SpotColor.
                bool layer = layerSink is not null && frame.Type is JxlFrameType.Regular or JxlFrameType.SkipProgressive;
                JxlPlane[]? spotColor;
                using (var frameBuffers = new JxlBufferScope())
                {
                    var decoder = new JxlFrameDecoder(frame, headers, references, cancellationToken, original, buffers: frameBuffers,
                        parallelism: parallelism, frameIndex: frameIndex);
                    JxlFrameSectionReader.DecodeSections(codestream, frame, start, toc, decoder);
                    (JxlPlane[] color, JxlPlane[] extra, spotColor) = decoder.RenderColor(spot: spots && layer);
                    if (layer)
                    {
                        layerSink!(frame, Layer(frame, spotColor ?? color, extra, alpha, original, gray));
                    }
                    canvas = blend ? Blend(frame, color, extra) : Copy(color, extra);
                }

                bool store = referenced && !frame.SaveBeforeColorTransform;
                JxlReferenceFrame shownCanvas = spots && shown ? WithSpotColors(canvas, copy: store) : canvas;
                if (!animation && frame.IsLast)
                {
                    long spotBytes = shownCanvas.Color.Distinct().Where(plane => !canvas.Color.Contains(plane)).Sum(plane => plane.Bytes);
                    JxlWorkBudget.Rewind(mark + references.RetainedBytes(canvas) + spotBytes);
                    yield return (frame, Output(shownCanvas, frame, alpha, original, gray, copy: false));
                    yield break;
                }

                if (shown) yield return (frame, Output(shownCanvas, frame, alpha, original, gray, copy: store && !spots));
                if (store) references.StoreReference(frame.SaveAsReference, canvas, beforeColorTransform: false);
                retained = references.RetainedBytes();
                JxlWorkBudget.Rewind(mark + retained);
            }

            if (shown && referenced && frame.SaveBeforeColorTransform)
            {
                // Displayed and saved before the colour transform: decoded once more for the slot.
                Store(frame, start, toc, target);
            }
        }
    }

    private JxlBitReader Reader(long bitPosition)
    {
        var reader = new JxlBitReader(codestream);
        reader.SkipBits((ulong)bitPosition);
        return reader;
    }

    private (JxlFrameHeader Frame, long Start, JxlToc Toc) ReadFrame(long bitPosition)
    {
        var reader = Reader(bitPosition);
        JxlFrameHeader frame = JxlFrameHeaderReader.Read(ref reader, headers, preview: false);
        JxlDecodeSupportPolicy.EnsureFrameSupported(frame, references);
        long start = JxlFrameSectionReader.ReadToc(ref reader, codestream, frame, out JxlToc toc);
        return (frame, start, toc);
    }

    // Saved before the colour transform: decoded with the decode-wide buffers into its slot (or the DC levels).
    private void Store(JxlFrameHeader frame, long start, JxlToc toc, JxlColorTarget target)
    {
        var decoder = new JxlFrameDecoder(frame, headers, references, cancellationToken, target, buffers: buffers, parallelism: parallelism,
            frameIndex: frameIndex);
        JxlFrameSectionReader.DecodeSections(codestream, frame, start, toc, decoder);
        JxlReferenceFrame saved = decoder.Store();
        if (frame.Type == JxlFrameType.Dc) references.StoreDc(frame.DcLevel, saved);
        else references.StoreReference(frame.SaveAsReference, saved, beforeColorTransform: true);
    }

    // Reads the headers and TOCs of the frames after the first count ones without decoding them.
    private void SkipFrames(JxlBitReader reader, int count)
    {
        for (bool last = false; !last; )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > maxFrames) throw JxlDescriptor.Corrupt("JPEG XL exceeds MaxFrames.");
            JxlFrameHeader frame = JxlFrameHeaderReader.Read(ref reader, headers, preview: false);
            long start = JxlFrameSectionReader.ReadToc(ref reader, codestream, frame, out JxlToc toc);
            reader = JxlFrameSectionReader.After(codestream, start, toc);
            last = frame.IsLast;
        }
    }

    // BlendingStage: the frame onto its backgrounds, at the image size.
    private JxlReferenceFrame Blend(JxlFrameHeader frame, JxlPlane[] color, JxlPlane[] extra)
    {
        int width = (int)headers.Size.Width, height = (int)headers.Size.Height;
        IReadOnlyList<JxlExtraChannelInfo> info = headers.Metadata.ExtraChannels;
        IReadOnlyList<JxlPlane>? colorBackground = references.GetBackground(frame.Blending.Source, width, height)?.Color;
        var extraBackgrounds = new IReadOnlyList<JxlPlane>?[info.Count];
        for (int i = 0; i < info.Count; i++)
        {
            extraBackgrounds[i] = references.GetBackground(frame.ExtraChannelBlending[i].Source, width, height)?.Extra;
        }

        JxlPlane[] planes = JxlBlending.BlendFrame(frame, color, extra, width, height, colorBackground, extraBackgrounds, info,
            cancellationToken, parallelism, buffers: null);
        return new JxlReferenceFrame(planes[..3], planes[3..]);
    }

    // A frame saved as it is (a reference-only frame, or a full frame that replaces): copied out of the frame's buffers.
    private JxlReferenceFrame Copy(JxlPlane[] color, JxlPlane[] extra)
    {
        JxlPlane[] copies = [.. color.Select(plane => plane.Clone(cancellationToken))];
        JxlPlane[] colorPlanes = copies.Length == 3 ? copies : [copies[0], copies[0], copies[0]];
        return new JxlReferenceFrame(colorPlanes, [.. extra.Select(plane => plane.Clone(cancellationToken))]);
    }

    // The canvas colour with the spot colours rendered (libjxl's SpotColorStage after blending), on copies when the
    // canvas is kept for later frames; the extra channels are shared.
    private JxlReferenceFrame WithSpotColors(JxlReferenceFrame canvas, bool copy)
    {
        JxlPlane[] color = [.. canvas.Color];
        if (copy)
        {
            JxlPlane[] copies = [.. color.Distinct().Select(plane => plane.Clone(cancellationToken))];
            color = copies.Length == 3 ? copies : [copies[0], copies[0], copies[0]];
        }

        JxlSpotColors.For(headers.Metadata)!.Apply(color, canvas.Extra, cancellationToken, parallelism);
        return new JxlReferenceFrame(color, [.. canvas.Extra]);
    }

    // A layer as it is before blending, in the image's encoding or, with renderSrgb, converted to sRGB (on copies, as
    // the planes are blended afterwards).
    private JxlDecodedImage Layer(JxlFrameHeader frame, JxlPlane[] color, JxlPlane[] extra, int alpha, JxlColorTarget original, bool gray)
    {
        JxlColorEncoding encoding = JxlColorOutput.DescribeOutput(headers.Metadata, frame.ColorTransform, original);
        if (renderSrgb && headers.Metadata.XybEncoded)
        {
            JxlPlane[] copies = [.. color.Select(plane => plane.Clone(cancellationToken))];
            color = copies.Length == 3 ? copies : [copies[0], copies[0], copies[0]];
            encoding = JxlCanvasToSrgb.Convert(color, encoding, original.Icc, cancellationToken, parallelism);
        }

        return new JxlDecodedImage((int)frame.Width, (int)frame.Height, color, alpha < 0 ? null : extra[alpha], gray, encoding)
        {
            Premultiplied = Premultiplied(alpha),
        };
    }

    private bool Premultiplied(int alpha) => alpha >= 0 && headers.Metadata.ExtraChannels[alpha].AlphaAssociated;

    // The blended canvas as the decoded image, in the image's encoding or, with renderSrgb, converted to sRGB (on copies
    // when the canvas is kept in a slot).
    private JxlDecodedImage Output(JxlReferenceFrame canvas, JxlFrameHeader frame, int alpha, JxlColorTarget original, bool gray, bool copy)
    {
        JxlPlane[] color = [.. canvas.Color];
        JxlColorEncoding encoding = JxlColorOutput.DescribeOutput(headers.Metadata, frame.ColorTransform, original);
        if (renderSrgb && headers.Metadata.XybEncoded)
        {
            if (copy)
            {
                // A gray canvas holds one plane three times; so do its copies.
                JxlPlane[] copies = [.. color.Distinct().Select(plane => plane.Clone(cancellationToken))];
                color = copies.Length == 3 ? copies : [copies[0], copies[0], copies[0]];
            }

            encoding = JxlCanvasToSrgb.Convert(color, encoding, original.Icc, cancellationToken, parallelism);
        }

        return new JxlDecodedImage((int)headers.Size.Width, (int)headers.Size.Height, color, alpha < 0 ? null : canvas.Extra[alpha],
            gray, encoding)
        {
            Premultiplied = Premultiplied(alpha),
        };
    }
}
