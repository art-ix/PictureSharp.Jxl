using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// The patches of a frame: a reference-only Modular frame with the patch image (XYB quantized to integers and coded
/// losslessly, as libjxl's RoundtripPatchFrame does), the dictionary that adds it at every position, and its
/// subtraction from the image before encoding. Ported from libjxl v0.12.0 lib/jxl/enc_patch_dictionary.cc and
/// enc_modular.cc (BSD-3-Clause).
/// </summary>
internal sealed class JxlPatchEncoder
{
    /// <summary>The reference slot libjxl stores patch images in (kPatchFrameReferenceId).</summary>
    public const int ReferenceSlot = 3;

    private const int Contexts = 10;

    private readonly List<JxlPatchEntry> entries;
    private readonly JxlImage3F reference;
    private readonly int[][] samples;
    private readonly float[] dcQuant = new float[3];

    private JxlPatchEncoder(List<JxlPatchEntry> entries, JxlImage3F reference, float distance)
    {
        this.entries = entries;
        this.reference = reference;
        // XYB is quantized with these factors and then treated as a lossless image.
        ReadOnlySpan<float> factors = [65536.0f / (1 + 23 * distance), 4096.0f / (1 + 14 * distance), 4096.0f / (1 + 14 * distance)];
        float[] inverse = new float[3];
        for (int c = 0; c < 3; c++)
        {
            dcQuant[c] = (float)(Half)(128.0f / factors[c]) / 128.0f;
            inverse[c] = 1.0f / dcQuant[c];
        }

        int size = reference.Width * reference.Height;
        samples = [new int[size], new int[size], new int[size]];
        for (int i = 0; i < size; i++)
        {
            float x = reference.Planes[0][i];
            float y = reference.Planes[1][i];
            float b = reference.Planes[2][i];
            samples[1][i] = (int)(x * inverse[0] + (x < 0 ? -0.5f : 0.5f));
            samples[0][i] = (int)(y * inverse[1] + (y < 0 ? -0.5f : 0.5f));
            samples[2][i] = (int)(b * inverse[2] + 0.5f) - samples[0][i];
        }

        // What the decoder reconstructs, which is what the frame adds back.
        Decoded = new JxlPlane[3];
        for (int c = 0; c < 3; c++)
        {
            var plane = new JxlPlane(reference.Width, reference.Height);
            for (int yy = 0; yy < reference.Height; yy++)
            {
                Span<float> row = plane.Row(yy);
                for (int xx = 0; xx < reference.Width; xx++)
                {
                    int i = yy * reference.Width + xx;
                    row[xx] = c switch
                    {
                        0 => samples[1][i] * dcQuant[0],
                        1 => samples[0][i] * dcQuant[1],
                        _ => (samples[2][i] + samples[0][i]) * dcQuant[2],
                    };
                }
            }

            Decoded[c] = plane;
        }
    }

    /// <summary>The reference image as the decoder reconstructs it.</summary>
    public JxlPlane[] Decoded { get; }

    /// <summary>The patches of a frame of the given size, or null when it has none.</summary>
    public static JxlPatchEncoder? Create(JxlImage3F image, int width, int height, float distance, CancellationToken cancellationToken)
    {
        var found = JxlPatchFinder.Find(image, width, height, cancellationToken);
        if (found is null) return null;
        return new JxlPatchEncoder(found.Value.Patches, found.Value.Reference, distance);
    }

    /// <summary>The patches as the decoder's renderer applies them, each adding the reference to the colour channels.</summary>
    public JxlPatch[] Patches(int extraChannels)
    {
        JxlPatchBlending[] blendings = [new(JxlPatchBlendMode.Add, 0, false), .. Enumerable.Repeat(new JxlPatchBlending(JxlPatchBlendMode.None, 0, false), extraChannels)];
        return [.. entries.SelectMany(entry => entry.Positions.Select(position =>
            new JxlPatch(ReferenceSlot, entry.ReferenceX, entry.ReferenceY, entry.Width, entry.Height, position.X, position.Y, blendings)))];
    }

