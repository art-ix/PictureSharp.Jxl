using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Metadata;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Frames;

/// <summary>
/// Owns the reference/DC frames of one decode; never shared between calls. The ICC profile is limited to
/// <paramref name="maxIccBytes"/> and charged to <paramref name="reserveIcc"/> before it is allocated. With
/// <paramref name="renderSrgb"/> XYB frames are rendered as sRGB instead of the image's colour space.
/// </summary>
internal sealed class JxlDecodeSession(JxlCodestream codestream, JxlHeaders headers, CancellationToken cancellationToken = default,
    long maxIccBytes = JxlIccStream.MaxProfileSize, Action<long>? reserveIcc = null, bool renderSrgb = false, JxlBufferScope? buffers = null)
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
        JxlColorTarget target = renderSrgb ? JxlColorTarget.Srgb
            : JxlColorTarget.Profile(icc is null ? null : JxlIccTarget.TryCreate(icc, metadata.Color.ColorSpace == JxlColorSpace.Gray));
        // Reference frames (patch sources) and DC frames come first; the last frame is the image.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JxlFrameHeader frame = JxlFrameHeaderReader.Read(ref reader, headers, preview: false);
            JxlDecodeSupportPolicy.EnsureFrameSupported(frame, headers, references);
            long start = JxlFrameSectionReader.ReadToc(ref reader, codestream, frame, out JxlToc toc);
            // A reference frame saved after the colour transform can only serve blending, which is not supported.
            if (frame.Type != JxlFrameType.ReferenceOnly || frame.SaveBeforeColorTransform)
            {
                var decoder = new JxlFrameDecoder(frame, headers, references, cancellationToken, target, buffers: buffers);
                JxlFrameSectionReader.DecodeSections(codestream, frame, start, toc, decoder);
                if (frame.Type == JxlFrameType.Regular)
                {
                    return decoder.Finish(alpha);
                }

                JxlReferenceFrame saved = decoder.Store();
                if (frame.Type == JxlFrameType.Dc)
                {
                    references.StoreDc(frame.DcLevel, saved);
                }
                else
                {
                    references.StoreReference(frame.SaveAsReference, saved, frame.SaveBeforeColorTransform);
                }
            }

            reader = JxlFrameSectionReader.After(codestream, start, toc);
        }
    }
}
