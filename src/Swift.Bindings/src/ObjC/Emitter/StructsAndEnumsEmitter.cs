// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

public record StructsAndEnumsResult(string FilePath, string? BgenDelegatesFilePath);

public static class StructsAndEnumsEmitter
{
    // Structs already defined by .NET MAUI's framework bindings are skipped via
    // AppleFrameworkRegistry.IsObjCSystemStruct (objc-type-mappings.json: systemStructs).

    public static StructsAndEnumsResult? Emit(ObjCModule module, string outputDir, string resolvedNamespace, ILogger logger, ObjCBindingDiagnostics? diagnostics = null, PlatformInfo? platformInfo = null, HashSet<string>? excludeTypeNames = null)
    {
        var blockTypedefs = module.Typedefs.Where(t => t.UnderlyingType.IsBlock).ToList();
        if (module.Enums.Count == 0 && module.Structs.Count == 0 && module.Functions.Count == 0 && blockTypedefs.Count == 0)
        {
            logger.LogDebug("No enums, structs, functions, or block typedefs to emit for module {ModuleName}", module.ModuleName);
            // Regenerating into a populated output directory has to remove what it no longer
            // writes: the csproj picks both files up on Exists(), so a leftover from an earlier
            // generation would still reach bgen. A constants-only module reaches this return, and
            // its stale file holds the pre-move `public static class {Module}Constants` — which
            // collides with the interface the ApiDefinition now declares under that name.
            DeleteIfPresent(Path.Combine(outputDir, "StructsAndEnums.cs"), logger);
            DeleteIfPresent(Path.Combine(outputDir, "BgenDelegates.cs"), logger);
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
        var allModuleStructNames = new HashSet<string>(module.Structs.Where(s => !AppleFrameworkRegistry.IsObjCSystemStruct(s.Name)).Select(s => s.Name));
        var emittableStructs = ComputeEmittableStructs(module.Structs, typedefMap, knownTypes, allModuleStructNames, logger);
        foreach (var s in emittableStructs) knownTypes.Add(s);
        // Track struct names that were parsed but won't be emitted (unsafe layout,
        // unresolvable fields, etc.). Used by EmitStruct to catch references that
        // slip through the CamelCase heuristic in IsTypeResolvable (a skipped
        // module struct still starts uppercase).
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

        // Cross-boundary drop: a block-typedef delegate that references a Swift type emitted on the
        // Swift side and excluded from this ObjC binding (e.g. ExampleMetadata) has no C# definition
        // here, so emitting it — and any C function that takes/returns it — would fail with CS0246.
        // Compute the transitive set of such delegates so EmitBlockDelegate and EmitFunction can
        // drop them and their dependents.
        var droppedDelegateNames = ComputeCrossBoundaryDroppedDelegates(blockTypedefs, typedefMap, excludeTypeNames, diagnostics);

        var sb = new StringBuilder();
        // Provenance-derived usings for both StructsAndEnums.cs and BgenDelegates.cs — same
        // mechanism as ApiDefinition.cs so a struct field or block typedef referencing e.g.
        // MTLPixelFormat (Metal) gets `using Metal;` without hand-editing the baseline arrays.
        var referencedAppleNamespaces =
            ObjCUsingsEmitter.CollectReferencedNamespaces(module, module.AppleSdkTypeNamespaces);
        ObjCUsingsEmitter.EmitStructsAndEnumsHeader(sb, platformInfo, referencedAppleNamespaces);
        sb.AppendLine();
        sb.AppendLine($"namespace {resolvedNamespace}");
        sb.AppendLine("{");

        // The module's registered tag (the acronym every extern constant carries, e.g. MLN) is the
        // fallback prefix for enum cases that don't repeat their own type name. Resolved once per
        // module from the SAME inference AND the same input the constants emitter uses — extern
        // constants only — so one module has exactly one tag. Feeding the unfiltered list instead
        // would let a non-exported constant that doesn't carry the acronym null the tag out here
        // while the constants emitter still resolves one.
        var moduleTag = ObjCConstantsEmitter.ResolveModuleTag(
            module.Constants.Where(c => c.IsExtern).ToList());

        foreach (var enumDecl in module.Enums)
            EmitEnum(sb, enumDecl, typedefMap, diagnostics, moduleTag);

        foreach (var structDecl in module.Structs.Where(s => !AppleFrameworkRegistry.IsObjCSystemStruct(s.Name)))
            EmitStruct(sb, structDecl, typedefMap, knownTypes, skippedStructNames, logger, diagnostics);

        foreach (var blockTypedef in blockTypedefs)
            EmitBlockDelegate(sb, blockTypedef, typedefMap, moduleLocalTypes, bgenAutoGeneratedDelegates, droppedDelegateNames);

        // For function type resolution, also include module-local types (classes/protocols)
        var functionKnownTypes = new HashSet<string>(knownTypes);
        foreach (var cls in module.Classes) functionKnownTypes.Add(cls.Name);
        foreach (var proto in module.Protocols)
        {
            functionKnownTypes.Add(proto.Name);
            functionKnownTypes.Add($"I{proto.Name}");
        }

        // Extern constants are NOT emitted here — they go to ApiDefinition.cs via
        // ObjCConstantsEmitter, the only input bgen generates [Field] backing from.
        if (module.Functions.Count > 0)
            EmitFunctionsClass(sb, module, typedefMap, moduleLocalTypes, functionKnownTypes, enumNames, droppedDelegateNames, excludeTypeNames, logger, diagnostics);

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
            ObjCUsingsEmitter.EmitBgenDelegatesHeader(bgenSb, platformInfo, referencedAppleNamespaces);
            bgenSb.AppendLine();
            bgenSb.AppendLine($"namespace {resolvedNamespace}");
            bgenSb.AppendLine("{");

            var emptySet = new HashSet<string>();
            foreach (var blockTypedef in blockTypedefs.Where(t => bgenAutoGeneratedDelegates.Contains(t.Name)))
                EmitBlockDelegate(bgenSb, blockTypedef, typedefMap, moduleLocalTypes, emptySet, droppedDelegateNames);

            bgenSb.AppendLine("}");

            bgenDelegatesPath = Path.Combine(outputDir, "BgenDelegates.cs");
            File.WriteAllText(bgenDelegatesPath, bgenSb.ToString());
            logger.LogInformation("Wrote bgen delegate hints to {FilePath}", bgenDelegatesPath);
        }
        else
        {
            DeleteIfPresent(Path.Combine(outputDir, "BgenDelegates.cs"), logger);
        }

        return new StructsAndEnumsResult(filePath, bgenDelegatesPath);
    }

