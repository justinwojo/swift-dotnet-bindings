// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Helper class for handling existential types (protocol types and protocol compositions) in Swift bindings.
/// Existential types in Swift are represented using existential containers that hold the value,
/// type metadata, and protocol witness tables.
/// </summary>
public class ExistentialHandler
{
    private readonly ITypeDatabase _typeDatabase;
    private SortedDictionary<string, List<string>>? _compositionCollector;

    /// <summary>
    /// Maximum number of protocol witness tables supported.
    /// This corresponds to ExistentialContainer1 through ExistentialContainer8.
    /// </summary>
    public const int MaxSupportedWitnessTables = 8;

    /// <summary>
    /// The name of the module currently being generated. When set, cross-module
    /// protocol references are qualified with the protocol's module namespace.
    /// </summary>
    public string? CurrentModuleName { get; set; }

    /// <summary>
    /// Optional concrete specialization engine for discovering known conformers
    /// of PAT protocols. When set, existentials for PAT protocols with finite
    /// known conformers use ExistentialUnion (try-cast) instead of falling back to object.
    /// </summary>
    public ConcreteSpecializationEngine? SpecializationEngine { get; set; }

    public ExistentialHandler(ITypeDatabase typeDatabase, SortedDictionary<string, List<string>>? compositionCollector = null)
    {
        _typeDatabase = typeDatabase;
        _compositionCollector = compositionCollector;
    }

    /// <summary>
    /// Sets the composition collector on this handler for late injection.
    /// </summary>
    /// <remarks>
    /// IHandler.Marshal() creates environments (and their ExistentialHandler) before TypeHandlerContext
    /// is available, so the collector is null at construction. IHandler.Emit() receives the context and
    /// injects the collector here. We mutate the existing handler rather than recreating the environment
    /// because downstream code (SignatureHandler, WrapperEmitter) already holds references to this instance.
    /// </remarks>
    public void SetCompositionCollector(SortedDictionary<string, List<string>> collector)
    {
        _compositionCollector = collector;
    }

    /// <summary>
    /// Returns true if the protocol is a marker protocol (no witness table, no C# representation).
    /// Marker protocols: Sendable, Escapable, Copyable, SendableMetatype.
    /// </summary>
    public static bool IsMarkerProtocol(NamedTypeSpec protocol)
    {
        var simpleName = protocol.NameWithoutModule;
        return simpleName is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype";
    }

    /// <summary>
    /// Returns the non-marker protocols in a composition — excluding only marker protocols.
    /// Used for ABI-sensitive logic (EC container type, container size) where ObjC protocols
    /// DO contribute witness tables. Only markers are excluded (they have no witness tables).
    /// </summary>
    public static IReadOnlyList<NamedTypeSpec> GetNonMarkerProtocols(ProtocolListTypeSpec protocolList)
    {
        return protocolList.Protocols.Keys
            .Where(p => !IsMarkerProtocol(p))
            .ToList();
    }

    /// <summary>
    /// Returns the effective protocols in a composition — excluding both marker protocols and ObjC module types.
    /// Used for public API naming (proxy classes, interface names) where ObjC module types have no
    /// emitted interfaces and markers have no C# representation. NOT for ABI/container size computation.
    /// </summary>
    public static IReadOnlyList<NamedTypeSpec> GetEffectiveProtocols(ProtocolListTypeSpec protocolList)
    {
        return protocolList.Protocols.Keys
            .Where(p => !IsMarkerProtocol(p) && !TypeDatabaseExtensions.IsObjCModuleType(p))
            .ToList();
    }

