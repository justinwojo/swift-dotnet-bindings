// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;


public static class TypeDatabaseExtensions
{
    public readonly record struct AnyTypeFallbackInfo(string Reason, string SwiftType);
    private static readonly HashSet<string> BareGenericCSharpTypeNames = new(StringComparer.Ordinal)
    {
        "SwiftDictionary", "Swift.SwiftDictionary", "Swift.Runtime.SwiftDictionary",
        "SwiftArray", "Swift.SwiftArray", "Swift.Runtime.SwiftArray",
        "SwiftOptional", "Swift.SwiftOptional", "Swift.Runtime.SwiftOptional",
        "SwiftResult", "Swift.SwiftResult", "Swift.Runtime.SwiftResult",
        "SwiftSet", "Swift.SwiftSet", "Swift.Runtime.SwiftSet",
    };

    /// <summary>
    /// Determines whether the specified Swift type has been processed.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>True if the type has been processed; otherwise, false.</returns>
    public static bool IsTypeProcessed(this ITypeDatabase typeDatabase, TypeSpec typeSpec)
    {
        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => typeDatabase.IsTypeProcessed(namedTypeSpec),
            TupleTypeSpec { IsEmptyTuple: true } => true,
            ProtocolListTypeSpec => true, // Existential types are handled via ExistentialContainer
            _ => false
        };
    }

    /// <summary>
    /// Determines whether the specified Swift type has been processed.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>True if the type has been processed; otherwise, false.</returns>
    public static bool IsTypeProcessed(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec)
    {
        // M4 Session 1: dynamic-self / generic-parameter / primitive-alias strategies
        // route through TypeResolver. Treat the type as processed only when the
        // resolver produced a real TypeRecord — a skip-style outcome (Record is null)
        // must fall through to the legacy stages, parity with the other entry points.
        if (TypeResolver.Default.TryResolve(typeSpec, new ResolutionContext(typeDatabase), out var resolved)
            && resolved.Record is not null)
            return true;

        // Existential types (any X) are handled separately, not processed as regular types
        if (IsExistentialTypeName(typeSpec))
        {
            return true;
        }

        // Pointer types are always mapped to IntPtr
        if (IsPointerType(typeSpec))
        {
            return true;
        }

        // Unsupported Apple modules (SwiftUI, XCTest, etc.) are considered processed (→ AnyType)
        if (IsUnsupportedAppleModule(typeSpec))
            return true;

        // Bound-generic SIMD aliases resolve via the alias map (e.g., Swift.SIMD3<Swift.Float>).
        if (TryResolveBoundGenericAlias(typeDatabase, typeSpec, out _))
            return true;

        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        if (typeDatabase.IsTypeProcessed(typeName))
            return true;

        // ObjC class types get synthetic ObjCBridged records (DB-first to allow explicit overrides)
        return IsObjCModuleType(typeSpec);
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or the Any type if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrAnyType(this ITypeDatabase typeDatabase, TypeSpec typeSpec)
    {
        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => typeDatabase.GetTypeRecordOrAnyType(namedTypeSpec),
            TupleTypeSpec { IsEmptyTuple: true } => VoidType,
            ProtocolListTypeSpec protocolList => GetExistentialTypeRecord(protocolList),
            _ => AnyType
        };
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or the Any type if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrAnyType(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec)
    {
        // M4 Session 1: dynamic-self / generic-parameter / primitive-alias strategies
        // route through TypeResolver instead of the legacy inline checks.
        if (TypeResolver.Default.TryResolve(typeSpec, new ResolutionContext(typeDatabase), out var resolved)
            && resolved.Record is not null)
        {
            return resolved.Record;
        }

        // Existential types (any X) return AnyType
        if (IsExistentialTypeName(typeSpec))
        {
            return AnyType;
        }

        // Pointer types are always mapped to IntPtr
        if (IsPointerType(typeSpec))
        {
            return IntPtrType;
        }

        // Metatype types (Foundation.Decimal.Type) have no C# equivalent.
        // Without this guard, the nested flattening in CreateObjCBridgedTypeRecord
        // produces invalid names like "Foundation.DecimalType" (CS0234).
        if (WrapperValidation.IsMetatypeType(typeSpec))
        {
            return AnyType;
        }

        // Types from unsupported Apple framework modules (SwiftUI, XCTest, Combine, etc.)
        // get mapped to AnyType so members referencing them are gracefully suppressed.
        // Exception: registered non-generic types with C# ISwiftObject stubs resolve normally.
        if (IsUnsupportedAppleModule(typeSpec))
        {
            if (!typeSpec.ContainsGenericParameters)
            {
                var registeredName = SwiftTypeName.FromTypeSpec(typeSpec);
                if (typeDatabase.TryGetTypeRecord(registeredName, out var registeredRecord))
                    return registeredRecord;
            }
            return AnyType;
        }

        // Guard: known-generic types used without type arguments produce bare
        // C# types like "SwiftDictionary" (CS0305). Return AnyType to trigger skip.
        if (!typeSpec.ContainsGenericParameters && IsKnownGenericType(typeSpec.Name))
            return AnyType;

        // Bound-generic SIMD aliases: Swift.SIMD3<Swift.Float> → simd.simd_float3, etc.
        // Swift stdlib SIMD generics map to C simd module typedefs with different ABI layouts.
        if (TryResolveBoundGenericAlias(typeDatabase, typeSpec, out var aliasRecord))
            return aliasRecord;

        // ObjC types are handled in the SwiftTypeName overload (DB-first, synthetic second)
        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        return typeDatabase.GetTypeRecordOrAnyType(typeName);
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or throws an exception if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrThrow(this ITypeDatabase typeDatabase, TypeSpec typeSpec)
    {
        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => typeDatabase.GetTypeRecordOrThrow(namedTypeSpec),
            TupleTypeSpec { IsEmptyTuple: true } => VoidType,
            _ => throw new ArgumentException($"Attempted to read TypeRecord of unsupported type spec: {typeSpec}")
        };
    }

    /// <summary>
    /// Tries to get the type record for the specified Swift type.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="record">The type record.</param>
    /// <returns>True if the type record was found; otherwise, false.</returns>
    public static bool TryGetTypeRecord(this ITypeDatabase typeDatabase, TypeSpec typeSpec, [NotNullWhen(returnValue: true)] out TypeRecord? record)
    {
        record = null;
        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => typeDatabase.TryGetTypeRecord(namedTypeSpec, out record),
            TupleTypeSpec { IsEmptyTuple: true } => false,
            _ => false
        };
    }

    /// <summary>
    /// Tries to get the type record for the specified Swift type.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="record">The type record.</param>
    /// <returns>True if the type record was found; otherwise, false.</returns>
    public static bool TryGetTypeRecord(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec, [NotNullWhen(returnValue: true)] out TypeRecord? record)
    {
        // M4 Session 1: dynamic-self / generic-parameter / primitive-alias strategies
        // route through TypeResolver instead of the legacy inline checks.
        if (TypeResolver.Default.TryResolve(typeSpec, new ResolutionContext(typeDatabase), out var resolved)
            && resolved.Record is not null)
        {
            record = resolved.Record;
            return true;
        }

        // Existential types (any X) return AnyType
        if (IsExistentialTypeName(typeSpec))
        {
            record = AnyType;
            return true;
        }

        // Swift.Any and Swift.AnyObject are special protocol types with no concrete C# equivalent.
        // They are module-qualified so they don't match IsExistentialTypeName, and they aren't in
        // the type database. Map them to AnyType so members using them are gracefully skipped.
        if (typeSpec.Name is "Swift.Any" or "Swift.AnyObject")
        {
            record = AnyType;
            return true;
        }

        // Pointer types are always mapped to IntPtr
        if (IsPointerType(typeSpec))
        {
            record = IntPtrType;
            return true;
        }

        // Metatype types (Foundation.Decimal.Type) have no C# equivalent
        if (WrapperValidation.IsMetatypeType(typeSpec))
        {
            record = AnyType;
            return true;
        }

        // Types from unsupported Apple framework modules (SwiftUI, XCTest, Combine, etc.)
        // get mapped to AnyType so members referencing them are gracefully suppressed.
        // Exception: registered non-generic types with C# ISwiftObject stubs resolve normally.
        if (IsUnsupportedAppleModule(typeSpec))
        {
            if (!typeSpec.ContainsGenericParameters)
            {
                var registeredName = SwiftTypeName.FromTypeSpec(typeSpec);
                if (typeDatabase.TryGetTypeRecord(registeredName, out var registeredRecord))
                {
                    record = registeredRecord;
                    return true;
                }
            }
            record = AnyType;
            return true;
        }

        // Bound-generic SIMD aliases: Swift.SIMD3<Swift.Float> → simd.simd_float3, etc.
        // Must resolve here too (not only GetTypeRecordOrAnyType) — return/parameter mapping
        // paths call TryGetTypeRecord directly and would otherwise miss the alias.
        if (TryResolveBoundGenericAlias(typeDatabase, typeSpec, out var aliasRecord))
        {
            record = aliasRecord;
            return true;
        }

        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        if (typeDatabase.TryGetTypeRecord(typeName, out record))
            return true;

        // SwiftBindings.Apple supplement: pulled in before the ObjC synthetic fallback
        // so a Swift-only Apple type (e.g. Foundation.Locale.Language) resolves to its
        // managed projection in SwiftBindings.Apple rather than being force-bridged to
        // an ObjC class that does not exist. Records the identity so the csproj emitter
        // can add the PackageReference only for consumers that actually touch a supplement
        // type.
        //
        // INVARIANT: currentlyGeneratingModule is always null on this path. The main
        // generator never rebuilds the supplement through this helper — supplement
        // regeneration uses the dedicated AppleTypesCsEmitter pipeline, which never
        // flows through here. Mirrors the same contract in
        // TypeDatabase.ModuleTypeDatabase.TryGetTypeRecord; if the two paths ever
        // merge, both call sites need a real module name to keep the TypeOwnerRegistry
        // Level-5 (Local) fall-through correct.
        if (AppleSupplementResolver.TryResolve(typeName, currentlyGeneratingModule: null, out var supplementRecord))
        {
            AppleSupplementReferences.Record(typeName.ModuleQualifiedName);
            record = supplementRecord;
            return true;
        }

        // ObjC class types get synthetic ObjCBridged records (DB-first to allow explicit overrides)
        if (IsObjCModuleType(typeSpec))
        {
            record = CreateObjCBridgedTypeRecord(typeName);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or throws an exception if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrThrow(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec)
    {
        // M4 Session 1: dynamic-self / generic-parameter / primitive-alias strategies
        // route through TypeResolver instead of the legacy inline checks.
        if (TypeResolver.Default.TryResolve(typeSpec, new ResolutionContext(typeDatabase), out var resolved)
            && resolved.Record is not null)
        {
            return resolved.Record;
        }

        // Metatype types (Foundation.Decimal.Type) have no C# equivalent.
        if (WrapperValidation.IsMetatypeType(typeSpec))
        {
            return AnyType;
        }

        // Existential types (any X) return AnyType
        if (IsExistentialTypeName(typeSpec))
        {
            return AnyType;
        }

        // Swift.Any and Swift.AnyObject are special protocol types with no concrete C# equivalent
        if (typeSpec.Name is "Swift.Any" or "Swift.AnyObject")
        {
            return AnyType;
        }

        // Pointer types are always mapped to IntPtr
        if (IsPointerType(typeSpec))
        {
            return IntPtrType;
        }

        // Unsupported Apple modules (SwiftUI, XCTest, etc.) → AnyType
        // Exception: registered non-generic types with C# ISwiftObject stubs resolve normally.
        if (IsUnsupportedAppleModule(typeSpec))
        {
            if (!typeSpec.ContainsGenericParameters)
            {
                var registeredName = SwiftTypeName.FromTypeSpec(typeSpec);
                if (typeDatabase.TryGetTypeRecord(registeredName, out var registeredRecord))
                    return registeredRecord;
            }
            return AnyType;
        }

        // Bound-generic SIMD aliases: Swift.SIMD2/3/4<Swift.Float> → simd.simd_floatN.
        // Without this short-circuit, the bare name "Swift.SIMD2" is not in the TypeDatabase
        // (SIMD is a Swift stdlib generic with no direct TypeRecord) and GetTypeRecordOrThrow
        // would throw. Mirrors the same guard in GetTypeRecordOrAnyType / TryGetTypeRecord.
        if (TryResolveBoundGenericAlias(typeDatabase, typeSpec, out var aliasRecord))
            return aliasRecord;

        // ObjC types are handled in the SwiftTypeName overload (DB-first, synthetic second)
        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        return typeDatabase.GetTypeRecordOrThrow(typeName);
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or throws an exception if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="swiftTypeName">The Swift type name.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrThrow(this ITypeDatabase typeDatabase, SwiftTypeName swiftTypeName)
    {
        if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return record;

        // ObjC class types not in the database get synthetic ObjCBridged records.
        // Covers ObjectiveC/Foundation root classes and Apple framework module types.
        if (IsObjCClassSwiftType(swiftTypeName))
            return CreateObjCBridgedTypeRecord(swiftTypeName);

        throw new Exception($"Type {swiftTypeName.ModuleQualifiedName} not found in database.");
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or the Any type if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="swiftTypeName">The Swift type name.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrAnyType(this ITypeDatabase typeDatabase, SwiftTypeName swiftTypeName)
    {
        if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return record;

        // ObjC class types not in the database get synthetic ObjCBridged records.
        // Covers ObjectiveC/Foundation root classes and Apple framework module types.
        if (IsObjCClassSwiftType(swiftTypeName))
            return CreateObjCBridgedTypeRecord(swiftTypeName);

        return AnyType;
    }

    /// <summary>
    /// Tries to describe why a type would degrade to AnyType when resolving type records.
    /// Generic type parameters are excluded because they are expected to resolve through generic constraints.
    /// </summary>
    public static bool TryGetAnyTypeFallbackInfo(this ITypeDatabase typeDatabase, TypeSpec typeSpec, [NotNullWhen(true)] out AnyTypeFallbackInfo? fallbackInfo)
    {
        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                return typeDatabase.TryGetAnyTypeFallbackInfo(namedTypeSpec, out fallbackInfo);
            default:
                fallbackInfo = null;
                return false;
        }
    }

    /// <summary>
    /// Tries to describe why a named type would degrade to AnyType when resolving type records.
    /// </summary>
    public static bool TryGetAnyTypeFallbackInfo(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec, [NotNullWhen(true)] out AnyTypeFallbackInfo? fallbackInfo)
    {
        // M4 Session 1: dynamic-self / generic-parameter / primitive-alias resolutions are
        // intentional, not fallbacks. The resolver short-circuits to false on a hit so the
        // legacy fallback heuristics never re-classify these types as "missing from database".
        if (TypeResolver.Default.TryResolve(typeSpec, new ResolutionContext(typeDatabase), out var resolved)
            && resolved.Record is not null
            && resolved.SyntheticFallback is null)
        {
            fallbackInfo = null;
            return false;
        }

        // Metatype types are intentionally mapped to AnyType, not a fallback
        if (WrapperValidation.IsMetatypeType(typeSpec))
        {
            fallbackInfo = null;
            return false;
        }

        if (IsExistentialTypeName(typeSpec))
        {
            fallbackInfo = new AnyTypeFallbackInfo(
                "Existential type fallback",
                typeSpec.ToString());
            return true;
        }

        // Pointer types are fully handled (mapped to IntPtr), not a fallback
        if (IsPointerType(typeSpec))
        {
            fallbackInfo = null;
            return false;
        }

        // Unsupported Apple modules are intentionally mapped to AnyType, not a fallback
        if (IsUnsupportedAppleModule(typeSpec))
        {
            fallbackInfo = null;
            return false;
        }

        // ObjC framework types are handled via synthetic ObjCBridged records, not a fallback
        if (IsObjCModuleType(typeSpec))
        {
            fallbackInfo = null;
            return false;
        }

        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        if (typeDatabase.TryGetTypeRecord(typeName, out _))
        {
            fallbackInfo = null;
            return false;
        }

        fallbackInfo = new AnyTypeFallbackInfo(
            "Type is missing from the type database",
            typeName.ModuleQualifiedName);
        return true;
    }

    /// <summary>
    /// Detects bare generic C# type names (no &lt;...&gt; arguments), including nullable reference suffixes.
    /// </summary>
    public static bool IsBareGenericTypeName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        var normalized = typeName.Trim();
        if (normalized.EndsWith("?", StringComparison.Ordinal))
            normalized = normalized.Substring(0, normalized.Length - 1);

        return !normalized.Contains('<') && BareGenericCSharpTypeNames.Contains(normalized);
    }

    /// <summary>
    /// Gets the type record for Swift pointer types, mapped to System.IntPtr.
    /// Covers OpaquePointer, UnsafePointer, UnsafeMutablePointer, UnsafeRawPointer,
    /// UnsafeMutableRawPointer, and Builtin.RawPointer.
    /// </summary>
    public static TypeRecord IntPtrType { get; } = new TypeRecord
    {
        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.OpaquePointer"),
        MetadataAccessor = string.Empty,
        Flags = TypeRecordFlags.Frozen,
        Kind = TypeRecordKind.Struct,
    };

    /// <summary>
    /// Gets the type record for the Any type.
    /// </summary>
    /// <returns>The type record for the Any type.</returns>
    public static TypeRecord AnyType { get; } = new TypeRecord
    {
        CSharpTypeName = CSharpTypeName.AnyType,
        SwiftTypeName = SwiftTypeName.AnyType,
        MetadataAccessor = string.Empty,
        Flags = TypeRecordFlags.None,
        Kind = TypeRecordKind.Protocol,
    };

    /// <summary>
    /// Gets the type record for the Void type.
    /// </summary>
    /// <returns>The type record for the Void type.</returns>
    public static TypeRecord VoidType { get; } = new TypeRecord
    {
        CSharpTypeName = CSharpTypeName.VoidType,
        SwiftTypeName = SwiftTypeName.VoidType,
        MetadataAccessor = string.Empty,
        Flags = TypeRecordFlags.Frozen,
        Kind = TypeRecordKind.Struct,
    };

    /// <summary>
    /// Gets the type record for Swift.Error, mapped to Swift.Foundation.AnyError.
    /// This enables 'any Swift.Error' existentials to resolve through the type database
    /// instead of falling back to raw ExistentialContainer1.
    /// </summary>
    public static TypeRecord SwiftErrorType { get; } = new TypeRecord
    {
        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Foundation", "AnyError"),
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
        MetadataAccessor = string.Empty,
        Flags = TypeRecordFlags.Frozen,
        Kind = TypeRecordKind.Protocol,
    };

    /// <summary>
    /// Determines whether a TypeRecord represents a well-known stdlib protocol that maps
    /// to a direct runtime type (e.g., Swift.Error → AnyError) rather than a generated interface.
    /// Such protocols should not produce "I{Name}" constraints in generic where clauses.
    /// </summary>
    /// <remarks>
    /// Also covers the four compile-time marker protocols (<c>Sendable</c>, <c>Copyable</c>,
    /// <c>Escapable</c>, <c>SendableMetatype</c>) and the implicit actor protocol
    /// (<c>_Concurrency.Actor</c>). These have type-database entries so that classes /
    /// structs / enums (and especially actor types) which list them in their conformance
    /// arrays can resolve a TypeRecord during lookup, but they must never be projected
    /// into the generated C# surface — they have no witness table, no usable conformance
    /// descriptor, and no consumer-facing semantics.
    /// </remarks>
    public static bool IsWellKnownRuntimeProtocol(TypeRecord record)
    {
        var name = record.SwiftTypeName.ModuleQualifiedName;
        return name is "Swift.Error"
            or "Swift.Sendable"
            or "Swift.Copyable"
            or "Swift.Escapable"
            or "Swift.SendableMetatype"
            or "_Concurrency.Actor";
    }

    /// <summary>
    /// Gets the type record for an existential type (protocol or protocol composition).
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The type record for the existential type.</returns>
    private static TypeRecord GetExistentialTypeRecord(ProtocolListTypeSpec protocolList)
    {
        var protocolCount = protocolList.Protocols.Count;
        var protocolNames = protocolList.Protocols.Count == 0
            ? "Any"
            : string.Join(" & ", protocolList.Protocols.Keys.Select(p => p.NameWithoutModule));

        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", $"ExistentialContainer{protocolCount}"),
            // Use AnyType for existential types since they don't have a standard module-qualified name
            SwiftTypeName = SwiftTypeName.AnyType,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.Frozen, // Existential containers have fixed layout
            Kind = TypeRecordKind.Existential,
        };
    }

    /// <summary>
    /// The ObjectiveC module name in Swift ABI.
    /// TypeSpecParser.cs remaps "ObjectiveC.X" → "Foundation.X", so both must be checked.
    /// </summary>
    private const string ObjCModuleName = "ObjectiveC";

    // Swift module → .NET namespace overrides are centralized in AppleFrameworkRegistry.

    /// <summary>
    /// Returns true if the type is a known Apple framework value type (struct or enum)
    /// that should NOT be ObjC-bridged.
    /// </summary>
    internal static bool IsKnownAppleValueType(NamedTypeSpec typeSpec)
    {
        if (!typeSpec.HasModule())
            return false;
        return AppleFrameworkRegistry.IsKnownValueType(typeSpec.Name);
    }

    /// <summary>
    /// Creates a synthetic ObjCBridged TypeRecord for an ObjC class type.
    /// The resulting record triggers the existing ObjCBridged marshalling pipeline
    /// (IntPtr in P/Invoke, Handle extraction in wrappers).
    /// Types with explicit name remappings in the registry are resolved first;
    /// remaining types use module→namespace mapping + nested name flattening.
    /// </summary>
    private static TypeRecord CreateObjCBridgedTypeRecord(SwiftTypeName swiftTypeName)
    {
        // Check registry for explicit name remapping (Foundation Swift names → .NET ObjC names)
        if (AppleFrameworkRegistry.TryGetNetTypeName(swiftTypeName.ModuleQualifiedName, out var netName))
        {
            var dotIdx = netName.IndexOf('.');
            if (dotIdx > 0)
            {
                return new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName(
                        netName.Substring(0, dotIdx), netName.Substring(dotIdx + 1)),
                    SwiftTypeName = swiftTypeName,
                    MetadataAccessor = string.Empty,
                    Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class,
                };
            }
        }

        // Resolve C# namespace: use centralized Swift→.NET mapping, then ObjectiveC/Foundation → Foundation,
        // then use Swift module name as-is (e.g., UIKit → UIKit).
        var mappedModule = AppleFrameworkRegistry.MapModuleToNetNamespace(swiftTypeName.Module);
        string csharpNamespace;
        if (mappedModule != swiftTypeName.Module)
            csharpNamespace = mappedModule;
        else if (swiftTypeName.Module == ObjCModuleName || swiftTypeName.Module == "Foundation")
            csharpNamespace = "Foundation";
        else
            csharpNamespace = swiftTypeName.Module;

        // For nested ObjC types (e.g., UIKit.UIView.ContentMode), .NET iOS bindings flatten
        // the parent type into the name: UIView + ContentMode = UIViewContentMode.
        var csharpName = swiftTypeName.Name;
        var parts = swiftTypeName.ModuleQualifiedName.Split('.');
        if (parts.Length > 2)
        {
            csharpName = parts[1];
            for (int i = 2; i < parts.Length; i++)
            {
                csharpName = ConcatWithOverlapDedup(csharpName, parts[i]);
            }
        }

        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csharpNamespace, csharpName),
            SwiftTypeName = swiftTypeName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        };
    }

    /// <summary>
    /// Concatenates two name parts with overlap deduplication. If the first part ends with
    /// a substring that the second part starts with (case-sensitive), the overlapping portion
    /// is removed from the second part before concatenation.
    /// Example: "UITableViewCell" + "CellStyle" → "UITableViewCellStyle" (overlap: "Cell")
    /// </summary>
    internal static string ConcatWithOverlapDedup(string first, string second)
    {
        // Find the longest suffix of first that matches a prefix of second
        var maxOverlap = Math.Min(first.Length, second.Length);
        for (int len = maxOverlap; len > 0; len--)
        {
            if (first.AsSpan(first.Length - len).SequenceEqual(second.AsSpan(0, len)))
            {
                return first + second.Substring(len);
            }
        }
        return first + second;
    }

    /// <summary>
    /// Determines whether the specified NamedTypeSpec represents an ObjC class type
    /// that should get a synthetic ObjCBridged record.
    /// Covers two categories:
    /// 1. ObjectiveC/Foundation root classes (NSObject, NSProxy) — known safe subset
    /// 2. Apple framework module types (UIKit.UIImage, AppKit.NSImage) — assumed to be classes
    ///    unless listed in AppleFrameworkRegistry.ValueTypes
    /// TypeSpecParser.cs remaps "ObjectiveC.X" → "Foundation.X", so we check both modules.
    /// </summary>
    internal static bool IsObjCModuleType(NamedTypeSpec typeSpec)
    {
        if (!typeSpec.HasModule())
            return false;

        // Foundation typealiases to stdlib primitives (e.g., TimeInterval → Swift.Double) are
        // Swift value types, not ObjC classes. Without this exclusion, the synthetic ObjCBridged
        // record (Kind=Class) misroutes Optional<TimeInterval> through OptionalClassPointer
        // (AnyObject boxing), breaking the C#/Swift wrapper contract. The actual TypeRecord for
        // these typealiases is supplied by TryGetTypeRecord via the underlying primitive lookup.
        if (MarshallingHelpers.TypeAliasToCSPrimitive.ContainsKey(typeSpec.Name))
            return false;

        // ObjectiveC/Foundation root classes (conservative: only NSObject, NSProxy)
        if ((typeSpec.Module == ObjCModuleName || typeSpec.Module == "Foundation")
            && AppleFrameworkRegistry.IsKnownObjCRootClass(typeSpec.NameWithoutModule))
            return true;

        // Apple framework module types (UIKit, AppKit, etc.) are ObjC classes by default,
        // but exclude known value types (structs/enums) from those modules
        return AppleFrameworkRegistry.IsAutoBridgeModule(typeSpec.Module)
            && !AppleFrameworkRegistry.IsKnownValueType(typeSpec.Name);
    }

    /// <summary>
    /// Determines whether the specified NamedTypeSpec is from an Apple framework module
    /// that has no .NET iOS binding equivalent (SwiftUI, XCTest, Combine, etc.).
    /// </summary>
    private static bool IsUnsupportedAppleModule(NamedTypeSpec typeSpec)
    {
        if (!typeSpec.HasModule())
            return false;
        return AppleFrameworkRegistry.IsUnsupportedModule(typeSpec.Module);
    }

    /// <summary>
    /// Determines whether the specified SwiftTypeName represents an ObjC class type.
    /// Mirrors <see cref="IsObjCModuleType"/> but for the SwiftTypeName path.
    /// </summary>
    private static bool IsObjCClassSwiftType(SwiftTypeName swiftTypeName)
    {
        // Foundation typealiases to stdlib primitives (parity with IsObjCModuleType)
        if (MarshallingHelpers.TypeAliasToCSPrimitive.ContainsKey(swiftTypeName.ModuleQualifiedName))
            return false;

        // ObjectiveC/Foundation root classes
        if ((swiftTypeName.Module == ObjCModuleName || swiftTypeName.Module == "Foundation")
            && AppleFrameworkRegistry.IsKnownObjCRootClass(swiftTypeName.Name))
            return true;

        // Apple framework module types, excluding known value types
        return AppleFrameworkRegistry.IsAutoBridgeModule(swiftTypeName.Module)
            && !AppleFrameworkRegistry.IsKnownValueType(swiftTypeName.ModuleQualifiedName);
    }

    /// <summary>
    /// Determines whether the specified NamedTypeSpec represents a Swift pointer type
    /// that should be mapped to System.IntPtr.
    /// </summary>
    private static readonly HashSet<string> KnownGenericTypes = new(StringComparer.Ordinal)
    {
        "Dictionary", "Array", "Set", "Optional", "Result",
        "Swift.Dictionary", "Swift.Array", "Swift.Set", "Swift.Optional", "Swift.Result"
    };

    private static bool IsKnownGenericType(string name) => KnownGenericTypes.Contains(name);

    private static bool IsPointerType(NamedTypeSpec typeSpec)
    {
        return typeSpec.Name is "Swift.OpaquePointer" or "Swift.UnsafePointer"
            or "Swift.UnsafeMutablePointer" or "Swift.UnsafeRawPointer"
            or "Swift.UnsafeMutableRawPointer" or "Builtin.RawPointer";
    }

    /// <summary>
    /// Determines whether the specified NamedTypeSpec represents an existential type.
    /// Existential types come through as NamedTypeSpec with names like "any" or "any SomeProtocol"
    /// when parsing tuple elements or enum associated values containing existential types.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if this is an existential type name; otherwise, <c>false</c>.</returns>
    private static bool IsExistentialTypeName(NamedTypeSpec typeSpec)
    {
        // Check if the TypeSpec has the IsAny flag set (set by TypeSpecParser when "any" prefix is parsed)
        // This is the primary way existential types are detected (e.g., "any Swift.Encoder" -> IsAny=true, Name="Swift.Encoder")
        if (typeSpec.IsAny)
        {
            return true;
        }

        // Check for existential type patterns:
        // - "any" alone
        // - "any SomeProtocol" or "any Module.Protocol"
        if (typeSpec.Name == "any" || typeSpec.Name.StartsWith("any "))
        {
            return true;
        }

        // Don't classify generic type parameters as existential types.
        // Generic parameters (τ_0_0, T, Element, etc.) are unbound type parameters
        // that should be handled by the generic type system, not as existentials.
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            return false;
        }

        // Check if this is a type name without a module qualifier (no dot)
        // These are typically special types or parsing artifacts that should be treated as existential
        // Exclude known single-word types that are valid (Swift.Any, Swift.AnyObject are already prefixed)
        if (!typeSpec.HasModule() && typeSpec.Name != "Swift.Any" && typeSpec.Name != "Swift.AnyObject")
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Maps bound-generic Swift stdlib SIMD types to their C simd module aliases.
    /// E.g. <c>Swift.SIMD3&lt;Swift.Float&gt;</c> → <c>simd.simd_float3</c>, which is in turn
    /// projected onto <c>System.Numerics.Vector3</c> by <c>SimdDatabase.xml</c>. These aliases
    /// resolve to non-generic managed types — callers that reach for the FQN should NOT append
    /// the bound-generic's type arguments to the resolved record.
    /// </summary>
    private static readonly Dictionary<(string BaseName, string ElementType), string> BoundGenericSimdAliases = new()
    {
        { ("Swift.SIMD2", "Swift.Float"), "simd.simd_float2" },
        { ("Swift.SIMD3", "Swift.Float"), "simd.simd_float3" },
        { ("Swift.SIMD4", "Swift.Float"), "simd.simd_float4" },
    };

    /// <summary>
    /// Attempts to resolve a bound-generic TypeSpec (e.g. <c>Swift.SIMD3&lt;Swift.Float&gt;</c>)
    /// through the <see cref="BoundGenericSimdAliases"/> table. Exposed as <c>internal</c> so
    /// callers that format C# type names (e.g. <c>TupleHandler.TranslateBoundGenericToCSharp</c>)
    /// can short-circuit to the non-generic alias record instead of appending generic arguments
    /// to a typealias that doesn't accept them.
    /// </summary>
    internal static bool TryResolveBoundGenericAlias(
        ITypeDatabase typeDatabase, NamedTypeSpec typeSpec,
        [NotNullWhen(true)] out TypeRecord? record)
    {
        record = null;
        if (typeSpec.GenericParameters.Count != 1)
            return false;

        if (typeSpec.GenericParameters[0] is not NamedTypeSpec elementSpec)
            return false;

        if (!BoundGenericSimdAliases.TryGetValue((typeSpec.Name, elementSpec.Name), out var aliasName))
            return false;

        var aliasTypeName = SwiftTypeName.FromModuleQualifiedName(aliasName);
        return typeDatabase.TryGetTypeRecord(aliasTypeName, out record);
    }
}