    static void DeleteIfPresent(string filePath, ILogger logger)
    {
        if (!File.Exists(filePath))
            return;
        File.Delete(filePath);
        logger.LogInformation("Removed stale {FilePath} — this generation emits nothing into it", filePath);
    }

    static void EmitEnum(StringBuilder sb, ObjCEnumDecl enumDecl, Dictionary<string, ObjCTypeRef>? typedefMap = null, ObjCBindingDiagnostics? diagnostics = null, string? moduleTag = null)
    {
        ObjCDocCommentEmitter.EmitDocComment(sb, enumDecl.DocComment, null, "    ");
        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, enumDecl.Availability, "    ");

        var (baseType, isNative) = ResolveEnumBackingType(enumDecl, typedefMap);
        if (isNative)
            sb.AppendLine("    [Native]");
        if (enumDecl.IsOptions)
            sb.AppendLine("    [Flags]");

        sb.AppendLine($"    public enum {enumDecl.Name} : {baseType}");
        sb.AppendLine("    {");

        var stripToken = ResolveCasePrefix(enumDecl, moduleTag);

        // Reserve the enum's own type name so a case that PascalCases to it is disambiguated
        // rather than emitting a member with the same name as the enclosing type (CS0542), and
        // to detect two source cases that collapse to the same C# identifier (CS0102). Swift
        // permits e.g. `case foo` alongside `case Foo` — both PascalCase to `Foo`.
        var emittedNames = new HashSet<string>(StringComparer.Ordinal) { enumDecl.Name };

