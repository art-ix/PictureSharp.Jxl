using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;

namespace PictureSharp.Formats.Jxl.Codestream.Modular.Transforms;

/// <summary>Scalar transform ported from libjxl v0.12.0 modular/transform (BSD-3-Clause).</summary>
internal sealed class JxlPaletteTransform(JxlTransform description) : IJxlModularTransform
{
    public void MetaApply(JxlModularImage image)
    {
        long last = description.BeginC + (long)description.NumC - 1;
        image.CheckEqualChannels(description.BeginC, last);
        int count = (int)description.NumC;
        if (description.BeginC >= image.MetaChannels)
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

        image.RemoveChannels((int)description.BeginC + 1, count - 1);
        long width = (long)description.Colors + description.Deltas;
        image.InsertChannel(0, new JxlChannel((int)width, count, -1, -1));
    }

    public void Inverse(JxlModularImage image, JxlWeightedHeader weighted, CancellationToken cancellationToken)
    {
        if (image.MetaChannels < 1)
        {
            throw JxlDescriptor.Corrupt("Palette transform without a palette.");
        }

        JxlChannel palette = image.Channels[0];
        int count = palette.Height;
        long c0 = description.BeginC + 1L;
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
            if (description.Deltas == 0 && description.Predictor == JxlPredictor.Zero)
            {
                for (int c = 0; c < count; c++)
                {
                    int[] output = outputs[c].Pixels;
                    for (int i = 0; i < indices.Length; i++)
                    {
                        if ((i & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
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
                    JxlWeightedPredictor? predictor = description.Predictor == JxlPredictor.Weighted ? new JxlWeightedPredictor(weighted, width) : null;
                    for (int y = 0; y < height; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        for (int x = 0; x < width; x++)
                        {
                            int position = y * width + x;
                            int index = indices[position];
                            int entry = PaletteValue(entries, index, c, paletteSize, bitDepth);
                            long value = entry;
                            if (predictor is not null)
                            {
                                long guess = JxlMaTree.PredictNoTree(output, width, x, y, description.Predictor, predictor);
                                if (index < description.Deltas)
                                {
                                    value = guess + entry;
                                }

                                output[position] = unchecked((int)value);
                                predictor.Update(output[position], x, y);
                                continue;
                            }

                            if (index < description.Deltas)
                            {
                                value = JxlMaTree.PredictNoTree(output, width, x, y, description.Predictor, null) + entry;
                            }

                            output[position] = unchecked((int)value);
                        }
                    }
                }
            }
        }

        image.ReplaceChannel((int)c0, outputs[0]);
        image.InsertChannels((int)c0 + 1, outputs[1..]);
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
            if (description.BeginC + count - 1 >= image.MetaChannels)
            {
                throw JxlDescriptor.Corrupt("Invalid palette of meta channels.");
            }
        }

        image.RemoveChannels(0, 1);
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
}
