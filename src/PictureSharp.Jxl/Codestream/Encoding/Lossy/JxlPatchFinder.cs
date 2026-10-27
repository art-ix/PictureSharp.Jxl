namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>A patch of the reference image and the positions where it is added to the frame.</summary>
internal sealed record JxlPatchEntry(int ReferenceX, int ReferenceY, int Width, int Height, List<(int X, int Y)> Positions);

/// <summary>
/// Repeated small shapes on flat backgrounds, such as the glyphs of text in screenshots (FindTextLikePatches): seeds of
/// flat 4×4 areas grow into the background, the connected components on it that recur at least twice become patches,
/// packed into a reference image. Ported from libjxl v0.12.0 lib/jxl/enc_patch_dictionary.cc (BSD-3-Clause); dots are
/// not detected.
/// </summary>
internal static class JxlPatchFinder
{
    private const int PatchSide = 4;
    private const int MaxPatchSize = 32;
    private const int SearchRadius = 1;
    private const int DistanceLimit = 50;
    private const float SimilarThreshold = 0.8f;
    private const float VerySimilarThreshold = 0.03f;
    private const float HasSimilarThreshold = 0.03f;
    private const int MinPeak = 2;
    private const int HasSimilarRadius = 2;
    private const int MinPatchOccurrences = 2;
    private const int MinMaxPatchSize = 20;

    // PatchColorspaceInfo for XYB.
    private static ReadOnlySpan<float> ChannelDequant => [0.01615f, 0.08875f, 0.1922f];
    private static ReadOnlySpan<float> ChannelWeights => [30.0f, 3.0f, 1.0f];

    private sealed record Candidate(int Width, int Height, sbyte[] Quantized, float[] Values, List<(int X, int Y)> Positions);

    /// <summary>
    /// The patches of a <paramref name="width"/>×<paramref name="height"/> XYB image (stored with its own stride) and
    /// the reference image holding them, or null when there are none worth coding.
    /// </summary>
    public static (List<JxlPatchEntry> Patches, JxlImage3F Reference)? Find(JxlImage3F image, int width, int height, CancellationToken cancellationToken)
    {
        List<Candidate> candidates = FindTextLikePatches(image, width, height, cancellationToken);
        if (candidates.Count == 0) return null;

        // Larger patches are placed first; libjxl's sort is not stable, this one is.
        candidates = [.. candidates.OrderByDescending(candidate => candidate.Width * candidate.Height)];
        int maxWidth = candidates.Max(candidate => candidate.Width);
        int maxHeight = candidates.Max(candidate => candidate.Height);
        long totalPixels = candidates.Sum(candidate => (long)candidate.Width * candidate.Height);
        const float slackness = 1.05f;
        int referenceWidth = (int)Math.Max(maxWidth, MathF.Sqrt(totalPixels));
        int referenceHeight = (int)Math.Max(maxHeight, MathF.Sqrt(totalPixels));
        var positions = new (int X, int Y)[candidates.Count];
        int usedHeight;
        while (true)
        {
            usedHeight = 0;
            referenceWidth = (int)(referenceWidth * slackness + 1);
            referenceHeight = (int)(referenceHeight * slackness + 1);
            bool[] occupied = new bool[referenceWidth * referenceHeight];
            bool success = true;
            for (int i = 0; i < candidates.Count && success; i++)
            {
                (int w, int h) = (candidates[i].Width, candidates[i].Height);
                success = Place(occupied, referenceWidth, referenceHeight, w, h, out int x0, out int y0);
                if (!success) break;
                positions[i] = (x0, y0);
                for (int y = y0; y < y0 + h; y++) occupied.AsSpan(y * referenceWidth + x0, w).Fill(true);
                usedHeight = Math.Max(usedHeight, y0 + h);
            }

            if (success) break;
        }

        var reference = new JxlImage3F(referenceWidth, usedHeight);
        var patches = new List<JxlPatchEntry>();
        for (int i = 0; i < candidates.Count; i++)
        {
            Candidate candidate = candidates[i];
            (int x0, int y0) = positions[i];
            int size = candidate.Width * candidate.Height;
            for (int c = 0; c < 3; c++)
            {
                for (int y = 0; y < candidate.Height; y++)
                {
                    candidate.Values.AsSpan(c * size + y * candidate.Width, candidate.Width).CopyTo(reference.Row(c, y0 + y)[x0..]);
                }
            }

            patches.Add(new JxlPatchEntry(x0, y0, candidate.Width, candidate.Height, candidate.Positions));
        }

        return (patches, reference);
    }