        foreach (var c in enumDecl.Cases)
        {
            string caseName;
            if (stripToken != null)
            {
                // Strip the shared prefix (ObjC's `EnumNameCaseName` idiom, or the module's own
                // acronym tag). The remainder is already PascalCase by that convention, so it is
                // emitted VERBATIM — NOT run through ToPascalCase, which would collapse an all-caps
                // remainder like `OK` to `Ok`. A case whose name equals the prefix strips to the
                // empty string; fall back to the full case name so we never emit a nameless member
                // (the collision guard below then disambiguates it against the reserved enum-type
                // name).
                var stripped = c.Name[stripToken.Length..];
                caseName = stripped.Length == 0 ? c.Name : stripped;
            }
            else
            {
                // Not prefix-stripped: PascalCase the member via the SAME transform the reference
                // sites use (NameProvider.ToPascalCase in SwiftDefaultValueMapper.MapEnumCase). The
                // Swift-side default-value/enum-reference emitters name a case as
                // NameProvider.ToPascalCase(caseName) (e.g. RiveAlignment.Center), so emitting the
                // raw Swift-lowercase declaration name (`center`) produced CS0117 at every reference.
                // Matching the transform keeps declaration and references consistent.
                caseName = NameProvider.ToPascalCase(c.Name);
            }
            // Prefix with _ if the result is a digit-leading identifier (invalid C#).
            if (caseName.Length > 0 && char.IsDigit(caseName[0]))
                caseName = "_" + caseName;
            // Deterministic disambiguation for a genuine source-level collision (two cases mapping
            // to the same identifier, or a case equal to the enum type name): the enum cannot carry
            // two members of one name, so suffix the later one and record it, rather than emitting
            // an invalid enum (CS0102/CS0542) silently.
            if (!emittedNames.Add(caseName))
            {
                var disambiguated = caseName;
                var n = 2;
                while (!emittedNames.Add(disambiguated = $"{caseName}_{n}"))
                    n++;
                diagnostics?.RecordSkip(
                    "enum-case", $"{enumDecl.Name}.{c.Name}", ObjCSkipReason.DuplicateSignature,
                    $"Enum member name '{caseName}' collides with the enum type name or a " +
                    $"sibling case; emitted as '{disambiguated}' to keep the enum valid.");
                caseName = disambiguated;
            }
            // Per-case availability attributes ([Supported/Obsoleted/UnsupportedOSPlatform] are valid
            // on enum members) — a no-op when the enumerator carried no availability macro.
            ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, c.Availability, "        ");
            var valueStr = c.Value.HasValue ? $" = {c.Value.Value}" : "";
            sb.AppendLine($"        {caseName}{valueStr},");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    /// <summary>
    /// The prefix to strip from every case of <paramref name="enumDecl"/>, or <c>null</c> to leave
    /// the case names alone. Two rules, tried in order, both derived from metadata that a later
    /// upstream release cannot move:
    /// <list type="number">
    /// <item>the enum's own ObjC type name, when EVERY case repeats it (the <c>EnumNameCaseName</c>
    /// idiom);</item>
    /// <item>otherwise the module's registered acronym tag, when every case carries it AND the
    /// remainder starts a new PascalCase word — <c>MLNMapTiler</c>/<c>MLNMapLibre</c> under tag
    /// <c>MLN</c> become <c>MapTiler</c>/<c>MapLibre</c>.</item>
    /// </list>
    /// A longest-common-prefix over the case SET is deliberately not a rule here: the LCP moves when
    /// upstream adds a case, so every previously-emitted member would silently rename on a library
    /// update. Both rules above are independent of which cases exist and in what order, so an added
    /// case can only add a member. Both are also all-or-nothing across the case set — one case that
    /// doesn't carry the prefix leaves the whole enum unstripped rather than producing a
    /// half-renamed surface (matching the constants emitter's tag policy).
    /// </summary>
    static string? ResolveCasePrefix(ObjCEnumDecl enumDecl, string? moduleTag)
    {
        if (enumDecl.Cases.Count == 0)
            return null;

        // The strip is keyed on the ObjC spelling the cases were written against, which is the raw
        // declaration name even when the C# type carries a Swift-import rename.
        var typeName = enumDecl.RawObjCName ?? enumDecl.Name;
        if (enumDecl.Cases.All(c => c.Name.StartsWith(typeName, StringComparison.Ordinal)))
            return typeName;

        if (moduleTag != null && enumDecl.Cases.All(c => CarriesTagAtTokenBoundary(c.Name, moduleTag)))
            return moduleTag;

        return null;
    }

