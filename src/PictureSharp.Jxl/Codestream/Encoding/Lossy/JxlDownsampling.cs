namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// Halves an image for frames upsampled 2× by the decoder (resampling at high distances): a sharpening 12×12 kernel
/// clamped to the local range widened by a mask of the box-downsampled image. Ported from libjxl v0.12.0
/// lib/jxl/enc_heuristics.cc (DownsampleImage2_Sharper) and image_ops.cc (DownsampleImage; BSD-3-Clause).
/// </summary>
internal static class JxlDownsampling
{
    private const int KernelSize = 12;

    private static ReadOnlySpan<float> Kernel =>
    [
        -0.000314256996835f, -0.000314256996835f, -0.000897597057705f, -0.000562751488849f, -0.000176807273646f, 0.001864627368902f, 0.001864627368902f, -0.000176807273646f, -0.000562751488849f, -0.000897597057705f, -0.000314256996835f, -0.000314256996835f,
        -0.000314256996835f, -0.001527942804748f, -0.000121760530512f, 0.000191123989093f, 0.010193185932466f, 0.058637519197110f, 0.058637519197110f, 0.010193185932466f, 0.000191123989093f, -0.000121760530512f, -0.001527942804748f, -0.000314256996835f,
        -0.000897597057705f, -0.000121760530512f, 0.000946363683751f, 0.007113577630288f, 0.000437956841058f, -0.000372823835211f, -0.000372823835211f, 0.000437956841058f, 0.007113577630288f, 0.000946363683751f, -0.000121760530512f, -0.000897597057705f,
        -0.000562751488849f, 0.000191123989093f, 0.007113577630288f, 0.044592622228814f, 0.000222278879007f, -0.162864473015945f, -0.162864473015945f, 0.000222278879007f, 0.044592622228814f, 0.007113577630288f, 0.000191123989093f, -0.000562751488849f,
        -0.000176807273646f, 0.010193185932466f, 0.000437956841058f, 0.000222278879007f, -0.000913092543974f, -0.017071696107902f, -0.017071696107902f, -0.000913092543974f, 0.000222278879007f, 0.000437956841058f, 0.010193185932466f, -0.000176807273646f,
        0.001864627368902f, 0.058637519197110f, -0.000372823835211f, -0.162864473015945f, -0.017071696107902f, 0.414660099370354f, 0.414660099370354f, -0.017071696107902f, -0.162864473015945f, -0.000372823835211f, 0.058637519197110f, 0.001864627368902f,
        0.001864627368902f, 0.058637519197110f, -0.000372823835211f, -0.162864473015945f, -0.017071696107902f, 0.414660099370354f, 0.414660099370354f, -0.017071696107902f, -0.162864473015945f, -0.000372823835211f, 0.058637519197110f, 0.001864627368902f,
        -0.000176807273646f, 0.010193185932466f, 0.000437956841058f, 0.000222278879007f, -0.000913092543974f, -0.017071696107902f, -0.017071696107902f, -0.000913092543974f, 0.000222278879007f, 0.000437956841058f, 0.010193185932466f, -0.000176807273646f,
        -0.000562751488849f, 0.000191123989093f, 0.007113577630288f, 0.044592622228814f, 0.000222278879007f, -0.162864473015945f, -0.162864473015945f, 0.000222278879007f, 0.044592622228814f, 0.007113577630288f, 0.000191123989093f, -0.000562751488849f,
        -0.000897597057705f, -0.000121760530512f, 0.000946363683751f, 0.007113577630288f, 0.000437956841058f, -0.000372823835211f, -0.000372823835211f, 0.000437956841058f, 0.007113577630288f, 0.000946363683751f, -0.000121760530512f, -0.000897597057705f,
        -0.000314256996835f, -0.001527942804748f, -0.000121760530512f, 0.000191123989093f, 0.010193185932466f, 0.058637519197110f, 0.058637519197110f, 0.010193185932466f, 0.000191123989093f, -0.000121760530512f, -0.001527942804748f, -0.000314256996835f,
        -0.000314256996835f, -0.000314256996835f, -0.000897597057705f, -0.000562751488849f, -0.000176807273646f, 0.001864627368902f, 0.001864627368902f, -0.000176807273646f, -0.000562751488849f, -0.000897597057705f, -0.000314256996835f, -0.000314256996835f,
    ];

