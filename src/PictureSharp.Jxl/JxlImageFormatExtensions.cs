namespace PictureSharp.Formats.Jxl;

/// <summary>JPEG XL members of ImageFormat.</summary>
public static class JxlImageFormatExtensions
{
    extension(ImageFormat)
    {
        /// <summary>JPEG XL (image/jxl). Register JxlFormat for detection, Identify, decoding and lossless encoding of still images with metadata (0.6.1).</summary>
        public static ImageFormat Jxl => JxlFormat.Descriptor;
    }
}
