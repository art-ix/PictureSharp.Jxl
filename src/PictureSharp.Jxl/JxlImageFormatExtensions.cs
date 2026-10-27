namespace PictureSharp.Formats.Jxl;

/// <summary>JPEG XL members of ImageFormat.</summary>
public static class JxlImageFormatExtensions
{
    extension(ImageFormat)
    {
        /// <summary>JPEG XL (image/jxl). Register JxlFormat for detection, Identify and lossless decoding (0.3.0); no encoder yet.</summary>
        public static ImageFormat Jxl => JxlFormat.Descriptor;
    }
}
