using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlExtraChannelInfo;

namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>Reads JxlExtraChannelInfo using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlExtraChannelInfoReader
{
    public static JxlExtraChannelInfo Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        var type = (JxlExtraChannelType)JxlFields.Enum(ref reader, JxlFields.Bits(0, 1, 2, 3, 4, 5, 6, 16), "extra channel type");
        JxlBitDepth bitDepth = JxlBitDepthReader.Read(ref reader);
        uint dimShift = JxlFields.U32(ref reader, Val(0), Val(3), Val(4), BitsOffset(3, 1));
        if (dimShift > 3)
        {
            throw JxlDescriptor.Corrupt($"Extra channel dim_shift {dimShift} is too large.");
        }

        var info = new JxlExtraChannelInfo { Type = type, BitDepth = bitDepth, DimShift = dimShift, Name = JxlFields.Name(ref reader) };
        switch (type)
        {
            case JxlExtraChannelType.Alpha:
                return info with { AlphaAssociated = reader.ReadBool() };
            case JxlExtraChannelType.SpotColor:
                return info with { SpotColor = JxlFields.F16Array(ref reader, 4) };
            case JxlExtraChannelType.Cfa:
                return info with { CfaChannel = JxlFields.U32(ref reader, Val(1), Bits32(2), BitsOffset(4, 3), BitsOffset(8, 19)) };
            default:
                return info;
        }
    }
}
