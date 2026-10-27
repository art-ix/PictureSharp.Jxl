using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PictureSharp.Formats.Jxl.Codestream.Color;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>ICC profiles, colour descriptions, EXIF/XMP boxes (plain and Brotli-compressed) and orientation.</summary>
public sealed class JxlMetadataTests
{
    private static ImageMetadata Read(byte[] data, DecoderOptions? options = null) =>
        Image.ReadMetadata((ReadOnlyMemory<byte>)data, Options(options));

    private static ImageMetadata Read(string name, DecoderOptions? options = null) => Read(File.ReadAllBytes(CorpusPath(name)), options);

    [Test]
    [Arguments("icc-profile.jxl", "RGB ")]
    [Arguments("icc-lossy-rgb.jxl", "RGB ")]
    [Arguments("icc-lossy-gray.jxl", "GRAY")]
    public async Task Icc_ProfileIsDecodedAttachedAndRead(string name, string colorSpace)
    {
        using Image image = Image.Load(CorpusPath(name), Options());
        byte[]? icc = image.Metadata.IccProfile;
        await Assert.That(icc).IsNotNull();
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(icc!)).IsEqualTo((uint)icc!.Length);
        await Assert.That(Encoding.ASCII.GetString(icc, 36, 4)).IsEqualTo("acsp");
        await Assert.That(Encoding.ASCII.GetString(icc, 16, 4)).IsEqualTo(colorSpace);
        await Assert.That(image.Metadata.HasColorDescription).IsFalse();
        await Assert.That(Read(name).IccProfile).IsEquivalentTo(icc);
    }

    [Test]
    public async Task Icc_XybIsRenderedInMatrixAndGrayProfiles()
    {
        byte[] rgb = Read("icc-lossy-rgb.jxl").IccProfile!;
        byte[] gray = Read("icc-lossy-gray.jxl").IccProfile!;
        await Assert.That(JxlIccTarget.TryCreate(rgb, gray: false)).IsNotNull();
        await Assert.That(JxlIccTarget.TryCreate(gray, gray: true)).IsNotNull();
        // The colour space must match the image.
        await Assert.That(JxlIccTarget.TryCreate(rgb, gray: true)).IsNull();
        await Assert.That(JxlIccTarget.TryCreate(gray, gray: false)).IsNull();
        await Assert.That(JxlIccTarget.TryCreate(rgb.AsSpan(0, 200), gray: false)).IsNull();
        // Profiles with lookup tables need a colour management system; such images are rendered as sRGB.
        byte[] lut = (byte[])rgb.Clone();
        int tags = (int)BinaryPrimitives.ReadUInt32BigEndian(lut.AsSpan(128));
        for (int i = 0; i < tags; i++)
        {
            if (Encoding.ASCII.GetString(lut, 132 + i * 12, 4) == "cprt") "A2B0"u8.CopyTo(lut.AsSpan(132 + i * 12));
        }

        await Assert.That(JxlIccTarget.TryCreate(lut, gray: false)).IsNull();
        // The 1024-entry tone curve is inverted exactly at its points.
        JxlToneCurve curve = JxlIccTarget.TryCreate(gray, gray: true)!.Curves[0];
        await Assert.That(curve.Encode(0)).IsEqualTo(0f);
        await Assert.That(curve.Encode(1)).IsEqualTo(1f);
        await Assert.That(curve.Encode(2)).IsEqualTo(1f);
        double half = Math.Pow(0.5, 2.2) * (1 + 0.04 * Math.Sin(Math.PI * 0.5));
        await Assert.That(Math.Abs(curve.Encode((float)half) - 0.5)).IsLessThan(1e-3);
    }

    [Test]
    public async Task Icc_ConversionToSrgbUsesTheProfile()
    {
        using Image managed = Image.Load(CorpusPath("icc-lossy-rgb.jxl"), Options());
        using Image srgb = Image.Load(CorpusPath("icc-lossy-rgb.jxl"), Options() with { ColorConversion = ColorConversionMode.ToSrgb });
        await Assert.That(srgb.Metadata.IccProfile).IsNull();
        await Assert.That(JxlDecodeOracleTests.Rgba(srgb)).IsNotEquivalentTo(JxlDecodeOracleTests.Rgba(managed));
    }

    [Test]
    public async Task ColorDescription_DescribesEnumeratedEncodings()
    {
        foreach (string name in new[] { "rgb8-lossless.jxl", "rgb8-lossy.jxl", "gray8-lossless.jxl" })
        {
            using Image srgb = Image.Load(CorpusPath(name), Options());
            await Assert.That(srgb.Metadata.HasColorDescription).IsFalse();
            await Assert.That(srgb.Metadata.IccProfile).IsNull();
        }

        foreach (ImageMetadata p3 in new[] { Image.Load(CorpusPath("p3-perceptual.jxl"), Options()).Metadata, Read("p3-perceptual.jxl") })
        {
            await Assert.That(p3.Chromaticities).IsEqualTo(new CieChromaticities(0.3127f, 0.3290f, 0.680f, 0.320f, 0.265f, 0.690f, 0.150f, 0.060f));
            await Assert.That(p3.Gamma).IsEqualTo(0.45455f);
        }

        ImageMetadata gamma = Read("gamma-custom.jxl");
        await Assert.That((gamma.Chromaticities, gamma.Gamma)).IsEqualTo(((CieChromaticities?)null, (float?)0.45455f));
        // PQ cannot be described as a gamma, and therefore not converted either.
        await Assert.That(Read("pq-bt2100.jxl").HasColorDescription).IsFalse();
        await Assert.That(() => Image.Load(CorpusPath("pq-bt2100.jxl"), Options() with { ColorConversion = ColorConversionMode.ToSrgb }))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task Boxes_BrotliExifAndXmpAreRead()
    {
        foreach (ImageMetadata metadata in new[] { Image.Load(CorpusPath("metadata-brob.jxl"), Options()).Metadata, Read("metadata-brob.jxl") })
        {
            await Assert.That(metadata.ExifProfile.TryGetValue(ExifTag.Make, out string? make) ? make : null).IsEqualTo("PictureSharp");
            await Assert.That(Encoding.UTF8.GetString(metadata.Xmp!)).Contains("dc:format=\"image/jxl\"");
            await Assert.That(metadata.Orientation).IsEqualTo(ExifOrientation.RightTop);
        }

        using Image ignored = Image.Load(CorpusPath("metadata-brob.jxl"), Options() with { IgnoreMetadata = true });
        await Assert.That((ignored.Metadata.Xmp, ignored.Metadata.ExifProfile.TryGetValue(ExifTag.Make, out _))).IsEqualTo(((byte[]?)null, false));
        await Assert.That(ignored.Metadata.Orientation).IsEqualTo(ExifOrientation.RightTop);
        using Image oriented = Image.Load(CorpusPath("metadata-brob.jxl"), Options() with { ApplyExifOrientation = true });
        await Assert.That((oriented.Width, oriented.Height)).IsEqualTo((24, 40));
        // An uncompressed Exif box, as written by cjxl --compress_boxes=0.
        await Assert.That(Read("container-exif.jxl").GetBlockLength(MetadataKinds.Exif)).IsGreaterThan(0);
    }

    [Test]
    public async Task Orientation_TheCodestreamOverridesExif()
    {
        // Codestream orientation 1 with an EXIF tag 6: applying the orientation must not rotate.
        byte[] upright = Boxed(FileType(), Box("Exif", ExifBox(6)), Box("jxlc", File.ReadAllBytes(CorpusPath("rgb8-lossless.jxl"))));
        using (Image image = Image.Load(upright, Options() with { ApplyExifOrientation = true }))
        {
            await Assert.That((image.Width, image.Height)).IsEqualTo((40, 24));
        }

        await Assert.That(Read(upright).Orientation).IsEqualTo(ExifOrientation.TopLeft);
        // Codestream orientation 6 with an EXIF tag 3.
        byte[] rotated = ReplaceMetadataBoxes(File.ReadAllBytes(CorpusPath("orient6.jxl")), Box("Exif", ExifBox(3)));
        using Image stored = Image.Load(rotated, Options());
        await Assert.That((stored.Width, stored.Height, stored.Metadata.Orientation)).IsEqualTo((40, 24, ExifOrientation.RightTop));
        await Assert.That(Read(rotated).Orientation).IsEqualTo(ExifOrientation.RightTop);
    }

    [Test]
    public async Task Boxes_DamagedMetadataIsSkipped()
    {
        byte[] codestream = File.ReadAllBytes(CorpusPath("rgb8-lossless.jxl"));
        byte[] compressed = Brotli(ExifBox(6));
        byte[] Brob(string type, byte[] payload) => Box("brob", [.. Encoding.ASCII.GetBytes(type), .. payload]);

        ImageMetadata valid = Read(Boxed(FileType(), Brob("Exif", compressed), Brob("xml ", Brotli("<x/>"u8.ToArray())), Box("jxlc", codestream)));
        await Assert.That(valid.GetBlockLength(MetadataKinds.Exif)).IsGreaterThan(0);
        await Assert.That(valid.Xmp).IsEquivalentTo("<x/>"u8.ToArray());
        // The codestream orientation still overrides the decompressed tag.
        await Assert.That(valid.Orientation).IsEqualTo(ExifOrientation.TopLeft);

        // A truncated Brotli stream, trailing data and an Exif box with a TIFF offset beyond its end are skipped.
        foreach (byte[] damaged in new[]
        {
            Brob("Exif", compressed[..^2]),
            Brob("Exif", [.. compressed, 0]),
            Box("Exif", [0, 0, 1, 0, .. ExifBox(6).AsSpan(4)]),
        })
        {
            byte[] file = Boxed(FileType(), damaged, Box("jxlc", codestream));
            await Assert.That(Read(file).GetBlockLength(MetadataKinds.Exif)).IsEqualTo(0);
            using Image image = Image.Load(file, Options());
            await Assert.That(image.Width).IsEqualTo(40);
        }

        // Only the first Exif box counts.
        ImageMetadata first = Read(Boxed(FileType(), Box("Exif", ExifBox(6, "first")), Box("Exif", ExifBox(6, "second")), Box("jxlc", codestream)));
        await Assert.That(first.ExifProfile.TryGetValue(ExifTag.Make, out string? make) ? make : null).IsEqualTo("first");
    }

    [Test]
    public async Task Limits_MetadataIsChargedBeforeItIsMaterialized()
    {
        // A small Brotli stream that inflates to 1 MiB of XMP.
        byte[] bomb = Boxed(FileType(), Box("brob", [.. "xml "u8, .. Brotli(new byte[1 << 20])]),
            Box("jxlc", File.ReadAllBytes(CorpusPath("rgb8-lossless.jxl"))));
        var limited = new DecoderOptions { MaxMetadataBytes = 64 << 10 };
        await Assert.That(() => Read(bomb, limited)).Throws<ImageFormatException>();
        await Assert.That(() => Image.Load(bomb, Options(limited))).Throws<ImageFormatException>();
        using (Image ignored = Image.Load(bomb, Options(limited with { IgnoreMetadata = true })))
        {
            await Assert.That(ignored.Metadata.Xmp).IsNull();
        }

        await Assert.That(Read(bomb).GetBlockLength(MetadataKinds.Xmp)).IsEqualTo(1 << 20);

        // The ICC profile (2500 bytes) counts against the limit, except with IgnoreMetadata.
        var tiny = new DecoderOptions { MaxMetadataBytes = 1000 };
        await Assert.That(() => Read("icc-lossy-rgb.jxl", tiny)).Throws<ImageFormatException>();
        await Assert.That(() => Image.Load(CorpusPath("icc-lossy-rgb.jxl"), Options(tiny))).Throws<ImageFormatException>();
        using Image icc = Image.Load(CorpusPath("icc-lossy-rgb.jxl"), Options(tiny with { IgnoreMetadata = true }));
        await Assert.That(icc.Metadata.IccProfile).IsNull();
    }

    [Test]
    public async Task Icc_DamagedStreamsFailAsCorruptData()
    {
        byte[] original = File.ReadAllBytes(CorpusPath("icc-profile.jxl"));
        int failures = 0;
        for (int bit = 8 * 8; bit < 80 * 8; bit++)
        {
            byte[] damaged = (byte[])original.Clone();
            damaged[bit / 8] ^= (byte)(1 << (bit % 8));
            try
            {
                _ = Read(damaged);
            }
            catch (ImageFormatException)
            {
                failures++;
            }
            catch (NotSupportedException)
            {
            }
        }

        await Assert.That(failures).IsGreaterThan(0);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(() => Image.ReadMetadata(new MemoryStream(original), Options(), cancelled.Token))
            .Throws<OperationCanceledException>();
    }

    // A big-endian TIFF with Orientation and Make, preceded by the Exif box's four-byte offset (0).
    private static byte[] ExifBox(ushort orientation, string make = "PictureSharp")
    {
        byte[] text = [.. Encoding.ASCII.GetBytes(make), 0];
        var tiff = new List<byte>([0, 0, 0, 0, .. "MM\0*"u8, 0, 0, 0, 8, 0, 2]);
        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            Span<byte> entry = stackalloc byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(entry, tag);
            BinaryPrimitives.WriteUInt16BigEndian(entry[2..], type);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], count);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], value);
            tiff.AddRange(entry);
        }

        Entry(0x010F, 2, (uint)text.Length, 8 + 2 + 2 * 12 + 4);
        Entry(0x0112, 3, 1, (uint)orientation << 16);
        tiff.AddRange([0, 0, 0, 0, .. text]);
        return [.. tiff];
    }

    private static byte[] Brotli(byte[] data)
    {
        byte[] output = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        if (!BrotliEncoder.TryCompress(data, output, out int written)) throw new InvalidOperationException();
        return output[..written];
    }

    // The container with its Exif, xml and brob boxes replaced by the given boxes, placed before the codestream.
    private static byte[] ReplaceMetadataBoxes(byte[] container, params byte[][] metadata)
    {
        var boxes = new List<byte[]>();
        for (int offset = 0; offset < container.Length;)
        {
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(container.AsSpan(offset));
            string type = Encoding.ASCII.GetString(container, offset + 4, 4);
            if (type is not ("Exif" or "xml " or "brob"))
            {
                boxes.Add(container[offset..(offset + size)]);
            }

            offset += size;
        }

        int first = boxes.FindIndex(box => Encoding.ASCII.GetString(box, 4, 4) is "jxlc" or "jxlp");
        boxes.InsertRange(first, metadata);
        return [.. boxes.SelectMany(box => box)];
    }
}