    /// <summary>
    /// Returns true when the composition includes at least one non-protocol participant
    /// (e.g., a class, struct, or enum). Swift permits class-constrained existentials like
    /// <c>ClassA &amp; ProtoP</c>, but C# has no <c>I{ClassA}</c> interface and the ABI
    /// container is a class-bounded existential with a different layout than a regular
    /// composition. We flag these so that <see cref="GetPublicExistentialType"/> collapses
    /// them to <c>object</c> instead of synthesising a broken <c>I...And...</c> interface.
    /// Iterates the RAW protocol list (not <see cref="GetEffectiveProtocols"/>) because
    /// that helper strips ObjC-module participants up front, which would hide exactly
    /// the class-bounded shape (e.g., <c>Foundation.NSObject &amp; SomeProtocol</c>) we
    /// are trying to catch here.
    /// </summary>
    public bool CompositionHasNonProtocolParticipant(ProtocolListTypeSpec protocolList)
    {
        foreach (var p in protocolList.Protocols.Keys)
        {
            if (IsMarkerProtocol(p))
                continue;

            // Swift-side: resolved TypeRecord with a non-protocol kind.
            try
            {
                var swiftTypeName = SwiftTypeName.FromTypeSpec(p);
                if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                {
                    if (typeRecord.Kind == TypeRecordKind.Class ||
                        typeRecord.Kind == TypeRecordKind.Struct ||
                        typeRecord.Kind == TypeRecordKind.Enum)
                    {
                        return true;
                    }
                    // A protocol TypeRecord is unambiguously a protocol participant.
                    if (typeRecord.Kind == TypeRecordKind.Protocol)
                        continue;
                }
            }
            catch
            {
                // FromTypeSpec may throw for malformed names — fall through to the
                // ObjC root-class heuristic below before giving up.
            }

            // Auto-bridged ObjC root classes: NSObject/NSProxy are the only canonical
            // ObjC class roots we can identify purely from the type name. Anything
            // else in Foundation/ObjectiveC could be either a class or a protocol
            // (NSCoding, NSCopying, etc.), so we do NOT treat generic "ObjC module
            // type" as class-bounded — only the narrow root-class set.
            if (p.HasModule() &&
                (p.Module == "Foundation" || p.Module == "ObjectiveC") &&
                AppleFrameworkRegistry.IsKnownObjCRootClass(p.NameWithoutModule))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Determines whether the specified argument declaration represents an existential type
    /// (a protocol type or protocol composition).
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns><c>true</c> if the argument's Swift type is an existential; otherwise, <c>false</c>.</returns>
    public bool IsExistential(ArgumentDecl argumentDecl) =>
        argumentDecl.SwiftTypeSpec is ProtocolListTypeSpec;

    /// <summary>
    /// Determines whether the specified property declaration represents an existential type.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns><c>true</c> if the property's Swift type is an existential; otherwise, <c>false</c>.</returns>
    public bool IsExistential(PropertyDecl propertyDecl) =>
        propertyDecl.SwiftTypeSpec is ProtocolListTypeSpec;

    /// <summary>
    /// Determines whether the specified type spec represents an existential type.
    /// This includes both protocol compositions (ProtocolListTypeSpec) and single-protocol
    /// existentials (NamedTypeSpec with IsAny = true).
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if the type spec is an existential; otherwise, <c>false</c>.</returns>
    public bool IsExistential(TypeSpec typeSpec) =>
        typeSpec is ProtocolListTypeSpec ||
        (typeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.IsAny);

    /// <summary>
    /// Gets the ProtocolListTypeSpec from an argument declaration.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns>The ProtocolListTypeSpec if the argument is an existential; otherwise, null.</returns>
    public ProtocolListTypeSpec? GetProtocolListTypeSpec(ArgumentDecl argumentDecl) =>
        argumentDecl.SwiftTypeSpec as ProtocolListTypeSpec;

    /// <summary>
    /// Gets the ProtocolListTypeSpec from a property declaration.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns>The ProtocolListTypeSpec if the property is an existential; otherwise, null.</returns>
    public ProtocolListTypeSpec? GetProtocolListTypeSpec(PropertyDecl propertyDecl) =>
        propertyDecl.SwiftTypeSpec as ProtocolListTypeSpec;

    /// <summary>
    /// Converts a type spec to a ProtocolListTypeSpec if it represents an existential.
    /// For ProtocolListTypeSpec, returns as-is.
    /// For NamedTypeSpec with IsAny=true (single protocol existential), creates a ProtocolListTypeSpec with one protocol.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns>A ProtocolListTypeSpec representing the existential, or null if not an existential.</returns>
    public ProtocolListTypeSpec? ToProtocolListTypeSpec(TypeSpec typeSpec)
    {
        if (typeSpec is ProtocolListTypeSpec protocolList)
            return protocolList;

        if (typeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.IsAny)
        {
            // Single protocol existential: "any SomeProtocol" → ProtocolListTypeSpec with one protocol
            return new ProtocolListTypeSpec(new[] { namedTypeSpec });
        }

        return null;
    }

    /// <summary>
    /// Gets the number of protocols in an existential type.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The number of protocols.</returns>
    public int GetProtocolCount(ProtocolListTypeSpec protocolList) =>
        protocolList.Protocols.Count;

    /// <summary>
    /// Determines whether the existential type is the special "Any" type (zero protocols).
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns><c>true</c> if this is the "Any" type; otherwise, <c>false</c>.</returns>
    public bool IsAnyType(ProtocolListTypeSpec protocolList) =>
        protocolList.Protocols.Count == 0;

    /// <summary>
    /// Returns true if the protocol list represents bare 'Any' (0 protocols in the original list).
    /// Only matches literal Swift 'Any' — NOT pure-marker compositions like 'any Sendable'
    /// (which also have 0 effective protocols after marker filtering, but are semantically distinct).
    /// Bare Any is intentionally supported for container elements (e.g., Dictionary&lt;String, Any&gt;).
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns><c>true</c> if this is bare Any; otherwise, <c>false</c>.</returns>
    public bool IsBareAny(ProtocolListTypeSpec protocolList) =>
        protocolList.Protocols.Count == 0;

    /// <summary>
    /// Determines whether the existential type is a supported type.
    /// Currently supports:
    /// - Protocol compositions with 0-8 protocols (Any through 8-protocol compositions)
    /// - Only protocols without associated types (PATs are not fully supported)
    /// - Pure protocol compositions (no class-bounded participants — see
    ///   <see cref="CompositionHasNonProtocolParticipant"/>). Class-bounded compositions
    ///   use a different ABI container layout and would need their own marshalling
    ///   path; they are skipped entirely so callers can't try to box a concrete class
    ///   through the regular ExistentialContainerN route.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns><c>true</c> if the existential is supported; otherwise, <c>false</c>.</returns>
    public bool IsSupportedExistential(ProtocolListTypeSpec protocolList)
    {
        // Check witness table count limit
        if (protocolList.Protocols.Count > MaxSupportedWitnessTables)
            return false;

        // Class-bounded compositions (e.g. `any ClassA & ProtoP`, `any NSObject & SomeProtocol`)
        // box through a class-bounded existential container with a different layout than
        // the regular ExistentialContainerN shape. We have no marshalling path for them,
        // and degrading the public parameter to `object` still leaves the emitted body
        // casting to `ISwiftExistentialConvertible<ExistentialContainer2>` — which the
        // concrete class does not implement and which throws at the first real call.
        // Reject the whole member instead.
        if (CompositionHasNonProtocolParticipant(protocolList))
            return false;

        // All protocols in the composition must be known
        foreach (var protocol in protocolList.Protocols.Keys)
        {
            // For now, we allow any protocol since we can't easily determine
            // if it has associated types from the type spec alone.
            // The runtime will handle the actual conformance checking.
        }

        return true;
    }

    /// <summary>
    /// Gets the appropriate C# existential container type for the given protocol list.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The C# existential container type name.</returns>
    public string GetCSharpExistentialType(ProtocolListTypeSpec protocolList)
    {
        var count = GetNonMarkerProtocols(protocolList).Count;
        return $"Swift.Runtime.ExistentialContainer{count}";
    }

    /// <summary>
    /// Gets the P/Invoke type for an existential container.
    /// Uses the appropriate ExistentialContainer struct.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The P/Invoke type name.</returns>
    public string GetPInvokeExistentialType(ProtocolListTypeSpec protocolList)
    {
        // For P/Invoke, we use the same ExistentialContainer type
        return GetCSharpExistentialType(protocolList);
    }

    /// <summary>
    /// Gets the size of the existential container in machine words (8 bytes each on 64-bit).
    /// Layout: 3 payload words + 1 metadata word + N witness table words
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The size in machine words.</returns>
    public int GetExistentialContainerSizeInWords(ProtocolListTypeSpec protocolList)
    {
        // 3 words for payload + 1 word for metadata + N words for witness tables
        // Marker protocols have no witness tables; ObjC protocols DO have witness tables.
        return 4 + GetNonMarkerProtocols(protocolList).Count;
    }

    /// <summary>
    /// Gets the size of the existential container in bytes (64-bit platform).
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The size in bytes.</returns>
    public int GetExistentialContainerSizeInBytes(ProtocolListTypeSpec protocolList)
    {
        return GetExistentialContainerSizeInWords(protocolList) * 8;
    }

    /// <summary>
    /// Gets a human-readable description of the existential type for diagnostics.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>A description like "any SomeProtocol" or "any P1 & P2".</returns>
    public string GetExistentialDescription(ProtocolListTypeSpec protocolList)
    {
        if (protocolList.Protocols.Count == 0)
            return "Any";

        var protocolNames = protocolList.Protocols.Keys.Select(p => p.NameWithoutModule);
        return $"any {string.Join(" & ", protocolNames)}";
    }

    /// <summary>
    /// Gets the list of protocol names from an existential type (used for interface generation).
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>A list of protocol names.</returns>
    public IReadOnlyList<string> GetProtocolNames(ProtocolListTypeSpec protocolList)
    {
        return protocolList.Protocols.Keys.Select(p => p.Name).ToList();
    }

    /// <summary>
    /// Gets the list of protocol type specs from an existential type.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>A list of protocol type specifications.</returns>
    public IReadOnlyList<NamedTypeSpec> GetProtocols(ProtocolListTypeSpec protocolList)
    {
        return protocolList.Protocols.Keys.ToList();
    }

    /// <summary>
    /// Determines whether the specified type spec represents an Optional-wrapped existential type.
    /// This is for types like (any DataCaching)? which are Swift.Optional with an existential generic parameter.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if the type spec is an Optional containing an existential; otherwise, <c>false</c>.</returns>
    public bool IsOptionalExistential(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        // Check if it's Swift.Optional with exactly one generic parameter
        if (namedTypeSpec.Name != "Swift.Optional" || namedTypeSpec.GenericParameters.Count != 1)
            return false;

        // Check if the generic parameter is an existential
        var innerType = namedTypeSpec.GenericParameters[0];
        return IsExistential(innerType);
    }

    /// <summary>
    /// Extracts the inner existential type from an Optional-wrapped existential.
    /// </summary>
    /// <param name="typeSpec">The type specification (must be an Optional-wrapped existential).</param>
    /// <returns>The inner existential type as a ProtocolListTypeSpec, or null if not an Optional-wrapped existential.</returns>
    public ProtocolListTypeSpec? UnwrapOptionalExistential(TypeSpec typeSpec)
    {
        if (!IsOptionalExistential(typeSpec))
            return null;

        var namedTypeSpec = (NamedTypeSpec)typeSpec;
        var innerType = namedTypeSpec.GenericParameters[0];
        return ToProtocolListTypeSpec(innerType);
    }

    /// <summary>
    /// Gets the appropriate C# type for an Optional-wrapped existential.
    /// Returns a nullable existential container type (e.g., "Swift.Runtime.ExistentialContainer1?").
    /// </summary>
    /// <param name="protocolList">The protocol list type specification from the inner existential.</param>
    /// <returns>The C# nullable existential container type name.</returns>
    public string GetCSharpOptionalExistentialType(ProtocolListTypeSpec protocolList)
    {
        return $"{GetCSharpExistentialType(protocolList)}?";
    }

    /// <summary>
    /// Checks whether a protocol composition maps to a well-known runtime type
    /// (e.g., 'any Swift.Error' → Swift.Foundation.AnyError). Extensible for future stdlib protocols.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <param name="csharpType">The fully-qualified C# type name if this is a well-known protocol.</param>
    /// <returns><c>true</c> if this is a well-known protocol with a direct runtime type mapping.</returns>
    public bool TryGetWellKnownProtocolType(ProtocolListTypeSpec protocolList, out string csharpType)
    {
        csharpType = "";
        if (protocolList.Protocols.Count != 1)
            return false;

        var protocol = protocolList.Protocols.Keys.First();
        var swiftName = protocol.Name; // e.g., "Swift.Error"

        if (swiftName == "Swift.Error")
        {
            // AnyError is hand-rolled in SwiftBindings.Apple; record the reference so the
            // consumer csproj adds the supplement PackageReference.
            AppleSupplementReferences.Record("Foundation.AnyError");
            csharpType = "Swift.Foundation.AnyError";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the protocol interface name for public API (e.g., "IDescribable").
    /// For multi-protocol compositions, returns a combined interface name.
    /// Well-known stdlib protocols (e.g., Swift.Error) return their direct runtime types.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The public-facing interface type name.</returns>
    public string GetPublicExistentialType(ProtocolListTypeSpec protocolList)
    {
        // Class-constrained compositions (e.g. `any ClassA & ProtoP`) have no C# API
        // representation — the ABI container is a class-bounded existential with a
        // different layout than a regular composition, and there is no I{ClassName}
        // interface for the class side. Collapse to object so callers skip the member
        // or fall back to the raw container instead of synthesising a broken interface.
        if (CompositionHasNonProtocolParticipant(protocolList))
            return "object";

        // Filter markers and ObjC before dispatching on count.
        // Marker protocols (Sendable, Escapable, etc.) have no C# representation;
        // ObjC module types have no emitted interfaces.
        var effective = GetEffectiveProtocols(protocolList);

        if (effective.Count == 0)
            return "object"; // 'Any', pure-marker (e.g., 'any Sendable'), or pure-ObjC → object

        if (effective.Count == 1)
        {
            var firstProtocol = effective[0];

            // Well-known stdlib protocols → direct runtime type (no proxy needed)
            if (firstProtocol.Name == "Swift.Error")
            {
                AppleSupplementReferences.Record("Foundation.AnyError");
                return "Swift.Foundation.AnyError";
            }

            // Validate that the protocol has a TypeRecord in the database with Kind=Protocol.
            // This handles multiple cases:
            //   - Metatype expressions (e.g., "Any.Type") misclassified as protocols → no TypeRecord → object
            //   - Real protocols with emitted interfaces → TypeRecord with Kind=Protocol → I{Name}
            //   - PAT / Self-requirement protocols → emitted as generic interface I{Name}<TSelf>,
            //     which can't be referenced without type arguments. Fall back to object so call
            //     sites don't emit CS0305 references like `IReadOnlyList<ITip>` or `ITip? Foo_Get()`.
            try
            {
                var swiftTypeName = SwiftTypeName.FromTypeSpec(firstProtocol);
                if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord) ||
                    typeRecord.Kind != TypeRecordKind.Protocol)
                {
                    return "object";
                }
                if (typeRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement) ||
                    typeRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
                {
                    // PAT protocol with known conformers → ExistentialUnion (try-cast pattern)
                    // instead of falling back to object which makes the member unusable.
                    if (SpecializationEngine != null)
                    {
                        var conformers = SpecializationEngine.GetConformers(swiftTypeName);
                        if (conformers.Count > 0)
                            return "Swift.Runtime.ExistentialUnion";
                    }
                    return "object";
                }
            }
            catch
            {
                // FromTypeSpec/FromModuleQualifiedName may throw for malformed names
                return "object";
            }

            // Generic protocol existentials (e.g., "any EventStream<τ_0_0.Event>")
            // have associated type refs we can't resolve to concrete C# types.
            // Use AnyType to preserve API surface (not "object" which triggers member pruning).
            if (firstProtocol.GenericParameters.Count > 0)
                return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;

            var firstProtocolTypeName = SwiftTypeName.FromTypeSpec(firstProtocol);
            var emissionModule = ProtocolConformanceHelper.ResolveProtocolEmissionModule(firstProtocolTypeName, _typeDatabase);
            var interfaceName = NameProvider.GetInterfaceName(firstProtocol.NameWithoutModule, moduleName: emissionModule);

            // Cross-module protocol reference: qualify with the resolved emission namespace
            // (umbrella fallback aware) so umbrella-qualified ABI shapes that resolve to a
            // dep module emit `<DepModule>.IProtocol` instead of bare `IProtocol`.
            if (!string.IsNullOrEmpty(CurrentModuleName) &&
                !string.IsNullOrEmpty(emissionModule) &&
                emissionModule != CurrentModuleName &&
                emissionModule != "Swift")
            {
                interfaceName = $"{emissionModule}.{interfaceName}";
            }

            return interfaceName;
        }

        // Multi-protocol: generate combined interface name
        return GetCompositionInterfaceName(protocolList);
    }

    /// <summary>
    /// Returns nullable protocol interface (e.g., "IDescribable?").
    /// </summary>
    /// <param name="protocolList">The protocol list type specification from the inner existential.</param>
    /// <returns>The nullable public-facing interface type name.</returns>
    public string GetPublicOptionalExistentialType(ProtocolListTypeSpec protocolList)
    {
        return $"{GetPublicExistentialType(protocolList)}?";
    }

    /// <summary>
    /// Gets the proxy class name for an existential type (used for container→interface wrapping).
    /// For single protocols: "DescribableProxy". For compositions: "DescribableAndIdentifiableProxy".
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The proxy class name.</returns>
    public string GetProxyClassName(ProtocolListTypeSpec protocolList)
    {
        if (protocolList.Protocols.Count == 1)
        {
            var protocolName = protocolList.Protocols.Keys.First().NameWithoutModule;
            return $"{protocolName}Proxy";
        }

        // Multi-protocol: combined proxy name
        var names = protocolList.Protocols.Keys
            .Select(p => p.NameWithoutModule)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        return string.Join("And", names) + "Proxy";
    }

    /// <summary>
    /// Gets the proxy class name qualified for cross-module use. When CurrentModuleName is set
    /// and the protocol belongs to a different module, returns "OtherModule.SwiftInterop.ProxyName".
    /// Proxy classes live in the {Module}.SwiftInterop namespace, so cross-assembly references
    /// require the full namespace qualification.
    /// </summary>
    public string GetQualifiedProxyClassName(ProtocolListTypeSpec protocolList)
    {
        return QualifyProxyClassName(GetProxyClassName(protocolList), protocolList);
    }

    /// <summary>
    /// Applies cross-module qualification to a proxy class name.
    /// Returns "Module.SwiftInterop.ProxyName" when the protocol belongs to a different module.
    /// Used by both GetQualifiedProxyClassName and TryGetFilteredProxyClassName callers that
    /// need cross-module qualification on an already-computed (ObjC-filtered) name.
    /// </summary>
    public string QualifyProxyClassName(string proxyClassName, ProtocolListTypeSpec protocolList)
    {
        if (string.IsNullOrEmpty(CurrentModuleName))
            return proxyClassName;

        var protocolModule = protocolList.Protocols.Keys
            .Where(p => !TypeDatabaseExtensions.IsObjCModuleType(p))
            .Select(p => p.Module)
            .FirstOrDefault(m => !string.IsNullOrEmpty(m));

        if (protocolModule == null || protocolModule == CurrentModuleName || protocolModule == "Swift")
            return proxyClassName;

        return $"{protocolModule}.SwiftInterop.{proxyClassName}";
    }

    /// <summary>
    /// Returns true if the TypeSpec represents a constrained existential — a protocol type with
    /// concrete generic arguments (e.g., any CameraFrameAnalyzer&lt;CameraFrame, UIEvent&gt;).
    /// Handles both ProtocolListTypeSpec (from protocol composition) and NamedTypeSpec (from ABI JSON
    /// where constrained existentials are parsed as NamedTypeSpec with generic params via printedName).
    /// Does NOT gate on ClassBound — see ConstrainedExistentialBridge for safety constraints.
    /// </summary>
    public static bool IsConstrainedExistential(TypeSpec? typeSpec, ITypeDatabase typeDatabase)
    {
        NamedTypeSpec? protocolSpec = null;

        if (typeSpec is ProtocolListTypeSpec protocolList && protocolList.Protocols.Count == 1)
        {
            protocolSpec = protocolList.Protocols.Keys[0];
        }
        else if (typeSpec is NamedTypeSpec named && named.GenericParameters.Count > 0)
        {
            protocolSpec = named;
        }

        if (protocolSpec == null || protocolSpec.GenericParameters.Count == 0)
            return false;

        // All generic args must be concrete (not τ_0_0 style generic params)
        if (!protocolSpec.GenericParameters.All(gp =>
            gp is NamedTypeSpec n && !TypeSpecHelpers.IsGenericTypeParameter(n.Name)))
            return false;

        // Must be a protocol type
        try
        {
            var swiftTypeName = SwiftTypeName.FromTypeSpec(protocolSpec);
            if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord) ||
                typeRecord.Kind != TypeRecordKind.Protocol)
                return false;
        }
        catch
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Checks whether ALL protocols in a composition have TypeRecords with Kind == Protocol.
    /// Returns false if any protocol is unknown/unregistered or not a Protocol kind.
    /// </summary>
    public bool AllProtocolsHaveTypeRecords(ProtocolListTypeSpec protocolList)
    {
        if (protocolList.Protocols.Count == 0)
            return false; // 'Any' (no protocols) → false

        var nonMarker = GetNonMarkerProtocols(protocolList);
        if (nonMarker.Count == 0)
            return true; // Pure-marker (e.g., 'any Sendable') → vacuously true

        foreach (var protocol in nonMarker)
        {
            try
            {
                var swiftTypeName = SwiftTypeName.FromTypeSpec(protocol);
                if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord) ||
                    typeRecord.Kind != TypeRecordKind.Protocol)
                    return false;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Tries to get the proxy class name using the same ObjC-filtered protocol set
    /// as GetCompositionInterfaceName. Returns false if no non-ObjC protocols remain
    /// (e.g., `any NSObjectProtocol` — the proxy class doesn't exist).
    /// </summary>
    public bool TryGetFilteredProxyClassName(ProtocolListTypeSpec protocolList, out string proxyClassName)
    {
        proxyClassName = "";
        var protocols = GetEffectiveProtocols(protocolList)
            .OrderBy(p => p.NameWithoutModule, StringComparer.Ordinal)
            .ToList();
        if (protocols.Count == 0) return false;
        if (protocols.Count == 1) { proxyClassName = $"{protocols[0].NameWithoutModule}Proxy"; return true; }
        proxyClassName = string.Join("And", protocols.Select(p => p.NameWithoutModule)) + "Proxy";
        return true;
    }

    /// <summary>
    /// Gets the combined interface name for a multi-protocol composition.
    /// Protocol names are sorted alphabetically for determinism.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The combined interface name (e.g., "IDescribableAndTestIdentifiable").</returns>
    public string GetCompositionInterfaceName(ProtocolListTypeSpec protocolList)
    {
        // Filter out ObjC module types (no emitted interfaces) and marker protocols (no C# representation).
        var protocols = GetEffectiveProtocols(protocolList)
            .OrderBy(p => p.NameWithoutModule, StringComparer.Ordinal)
            .ToList();

        // If filtering leaves only 1 protocol, return its interface name directly
        if (protocols.Count == 1)
        {
            var firstProtocolTypeName = SwiftTypeName.FromTypeSpec(protocols[0]);
            var emissionModule = ProtocolConformanceHelper.ResolveProtocolEmissionModule(firstProtocolTypeName, _typeDatabase);
            var interfaceName = NameProvider.GetInterfaceName(protocols[0].NameWithoutModule, moduleName: emissionModule);

            // Cross-module protocol reference: qualify with the resolved emission namespace.
            // Same logic as GetPublicExistentialType single-protocol path.
            if (!string.IsNullOrEmpty(CurrentModuleName) &&
                !string.IsNullOrEmpty(emissionModule) &&
                emissionModule != CurrentModuleName &&
                emissionModule != "Swift")
            {
                interfaceName = $"{emissionModule}.{interfaceName}";
            }

            return interfaceName;
        }

        // If all protocols were filtered out, return object
        if (protocols.Count == 0)
        {
            return "object";
        }

        var names = protocols.Select(p => p.NameWithoutModule).ToList();
        var compositionName = "I" + string.Join("And", names);

        // Collect for later emission via the per-conductor scoped collector. Use the
        // resolved emission namespace so umbrella-qualified parents pick up the
        // cross-module qualification (matches the single-protocol path above).
        var parentInterfaces = protocols.Select(p =>
        {
            var pTypeName = SwiftTypeName.FromTypeSpec(p);
            var emissionModule = ProtocolConformanceHelper.ResolveProtocolEmissionModule(pTypeName, _typeDatabase);
            var raw = NameProvider.GetInterfaceName(p.NameWithoutModule, moduleName: emissionModule);
            if (!string.IsNullOrEmpty(CurrentModuleName) &&
                !string.IsNullOrEmpty(emissionModule) &&
                emissionModule != CurrentModuleName &&
                emissionModule != "Swift")
            {
                raw = $"{emissionModule}.{raw}";
            }
            return raw;
        }).ToList();
        _compositionCollector?.TryAdd(compositionName, parentInterfaces);

        return compositionName;
    }
}
