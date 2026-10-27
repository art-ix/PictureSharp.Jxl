using System.Reflection;
using System.Reflection.Emit;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>Checks compiled dependencies, including calls hidden inside method bodies and generated lambdas.</summary>
public sealed class ModuleBoundaryTests
{
    private const string Prefix = "PictureSharp.Formats.Jxl.";
    private static readonly Assembly Plugin = typeof(JxlFormat).Assembly;
    private static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => unchecked((ushort)code.Value));

    [Test]
    public async Task CompiledModules_RespectDependencyDirection()
    {
        var violations = new HashSet<string>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (Type type in Plugin.GetTypes().Where(type => type.Namespace?.StartsWith(Prefix, StringComparison.Ordinal) == true))
        {
            string source = Module(type);
            if (source == "Integration") continue;
            void Check(Type? dependency)
            {
                if (dependency is null) return;
                if (dependency.HasElementType) { Check(dependency.GetElementType()); return; }
                if (dependency.IsGenericType)
                    foreach (Type argument in dependency.GetGenericArguments()) Check(argument);
                if (dependency.Assembly == typeof(Image).Assembly && dependency != typeof(ImageFormatException))
                    violations.Add($"{type.FullName} -> {dependency.FullName}");
                if (dependency.Assembly != Plugin || (dependency.Namespace is null && dependency.Name.StartsWith('<'))) return;
                if (dependency.Namespace == "PictureSharp.Formats.Jxl")
                {
                    if (dependency.Name != "JxlDescriptor") violations.Add($"{type.FullName} -> {dependency.FullName}");
                    return;
                }
                string target = Module(dependency);
                if (!Allowed(source, target)) violations.Add($"{type.FullName} -> {dependency.FullName}");
            }
            Check(type.BaseType);
            foreach (Type contract in type.GetInterfaces()) Check(contract);
            foreach (FieldInfo field in type.GetFields(flags)) Check(field.FieldType);
            foreach (MethodBase method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
            {
                if (method is MethodInfo info) Check(info.ReturnType);
                foreach (ParameterInfo parameter in method.GetParameters()) Check(parameter.ParameterType);
                MethodBody? body = method.GetMethodBody();
                if (body is null) continue;
                foreach (LocalVariableInfo local in body.LocalVariables) Check(local.LocalType);
                foreach (MemberInfo member in ReferencedMembers(method, body))
                {
                    Check(member as Type ?? member.DeclaringType);
                    if (member is MethodInfo called && called.IsGenericMethod)
                        foreach (Type argument in called.GetGenericArguments()) Check(argument);
                }
            }
        }
        await Assert.That(violations).IsEmpty();
    }

    private static string Module(Type type) => (type.Namespace ?? "").Replace(Prefix, "", StringComparison.Ordinal)
        .Replace("Codestream.", "", StringComparison.Ordinal).Split('.')[0];

    // Numerics depends on nothing, so every module may use its scalar helpers.
    private static bool Allowed(string source, string target) => source == target || target == "Numerics" || source switch
    {
        "Model" or "Numerics" => false,
        "IO" => target is "Model",
        "Buffers" => target is "Model",
        "Entropy" => target is "IO",
        "Container" => target is "IO",
        "Color" => target is "Model" or "Buffers" or "Numerics",
        "Rendering" => target is "Model" or "Buffers" or "Color" or "Numerics",
        "Modular" => target is "Model" or "Buffers" or "IO" or "Entropy",
        "VarDct" => target is "Model" or "Buffers" or "IO" or "Entropy" or "Modular" or "Rendering" or "Numerics",
        "Metadata" => target is "IO" or "Entropy",
        "Frames" => target is "Model" or "Buffers" or "IO" or "Entropy" or "Modular" or "VarDct" or "Rendering" or "Metadata" or "Color",
        // The lossy encoder shares the decoder's transforms and tables and reconstructs frames with its renderer.
        "Encoding" => target is "Model" or "Buffers" or "IO" or "Entropy" or "Modular" or "Metadata" or "VarDct" or "Rendering",
        _ => false
    };

    private static IEnumerable<MemberInfo> ReferencedMembers(MethodBase method, MethodBody body)
    {
        byte[] il = body.GetILAsByteArray()!;
        for (int at = 0; at < il.Length;)
        {
            ushort value = il[at++];
            if (value == 0xFE) value = (ushort)(0xFE00 | il[at++]);
            OpCode code = Opcodes[value];
            OperandType operand = code.OperandType;
            if (operand is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType)
            {
                MemberInfo? member = method.Module.ResolveMember(BitConverter.ToInt32(il, at),
                    method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (member is not null) yield return member;
            }
            at += operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
                _ => 4
            };
        }
    }
}
