namespace PictureSharp.Formats.Jxl;

/// <summary>JPEG XL shortcuts for <see cref="Image"/>; require <see cref="JxlFormat"/> to be registered.</summary>
public static class JxlImageExtensions
{
    extension(Image image)
    {
        /// <summary>Saves the image as JPEG XL (lossy at distance 1.0 by default); null options mean the defaults.</summary>
        public void SaveAsJxl(string path, JxlEncoderOptions? options = null) => image.Save(path, options ?? new());

        /// <summary>Writes the image to a stream as JPEG XL (lossy at distance 1.0 by default); null options mean the defaults. The stream is left open.</summary>
        public void SaveAsJxl(Stream stream, JxlEncoderOptions? options = null) => image.Save(stream, options ?? new());

        /// <summary>Saves the image as JPEG XL (lossy at distance 1.0 by default) asynchronously; null options mean the defaults.</summary>
        public Task SaveAsJxlAsync(string path, JxlEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(path, options ?? new(), cancellationToken);

        /// <summary>Writes the image to a stream as JPEG XL (lossy at distance 1.0 by default) asynchronously; null options mean the defaults.</summary>
        public Task SaveAsJxlAsync(Stream stream, JxlEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(stream, options ?? new(), cancellationToken);

        /// <summary>Encodes the image as JPEG XL (lossy at distance 1.0 by default); null options mean the defaults.</summary>
        public byte[] ToJxl(JxlEncoderOptions? options = null) => image.Encode(options ?? new());
    }
}
