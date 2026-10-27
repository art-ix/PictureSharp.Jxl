using PictureSharp.Formats.Jxl.Codestream.VarDct;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// The AC tokens of every group, the counterpart of <see cref="JxlVarDctFrame.DecodePass"/>: per varblock and channel
/// the number of non-zero coefficients, predicted from the blocks above and to the left, then the coefficients in
/// order until the last non-zero one; and the block context model. Ported from libjxl v0.12.0
/// lib/jxl/enc_entropy_coder.cc (TokenizeCoefficients) and enc_heuristics.cc (FindBestBlockEntropyModel; BSD-3-Clause).
/// </summary>
internal static class JxlAcTokenizer
{
    private const int GroupBlocks = JxlLossyFrame.GroupBlocks;

    public static JxlTokens Tokenize(JxlLossyFrame frame, int group, int[] orders)
    {
        (int x0, int y0, int width, int height) = frame.GroupRect(group);
        int area = width * height * JxlAcStrategy.BlockSize;
        int[] coefficients = frame.Coefficients[group];
        JxlBlockContextMap contexts = frame.BlockContexts;
        int[][] nonZeros = [new int[GroupBlocks * GroupBlocks], new int[GroupBlocks * GroupBlocks], new int[GroupBlocks * GroupBlocks]];
        var tokens = new JxlTokens();
        int offset = 0;
        for (int by = 0; by < height; by++)
        {
            int row = (y0 + by) * frame.XBlocks + x0;
            for (int bx = 0; bx < width; bx++)
            {
                JxlVarblock varblock = frame.Strategies[row + bx];
                if (!varblock.IsFirst) continue;
                JxlAcStrategyType type = varblock.Type;
                int covered = type.BlocksX * type.BlocksY;
                int log2Covered = type.Log2Blocks;
                int size = JxlAcStrategy.BlockSize << log2Covered;
                int cx = Math.Max(type.BlocksX, type.BlocksY);
                int cy = Math.Min(type.BlocksX, type.BlocksY);
                foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
                {
                    ReadOnlySpan<int> block = coefficients.AsSpan(c * area + offset, size);
                    int count = 0;
                    for (int k = 0; k < size; k++)
                    {
                        if (block[k] != 0 && !(k % (cx * 8) < cx && k / (cx * 8) < cy)) count++;
                    }

                    int[] counts = nonZeros[c];
                    int predicted = bx == 0 ? (by == 0 ? 32 : counts[(by - 1) * GroupBlocks])
                        : by == 0 ? counts[by * GroupBlocks + bx - 1]
                        : (counts[(by - 1) * GroupBlocks + bx] + counts[by * GroupBlocks + bx - 1] + 1) / 2;
                    int shifted = (count + covered - 1) >> log2Covered;
                    for (int iy = 0; iy < type.BlocksY; iy++)
                    {
                        counts.AsSpan((by + iy) * GroupBlocks + bx, type.BlocksX).Fill(shifted);
                    }

                    int bucket = type.OrderBucket;
                    int blockContext = contexts.Context(0, frame.QuantField[row + bx], bucket, c);
                    tokens.Add(contexts.NonZeroContext(predicted, blockContext), (uint)count);
                    int histogramOffset = contexts.ZeroDensityContextsOffset(blockContext);
                    int orderOffset = JxlAcStrategy.OrderOffset(bucket, c);
                    int previous = count > size / 16 ? 0 : 1;
                    for (int k = covered; k < size && count != 0; k++)
                    {
                        int coefficient = block[orders[orderOffset + k]];
                        tokens.Add(histogramOffset + JxlBlockContextMap.ZeroDensityContext(count, k, log2Covered, previous), PackSigned(coefficient));
                        previous = coefficient != 0 ? 1 : 0;
                        count -= previous;
                    }
                }

                offset += size;
            }
        }

        return tokens;
    }

