// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

public static class ApiDefinitionEmitter
{
    public static string Emit(ObjCModule module, string outputDir, string resolvedNamespace, ILogger logger)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using CoreFoundation;");
        sb.AppendLine("using Foundation;");
        sb.AppendLine("using ObjCRuntime;");
        sb.AppendLine("using CoreGraphics;");
        sb.AppendLine();
        sb.AppendLine($"namespace {resolvedNamespace}");
        sb.AppendLine("{");

        foreach (var proto in module.Protocols)
            EmitProtocol(sb, proto);

        foreach (var cls in module.Classes)
            EmitClass(sb, cls);

        sb.AppendLine("}");

        Directory.CreateDirectory(outputDir);
        var filePath = Path.Combine(outputDir, "ApiDefinition.cs");
        File.WriteAllText(filePath, sb.ToString());

        logger.LogInformation("Wrote {FilePath}", filePath);
        return filePath;
    }

    static void EmitProtocol(StringBuilder sb, ObjCProtocolDecl proto)
    {
        EmitAvailabilityAttributes(sb, proto.Availability, "    ");

        sb.AppendLine("    [Protocol]");
        sb.AppendLine("    [BaseType(typeof(NSObject))]");

        // Filter out implicit protocols from inheritance — NSObject is implicit in .NET MAUI bindings,
        // NSFastEnumeration maps to IEnumerable but isn't a binding interface
        var filteredInherited = proto.InheritedProtocolNames
            .Where(n => n != "NSObject" && n != "NSFastEnumeration")
            .ToList();
        var inheritList = filteredInherited.Count > 0
            ? $" : {string.Join(", ", filteredInherited.Select(n => $"I{n}"))}"
            : "";
        sb.AppendLine($"    partial interface I{proto.Name}{inheritList}");
        sb.AppendLine("    {");

        // Protocols don't declare ObjC lightweight generics — only pass the common fallback set
        foreach (var method in proto.Methods)
            EmitMethod(sb, method, declaringClassName: null, isProtocol: true, genericTypeParams: null);

        foreach (var prop in proto.Properties)
            EmitProperty(sb, prop, declaringClassName: null, genericTypeParams: null);

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitClass(StringBuilder sb, ObjCClassDecl cls)
    {
        EmitAvailabilityAttributes(sb, cls.Availability, "    ");

        var baseType = cls.SuperclassName ?? "NSObject";
        sb.AppendLine($"    [BaseType(typeof({baseType}))]");

        var filteredProtocols = cls.ProtocolNames
            .Where(n => n != "NSObject" && n != "NSFastEnumeration")
            .ToList();
        var protocols = filteredProtocols.Count > 0
            ? $" : {string.Join(", ", filteredProtocols.Select(n => $"I{n}"))}"
            : "";
        sb.AppendLine($"    partial interface {cls.Name}{protocols}");
        sb.AppendLine("    {");

        // Scope generic type params to THIS class only — avoids cross-type collisions
        // where one class's generic param name matches a real type used elsewhere.
        var classGenericParams = cls.GenericTypeParamNames.Count > 0
            ? new HashSet<string>(cls.GenericTypeParamNames)
            : null;

        foreach (var method in cls.Methods)
            EmitMethod(sb, method, declaringClassName: cls.Name, isProtocol: false, genericTypeParams: classGenericParams);

        foreach (var prop in cls.Properties)
            EmitProperty(sb, prop, declaringClassName: cls.Name, genericTypeParams: classGenericParams);

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitMethod(StringBuilder sb, ObjCMethodDecl method, string? declaringClassName, bool isProtocol, HashSet<string>? genericTypeParams)
    {
        EmitAvailabilityAttributes(sb, method.Availability, "        ");

        var isConstructor = method.Selector == "init" || method.Selector.StartsWith("initWith", StringComparison.Ordinal);

        if (isProtocol && !method.IsOptional)
            sb.AppendLine("        [Abstract]");

        if (!method.IsInstanceMethod && !isConstructor)
            sb.AppendLine("        [Static]");

        sb.AppendLine($"        [Export(\"{method.Selector}\")]");

        var returnType = isConstructor
            ? "NativeHandle"
            : ObjCTypeMapper.MapType(method.ReturnType, declaringClassName, genericTypeParams);

        if (!isConstructor && ObjCTypeMapper.IsNullableAttribute(method.ReturnType))
            sb.AppendLine("        [return: NullAllowed]");

        var methodName = isConstructor
            ? "Constructor"
            : SelectorToMethodName(method.Selector);

        var parameters = EmitParameters(method.Parameters, genericTypeParams);
        sb.AppendLine($"        {returnType} {methodName}({parameters});");
        sb.AppendLine();
    }

    static void EmitProperty(StringBuilder sb, ObjCPropertyDecl prop, string? declaringClassName, HashSet<string>? genericTypeParams)
    {
        EmitAvailabilityAttributes(sb, prop.Availability, "        ");

        if (!prop.IsOptional)
        {
            // Only emit [Abstract] for protocol properties (no declaringClassName)
            // Actually, IsOptional is only set on protocol members, so we need to check context.
            // For protocol properties that are required (not optional), emit [Abstract].
            // We use declaringClassName == null as the protocol indicator.
            if (declaringClassName == null)
                sb.AppendLine("        [Abstract]");
        }

        if (prop.IsClass)
            sb.AppendLine("        [Static]");

        var getterSelector = prop.GetterSelector ?? prop.Name;
        sb.AppendLine($"        [Export(\"{getterSelector}\")]");

        if (ObjCTypeMapper.IsNullableAttribute(prop.Type))
            sb.AppendLine("        [NullAllowed]");

        var mappedType = ObjCTypeMapper.MapType(prop.Type, declaringClassName, genericTypeParams);
        if (prop.IsReadonly)
        {
            sb.AppendLine($"        {mappedType} {ToPascalCase(prop.Name)} {{ get; }}");
        }
        else
        {
            // Emit setter with custom selector if present
            var setterSelector = prop.SetterSelector ?? $"set{ToPascalCase(prop.Name)}:";
            sb.AppendLine($"        {mappedType} {ToPascalCase(prop.Name)} {{");
            sb.AppendLine($"            get;");
            sb.AppendLine($"            [Export(\"{setterSelector}\")] set;");
            sb.AppendLine($"        }}");
        }
        sb.AppendLine();
    }

    static void EmitAvailabilityAttributes(StringBuilder sb, List<ObjCAvailability> availability, string indent)
    {
        foreach (var avail in availability)
        {
            if (avail.Platform != "ios")
                continue;

            if (avail.IntroducedVersion != null)
            {
                var (major, minor) = ParseVersion(avail.IntroducedVersion);
                sb.AppendLine($"{indent}[Introduced(PlatformName.iOS, {major}, {minor})]");
            }

            if (avail.DeprecatedVersion != null)
            {
                var (major, minor) = ParseVersion(avail.DeprecatedVersion);
                sb.AppendLine($"{indent}[Deprecated(PlatformName.iOS, {major}, {minor})]");
            }
        }
    }

    static string EmitParameters(List<ObjCParameterDecl> parameters, HashSet<string>? genericTypeParams)
    {
        var parts = new List<string>();
        foreach (var param in parameters)
        {
            if (ObjCTypeMapper.IsNSErrorOutParameter(param.Type))
            {
                parts.Add("[NullAllowed] out NSError error");
            }
            else
            {
                var mappedType = ObjCTypeMapper.MapType(param.Type, genericTypeParams: genericTypeParams);
                var nullAttr = ObjCTypeMapper.IsNullableAttribute(param.Type)
                    ? "[NullAllowed] "
                    : "";
                var safeName = EscapeCSharpKeyword(param.Name);
                parts.Add($"{nullAttr}{mappedType} {safeName}");
            }
        }
        return string.Join(", ", parts);
    }

    // C# reserved keywords that cannot be used as identifiers without '@' prefix
    static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate",
        "do", "double", "else", "enum", "event", "explicit", "extern", "false",
        "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit",
        "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
        "new", "null", "object", "operator", "out", "override", "params", "private",
        "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    internal static string EscapeCSharpKeyword(string name) =>
        CSharpKeywords.Contains(name) ? $"@{name}" : name;

    internal static string SelectorToMethodName(string selector)
    {
        // Take text before first ':', PascalCase it
        var colonIndex = selector.IndexOf(':');
        var baseName = colonIndex >= 0 ? selector[..colonIndex] : selector;
        return ToPascalCase(baseName);
    }

    static (int major, int minor) ParseVersion(string version)
    {
        var parts = version.Split('.');
        var major = int.Parse(parts[0]);
        var minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
        return (major, minor);
    }

    static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
