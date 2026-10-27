using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlImageMetadata;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlImageMetadata using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlImageMetadataReader
{
    public static JxlImageMetadata Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        var metadata = Default;
        bool extraFields = reader.ReadBool();
        if (extraFields)
        {
            metadata = metadata with { Orientation = reader.ReadBits(3) + 1 };
            if (reader.ReadBool())
            {
                metadata = metadata with { IntrinsicSize = JxlSizeHeaderReader.Read(ref reader) };
            }

            if (reader.ReadBool())
            {
                metadata = metadata with { Preview = JxlPreviewHeaderReader.Read(ref reader) };
            }

            if (reader.ReadBool())
            {
                metadata = metadata with { Animation = JxlAnimationHeaderReader.Read(ref reader) };
            }
        }

        metadata = metadata with { BitDepth = JxlBitDepthReader.Read(ref reader), Modular16BitBufferSufficient = reader.ReadBool() };
        uint count = JxlFields.U32(ref reader, Val(0), Val(1), BitsOffset(4, 2), BitsOffset(12, 1));
        // Each channel takes at least one bit, so a count beyond the remaining input is truncated, not allocated.
        reader.EnsureRemaining(count);

        var channels = new JxlExtraChannelInfo[count];
        for (int i = 0; i < channels.Length; i++)
        {
            channels[i] = JxlExtraChannelInfoReader.Read(ref reader);
        }

        metadata = metadata with { ExtraChannels = channels, XybEncoded = reader.ReadBool(), Color = JxlColorEncodingReader.Read(ref reader) };
        if (extraFields)
        {
            metadata = metadata with { ToneMapping = JxlToneMappingReader.Read(ref reader) };
        }

        return metadata with { Extensions = JxlFields.SkipExtensions(ref reader) };
    }
}
