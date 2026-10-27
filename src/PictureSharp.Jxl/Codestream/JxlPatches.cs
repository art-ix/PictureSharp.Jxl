namespace PictureSharp.Formats.Jxl.Codestream;

internal enum JxlPatchBlendMode
{
    None = 0,
    Replace = 1,
    Add = 2,
    Mul = 3,
    BlendAbove = 4,
    BlendBelow = 5,
    AlphaWeightedAddAbove = 6,
    AlphaWeightedAddBelow = 7,
}

internal readonly record struct JxlPatchBlending(JxlPatchBlendMode Mode, int AlphaChannel, bool Clamp);

/// <summary>One placement of a reference-frame rectangle, with the blending of colour and of each extra channel.</summary>
internal sealed record JxlPatch(int Reference, int ReferenceX, int ReferenceY, int Width, int Height, int X, int Y, JxlPatchBlending[] Blendings);

/// <summary>A frame saved for later frames: colour planes (before the colour transform) and extra channels.</summary>
internal sealed record JxlReferenceFrame(JxlPlane[] Color, JxlPlane[] Extra);

/// <summary>
/// The patch dictionary of a frame and its application. Ported from libjxl v0.12.0
/// lib/jxl/dec_patch_dictionary.cc, lib/jxl/blending.cc (PerformBlending) and lib/jxl/alpha.cc (BSD-3-Clause).
/// Patches are applied in dictionary order, which is the order libjxl's per-row patch tree yields for each pixel.
/// </summary>
internal static class JxlPatches
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

    public static JxlPatch[] Read(ref JxlBitReader reader, long xSize, long ySize, int extraChannels, JxlReferenceFrame?[] references)
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
            JxlReferenceFrame? frame = reference < references.Length ? references[reference] : null;
            if (frame is null)
            {
                throw JxlDescriptor.Corrupt("Invalid reference frame of a patch.");
            }

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
    public static void Apply(JxlPatch[] patches, JxlPlane[] color, JxlPlane[] extra, JxlReferenceFrame?[] references, JxlExtraChannelInfo[] info)
    {
        int channels = 3 + extra.Length;
        JxlPlane[] planes = [.. color, .. extra];
        bool hasAlpha = info.Any(channel => channel.Type == JxlExtraChannelType.Alpha);
        float[] bg = new float[channels];
        float[] fg = new float[channels];
        float[] result = new float[channels];
        foreach (JxlPatch patch in patches)
        {
            JxlReferenceFrame reference = references[patch.Reference]!;
            JxlPlane[] source = [.. reference.Color, .. reference.Extra];
            int width = Math.Min(patch.Width, planes[0].Width - patch.X);
            int height = Math.Min(patch.Height, planes[0].Height - patch.Y);
            for (int iy = 0; iy < height; iy++)
            {
                for (int ix = 0; ix < width; ix++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        bg[c] = planes[c].Row(patch.Y + iy)[patch.X + ix];
                        fg[c] = source[c].Row(patch.ReferenceY + iy)[patch.ReferenceX + ix];
                    }

                    Blend(bg, fg, result, patch.Blendings, info, hasAlpha);
                    for (int c = 0; c < channels; c++)
                    {
                        planes[c].Row(patch.Y + iy)[patch.X + ix] = result[c];
                    }
                }
            }
        }
    }

    private static bool UsesAlpha(JxlPatchBlendMode mode) => mode is JxlPatchBlendMode.BlendAbove or JxlPatchBlendMode.BlendBelow
        or JxlPatchBlendMode.AlphaWeightedAddAbove or JxlPatchBlendMode.AlphaWeightedAddBelow;

    private static int UnpackSigned(uint value) => (int)((value >> 1) ^ (((~value) & 1) - 1));

    // PerformBlending for one pixel: extra channels first (with the alpha before blending), then colour.
    private static void Blend(float[] bg, float[] fg, float[] result, JxlPatchBlending[] blendings, JxlExtraChannelInfo[] info, bool hasAlpha)
    {
        for (int i = 0; i < info.Length; i++)
        {
            JxlPatchBlending blending = blendings[1 + i];
            int c = 3 + i;
            int a = 3 + blending.AlphaChannel;
            result[c] = blending.Mode switch
            {
                JxlPatchBlendMode.Add => bg[c] + fg[c],
                JxlPatchBlendMode.BlendAbove => AlphaBlend(bg[c], bg[a], fg[c], fg[a], c == a, info[blending.AlphaChannel].AlphaAssociated, blending.Clamp),
                JxlPatchBlendMode.BlendBelow => AlphaBlend(fg[c], fg[a], bg[c], bg[a], c == a, info[blending.AlphaChannel].AlphaAssociated, blending.Clamp),
                JxlPatchBlendMode.AlphaWeightedAddAbove => c == a ? bg[c] : bg[c] + fg[c] * Clamp(fg[a], blending.Clamp),
                JxlPatchBlendMode.AlphaWeightedAddBelow => c == a ? fg[c] : fg[c] + bg[c] * Clamp(bg[a], blending.Clamp),
                JxlPatchBlendMode.Mul => bg[c] * Clamp(fg[c], blending.Clamp),
                JxlPatchBlendMode.Replace => fg[c],
                _ => bg[c],
            };
        }

        JxlPatchBlending color = blendings[0];
        int alpha = 3 + color.AlphaChannel;
        switch (color.Mode)
        {
            case JxlPatchBlendMode.Add:
            case JxlPatchBlendMode.AlphaWeightedAddAbove when !hasAlpha:
            case JxlPatchBlendMode.AlphaWeightedAddBelow when !hasAlpha:
                for (int c = 0; c < 3; c++)
                {
                    result[c] = bg[c] + fg[c];
                }

                break;
            case JxlPatchBlendMode.AlphaWeightedAddAbove:
                for (int c = 0; c < 3; c++)
                {
                    result[c] = bg[c] + fg[c] * Clamp(fg[alpha], color.Clamp);
                }

                break;
            case JxlPatchBlendMode.AlphaWeightedAddBelow:
                for (int c = 0; c < 3; c++)
                {
                    result[c] = fg[c] + bg[c] * Clamp(bg[alpha], color.Clamp);
                }

                break;
            case JxlPatchBlendMode.BlendAbove when hasAlpha:
                BlendColor(bg, fg, result, alpha, info[color.AlphaChannel].AlphaAssociated, color.Clamp);
                break;
            case JxlPatchBlendMode.BlendBelow when hasAlpha:
                BlendColor(fg, bg, result, alpha, info[color.AlphaChannel].AlphaAssociated, color.Clamp);
                break;
            case JxlPatchBlendMode.Mul:
                for (int c = 0; c < 3; c++)
                {
                    result[c] = bg[c] * Clamp(fg[c], color.Clamp);
                }

                break;
            case JxlPatchBlendMode.None:
                Array.Copy(bg, result, 3);
                break;
            default:
                Array.Copy(fg, result, 3);
                break;
        }
    }

    private static float Clamp(float value, bool clamp) => clamp ? Math.Clamp(value, 0.0f, 1.0f) : value;

    // PerformAlphaBlending of a single channel; the alpha channel itself blends as 1 - (1 - fa)(1 - ba).
    private static float AlphaBlend(float bg, float bga, float fg, float fga, bool isAlpha, bool premultiplied, bool clamp)
    {
        float fa = Clamp(fga, clamp);
        if (isAlpha)
        {
            return 1.0f - (1.0f - fa) * (1.0f - bga);
        }

        if (premultiplied)
        {
            return fg + bg * (1.0f - fa);
        }

        float newAlpha = 1.0f - (1.0f - fa) * (1.0f - bga);
        float inverse = newAlpha > 0 ? 1.0f / newAlpha : 0.0f;
        return (fg * fa + bg * bga * (1.0f - fa)) * inverse;
    }

    // PerformAlphaBlending of the colour channels together with the alpha channel of the colour blending.
    private static void BlendColor(float[] bottom, float[] top, float[] result, int alpha, bool premultiplied, bool clamp)
    {
        float fa = Clamp(top[alpha], clamp);
        float newAlpha = 1.0f - (1.0f - fa) * (1.0f - bottom[alpha]);
        float inverse = newAlpha > 0 ? 1.0f / newAlpha : 0.0f;
        for (int c = 0; c < 3; c++)
        {
            result[c] = premultiplied ? top[c] + bottom[c] * (1.0f - fa) : (top[c] * fa + bottom[c] * bottom[alpha] * (1.0f - fa)) * inverse;
        }

        result[alpha] = newAlpha;
    }
}
