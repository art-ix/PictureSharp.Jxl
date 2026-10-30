namespace PictureSharp.Formats.Jxl.Codestream.IO
{
    /// <summary>Records the bit position and width of the first reads of the bit reader (JXL_FUZZ_TRACE).</summary>
    public static class JxlFuzzTrace
    {
        public const int Limit = 4096;

        private static List<(long Bit, int Count)>? reads;

        public static void Read(long bit, int count)
        {
            if (reads is { Count: < Limit } list && count > 0) list.Add((bit, count));
        }

        /// <summary>The reads of <paramref name="action"/>; the caller must run it on one thread.</summary>
        public static List<(long Bit, int Count)> Record(Action action)
        {
            var list = new List<(long, int)>();
            reads = list;
            try
            {
                action();
            }
            catch (Exception)
            {
                // A seed that fails part way still gives the fields before the failure.
            }
            finally
            {
                reads = null;
            }

            return list;
        }
    }
}
