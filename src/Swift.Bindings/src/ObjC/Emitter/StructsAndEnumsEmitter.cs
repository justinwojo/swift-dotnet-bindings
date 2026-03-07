// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

public record StructsAndEnumsResult(string FilePath, string? BgenDelegatesFilePath);

public static class StructsAndEnumsEmitter
{
    static readonly HashSet<string> FieldSupportedTypes = ["NSString", "nint", "nuint", "nfloat", "int", "float", "double"];

    // Structs already defined by .NET MAUI's framework bindings — skip re-emission.
    static readonly HashSet<string> SystemStructs =
    [
        "CLLocationCoordinate2D", "MKCoordinateSpan", "MKCoordinateRegion",
        "MKMapPoint", "MKMapSize", "MKMapRect",
        "CMTime", "CMTimeRange", "CMTimeMapping",
        "CGAffineTransform", "CGPoint", "CGSize", "CGRect", "CGVector",
        "UIEdgeInsets", "NSDirectionalEdgeInsets",
        "NSRange", "UIOffset", "CATransform3D",
        "SCNVector3", "SCNVector4", "SCNMatrix4",
        "MKTileOverlayPath",
    ];

    public static StructsAndEnumsResult? Emit(ObjCModule module, string outputDir, string resolvedNamespace, ILogger logger)
    {
        var blockTypedefs = module.Typedefs.Where(t => t.UnderlyingType.IsBlock).ToList();
        if (module.Enums.Count == 0 && module.Structs.Count == 0 && !module.Constants.Any(c => c.IsExtern) && module.Functions.Count == 0 && blockTypedefs.Count == 0)
        {
            logger.LogDebug("No enums, structs, constants, functions, or block typedefs to emit for module {ModuleName}", module.ModuleName);
            return null;
        }

        var typedefMap = ObjCTypeMapper.BuildResolvedTypedefMap(module);

        // Build set of known type names for unresolvable type detection.
        // Includes C# primitives, MAUI framework types, and all module-defined types.
        var knownTypes = ObjCTypeMapper.BuildKnownMappedTypes();
        foreach (var e in module.Enums) knownTypes.Add(e.Name);
        foreach (var s in module.Structs.Where(s => !SystemStructs.Contains(s.Name))) knownTypes.Add(s.Name);

        // Build set of module-local type names (classes + protocol interfaces)
        // to detect accessibility issues with delegates and functions
        var moduleLocalTypes = new HashSet<string>();
        foreach (var cls in module.Classes)
            moduleLocalTypes.Add(cls.Name);
        foreach (var proto in module.Protocols)
        {
            moduleLocalTypes.Add(proto.Name);
            moduleLocalTypes.Add($"I{proto.Name}");
        }

        // Build set of block typedef names that MAUI bgen auto-generates as delegates.
        // bgen generates delegates for block typedefs used as:
        //   (a) direct method parameter types in protocols/classes
        //   (b) type arguments in Action<T>/Func<T> property types (nested blocks)
        // Emitting our own delegate for these causes CS0101.
        // However, if the same typedef is also used by a C function, we must keep emitting it
        // because function signatures reference the named delegate type.
        var blockTypedefNames = new HashSet<string>(blockTypedefs.Select(t => t.Name));
        var blockTypedefMap = ObjCTypeMapper.BuildBlockTypedefMap(module);
        var bgenUsedBlocks = new HashSet<string>();

        // Collect block typedef names that bgen auto-generates delegates for.
        // bgen auto-generates delegates for block typedefs that appear as:
        //   (a) direct method parameter or property types in protocols/classes
        //   (b) nested block params within those types (resolve through blockTypedefMap)
        // Recursively scan to catch all levels of nesting.
        void CollectBgenUsages(ObjCTypeRef typeRef, HashSet<string>? visited = null)
        {
            if (blockTypedefNames.Contains(typeRef.Name))
            {
                bgenUsedBlocks.Add(typeRef.Name);
                // Resolve the typedef to its underlying block type to find nested block typedef params
                if (blockTypedefMap.TryGetValue(typeRef.Name, out var underlying))
                {
                    visited ??= [];
                    if (visited.Add(typeRef.Name))
                        CollectBgenUsages(underlying, visited);
                }
            }
            if (typeRef.IsBlock)
            {
                foreach (var bp in typeRef.BlockParams)
                    CollectBgenUsages(bp, visited);
                if (typeRef.BlockReturnType != null)
                    CollectBgenUsages(typeRef.BlockReturnType, visited);
            }
        }

        foreach (var proto in module.Protocols)
        {
            foreach (var method in proto.Methods)
                foreach (var param in method.Parameters)
                    CollectBgenUsages(param.Type);
            foreach (var prop in proto.Properties)
                CollectBgenUsages(prop.Type);
        }
        foreach (var cls in module.Classes)
        {
            foreach (var method in cls.Methods)
                foreach (var param in method.Parameters)
                    CollectBgenUsages(param.Type);
            foreach (var prop in cls.Properties)
                CollectBgenUsages(prop.Type);
        }

        // Resolve a type name through typedef chains to find the underlying block typedef.
        // e.g., AliasBlock → OriginalBlock (via typedefMap) when OriginalBlock is a block typedef.
        string? ResolveToBlockTypedef(string name)
        {
            if (blockTypedefNames.Contains(name))
                return name;
            if (typedefMap.TryGetValue(name, out var resolved) && blockTypedefNames.Contains(resolved.Name))
                return resolved.Name;
            return null;
        }

        var functionUsedBlocks = new HashSet<string>();
        foreach (var func in module.Functions)
        {
            var resolved = ResolveToBlockTypedef(func.ReturnType.Name);
            if (resolved != null) functionUsedBlocks.Add(resolved);
            foreach (var param in func.Parameters)
            {
                resolved = ResolveToBlockTypedef(param.Type.Name);
                if (resolved != null) functionUsedBlocks.Add(resolved);
            }
        }

        var bgenAutoGeneratedDelegates = new HashSet<string>(bgenUsedBlocks.Except(functionUsedBlocks));

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using CoreAnimation;");
        sb.AppendLine("using CoreFoundation;");
        sb.AppendLine("using CoreGraphics;");
        sb.AppendLine("using CoreLocation;");
        sb.AppendLine("using CoreMedia;");
        sb.AppendLine("using Foundation;");
        sb.AppendLine("using ObjCRuntime;");
        sb.AppendLine("using UIKit;");
        sb.AppendLine();
        sb.AppendLine($"namespace {resolvedNamespace}");
        sb.AppendLine("{");

        foreach (var enumDecl in module.Enums)
            EmitEnum(sb, enumDecl);

        foreach (var structDecl in module.Structs.Where(s => !SystemStructs.Contains(s.Name)))
            EmitStruct(sb, structDecl, typedefMap, knownTypes, logger);

        foreach (var blockTypedef in blockTypedefs)
            EmitBlockDelegate(sb, blockTypedef, typedefMap, moduleLocalTypes, bgenAutoGeneratedDelegates);

        // For function type resolution, also include module-local types (classes/protocols)
        var functionKnownTypes = new HashSet<string>(knownTypes);
        foreach (var cls in module.Classes) functionKnownTypes.Add(cls.Name);
        foreach (var proto in module.Protocols)
        {
            functionKnownTypes.Add(proto.Name);
            functionKnownTypes.Add($"I{proto.Name}");
        }

        if (module.Constants.Any(c => c.IsExtern) || module.Functions.Count > 0)
            EmitConstantsClass(sb, module, typedefMap, moduleLocalTypes, functionKnownTypes, logger);

        sb.AppendLine("}");

        Directory.CreateDirectory(outputDir);
        var filePath = Path.Combine(outputDir, "StructsAndEnums.cs");
        File.WriteAllText(filePath, sb.ToString());
        logger.LogInformation("Wrote {FilePath}", filePath);

        // Emit bgen-auto-generated delegates to a separate file.
        // These are included as ObjcBindingCoreSource (so bgen can resolve them when parsing
        // ApiDefinition.cs) but excluded from Compile (bgen generates its own copies in
        // SupportDelegates.g.cs, which would conflict with ours → CS0101).
        string? bgenDelegatesPath = null;
        if (bgenAutoGeneratedDelegates.Count > 0)
        {
            var bgenSb = new StringBuilder();
            bgenSb.AppendLine("using System;");
            bgenSb.AppendLine("using Foundation;");
            bgenSb.AppendLine("using ObjCRuntime;");
            bgenSb.AppendLine("using UIKit;");
            bgenSb.AppendLine("using CoreGraphics;");
            bgenSb.AppendLine("using CoreLocation;");
            bgenSb.AppendLine("using CoreMedia;");
            bgenSb.AppendLine();
            bgenSb.AppendLine($"namespace {resolvedNamespace}");
            bgenSb.AppendLine("{");

            var emptySet = new HashSet<string>();
            foreach (var blockTypedef in blockTypedefs.Where(t => bgenAutoGeneratedDelegates.Contains(t.Name)))
                EmitBlockDelegate(bgenSb, blockTypedef, typedefMap, moduleLocalTypes, emptySet);

            bgenSb.AppendLine("}");

            bgenDelegatesPath = Path.Combine(outputDir, "BgenDelegates.cs");
            File.WriteAllText(bgenDelegatesPath, bgenSb.ToString());
            logger.LogInformation("Wrote bgen delegate hints to {FilePath}", bgenDelegatesPath);
        }

        return new StructsAndEnumsResult(filePath, bgenDelegatesPath);
    }

