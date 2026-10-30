using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// Inputs found by the 0.8.7 fuzzer (docs/design/0.8.7-fuzzing.md), in corpus/fuzz named after their target. Each must
/// decode or fail only with format errors, with no other exception inside them, at 1 and 4 threads alike.
/// </summary>
public sealed class JxlFuzzRegressionTests
{
    public static IEnumerable<string> Files() =>
        Directory.GetFiles(CorpusPath("fuzz")).Select(Path.GetFileName).Order(StringComparer.Ordinal)!;

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task FuzzInput_FailsOnlyWithFormatErrors(string name)
    {
        byte[] data = File.ReadAllBytes(CorpusPath(Path.Combine("fuzz", name)));
        string Run(int threads)
        {
            try
            {
                using Image image = Image.Load(data, Options(new DecoderOptions { MaxPixels = 1 << 22, MaxDegreeOfParallelism = threads }));
                return $"{image.Width}x{image.Height}";
            }
            catch (Exception exception) when (exception is ImageFormatException or NotSupportedException)
            {
                // The core wraps decoder exceptions: the chain must hold format errors only.
                for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
                {
                    if (inner is not (ImageFormatException or NotSupportedException)) return $"unexpected {inner.GetType().Name}: {inner.Message}";
                }

                return exception.GetType().Name;
            }
        }

        string sequential = Run(1);
        await Assert.That(sequential).DoesNotStartWith("unexpected");
        await Assert.That(Run(4)).IsEqualTo(sequential);
    }
}
