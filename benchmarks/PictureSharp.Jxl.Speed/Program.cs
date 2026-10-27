using System.Diagnostics;
using System.Text.Json;
using PictureSharp.Formats.Jxl;

if (args.Length is not (3 or 4) || args[0] is not ("encode" or "decode"))
    throw new ArgumentException("Usage: encode|decode input output [warm]");
byte[] input = File.ReadAllBytes(args[1]);
byte[] Convert()
{
    using var output = new MemoryStream();
    if (args[0] == "encode") JxlTranscoder.FromJpeg(input, output);
    else JxlTranscoder.ToJpeg(input, output);
    return output.ToArray();
}
bool warm = args.Length == 4 && args[3] == "warm";
byte[] result = Convert(); // Single cold operation; with "warm", also the JIT warmup.
var times = new double[3];
var allocations = new long[3];
for (int i = 0; warm && i < times.Length; i++)
{
    long allocated = GC.GetTotalAllocatedBytes(precise: true);
    long start = Stopwatch.GetTimestamp();
    result = Convert();
    times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    allocations[i] = GC.GetTotalAllocatedBytes(precise: true) - allocated;
}
Array.Sort(times); Array.Sort(allocations);
File.WriteAllBytes(args[2], result);
Console.WriteLine(JsonSerializer.Serialize(new { bytes = result.Length, medianApiMs = warm ? times[1] : (double?)null, allocatedBytes = warm ? allocations[1] : (long?)null }));