    /// <summary>The decoder's reference slots after the patch frame.</summary>
    public JxlReferenceFrames References(int extraChannels)
    {
        var references = new JxlReferenceFrames();
        JxlPlane[] extra = [.. Enumerable.Range(0, extraChannels).Select(_ => new JxlPlane(reference.Width, reference.Height))];
        references.StoreReference(ReferenceSlot, new JxlReferenceFrame(Decoded, extra), beforeColorTransform: true);
        return references;
    }

    /// <summary>Removes the patches from <paramref name="image"/>, which the decoder adds back (SubtractFrom with kAdd).</summary>
    public void SubtractFrom(JxlImage3F image)
    {
        foreach (JxlPatchEntry entry in entries)
        {
            foreach ((int x, int y) in entry.Positions)
            {
                for (int c = 0; c < 3; c++)
                {
                    for (int iy = 0; iy < entry.Height; iy++)
                    {
                        Span<float> row = image.Row(c, y + iy).Slice(x, entry.Width);
                        ReadOnlySpan<float> source = Decoded[c].Row(entry.ReferenceY + iy).Slice(entry.ReferenceX, entry.Width);
                        for (int ix = 0; ix < row.Length; ix++) row[ix] -= source[ix];
                    }
                }
            }
        }
    }

    /// <summary>Writes the reference-only Modular frame of the patch image; zero extra channels accompany it.</summary>
    public void WriteReferenceFrame(JxlBitWriter writer, JxlImageMetadata metadata, CancellationToken cancellationToken)
    {
        int extraChannels = metadata.ExtraChannels.Count;
        var header = new JxlFrameHeader
        {
            Type = JxlFrameType.ReferenceOnly,
            Encoding = JxlFrameEncoding.Modular,
            ColorTransform = JxlColorTransform.Xyb,
            Width = (uint)reference.Width,
            Height = (uint)reference.Height,
            CustomSizeOrOrigin = true,
            ExtraChannelUpsampling = [.. Enumerable.Repeat(1u, extraChannels)],
            IsLast = false,
            SaveAsReference = ReferenceSlot,
            SaveBeforeColorTransform = true,
            LoopFilter = new JxlLoopFilter { Gaborish = false, EpfIterations = 0 },
        };
        var channels = new List<JxlChannel>();
        foreach (int[] plane in samples)
        {
            var channel = new JxlChannel(reference.Width, reference.Height);
            plane.CopyTo(channel.Pixels, 0);
            channels.Add(channel);
        }

        for (int i = 0; i < extraChannels; i++) channels.Add(new JxlChannel(reference.Width, reference.Height));
        JxlModularEncoder.WriteFrame(writer, channels, 3, cancellationToken, header, metadata, dcQuant);
    }

    /// <summary>Writes the patch dictionary (PatchDictionaryEncoder::Encode) at the start of the frame's LF global section.</summary>
    public void WriteDictionary(JxlBitWriter writer, int extraChannels)
    {
        var tokens = new JxlTokens();
        tokens.Add(0, (uint)entries.Count);
        foreach (JxlPatchEntry entry in entries)
        {
            tokens.Add(1, ReferenceSlot);
            tokens.Add(3, (uint)entry.ReferenceX);
            tokens.Add(3, (uint)entry.ReferenceY);
            tokens.Add(2, (uint)entry.Width - 1);
            tokens.Add(2, (uint)entry.Height - 1);
            tokens.Add(7, (uint)entry.Positions.Count - 1);
            for (int i = 0; i < entry.Positions.Count; i++)
            {
                (int x, int y) = entry.Positions[i];
                if (i == 0)
                {
                    tokens.Add(4, (uint)x);
                    tokens.Add(4, (uint)y);
                }
                else
                {
                    tokens.Add(6, PackSigned(x - entry.Positions[i - 1].X));
                    tokens.Add(6, PackSigned(y - entry.Positions[i - 1].Y));
                }

                // Blend modes: add the colour, leave the extra channels.
                tokens.Add(5, (uint)JxlPatchBlendMode.Add);
                for (int k = 0; k < extraChannels; k++) tokens.Add(5, (uint)JxlPatchBlendMode.None);
            }
        }

        JxlEntropyEncoder.WriteStandalone(writer, Contexts, tokens);
    }
}
