// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;


public static class TypeDatabaseExtensions
{
    public readonly record struct AnyTypeFallbackInfo(string Reason, string SwiftType)
    {
        /// <summary>
        /// The <see cref="Reason"/> value set when a protocol existential (<c>any P</c>) cannot be
        /// projected to a real C# type and degrades to <c>object</c>. Used to route the loud
        /// SWIFTBIND023 diagnostic — distinguishing existential degradation from unrelated fallbacks
        /// (unsupported closures, unknown generics) that share the <c>[UnsupportedSwiftType]</c> path.
        /// </summary>
        public const string ExistentialFallbackReason = "Existential type fallback";
    }
    private static readonly HashSet<string> BareGenericCSharpTypeNames = new(StringComparer.Ordinal)
    {
        "SwiftDictionary", "Swift.SwiftDictionary", "Swift.Runtime.SwiftDictionary",
        "SwiftArray", "Swift.SwiftArray", "Swift.Runtime.SwiftArray",
        "SwiftOptional", "Swift.SwiftOptional", "Swift.Runtime.SwiftOptional",
        "SwiftResult", "Swift.SwiftResult", "Swift.Runtime.SwiftResult",
        "SwiftSet", "Swift.SwiftSet", "Swift.Runtime.SwiftSet",
        "SwiftClosedRange", "Swift.SwiftClosedRange", "Swift.Runtime.SwiftClosedRange",
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
    /// <remarks>
    /// Single-path policy: this overload now agrees with
    /// <see cref="TryGetTypeRecord(ITypeDatabase, NamedTypeSpec, out TypeRecord)"/>
    /// across the entire <see cref="TypeResolver.Default"/> chain. The legacy
    /// stage chain only consulted the module DB / module-alias / Apple-umbrella
    /// paths, which made <c>IsTypeProcessed</c> disagree with
    /// <c>TryGetTypeRecord</c> on supplement-owned identities (e.g.,
    /// <c>Foundation.Locale.Language</c>) and on metatype / bare-generic /
    /// <c>Swift.Any</c>-style consolidations the resolver claims as intentional
    /// resolutions. The user-visible effect is in
    /// <c>ModuleProcessor</c>'s cross-module property gate: properties whose
    /// type the resolver can marshal (because the supplement projection or
    /// fallback record exists) are no longer skipped with a "type should have
    /// been processed in a previous module but was not found" warning.
    /// </remarks>
    public static bool IsTypeProcessed(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec)
    {
        return TypeResolver.Default.TryResolve(typeSpec, new ResolutionContext(typeDatabase), out var resolved)
            && resolved.Record is not null;
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
        if (TypeResolver.Default.TryResolve(typeSpec, new ResolutionContext(typeDatabase), out var resolved)
            && resolved.Record is not null)
        {
            return resolved.Record;
        }

        return AnyType;
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
        if (TypeResolver.Default.TryResolve(typeSpec, new ResolutionContext(typeDatabase), out var resolved)
            && resolved.Record is not null)
        {
            record = resolved.Record;
            return true;
        }

        record = null;
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
        if (TypeResolver.Default.TryResolve(typeSpec, new ResolutionContext(typeDatabase), out var resolved)
            && resolved.Record is not null)
        {
            return resolved.Record;
        }

        throw new Exception($"Type {SwiftTypeName.FromTypeSpec(typeSpec).ModuleQualifiedName} not found in database.");
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
                // KNOWN GAP: only NamedTypeSpec degradations are classified here. A
                // ProtocolListTypeSpec composition (`any P & Q`) that ExistentialHandler
                // .GetPublicExistentialType collapses to `object` (class-bound, `any Sendable`,
                // PAT-without-projection) is invisible to BOTH the [UnsupportedSwiftType] flag
                // and the SWIFTBIND023 diagnostic, because the resolver/strategy that produces
                // the SyntheticFallback only matches NamedTypeSpec too. Detecting *only* the
                // degrading compositions (GetPublicExistentialType returns a real composition
                // interface when EffectiveProtocolsHaveTypeRecords) requires the resolver itself
                // to record the degradation — the Finding 21 configured/unconfigured-fork
                // refactor — rather than mirroring projectability logic into a second universe
                // here, which is the Finding 10 "two resolution universes" duplication this gap
                // is deliberately NOT widened into. Pinned by
                // TypeDatabaseExtensionsTests.TryGetAnyTypeFallbackInfo_ProtocolListComposition_ReturnsFalse
                // so a future fix flips a red test rather than landing as silent drift.
                fallbackInfo = null;
                return false;
        }
    }

