using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// The 0.8.9 working budget (docs/design/0.8.9-resilience.md). The inputs in corpus/resilience declare many extra
/// channels of type Optional with all-zero samples: valid files (djxl decodes them) that a few hundred bytes describe.
/// </summary>
public sealed class JxlResilienceTests
{
    [Test]
    public async Task ExtraChannels_AreChargedAgainstMaxPixels()
    {
        // 1024x1024 with 64 extra channels: 374 bytes, ~280 MiB of channels for a 4 MiB canvas.
        byte[] data = File.ReadAllBytes(CorpusPath("resilience/extra-channels-64-1024.jxl"));
        using (Image image = Image.Load(data, Options()))
        {
            await Assert.That(image.Width).IsEqualTo(1024);
        }

        foreach (int threads in new[] { 1, 4 })
        {
            await Assert.That(() => Image.Load(data, Options(new DecoderOptions { MaxPixels = 1024 * 1024, MaxDegreeOfParallelism = threads })))
                .Throws<ImageFormatException>().WithMessageContaining("working memory");
        }
    }

    [Test]
    public async Task ExtraChannels_FailBeforeTheirStorageIsAllocated()
    {
        // 2048x2048 with 256 extra channels: 1369 bytes asking for 4 GiB of channels.
        byte[] data = File.ReadAllBytes(CorpusPath("resilience/extra-channels-256-2048.jxl"));
        long before = GC.GetAllocatedBytesForCurrentThread();
        await Assert.That(() => Image.Load(data, Options(new DecoderOptions { MaxPixels = 1 << 22, MaxDegreeOfParallelism = 1 })))
            .Throws<ImageFormatException>().WithMessageContaining("working memory");
        long limit = JxlWorkBudget.FixedBytes + (1L << 22) * JxlWorkBudget.SamplesPerPixel * sizeof(int);
        await Assert.That(GC.GetAllocatedBytesForCurrentThread() - before).IsLessThan(limit + (64 << 20));
    }

    [Test]
    public async Task Budget_LeavesOrdinaryImagesAtTheirOwnSize()
    {
        // The smallest MaxPixels that admits the image also admits its working memory.
        foreach (string name in new[] { "groups-300x200.jxl", "lossy-patches.jxl", "lossy-progressive-dc.jxl", "rgb16-lossless.jxl", "lf-groups.jxl" })
        {
            byte[] data = File.ReadAllBytes(CorpusPath(name));
            ImageInfo info = Image.Identify(data, Options());
            using Image image = Image.Load(data, Options(new DecoderOptions { MaxPixels = (long)info.Width * info.Height, MaxDegreeOfParallelism = -1 }));
            await Assert.That(image.Width).IsEqualTo(info.Width);
        }
    }

    [Test]
    public async Task ReleaseBuild_RentsFromTheSharedPool()
    {
        // Only the fuzz targets' checked build counts rentals.
        await Assert.That(ReferenceEquals(JxlArrayPool<int>.Shared, System.Buffers.ArrayPool<int>.Shared)).IsTrue();
        await Assert.That(ReferenceEquals(JxlArrayPool<float>.Shared, System.Buffers.ArrayPool<float>.Shared)).IsTrue();
    }

    [Test]
    public async Task MaxFrames_CountsFramesThatAreNotDisplayed()
    {
        // A reference frame for the patches, then the image.
        byte[] data = File.ReadAllBytes(CorpusPath("lossy-patches.jxl"));
        using (Image.Load(data, Options(new DecoderOptions { MaxFrames = 2 })))
        {
        }

        await Assert.That(() => Image.Load(data, Options(new DecoderOptions { MaxFrames = 1 }))).Throws<ImageFormatException>()
            .WithMessageContaining("MaxFrames");
    }
}
