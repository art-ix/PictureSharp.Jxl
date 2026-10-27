using PictureSharp.Formats.Jxl.Codestream.IO;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;

namespace PictureSharp.Formats.Jxl.Codestream.Modular;

/// <summary>
/// The field distributions of the Modular group header and its transforms, shared by the reader and the encoder.
/// From libjxl v0.12.0 lib/jxl/modular/encoding/encoding.h and modular/transform/transform.h (BSD-3-Clause).
/// </summary>
internal static class JxlTransformFields
{
    public const int PredictorBits = 4;

    public static readonly JxlU32Distribution TransformCount = new(Val(0), Val(1), BitsOffset(4, 2), BitsOffset(8, 18));
    public static readonly JxlU32Distribution Id = new(Val(0), Val(1), Val(2), Val(3));
    public static readonly JxlU32Distribution BeginC = new(Bits32(3), BitsOffset(6, 8), BitsOffset(10, 72), BitsOffset(13, 1096));
    public static readonly JxlU32Distribution RctType = new(Val(6), Bits32(2), BitsOffset(4, 2), BitsOffset(6, 10));
    public static readonly JxlU32Distribution NumC = new(Val(1), Val(3), Val(4), BitsOffset(13, 1));
    public static readonly JxlU32Distribution Colors = new(Bits32(8), BitsOffset(10, 256), BitsOffset(12, 1280), BitsOffset(16, 5376));
    public static readonly JxlU32Distribution Deltas = new(Val(0), BitsOffset(8, 1), BitsOffset(10, 257), BitsOffset(16, 1281));
    public static readonly JxlU32Distribution SqueezeCount = new(Val(0), BitsOffset(4, 1), BitsOffset(6, 9), BitsOffset(8, 41));
    public static readonly JxlU32Distribution SqueezeNumC = new(Val(1), Val(2), Val(3), BitsOffset(4, 4));
}