    /// <summary>
    /// True when <paramref name="caseName"/> starts with <paramref name="tag"/> and the remainder
    /// begins a new PascalCase word (an upper-case letter). The token-boundary requirement is what
    /// keeps the strip from biting into the middle of a word: under tag <c>MLN</c>, <c>MLNMapTiler</c>
    /// qualifies (<c>MapTiler</c>) while a hypothetical <c>MLNext</c> does not.
    /// </summary>
    static bool CarriesTagAtTokenBoundary(string caseName, string tag) =>
        caseName.Length > tag.Length
        && caseName.StartsWith(tag, StringComparison.Ordinal)
        && char.IsUpper(caseName[tag.Length]);

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

    static void EmitBlockDelegate(StringBuilder sb, ObjCTypedefDecl typedef, Dictionary<string, ObjCTypeRef> typedefMap, HashSet<string> moduleLocalTypes, HashSet<string> bgenAutoGeneratedDelegates, HashSet<string> droppedDelegateNames)
    {
        // Skip delegates that reference a cross-boundary Swift type excluded from this ObjC binding
        // (or, transitively, another dropped delegate) — the referenced type has no C# definition
        // here, so emitting the delegate would fail with CS0246.
        if (droppedDelegateNames.Contains(typedef.Name))
            return;

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
        // Also check array element types (e.g., MOSPropertyChange[] → MOSPropertyChange)
        // and generic args (e.g., NSDictionary<NSString, IMOSBSON> → IMOSBSON).
        if (allMappedTypes.Any(t => IsModuleLocalType(t, moduleLocalTypes)))
            return;

        var parameters = string.Join(", ", paramParts);
        sb.AppendLine($"    public delegate {returnType} {typedef.Name}({parameters});");
        sb.AppendLine();
    }