    /// <summary>The image at half its size (rounded up) in each direction.</summary>
    public static JxlImage3F Sharper(JxlImage3F input, CancellationToken cancellationToken)
    {
        int width = input.Width;
        int height = input.Height;
        var output = new JxlImage3F((width + 1) / 2, (height + 1) / 2);
        for (int c = 0; c < 3; c++)
        {
            Sharper(input.Planes[c], width, height, output.Planes[c], output.Width, output.Height, cancellationToken);
        }

        return output;
    }

    private static void Sharper(float[] input, int width, int height, float[] output, int outWidth, int outHeight, CancellationToken cancellationToken)
    {
        float[] box = Box(input, width, height, outWidth, outHeight);
        float[] mask = Mask(box, outWidth, outHeight);
        Span<int> rows = stackalloc int[KernelSize];
        ReadOnlySpan<float> kernel = Kernel;
        for (int y = 0; y < outHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int ky = 0; ky < KernelSize; ky++)
            {
                rows[ky] = Math.Clamp(y * 2 + ky - (KernelSize - 1) / 2, 0, height - 1) * width;
            }

            for (int x = 0; x < outWidth; x++)
            {
                float min = float.MaxValue;
                // std::numeric_limits<float>::min(), the smallest normal value, as libjxl starts.
                float max = 1.17549435e-38f;
                const int radius = 5;
                for (int ky = radius; ky + radius < KernelSize; ky++)
                {
                    for (int kx = radius; kx + radius < KernelSize; kx++)
                    {
                        float value = input[rows[ky] + Math.Clamp(x * 2 + kx - (KernelSize - 1) / 2, 0, width - 1)];
                        min = Math.Min(min, value);
                        max = Math.Max(max, value);
                    }
                }

                float sum = 0;
                for (int ky = 0; ky < KernelSize; ky++)
                {
                    for (int kx = 0; kx < KernelSize; kx++)
                    {
                        sum += input[rows[ky] + Math.Clamp(x * 2 + kx - (KernelSize - 1) / 2, 0, width - 1)] * kernel[ky * KernelSize + kx];
                    }
                }

                float a = mask[y * outWidth + x];
                output[y * outWidth + x] = sum < min - a ? min - a : sum > max + a ? max + a : sum;
            }
        }
    }

    // DownsampleImage: the mean of each 2×2 square (or what of it is inside the image).
    private static float[] Box(float[] input, int width, int height, int outWidth, int outHeight)
    {
        float[] output = new float[outWidth * outHeight];
        for (int y = 0; y < outHeight; y++)
        {
            for (int x = 0; x < outWidth; x++)
            {
                float sum = 0;
                int count = 0;
                for (int iy = 0; iy < 2 && 2 * y + iy < height; iy++)
                {
                    for (int ix = 0; ix < 2 && 2 * x + ix < width; ix++)
                    {
                        sum += input[(2 * y + iy) * width + 2 * x + ix];
                        count++;
                    }
                }

                output[y * outWidth + x] = sum / count;
            }
        }

        return output;
    }

    // CreateMask: the second smallest difference to the four neighbours.
    private static float[] Mask(float[] image, int width, int height)
    {
        float[] mask = new float[image.Length];
        for (int y = 0; y < height; y++)
        {
            int north = (y > 0 ? y - 1 : y) * width;
            int row = y * width;
            int south = (y + 1 < height ? y + 1 : y) * width;
            for (int x = 0; x < width; x++)
            {
                float c = image[row + x];
                float w = x > 0 ? image[row + x - 1] : c;
                float e = x + 1 < width ? image[row + x + 1] : c;
                float min = float.MaxValue;
                float min2 = float.MaxValue;
                foreach (float d in (ReadOnlySpan<float>)[Math.Abs(c - w), Math.Abs(c - e), Math.Abs(c - image[north + x]), Math.Abs(c - image[south + x])])
                {
                    if (d < min2)
                    {
                        if (d < min)
                        {
                            min2 = min;
                            min = d;
                        }
                        else
                        {
                            min2 = d;
                        }
                    }
                }

                mask[row + x] = min2;
            }
        }

        return mask;
    }
}
