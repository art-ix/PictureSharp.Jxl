using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Jpeg;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Frames;

/// <summary>The coefficient-only decode path used by JPEG reconstruction; no pixels or rendering pipeline.</summary>
internal static class JxlJpegDecoder
{
    public static byte[]? Decode(JxlCodestream codestream, JxlHeaders headers, JxlJpegData jpeg, long maxMetadataBytes,
        Action<long> reserve, CancellationToken cancellationToken, int parallelism = 1)
    {
        JxlImageMetadata metadata = headers.Metadata;
        if (metadata.XybEncoded || metadata.Animation is not null || metadata.ExtraChannels.Count != 0 || metadata.Preview is not null
            || metadata.BitDepth.FloatingPoint || metadata.BitDepth.BitsPerSample != 8)
            throw JxlDescriptor.Corrupt("The image is incompatible with JPEG reconstruction data.");
        if ((metadata.Color.ColorSpace == JxlColorSpace.Gray) != jpeg.Gray) throw JxlDescriptor.Corrupt("Reconstruction color component mismatch.");
        var reader = JxlFrameSectionReader.FirstFrame(codestream, headers, cancellationToken, out byte[]? icc, maxMetadataBytes, reserve);
        JxlFrameHeader frame = JxlFrameHeaderReader.Read(ref reader, headers, preview: false);
        var references = new JxlReferenceFrames();
        JxlDecodeSupportPolicy.EnsureFrameSupported(frame, headers, references);
        if (frame.ColorTransform == JxlColorTransform.None && !jpeg.Gray)
            throw JxlDescriptor.Unsupported("RGB JPEG reconstruction is unsupported.");
        if (!frame.IsLast || frame.Type != JxlFrameType.Regular || frame.Encoding != JxlFrameEncoding.VarDct
            || frame.ColorTransform != JxlColorTransform.YCbCr || frame.Upsampling != 1 || frame.Flags != JxlFrameFlags.SkipAdaptiveDcSmoothing
            || frame.LoopFilter.Gaborish || frame.LoopFilter.EpfIterations != 0)
            throw JxlDescriptor.Corrupt("The frame is incompatible with JPEG reconstruction.");
        if (headers.Size.Width > 65535 || headers.Size.Height > 65535) throw JxlDescriptor.Corrupt("JPEG reconstruction dimensions exceed JPEG limits.");
        jpeg.Width = (int)headers.Size.Width; jpeg.Height = (int)headers.Size.Height;
        foreach (int c in jpeg.Gray ? (ReadOnlySpan<int>)[1] : [0, 1, 2])
        {
            var component = jpeg.Components[jpeg.Gray ? 0 : c == 0 ? 1 : c == 1 ? 0 : 2];
            component.H = 1 << (frame.MaxHShift - frame.HShift(c));
            component.V = 1 << (frame.MaxVShift - frame.VShift(c));
            component.WidthBlocks = checked((int)(frame.XSizeBlocks >> frame.HShift(c)));
            component.HeightBlocks = checked((int)(frame.YSizeBlocks >> frame.VShift(c)));
            long size = (long)component.WidthBlocks * component.HeightBlocks * 64;
            if (size > Array.MaxLength) throw JxlDescriptor.Unsupported("JPEG coefficient array is too large.");
            component.Coefficients = new short[(int)size];
        }
        jpeg.ValidateSupported();
        long start = JxlFrameSectionReader.ReadToc(ref reader, codestream, frame, out JxlToc toc);
        var decoder = new JxlFrameDecoder(frame, headers, references, cancellationToken, jpeg: jpeg, parallelism: parallelism);
        JxlFrameSectionReader.DecodeSections(codestream, frame, start, toc, decoder);
        return icc;
    }
}