    /// <summary>
    /// Checks if a mapped C# type references a module-local type, including through arrays and generics.
    /// E.g., "MOSPropertyChange[]" → checks "MOSPropertyChange", "NSDictionary&lt;NSString, IMOSBSON&gt;" → checks "IMOSBSON".
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
    /// Tests whether <paramref name="name"/> — or any typedef alias it resolves to — is in
    /// <paramref name="names"/>. A raw-name check alone misses <c>typedef ExcludedType Alias;</c>:
    /// a param typed as the alias passes the check, but <see cref="ObjCTypeMapper.MapType"/>
    /// resolves the alias at emit time and re-emits the excluded/dropped name (CS0246). Walks the
    /// typedef chain with a cycle guard so a multi-hop alias is still caught.
    /// </summary>
    static bool NameOrAliasIn(string name, Dictionary<string, ObjCTypeRef>? typedefMap, HashSet<string> names)
    {
        var current = name;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current != null && seen.Add(current))
        {
            if (names.Contains(current))
                return true;
            if (typedefMap != null && typedefMap.TryGetValue(current, out var resolved))
                current = resolved.Name;
            else
                break;
        }
        return false;
    }

    /// <summary>
    /// Computes the transitive set of block-typedef delegate names that must be dropped because
    /// their signature references a cross-boundary Swift type — one emitted on the Swift side and
    /// excluded from this ObjC binding (e.g. ExampleMetadata), which therefore has no C# definition
    /// in the ObjC binding assembly. A delegate is dropped if any of its block params/return
    /// (recursively, through inline nested blocks) is an excluded type name OR a previously-dropped
    /// delegate name — directly or through a typedef alias to either. Iterates to a fixpoint so a
    /// delegate that only references a dropped delegate is caught. Returns an empty set when there
    /// are no exclusions (pure-ObjC bindings).
    /// </summary>
    static HashSet<string> ComputeCrossBoundaryDroppedDelegates(
        List<ObjCTypedefDecl> blockTypedefs,
        Dictionary<string, ObjCTypeRef> typedefMap,
        HashSet<string>? excludeTypeNames,
        ObjCBindingDiagnostics? diagnostics)
    {
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        if (excludeTypeNames == null || excludeTypeNames.Count == 0)
            return dropped;

        // True if this type ref (or a nested inline block within it) references an excluded Swift
        // type or an already-dropped delegate name. Resolves typedef aliases so an alias of an
        // excluded type — or an alias of an already-dropped delegate — is also caught, because
        // MapType expands the alias back to the excluded/dropped name at emit time.
        bool ReferencesUnresolvable(ObjCTypeRef t)
        {
            if (NameOrAliasIn(t.Name, typedefMap, excludeTypeNames))
                return true;
            // `dropped` only ever holds block-typedef names, so an alias-aware membership test
            // subsumes the old raw `blockTypedefNames.Contains(t.Name) && dropped.Contains(t.Name)`
            // check while also catching `typedef DroppedBlock Alias;` reaching a dropped delegate.
            if (NameOrAliasIn(t.Name, typedefMap, dropped))
                return true;
            if (t.IsBlock)
            {
                foreach (var bp in t.BlockParams)
                    if (ReferencesUnresolvable(bp))
                        return true;
                if (t.BlockReturnType != null && ReferencesUnresolvable(t.BlockReturnType))
                    return true;
            }
            return false;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var typedef in blockTypedefs)
            {
                if (dropped.Contains(typedef.Name))
                    continue;
                var block = typedef.UnderlyingType;
                var refsBad = block.BlockParams.Any(ReferencesUnresolvable)
                    || (block.BlockReturnType != null && ReferencesUnresolvable(block.BlockReturnType));
                if (refsBad)
                {
                    dropped.Add(typedef.Name);
                    changed = true;
                    diagnostics?.RecordSkip("Delegate", typedef.Name, ObjCSkipReason.UnresolvableType,
                        "block signature references a cross-boundary excluded Swift type");
                }
            }
        }

        return dropped;
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
        var candidates = structs.Where(s => !AppleFrameworkRegistry.IsObjCSystemStruct(s.Name) && !s.HasUnsafeLayout).ToList();

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
                    // The CamelCase heuristic in IsTypeResolvable would let it
                    // through, so we must check explicitly.
                    bool isSkippedModuleStruct = allModuleStructNames.Contains(checkType) && !emittable.Contains(checkType);

                    if (isSkippedModuleStruct || !ObjCTypeMapper.IsTypeResolvable(checkType, tempKnown, field.Type.Name))
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
            if (!ObjCTypeMapper.IsTypeResolvable(checkType, knownTypes, field.Type.Name))
            {
                logger.LogDebug("Skipping struct {StructName}: field '{FieldName}' has unresolvable type '{TypeName}'",
                    structDecl.Name, field.Name, checkType);
                diagnostics?.RecordSkip("Struct", structDecl.Name, ObjCSkipReason.UnresolvableType, $"field '{field.Name}' has unresolvable type '{checkType}'");
                return;
            }
        }

        sb.AppendLine("    [global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Sequential)]");
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
                sb.AppendLine($"        [global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = {field.Type.FixedArraySize})]");
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

    /// <summary>
    /// Emits the module's free C functions as a plain <c>DllImport</c> holder.
    /// <para/>
    /// Named <c>{Module}Functions</c>, not <c>{Module}Constants</c>: the module's extern constants
    /// now live in an <c>ApiDefinition.cs</c> <c>[Static] partial interface {Module}Constants</c>
    /// (the only input bgen synthesizes <c>[Field]</c> backing from), and bgen compiles its
    /// api-definition contract from <c>ApiDefinition.cs</c> plus every <c>ObjcBindingCoreSource</c>
    /// together — so a class here sharing that name is a CS0261 partial-kind conflict, not two
    /// separate types.
    /// </summary>
    static void EmitFunctionsClass(StringBuilder sb, ObjCModule module, Dictionary<string, ObjCTypeRef> typedefMap, HashSet<string> moduleLocalTypes, HashSet<string> knownTypes, HashSet<string> enumNames, HashSet<string> droppedDelegateNames, HashSet<string>? excludeTypeNames, ILogger logger, ObjCBindingDiagnostics? diagnostics)
    {
        sb.AppendLine($"    public static class {module.ModuleName}Functions");
        sb.AppendLine("    {");

        foreach (var function in module.Functions)
            EmitFunction(sb, function, typedefMap, moduleLocalTypes, knownTypes, enumNames, droppedDelegateNames, excludeTypeNames, logger, diagnostics);

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitFunction(StringBuilder sb, ObjCFunctionDecl function, Dictionary<string, ObjCTypeRef> typedefMap, HashSet<string> moduleLocalTypes, HashSet<string> knownTypes, HashSet<string> enumNames, HashSet<string> droppedDelegateNames, HashSet<string>? excludeTypeNames, ILogger logger, ObjCBindingDiagnostics? diagnostics)
    {
        // Skip variadic C functions — they require va_list which can't be safely P/Invoked
        if (function.IsVariadic)
        {
            diagnostics?.RecordSkip("Function", function.Name, ObjCSkipReason.VariadicFunction, "variadic C functions cannot be safely P/Invoked");
            return;
        }

        // Skip functions that take/return a block-typedef delegate we had to drop (its signature
        // referenced a cross-boundary excluded Swift type), or that reference such a type directly.
        // The named delegate would be undefined and the raw Swift type has no C# binding (CS0246).
        if (droppedDelegateNames.Count > 0 || (excludeTypeNames != null && excludeTypeNames.Count > 0))
        {
            // Resolve typedef aliases: a param typed as `typedef ExcludedType Alias` (or an alias
            // of a dropped delegate) passes a raw-name check but MapType expands it back to the
            // undefined name at emit time.
            bool Tainted(ObjCTypeRef t) =>
                NameOrAliasIn(t.Name, typedefMap, droppedDelegateNames)
                || (excludeTypeNames != null && NameOrAliasIn(t.Name, typedefMap, excludeTypeNames));
            if (Tainted(function.ReturnType) || function.Parameters.Any(p => Tainted(p.Type)))
            {
                logger.LogDebug("Skipping function {FuncName}: references dropped cross-boundary delegate or excluded Swift type", function.Name);
                diagnostics?.RecordSkip("Function", function.Name, ObjCSkipReason.UnresolvableType, "references a dropped cross-boundary delegate or excluded Swift type");
                return;
            }
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
        // from included headers whose definitions aren't available in C#). Pair each mapped
        // type with its retained source ObjC name so the resolvability check keys on the
        // source identity (amendment D), not the already-mapped text.
        var allTypeChecks = function.Parameters
            .Select((p, i) => (Mapped: paramTypes[i], Source: p.Type.Name))
            .Append((Mapped: returnType, Source: function.ReturnType.Name))
            .ToList();
        var unresolvable = allTypeChecks.FirstOrDefault(t => !ObjCTypeMapper.IsTypeResolvable(t.Mapped, knownTypes, t.Source));
        if (unresolvable.Mapped != null)
        {
            logger.LogDebug("Skipping function {FuncName}: unresolvable type '{TypeName}'", function.Name, unresolvable.Mapped);
            diagnostics?.RecordSkip("Function", function.Name, ObjCSkipReason.UnresolvableType, $"unresolvable type '{unresolvable.Mapped}'");
            return;
        }

        // A const pointer to a value type is read-only by construction, so it can never be the
        // `out T` the value-type-pointer path below emits — C# `out` zeroes the caller's storage
        // before the call, silently destroying the data the function was given to read. Unlike an
        // ObjC selector, a C function carries no keyword that could identify the parameter as an
        // array to project instead, so there is no sound signature for it and the function drops.
        var constPointerParam = function.Parameters.FirstOrDefault(
            p => ObjCTypeMapper.IsConstValueTypePointerParameter(p.Type, typedefMap, enumNames));
        if (constPointerParam != null)
        {
            var detail = $"parameter '{constPointerParam.Name}' ('{constPointerParam.Type.RawQualType}') is a const pointer to a value type — read-only, so it cannot be an out parameter, and a C function has no keyword identifying it as an array";
            logger.LogDebug("Skipping function {FuncName}: {Detail}", function.Name, detail);
            diagnostics?.RecordSkip("Function", function.Name, ObjCSkipReason.UnsupportedConstruct, detail);
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

        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, function.Availability, "        ");
        sb.AppendLine($"        [global::System.Runtime.InteropServices.DllImport(\"__Internal\")]");
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
