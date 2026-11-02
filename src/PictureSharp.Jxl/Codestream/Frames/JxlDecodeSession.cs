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
/// <paramref name="maxFrames"/> frames are read, displayed or not.
/// </summary>
internal sealed class JxlDecodeSession(JxlCodestream codestream, JxlHeaders headers, CancellationToken cancellationToken = default,
    long maxIccBytes = JxlIccStream.MaxProfileSize, Action<long>? reserveIcc = null, bool renderSrgb = false, JxlBufferScope? buffers = null,
    int parallelism = 1, int maxFrames = int.MaxValue)
{
    private readonly JxlReferenceFrames references = new();
    private bool started;

    /// <summary>The image's ICC profile after <see cref="Decode"/>, or null when it has none.</summary>
    public byte[]? IccProfile { get; private set; }

    /// <summary>The headers of every frame after the preview, in codestream order, up to the last one.</summary>
    public static List<JxlFrameHeader> ReadFrameHeaders(JxlCodestream codestream, JxlHeaders headers, CancellationToken cancellationToken = default)
    {
        var frames = new List<JxlFrameHeader>();
        var reader = JxlFrameSectionReader.FirstFrame(codestream, headers, cancellationToken, out _);
        while (frames.Count == 0 || !frames[^1].IsLast)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JxlFrameHeader frame = JxlFrameHeaderReader.Read(ref reader, headers, preview: false);
            long start = JxlFrameSectionReader.ReadToc(ref reader, codestream, frame, out JxlToc toc);
            frames.Add(frame);
            reader = JxlFrameSectionReader.After(codestream, start, toc);
        }

        return frames;
    }

    public JxlDecodedImage Decode()
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
        // the image's colour encoding; with renderSrgb the blended canvas is converted to sRGB afterwards.
        long retained = 0;
        for (int count = 1; ; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count > maxFrames) throw JxlDescriptor.Corrupt("JPEG XL exceeds MaxFrames.");
            JxlFrameHeader frame = JxlFrameHeaderReader.Read(ref reader, headers, preview: false);
            JxlDecodeSupportPolicy.EnsureFrameSupported(frame, references);
            long start = JxlFrameSectionReader.ReadToc(ref reader, codestream, frame, out JxlToc toc);
            bool blend = JxlBlending.NeedsBlending(frame);
            if (frame.IsLast && !blend)
            {
                // A full frame that replaces everything: the still-image path, unchanged since 0.8.9.
                var decoder = new JxlFrameDecoder(frame, headers, references, cancellationToken, target, buffers: buffers, parallelism: parallelism);
                JxlFrameSectionReader.DecodeSections(codestream, frame, start, toc, decoder);
                return decoder.Finish(alpha);
            }

            if (frame.Type == JxlFrameType.Dc || frame.SaveBeforeColorTransform)
            {
                // Saved before the colour transform (patch sources, DC frames); a layer saved this way is not shown.
                var decoder = new JxlFrameDecoder(frame, headers, references, cancellationToken, target, buffers: buffers, parallelism: parallelism);
                JxlFrameSectionReader.DecodeSections(codestream, frame, start, toc, decoder);
                JxlReferenceFrame saved = decoder.Store();
                if (frame.Type == JxlFrameType.Dc) references.StoreDc(frame.DcLevel, saved);
                else references.StoreReference(frame.SaveAsReference, saved, beforeColorTransform: true);
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
                    canvas = blend ? Blend(frame, color, extra) : Copy(color, extra);
                }

                if (frame.IsLast)
                {
                    JxlWorkBudget.Rewind(mark + references.RetainedBytes(canvas));
                    return Output(canvas, frame, alpha, original, gray);
                }

                references.StoreReference(frame.SaveAsReference, canvas, beforeColorTransform: false);
                retained = references.RetainedBytes();
                JxlWorkBudget.Rewind(mark + retained);
            }

            reader = JxlFrameSectionReader.After(codestream, start, toc);
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

    // The blended canvas as the decoded image, in the image's encoding or, with renderSrgb, converted to sRGB.
    private JxlDecodedImage Output(JxlReferenceFrame canvas, JxlFrameHeader frame, int alpha, JxlColorTarget original, bool gray)
    {
        JxlPlane[] color = [.. canvas.Color];
        JxlColorEncoding encoding = JxlColorOutput.DescribeOutput(headers.Metadata, frame.ColorTransform, original);
        if (renderSrgb && headers.Metadata.XybEncoded)
        {
            encoding = JxlCanvasToSrgb.Convert(color, encoding, original.Icc, cancellationToken, parallelism);
        }

        return new JxlDecodedImage((int)headers.Size.Width, (int)headers.Size.Height, color, alpha < 0 ? null : canvas.Extra[alpha],
            gray, encoding);
    }
}