    // The first free w×h position, row by row, skipping past occupied pixels.
    private static bool Place(bool[] occupied, int width, int height, int w, int h, out int x0, out int y0)
    {
        for (y0 = 0; y0 + h <= height; y0++)
        {
            for (x0 = 0; x0 + w <= width; x0++)
            {
                bool hasOccupied = false;
                int x = x0;
                for (int y = y0; y < y0 + h; y++)
                {
                    x = x0;
                    for (; x < x0 + w; x++)
                    {
                        if (occupied[y * width + x])
                        {
                            hasOccupied = true;
                            break;
                        }
                    }
                }

                if (!hasOccupied) return true;
                x0 = x;
            }
        }

        x0 = 0;
        return false;
    }

    private static List<Candidate> FindTextLikePatches(JxlImage3F image, int width, int height, CancellationToken cancellationToken)
    {
        var info = new List<Candidate>();
        int stride = image.Width;
        float[][] p = image.Planes;
        int pw = width / PatchSide;
        int ph = height / PatchSide;
        if (pw < 3 || ph < 3) return info;

        bool SameColor(int x, int y, float c0, float c1, float c2)
        {
            int offset = y * stride + x;
            return Math.Abs(c0 - p[0][offset]) <= 1e-4f && Math.Abs(c1 - p[1][offset]) <= 1e-4f && Math.Abs(c2 - p[2][offset]) <= 1e-4f;
        }

        // Flat 4×4 areas surrounded by the same colour seed the background.
        bool[] screenshotLike = new bool[pw * ph];
        int seeds = 0;
        for (int py = 1; py < ph - 1; py++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int px = 1; px <= pw - 2; px++)
            {
                int ox = px * PatchSide;
                int oy = py * PatchSide;
                int origin = oy * stride + ox;
                (float b0, float b1, float b2) = (p[0][origin], p[1][origin], p[2][origin]);
                bool flat = true;
                for (int iy = 0; iy < PatchSide && flat; iy++)
                {
                    for (int ix = 0; ix < PatchSide && flat; ix++) flat = SameColor(ox + ix, oy + iy, b0, b1, b2);
                }

                if (!flat) continue;
                int same = 0;
                for (int y = (py - 1) * PatchSide; y <= (py + 1) * PatchSide; y += PatchSide)
                {
                    for (int x = (px - 1) * PatchSide; x <= (px + 1) * PatchSide; x += PatchSide) same += SameColor(x, y, b0, b1, b2) ? 1 : 0;
                }

                if (same < 8) continue;
                screenshotLike[py * pw + px] = true;
                seeds++;
            }
        }

        if (seeds == 0) return info;

        // Grow the background from the seeds through similar colours.
        bool[] isBackground = new bool[width * height];
        float[][] background = [new float[width * height], new float[width * height], new float[width * height]];
        var queue = new List<((int X, int Y) Current, (int X, int Y) Source)>();
        for (int py = 1; py < ph - 1; py++)
        {
            for (int px = 1; px < pw - 1; px++)
            {
                if (!screenshotLike[py * pw + px]) continue;
                for (int y = py * PatchSide; y < (py + 1) * PatchSide; y++)
                {
                    for (int x = px * PatchSide; x < (px + 1) * PatchSide; x++)
                    {
                        queue.Add(((x, y), (x, y)));
                        isBackground[y * width + x] = true;
                    }
                }
            }
        }

        for (int front = 0; front < queue.Count; front++)
        {
            if ((front & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            ((int cx, int cy), (int sx, int sy)) = queue[front];
            int source = sy * stride + sx;
            for (int c = 0; c < 3; c++) background[c][cy * width + cx] = p[c][source];
            for (int dx = -SearchRadius; dx <= SearchRadius; dx++)
            {
                for (int dy = -SearchRadius; dy <= SearchRadius; dy++)
                {
                    int nx = cx + dx;
                    int ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height || isBackground[ny * width + nx]) continue;
                    if (Math.Abs(nx - sx) + Math.Abs(ny - sy) > DistanceLimit) continue;
                    if (Similar(p, source, p, ny * stride + nx, SimilarThreshold))
                    {
                        queue.Add(((nx, ny), (sx, sy)));
                        isBackground[ny * width + nx] = true;
                    }
                }
            }
        }

        queue.Clear();

        // Connected components on a uniform background become candidate patches.
        bool[] visited = new bool[width * height];
        var stack = new Stack<(int X, int Y)>();
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                if (isBackground[y * width + x]) continue;
                stack.Clear();
                stack.Push((x, y));
                int minX = x, maxX = x, minY = y, maxY = y;
                (int X, int Y) reference = default;
                bool foundBorder = false;
                bool allSimilar = true;
                while (stack.Count > 0)
                {
                    (int cx, int cy) = stack.Pop();
                    if (visited[cy * width + cx]) continue;
                    visited[cy * width + cx] = true;
                    minX = Math.Min(minX, cx);
                    maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy);
                    maxY = Math.Max(maxY, cy);
                    for (int dx = -SearchRadius; dx <= SearchRadius; dx++)
                    {
                        for (int dy = -SearchRadius; dy <= SearchRadius; dy++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = cx + dx;
                            int ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                            if (!isBackground[ny * width + nx])
                            {
                                stack.Push((nx, ny));
                            }
                            else if (!foundBorder)
                            {
                                reference = (nx, ny);
                                foundBorder = true;
                            }
                            else if (!Similar(background, ny * width + nx, background, reference.Y * width + reference.X, VerySimilarThreshold))
                            {
                                allSimilar = false;
                            }
                        }
                    }
                }