    static void EmitEnum(StringBuilder sb, ObjCEnumDecl enumDecl)
    {
        var (baseType, isNative) = ResolveEnumBackingType(enumDecl);
        if (isNative)
            sb.AppendLine("    [Native]");
        if (enumDecl.IsOptions)
            sb.AppendLine("    [Flags]");

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

    // Native-width ObjC types that map to long/ulong with [Native] attribute.
    static readonly HashSet<string> NativeWidthSignedTypes = ["NSInteger", "long", "CFIndex"];
    static readonly HashSet<string> NativeWidthUnsignedTypes = ["NSUInteger", "unsigned long"];

    // Fixed-width C types to C# enum backing types.
    static readonly Dictionary<string, string> FixedWidthEnumTypes = new()
    {
        ["uint8_t"] = "byte",
        ["unsigned char"] = "byte",
        ["int8_t"] = "sbyte",
        ["signed char"] = "sbyte",
        ["int16_t"] = "short",
        ["short"] = "short",
        ["uint16_t"] = "ushort",
        ["unsigned short"] = "ushort",
        ["int32_t"] = "int",
        ["int"] = "int",
        ["uint32_t"] = "uint",
        ["unsigned int"] = "uint",
        ["int64_t"] = "long",
        ["long long"] = "long",
        ["uint64_t"] = "ulong",
        ["unsigned long long"] = "ulong",
    };

    /// <summary>
    /// Resolves the C# backing type for an ObjC enum from its UnderlyingType.
    /// Returns the C# type name and whether [Native] should be emitted.
    /// </summary>
    internal static (string CSharpType, bool IsNative) ResolveEnumBackingType(ObjCEnumDecl enumDecl)
    {
        var underlyingName = enumDecl.UnderlyingType?.Name;

        if (underlyingName != null)
        {
            if (NativeWidthSignedTypes.Contains(underlyingName))
                return ("long", true);

            if (NativeWidthUnsignedTypes.Contains(underlyingName))
                return ("ulong", true);

            if (FixedWidthEnumTypes.TryGetValue(underlyingName, out var fixedType))
                return (fixedType, false);
        }

        // Default: native-width signed/unsigned based on IsOptions
        return (enumDecl.IsOptions ? "ulong" : "long", true);
    }

    static void EmitBlockDelegate(StringBuilder sb, ObjCTypedefDecl typedef, Dictionary<string, ObjCTypeRef> typedefMap, HashSet<string> moduleLocalTypes, HashSet<string> bgenAutoGeneratedDelegates)
    {
        // Skip delegates that MAUI bgen auto-generates from protocol method block parameters.
        // With proper typedef chain resolution, these params emit as Action<>/Func<> instead
        // of the named delegate, so this is a safety net for edge cases.
        if (bgenAutoGeneratedDelegates.Contains(typedef.Name))
            return;

        var block = typedef.UnderlyingType;
        var returnType = block.BlockReturnType != null
            ? ObjCTypeMapper.MapType(block.BlockReturnType, typedefMap: typedefMap)
            : "void";

        var paramParts = new List<string>();
        var allMappedTypes = new List<string> { returnType };
        for (var i = 0; i < block.BlockParams.Count; i++)
        {
            var mappedType = ObjCTypeMapper.MapType(block.BlockParams[i], typedefMap: typedefMap);
            paramParts.Add($"{mappedType} arg{i}");
            allMappedTypes.Add(mappedType);
        }

        // Skip delegates that reference module-local types (defined in ApiDefinition.cs)
        // to avoid CS0059 accessibility errors — these are internal partial interfaces
        if (allMappedTypes.Any(t => moduleLocalTypes.Contains(t)))
            return;

        var parameters = string.Join(", ", paramParts);
        sb.AppendLine($"    public delegate {returnType} {typedef.Name}({parameters});");
        sb.AppendLine();
    }

    static void EmitStruct(StringBuilder sb, ObjCStructDecl structDecl, Dictionary<string, ObjCTypeRef> typedefMap, HashSet<string> knownTypes, ILogger logger)
    {
        // Pre-validate: check all field types are resolvable before emitting.
        // Missing a field in SequentialLayout would break the struct's memory layout.
        foreach (var field in structDecl.Fields)
        {
            var checkType = field.Type.FixedArraySize is > 0
                ? ObjCTypeMapper.MapType(new ObjCTypeRef { Name = field.Type.Name, IsPointer = field.Type.IsPointer }, typedefMap: typedefMap)
                : ObjCTypeMapper.MapType(field.Type, typedefMap: typedefMap);
            if (checkType == structDecl.Name) continue; // self-ref → IntPtr
            if (!ObjCTypeMapper.IsTypeResolvable(checkType, knownTypes))
            {
                logger.LogDebug("Skipping struct {StructName}: field '{FieldName}' has unresolvable type '{TypeName}'",
                    structDecl.Name, field.Name, checkType);
                return;
            }
        }

        sb.AppendLine("    [StructLayout(LayoutKind.Sequential)]");
        sb.AppendLine($"    public struct {structDecl.Name}");
        sb.AppendLine("    {");

        foreach (var field in structDecl.Fields)
        {
            var mappedType = ObjCTypeMapper.MapType(field.Type, typedefMap: typedefMap);
            var pascalName = ToPascalCase(field.Name);

            // Self-referential struct fields (e.g., linked list next pointers) cause CS0523.
            // These are always pointers in C — emit as IntPtr.
            if (mappedType == structDecl.Name)
                mappedType = "IntPtr";

            // Handle C fixed-size array fields (parsed from clang's "uint8_t [4]" qualType)
            if (field.Type.FixedArraySize is > 0)
            {
                var elementType = ObjCTypeMapper.MapType(new ObjCTypeRef { Name = field.Type.Name, IsPointer = field.Type.IsPointer }, typedefMap: typedefMap);
                sb.AppendLine($"        [MarshalAs(UnmanagedType.ByValArray, SizeConst = {field.Type.FixedArraySize})]");
                sb.AppendLine($"        public {elementType}[] {pascalName};");
            }
            else
            {
                sb.AppendLine($"        public {mappedType} {pascalName};");
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitConstantsClass(StringBuilder sb, ObjCModule module, Dictionary<string, ObjCTypeRef> typedefMap, HashSet<string> moduleLocalTypes, HashSet<string> knownTypes, ILogger logger)
    {
        sb.AppendLine($"    public static class {module.ModuleName}Constants");
        sb.AppendLine("    {");

        foreach (var constant in module.Constants.Where(c => c.IsExtern))
            EmitConstant(sb, constant, typedefMap);

        foreach (var function in module.Functions)
            EmitFunction(sb, function, typedefMap, moduleLocalTypes, knownTypes, logger);

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitConstant(StringBuilder sb, ObjCConstantDecl constant, Dictionary<string, ObjCTypeRef> typedefMap)
    {
        var pascalName = ToPascalCase(constant.Name);

        // NSString* constants use NSString as the [Field] property type (MAUI convention),
        // not the mapped "string" type that ObjCTypeMapper returns.
        var isNSString = constant.Type is { Name: "NSString", IsPointer: true };
        var fieldType = isNSString ? "NSString" : ObjCTypeMapper.MapType(constant.Type, typedefMap: typedefMap);

        if (FieldSupportedTypes.Contains(fieldType))
        {
            sb.AppendLine($"        [Field(\"{constant.Name}\", \"__Internal\")]");
            sb.AppendLine($"        public static {fieldType} {pascalName} {{ get; }}");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine($"        // TODO: {constant.Name} ({fieldType}) — [Field] not supported for this type");
            sb.AppendLine();
        }
    }

    static void EmitFunction(StringBuilder sb, ObjCFunctionDecl function, Dictionary<string, ObjCTypeRef> typedefMap, HashSet<string> moduleLocalTypes, HashSet<string> knownTypes, ILogger logger)
    {
        var returnType = ObjCTypeMapper.MapType(function.ReturnType, typedefMap: typedefMap);
        var paramTypes = function.Parameters.Select(p => ObjCTypeMapper.MapType(p.Type, typedefMap: typedefMap)).ToList();

        // Skip functions that reference module-local types (defined in ApiDefinition.cs)
        // to avoid CS0050 accessibility errors
        if (moduleLocalTypes.Contains(returnType) || paramTypes.Any(t => moduleLocalTypes.Contains(t)))
            return;

        // Skip functions that reference unresolvable types (e.g., external C typedefs
        // from included headers whose definitions aren't available in C#)
        var allTypes = paramTypes.Append(returnType);
        if (allTypes.Any(t => !ObjCTypeMapper.IsTypeResolvable(t, knownTypes)))
        {
            var unresolvable = allTypes.FirstOrDefault(t => !ObjCTypeMapper.IsTypeResolvable(t, knownTypes));
            logger.LogDebug("Skipping function {FuncName}: unresolvable type '{TypeName}'", function.Name, unresolvable);
            return;
        }

        var parameters = string.Join(", ", function.Parameters.Select((p, i) =>
        {
            var paramName = string.IsNullOrEmpty(p.Name) ? $"arg{i}" : SanitizeIdentifier(p.Name);
            return $"{paramTypes[i]} {paramName}";
        }));

        sb.AppendLine($"        [DllImport(\"__Internal\")]");
        sb.AppendLine($"        public static extern {returnType} {function.Name}({parameters});");
        sb.AppendLine();
    }

    static readonly HashSet<string> CSharpKeywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    ];

    static string SanitizeIdentifier(string name) =>
        CSharpKeywords.Contains(name) ? $"@{name}" : name;

    static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
