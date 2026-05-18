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

    public static StructsAndEnumsResult? Emit(ObjCModule module, string outputDir, string resolvedNamespace, ILogger logger, ObjCBindingDiagnostics? diagnostics = null, PlatformInfo? platformInfo = null)
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
        var enumNames = new HashSet<string>();
        foreach (var e in module.Enums) { knownTypes.Add(e.Name); enumNames.Add(e.Name); }
        // Two-pass struct registration: first determine which structs are emittable,
        // then add only those to knownTypes. This prevents parent structs from being
        // emitted when their field types reference skipped structs (e.g., structs with
        // unions or unresolvable field types like protobuf types).
        var allModuleStructNames = new HashSet<string>(module.Structs.Where(s => !SystemStructs.Contains(s.Name)).Select(s => s.Name));
        var emittableStructs = ComputeEmittableStructs(module.Structs, typedefMap, knownTypes, allModuleStructNames, logger);
        foreach (var s in emittableStructs) knownTypes.Add(s);
        // Track struct names that were parsed but won't be emitted (unsafe layout,
        // unresolvable fields, etc.). Used by EmitStruct to catch references that
        // slip through the CamelCase heuristic in IsTypeResolvable.
        var skippedStructNames = new HashSet<string>(allModuleStructNames.Except(emittableStructs));

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
        ObjCUsingsEmitter.EmitStructsAndEnumsHeader(sb, platformInfo);
        sb.AppendLine();
        sb.AppendLine($"namespace {resolvedNamespace}");
        sb.AppendLine("{");

        foreach (var enumDecl in module.Enums)
            EmitEnum(sb, enumDecl, typedefMap, diagnostics, platformInfo);

        foreach (var structDecl in module.Structs.Where(s => !SystemStructs.Contains(s.Name)))
            EmitStruct(sb, structDecl, typedefMap, knownTypes, skippedStructNames, logger, diagnostics);

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
            EmitConstantsClass(sb, module, typedefMap, moduleLocalTypes, functionKnownTypes, enumNames, logger, diagnostics, platformInfo);

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
            ObjCUsingsEmitter.EmitBgenDelegatesHeader(bgenSb, platformInfo);
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

    static void EmitEnum(StringBuilder sb, ObjCEnumDecl enumDecl, Dictionary<string, ObjCTypeRef>? typedefMap = null, ObjCBindingDiagnostics? diagnostics = null, PlatformInfo? platformInfo = null)
    {
        if (ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, enumDecl.Availability, "    ", platformInfo))
        {
            diagnostics?.RecordSkip("Enum", enumDecl.Name, ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return;
        }
        ObjCDocCommentEmitter.EmitDocComment(sb, enumDecl.DocComment, null, "    ");

        var (baseType, isNative) = ResolveEnumBackingType(enumDecl, typedefMap);
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
    internal static (string CSharpType, bool IsNative) ResolveEnumBackingType(ObjCEnumDecl enumDecl, Dictionary<string, ObjCTypeRef>? typedefMap = null)
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

            // Resolve through typedef aliases (e.g., MyEnumBase → uint32_t → uint)
            if (typedefMap != null && typedefMap.TryGetValue(underlyingName, out var resolved))
            {
                var resolvedName = resolved.Name;
                if (NativeWidthSignedTypes.Contains(resolvedName))
                    return ("long", true);
                if (NativeWidthUnsignedTypes.Contains(resolvedName))
                    return ("ulong", true);
                if (FixedWidthEnumTypes.TryGetValue(resolvedName, out var resolvedFixed))
                    return (resolvedFixed, false);
            }
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
        // to avoid CS0059 accessibility errors — these are internal partial interfaces.
        // Also check array element types (e.g., RLMPropertyChange[] → RLMPropertyChange)
        // and generic args (e.g., NSDictionary<NSString, IRLMBSON> → IRLMBSON).
        if (allMappedTypes.Any(t => IsModuleLocalType(t, moduleLocalTypes)))
            return;

        var parameters = string.Join(", ", paramParts);
        sb.AppendLine($"    public delegate {returnType} {typedef.Name}({parameters});");
        sb.AppendLine();
    }

    /// <summary>
    /// Checks if a mapped C# type references a module-local type, including through arrays and generics.
    /// E.g., "RLMPropertyChange[]" → checks "RLMPropertyChange", "NSDictionary&lt;NSString, IRLMBSON&gt;" → checks "IRLMBSON".
    /// </summary>
    static bool IsModuleLocalType(string mappedType, HashSet<string> moduleLocalTypes)
    {
        if (moduleLocalTypes.Contains(mappedType))
            return true;
        // Strip array suffix: "Foo[]" → "Foo"
        if (mappedType.EndsWith("[]", StringComparison.Ordinal))
        {
            var baseType = mappedType[..^2];
            if (moduleLocalTypes.Contains(baseType))
                return true;
        }
        // Check generic args: "NSDictionary<K, V>" → check K, V
        var genericStart = mappedType.IndexOf('<');
        if (genericStart >= 0 && mappedType.EndsWith('>'))
        {
            var args = mappedType[(genericStart + 1)..^1].Split(',');
            foreach (var arg in args)
            {
                var trimmed = arg.Trim();
                if (moduleLocalTypes.Contains(trimmed))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Computes the set of struct names that will actually be emitted, using iterative
    /// fixpoint convergence. A struct is emittable if: (1) it has no unsafe layout, and
    /// (2) all its field types are resolvable against the set of emittable structs (not
    /// all parsed structs). This prevents emitting structs that reference skipped structs
    /// (e.g., ones with unions or unresolvable protobuf field types).
    /// </summary>
    static HashSet<string> ComputeEmittableStructs(List<ObjCStructDecl> structs, Dictionary<string, ObjCTypeRef> typedefMap, HashSet<string> baseKnownTypes, HashSet<string> allModuleStructNames, ILogger logger)
    {
        var candidates = structs.Where(s => !SystemStructs.Contains(s.Name) && !s.HasUnsafeLayout).ToList();

        // Seed with all candidate names, then iteratively remove structs whose fields
        // reference types that are no longer in the emittable set.
        var emittable = new HashSet<string>(candidates.Select(c => c.Name));
        var tempKnown = new HashSet<string>(baseKnownTypes);
        foreach (var name in emittable) tempKnown.Add(name);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var s in candidates)
            {
                if (!emittable.Contains(s.Name)) continue;

                foreach (var field in s.Fields)
                {
                    var checkType = field.Type.FixedArraySize is > 0
                        ? ObjCTypeMapper.MapType(new ObjCTypeRef { Name = field.Type.Name, IsPointer = field.Type.IsPointer }, typedefMap: typedefMap)
                        : ObjCTypeMapper.MapType(field.Type, typedefMap: typedefMap);
                    if (checkType == s.Name) continue; // self-ref → IntPtr

                    // A field type that IS a module struct but NOT in the emittable set
                    // means it was skipped (unsafe layout, unresolvable fields, etc.).
                    // The CamelCase heuristic in IsTypeResolvable would let it through,
                    // so we must check explicitly.
                    bool isSkippedModuleStruct = allModuleStructNames.Contains(checkType) && !emittable.Contains(checkType);

                    if (isSkippedModuleStruct || !ObjCTypeMapper.IsTypeResolvable(checkType, tempKnown))
                    {
                        emittable.Remove(s.Name);
                        tempKnown.Remove(s.Name);
                        changed = true;
                        logger.LogDebug("Struct {StructName} not emittable: field '{FieldName}' has unresolvable type '{TypeName}'",
                            s.Name, field.Name, checkType);
                        break;
                    }
                }
            }
        }

        return emittable;
    }

    static void EmitStruct(StringBuilder sb, ObjCStructDecl structDecl, Dictionary<string, ObjCTypeRef> typedefMap, HashSet<string> knownTypes, HashSet<string> skippedStructNames, ILogger logger, ObjCBindingDiagnostics? diagnostics)
    {
        // Skip structs with unsafe layouts (bitfields, anonymous unions/structs)
        if (structDecl.HasUnsafeLayout)
        {
            logger.LogDebug("Skipping struct {StructName}: {Reason}",
                structDecl.Name, structDecl.UnsafeLayoutReason);
            diagnostics?.RecordSkip("Struct", structDecl.Name, ObjCSkipReason.UnsupportedConstruct,
                structDecl.UnsafeLayoutReason ?? "unsafe layout");
            return;
        }

        // Pre-validate: check all field types are resolvable before emitting.
        // Missing a field in SequentialLayout would break the struct's memory layout.
        foreach (var field in structDecl.Fields)
        {
            var checkType = field.Type.FixedArraySize is > 0
                ? ObjCTypeMapper.MapType(new ObjCTypeRef { Name = field.Type.Name, IsPointer = field.Type.IsPointer }, typedefMap: typedefMap)
                : ObjCTypeMapper.MapType(field.Type, typedefMap: typedefMap);
            if (checkType == structDecl.Name) continue; // self-ref → IntPtr
            // Explicitly reject references to module structs that were skipped
            // (the CamelCase heuristic in IsTypeResolvable would let them through)
            if (skippedStructNames.Contains(checkType))
            {
                logger.LogDebug("Skipping struct {StructName}: field '{FieldName}' references skipped struct '{TypeName}'",
                    structDecl.Name, field.Name, checkType);
                diagnostics?.RecordSkip("Struct", structDecl.Name, ObjCSkipReason.UnresolvableType, $"field '{field.Name}' references skipped struct '{checkType}'");
                return;
            }
            if (!ObjCTypeMapper.IsTypeResolvable(checkType, knownTypes))
            {
                logger.LogDebug("Skipping struct {StructName}: field '{FieldName}' has unresolvable type '{TypeName}'",
                    structDecl.Name, field.Name, checkType);
                diagnostics?.RecordSkip("Struct", structDecl.Name, ObjCSkipReason.UnresolvableType, $"field '{field.Name}' has unresolvable type '{checkType}'");
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

    static void EmitConstantsClass(StringBuilder sb, ObjCModule module, Dictionary<string, ObjCTypeRef> typedefMap, HashSet<string> moduleLocalTypes, HashSet<string> knownTypes, HashSet<string> enumNames, ILogger logger, ObjCBindingDiagnostics? diagnostics, PlatformInfo? platformInfo = null)
    {
        sb.AppendLine($"    public static class {module.ModuleName}Constants");
        sb.AppendLine("    {");

        foreach (var constant in module.Constants.Where(c => c.IsExtern))
            EmitConstant(sb, constant, typedefMap, diagnostics, platformInfo);

        foreach (var function in module.Functions)
            EmitFunction(sb, function, typedefMap, moduleLocalTypes, knownTypes, enumNames, logger, diagnostics, platformInfo);

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitConstant(StringBuilder sb, ObjCConstantDecl constant, Dictionary<string, ObjCTypeRef> typedefMap, ObjCBindingDiagnostics? diagnostics, PlatformInfo? platformInfo = null)
    {
        if (ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, constant.Availability, "        ", platformInfo))
        {
            diagnostics?.RecordSkip("Constant", constant.Name, ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return;
        }

        var pascalName = ToPascalCase(constant.Name);

        // NSString* constants use NSString as the [Field] property type (MAUI convention),
        // not the mapped "string" type that ObjCTypeMapper returns.
        // Also resolve typedef'd NSString types (e.g., RLMNotification → NSString*).
        var isNSString = IsNSStringType(constant.Type, typedefMap);
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

    /// <summary>
    /// Checks if a type is NSString* directly or through typedef chain resolution.
    /// e.g., RLMNotification (typedef for NSString*) → true.
    /// </summary>
    static bool IsNSStringType(ObjCTypeRef type, Dictionary<string, ObjCTypeRef> typedefMap)
    {
        // Direct NSString* check
        if (type is { Name: "NSString", IsPointer: true })
            return true;

        // Resolve through typedef chain: the constant's type name may be a typedef
        // for NSString* (e.g., typedef NSString *RLMNotification).
        // The typedefMap resolves chains, so we just need a single lookup.
        if (typedefMap.TryGetValue(type.Name, out var resolved))
        {
            if (resolved is { Name: "NSString", IsPointer: true })
                return true;
            // Also handle when the typedef drops the pointer but the usage adds it
            if (resolved.Name == "NSString" && type.IsPointer)
                return true;
        }

        return false;
    }

    static void EmitFunction(StringBuilder sb, ObjCFunctionDecl function, Dictionary<string, ObjCTypeRef> typedefMap, HashSet<string> moduleLocalTypes, HashSet<string> knownTypes, HashSet<string> enumNames, ILogger logger, ObjCBindingDiagnostics? diagnostics, PlatformInfo? platformInfo = null)
    {
        if (ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, function.Availability, "        ", platformInfo))
        {
            diagnostics?.RecordSkip("Function", function.Name, ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return;
        }

        // Skip variadic C functions — they require va_list which can't be safely P/Invoked
        if (function.IsVariadic)
        {
            diagnostics?.RecordSkip("Function", function.Name, ObjCSkipReason.VariadicFunction, "variadic C functions cannot be safely P/Invoked");
            return;
        }

        var returnType = ObjCTypeMapper.MapType(function.ReturnType, typedefMap: typedefMap);
        var paramTypes = function.Parameters.Select(p => ObjCTypeMapper.MapType(p.Type, typedefMap: typedefMap)).ToList();

        // Skip functions that reference module-local types (defined in ApiDefinition.cs)
        // to avoid CS0050 accessibility errors
        if (moduleLocalTypes.Contains(returnType) || paramTypes.Any(t => moduleLocalTypes.Contains(t)))
        {
            diagnostics?.RecordSkip("Function", function.Name, ObjCSkipReason.AccessibilityConflict, "references module-local type");
            return;
        }

        // Skip functions that reference unresolvable types (e.g., external C typedefs
        // from included headers whose definitions aren't available in C#)
        var allTypes = paramTypes.Append(returnType);
        if (allTypes.Any(t => !ObjCTypeMapper.IsTypeResolvable(t, knownTypes)))
        {
            var unresolvable = allTypes.FirstOrDefault(t => !ObjCTypeMapper.IsTypeResolvable(t, knownTypes));
            logger.LogDebug("Skipping function {FuncName}: unresolvable type '{TypeName}'", function.Name, unresolvable);
            diagnostics?.RecordSkip("Function", function.Name, ObjCSkipReason.UnresolvableType, $"unresolvable type '{unresolvable}'");
            return;
        }

        var parameters = string.Join(", ", function.Parameters.Select((p, i) =>
        {
            var paramName = string.IsNullOrEmpty(p.Name) ? $"arg{i}" : SanitizeIdentifier(p.Name);
            if (ObjCTypeMapper.IsValueTypePointerParameter(p.Type, typedefMap, enumNames))
            {
                var pointeeType = ObjCTypeMapper.MapValueTypePointerParameterType(p.Type, typedefMap);
                return $"out {pointeeType} {paramName}";
            }
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
