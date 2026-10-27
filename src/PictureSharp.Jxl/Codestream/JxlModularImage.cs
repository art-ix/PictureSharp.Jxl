namespace PictureSharp.Formats.Jxl.Codestream;

/// <summary>A plane of 32-bit samples; its storage is allocated on first access, after transforms fixed its size.</summary>
internal sealed class JxlChannel
{
    private int[]? pixels;

    public JxlChannel(int width, int height, int hShift = 0, int vShift = 0)
    {
        Width = width;
        Height = height;
        HShift = hShift;
        VShift = vShift;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>log2 of the horizontal downsampling; -1 for meta channels such as a palette.</summary>
    public int HShift { get; set; }

    public int VShift { get; set; }

    public bool IsEmpty => Width == 0 || Height == 0;

    public int[] Pixels => pixels ??= (long)Width * Height <= Array.MaxLength
        ? new int[Width * Height]
        : throw JxlDescriptor.Unsupported($"a {Width}x{Height} channel is too large.");

    public Span<int> Row(int y) => Pixels.AsSpan(y * Width, Width);

    /// <summary>Changes the size and drops the samples, as MetaApply does before anything is decoded.</summary>
    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        pixels = null;
    }

    public bool SameShape(JxlChannel other) =>
        Width == other.Width && Height == other.Height && HShift == other.HShift && VShift == other.VShift;
}

/// <summary>A Modular image: channels (meta channels such as palettes first) and the transforms to undo.</summary>
internal sealed class JxlModularImage
{
    public JxlModularImage(int bitDepth, List<JxlChannel> channels)
    {
        BitDepth = bitDepth;
        Channels = channels;
    }

    public List<JxlChannel> Channels { get; }

    public int MetaChannels { get; set; }

    public int BitDepth { get; }

    public List<JxlTransform> Transforms { get; set; } = [];

    public void UndoTransforms(JxlWeightedHeader weighted)
    {
        for (int i = Transforms.Count - 1; i >= 0; i--)
        {
            Transforms[i].Inverse(this, weighted);
        }

        Transforms = [];
    }

    /// <summary>Fails unless channels <paramref name="first"/>..<paramref name="last"/> exist, have one shape and do not mix meta and data.</summary>
    public void CheckEqualChannels(long first, long last)
    {
        if (first > Channels.Count || last >= Channels.Count || last < first
            || (first < MetaChannels && last >= MetaChannels)
            || Channels.Skip((int)first + 1).Take((int)(last - first)).Any(channel => !channel.SameShape(Channels[(int)first])))
        {
            throw JxlDescriptor.Corrupt("Invalid channel range of a Modular transform.");
        }
    }
}

internal enum JxlTransformId : uint { Rct = 0, Palette = 1, Squeeze = 2 }

internal sealed record JxlSqueeze(bool Horizontal, bool InPlace, uint BeginC, uint NumC);

/// <summary>
/// A Modular transform: reversible colour transform, (delta) palette or squeeze. Ported from libjxl v0.12.0
/// lib/jxl/modular/transform/transform.cc, rct.cc, palette.cc, palette.h and squeeze.cc (BSD-3-Clause), scalar paths.
/// </summary>
internal sealed class JxlTransform
{
    private const int MaxFirstPreviewSize = 8;

    public JxlTransformId Id { get; private init; }

    public uint BeginC { get; private init; }

    public uint RctType { get; private init; }

    public uint NumC { get; private init; }

    public uint Colors { get; private init; }

    public uint Deltas { get; private init; }

    public JxlPredictor Predictor { get; private init; }

    /// <summary>The squeeze steps; filled with the default steps by <see cref="MetaApply"/> when none are coded.</summary>
    public List<JxlSqueeze> Squeezes { get; private init; } = [];

