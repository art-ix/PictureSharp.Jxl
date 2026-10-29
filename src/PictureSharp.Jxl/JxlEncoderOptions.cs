namespace PictureSharp.Formats.Jxl;

/// <summary>
/// Options for the JPEG XL encoder. By default the root frame is encoded lossily (VarDCT in the XYB colour space) at
/// butteraugli distance 1.0 and effort 7, as <c>cjxl</c> does for non-JPEG input; <see cref="Quality"/> 100 or
/// <see cref="Distance"/> 0 encode losslessly as one 8-bit or 16-bit Modular frame. Alpha is always stored losslessly. The image
/// is gray when every pixel is gray (unless an RGB ICC profile is written). When <see cref="EncoderOptions.Metadata"/>
/// allows them, the ICC profile is stored in the codestream (lossy frames store a profile that an enumerated colour
/// encoding describes, such as Display P3, as that encoding, like <c>cjxl</c>) and EXIF and XMP in Exif and xml boxes of a container;
/// without an ICC profile the colour description (gamma, chromaticities, sRGB) becomes the enumerated colour encoding.
/// The orientation is stored in the codestream, where JPEG XL readers apply it.
/// </summary>
/// <example>
/// <code>
/// image.SaveAsJxl("photo.jxl", new JxlEncoderOptions { Quality = 85, Effort = 5 });
/// image.SaveAsJxl("exact.jxl", new JxlEncoderOptions { Distance = 0 });
/// </code>
/// </example>
public sealed record JxlEncoderOptions : EncoderOptions
{
    /// <summary>The default <see cref="Quality"/>, which corresponds to distance 1.0.</summary>
    public const float DefaultQuality = 90;

    /// <summary>The default <see cref="Effort"/>.</summary>
    public const int DefaultEffort = 7;

    /// <inheritdoc/>
    public override ImageFormat Format => JxlFormat.Descriptor;

    /// <summary>
    /// Quality 0–100 on <c>cjxl -q</c>'s scale: 100 is lossless, 90 is distance 1.0 (visually lossless), and from 30
    /// down to 0 the distance rises to 25. It is used when <see cref="Distance"/> is null. Default:
    /// <see cref="DefaultQuality"/>.
    /// </summary>
    public float Quality
    {
        get;
        init => field = value is >= 0 and <= 100 ? value : throw new ArgumentOutOfRangeException(nameof(Quality), value, "Use a value from 0 to 100.");
    } = DefaultQuality;

    /// <summary>
    /// The butteraugli distance 0–25 as <c>cjxl -d</c>: 0 is lossless, 1.0 visually lossless, higher is smaller and
    /// less faithful; values between 0 and 0.05 are encoded at 0.05, and from 10 the frame is stored at half size and
    /// upsampled by the decoder. When set it takes precedence over <see cref="Quality"/>. Default: null.
    /// </summary>
    public float? Distance
    {
        get;
        init => field = value is null or (>= 0 and <= 25) ? value : throw new ArgumentOutOfRangeException(nameof(Distance), value, "Use a value from 0 to 25.");
    }

    /// <summary>
    /// Encoder effort 1–9 as <c>cjxl -e</c>: higher is slower and usually smaller. Lossy frames search more transforms
    /// and quantization choices; efforts 8 and 9 behave as 7 (the butteraugli-guided quantization of libjxl is not
    /// implemented). Lossless frames follow libjxl's Modular tiers: effort 1 codes RGB as YCoCg with the gradient
    /// predictor and one context per channel, 2 and 3 use libjxl's fixed gradient and weighted-predictor trees, 4–6 learn
    /// a smaller tree from fewer samples, and 7 (also 8 and 9) learns the full tree and searches the colour transform.
    /// Default: <see cref="DefaultEffort"/>.
    /// </summary>
    public int Effort
    {
        get;
        init => field = value is >= 1 and <= 9 ? value : throw new ArgumentOutOfRangeException(nameof(Effort), value, "Use a value from 1 to 9.");
    } = DefaultEffort;

    /// <summary>
    /// Whether EXIF and XMP are Brotli-compressed into brob boxes, as cjxl does by default. Readers without Brotli
    /// support then cannot see them. Default: false.
    /// </summary>
    public bool CompressMetadata { get; init; }

    /// <summary>
    /// Maximum number of threads to use (1 = single-threaded, -1 = automatic). Output does not depend on it.
    /// Default: -1.
    /// </summary>
    public int MaxDegreeOfParallelism
    {
        get;
        init => field = value == -1 || value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism), value, "Expected -1 or a positive number.");
    } = -1;

    /// <summary>The distance that will be encoded: <see cref="Distance"/>, or <see cref="Quality"/> mapped as cjxl does.</summary>
    public float EffectiveDistance => Distance ?? (float)(Quality >= 100 ? 0
        : Quality >= 30 ? 0.1 + (100 - Quality) * 0.09
        : 53.0 / 3000.0 * Quality * Quality - 23.0 / 20.0 * Quality + 25.0);
}
