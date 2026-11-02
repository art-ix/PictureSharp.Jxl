using System.Diagnostics;
using System.Drawing;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// The layer API (0.8.11, docs/design/0.8.11-layers.md): <see cref="JxlLayeredImage.Flatten"/> is exactly
/// <c>Image.Load</c>, and the layers are djxl's frames without coalescing (djxl --no_coalescing --output_frames).
/// </summary>
public sealed class JxlLayersTests
{
    private static readonly string[] LayeredFiles =
    [
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "corpus"), "layers-*.jxl").Select(Path.GetFileName).Order()!,
    ];

    // Single frames, patches and DC frames (reference frames that are not layers), alpha, gray, 16 bits, ICC, XYB.
    private static readonly string[] SingleLayerFiles =
    [
        "rgb8-lossless.jxl", "rgba8-lossless.jxl", "gray16-alpha.jxl", "rgb16-lossless.jxl", "rgb8-lossy.jxl", "lossy-alpha.jxl",
        "lossy-patches.jxl", "lossy-progressive-dc.jxl", "lossy-p3.jxl", "icc-lossy-rgb.jxl", "icc-lossy-gray.jxl", "orient6.jxl",
        "jpeg-420.jxl", "lossy-resampling.jxl",
    ];

    private static readonly string Conformance = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "external",
        "conformance", "testcases");

    public static IEnumerable<string> FlattenFiles() => [.. LayeredFiles, .. SingleLayerFiles];

    public static IEnumerable<DecoderOptions> OptionVariants() =>
    [
        new DecoderOptions(),
        new DecoderOptions { PreserveBitDepth = true },
        new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb },
        new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb, PreserveBitDepth = true },
        new DecoderOptions { ApplyExifOrientation = true },
        new DecoderOptions { IgnoreMetadata = true, MaxDegreeOfParallelism = 1 },
    ];

    [Test]
    [MethodDataSource(nameof(FlattenFiles))]
    public async Task Flatten_EqualsImageLoad(string name)
    {
        byte[] data = await File.ReadAllBytesAsync(CorpusPath(name));
        foreach (DecoderOptions variant in OptionVariants())
        {
            DecoderOptions options = Options(variant);
            using Image expected = Image.Load(data.AsMemory(), options);
            using JxlLayeredImage layered = JxlLayers.Load(data.AsMemory(), options);
            using Image flattened = layered.Flatten();
            await AssertSame(flattened, expected, $"{name} {variant}");
            await Assert.That((layered.Width, layered.Height)).IsEqualTo((expected.Width, expected.Height));
            await Assert.That(layered.Metadata.Orientation).IsEqualTo(expected.Metadata.Orientation);
            int layers = name.StartsWith("layers-", StringComparison.Ordinal) ? Frames(data).Count(f => f.Type == JxlFrameType.Regular) : 1;
            await Assert.That(layered.Layers.Count).IsEqualTo(layers);
            foreach (JxlLayer layer in layered.Layers)
            {
                await Assert.That(layer.Image.PixelType).IsEqualTo(expected.PixelType);
            }
        }
    }

    [Test]
    public async Task SingleFrame_IsOneLayerEqualToTheImage()
    {
        using JxlLayeredImage layered = JxlLayers.Load(CorpusPath("rgba8-lossless.jxl"), Options());
        using Image expected = Image.Load(CorpusPath("rgba8-lossless.jxl"), Options());
        JxlLayer layer = layered.Layers.Single();
        await Assert.That((layer.Offset, layer.Name, layer.BlendMode, layer.Clamp)).IsEqualTo((Point.Empty, "", JxlLayerBlendMode.Replace, false));
        await AssertSame(layer.Image, expected, "layer");
    }

    [Test]
    public async Task Layers_DescribeTheFrames()
    {
        using JxlLayeredImage layered = JxlLayers.Load(CorpusPath("layers-three.jxl"), Options());
        await Assert.That(layered.Layers.Select(l => (l.Offset, l.Image.Size, l.BlendMode, l.Name))).IsEquivalentTo(
        [
            (new Point(0, 0), new Size(64, 48), JxlLayerBlendMode.Replace, ""),
            (new Point(5, 5), new Size(30, 20), JxlLayerBlendMode.Add, ""),
            (new Point(30, 4), new Size(20, 40), JxlLayerBlendMode.Multiply, ""),
        ]);
        JxlLayeredImageInfo info = JxlLayers.Identify(CorpusPath("layers-three.jxl"), Options());
        await Assert.That(info.Layers.Select(l => (l.Offset, l.Size, l.BlendMode, l.Clamp))).IsEquivalentTo(
            layered.Layers.Select(l => (l.Offset, l.Image.Size, l.BlendMode, l.Clamp)));
        await Assert.That((info.Image.Width, info.Image.Height)).IsEqualTo((64, 48));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Orientation_IsAppliedToLayersAndOffsets(bool apply)
    {
        // Orientation 6 (rotate 90° clockwise): the canvas is 48×64, the layer at (30, -4) of 20×40 is placed at
        // (48 - 40 - (-4), 30) = (12, 30) as 40×20, like libjxl's JxlDecoderGetFrameHeader.
        DecoderOptions options = Options(new DecoderOptions { ApplyExifOrientation = apply });
        using JxlLayeredImage layered = JxlLayers.Load(CorpusPath("layers-orient6.jxl"), options);
        JxlLayer top = layered.Layers[2];
        await Assert.That(top.BlendMode).IsEqualTo(JxlLayerBlendMode.Replace);
        if (!apply)
        {
            await Assert.That((layered.Width, layered.Height, top.Offset, top.Image.Size)).IsEqualTo((64, 48, new Point(30, -4), new Size(20, 40)));
            await Assert.That(layered.Metadata.Orientation).IsEqualTo(ExifOrientation.RightTop);
        }
        else
        {
            await Assert.That((layered.Width, layered.Height, top.Offset, top.Image.Size)).IsEqualTo((48, 64, new Point(12, 30), new Size(40, 20)));
            await Assert.That(layered.Layers[1].Offset).IsEqualTo(new Point(23, 5));
        }

        // A lossless Replace layer is the flattened image where it lies on the canvas.
        using Image flattened = layered.Flatten();
        for (int y = 0; y < top.Image.Height; y++)
        {
            for (int x = 0; x < top.Image.Width; x++)
            {
                int cx = x + top.Offset.X, cy = y + top.Offset.Y;
                if (cx < 0 || cy < 0 || cx >= flattened.Width || cy >= flattened.Height) continue;
                if (top.Image[x, y] != flattened[cx, cy])
                {
                    Assert.Fail($"Layer pixel ({x}, {y}) differs from the canvas at ({cx}, {cy}).");
                }
            }
        }

        JxlLayeredImageInfo info = JxlLayers.Identify(await File.ReadAllBytesAsync(CorpusPath("layers-orient6.jxl")), options);
        await Assert.That(info.Layers.Select(l => (l.Offset, l.Size))).IsEquivalentTo(layered.Layers.Select(l => (l.Offset, l.Image.Size)));
    }

    /// <summary>Every layer matches djxl's frame without coalescing (oriented, as djxl does): lossless exactly.</summary>
    [Test]
    [Category("Oracle")]
    public async Task Layers_MatchDjxlWithoutCoalescing()
    {
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(djxl is not null, "djxl v0.12.0 is not available (PATH or JXL_TOOLS).");
        // 0.8.15: layers with noise, including frames that are not shown (seeded by nonvisible_frame_index).
        var files = LayeredFiles.Append("noise-layers.jxl").Select(CorpusPath).ToList();
        foreach (string name in new[] { "blendmodes", "mul_no_extra_channels", "sunset_logo" })
        {
            string path = Path.Combine(Conformance, name, "input.jxl");
            if (File.Exists(path)) files.Add(path);
        }

        foreach (string path in files)
        {
            DecoderOptions options = Options(new DecoderOptions { ApplyExifOrientation = true });
            using JxlLayeredImage layered = JxlLayers.Load(path, options);
            List<Image> expected = DjxlLayers(djxl!, path);
            try
            {
                await Assert.That(layered.Layers.Count).IsEqualTo(expected.Count);
                bool lossy = JxlHeadersReader.Read(JxlContainer.Parse(File.ReadAllBytes(path)).Codestream).Metadata.XybEncoded
                    || path.Contains("noise", StringComparison.Ordinal);
                for (int i = 0; i < expected.Count; i++)
                {
                    Image layer = layered.Layers[i].Image;
                    await Assert.That(layer.Size).IsEqualTo(expected[i].Size);
                    string? mismatch = lossy ? JxlDecodeTests.Tolerance(layer, expected[i])
                        : JxlDecodeOracleTests.Rgba(layer).SequenceEqual(JxlDecodeOracleTests.Rgba(expected[i])) ? null : "pixels differ";
                    if (mismatch is not null) Assert.Fail($"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)} layer {i}: {mismatch}");
                }

                using Image flattened = layered.Flatten();
                using Image load = Image.Load(path, options);
                await AssertSame(flattened, load, path);
            }
            finally
            {
                expected.ForEach(image => image.Dispose());
            }
        }

        Console.WriteLine($"Layers like djxl: {string.Join(", ", files.Select(Path.GetFileName))} ({files.Count})");
    }

    [Test]
    public async Task NotAStack_IsRejected()
    {
        // The top layer of layers-three blends onto an empty slot instead of the layer below it: Image.Load still
        // flattens it, the layer API refuses it before decoding.
        byte[] data = WithLastSource(await File.ReadAllBytesAsync(CorpusPath("layers-three.jxl")));
        using (Image flattened = Image.Load(data.AsMemory(), Options())) await Assert.That(flattened.Width).IsEqualTo(64);
        NotSupportedException? load = Assert.Throws<NotSupportedException>(() => JxlLayers.Load(data.AsMemory(), Options()));
        await Assert.That(load!.Message).Contains("Image.Load");
        Assert.Throws<NotSupportedException>(() => JxlLayers.Identify(data.AsMemory(), Options()));
        Assert.Throws<NotSupportedException>(() => JxlLayers.Load(CorpusPath("animation.jxl"), Options()));
    }

    [Test]
    public async Task StackRules_FollowTheSlots()
    {
        // Layers bottom-up on slot 1 like libjxl's encoder; the rules on headers that the corpus tools cannot write.
        var metadata = new JxlImageMetadata { ExtraChannels = [new JxlExtraChannelInfo { Type = JxlExtraChannelType.Alpha }, new JxlExtraChannelInfo()] };
        JxlBlendingInfo blend = new(JxlBlendMode.Blend, 0, false, 1);
        JxlFrameHeader Layer(JxlBlendingInfo? color = null, JxlBlendingInfo? alpha = null, JxlBlendingInfo? other = null, bool last = false) => new()
        {
            CustomSizeOrOrigin = true, Width = 8, Height = 8, IsLast = last, SaveAsReference = last ? 0u : 1u,
            Blending = color ?? blend, ExtraChannelBlending = [alpha ?? color ?? blend, other ?? (color ?? blend) with { Mode = JxlBlendMode.Add }],
        };
        JxlFrameHeader full = new() { Width = 8, Height = 8, IsLast = false, SaveAsReference = 1, ExtraChannelBlending = [JxlBlendingInfo.Replace, JxlBlendingInfo.Replace] };
        JxlFrameHeader reference = new() { Type = JxlFrameType.ReferenceOnly, IsLast = false, SaveAsReference = 1 };
        JxlFrameHeader dc = new() { Type = JxlFrameType.Dc, IsLast = false };

        await Assert.That(JxlLayerStack.Select([Layer(), Layer(), Layer(last: true)], metadata, 0).Count).IsEqualTo(3);
        await Assert.That(JxlLayerStack.Select([dc, full, Layer(last: true)], metadata, 0).Count).IsEqualTo(2);
        // A full Replace layer ignores its background, even over a slot a reference frame wrote.
        await Assert.That(JxlLayerStack.Select([reference, full, Layer(last: true)], metadata, 0).Count).IsEqualTo(2);
        // A reference frame in the stack's slot, a first layer on a written slot, another slot for the next layer.
        Assert.Throws<NotSupportedException>(() => JxlLayerStack.Select([Layer(), reference, Layer(last: true)], metadata, 0));
        Assert.Throws<NotSupportedException>(() => JxlLayerStack.Select([reference, Layer(last: true)], metadata, 0));
        Assert.Throws<NotSupportedException>(() => JxlLayerStack.Select([Layer(), Layer(blend with { Source = 2 }, last: true)], metadata, 0));
        // Extra channels from another slot, alpha blended differently, blending with another alpha channel.
        Assert.Throws<NotSupportedException>(() => JxlLayerStack.Select([Layer(), Layer(other: blend with { Source = 2 }, last: true)], metadata, 0));
        Assert.Throws<NotSupportedException>(() => JxlLayerStack.Select([Layer(), Layer(alpha: blend with { Mode = JxlBlendMode.Add }, last: true)], metadata, 0));
        Assert.Throws<NotSupportedException>(() => JxlLayerStack.Select([Layer(), Layer(alpha: blend with { Clamp = true }, last: true)], metadata, 0));
        Assert.Throws<NotSupportedException>(() => JxlLayerStack.Select([Layer(), Layer(blend with { AlphaChannel = 1 }, last: true)], metadata, 0));
        // A layer saved before the colour transform would be dropped from the coalesced image.
        Assert.Throws<NotSupportedException>(() => JxlLayerStack.Select([Layer() with { SaveBeforeColorTransform = true }, Layer(last: true)], metadata, 0));
        Assert.Throws<NotSupportedException>(() => JxlLayerStack.Select([Layer(last: true)], metadata with { Animation = new JxlAnimationHeader(1000, 1, 0, false) }, 0));
    }

    [Test]
    public async Task ReferenceFrameAfterTheColourTransform_IsNotALayer()
    {
        // Found by the layers fuzz target: a reference-only frame saved after the colour transform is rendered like a
        // layer but must not be reported as one.
        byte[] data = await File.ReadAllBytesAsync(CorpusPath(Path.Combine("fuzz", "layers-reference-after-color-transform.jxl")));
        List<JxlFrameHeader> frames = Frames(data);
        await Assert.That(frames.Any(f => f.Type == JxlFrameType.ReferenceOnly && !f.SaveBeforeColorTransform)).IsTrue();
        DecoderOptions options = Options(new DecoderOptions { MaxPixels = 1 << 22 });
        using JxlLayeredImage layered = JxlLayers.Load(data.AsMemory(), options);
        await Assert.That(layered.Layers.Count).IsEqualTo(frames.Count(f => f.Type is JxlFrameType.Regular or JxlFrameType.SkipProgressive));
        using Image flattened = layered.Flatten();
        using Image expected = Image.Load(data.AsMemory(), options);
        await AssertSame(flattened, expected, "flattened");
    }

    [Test]
    public async Task Options_AreChecked()
    {
        Assert.Throws<ArgumentException>(() => JxlLayers.Load(CorpusPath("layers-three.jxl"),
            Options(new DecoderOptions { TargetSize = new Size(10, 10) })));
        // Only JPEG XL: other formats are unknown to the layer API.
        using var png = new MemoryStream();
        using (var image = new Image(4, 4)) image.Save(png, ImageFormat.Png);
        Assert.Throws<Exception>(() => JxlLayers.Load(png.ToArray().AsMemory(), Options()));
        await Assert.That(JxlLayers.Load(CorpusPath("layers-three.jxl")).Layers.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Budget_CoversEveryLayer()
    {
        // 64×48 canvas + layers of 64×48, 30×20 and 20×40 = 7544 pixels; four reservations.
        DecoderOptions options = Options(new DecoderOptions { MaxPixels = 7544, MaxFrames = 4 });
        using (JxlLayeredImage layered = JxlLayers.Load(CorpusPath("layers-three.jxl"), options))
        {
            await Assert.That(layered.Layers.Count).IsEqualTo(3);
        }

        Assert.Throws<ImageFormatException>(() => JxlLayers.Load(CorpusPath("layers-three.jxl"), options with { MaxPixels = 7543 }));
        Assert.Throws<ImageFormatException>(() => JxlLayers.Load(CorpusPath("layers-three.jxl"), options with { MaxFrames = 3 }));
        // 16-bit layers count twice.
        DecoderOptions wide = Options(new DecoderOptions { PreserveBitDepth = true, MaxPixels = 2 * (3072 + 3072 + 1200), MaxFrames = 3 });
        using (JxlLayers.Load(CorpusPath("layers-16bit.jxl"), wide)) { }
        Assert.Throws<ImageFormatException>(() => JxlLayers.Load(CorpusPath("layers-16bit.jxl"), wide with { MaxPixels = wide.MaxPixels - 1 }));
    }

    [Test]
    public async Task Cancellation_StopsTheLoad()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => JxlLayers.Load(CorpusPath("layers-three.jxl"), Options(), cancellation.Token));
        await Assert.That(async () => await JxlLayers.LoadAsync(CorpusPath("layers-three.jxl"), Options(), cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task StreamAndAsync_LoadTheSameLayers()
    {
        using JxlLayeredImage expected = JxlLayers.Load(CorpusPath("layers-three.jxl"), Options());
        await using FileStream stream = File.OpenRead(CorpusPath("layers-three.jxl"));
        using JxlLayeredImage fromStream = JxlLayers.Load(stream, Options());
        using JxlLayeredImage fromAsync = await JxlLayers.LoadAsync(CorpusPath("layers-three.jxl"), Options());
        stream.Position = 0;
        using JxlLayeredImage fromAsyncStream = await JxlLayers.LoadAsync(stream, Options());
        foreach (JxlLayeredImage other in new[] { fromStream, fromAsync, fromAsyncStream })
        {
            for (int i = 0; i < expected.Layers.Count; i++)
            {
                await AssertSame(other.Layers[i].Image, expected.Layers[i].Image, $"layer {i}");
            }
        }

        stream.Position = 0;
        await Assert.That(JxlLayers.Identify(stream, Options()).Layers.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Dispose_ReleasesTheLayers()
    {
        JxlLayeredImage layered = JxlLayers.Load(CorpusPath("layers-three.jxl"), Options());
        Image first = layered.Layers[0].Image;
        layered.Dispose();
        layered.Dispose();
        Assert.Throws<ObjectDisposedException>(() => layered.Flatten());
        Assert.Throws<ObjectDisposedException>(() => _ = first.GetPixel<Rgba32>(0, 0));
        await Assert.That(layered.Layers.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Parallelism_DoesNotChangeTheLayers()
    {
        using JxlLayeredImage one = JxlLayers.Load(CorpusPath("layers-xyb.jxl"), Options(new DecoderOptions { MaxDegreeOfParallelism = 1 }));
        using JxlLayeredImage many = JxlLayers.Load(CorpusPath("layers-xyb.jxl"), Options(new DecoderOptions { MaxDegreeOfParallelism = 16 }));
        for (int i = 0; i < one.Layers.Count; i++) await AssertSame(many.Layers[i].Image, one.Layers[i].Image, $"layer {i}");
    }

    internal static async Task AssertSame(Image actual, Image expected, string what)
    {
        await Assert.That((actual.Width, actual.Height, actual.PixelType)).IsEqualTo((expected.Width, expected.Height, expected.PixelType));
        if (!actual.GetPixelByteSpan().SequenceEqual(expected.GetPixelByteSpan())) Assert.Fail($"{what}: pixels differ.");
        await Assert.That((actual.Metadata.IccProfile ?? []).SequenceEqual(expected.Metadata.IccProfile ?? [])).IsTrue();
        await Assert.That((actual.Metadata.Gamma, actual.Metadata.Chromaticities)).IsEqualTo((expected.Metadata.Gamma, expected.Metadata.Chromaticities));
    }

    private static List<JxlFrameHeader> Frames(byte[] data)
    {
        JxlCodestream codestream = JxlContainer.Parse(data).Codestream;
        return JxlDecodeSession.ReadFrameHeaders(codestream, JxlHeadersReader.Read(codestream));
    }

    // Flips the one bit that changes the last frame's blending source and nothing else (a 2-bit field: slot 1 becomes
    // 0 or 3, both empty).
    private static byte[] WithLastSource(byte[] data)
    {
        JxlFrameHeader original = Frames(data)[^1];
        for (int bit = 16; bit < data.Length * 8; bit++)
        {
            byte[] copy = [.. data];
            copy[bit >> 3] ^= (byte)(1 << (bit & 7));
            try
            {
                List<JxlFrameHeader> frames = Frames(copy);
                JxlFrameHeader last = frames[^1];
                if (frames.Count == 3 && last.Blending == original.Blending with { Source = last.Blending.Source }
                    && last.Blending.Source != original.Blending.Source
                    && (last.Width, last.Height, last.X0, last.Y0, last.Type, last.Encoding, last.Name)
                    == (original.Width, original.Height, original.X0, original.Y0, original.Type, original.Encoding, original.Name))
                {
                    return copy;
                }
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
            {
            }
        }

        throw new InvalidOperationException("No bit changes only the blending source.");
    }

    // djxl's frames without coalescing, oriented, as 8-bit PNG files <output>-N.png.
    private static List<Image> DjxlLayers(string djxl, string file)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"jxl-layers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "layer.png");
            using var process = Process.Start(new ProcessStartInfo(djxl,
                [file, output, "--no_coalescing", "--output_frames", "--bits_per_sample=8", "--quiet"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"djxl failed: {error}");
            int count = Directory.GetFiles(directory).Length;
            return [.. Enumerable.Range(0, count).Select(i => Image.Load(Path.Combine(directory, $"layer.png-{i}.png")))];
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