    public static JxlTransform Read(ref JxlBitReader reader)
    {
        uint id = JxlFields.U32(ref reader, JxlU32.Val(0), JxlU32.Val(1), JxlU32.Val(2), JxlU32.Val(3));
        if (id == 3)
        {
            throw JxlDescriptor.Corrupt("Invalid Modular transform.");
        }

        var transform = new JxlTransform { Id = (JxlTransformId)id };
        if (transform.Id == JxlTransformId.Squeeze)
        {
            uint count = JxlFields.U32(ref reader, JxlU32.Val(0), JxlU32.BitsOffset(4, 1), JxlU32.BitsOffset(6, 9), JxlU32.BitsOffset(8, 41));
            var squeezes = new List<JxlSqueeze>();
            for (int i = 0; i < count; i++)
            {
                squeezes.Add(new JxlSqueeze(reader.ReadBool(), reader.ReadBool(), Channel(ref reader),
                    JxlFields.U32(ref reader, JxlU32.Val(1), JxlU32.Val(2), JxlU32.Val(3), JxlU32.BitsOffset(4, 4))));
            }

            return new JxlTransform { Id = transform.Id, Squeezes = squeezes };
        }

        uint begin = Channel(ref reader);
        if (transform.Id == JxlTransformId.Rct)
        {
            uint type = JxlFields.U32(ref reader, JxlU32.Val(6), JxlU32.Bits32(2), JxlU32.BitsOffset(4, 2), JxlU32.BitsOffset(6, 10));
            return type < 42 ? new JxlTransform { Id = transform.Id, BeginC = begin, RctType = type } : throw JxlDescriptor.Corrupt("Invalid RCT type.");
        }

        uint channels = JxlFields.U32(ref reader, JxlU32.Val(1), JxlU32.Val(3), JxlU32.Val(4), JxlU32.BitsOffset(13, 1));
        uint colors = JxlFields.U32(ref reader, JxlU32.Bits32(8), JxlU32.BitsOffset(10, 256), JxlU32.BitsOffset(12, 1280), JxlU32.BitsOffset(16, 5376));
        uint deltas = JxlFields.U32(ref reader, JxlU32.Val(0), JxlU32.BitsOffset(8, 1), JxlU32.BitsOffset(10, 257), JxlU32.BitsOffset(16, 1281));
        uint predictor = reader.ReadBits(4);
        return predictor < JxlMaTree.PredictorCount
            ? new JxlTransform { Id = transform.Id, BeginC = begin, NumC = channels, Colors = colors, Deltas = deltas, Predictor = (JxlPredictor)predictor }
            : throw JxlDescriptor.Corrupt("Invalid palette predictor.");
    }

    private static uint Channel(ref JxlBitReader reader) =>
        JxlFields.U32(ref reader, JxlU32.Bits32(3), JxlU32.BitsOffset(6, 8), JxlU32.BitsOffset(10, 72), JxlU32.BitsOffset(13, 1096));

    /// <summary>Applies the effect of the transform on the channel list before the channels are decoded.</summary>
    public void MetaApply(JxlModularImage image)
    {
        switch (Id)
        {
            case JxlTransformId.Rct:
                image.CheckEqualChannels(BeginC, BeginC + 2L);
                break;
            case JxlTransformId.Palette:
                MetaPalette(image);
                break;
            default:
                MetaSqueeze(image);
                break;
        }
    }

    public void Inverse(JxlModularImage image, JxlWeightedHeader weighted)
    {
        switch (Id)
        {
            case JxlTransformId.Rct:
                InverseRct(image);
                break;
            case JxlTransformId.Palette:
                InversePalette(image, weighted);
                break;
            default:
                InverseSqueeze(image);
                break;
        }
    }

    private void InverseRct(JxlModularImage image)
    {
        image.CheckEqualChannels(BeginC, BeginC + 2L);
        int m = (int)BeginC;
        if (RctType == 0)
        {
            return;
        }

        // Permutation: 0=RGB, 1=GBR, 2=BRG, 3=RBG, 4=GRB, 5=BGR. Type 6 is YCoCg; 0–5 have the low bit for the third
        // channel (subtract first) and the high bits for the second (nothing, subtract first, subtract average).
        int permutation = (int)RctType / 7;
        int custom = (int)RctType % 7;
        JxlChannel[] inputs = [image.Channels[m], image.Channels[m + 1], image.Channels[m + 2]];
        int[] outputs = [m + permutation % 3, m + (permutation + 1 + permutation / 3) % 3, m + (permutation + 2 - permutation / 3) % 3];
        if (custom != 0)
        {
            int[] first = inputs[0].Pixels;
            int[] second = inputs[1].Pixels;
            int[] third = inputs[2].Pixels;
            for (int i = 0; i < first.Length; i++)
            {
                unchecked
                {
                    if (custom == 6)
                    {
                        int y = first[i];
                        int co = second[i];
                        int cg = third[i];
                        int tmp = y - (cg >> 1);
                        int g = cg + tmp;
                        int b = tmp - (co >> 1);
                        (first[i], second[i], third[i]) = (b + co, g, b);
                        continue;
                    }

                    int a = first[i];
                    int s = second[i];
                    int t = third[i];
                    if ((custom & 1) != 0)
                    {
                        t += a;
                    }

                    if (custom >> 1 == 1)
                    {
                        s += a;
                    }
                    else if (custom >> 1 == 2)
                    {
                        s += (a + t) >> 1;
                    }

                    (first[i], second[i], third[i]) = (a, s, t);
                }
            }
        }

        for (int i = 0; i < 3; i++)
        {
            image.Channels[outputs[i]] = inputs[i];
        }
    }