                if (!foundBorder || !allSimilar || maxX - minX >= MaxPatchSize || maxY - minY >= MaxPatchSize) continue;
                int backgroundAt = reference.Y * width + reference.X;
                float r0 = background[0][backgroundAt];
                float r1 = background[1][backgroundAt];
                float r2 = background[2][backgroundAt];
                bool hasSimilar = false;
                for (int iy = Math.Max(minY - HasSimilarRadius, 0); iy < Math.Min(maxY + HasSimilarRadius + 1, height) && !hasSimilar; iy++)
                {
                    for (int ix = Math.Max(minX - HasSimilarRadius, 0); ix < Math.Min(maxX + HasSimilarRadius + 1, width); ix++)
                    {
                        int at = iy * stride + ix;
                        if (Distance(r0, r1, r2, p[0][at], p[1][at], p[2][at]) <= HasSimilarThreshold)
                        {
                            hasSimilar = true;
                            break;
                        }
                    }
                }

                if (!hasSimilar) continue;
                int patchWidth = maxX - minX + 1;
                int patchHeight = maxY - minY + 1;
                int size = patchWidth * patchHeight;
                sbyte[] quantized = new sbyte[3 * size];
                float[] values = new float[3 * size];
                bool tooBig = false;
                bool tooSmall = true;
                ReadOnlySpan<float> referenceColor = [r0, r1, r2];
                foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
                {
                    for (int iy = minY; iy <= maxY; iy++)
                    {
                        for (int ix = minX; ix <= maxX; ix++)
                        {
                            int offset = c * size + (iy - minY) * patchWidth + ix - minX;
                            float value = p[c][iy * stride + ix] - referenceColor[c];
                            values[offset] = value;
                            int q = (int)MathF.Truncate(Math.Clamp(value / ChannelDequant[c], -32768.0f, 32767.0f));
                            quantized[offset] = unchecked((sbyte)q);
                            tooBig |= q != quantized[offset];
                            tooSmall &= q < MinPeak && q > -MinPeak;
                        }
                    }
                }

                if (tooSmall || tooBig) continue;
                info.Add(new Candidate(patchWidth, patchHeight, quantized, values, [(minX, minY)]));
            }
        }

        if (info.Count == 0) return info;

        // Identical quantized patches are merged; those occurring once are dropped.
        info = [.. info.OrderBy(candidate => candidate, CandidateComparer.Instance)];
        var unique = new List<Candidate>();
        foreach (Candidate candidate in info)
        {
            if (unique.Count > 0 && CandidateComparer.Instance.Compare(unique[^1], candidate) == 0)
            {
                unique[^1].Positions.AddRange(candidate.Positions);
            }
            else
            {
                if (unique.Count > 0 && unique[^1].Positions.Count < MinPatchOccurrences) unique.RemoveAt(unique.Count - 1);
                unique.Add(candidate);
            }
        }

        if (unique.Count > 0 && unique[^1].Positions.Count < MinPatchOccurrences) unique.RemoveAt(unique.Count - 1);
        return unique.Count == 0 || unique.Max(candidate => candidate.Width * candidate.Height) < MinMaxPatchSize ? [] : unique;
    }

    private static bool Similar(float[][] a, int ia, float[][] b, int ib, float threshold) =>
        Distance(a[0][ia], a[1][ia], a[2][ia], b[0][ib], b[1][ib], b[2][ib]) <= threshold;

    private static float Distance(float a0, float a1, float a2, float b0, float b1, float b2) =>
        Math.Abs(a0 - b0) * ChannelWeights[0] + Math.Abs(a1 - b1) * ChannelWeights[1] + Math.Abs(a2 - b2) * ChannelWeights[2];

    // QuantizedPatch::operator<: size, then the quantized samples as unsigned bytes, channel by channel.
    private sealed class CandidateComparer : IComparer<Candidate>
    {
        public static CandidateComparer Instance { get; } = new();

        public int Compare(Candidate? a, Candidate? b)
        {
            if (a!.Width != b!.Width) return a.Width.CompareTo(b.Width);
            if (a.Height != b.Height) return a.Height.CompareTo(b.Height);
            for (int i = 0; i < a.Quantized.Length; i++)
            {
                int cmp = ((byte)a.Quantized[i]).CompareTo((byte)b.Quantized[i]);
                if (cmp != 0) return cmp;
            }

            return 0;
        }
    }
}
