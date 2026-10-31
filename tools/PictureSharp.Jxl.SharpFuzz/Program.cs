using PictureSharp.Formats.Jxl.Fuzz;
using SharpFuzz;

// SharpFuzz entry: <target> for AFL++ (afl-fuzz ... -- dotnet PictureSharp.Jxl.SharpFuzz.dll <target>), or
// <target> --libfuzzer for libfuzzer-dotnet. Allowed exceptions pass; any other one reaches the fuzzer as a crash.
// The instrumented assembly must not run before the fuzzer maps its coverage memory, so the target names are repeated
// here instead of read from FuzzTargets.Names (its static constructor would write coverage into unmapped memory).
// The ICC profile of encode inputs is not loaded here: under AFL++ encode runs without the ICC flag.
string[] targets = ["container", "headers", "decode", "entropy", "tojpeg", "fromjpeg", "encode", "edit",
    "cancel-decode", "cancel-tojpeg", "cancel-fromjpeg", "stream-tojpeg", "stream-fromjpeg", "stream-encode"];
if (args.Length is < 1 or > 2 || !targets.Contains(args[0]))
{
    Console.Error.WriteLine("Usage: <target> [--libfuzzer]; targets: " + string.Join(", ", targets));
    return 2;
}

string target = args[0];
if (args is [_, "--libfuzzer"])
{
    Fuzzer.LibFuzzer.Run(span => Run(span.ToArray()));
}
else
{
    Fuzzer.OutOfProcess.Run(stream =>
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Run(buffer.ToArray());
    });
}

return 0;

void Run(byte[] data)
{
    try
    {
        FuzzTargets.Run(target, data);
    }
    catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
    {
    }
}
