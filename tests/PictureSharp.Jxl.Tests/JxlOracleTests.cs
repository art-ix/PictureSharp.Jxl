using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// Differential checks against jxlinfo v0.12.0 (on PATH or in JXL_TOOLS); skipped when it is missing. Run with
/// <c>dotnet test -c Release --treenode-filter "/*/*/*/*[Category=Oracle]"</c>.
/// </summary>
public sealed partial class JxlOracleTests
{
    private static string? JxlInfo()
    {
        string? tools = Environment.GetEnvironmentVariable("JXL_TOOLS");
        IEnumerable<string> directories = tools is not null
            ? [tools]
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return directories.Select(directory => Path.Combine(directory, "jxlinfo")).FirstOrDefault(File.Exists);
    }

    [GeneratedRegex(@"JPEG XL (?:image|animation), (\d+)x(\d+), .*?(\d+)-bit")]
    private static partial Regex BasicInfo();

    /// <summary>
    /// Flips every bit of the first 48 bytes of each corpus file. Whatever this parser rejects, libjxl must reject
    /// too; when both accept, the basic information agrees. libjxl may reject more, because it also parses frames.
    /// </summary>
    [Test]
    [Property("Category", "Oracle")]
    public async Task DamagedHeaders_AgreeWithJxlinfo()
    {
        string? jxlinfo = JxlInfo();
        Skip.Unless(jxlinfo is not null, "jxlinfo v0.12.0 is not available (PATH or JXL_TOOLS).");
        string directory = Directory.CreateTempSubdirectory("jxl-oracle-").FullName;
        var mismatches = new ConcurrentBag<string>();
        int compared = 0;
        try
        {
            var cases = Directory.GetFiles(CorpusPath(""), "*.jxl").SelectMany(path =>
            {
                byte[] data = File.ReadAllBytes(path);
                return Enumerable.Range(0, Math.Min(48, data.Length) * 8).Select(bit => (Name: Path.GetFileName(path), Data: data, Bit: bit));
            }).ToArray();
            Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, item =>
            {
                byte[] damaged = [.. item.Data];
                damaged[item.Bit / 8] ^= (byte)(1 << (item.Bit % 8));
                string file = Path.Combine(directory, $"{item.Name}.{item.Bit}.jxl");
                File.WriteAllBytes(file, damaged);
                (bool accepted, Match match) = Run(jxlinfo!, file);
                File.Delete(file);
                string label = $"{item.Name} bit {item.Bit}";
                JxlHeaders headers;
                try
                {
                    headers = JxlHeadersReader.Read(JxlContainer.Parse(damaged).Codestream);
                }
                catch (ImageFormatException exception)
                {
                    if (accepted)
                    {
                        mismatches.Add($"{label}: rejected ({exception.Message}) but jxlinfo accepts");
                    }

                    return;
                }

                Interlocked.Increment(ref compared);
                if (match.Success)
                {
                    string ours = $"{headers.Size.Width}x{headers.Size.Height} {headers.Metadata.BitDepth.BitsPerSample}";
                    string theirs = $"{match.Groups[1].Value}x{match.Groups[2].Value} {match.Groups[3].Value}";
                    if (ours != theirs)
                    {
                        mismatches.Add($"{label}: {ours} but jxlinfo {theirs}");
                    }
                }
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine($"jxlinfo differential: {compared} damaged files accepted by this parser.");
        await Assert.That(mismatches.Order(StringComparer.Ordinal).Take(20)).IsEmpty();
    }

    private static (bool Accepted, Match BasicInfo) Run(string jxlinfo, string file)
    {
        using var process = Process.Start(new ProcessStartInfo(jxlinfo, [file])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        // jxlinfo 0.12.0 exits with 0 after "Decoder error", so its messages decide.
        bool failed = process.ExitCode != 0 || (output + error.Result).Contains("error", StringComparison.OrdinalIgnoreCase);
        return (!failed, BasicInfo().Match(output));
    }
}
