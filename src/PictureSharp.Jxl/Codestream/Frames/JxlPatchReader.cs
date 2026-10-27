using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Frames;

/// <summary>Patch dictionary parsing from libjxl v0.12.0 dec_patch_dictionary.cc (BSD-3-Clause).</summary>
internal static class JxlPatchReader
{
    private const int Contexts = 10;
    private const int NumRefPatchContext = 0;
    private const int ReferenceFrameContext = 1;
    private const int PatchSizeContext = 2;
    private const int ReferencePositionContext = 3;
    private const int PatchPositionContext = 4;
    private const int BlendModeContext = 5;
    private const int OffsetContext = 6;
    private const int CountContext = 7;
    private const int AlphaChannelContext = 8;
    private const int ClampContext = 9;

    public static JxlPatch[] Read(ref JxlBitReader reader, long xSize, long ySize, int extraChannels, JxlReferenceFrames references)
    {
        JxlEntropyCode code = JxlEntropyCode.Read(ref reader, Contexts);
        using var symbols = new JxlSymbolReader(code, ref reader);
        long maxReferences = 1024 + xSize * ySize / 4;
        long maxPatches = maxReferences * 4;
        uint count = symbols.Read(NumRefPatchContext, ref reader);
        if (count > maxReferences)
        {
            throw JxlDescriptor.Corrupt("Too many patches in dictionary.");
        }

        var patches = new List<JxlPatch>();
        int stride = extraChannels + 1;
        for (int id = 0; id < count; id++)
        {
            uint reference = symbols.Read(ReferenceFrameContext, ref reader);
            JxlReferenceFrame frame = references.GetPatch(reference);

            uint x0 = symbols.Read(ReferencePositionContext, ref reader);
            uint y0 = symbols.Read(ReferencePositionContext, ref reader);
            long width = symbols.Read(PatchSizeContext, ref reader) + 1L;
            long height = symbols.Read(PatchSizeContext, ref reader) + 1L;
            if (x0 + width > frame.Color[0].Width || y0 + height > frame.Color[0].Height)
            {
                throw JxlDescriptor.Corrupt("Invalid position in the reference frame of a patch.");
            }

            long instances = symbols.Read(CountContext, ref reader) + 1L;
            if (instances > maxPatches || patches.Count + instances > maxPatches)
            {
                throw JxlDescriptor.Corrupt("Too many patches in dictionary.");
            }

            long x = 0;
            long y = 0;
            for (int i = 0; i < instances; i++)
            {
                if (i == 0)
                {
                    x = symbols.Read(PatchPositionContext, ref reader);
                    y = symbols.Read(PatchPositionContext, ref reader);
                }
                else
                {
                    x += UnpackSigned(symbols.Read(OffsetContext, ref reader));
                    y += UnpackSigned(symbols.Read(OffsetContext, ref reader));
                    if (x < 0 || y < 0)
                    {
                        throw JxlDescriptor.Corrupt("Invalid patch: negative coordinate.");
                    }
                }

                if (x + width > xSize || y + height > ySize)
                {
                    throw JxlDescriptor.Corrupt("Invalid patch position.");
                }

                var blendings = new JxlPatchBlending[stride];
                for (int j = 0; j < stride; j++)
                {
                    uint mode = symbols.Read(BlendModeContext, ref reader);
                    if (mode > (uint)JxlPatchBlendMode.AlphaWeightedAddBelow)
                    {
                        throw JxlDescriptor.Corrupt("Invalid patch blend mode.");
                    }

                    var blendMode = (JxlPatchBlendMode)mode;
                    int alpha = 0;
                    if (UsesAlpha(blendMode) && extraChannels > 1)
                    {
                        uint channel = symbols.Read(AlphaChannelContext, ref reader);
                        alpha = channel < extraChannels ? (int)channel : throw JxlDescriptor.Corrupt("Invalid alpha channel for blending.");
                    }

                    bool clamp = (UsesAlpha(blendMode) || blendMode == JxlPatchBlendMode.Mul) && symbols.Read(ClampContext, ref reader) != 0;
                    blendings[j] = new JxlPatchBlending(blendMode, alpha, clamp);
                }

                patches.Add(new JxlPatch((int)reference, (int)x0, (int)y0, (int)width, (int)height, (int)x, (int)y, blendings));
            }
        }

        return symbols.IsFinalStateValid ? [.. patches] : throw JxlDescriptor.Corrupt("Invalid ANS stream of the patch dictionary.");
    }

    /// <summary>Blends every patch onto the frame, clipped to its planes (PatchDictionary::AddOneRow).</summary>
    private static bool UsesAlpha(JxlPatchBlendMode mode) => mode is JxlPatchBlendMode.BlendAbove or JxlPatchBlendMode.BlendBelow
        or JxlPatchBlendMode.AlphaWeightedAddAbove or JxlPatchBlendMode.AlphaWeightedAddBelow;

    private static int UnpackSigned(uint value) => (int)((value >> 1) ^ (((~value) & 1) - 1));

    // PerformBlending for one pixel: extra channels first (with the alpha before blending), then colour.
}
