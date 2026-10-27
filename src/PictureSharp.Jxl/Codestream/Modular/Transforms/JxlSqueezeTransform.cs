using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;

namespace PictureSharp.Formats.Jxl.Codestream.Modular.Transforms;

/// <summary>Scalar transform ported from libjxl v0.12.0 modular/transform (BSD-3-Clause).</summary>
internal sealed class JxlSqueezeTransform(JxlTransform description) : IJxlModularTransform
{
    private const int MaxFirstPreviewSize = 8;
    private readonly List<JxlSqueeze> Squeezes = [.. description.Squeezes];

    public void MetaApply(JxlModularImage image)
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

                image.InsertChannel(offset + (c - begin), new JxlChannel(w, h, channel.HShift, channel.VShift));
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

    public void Inverse(JxlModularImage image, JxlWeightedHeader weighted, CancellationToken cancellationToken)
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

                image.ReplaceChannel(c, squeeze.Horizontal ? UnsqueezeHorizontal(average, residual, cancellationToken) : UnsqueezeVertical(average, residual, cancellationToken));
            }

            image.RemoveChannels(offset, end - begin + 1);
        }
    }

    private static JxlChannel UnsqueezeHorizontal(JxlChannel average, JxlChannel residual, CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
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

    private static JxlChannel UnsqueezeVertical(JxlChannel average, JxlChannel residual, CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
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
    }}
