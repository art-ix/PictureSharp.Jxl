using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.VarDct;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// What the encoder decides for one VarDCT frame (PassesEncoderState and PassesSharedState in libjxl): per 8×8 block
/// the varblock, quantization value and EPF sharpness, per 64×64 tile the chroma-from-luma factors, the quantizer,
/// and the quantized DC and AC coefficients.
/// </summary>
internal sealed class JxlLossyFrame
{
    public const int GroupBlocks = 32;
    public const int TileBlocks = 8;

    public JxlLossyFrame(JxlFrameHeader header, JxlLossySettings settings)
    {
        Header = header;
        Settings = settings;
        XBlocks = (int)header.XSizeBlocks;
        YBlocks = (int)header.YSizeBlocks;
        int blocks = XBlocks * YBlocks;
        Strategies = new JxlVarblock[blocks];
        QuantField = new int[blocks];
        Sharpness = new byte[blocks];
        Array.Fill(Sharpness, (byte)4);
        XTiles = DivCeil(XBlocks, TileBlocks);
        YTiles = DivCeil(YBlocks, TileBlocks);
        YToX = new sbyte[XTiles * YTiles];
        YToB = new sbyte[XTiles * YTiles];
        Dc = [new float[blocks], new float[blocks], new float[blocks]];
        Coefficients = new int[header.Groups][];
    }

    public JxlFrameHeader Header { get; set; }

    public JxlLossySettings Settings { get; }

    public int XBlocks { get; }

    public int YBlocks { get; }

    public int XTiles { get; }

    public int YTiles { get; }

    public JxlVarblock[] Strategies { get; }

    /// <summary>The coded AC quantization (1–256) of the first block of every varblock.</summary>
    public int[] QuantField { get; }

    public byte[] Sharpness { get; }

    public sbyte[] YToX { get; }

    public sbyte[] YToB { get; }

    public JxlEncoderQuantizer Quantizer { get; } = new();

    public JxlQuantWeights Matrices { get; } = new();

    public JxlBlockContextMap BlockContexts { get; set; } = JxlBlockContextMap.Default;

    /// <summary>The DC of every block of X, Y and B before quantization.</summary>
    public float[][] Dc { get; }

    /// <summary>Per group, the quantized coefficients of X, Y and B, each in varblock order (as the decoder lays them out).</summary>
    public int[][] Coefficients { get; }

    public float XQmMultiplier => MathF.Pow(1.25f, Header.XQmScale - 2.0f);

    public float BQmMultiplier => MathF.Pow(1.25f, Header.BQmScale - 2.0f);

    public static float YToXRatio(int factor) => factor * (1.0f / JxlChromaFromLumaSearch.ColorFactor);

    public static float YToBRatio(int factor) => 1.0f + factor * (1.0f / JxlChromaFromLumaSearch.ColorFactor);

    public int Tile(int bx, int by) => by / TileBlocks * XTiles + bx / TileBlocks;

    /// <summary>The first block and size of group <paramref name="group"/> in blocks.</summary>
    public (int X0, int Y0, int Width, int Height) GroupRect(int group)
    {
        (int x0, int y0) = Header.GroupOrigin(group, GroupBlocks);
        return (x0, y0, Math.Min(GroupBlocks, XBlocks - x0), Math.Min(GroupBlocks, YBlocks - y0));
    }

    /// <summary>The first block and size of DC group <paramref name="group"/> in blocks.</summary>
    public (int X0, int Y0, int Width, int Height) DcGroupRect(int group)
    {
        int dimension = Header.GroupDimension;
        (int x0, int y0) = Header.DcGroupOrigin(group, dimension);
        return (x0, y0, Math.Min(dimension, XBlocks - x0), Math.Min(dimension, YBlocks - y0));
    }

    /// <summary>The strategies used anywhere in the frame.</summary>
    public JxlAcStrategySet UsedStrategies
    {
        get
        {
            JxlAcStrategySet used = default;
            foreach (JxlVarblock varblock in Strategies) used = used.With(varblock.Type);
            return used;
        }
    }
}
