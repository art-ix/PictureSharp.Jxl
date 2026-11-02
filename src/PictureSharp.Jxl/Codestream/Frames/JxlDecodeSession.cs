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
    private bool started;

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
    public JxlDecodedImage Decode() => Run(displayed: null)!;

    /// <summary>
    /// Decodes an animation: every displayed frame (libjxl's coalescing: the last frame and every frame with a duration)
    /// goes to <paramref name="displayed"/> as a full canvas, which must consume it before returning. When it returns
    /// false no further frame is decoded, but the remaining frame headers are still read (MaxFrames, corrupt headers).
    /// </summary>
    public void DecodeAnimation(Func<JxlFrameHeader, JxlDecodedImage, bool> displayed) => Run(displayed);

    /// <summary>A frame libjxl returns when coalescing: the last one, and in animations every frame with a duration.</summary>
    public static bool IsDisplayed(JxlFrameHeader frame) =>
        frame.Type is JxlFrameType.Regular or JxlFrameType.SkipProgressive && (frame.IsLast || frame.Duration > 0);

    // CanBeReferenced (frame_header.h): only such frames are saved to their slot.
    private static bool CanBeReferenced(JxlFrameHeader frame) =>
        !frame.IsLast && frame.Type != JxlFrameType.Dc && (frame.Duration == 0 || frame.SaveAsReference != 0);

    private JxlDecodedImage? Run(Func<JxlFrameHeader, JxlDecodedImage, bool>? displayed)
    {
        if (started) throw new InvalidOperationException("A decode session can only be consumed once.");
        started = true;
        cancellationToken.ThrowIfCancellationRequested();
        headers.Size.Validate();
        JxlImageMetadata metadata = headers.Metadata;
        int alpha = JxlDecodeSupportPolicy.EnsureImageSupported(metadata);
        var reader = JxlFrameSectionReader.FirstFrame(codestream, headers, cancellationToken, out byte[]? icc, maxIccBytes, reserveIcc);
        IccProfile = icc;
        bool gray = metadata.Color.ColorSpace == JxlColorSpace.Gray;
        JxlColorTarget original = JxlColorTarget.Profile(icc, gray);
        JxlColorTarget target = renderSrgb ? JxlColorTarget.Srgb : original;
        // Reference, DC and layer frames come first; the last frame is the image. Like libjxl, frames are blended in
        // the image's colour encoding; with renderSrgb the blended canvas is converted to sRGB afterwards. In an
        // animation every frame with a duration is displayed as well (frames without one are layers of the next).
        long retained = 0;
        for (int count = 1; ; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count > maxFrames) throw JxlDescriptor.Corrupt("JPEG XL exceeds MaxFrames.");
            JxlFrameHeader frame = JxlFrameHeaderReader.Read(ref reader, headers, preview: false);
            JxlDecodeSupportPolicy.EnsureFrameSupported(frame, references);
            long start = JxlFrameSectionReader.ReadToc(ref reader, codestream, frame, out JxlToc toc);
            bool blend = JxlBlending.NeedsBlending(frame);
            bool shown = displayed is null ? frame.IsLast : IsDisplayed(frame);
            bool referenced = CanBeReferenced(frame);
            bool more = true;
            if (displayed is null && frame.IsLast && !blend)
            {
                // A full frame that replaces everything: the still-image path, unchanged since 0.8.9.
                var decoder = new JxlFrameDecoder(frame, headers, references, cancellationToken, target, buffers: buffers, parallelism: parallelism);
                JxlFrameSectionReader.DecodeSections(codestream, frame, start, toc, decoder);
                return decoder.Finish(alpha);
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
                        parallelism: parallelism);
                    JxlFrameSectionReader.DecodeSections(codestream, frame, start, toc, decoder);
                    more = displayed!(frame, decoder.Finish(alpha));
                }

                JxlWorkBudget.Rewind(mark + retained);
            }
            else
            {
                // Rendered in the image's encoding and blended (or copied) into planes that outlive the frame, whose
                // temporary memory is returned and no longer charged.
                long mark = JxlWorkBudget.Used - retained;
                JxlReferenceFrame canvas;
                using (var frameBuffers = new JxlBufferScope())
                {
                    var decoder = new JxlFrameDecoder(frame, headers, references, cancellationToken, original, buffers: frameBuffers,
                        parallelism: parallelism);
                    JxlFrameSectionReader.DecodeSections(codestream, frame, start, toc, decoder);
                    (JxlPlane[] color, JxlPlane[] extra) = decoder.RenderColor();
                    if (layerSink is not null && frame.Type is JxlFrameType.Regular or JxlFrameType.SkipProgressive)
                    {
                        layerSink(frame, Layer(frame, color, extra, alpha, original, gray));
                    }
                    canvas = blend ? Blend(frame, color, extra) : Copy(color, extra);
                }

                bool store = referenced && !frame.SaveBeforeColorTransform;
                if (displayed is null && frame.IsLast)
                {
                    JxlWorkBudget.Rewind(mark + references.RetainedBytes(canvas));
                    return Output(canvas, frame, alpha, original, gray, copy: false);
                }

                if (shown) more = displayed!(frame, Output(canvas, frame, alpha, original, gray, copy: store));
                if (store) references.StoreReference(frame.SaveAsReference, canvas, beforeColorTransform: false);
                retained = references.RetainedBytes();
                JxlWorkBudget.Rewind(mark + retained);
            }

            if (shown && referenced && frame.SaveBeforeColorTransform && more)
            {
                // Displayed and saved before the colour transform: decoded once more for the slot.
                Store(frame, start, toc, target);
            }

            reader = JxlFrameSectionReader.After(codestream, start, toc);
            if (frame.IsLast) return null;
            if (!more)
            {
                // FirstFrameOnly: the rest is not decoded, but MaxFrames still applies to every stored frame.
                SkipFrames(reader, count);
                return null;
            }
        }
    }

    // Saved before the colour transform: decoded with the decode-wide buffers into its slot (or the DC levels).
    private void Store(JxlFrameHeader frame, long start, JxlToc toc, JxlColorTarget target)
    {
        var decoder = new JxlFrameDecoder(frame, headers, references, cancellationToken, target, buffers: buffers, parallelism: parallelism);
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

        return new JxlDecodedImage((int)frame.Width, (int)frame.Height, color, alpha < 0 ? null : extra[alpha], gray, encoding);
    }

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
            gray, encoding);
    }
}