    /// <summary>
    /// Splits the block contexts by quantization value and order bucket for larger images, merging the rarest
    /// combinations (FindBestBlockEntropyModel); small images keep the default map.
    /// </summary>
    public static JxlBlockContextMap BlockEntropyModel(JxlLossyFrame frame)
    {
        if (!frame.Settings.CustomOrders) return JxlBlockContextMap.Default;
        long total = (long)frame.XBlocks * frame.YBlocks;
        float distance = frame.Settings.Distance;
        long sizeForContextModel = (long)((1 << 10) * distance);
        if (total < sizeForContextModel) return JxlBlockContextMap.Default;

        const int orders = JxlAcStrategy.Orders;
        long[] qfCounts = new long[256];
        long[,] qfOrderCounts = new long[orders, 256];
        for (int i = 0; i < frame.Strategies.Length; i++)
        {
            int order = frame.Strategies[i].Type.OrderBucket;
            int qf = frame.QuantField[i] - 1;
            qfCounts[qf]++;
            qfOrderCounts[order, qf]++;
        }

        // Up to two segments of the quantization field, for large images.
        long sizeForQfSplit = (long)((1 << 13) * distance);
        int segments = total < sizeForQfSplit ? 1 : 2;
        var thresholds = new List<uint>();
        long cumulative = 0;
        int next = 1;
        int lastCut = 256;
        long cut = total * next / segments;
        for (uint j = 0; j < 256; j++)
        {
            cumulative += qfCounts[j];
            if (cumulative > cut)
            {
                if (j != 0) thresholds.Add(j);
                lastCut = (int)j;
                while (cumulative > cut)
                {
                    next++;
                    cut = total * next / segments;
                }
            }
            else if (next > thresholds.Count + 1)
            {
                if (j - 1 == lastCut && j != 0) thresholds.Add(j);
            }
        }

        int qfBuckets = thresholds.Count + 1;
        long[] counts = new long[orders * qfBuckets];
        int position = 0;
        for (int j = 0; j < 256; j++)
        {
            if (position < thresholds.Count && j == thresholds[position]) position++;
            for (int i = 0; i < orders; i++) counts[position + i * qfBuckets] += qfOrderCounts[i, j];
        }

        // Repeatedly merge the lowest-count pair.
        byte[] remap = [.. Enumerable.Range(0, qfBuckets * orders).Select(i => (byte)i)];
        var clusters = new List<byte>(remap);
        int clusterCount = (int)Math.Clamp(total / sizeForContextModel / 2, 2, 9);
        int chromaClusters = (int)Math.Clamp(total / sizeForContextModel / 3, 1, 5);
        while (clusters.Count > clusterCount)
        {
            // A stable sort by descending count, as std::sort leaves equal counts in an unspecified order.
            clusters = [.. clusters.OrderByDescending(cluster => counts[cluster])];
            byte last = clusters[^1];
            byte target = clusters[^2];
            counts[target] += counts[last];
            counts[last] = 0;
            remap[last] = target;
            clusters.RemoveAt(clusters.Count - 1);
        }

        for (int i = 0; i < remap.Length; i++)
        {
            while (remap[remap[i]] != remap[i]) remap[i] = remap[remap[i]];
        }

        // Relabel from zero.
        byte[] relabel = new byte[remap.Length];
        Array.Fill(relabel, (byte)remap.Length);
        byte labels = 0;
        for (int i = 0; i < remap.Length; i++)
        {
            if (relabel[remap[i]] == remap.Length) relabel[remap[i]] = labels++;
            remap[i] = relabel[remap[i]];
        }

        // Luma has a context per cluster; chroma uses up to chromaClusters contexts, those of the biggest clusters.
        byte[] map = new byte[remap.Length * 3];
        remap.CopyTo(map, 0);
        for (int i = remap.Length; i < map.Length; i++)
        {
            map[i] = (byte)(labels + Math.Clamp(remap[i % remap.Length], 0, chromaClusters - 1));
        }

        return JxlBlockContextMap.Create([.. thresholds], map);
    }
}
