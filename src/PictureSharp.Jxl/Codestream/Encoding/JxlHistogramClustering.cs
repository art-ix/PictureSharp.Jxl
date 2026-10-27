namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// Groups the token histograms of contexts into at most 64 clusters that share one code. Modelled on libjxl v0.12.0
/// lib/jxl/enc_cluster.cc (FastClusterHistograms; BSD-3-Clause), followed by pairwise merges while they save bits.
/// </summary>
internal static class JxlHistogramClustering
{
    private const int MaxClusters = 64;
    // Merging histograms that cost less than this apart saves their header.
    private const double MinClusterDistance = 64;

    /// <summary>The cluster of every context, numbered by first use; unused contexts map to cluster 0.</summary>
    public static byte[] Cluster(long[][] histograms, out int clusterCount)
    {
        int contexts = histograms.Length;
        int[] used = [.. Enumerable.Range(0, contexts).Where(c => histograms[c].Any(v => v > 0))];
        if (used.Length <= 1)
        {
            clusterCount = 1;
            return new byte[contexts];
        }

        List<List<int>> members = SeedClusters(histograms, used);
        MergeClusters(histograms, members);
        return NumberByFirstUse(contexts, used, members, out clusterCount);
    }

    // Farthest-point seeding and nearest-centre assignment (FastClusterHistograms): the members of every cluster.
    private static List<List<int>> SeedClusters(long[][] histograms, int[] used)
    {
        int contexts = histograms.Length;
        double[] own = new double[contexts];
        foreach (int c in used) own[c] = Entropy(histograms[c]);
        var centres = new List<int> { used.MaxBy(c => histograms[c].Sum()) };
        double[] distance = new double[contexts];
        int[] nearest = new int[contexts];
        foreach (int c in used)
        {
            distance[c] = MergeCost(histograms[c], own[c], histograms[centres[0]], own[centres[0]]);
        }

        while (centres.Count < MaxClusters)
        {
            int farthest = used.MaxBy(c => distance[c]);
            if (distance[farthest] < MinClusterDistance) break;
            centres.Add(farthest);
            foreach (int c in used)
            {
                double d = MergeCost(histograms[c], own[c], histograms[farthest], own[farthest]);
                if (d < distance[c])
                {
                    distance[c] = d;
                    nearest[c] = centres.Count - 1;
                }
            }
        }

        var members = new List<List<int>>();
        for (int i = 0; i < centres.Count; i++) members.Add([]);
        foreach (int c in used) members[nearest[c]].Add(c);
        return members;
    }

    // Merges the cheapest pair of clusters while that costs less than the histogram header it saves.
    private static void MergeClusters(long[][] histograms, List<List<int>> members)
    {
        var sums = new List<long[]>();
        foreach (List<int> cluster in members)
        {
            long[] sum = [];
            foreach (int c in cluster) sum = Add(sum, histograms[c]);
            sums.Add(sum);
        }

        while (sums.Count > 1)
        {
            double[] costs = [.. sums.Select(Entropy)];
            (int a, int b, double delta) best = (-1, -1, double.MaxValue);
            for (int a = 0; a < sums.Count; a++)
            {
                for (int b = a + 1; b < sums.Count; b++)
                {
                    double delta = MergeCost(sums[a], costs[a], sums[b], costs[b]);
                    if (delta < best.delta) best = (a, b, delta);
                }
            }

            if (best.delta >= HeaderCost(sums[best.a]) + HeaderCost(sums[best.b]) - HeaderCost(Add(sums[best.a], sums[best.b])))
            {
                break;
            }

            sums[best.a] = Add(sums[best.a], sums[best.b]);
            members[best.a].AddRange(members[best.b]);
            sums.RemoveAt(best.b);
            members.RemoveAt(best.b);
        }
    }

    // Numbers clusters by their first context, so the map starts 0, 1, 2… and suits move-to-front.
    private static byte[] NumberByFirstUse(int contexts, int[] used, List<List<int>> members, out int clusterCount)
    {
        int[] clusterOf = new int[contexts];
        for (int i = 0; i < members.Count; i++)
        {
            foreach (int c in members[i]) clusterOf[c] = i;
        }

        byte[] map = new byte[contexts];
        int[] renumber = new int[members.Count];
        Array.Fill(renumber, -1);
        int next = 0;
        foreach (int c in used)
        {
            if (renumber[clusterOf[c]] < 0) renumber[clusterOf[c]] = next++;
            map[c] = (byte)renumber[clusterOf[c]];
        }

        clusterCount = next;
        return map;
    }

    private static double HeaderCost(long[] histogram) => 16 + 5 * histogram.Count(c => c > 0);

    private static long[] Add(long[] a, long[] b)
    {
        long[] sum = new long[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < a.Length; i++) sum[i] += a[i];
        for (int i = 0; i < b.Length; i++) sum[i] += b[i];
        return sum;
    }

    private static double MergeCost(long[] a, double costA, long[] b, double costB)
    {
        long totalA = 0;
        long totalB = 0;
        foreach (long v in a) totalA += v;
        foreach (long v in b) totalB += v;
        long total = totalA + totalB;
        double merged = 0;
        int length = Math.Max(a.Length, b.Length);
        for (int i = 0; i < length; i++)
        {
            long v = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
            if (v > 0) merged += v * Math.Log2((double)total / v);
        }

        return merged - costA - costB;
    }

    /// <summary>The Shannon cost in bits of coding the histogram's symbols with their own probabilities.</summary>
    public static double Entropy(long[] histogram)
    {
        long total = 0;
        foreach (long v in histogram) total += v;
        double bits = 0;
        foreach (long v in histogram)
        {
            if (v > 0 && v < total) bits += v * Math.Log2((double)total / v);
        }

        return bits;
    }
}
