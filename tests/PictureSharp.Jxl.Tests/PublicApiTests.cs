using System.Reflection;
using PictureSharp.Formats.Jxl;

namespace PictureSharp.Formats.Jxl.Tests;

public sealed class PublicApiTests
{
    [Test]
    public async Task PublicApi_MatchesApprovedSnapshot()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string actual = string.Join('\n', typeof(JxlFormat).Assembly.GetExportedTypes()
            .Where(type => !type.Name.StartsWith('<'))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .SelectMany(type => new[] { type.FullName! }.Concat(type.GetMembers(flags)
                .Where(member => member.MemberType is MemberTypes.Method or MemberTypes.Constructor or MemberTypes.Property)
                .Select(member => "  " + member).Order(StringComparer.Ordinal)))) + "\n";
        string expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PublicApi.approved.txt")).Replace("\r\n", "\n");
        if (actual != expected)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "PublicApi.received.txt"), actual);
        }

        await Assert.That(actual).IsEqualTo(expected);
    }
}
