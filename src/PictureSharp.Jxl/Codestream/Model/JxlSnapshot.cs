namespace PictureSharp.Formats.Jxl.Codestream.Model;

/// <summary>Header models keep read-only copies of the collections they are given, so callers cannot change them later.</summary>
internal static class JxlSnapshot
{
    extension<T>(IEnumerable<T> source)
    {
        /// <summary>A read-only copy of the elements.</summary>
        public IReadOnlyList<T> Snapshot() => Array.AsReadOnly(source.ToArray());
    }
}