    private void MetaPalette(JxlModularImage image)
    {
        long last = BeginC + (long)NumC - 1;
        image.CheckEqualChannels(BeginC, last);
        int count = (int)NumC;
        if (BeginC >= image.MetaChannels)
        {
            image.MetaChannels++;
        }
        else
        {
            if (last >= image.MetaChannels)
            {
                throw JxlDescriptor.Corrupt("Invalid palette of meta channels.");
            }

            image.MetaChannels += 2 - count;
        }

        image.Channels.RemoveRange((int)BeginC + 1, count - 1);
        long width = (long)Colors + Deltas;
        image.Channels.Insert(0, new JxlChannel((int)width, count, -1, -1));
    }

    private void InversePalette(JxlModularImage image, JxlWeightedHeader weighted)
    {
        if (image.MetaChannels < 1)
        {
            throw JxlDescriptor.Corrupt("Palette transform without a palette.");
        }

        JxlChannel palette = image.Channels[0];
        int count = palette.Height;
        long c0 = BeginC + 1L;
        if (c0 >= image.Channels.Count || count < 1)
        {
            throw JxlDescriptor.Corrupt("Invalid palette transform.");
        }

        JxlChannel indexChannel = image.Channels[(int)c0];
        int width = indexChannel.Width;
        int height = indexChannel.Height;
        int[] indices = indexChannel.Pixels;
        var outputs = new JxlChannel[count];
        outputs[0] = new JxlChannel(width, height, indexChannel.HShift, indexChannel.VShift);
        for (int i = 1; i < count; i++)
        {
            outputs[i] = new JxlChannel(width, height, indexChannel.HShift, indexChannel.VShift);
        }

        int[] entries = palette.Pixels;
        int paletteSize = palette.Width;
        int bitDepth = Math.Min(image.BitDepth, 24);
        if (width > 0)
        {
            if (Deltas == 0 && Predictor == JxlPredictor.Zero)
            {
                for (int c = 0; c < count; c++)
                {
                    int[] output = outputs[c].Pixels;
                    for (int i = 0; i < indices.Length; i++)
                    {
                        // A single channel is clamped to the palette; several channels may use implicit entries.
                        int index = count == 1 ? Math.Min(Math.Max(indices[i], 0), paletteSize - 1) : indices[i];
                        output[i] = PaletteValue(entries, index, c, paletteSize, bitDepth);
                    }
                }
            }
            else
            {
                for (int c = 0; c < count; c++)
                {
                    int[] output = outputs[c].Pixels;
                    JxlWeightedPredictor? predictor = Predictor == JxlPredictor.Weighted ? new JxlWeightedPredictor(weighted, width) : null;
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int position = y * width + x;
                            int index = indices[position];
                            int entry = PaletteValue(entries, index, c, paletteSize, bitDepth);
                            long value = entry;
                            if (predictor is not null)
                            {
                                long guess = JxlMaTree.PredictNoTree(output, width, x, y, Predictor, predictor);
                                if (index < Deltas)
                                {
                                    value = guess + entry;
                                }

                                output[position] = unchecked((int)value);
                                predictor.Update(output[position], x, y, width);
                                continue;
                            }

                            if (index < Deltas)
                            {
                                value = JxlMaTree.PredictNoTree(output, width, x, y, Predictor, null) + entry;
                            }

                            output[position] = unchecked((int)value);
                        }
                    }
                }
            }
        }

        image.Channels[(int)c0] = outputs[0];
        image.Channels.InsertRange((int)c0 + 1, outputs[1..]);
        if (c0 >= image.MetaChannels)
        {
            image.MetaChannels--;
        }
        else
        {
            if (image.MetaChannels < 2 - count)
            {
                throw JxlDescriptor.Corrupt("Invalid palette of meta channels.");
            }

            image.MetaChannels -= 2 - count;
            if (BeginC + count - 1 >= image.MetaChannels)
            {
                throw JxlDescriptor.Corrupt("Invalid palette of meta channels.");
            }
        }

        image.Channels.RemoveAt(0);
    }

    // Deltas of the implicit delta palette (indices below zero).
    private static readonly short[,] DeltaPalette =
    {
        { 0, 0, 0 }, { 4, 4, 4 }, { 11, 0, 0 }, { 0, 0, -13 }, { 0, -12, 0 }, { -10, -10, -10 }, { -18, -18, -18 }, { -27, -27, -27 },
        { -18, -18, 0 }, { 0, 0, -32 }, { -32, 0, 0 }, { -37, -37, -37 }, { 0, -32, -32 }, { 24, 24, 45 }, { 50, 50, 50 },
        { -45, -24, -24 }, { -24, -45, -45 }, { 0, -24, -24 }, { -34, -34, 0 }, { -24, 0, -24 }, { -45, -45, -24 }, { 64, 64, 64 },
        { -32, 0, -32 }, { 0, -32, 0 }, { -32, 0, 32 }, { -24, -45, -24 }, { 45, 24, 45 }, { 24, -24, -45 }, { -45, -24, 24 },
        { 80, 80, 80 }, { 64, 0, 0 }, { 0, 0, -64 }, { 0, -64, -64 }, { -24, -24, 45 }, { 96, 96, 96 }, { 64, 64, 0 },
        { 45, -24, -24 }, { 34, -34, 0 }, { 112, 112, 112 }, { 24, -45, -45 }, { 45, 45, -24 }, { 0, -32, 32 }, { 24, -24, 45 },
        { 0, 96, 96 }, { 45, -24, 24 }, { 24, -45, -24 }, { -24, -45, 24 }, { 0, -64, 0 }, { 96, 0, 0 }, { 128, 128, 128 },
        { 64, 0, 64 }, { 144, 144, 144 }, { 96, 96, 0 }, { -36, -36, 36 }, { 45, -24, -45 }, { 45, -45, -24 }, { 0, 0, -96 },
        { 0, 128, 128 }, { 0, 96, 0 }, { 45, 24, -45 }, { -128, 0, 0 }, { 24, -45, 24 }, { -45, 24, -45 }, { 64, 0, -64 },
        { 64, -64, -64 }, { 96, 0, 96 }, { 45, -45, 24 }, { 24, 45, -45 }, { 64, 64, -64 }, { 128, 128, 0 }, { 0, 0, -128 },
        { -24, 45, -45 },
    };

    /// <summary>A palette entry, extended to implicit entries: delta palette below 0, colour cubes past the palette.</summary>
    private static int PaletteValue(int[] palette, int index, int c, int paletteSize, int bitDepth)
    {
        const int SmallCube = 4;
        const int LargeCube = 5;
        const int LargeCubeOffset = SmallCube * SmallCube * SmallCube;
        if (index < 0)
        {
            if (c >= 3)
            {
                return 0;
            }

            index = -(index + 1);
            index %= 1 + 2 * (DeltaPalette.GetLength(0) - 1);
            int result = DeltaPalette[(index + 1) >> 1, c] * ((index & 1) == 0 ? -1 : 1);
            return bitDepth > 8 ? result * (1 << (bitDepth - 8)) : result;
        }

        if (paletteSize <= index && index < paletteSize + LargeCubeOffset)
        {
            if (c >= 3)
            {
                return 0;
            }

            index = (index - paletteSize) >> (c * 2);
            return Scale(index % SmallCube, bitDepth) + (1 << Math.Max(0, bitDepth - 3));
        }

        if (paletteSize + LargeCubeOffset <= index)
        {
            if (c >= 3)
            {
                return 0;
            }

            index -= paletteSize + LargeCubeOffset;
            index /= c switch { 0 => 1, 1 => LargeCube, _ => LargeCube * LargeCube };
            return Scale(index % LargeCube, bitDepth);
        }

        return palette[c * paletteSize + index];
    }

    private static int Scale(long value, int bitDepth) => unchecked((int)((ulong)(value * ((1L << bitDepth) - 1)) >> 2));

    private void MetaSqueeze(JxlModularImage image)
    {
        if (Squeezes.Count == 0)
        {
            DefaultSqueezes(image);
        }

        foreach (JxlSqueeze squeeze in Squeezes)
        {
            (int begin, int end) = CheckSqueeze(squeeze, image.Channels.Count);
            if (begin < image.MetaChannels)
            {
                if (end >= image.MetaChannels || !squeeze.InPlace)
                {
                    throw JxlDescriptor.Corrupt("Invalid squeeze of meta channels.");
                }

                image.MetaChannels += (int)squeeze.NumC;
            }

            int offset = squeeze.InPlace ? end + 1 : image.Channels.Count;
            for (int c = begin; c <= end; c++)
            {
                JxlChannel channel = image.Channels[c];
                if (channel.HShift > 30 || channel.VShift > 30)
                {
                    throw JxlDescriptor.Corrupt("Too many squeezes.");
                }

                int w = channel.Width;
                int h = channel.Height;
                if (w == 0 || h == 0)
                {
                    throw JxlDescriptor.Corrupt("Squeezing an empty channel.");
                }

                if (squeeze.Horizontal)
                {
                    channel.Resize((w + 1) / 2, h);
                    if (channel.HShift >= 0)
                    {
                        channel.HShift++;
                    }

                    w -= (w + 1) / 2;
                }
                else
                {
                    channel.Resize(w, (h + 1) / 2);
                    if (channel.VShift >= 0)
                    {
                        channel.VShift++;
                    }

                    h -= (h + 1) / 2;
                }

                image.Channels.Insert(offset + (c - begin), new JxlChannel(w, h, channel.HShift, channel.VShift));
            }
        }
    }

    private void DefaultSqueezes(JxlModularImage image)
    {
        int first = image.MetaChannels;
        int channels = image.Channels.Count - first;
        if (channels <= 0)
        {
            throw JxlDescriptor.Corrupt("Squeeze without channels.");
        }

        int w = image.Channels[first].Width;
        int h = image.Channels[first].Height;
        bool wide = w > h;
        if (channels > 2 && image.Channels[first + 1].Width == w && image.Channels[first + 1].Height == h)
        {
            // Chroma first, for 4:2:0-like previews.
            Squeezes.Add(new JxlSqueeze(true, false, (uint)first + 1, 2));
            Squeezes.Add(new JxlSqueeze(false, false, (uint)first + 1, 2));
        }

        if (!wide && h > MaxFirstPreviewSize)
        {
            Squeezes.Add(new JxlSqueeze(false, true, (uint)first, (uint)channels));
            h = (h + 1) / 2;
        }

        while (w > MaxFirstPreviewSize || h > MaxFirstPreviewSize)
        {
            if (w > MaxFirstPreviewSize)
            {
                Squeezes.Add(new JxlSqueeze(true, true, (uint)first, (uint)channels));
                w = (w + 1) / 2;
            }

            if (h > MaxFirstPreviewSize)
            {
                Squeezes.Add(new JxlSqueeze(false, true, (uint)first, (uint)channels));
                h = (h + 1) / 2;
            }
        }
    }

    private static (int Begin, int End) CheckSqueeze(JxlSqueeze squeeze, int channels)
    {
        long begin = squeeze.BeginC;
        long end = begin + squeeze.NumC - 1;
        return begin >= channels || end >= channels || end < begin
            ? throw JxlDescriptor.Corrupt("Invalid squeeze channel range.")
            : ((int)begin, (int)end);
    }

    private void InverseSqueeze(JxlModularImage image)
    {
        for (int i = Squeezes.Count - 1; i >= 0; i--)
        {
            JxlSqueeze squeeze = Squeezes[i];
            (int begin, int end) = CheckSqueeze(squeeze, image.Channels.Count);
            int offset = squeeze.InPlace ? end + 1 : image.Channels.Count + begin - end - 1;
            if (begin < image.MetaChannels)
            {
                if (image.MetaChannels <= squeeze.NumC)
                {
                    throw JxlDescriptor.Corrupt("Invalid squeeze of meta channels.");
                }

                image.MetaChannels -= (int)squeeze.NumC;
            }

            for (int c = begin; c <= end; c++)
            {
                int rc = offset + c - begin;
                if (rc >= image.Channels.Count)
                {
                    throw JxlDescriptor.Corrupt("Invalid squeeze residual channel.");
                }

                JxlChannel average = image.Channels[c];
                JxlChannel residual = image.Channels[rc];
                if (average.Width < residual.Width || average.Height < residual.Height)
                {
                    throw JxlDescriptor.Corrupt("Corrupted squeeze transform.");
                }

                image.Channels[c] = squeeze.Horizontal ? UnsqueezeHorizontal(average, residual) : UnsqueezeVertical(average, residual);
            }

            image.Channels.RemoveRange(offset, end - begin + 1);
        }
    }

    private static JxlChannel UnsqueezeHorizontal(JxlChannel average, JxlChannel residual)
    {
        if (average.Width != (average.Width + residual.Width + 1) / 2 || average.Height != residual.Height)
        {
            throw JxlDescriptor.Corrupt("Corrupted squeeze transform.");
        }

        if (residual.Width == 0)
        {
            average.HShift--;
            return average;
        }

        var output = new JxlChannel(average.Width + residual.Width, average.Height, average.HShift - 1, average.VShift);
        if (residual.Height == 0)
        {
            return output;
        }

        for (int y = 0; y < average.Height; y++)
        {
            Span<int> averages = average.Row(y);
            Span<int> residuals = residual.Row(y);
            Span<int> row = output.Row(y);
            for (int x = 0; x < residual.Width; x++)
            {
                long avg = averages[x];
                long next = x + 1 < average.Width ? averages[x + 1] : avg;
                long left = x > 0 ? row[(x << 1) - 1] : avg;
                long diff = residuals[x] + SmoothTendency(left, avg, next);
                long a = avg + diff / 2;
                row[x << 1] = unchecked((int)a);
                row[(x << 1) + 1] = unchecked((int)(a - diff));
            }

            if ((output.Width & 1) != 0)
            {
                row[^1] = averages[^1];
            }
        }

        return output;
    }

    private static JxlChannel UnsqueezeVertical(JxlChannel average, JxlChannel residual)
    {
        if (average.Height != (average.Height + residual.Height + 1) / 2 || average.Width != residual.Width)
        {
            throw JxlDescriptor.Corrupt("Corrupted squeeze transform.");
        }

        if (residual.Height == 0)
        {
            average.VShift--;
            return average;
        }

        var output = new JxlChannel(average.Width, average.Height + residual.Height, average.HShift, average.VShift - 1);
        if (residual.Width == 0)
        {
            return output;
        }

        for (int y = 0; y < residual.Height; y++)
        {
            Span<int> averages = average.Row(y);
            Span<int> nextAverages = average.Row(y + 1 < average.Height ? y + 1 : y);
            Span<int> residuals = residual.Row(y);
            Span<int> row = output.Row(y << 1);
            Span<int> nextRow = output.Row((y << 1) + 1);
            Span<int> top = y > 0 ? output.Row((y << 1) - 1) : averages;
            for (int x = 0; x < average.Width; x++)
            {
                long avg = averages[x];
                long diff = residuals[x] + SmoothTendency(top[x], avg, nextAverages[x]);
                long value = avg + diff / 2;
                row[x] = unchecked((int)value);
                nextRow[x] = unchecked((int)(value - diff));
            }
        }

        if ((output.Height & 1) != 0)
        {
            average.Row(average.Height - 1).CopyTo(output.Row(output.Height - 1));
        }

        return output;
    }

    /// <summary>The expected difference of the two pixels of a pair in smooth areas, bounded to avoid ringing.</summary>
    private static long SmoothTendency(long b, long a, long n)
    {
        long diff = 0;
        if (b >= a && a >= n)
        {
            diff = (4 * b - 3 * n - a + 6) / 12;
            if (diff - (diff & 1) > 2 * (b - a))
            {
                diff = 2 * (b - a) + 1;
            }

            if (diff + (diff & 1) > 2 * (a - n))
            {
                diff = 2 * (a - n);
            }
        }
        else if (b <= a && a <= n)
        {
            diff = (4 * b - 3 * n - a - 6) / 12;
            if (diff + (diff & 1) < 2 * (b - a))
            {
                diff = 2 * (b - a) - 1;
            }

            if (diff - (diff & 1) < 2 * (a - n))
            {
                diff = 2 * (a - n);
            }
        }

        return diff;
    }
}
