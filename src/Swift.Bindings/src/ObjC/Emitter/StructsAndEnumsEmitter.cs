// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

public static class StructsAndEnumsEmitter
{
    static readonly HashSet<string> FieldSupportedTypes = ["NSString", "nint", "nuint", "nfloat", "int", "float", "double"];

    public static string? Emit(ObjCModule module, string outputDir, string resolvedNamespace, ILogger logger)
    {
        if (module.Enums.Count == 0 && module.Structs.Count == 0 && !module.Constants.Any(c => c.IsExtern) && module.Functions.Count == 0)
        {
            logger.LogDebug("No enums, structs, constants, or functions to emit for module {ModuleName}", module.ModuleName);
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using Foundation;");
        sb.AppendLine("using ObjCRuntime;");
        sb.AppendLine();
        sb.AppendLine($"namespace {resolvedNamespace}");
        sb.AppendLine("{");

        foreach (var enumDecl in module.Enums)
            EmitEnum(sb, enumDecl);

        foreach (var structDecl in module.Structs)
            EmitStruct(sb, structDecl);

        if (module.Constants.Any(c => c.IsExtern) || module.Functions.Count > 0)
            EmitConstantsClass(sb, module);

        sb.AppendLine("}");

        Directory.CreateDirectory(outputDir);
        var filePath = Path.Combine(outputDir, "StructsAndEnums.cs");
        File.WriteAllText(filePath, sb.ToString());

        logger.LogInformation("Wrote {FilePath}", filePath);
        return filePath;
    }

    static void EmitEnum(StringBuilder sb, ObjCEnumDecl enumDecl)
    {
        sb.AppendLine("    [Native]");
        if (enumDecl.IsOptions)
            sb.AppendLine("    [Flags]");

        var baseType = enumDecl.IsOptions ? "ulong" : "long";
        sb.AppendLine($"    public enum {enumDecl.Name} : {baseType}");
        sb.AppendLine("    {");

        var stripPrefix = ShouldStripPrefix(enumDecl);

        foreach (var c in enumDecl.Cases)
        {
            var caseName = stripPrefix ? c.Name[enumDecl.Name.Length..] : c.Name;
            // Prefix with _ if stripping left a digit-leading identifier (invalid C#)
            if (caseName.Length > 0 && char.IsDigit(caseName[0]))
                caseName = "_" + caseName;
            var valueStr = c.Value.HasValue ? $" = {c.Value.Value}" : "";
            sb.AppendLine($"        {caseName}{valueStr},");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static bool ShouldStripPrefix(ObjCEnumDecl enumDecl)
    {
        if (enumDecl.Cases.Count == 0)
            return false;
        return enumDecl.Cases.All(c => c.Name.StartsWith(enumDecl.Name, StringComparison.Ordinal));
    }

    static void EmitStruct(StringBuilder sb, ObjCStructDecl structDecl)
    {
        sb.AppendLine("    [StructLayout(LayoutKind.Sequential)]");
        sb.AppendLine($"    public struct {structDecl.Name}");
        sb.AppendLine("    {");

        foreach (var field in structDecl.Fields)
        {
            var mappedType = ObjCTypeMapper.MapType(field.Type);
            var pascalName = ToPascalCase(field.Name);
            sb.AppendLine($"        public {mappedType} {pascalName};");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitConstantsClass(StringBuilder sb, ObjCModule module)
    {
        sb.AppendLine("    [Static]");
        sb.AppendLine($"    public static partial class {module.ModuleName}Constants");
        sb.AppendLine("    {");

        foreach (var constant in module.Constants.Where(c => c.IsExtern))
            EmitConstant(sb, constant);

        foreach (var function in module.Functions)
            EmitFunction(sb, function);

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitConstant(StringBuilder sb, ObjCConstantDecl constant)
    {
        var pascalName = ToPascalCase(constant.Name);

        // NSString* constants use NSString as the [Field] property type (MAUI convention),
        // not the mapped "string" type that ObjCTypeMapper returns.
        var isNSString = constant.Type is { Name: "NSString", IsPointer: true };
        var fieldType = isNSString ? "NSString" : ObjCTypeMapper.MapType(constant.Type);

        if (FieldSupportedTypes.Contains(fieldType))
        {
            sb.AppendLine($"        [Field(\"{constant.Name}\", \"__Internal\")]");
            sb.AppendLine($"        public {fieldType} {pascalName} {{ get; }}");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine($"        // TODO: {constant.Name} ({fieldType}) — [Field] not supported for this type");
            sb.AppendLine();
        }
    }

    static void EmitFunction(StringBuilder sb, ObjCFunctionDecl function)
    {
        var returnType = ObjCTypeMapper.MapType(function.ReturnType);
        var parameters = string.Join(", ", function.Parameters.Select(p =>
            $"{ObjCTypeMapper.MapType(p.Type)} {p.Name}"));

        sb.AppendLine($"        [DllImport(\"__Internal\")]");
        sb.AppendLine($"        public static extern {returnType} {function.Name}({parameters});");
        sb.AppendLine();
    }

    static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
