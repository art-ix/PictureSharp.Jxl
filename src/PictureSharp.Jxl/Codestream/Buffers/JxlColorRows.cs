namespace PictureSharp.Formats.Jxl.Codestream.Buffers;

/// <summary>
/// Colour rows a decoded image renders on demand (0.8.6) instead of holding full-resolution planes; each row band
/// opens its own <see cref="Band"/>, which may keep state between the rows of that band.
/// </summary>
internal abstract class JxlColorRows
{
    public abstract Band Open();

    internal abstract class Band
    {
        /// <summary>Writes row <paramref name="y"/> as R, G and B, each the width of the image.</summary>
        public abstract void Render(int y, Span<float> red, Span<float> green, Span<float> blue);
    }
}
