using PictureSharp.Formats.Jxl.Codestream.Buffers;
using System.Buffers.Binary;
using PictureSharp.Formats.Jxl.Codestream.Encoding;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Jpeg;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>Per-call transcoding orchestration and resource accounting; codec modules use BCL data only.</summary>
internal static class JxlTranscoding
{
    public static byte[] FromJpeg(ReadOnlySpan<byte> input, JxlTranscoderOptions options, CancellationToken cancellationToken)
    {
        var budget = new Budget(options.MaxMetadataBytes);
        JxlJpegData jpeg = JxlJpegReader.Read(input, options.MaxPixels, budget.Reserve, cancellationToken);
        (byte[]? icc, byte[]? exif, byte[]? xmp) = ExtractMetadata(jpeg);
        uint orientation = 1;
        if (exif is not null)
        {
            var metadata = new ImageMetadata();
            metadata.SetBlock(MetadataKinds.Exif, exif);
            if (metadata.Orientation is >= ExifOrientation.TopLeft and <= ExifOrientation.LeftBottom) orientation = (uint)metadata.Orientation;
        }
        var color = new JxlColorEncoding { ColorSpace = jpeg.Gray ? JxlColorSpace.Gray : JxlColorSpace.Rgb, WantIcc = icc is not null };
        if (icc is not null)
        {
            JxlColorSpace space = JxlMetadataWriter.RequireIccColorSpace(icc, "JPEG ICC must describe gray or RGB samples.");
            if ((space == JxlColorSpace.Gray) != jpeg.Gray) throw JxlDescriptor.Unsupported("the JPEG ICC color space does not match its components.");
        }
        byte[] reconstruction = JxlJpegReconstructionWriter.Write(jpeg, cancellationToken);
        byte[] codestream = JxlJpegEncoder.Write(jpeg, color, icc, orientation, cancellationToken, options.MaxDegreeOfParallelism);
        var boxes = new List<JxlBox> { new(JxlBoxType.JpegReconstruction, reconstruction) };
        if (exif is not null) boxes.Add(new(JxlBoxType.Exif, new byte[4].Concat(exif).ToArray()));
        if (xmp is not null) boxes.Add(new(JxlBoxType.Xmp, xmp));
        int level = (long)jpeg.Width * jpeg.Height > 1 << 28 ? 10 : JxlContainer.DefaultLevel;
        return JxlContainerWriter.Write(codestream, boxes, level);
    }

    public static byte[] ToJpeg(ReadOnlyMemory<byte> input, JxlTranscoderOptions options, CancellationToken cancellationToken)
    {
        var budget = new Budget(options.MaxMetadataBytes);
        JxlContainer container = JxlContainer.Parse(input, includeMetadata: true, cancellationToken, includeJpegReconstruction: true);
        JxlHeaders headers = JxlHeadersReader.Read(container.Codestream);
        if ((long)headers.Size.Width * headers.Size.Height > options.MaxPixels) throw JxlDescriptor.Corrupt("JPEG XL exceeds MaxPixels.");
        if (container.JpegReconstruction is not { } reconstruction) throw JxlDescriptor.Unsupported("the JPEG XL file has no JPEG reconstruction data.");
        JxlJpegData jpeg = JxlJpegReconstruction.Read(reconstruction, budget.Reserve, cancellationToken);
        // The coefficients are pooled until the JPEG is written.
        using var buffers = new JxlBufferScope();
        byte[]? icc = JxlJpegDecoder.Decode(container.Codestream, headers, jpeg, options.MaxMetadataBytes, budget.Reserve, cancellationToken,
            options.MaxDegreeOfParallelism, buffers);
        ReadOnlyMemory<byte> exif = default, xmp = default;
        bool haveExif = false, haveXmp = false;
        foreach (var box in container.MetadataBoxes)
        {
            if (box.Type == JxlBoxType.Exif && !haveExif)
            {
                if (box.Read(options.MaxMetadataBytes, budget.Reserve, cancellationToken) is not { } data || data.Length < 4)
                    throw JxlDescriptor.Corrupt("Invalid reconstruction EXIF box.");
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(data.Span);
                if (offset > data.Length - 4) throw JxlDescriptor.Corrupt("Invalid reconstruction EXIF offset.");
                exif = data[(4 + (int)offset)..]; haveExif = true;
            }
            else if (box.Type == JxlBoxType.Xmp && !haveXmp)
            {
                xmp = box.Read(options.MaxMetadataBytes, budget.Reserve, cancellationToken) ?? throw JxlDescriptor.Corrupt("Invalid reconstruction XMP box.");
                haveXmp = true;
            }
        }
        JxlJpegReconstruction.FillExternalMetadata(jpeg, icc, exif.Span, xmp.Span);
        return JxlJpegWriter.Write(jpeg, cancellationToken, options.MaxDegreeOfParallelism);
    }

    private static (byte[]? Icc, byte[]? Exif, byte[]? Xmp) ExtractMetadata(JxlJpegData jpeg)
    {
        byte[]? exif = null, xmp = null;
        using var icc = new MemoryStream();
        int chunks = 0, total = 0;
        for (int i = 0; i < jpeg.App.Count; i++)
        {
            byte[] marker = jpeg.App[i];
            if (marker[0] == 0xe2 && marker.Length >= 17 && marker.AsSpan(3, 12).SequenceEqual("ICC_PROFILE\0"u8))
            {
                if (marker[15] != chunks + 1 || marker[16] == 0 || total != 0 && total != marker[16])
                    throw JxlDescriptor.Corrupt("Invalid JPEG ICC chunk sequence.");
                total = marker[16]; chunks++; icc.Write(marker, 17, marker.Length - 17); jpeg.AppTypes[i] = 1;
            }
            else if (marker[0] == 0xe1 && marker.Length > 9 && marker.AsSpan(3, 6).SequenceEqual("Exif\0\0"u8) && exif is null)
            { exif = marker.AsSpan(9).ToArray(); jpeg.AppTypes[i] = 2; }
            else if (marker[0] == 0xe1 && marker.Length >= 32 && marker.AsSpan(3, 29).SequenceEqual("http://ns.adobe.com/xap/1.0/\0"u8) && xmp is null)
            { xmp = marker.AsSpan(32).ToArray(); jpeg.AppTypes[i] = 3; }
        }
        if (chunks != total) throw JxlDescriptor.Corrupt("Incomplete JPEG ICC chunks.");
        return (chunks == 0 ? null : icc.ToArray(), exif, xmp);
    }

    private sealed class Budget(long limit)
    {
        private long used;
        public void Reserve(long count)
        {
            if (count < 0 || count > limit - used) throw JxlDescriptor.Corrupt("Transcoder metadata exceeds MaxMetadataBytes.");
            used += count;
        }
    }
}
