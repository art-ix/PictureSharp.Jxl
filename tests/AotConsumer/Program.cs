using PictureSharp;
using PictureSharp.Formats.Jxl;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();
byte[] data = [0xFF, 0x0A];
if (Image.DetectFormat(data) != ImageFormat.Jxl || ImageConfiguration.Default.CanEncode(ImageFormat.Jxl))
    throw new InvalidOperationException("Registration or detection failed.");
try
{
    using var image = Image.Load(data);
    throw new InvalidOperationException("The scaffold unexpectedly decoded an image.");
}
catch (NotSupportedException exception) when (exception.Message.Contains("0.1.0", StringComparison.Ordinal)) { }
Console.WriteLine("Package consumer: registration, detection and unsupported decoding passed.");
