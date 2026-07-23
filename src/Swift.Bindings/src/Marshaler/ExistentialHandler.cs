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
    /// True when <paramref name="containerType"/> names an opaque existential container that an
    /// owned-return proxy ADOPTS at +1 — <c>ExistentialContainer1</c> (single-protocol <c>any P</c>)
    /// through <c>ExistentialContainer8</c> (composition <c>any A &amp; B &amp; …</c>). The proxy then
    /// releases the container's value-witness retains on Dispose/finalize through the existential's
    /// own metadata, which destroys exactly the one conforming value the container holds regardless
    /// of how many witness-table words precede it (extra protocols add witness tables, not payloads).
    /// <c>ExistentialContainer0</c> (bare <c>Any</c>) is a value type with no proxy ownership and is
    /// excluded; the well-known <c>AnyError</c> reference type carries its own self-owning release and
    /// likewise does not flow through this proxy predicate.
    /// <para>
    /// Gated on the container TYPE, not the declared protocol count: ObjC filtering can drop protocols,
    /// so a protocol-list count diverges from the emitted EC width (see the mixed-composition guard in
    /// constraints), but the container type string is authoritative for which proxy ctor was emitted.
    /// </para>
    /// </summary>
    public static bool IsOwnedExistentialContainerType(string? containerType)
    {
        if (string.IsNullOrEmpty(containerType))
            return false;
        const string marker = "ExistentialContainer";
        var idx = containerType.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return false;
        var suffix = containerType.Substring(idx + marker.Length);
        // EC1..EC8 own a payload; EC0 (n == 0), non-numeric suffixes (e.g. "Heap"), and any
        // n past MaxSupportedWitnessTables (no runtime metadata case, no emitted proxy ctor)
        // are excluded — keep the shared ownership gate fail-closed.
        return int.TryParse(suffix, out var n) && n >= 1 && n <= MaxSupportedWitnessTables;
    }

    /// <summary>
    /// The owned-transfer constructor argument (<c>, ownsContainer: true</c>) for a well-known
    /// existential wrapper that adopts a Swift-returned value at +1, or empty. <c>Swift.Error</c>
    /// projects to <see cref="Swift.Foundation.AnyError"/>, a self-owning reference type that
    /// releases the adopted boxed error on Dispose/finalize. Emitted at every Swift→C# owned
    /// transfer (method/property returns and enum-payload extractions); borrowed closure
    /// parameters omit it so they release nothing. Empty for any other well-known type, which has
    /// no ownership-aware constructor.
    /// </summary>
    public static string WellKnownOwnedTransferArg(string? wellKnownType) =>
        wellKnownType == "Swift.Foundation.AnyError" ? ", ownsContainer: true" : string.Empty;

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
            .Where(p => !IsMarkerProtocol(p) && !TypeDatabaseExtensions.IsObjCExistentialBridgedProtocol(p))
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
    /// Returns true when the existential's ABI container is the zero-witness-table
    /// <see cref="Swift.Runtime.ExistentialContainer0"/> — i.e. bare <c>Any</c> (no protocols) OR a
    /// marker-only composition (<c>any Sendable</c>, <c>any Sendable &amp; Copyable</c>) whose protocols
    /// all filter out as markers. Both lower to the SAME ABI: a 5-word opaque existential carrying zero
    /// witness tables, marshalled via <c>ExistentialContainer0.Box</c>/<c>Unbox</c>. This is the
    /// container/projection-relevant notion of "bare Any" — broader than <see cref="IsBareAny"/>, which
    /// matches ONLY the literal zero-protocol <c>Any</c> (the two are distinct in Swift source but
    /// indistinguishable at the existential-container ABI). Mirrors the <see cref="GetCSharpExistentialType"/>
    /// container-arity computation (<c>GetNonMarkerProtocols(...).Count</c> == 0 ⟺ Container0). An
    /// ObjC-only existential is deliberately NOT included: an ObjC protocol contributes a witness table
    /// (Container1), so it is not zero-witness and has no Box/Unbox path.
    /// </summary>
    public static bool IsZeroWitnessExistential(ProtocolListTypeSpec protocolList) =>
        GetNonMarkerProtocols(protocolList).Count == 0;

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
    /// Returns true when the composition is a single class-bound (superclass- or
    /// <c>AnyObject</c>-constrained) protocol. Such existentials use the 2-word
    /// <c>[classRef][witnessTable]</c> class-existential ABI (16-byte stride),
    /// not the 5-word <see cref="OpaqueExistentialContainer"/> opaque layout.
    /// Marshalling them through <c>ExistentialContainer1</c> (40-byte stride) over-reads
    /// array elements and crashes; <c>ClassExistentialContainer1</c> carries the correct
    /// stride. Only arity 1 is handled — multi-protocol class-bound compositions are
    /// rejected upstream by <see cref="CompositionHasNonProtocolParticipant"/>.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns><c>true</c> for a single class-bound protocol existential.</returns>
    public bool IsClassBoundArity1Existential(ProtocolListTypeSpec protocolList)
    {
        var nonMarker = GetNonMarkerProtocols(protocolList);
        if (nonMarker.Count != 1)
            return false;

        try
        {
            var swiftTypeName = SwiftTypeName.FromTypeSpec(nonMarker[0]);
            if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord) &&
                typeRecord.Kind == TypeRecordKind.Protocol)
            {
                // An @objc protocol's existential is a single 8-byte ObjC object pointer with
                // no Swift witness-table word and no `…Mp` descriptor (even when also ClassBound
                // via AnyObject/NSObjectProtocol). The 16-byte ClassExistentialContainer1 carrier
                // would over-size it AND its metadata registration needs the missing descriptor,
                // so route @objc existentials through the descriptor-free opaque container path.
                if ((typeRecord.Flags & TypeRecordFlags.ObjCProtocol) != 0)
                    return false;
                return (typeRecord.Flags & TypeRecordFlags.ClassBound) != 0;
            }
        }
        catch
        {
            // Unresolvable type name — treat as non-class-bound and fall through to
            // the regular opaque container path.
        }

        return false;
    }

    /// <summary>
    /// Returns true when the existential is a single <c>@objc</c> protocol's existential
    /// (<c>any P</c> where <c>P</c> is declared <c>@objc</c>). Such an existential's ABI is a
    /// single 8-byte Objective-C object pointer — no Swift witness-table word and no <c>…Mp</c>
    /// protocol descriptor, even when class-bound via <c>AnyObject</c>/<c>NSObjectProtocol</c>.
    /// It must marshal as a bare object pointer (<c>IntPtr</c>, nil = <c>IntPtr.Zero</c>,
    /// unknown-object ARC), NOT through any <c>ExistentialContainerN</c> or the 16-byte
    /// <c>ClassExistentialContainer1</c> carrier. Keyed entirely on
    /// <see cref="TypeRecordFlags.ObjCProtocol"/> so the pure-Swift existential corpus is byte-identical.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns><c>true</c> for a single <c>@objc</c> protocol existential.</returns>
    public bool IsObjCProtocolExistential(ProtocolListTypeSpec protocolList)
    {
        var nonMarker = GetNonMarkerProtocols(protocolList);
        return nonMarker.Count == 1 && IsObjCProtocolNamed(nonMarker[0], _typeDatabase);
    }

    /// <summary>
    /// Spec-level form of <see cref="IsObjCProtocolExistential(ProtocolListTypeSpec)"/> for emitter
    /// sites that operate on raw <see cref="TypeSpec"/>s (cdecl param/return mappers, indirect-result
    /// classification, large-optional routing, P/Invoke type selection). Unwraps a single
    /// <c>Optional&lt;…&gt;</c> layer and reports whether the inner shape was optional via
    /// <paramref name="isOptional"/>. Recognises both the <see cref="ProtocolListTypeSpec"/> form
    /// (<c>any P</c> / <c>any P &amp; Sendable</c>) and the single <c>any</c> <see cref="NamedTypeSpec"/> form.
    /// </summary>
    public static bool IsObjCProtocolExistentialSpec(TypeSpec typeSpec, ITypeDatabase typeDatabase, out bool isOptional)
    {
        isOptional = false;
        var spec = typeSpec;
        if (spec is NamedTypeSpec optSpec && optSpec.Name == "Swift.Optional" && optSpec.GenericParameters.Count == 1)
        {
            isOptional = true;
            spec = optSpec.GenericParameters[0];
        }

        if (spec is ProtocolListTypeSpec protocolList)
        {
            var nonMarker = GetNonMarkerProtocols(protocolList);
            return nonMarker.Count == 1 && IsObjCProtocolNamed(nonMarker[0], typeDatabase);
        }
        if (spec is NamedTypeSpec named && named.IsAny && !WrapperValidation.IsMetatypeType(named))
            return IsObjCProtocolNamed(named, typeDatabase);
        return false;
    }

    /// <summary>
    /// Spec-level form that ignores any <c>Optional&lt;…&gt;</c> wrapping.
    /// </summary>
    public static bool IsObjCProtocolExistentialSpec(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => IsObjCProtocolExistentialSpec(typeSpec, typeDatabase, out _);

    /// <summary>
    /// Recursively reports whether an <c>@objc</c>-protocol existential
    /// (<see cref="IsObjCProtocolExistentialSpec(TypeSpec, ITypeDatabase)"/>) appears ANYWHERE in
    /// <paramref name="typeSpec"/> — at the top level, or nested inside an Optional, a container
    /// (Array/Dictionary/Set or any other bound generic), a tuple, or a closure parameter/return.
    /// </summary>
    public static bool ContainsObjCProtocolExistential(TypeSpec? typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec == null)
            return false;

        // A bare `any P` / single `Optional<any P>` here is an @objc existential occurrence.
        if (IsObjCProtocolExistentialSpec(typeSpec, typeDatabase))
            return true;

        switch (typeSpec)
        {
            case NamedTypeSpec named:
                foreach (var genericArg in named.GenericParameters)
                    if (ContainsObjCProtocolExistential(genericArg, typeDatabase))
                        return true;
                return false;
            case ProtocolListTypeSpec protoList:
                // A multi-protocol composition (`any P & Q`) is not a single @objc existential —
                // the top-level check already returned false. Inspect each protocol key defensively.
                foreach (var proto in protoList.Protocols.Keys)
                    if (ContainsObjCProtocolExistential(proto, typeDatabase))
                        return true;
                return false;
            case TupleTypeSpec tuple:
                foreach (var element in tuple.Elements)
                    if (ContainsObjCProtocolExistential(element, typeDatabase))
                        return true;
                return false;
            case ClosureTypeSpec closure:
                if (ContainsObjCProtocolExistential(closure.ReturnType, typeDatabase))
                    return true;
                if (closure.Arguments is TupleTypeSpec argTuple)
                {
                    foreach (var element in argTuple.Elements)
                        if (ContainsObjCProtocolExistential(element, typeDatabase))
                            return true;
                }
                else if (ContainsObjCProtocolExistential(closure.Arguments, typeDatabase))
                {
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Returns true when an <c>@objc</c>-protocol existential appears in a position the
    /// single-object-pointer path does NOT support: nested inside an Optional-of-container, a
    /// container element, a tuple, a closure, or any bound generic. The ONLY supported positions are
    /// a bare <c>any P</c> or a single <c>Optional&lt;any P&gt;</c> at the top of a (sync, non-closure)
    /// parameter/return/property. Everywhere else the carrier path would route the existential through
    /// the 16-byte <c>ClassExistentialContainer1</c>, whose descriptor registration silently fails (an
    /// <c>@objc</c> protocol exports no <c>…Mp</c> descriptor) — so the member must be dropped
    /// (fail-closed).
    /// </summary>
    public static bool HasUnsupportedObjCProtocolExistentialPosition(TypeSpec? typeSpec, ITypeDatabase typeDatabase)
        => typeSpec != null
           && !IsObjCProtocolExistentialSpec(typeSpec, typeDatabase)
           && ContainsObjCProtocolExistential(typeSpec, typeDatabase);

    private static bool IsObjCProtocolNamed(NamedTypeSpec protoSpec, ITypeDatabase typeDatabase)
    {
        try
        {
            var swiftTypeName = SwiftTypeName.FromTypeSpec(protoSpec);
            if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord) &&
                typeRecord.Kind == TypeRecordKind.Protocol)
            {
                return (typeRecord.Flags & TypeRecordFlags.ObjCProtocol) != 0;
            }
        }
        catch
        {
            // Unresolvable type name — not an @objc existential.
        }
        return false;
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
    /// Determines whether a type spec is specifically <c>Optional&lt;any Swift.Error&gt;</c> —
    /// the only existential whose Swift ABI is a single 8-byte boxed reference rather
    /// than a 40-byte 5-word existential container.
    ///
    /// <para>
    /// <c>any Error</c> is class-bound and stored as a single retained pointer to a Swift
    /// error box (<c>swift_allocError</c>). <c>MemoryLayout&lt;(any Error)?&gt;.size == 8</c>
    /// (verified via swiftc), and the function returns the boxed pointer in <c>x0</c> with
    /// <c>nil</c> encoded as <c>0</c> — no <c>@out</c>/sret involved. Every other
    /// <c>any P</c> existential is a 5-word container returned via sret.
    /// </para>
    /// <para>
    /// Used by <see cref="MarshallingHelpers"/> to keep <c>Optional&lt;any Error&gt;</c> on
    /// the direct-IntPtr return path (matching Swift's actual ABI) while leaving
    /// <c>Optional&lt;any P&gt;</c> for arbitrary <c>P</c> on the sret path.
    /// </para>
    /// </summary>
    public bool IsOptionalAnyError(TypeSpec typeSpec)
    {
        var inner = UnwrapOptionalExistential(typeSpec);
        if (inner is null || inner.Protocols.Count != 1)
            return false;
        return inner.Protocols.Keys.First().Name == "Swift.Error";
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
            AppleSupplementReferences.Record("Foundation.AnyError", "ExistentialHandler:AnyError");
            csharpType = "Swift.Foundation.AnyError";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Side-effect-free mirror of the projection factory's <c>proxyClassName != null</c> gate
    /// (<see cref="TypeProjectionFactory"/>): true when a single-protocol existential would project to a
    /// real, emittable proxy interface (<c>I{Name}</c> / <c>IP&lt;X&gt;</c>), false when it degrades to
    /// <c>object</c>. The <c>object</c>-degrade for a single protocol is a protocol-with-associated-types /
    /// Self-requirement (PAT) surface: <c>any P</c> collapses to <c>object</c>, so it carries NO
    /// <c>static __v =&gt; new {Proxy}(__v)</c> wrap fallback for a CONSUME arm to drop — reporting one
    /// would be a false degrade row. A reporting-only walk therefore ANDs this in alongside the suppression
    /// predicate to match <see cref="ExistentialProjection.SuppressedProxyName"/> (whose factory sets
    /// <c>proxyClassName</c> null for the same PAT surface, keeping the projection path silent).
    ///
    /// <para>Stays side-effect-free — it records NOTHING to <c>AppleSupplementReferences</c>, the
    /// <c>ReportCollector</c>, or the <c>CompositionCollector</c> — so a reporting walk may call it without
    /// altering emitted output. Two calls it must AVOID both record the <c>Foundation.AnyError</c> supplement
    /// for <c>Swift.Error</c>: <see cref="GetPublicExistentialType"/> (directly, and via the supplement-
    /// recording <c>TryGetTypeRecord</c> it uses to resolve constrained generic args) and — less obviously —
    /// <see cref="ITypeDatabase.TryGetTypeRecordWithoutSupplement"/> itself, whose resolver cascade includes
    /// <c>SwiftErrorStrategy</c> and thus records <c>Foundation.AnyError</c> when handed <c>Swift.Error</c>.
    /// So the well-known short-circuit below is by NAME (mirroring the factory's
    /// <c>!TryGetWellKnownProtocolType</c> half) and runs BEFORE the TypeRecord probe — never resolving
    /// <c>Swift.Error</c>. A constrained <c>any P&lt;Concrete&gt;</c> (generic args present) binds its
    /// associated types at the use site and projects to <c>IP&lt;X&gt;</c>, so it is treated as a real proxy,
    /// matching the factory's constrained-existential arm; the arg-resolvability sub-check the factory then
    /// runs is deliberately NOT replicated (it would require the supplement-recording resolver), leaving a
    /// constrained-but-unresolvable PAT as the one accepted, side-effect-free-mandated approximation.</para>
    /// </summary>
    public bool ProjectsToProxyInterface(ProtocolListTypeSpec protocolList)
    {
        // Dispatch exactly like GetPublicExistentialType: markers + ObjC filtered, then on count.
        var effective = GetEffectiveProtocols(protocolList);
        if (effective.Count != 1)
            return false; // 0 → object/bareAny; 2+ → composition (marshals via EC2+, no single-proxy fallback)
        var protocol = effective[0];

        // Well-known (Swift.Error → Foundation.AnyError): no proxy. Matched by NAME — the factory's
        // !TryGetWellKnownProtocolType gate — BEFORE any TypeRecord resolve, because resolving Swift.Error
        // (even via TryGetTypeRecordWithoutSupplement's SwiftErrorStrategy cascade arm) records the
        // Foundation.AnyError supplement, which a diagnostic-only walk must never do.
        if (protocol.Name == "Swift.Error")
            return false;

        SwiftTypeName swiftTypeName;
        try
        {
            swiftTypeName = SwiftTypeName.FromTypeSpec(protocol);
        }
        catch
        {
            return false; // malformed name → GetPublicExistentialType degrades to object
        }

        if (!_typeDatabase.TryGetTypeRecordWithoutSupplement(swiftTypeName, out var typeRecord) ||
            typeRecord.Kind != TypeRecordKind.Protocol)
            return false; // no TypeRecord / non-protocol (misclassified metatype) → object

        // PAT / Self-requirement → object, UNLESS constrained `any P<Concrete>` (binds ATs at the use site).
        if ((typeRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement) ||
             typeRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes)) &&
            protocol.GenericParameters.Count == 0)
            return false;

        return true;
    }

    /// <summary>
    /// Returns the protocol interface name for public API (e.g., "IDescribable").
    /// For multi-protocol compositions, returns a combined interface name.
    /// Well-known stdlib protocols (e.g., Swift.Error) return their direct runtime types.
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <param name="allowUnionProjection">
    /// When true, a protocol-with-associated-type / Self-requirement existential that has known
    /// conformers projects to <c>Swift.Runtime.ExistentialUnion</c> (the read-only forward try-cast
    /// wrapper). When false (the default) it degrades to <c>object</c>. This MUST stay false for any
    /// position where C# *sends* the existential to Swift — method/ctor parameters, property setters,
    /// and the input-marshalling path — because <c>ExistentialUnion</c> has no input marshalling: it
    /// is a Swift→C# read-only projection. Only pure-read positions (method/function return values,
    /// get-only property getters, async return wrapping) may pass true. Direction, not engine
    /// presence, gates the union projection — so the engine can be wired onto the env handler
    /// unconditionally without flipping parameters/setters to an unmarshallable type.
    /// </param>
    /// <returns>The public-facing interface type name.</returns>
    public string GetPublicExistentialType(ProtocolListTypeSpec protocolList, bool allowUnionProjection = false)
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
                AppleSupplementReferences.Record("Foundation.AnyError", "ExistentialHandler:CompositionAnyError");
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
                    // Constrained existential — `any P<Concrete>` — binds the associated
                    // types at the use site, so the PAT degradation does NOT apply: the
                    // surface is fully closed-form. Project as `IP<X, Y>` if every
                    // argument resolves through the type database. (Cases 1 and 2 in
                    // Constrained existential: binds associated types at the use site.)
                    if (firstProtocol.GenericParameters.Count > 0 &&
                        TryResolveExistentialGenericArgs(firstProtocol, out var constrainedArgs))
                    {
                        return BuildGenericInterfaceName(firstProtocol, constrainedArgs);
                    }

                    // PAT protocol with known conformers → ExistentialUnion (try-cast pattern)
                    // instead of falling back to object which makes the member unusable. Only in a
                    // pure-read (return) position: ExistentialUnion is a Swift→C# read-only wrapper
                    // with no input marshalling, so parameters/setters (allowUnionProjection == false)
                    // must keep degrading to object.
                    if (allowUnionProjection && SpecializationEngine != null)
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

            // Generic protocol existentials (e.g., "any EventStream<UIEvent>",
            // "any AsyncSequence<SampleBuffer>"). When every generic argument is concrete
            // (no τ_n_m placeholders) we can preserve the strongly-typed surface as
            // IProtocol<X, Y>. When any argument is an unresolved generic parameter or
            // an associated-type reference, fall back to AnyType — that preserves the
            // API surface without synthesising broken closed-form C# (the previous
            // 0.10.0 behaviour collapsed every generic existential to AnyType, see
            // Constrained existential Cases 1 and 2: concrete-arg `any P<X>` and plain `any P`).
            if (firstProtocol.GenericParameters.Count > 0)
            {
                if (TryResolveExistentialGenericArgs(firstProtocol, out var resolvedArgs))
                    return BuildGenericInterfaceName(firstProtocol, resolvedArgs);
                return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
            }

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

        // Multi-protocol: require every effective protocol to have an emitted C# interface.
        // Marker / underscore-prefixed protocols sometimes lack a TypeRecord (the ABI digester
        // omits them, or the emitter suppresses them), so a bare composition like
        // `I_FooMarkerAnd_BarMarker` would reference interfaces that don't exist. Collapse to
        // `object` rather than emitting unresolvable CS0246 references — matches the single-
        // protocol path's TypeRecord gate above.
        //
        // Mirror GetCompositionInterfaceName's `GetEffectiveProtocols` set so a mixed
        // `ObjCProtocol & SwiftProtocol` composition (where the ObjC participant is filtered
        // and only the Swift one is emitted) is not over-broadly collapsed to `object` just
        // because the ObjC participant has no TypeRecord. We do this inline rather than
        // changing AllProtocolsHaveTypeRecords semantics: that predicate is also used by
        // many marshalling-decision callsites that depend on the non-marker semantics.
        if (!EffectiveProtocolsHaveTypeRecords(protocolList))
            return "object";

        // Mirror the single-protocol branch's Self/associated-type degrade (lines 824-825) for
        // compositions. A constituent protocol with a Self requirement or associated types emits
        // its C# interface GENERICALLY — `I{Name}<TSelf>` / `I{Name}<T{Assoc}, …>`
        // (ProtocolHandler.GetInterfaceNameWithGenerics) — or, for a stdlib/database-only protocol
        // like Swift.Encodable (no ProtocolDecl), emits NO interface at all. GetCompositionInterfaceName
        // builds a BARE, non-generic `I{Name}` base for every constituent, so such a participant yields
        // an interface base-list (`IEncodableAndSomeProtocol : IEncodable, …`) referencing an interface
        // that is never emitted under that name — a dangling CS0246 the *Proxy-only
        // ProxyReferenceIntegrityGate cannot see (it reconciles proxy-class identifiers, not interface
        // bases). Collapse the whole composition to `object`. This can only make an already-broken
        // Self/AT composition compilable — an ordinary composition (all constituents flag-free) is
        // untouched, since neither flag is set on a plain protocol's TypeRecord.
        if (EffectiveCompositionHasSelfOrAssociatedType(protocolList))
            return "object";

        return GetCompositionInterfaceName(protocolList);
    }

    /// <summary>
    /// True when any protocol in <paramref name="protocolList"/>'s effective (marker/ObjC-filtered)
    /// set carries <see cref="TypeRecordFlags.HasSelfRequirement"/> or
    /// <see cref="TypeRecordFlags.HasAssociatedTypes"/>. Such a protocol has no bare non-generic
    /// <c>I{Name}</c> interface — it emits generic (<c>I{Name}&lt;…&gt;</c>) or, for a stdlib/database-only
    /// protocol, nothing — so a composition base-list built from bare names would dangle. The
    /// composition-branch counterpart to the single-protocol Self/associated-type gate in
    /// <see cref="GetPublicExistentialType"/>. Reads <see cref="TypeRecord.Flags"/> directly so it
    /// works for stdlib/database-only protocols (e.g. <c>Swift.Encodable</c>) that never get a
    /// <c>ProtocolDecl</c>. Only reached after <see cref="EffectiveProtocolsHaveTypeRecords"/> has
    /// confirmed every effective protocol has a <see cref="TypeRecordKind.Protocol"/> record.
    /// </summary>
    private bool EffectiveCompositionHasSelfOrAssociatedType(ProtocolListTypeSpec protocolList)
    {
        foreach (var protocol in GetEffectiveProtocols(protocolList))
        {
            try
            {
                var swiftTypeName = SwiftTypeName.FromTypeSpec(protocol);
                if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord) &&
                    (typeRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement) ||
                     typeRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes)))
                    return true;
            }
            catch
            {
                // Malformed name — EffectiveProtocolsHaveTypeRecords already collapsed such a
                // composition to object before this method is called.
            }
        }
        return false;
    }

    /// <summary>
    /// Builds the closed-form interface reference (e.g.
    /// <c>SwiftBindingsTestLib.ILabelledContainer&lt;string&gt;</c>) for a constrained
    /// existential whose generic arguments have already been resolved by
    /// <see cref="TryResolveExistentialGenericArgs"/>. Cross-module references are
    /// qualified with the protocol's emission module so that Apple-supplement
    /// shapes resolve correctly.
    /// </summary>
    private string BuildGenericInterfaceName(NamedTypeSpec protocolSpec, List<string> resolvedArgs)
    {
        var protoTypeName = SwiftTypeName.FromTypeSpec(protocolSpec);
        var protoEmissionModule = ProtocolConformanceHelper.ResolveProtocolEmissionModule(protoTypeName, _typeDatabase);
        var protoInterfaceName = NameProvider.GetInterfaceName(protocolSpec.NameWithoutModule, moduleName: protoEmissionModule);
        if (!string.IsNullOrEmpty(CurrentModuleName) &&
            !string.IsNullOrEmpty(protoEmissionModule) &&
            protoEmissionModule != CurrentModuleName &&
            protoEmissionModule != "Swift")
        {
            protoInterfaceName = $"{protoEmissionModule}.{protoInterfaceName}";
        }
        return $"{protoInterfaceName}<{string.Join(", ", resolvedArgs)}>";
    }

    /// <summary>
    /// Attempts to lower every generic argument of a protocol existential to its closed-form
    /// C# type name. Returns false if any argument is a generic-parameter placeholder
    /// (<c>τ_n_m</c>), an associated-type reference, or otherwise unresolvable through
    /// the type database — in which case the caller falls back to <c>Swift.AnyType</c>.
    /// Mirrors the gating used by <see cref="IsConstrainedExistential"/>: the protocol's
    /// generic args must be concrete <see cref="NamedTypeSpec"/>s with TypeRecords.
    /// </summary>
    private bool TryResolveExistentialGenericArgs(NamedTypeSpec protocolSpec, out List<string> resolvedArgs)
    {
        resolvedArgs = new List<string>(protocolSpec.GenericParameters.Count);

        // Primary-associated-type sugar: Swift lets `protocol P<Frame, Event>` declare
        // only some of its associated types as primary, so a use site `any P<X, Y>`
        // can supply fewer generic args than the protocol's interface arity (the
        // ProtocolHandler emits one C# type parameter per associated type, including
        // non-primary ones — see GetInterfaceNameWithGenerics). Without this gate, a
        // 3-AT protocol referenced as `any P<X, Y>` would compile to `IP<X, Y>` and
        // fail with CS0305 "requires 3 type arguments".
        //
        // We require an exact arity match against the protocol's persisted
        // AssociatedTypeCount. Null (legacy module databases that predate the
        // attribute) is treated as "unverifiable" and falls through to the prior
        // permissive behavior — that path was already correct for protocols whose
        // primary == total associated types, which is the dominant shape.
        try
        {
            var protoSwiftName = SwiftTypeName.FromTypeSpec(protocolSpec);
            if (_typeDatabase.TryGetTypeRecord(protoSwiftName, out var protoRecord) &&
                protoRecord.AssociatedTypeCount.HasValue &&
                protoRecord.AssociatedTypeCount.Value != protocolSpec.GenericParameters.Count)
            {
                return false;
            }
        }
        catch
        {
            // Fall through — the per-arg loop below will still bail on unresolvable
            // arguments, so we never emit a half-resolved arity-mismatched reference.
        }

        foreach (var gp in protocolSpec.GenericParameters)
        {
            if (gp is not NamedTypeSpec named || TypeSpecHelpers.IsGenericTypeParameter(named.Name))
                return false;

            // Nested generics (e.g. any Foo<Array<Int>>) require the same resolution path
            // for each layer; without recursive lowering we'd emit `Array` instead of
            // `SwiftArray<Swift.SwiftInt>`. Out of scope for the conservative fix —
            // bail to AnyType so the consumer stays opaque rather than miscompiles.
            if (named.GenericParameters.Count > 0)
                return false;

            string? csName = null;
            try
            {
                var argSwiftName = SwiftTypeName.FromTypeSpec(named);
                if (_typeDatabase.TryGetTypeRecord(argSwiftName, out var argRecord) &&
                    argRecord.CSharpTypeName != null)
                {
                    csName = argRecord.CSharpTypeName.FullyQualifiedName;
                }
            }
            catch
            {
                csName = null;
            }

            if (string.IsNullOrEmpty(csName))
                return false;
            resolvedArgs.Add(csName!);
        }
        return resolvedArgs.Count > 0;
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
            // Use the LEAF protocol name only — for a nested protocol like
            // `NestedProtoOuter.Listener`, NameWithoutModule returns the dotted path
            // `NestedProtoOuter.Listener` which would produce `NestedProtoOuter.ListenerProxy`.
            // The proxy class itself is emitted at module level as `ListenerProxy`
            // (see ProtocolProxyEmitter.GetProxyClassName which uses ProtocolDecl.Name —
            // the leaf), so the call-site reference must match. The cross-module
            // qualification still happens later via QualifyProxyClassName.
            // The proxy class is emitted at module level using the leaf name; the call-site reference must match.
            var protocolName = LeafName(protocolList.Protocols.Keys.First().NameWithoutModule);
            return $"{protocolName}Proxy";
        }

        // Multi-protocol: combined proxy name (leaf names so nested protocols compose
        // with the same shape as top-level protocols).
        var names = protocolList.Protocols.Keys
            .Select(p => LeafName(p.NameWithoutModule))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        return string.Join("And", names) + "Proxy";
    }

    private static string LeafName(string nameWithoutModule)
    {
        var lastDot = nameWithoutModule.LastIndexOf('.');
        return lastDot >= 0 ? nameWithoutModule.Substring(lastDot + 1) : nameWithoutModule;
    }

    /// <summary>
    /// Gets the proxy class name qualified for cross-module use. When CurrentModuleName is set
    /// and the protocol's emission namespace differs from the current module, returns
    /// "OtherNamespace.SwiftInterop.ProxyName". Proxy classes live in the
    /// {Namespace}.SwiftInterop namespace (where {Namespace} is the C# namespace from the
    /// generator's namespace pattern, which can diverge from the Swift module name), so
    /// cross-assembly references require the full namespace qualification.
    /// </summary>
    public string GetQualifiedProxyClassName(ProtocolListTypeSpec protocolList)
    {
        return QualifyProxyClassName(GetProxyClassName(protocolList), protocolList);
    }

    /// <summary>
    /// Applies cross-module qualification to a proxy class name.
    /// Returns "Namespace.SwiftInterop.ProxyName" when the protocol's emission namespace differs
    /// from the current module. Used by both GetQualifiedProxyClassName and
    /// TryGetFilteredProxyClassName callers that need cross-module qualification on an
    /// already-computed (ObjC-filtered) name.
    /// </summary>
    public string QualifyProxyClassName(string proxyClassName, ProtocolListTypeSpec protocolList)
    {
        if (string.IsNullOrEmpty(CurrentModuleName))
            return proxyClassName;

        // Mirror GetEffectiveProtocols's predicate shape: drop marker protocols and
        // ObjC-bridged protocols before picking a module. Otherwise an ordering like
        // `Swift.Sendable & OtherModule.Protocol` would pick "Swift" first, return
        // unqualified, and defeat the cross-module qualification path.
        // Resolve via umbrella-aware mapping so Apple's `@_implementationOnly` re-exports
        // (e.g., printedName "any RealityKit.HasAnchoring" for a protocol that actually
        // lives in RealityFoundation) collapse to the source module's namespace where the
        // proxy class is emitted. Mirrors GetPublicExistentialType's resolution path.
        var protocolModule = protocolList.Protocols.Keys
            .Where(p => !IsMarkerProtocol(p) && !TypeDatabaseExtensions.IsObjCExistentialBridgedProtocol(p))
            .Select(p => ProtocolConformanceHelper.ResolveProtocolEmissionModule(SwiftTypeName.FromTypeSpec(p), _typeDatabase))
            .FirstOrDefault(m => !string.IsNullOrEmpty(m));

        if (protocolModule == null || protocolModule == CurrentModuleName || protocolModule == "Swift")
            return proxyClassName;

        return $"{protocolModule}.SwiftInterop.{proxyClassName}";
    }

    // ==================== Proxy-suppression oracle ====================
    // The single emit-time decision that replaces the retired regex post-pass over
    // emitted C#: would a reference to this existential's proxy class name a proxy that was NOT
    // emitted because its EveryProtocol conformance was suppressed? Two call-site behaviors share
    // one predicate — CONSUME references drop their wrap fallback (member stays), PRODUCE
    // constructions throw so the member-emit boundary can stub/skip the whole member.

    /// <summary>
    /// Core suppression predicate. Preserves the local-vs-cross-module matching split: an
    /// <em>unqualified</em> reference (current module) matches only the simple suppressed-name set
    /// on <paramref name="ctx"/>, while a <c>{Namespace}.SwiftInterop.{Proxy}</c> reference (a
    /// dependency) matches only the cross-module <c>(namespace, name)</c> pairs persisted by that
    /// dependency's earlier generator run. The two sets are never cross-checked, so a current-module
    /// proxy and a same-named dependency proxy never false-positive on each other. Read-only
    /// (Swift-vended-only) proxies are absent from both sets — <see cref="ProtocolHandler"/> excludes
    /// <c>IsReadOnlyProxy</c> when recording — so the exemption holds for free.
    /// </summary>
    public bool IsProxyNameSuppressed(string bareName, string qualifiedName, ModuleEmissionContext? ctx)
    {
        // QualifyProxyClassName returns the bare name unchanged for a current-module reference
        // (also when the protocol module is "Swift" or CurrentModuleName is unset). Match those
        // against the current-module simple set only.
        if (string.Equals(qualifiedName, bareName, StringComparison.Ordinal))
            return ctx != null && ctx.SuppressedProxyClassNames.Contains(bareName);

        // A qualified reference can only point at a dependency: match the cross-module pairs,
        // never the current-module set (else a same-named local proxy false-positives).
        foreach (var (ns, proxyName) in _typeDatabase.GetCrossModuleSuppressedProxyClassNames())
        {
            if (string.Equals(qualifiedName, $"{ns}.SwiftInterop.{proxyName}", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when a reference to the proxy class for <paramref name="protocolList"/> would name a
    /// proxy recorded in a suppressed-name set. The NAME half of the availability oracle
    /// <see cref="IsProxyReferenceUnavailable"/>, which ORs this with the structural Self/AT half
    /// (<see cref="IsProxyStructurallyNeverEmitted"/>) that the name sets never record.
    /// </summary>
    public bool IsProxyReferenceSuppressed(ProtocolListTypeSpec protocolList, ModuleEmissionContext? ctx)
    {
        var bareName = GetProxyClassName(protocolList);
        return IsProxyNameSuppressed(bareName, QualifyProxyClassName(bareName, protocolList), ctx);
    }

    /// <summary>
    /// True when a marshalling site that reaches its proxy branch through
    /// <see cref="AllProtocolsHaveTypeRecords"/> must NOT construct a <c>new {P}Proxy(…)</c> for
    /// <paramref name="protocolList"/>, because that proxy class is not present in the emitted output.
    /// Broader than <see cref="IsProxyReferenceSuppressed"/> by one structural reason the suppressed-name
    /// set never records: a protocol with a Self requirement or associated types, for which
    /// <c>ProtocolProxyEmitter.EmitProxyClass</c> unconditionally early-returns without writing any class.
    /// Such a protocol still carries a Kind=Protocol TypeRecord — so <see cref="AllProtocolsHaveTypeRecords"/>
    /// is true and the proxy branch would otherwise fire — yet it is never in the suppressed-name set (a
    /// Swift stdlib protocol like <c>Encodable</c> that the consuming module never declares, so the
    /// precompute pass never visits it). Degrading here keeps the marshalling body consistent with the
    /// signature <see cref="GetPublicExistentialType"/> already emits for the same existential — <c>object</c>,
    /// not <c>I{P}</c> — and is always sound: the class provably does not exist, so this can only remove a
    /// dangling reference, never suppress a proxy that would otherwise be emitted. Mirrors the identical
    /// Self/AT withdrawal in <c>ProtocolProxyEmissionPolicy.Decide</c>.
    /// </summary>
    public bool IsProxyReferenceUnavailable(ProtocolListTypeSpec protocolList, ModuleEmissionContext? ctx)
    {
        return IsProxyReferenceSuppressed(protocolList, ctx) || IsProxyStructurallyNeverEmitted(protocolList);
    }

    /// <summary>
    /// The structural (flags) half of <see cref="IsProxyReferenceUnavailable"/>, exposed separately so
    /// CONSUME sites that key their name-half on an already-computed ObjC-filtered proxy name (via
    /// <see cref="IsProxyNameSuppressed"/>) can AND it in without changing how the name is derived.
    /// True when no <c>{P}Proxy</c> class is ever emitted for <paramref name="protocolList"/> for a
    /// reason the suppressed-name set never records: a protocol with a Self requirement or associated
    /// types, for which <c>ProtocolProxyEmitter.EmitProxyClass</c> unconditionally early-returns. The
    /// live residual is the CONSTRAINED existential (<c>any P&lt;X&gt;</c>) on a TypeRecord-only
    /// (foreign/dependency) protocol: its public type stays the generic interface rather than demoting
    /// to <c>object</c> — so the <c>publicType != "object"</c> gates pass — yet the precompute pass
    /// (which only visits ProtocolDecls) never records the proxy as suppressed. Always sound to degrade
    /// on: the class provably does not exist, so this can only remove a dangling reference.
    /// </summary>
    public bool IsProxyStructurallyNeverEmitted(ProtocolListTypeSpec protocolList)
    {
        // Over the same non-marker set AllProtocolsHaveTypeRecords already verified has TypeRecords,
        // mirror EmitProxyClass's Self/AT early-return: any such protocol yields no `{P}Proxy` class.
        foreach (var protocol in GetNonMarkerProtocols(protocolList))
        {
            try
            {
                var swiftTypeName = SwiftTypeName.FromTypeSpec(protocol);
                if (_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord) &&
                    (typeRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement) ||
                     typeRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes)))
                    return true;
            }
            catch
            {
                // FromTypeSpec throws only on a malformed name, which resolves to no proxy anyway; the
                // AllProtocolsHaveTypeRecords gate every caller passes first already excluded that case.
            }
        }
        return false;
    }

    /// <summary>
    /// CONSUME path. Returns the cross-module-qualified proxy class name to use in a
    /// <c>GetOrCreate&lt;T&gt;(value, static __v =&gt; new {Proxy}(__v))</c> wrap fallback, or
    /// <c>null</c> when the proxy is unavailable — name-suppressed OR a Self/AT protocol whose
    /// proxy class is never emitted (see <see cref="IsProxyReferenceUnavailable"/>) — in which
    /// case the call site emits the no-fallback overload and the member stays. Mirrors the
    /// nullable contract of <see cref="ClosureHandler.GetQualifiedProxyClassName"/>.
    /// </summary>
    public string? TryGetConsumableProxyClassName(ProtocolListTypeSpec protocolList, ModuleEmissionContext? ctx)
    {
        return IsProxyReferenceUnavailable(protocolList, ctx)
            ? null
            : QualifyProxyClassName(GetProxyClassName(protocolList), protocolList);
    }

    /// <summary>
    /// PRODUCE path. Returns the cross-module-qualified proxy class name for a standalone
    /// <c>new {Proxy}(…)</c> construction, or throws <see cref="SuppressedProxyReferenceException"/>
    /// when the proxy is unavailable — name-suppressed OR a Self/AT protocol whose proxy class is
    /// never emitted (see <see cref="IsProxyReferenceUnavailable"/>) — so the member-emit boundary
    /// can roll back and stub/skip the whole member (it cannot produce the value without the proxy).
    /// The Self/AT half matters for constrained existentials (<c>any P&lt;X&gt;</c>): their public
    /// type stays the generic interface rather than demoting to <c>object</c>, so no earlier
    /// object-fallback shields the construction site.
    /// </summary>
    public string GetRequiredProxyClassName(ProtocolListTypeSpec protocolList, ModuleEmissionContext? ctx)
    {
        var qualifiedName = QualifyProxyClassName(GetProxyClassName(protocolList), protocolList);
        if (IsProxyReferenceUnavailable(protocolList, ctx))
            throw new SuppressedProxyReferenceException(qualifiedName);
        return qualifiedName;
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
    /// <summary>
    /// Like <see cref="AllProtocolsHaveTypeRecords"/> but operates on the same
    /// <see cref="GetEffectiveProtocols"/> set that <see cref="GetCompositionInterfaceName"/> uses —
    /// ObjC-bridged protocols are dropped before the TypeRecord check. Used by
    /// <see cref="GetPublicExistentialType"/> so a mixed `ObjCProtocol &amp; SwiftProtocol`
    /// composition is not over-broadly collapsed to `object`.
    /// </summary>
    public bool EffectiveProtocolsHaveTypeRecords(ProtocolListTypeSpec protocolList)
    {
        if (protocolList.Protocols.Count == 0)
            return false;

        var effective = GetEffectiveProtocols(protocolList);
        if (effective.Count == 0)
            return true; // Pure-marker / pure-ObjC composition — handled downstream

        foreach (var protocol in effective)
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
        // Leaf names so a nested protocol like `Outer.Foo` produces `FooProxy`
        // matching the proxy-class emission shape. See GetProxyClassName above.
        if (protocols.Count == 1) { proxyClassName = $"{LeafName(protocols[0].NameWithoutModule)}Proxy"; return true; }
        proxyClassName = string.Join("And", protocols.Select(p => LeafName(p.NameWithoutModule))) + "Proxy";
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