    /// <summary>
    /// Tries to describe why a named type would degrade to AnyType when resolving type records.
    /// </summary>
    public static bool TryGetAnyTypeFallbackInfo(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec, [NotNullWhen(true)] out AnyTypeFallbackInfo? fallbackInfo)
    {
        // The resolver carries fallback intent inline: a strategy that produced a real
        // record but tagged it with SyntheticFallback wants the degradation surfaced
        // (e.g., the existential strategy). A strategy that produced a record without
        // a fallback tag is an intentional resolution. No claim at all means the type
        // is genuinely missing from every resolution path.
        if (TypeResolver.Default.TryResolve(typeSpec, new ResolutionContext(typeDatabase), out var resolved))
        {
            if (resolved.SyntheticFallback is not null)
            {
                fallbackInfo = resolved.SyntheticFallback;
                return true;
            }

            if (resolved.Record is not null)
            {
                fallbackInfo = null;
                return false;
            }
        }

        fallbackInfo = new AnyTypeFallbackInfo(
            "Type is missing from the type database",
            SwiftTypeName.FromTypeSpec(typeSpec).ModuleQualifiedName);
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
        // Unlike the compile-time marker protocols, the stdlib error protocol has a real
        // protocol descriptor and a real witness table. It has no projected C# interface
        // (it maps to a runtime struct), so a generic constrained on it resolves its witness
        // table dynamically from this descriptor symbol instead of a static interface.
        ProtocolDescriptorSymbol = "$ss5ErrorMp",
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
        => IsWellKnownRuntimeProtocol(record.SwiftTypeName.ModuleQualifiedName);

    /// <summary>
    /// Name-only form, for callers that must reach the same verdict BEFORE a record lookup — a gate
    /// that reports why a conformance was dropped has to recognize these by name, or a run whose type
    /// database happens not to carry <c>Swift.Sendable</c> reports every type's implicit marker
    /// conformances as a loss. Shares the one list with the record form so the two cannot drift.
    /// </summary>
    public static bool IsWellKnownRuntimeProtocol(string moduleQualifiedName)
        => moduleQualifiedName is "Swift.Error"
            or "Swift.Sendable"
            or "Swift.Copyable"
            or "Swift.Escapable"
            or "Swift.SendableMetatype"
            or "_Concurrency.Actor";

    /// <summary>
    /// The marker subset of <see cref="IsWellKnownRuntimeProtocol"/>: stdlib protocols
    /// that the compiler erases at runtime and that have no witness table, no protocol
    /// descriptor symbol, and no slot in any <c>...Ma</c> metadata accessor signature
    /// (<c>Swift.Sendable</c> / <c>Swift.Copyable</c> / <c>Swift.Escapable</c> /
    /// <c>Swift.SendableMetatype</c> / <c>Swift.BitwiseCopyable</c>). Distinct from
    /// <c>Swift.Error</c> and <c>_Concurrency.Actor</c>, which ARE well-known runtime
    /// protocols but DO carry a witness table and therefore appear in <c>...Ma</c>
    /// signatures — those must continue to gate-block wrapper emission because the C#
    /// side cannot materialize their PWT. Mirrors the local
    /// <c>PInvokeHelperEmitter.IsStdlibMarkerProtocol</c> set; promoted to the shared
    /// extension so emitter gates can ask the question without duplicating the list.
    /// </summary>
    public static bool IsStdlibMarkerProtocol(TypeRecord record)
    {
        var name = record.SwiftTypeName.ModuleQualifiedName;
        return name is "Swift.Sendable"
            or "Swift.Copyable"
            or "Swift.Escapable"
            or "Swift.SendableMetatype"
            or "Swift.BitwiseCopyable";
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
    /// remaining types are verified against the real Microsoft.iOS surface (see
    /// <see cref="AppleTypeSurfaceIndex"/>) and, failing that, fall back to
    /// module→namespace mapping + nested name flattening.
    /// </summary>
    /// <param name="usr">
    /// The referenced declaration's USR from the ABI JSON, when present. A clang integer-enum
    /// (<c>c:@E@…</c>), typedef (<c>c:@T@…</c>), or struct (<c>c:@S@…</c>) symbol names the real
    /// Apple type and marks it a value type; used both to look up the true .NET identity and to
    /// decide, when the type isn't in the binding, that a member referencing it must be skipped
    /// rather than dangle as a phantom class.
    /// </param>
    internal static TypeRecord CreateObjCBridgedTypeRecord(SwiftTypeName swiftTypeName, string? usr = null)
    {
        // Check registry for explicit name remapping (Foundation Swift names → .NET ObjC names)
        if (TryGetRegistryRemappedIdentity(swiftTypeName, out var regNamespace, out var regName))
        {
            // Surface-verify the hand-authoritative remap against what the binding actually ships:
            // project an integer enum as a value type, or withdraw a name the binding declares as a
            // struct/static-constants/absent shape a Handle-bearing class can't stand in for. The
            // registry name is the sole candidate (usr null) and a no-hit keeps the registry record
            // (withdrawOnNoHit: false) — the remap is trusted; only a definitive wrong-kind hit
            // overrides it.
            var regVerified = TryProjectViaAppleSurface(
                swiftTypeName, usr: null, regNamespace, regName,
                AppleTypeSurfaceIndex.Default, withdrawOnNoHit: false);
            if (regVerified is not null)
                return regVerified;

            return new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(regNamespace, regName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            };
        }

        DeriveSynthesizedAppleIdentity(swiftTypeName, out var csharpNamespace, out var csharpName);

        // Verify the synthesized identity against the type Microsoft.iOS actually declares: correct
        // the name, project integer enums as value types, or mark an absent/value/static type so the
        // referencing member is skipped. Null result = degrade to the synthesized class below.
        var verified = TryProjectViaAppleSurface(swiftTypeName, usr, csharpNamespace, csharpName);
        if (verified is not null)
            return verified;

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
    /// Splits the registry's hand-authored Swift→.NET remap for this type into a namespace and a
    /// simple name. False when no remap exists, or when the mapped name carries no namespace
    /// separator (nothing to bind the type to).
    /// </summary>
    private static bool TryGetRegistryRemappedIdentity(
        SwiftTypeName swiftTypeName,
        [NotNullWhen(true)] out string? netNamespace,
        [NotNullWhen(true)] out string? netName)
    {
        netNamespace = null;
        netName = null;
        if (!AppleFrameworkRegistry.TryGetNetTypeName(swiftTypeName.ModuleQualifiedName, out var mapped))
            return false;

        var dotIdx = mapped.IndexOf('.');
        if (dotIdx <= 0)
            return false;

        netNamespace = mapped.Substring(0, dotIdx);
        netName = mapped.Substring(dotIdx + 1);
        return true;
    }

    /// <summary>
    /// Derives the .NET identity the platform binding is expected to use for an Apple type that has
    /// no hand-authored remap: the Swift module mapped to its binding namespace, and — for a nested
    /// Swift type such as <c>UIKit.UIView.ContentMode</c> — the parent chain flattened into a single
    /// name the way the platform bindings spell it (<c>UIViewContentMode</c>).
    /// </summary>
    private static void DeriveSynthesizedAppleIdentity(
        SwiftTypeName swiftTypeName, out string csharpNamespace, out string csharpName)
    {
        // Resolve C# namespace: use centralized Swift→.NET mapping, then ObjectiveC/Foundation → Foundation,
        // then use Swift module name as-is (e.g., UIKit → UIKit).
        var mappedModule = AppleFrameworkRegistry.MapModuleToNetNamespace(swiftTypeName.Module);
        if (mappedModule != swiftTypeName.Module)
            csharpNamespace = mappedModule;
        else if (swiftTypeName.Module == ObjCModuleName || swiftTypeName.Module == "Foundation")
            csharpNamespace = "Foundation";
        else
            csharpNamespace = swiftTypeName.Module;

        csharpName = swiftTypeName.Name;
        var parts = swiftTypeName.ModuleQualifiedName.Split('.');
        if (parts.Length > 2)
        {
            csharpName = parts[1];
            for (int i = 2; i < parts.Length; i++)
            {
                csharpName = ConcatWithOverlapDedup(csharpName, parts[i]);
            }
        }
    }

    /// <summary>
    /// Builds a value-type record for an Apple framework type the registry describes as an
    /// integer-backed enum.
    /// <para>
    /// Listing a name as a value type only withholds the synthetic bridged-class record — it says
    /// "not an ObjC class" and nothing else, which leaves the name with no record at all and skips
    /// every member that mentions it. That is the right outcome for a shape the generator can't
    /// marshal, but a plain integer enum crosses the boundary as its raw value and is fully
    /// bindable. A registry entry that additionally declares the enum shape supplies the one fact
    /// nothing else can establish — that the <em>Swift</em> side is a raw-value enum, not a
    /// String-wrapping typed-constant group, which the platform bindings also project as a C# enum.
    /// </para>
    /// <para>
    /// The .NET identity (namespace, spelling, raw-value width, option-set-ness) is read back from
    /// the platform reference assembly rather than hand-carried, so it can never drift from the
    /// surface the emitted C# must compile against. No index (workload absent), no match, or a match
    /// the binding declares as something other than an enum all return null, which leaves the name
    /// exactly as fail-closed as it is today.
    /// </para>
    /// </summary>
    internal static TypeRecord? TryCreateRegisteredAppleEnumRecord(
        SwiftTypeName swiftTypeName, string? usr, Func<AppleTypeSurfaceIndex?> indexProvider)
    {
        // No auto-bridge-module gate: the registry description is strictly stronger than module
        // membership. IsIntegerEnumValueType can only be true for a module that HAS a registry entry
        // whose valueTypes list names this type as an integer enum, and the surface index
        // independently confirms the .NET binding declares an enum. Requiring autoBridge on top of
        // that only withheld the record from framework entries that carry no ObjC-bridging flags at
        // all (ImageIO's CGImagePropertyOrientation), whose members then skipped as unresolvable.
        if (!AppleFrameworkRegistry.IsIntegerEnumValueType(swiftTypeName.ModuleQualifiedName))
            return null;
        // The index is a provider, not a value, so the reference-pack surface is only resolved (and
        // on first touch, built) for a name the registry actually describes — not for every name
        // that merely reaches this arm on its way to failing resolution.
        if (indexProvider() is not { } index)
            return null;

        if (!TryGetRegistryRemappedIdentity(swiftTypeName, out var csharpNamespace, out var csharpName))
            DeriveSynthesizedAppleIdentity(swiftTypeName, out csharpNamespace, out csharpName);

        foreach (var candidate in AppleSurfaceCandidateNames(usr, csharpName))
        {
            if (index.TryResolveQualified(csharpNamespace, candidate, out var hit)
                || index.TryResolveBare(candidate, out hit))
            {
                return hit.Kind == AppleTypeSurfaceKind.Enum
                    ? CreateExternalEnumRecord(swiftTypeName, hit)
                    : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a record for an Apple framework type the registry describes as an NSString-backed
    /// NS_STRING_ENUM / NS_TYPED_ENUM.
    /// <para>
    /// Swift imports one of these as a String-backed <c>RawRepresentable</c> newtype — a struct that
    /// freely bridges to <c>NSString</c> via <c>_ObjectiveCBridgeable</c>, so it crosses the
    /// <c>@_cdecl</c> boundary as an ObjC object pointer (and a container of them crosses as an
    /// NSArray/NSSet/NSDictionary), exactly like <c>URL</c>↔<c>NSURL</c>. The platform binding
    /// projects the constant group as a C# <c>enum</c> plus a sibling <c>{Name}Extensions</c> class
    /// whose <c>GetConstant</c>/<c>GetValue</c> convert between the enum and the backing
    /// <c>NSString</c>, so the emitted C# can keep the idiomatic enum in its public signature and
    /// convert at the boundary.
    /// </para>
    /// <para>
    /// Both halves are verified against the reference assembly before the record is built: the name
    /// must resolve to an enum AND the sibling extensions class must exist. Without the sibling
    /// there is no way to reach the backing constant, so the type stays unresolvable (fail-closed)
    /// rather than emitting C# that cannot compile. No index (workload absent) does the same.
    /// </para>
    /// </summary>
    internal static TypeRecord? TryCreateRegisteredAppleTypedEnumRecord(
        SwiftTypeName swiftTypeName, string? usr, Func<AppleTypeSurfaceIndex?> indexProvider)
    {
        if (!AppleFrameworkRegistry.IsStringEnumValueType(swiftTypeName.ModuleQualifiedName))
            return null;
        if (indexProvider() is not { } index)
            return null;

        if (!TryGetRegistryRemappedIdentity(swiftTypeName, out var csharpNamespace, out var csharpName))
            DeriveSynthesizedAppleIdentity(swiftTypeName, out csharpNamespace, out csharpName);

        foreach (var candidate in AppleSurfaceCandidateNames(usr, csharpName))
        {
            if (!index.TryResolveQualified(csharpNamespace, candidate, out var hit)
                && !index.TryResolveBare(candidate, out hit))
                continue;

            if (hit.Kind != AppleTypeSurfaceKind.Enum)
                return null;

            // The converter the emitted C# will call has to actually ship. Microsoft's generator
            // emits it as a static (abstract+sealed) class named "{Enum}Extensions" beside the enum;
            // the index classifies that shape as StaticConstants.
            var extensionsName = hit.Name + AppleTypedEnumExtensionsSuffix;
            if (!index.TryResolveQualified(hit.Namespace, extensionsName, out var extensions)
                || extensions.Kind != AppleTypeSurfaceKind.StaticConstants)
                return null;

            var nsString = CSharpTypeName.FromNamespaceAndName("Foundation", "NSString");
            return new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(hit.Namespace, hit.Name),
                NativeTypeName = nsString,
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.ObjCBridgeable | TypeRecordFlags.AppleTypedEnum,
                Kind = TypeRecordKind.Struct,
            };
        }

        return null;
    }

    /// <summary>
    /// Suffix Microsoft's binding generator gives the static converter class it emits beside a
    /// projected NS_STRING_ENUM / NS_TYPED_ENUM (e.g. <c>VNBarcodeSymbologyExtensions</c>).
    /// </summary>
    internal const string AppleTypedEnumExtensionsSuffix = "Extensions";

    /// <summary>
    /// Resolves an ObjC-bridged reference against the real Microsoft.iOS surface. Returns a
    /// corrected record (integer enum value type, or class with the true name/namespace), a
    /// skip-marked record when the type is a value/static/absent shape a phantom class can't stand
    /// in for, or null to fall back to name synthesis (index unavailable / no reliable match on a
    /// non-value-type reference).
    /// </summary>
    private static TypeRecord? TryProjectViaAppleSurface(
        SwiftTypeName swiftTypeName, string? usr, string synthNamespace, string synthName)
        => TryProjectViaAppleSurface(swiftTypeName, usr, synthNamespace, synthName,
            AppleTypeSurfaceIndex.Default, withdrawOnNoHit: true);

    /// <summary>
    /// Core decision tree with the surface <paramref name="index"/> injected, so it can be exercised
    /// against a hand-built index without the installed Apple workload. A null index models the
    /// workload-absent case (graceful degradation to name synthesis). The production entry point reads
    /// the platform-matched singleton.
    /// <para>
    /// <paramref name="withdrawOnNoHit"/> chooses what a genuine no-hit means. On the synthesis path
    /// the candidate name is the generator's own guess: when the platform-authoritative index has no
    /// match at all, the synthesized qualified name is provably absent (the qualified lookup ran
    /// first), so an emitted bridged class would dangle — withdraw the referencing member. On the
    /// registry-verify path the name is a hand-authoritative remap, so a no-hit keeps that record (the
    /// index may simply not cover it) and only a definitive wrong-kind hit corrects or withdraws it.
    /// </para>
    /// <para>
    /// Every <em>absence</em> verdict below is additionally confined to namespaces the index actually
    /// covers. The index reflects one platform reference assembly and never the sibling binding
    /// packages a binding may also reference, so a miss in a namespace it declares nothing in is not
    /// evidence of absence — the type can be supplied by a referenced sibling package, and withdrawing
    /// it deletes public API that would have compiled. Coverage makes the index an authority for the
    /// namespaces it does reflect; it does not make it a complete authority (a sibling package that
    /// contributes a type to an already-covered namespace is still misjudged — closing that needs the
    /// sibling assemblies reflected into the index, which the generator has no resolved paths for at
    /// generation time).
    /// </para>
    /// </summary>
    internal static TypeRecord? TryProjectViaAppleSurface(
        SwiftTypeName swiftTypeName, string? usr, string synthNamespace, string synthName,
        AppleTypeSurfaceIndex? index, bool withdrawOnNoHit = false)
    {
        // A bridged NSError reference (Swift's NS_ERROR_ENUM import) is a struct, not a raw enum,
        // and cannot be reconstructed from a raw integer — skip any member that takes/returns one.
        // This is decided from the USR alone (no surface lookup) so it holds even without the index.
        if (IsBridgedNSErrorReference(usr, swiftTypeName.Name))
            return CreateAbsentAppleRecord(swiftTypeName, synthNamespace, synthName);

        if (index is null)
            return null; // Workload not installed → graceful degradation to synthesis.

        // Candidate .NET names, most authoritative first: the clang/ObjC USR symbol names the real
        // Apple type (which the flattening often gets wrong), then the synthesized flattened name.
        AppleTypeSurfaceEntry? hit = null;
        bool qualified = false;
        foreach (var candidate in AppleSurfaceCandidateNames(usr, synthName))
        {
            if (index.TryResolveQualified(synthNamespace, candidate, out hit))
            {
                qualified = true;
                break;
            }
            if (index.TryResolveBare(candidate, out hit))
            {
                qualified = false;
                break;
            }
        }

        if (hit is not null)
        {
            // Integer enums project as value types regardless of which candidate matched — the
            // raw-value marshalling is namespace-independent and uses the reflected identity.
            if (hit.Kind == AppleTypeSurfaceKind.Enum)
                return CreateExternalEnumRecord(swiftTypeName, hit);

            // A namespace-exact class match corrects the name/namespace to what actually ships.
            // A bare (cross-namespace) class match is too ambiguous to override a name that may
            // already compile — leave it to synthesis.
            if (qualified && hit.Kind == AppleTypeSurfaceKind.Class)
                return CreateCorrectedObjCClassRecord(swiftTypeName, hit);

            // A namespace-exact struct / static-constants / protocol has no Handle-bearing class to
            // marshal through — skip the referencing member.
            if (qualified)
                return CreateAbsentAppleRecord(swiftTypeName, synthNamespace, synthName);

            // A bare (cross-namespace) hit we declined to correct falls through deliberately: it can
            // neither correct the reference nor confirm it, and it says nothing about the namespace
            // that was asked about — the qualified lookup there already missed. Letting it short-
            // circuit the decisions below would retain a name the exact lookup disproved.
        }

        // Absence verdicts require the index to be an authority for this namespace — see the remark
        // on the parameter above. Computed once, applied to both arms so neither can drift into
        // claiming an absence the index cannot support.
        var namespaceIsCovered = index.CoversNamespace(synthNamespace);

        // A clang value-type reference (integer enum, typedef, or C struct) the binding doesn't
        // declare would dangle as a phantom class → skip, regardless of caller trust.
        if (namespaceIsCovered && IsClangImportedValueTypeUsr(usr))
            return CreateAbsentAppleRecord(swiftTypeName, synthNamespace, synthName);

        // A genuine no-hit — no candidate matched a usable name — under a platform-authoritative index
        // the caller trusts (synthesis path): the synthesized qualified name is absent from a namespace
        // the index does cover, so an emitted bridged class would be a CS0234/CS0246 dangling
        // reference. Withdraw the member.
        if (withdrawOnNoHit && namespaceIsCovered)
            return CreateAbsentAppleRecord(swiftTypeName, synthNamespace, synthName);

        return null;
    }

    /// <summary>Candidate .NET simple names for surface lookup, USR-derived first, then synthesized.</summary>
    private static IEnumerable<string> AppleSurfaceCandidateNames(string? usr, string synthName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usrName = DeriveNameFromUsr(usr);
        if (!string.IsNullOrEmpty(usrName) && seen.Add(usrName))
            yield return usrName;
        if (!string.IsNullOrEmpty(synthName) && seen.Add(synthName))
            yield return synthName;
    }

    /// <summary>
    /// Extracts the type's simple name from a USR: <c>c:objc(cs)UIView</c>/<c>c:objc(pl)…</c> →
    /// the trailing name; <c>c:@E@X</c>/<c>c:@T@X</c>/<c>c:@S@X</c> (optionally module-qualified
    /// <c>c:@M@Mod@E@X</c>) → the segment after the last <c>@</c>. Swift USRs are skipped (the
    /// synthesized name already carries the leaf). Returns null when nothing can be derived.
    /// </summary>
    internal static string? DeriveNameFromUsr(string? usr)
    {
        if (string.IsNullOrEmpty(usr))
            return null;

        if (usr.StartsWith("c:objc(", StringComparison.Ordinal))
        {
            int paren = usr.IndexOf(')');
            if (paren > 0 && paren + 1 < usr.Length)
                return usr.Substring(paren + 1);
            return null;
        }

        if (usr.StartsWith("c:", StringComparison.Ordinal))
        {
            int at = usr.LastIndexOf('@');
            if (at >= 0 && at + 1 < usr.Length)
                return usr.Substring(at + 1);
        }

        return null;
    }

    /// <summary>
    /// True when the USR is a clang-imported value type — an integer enum (<c>c:@E@</c>), a typedef
    /// (<c>c:@T@</c>), or a C struct (<c>c:@S@</c>, and <c>c:@SA@</c> for an anonymous aggregate
    /// named through a typedef, which is how vector/geometry aggregates and many CoreFoundation
    /// structs arrive), including module-qualified forms. These cross the boundary by value; if the
    /// platform binding has no such type, a synthesized bridged class would be a dangling reference,
    /// so the member must be skipped.
    /// </summary>
    internal static bool IsClangImportedValueTypeUsr(string? usr)
        => !string.IsNullOrEmpty(usr)
            && usr.StartsWith("c:", StringComparison.Ordinal)
            && (usr.Contains("@E@", StringComparison.Ordinal)
                || usr.Contains("@T@", StringComparison.Ordinal)
                || usr.Contains("@S@", StringComparison.Ordinal)
                || usr.Contains("@SA@", StringComparison.Ordinal));

    /// <summary>
    /// True when the reference is Swift's NS_ERROR_ENUM import: a clang error enum whose USR is
    /// <c>c:@E@{Name}Code</c> that Swift surfaces NOT as a flat enum but as a bridged NSError
    /// <em>struct</em> named <c>{Name}</c> (with the enum nested as <c>{Name}.Code</c>). The ABI
    /// spells the referenced type as that struct — its leaf name is <c>{Name}</c>, matching the USR
    /// name with the trailing <c>Code</c> removed. The struct wraps a full <c>NSError</c> and has no
    /// <c>init(rawValue:)</c>, so it can't be reconstructed from a raw integer and the member must be
    /// skipped. The leaf-name match is what keeps this precise: a direct reference to the nested
    /// <c>{Name}.Code</c> enum (leaf <c>Code</c>) or a flat enum Swift kept as <c>{Name}Code</c>
    /// (leaf <c>{Name}Code</c>) does not match, so neither is misclassified as a bridged error.
    /// </summary>
    internal static bool IsBridgedNSErrorReference(string? usr, string swiftLeafName)
    {
        var usrName = DeriveNameFromUsr(usr);
        if (string.IsNullOrEmpty(usrName)
            || !usr!.Contains("@E@", StringComparison.Ordinal)
            || !usrName.EndsWith("Code", StringComparison.Ordinal))
            return false;

        var bridgedStructName = usrName[..^"Code".Length];
        return bridgedStructName.Length > 0
            && string.Equals(bridgedStructName, swiftLeafName, StringComparison.Ordinal);
    }

    /// <summary>Projects a Microsoft.iOS integer enum as a SimpleEnum value-type record.</summary>
    private static TypeRecord CreateExternalEnumRecord(SwiftTypeName swiftTypeName, AppleTypeSurfaceEntry entry)
    {
        // ExternalAppleEnum steers @_cdecl reconstruction to the failability-agnostic form: the
        // managed [Flags] attribute distinguishes NS_ENUM (failable init) from NS_OPTIONS
        // (non-failable OptionSet init) only when the binding actually applied it, which is not
        // guaranteed, so the reconstruction must compile for either init shape.
        var flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum | TypeRecordFlags.ExternalAppleEnum;
        if (entry.IsFlags)
            flags |= TypeRecordFlags.OptionSet;

        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(entry.Namespace, entry.Name),
            SwiftTypeName = swiftTypeName,
            MetadataAccessor = string.Empty,
            Flags = flags,
            Kind = TypeRecordKind.Enum,
            RawValueTypeName = entry.EnumUnderlyingType ?? "Int",
        };
    }

    /// <summary>Projects an ObjC class using its real Microsoft.iOS name and namespace.</summary>
    private static TypeRecord CreateCorrectedObjCClassRecord(SwiftTypeName swiftTypeName, AppleTypeSurfaceEntry entry)
        => new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(entry.Namespace, entry.Name),
            SwiftTypeName = swiftTypeName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        };

    /// <summary>
    /// Builds a record that still looks like a synthesized bridged class (so downstream code that
    /// only reads ObjCBridged records behaves unchanged) but carries
    /// <see cref="TypeRecordFlags.AbsentAppleProjection"/> so member validation skips any reference
    /// to it instead of emitting a dangling type.
    /// </summary>
    private static TypeRecord CreateAbsentAppleRecord(SwiftTypeName swiftTypeName, string ns, string name)
        => new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ns, name),
            SwiftTypeName = swiftTypeName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement
                | TypeRecordFlags.AbsentAppleProjection,
            Kind = TypeRecordKind.Class,
        };

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
    ///
    /// This is the BROAD classification used by the marshalling pipeline
    /// (ObjCBridgingStrategy synthetic-record creation, namespace remap, foreign-type
    /// extension routing). For the EXISTENTIAL-FILTERING path use
    /// <see cref="IsObjCExistentialBridgedProtocol"/> instead — it gates by the module's
    /// declared <c>objcPrefixes</c> so Swift-only protocols whose names don't match the
    /// module prefix (e.g. <c>RealityKit.SynchronizationPeerID</c> from
    /// <c>RealityFoundation</c>'s umbrella collapse) survive
    /// <see cref="ExistentialHandler.GetEffectiveProtocols"/> and don't trip the
    /// mixed-composition parity guards.
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
    /// Narrow per-module ObjC-prefix gate for protocol-composition existential filtering.
    /// Returns true only when the protocol's bare name actually matches one of the module's
    /// declared <c>objcPrefixes</c> in <c>apple-frameworks.json</c>. Swift-only protocols
    /// whose names don't match (e.g. <c>RealityFoundation.SynchronizationPeerID</c>
    /// collapsed onto <c>RealityKit</c>'s umbrella, where the module declares only
    /// <c>"RE"</c>) return false so they survive
    /// <see cref="ExistentialHandler.GetEffectiveProtocols"/> and the mixed-composition
    /// parity guards instead of being silently dropped — which would force
    /// <see cref="ExistentialHandler.GetPublicExistentialType"/> to <c>"object"</c> and
    /// trip <c>B6 UnsupportedExistential</c>. Use this ONLY at existential-filter and
    /// parity-guard sites; broader marshalling decisions stay on <see cref="IsObjCModuleType"/>
    /// so we don't regress synthetic-record creation for umbrella-collapsed real ObjC types.
    /// </summary>
    internal static bool IsObjCExistentialBridgedProtocol(NamedTypeSpec typeSpec)
    {
        if (!typeSpec.HasModule())
            return false;

        // ObjectiveC/Foundation root classes show up as protocol participants in some
        // class-bounded compositions; keep them ObjC-classified for parity with the
        // broad path so the parity guard still catches classic ObjC-rooted shapes.
        if ((typeSpec.Module == ObjCModuleName || typeSpec.Module == "Foundation")
            && AppleFrameworkRegistry.IsKnownObjCRootClass(typeSpec.NameWithoutModule))
            return true;

        return AppleFrameworkRegistry.IsObjCBridgedTypeName(typeSpec.Module, typeSpec.Name);
    }

    /// <summary>
    /// Determines whether the specified NamedTypeSpec is from an Apple framework module
    /// that has no .NET iOS binding equivalent (SwiftUI, XCTest, Combine, etc.).
    /// </summary>
    internal static bool IsUnsupportedAppleModule(NamedTypeSpec typeSpec)
    {
        if (!typeSpec.HasModule())
            return false;
        return AppleFrameworkRegistry.IsUnsupportedModule(typeSpec.Module);
    }

    /// <summary>
    /// Determines whether the specified SwiftTypeName represents an ObjC class type.
    /// Mirrors <see cref="IsObjCModuleType"/> but for the SwiftTypeName path.
    /// </summary>
    internal static bool IsObjCClassSwiftType(SwiftTypeName swiftTypeName)
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
        "Dictionary", "Array", "Set", "Optional", "Result", "ClosedRange",
        "Swift.Dictionary", "Swift.Array", "Swift.Set", "Swift.Optional", "Swift.Result", "Swift.ClosedRange"
    };

    internal static bool IsKnownGenericType(string name) => KnownGenericTypes.Contains(name);

    // NamedTypeSpec-typed convenience over the pointer name-set. The set itself lives once in
    // AppleFrameworkRegistry.IsPointerType — the documented single source of truth for pointer/
    // nested-type detection (constraints.md). This delegates so the BoundGenericsHandler /
    // ClosureHandler / ClosureEmitter copies that used to re-list the six names stop drifting.
    // Nullable so it subsumes ClosureHandler's old `NamedTypeSpec?` predicate (null => false).
    internal static bool IsPointerType(NamedTypeSpec? typeSpec)
    {
        return typeSpec is not null && AppleFrameworkRegistry.IsPointerType(typeSpec.Name);
    }

    /// <summary>
    /// Determines whether the specified NamedTypeSpec represents an existential type.
    /// Existential types come through as NamedTypeSpec with names like "any" or "any SomeProtocol"
    /// when parsing tuple elements or enum associated values containing existential types.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if this is an existential type name; otherwise, <c>false</c>.</returns>
    internal static bool IsExistentialTypeName(NamedTypeSpec typeSpec)
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
