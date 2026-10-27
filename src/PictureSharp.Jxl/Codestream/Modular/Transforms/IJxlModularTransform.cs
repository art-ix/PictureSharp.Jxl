using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Modular;

namespace PictureSharp.Formats.Jxl.Codestream.Modular.Transforms;

/// <summary>The two phases of a decoded Modular transform; implementations own any derived working state.</summary>
internal interface IJxlModularTransform
{
    void MetaApply(JxlModularImage image);
    void Inverse(JxlModularImage image, JxlWeightedHeader weighted, CancellationToken cancellationToken);
}
