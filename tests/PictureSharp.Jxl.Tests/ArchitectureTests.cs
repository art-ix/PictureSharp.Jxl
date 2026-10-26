using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;

namespace PictureSharp.Formats.Jxl.Tests;

public sealed class ArchitectureTests
{
    private static readonly Assembly Plugin = typeof(JxlFormat).Assembly;

    private static readonly string[] PublicNamespaces = ["PictureSharp.Formats.Jxl"];

    [Test]
    public async Task Plugin_ReferencesOnlyPictureSharp_AndTheRuntime()
    {
        string[] references = [.. Plugin.GetReferencedAssemblies().Select(a => a.Name!)];
        await Assert.That(references.Where(name => name != "PictureSharp" && !IsRuntime(name))).IsEmpty();
        await Assert.That(references).Contains("PictureSharp");
        await Assert.That(Plugin.GetCustomAttributes<InternalsVisibleToAttribute>()).IsEmpty();
    }

    [Test]
    public async Task PictureSharp_DoesNotExposeItsInternalsToThePlugin()
    {
        string[] friends = [.. typeof(Image).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>().Select(a => a.AssemblyName)];
        await Assert.That(friends.Where(name => name.StartsWith("PictureSharp.Jxl", StringComparison.Ordinal))).IsEmpty();
    }

    [Test]
    public async Task Plugin_ExportsTypesOnlyFromItsFormatNamespaces()
    {
        foreach (Type type in Plugin.GetExportedTypes())
        {
            await Assert.That(PublicNamespaces).Contains(type.Namespace!);
        }
    }

    [Test]
    public async Task Plugin_HasNoUnsafeCodeOrPInvoke()
    {
        await Assert.That(Plugin.ManifestModule.GetCustomAttributes<UnverifiableCodeAttribute>()).IsEmpty();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        string[] pinvoke = [.. Plugin.GetTypes().SelectMany(t => t.GetMethods(all))
            .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0)
            .Select(m => $"{m.DeclaringType}.{m.Name}")];
        await Assert.That(pinvoke).IsEmpty();
    }

    private static bool IsRuntime(string name) =>
        File.Exists(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), name + ".dll"));
}
