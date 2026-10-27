namespace PictureSharp.Formats.Jxl;

/// <summary>JPEG XL members of ImageFormat.</summary>
public static class JxlImageFormatExtensions
{
    extension(ImageFormat)
    {
        /// <summary>JPEG XL (image/jxl). Register JxlFormat for detection and Identify; pixel codecs are not implemented in 0.2.1.</summary>
        public static ImageFormat Jxl => JxlFormat.Descriptor;
    }
}
