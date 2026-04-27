// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;

namespace BindingsGeneration;

/// <summary>
/// Represents type's flags.
/// </summary>
[Flags]
public enum TypeRecordFlags
{
    None = 0,
    // This flag is used in tooling to determine whether the type should be enregistered.
    // A type marked as "frozen" on the Swift side doesn't change its layout.
    // However, if it contains a non-frozen struct as a property, it is considered an opaque at compile-time;
    // otherwise, the layout is considered as known at compile-time and enregistration if possible.
    Frozen = 1 << 0,
    // This flag is used in tooling to determine whether a type requires memory management,
    // ensuring that the finalizer can handle memory if needed.
    // The 'RequiresMemoryManagement' flag indicates that the type is allocated on the heap (as in the case of classes)
    // or that it contains a heap-allocated property (for example, a struct with a reference property).
    RequiresMemoryManagement = 1 << 1,
    // This flag indicates the type is an Objective-C bridged class (e.g., UIImage, NSImage, URLResponse).
    // These types should be remapped to their .NET iOS binding equivalents (e.g., UIKit.UIImage)
    // rather than custom Swift.* wrapper types.
    ObjCBridged = 1 << 2,
    // This flag indicates a protocol has associated types.
    // Such protocols generate generic C# interfaces (e.g., ISwiftContainer<TElement>) and
    // cannot be used directly as generic constraints without type arguments.
    HasAssociatedTypes = 1 << 3,
    // This flag indicates the enum is simple (no associated values, frozen, non-generic,
    // integral or no raw value) and should be emitted as a C# enum value type.
    SimpleEnum = 1 << 4,
    // This flag indicates a protocol has a Self requirement (Self appears in method signatures).
    // Such protocols generate generic C# interfaces (e.g., IFoo<TSelf>) and cannot be used
    // as non-generic constraints or in conformance dictionaries.
    HasSelfRequirement = 1 << 5,
    // This flag indicates a protocol has no own instance members but inherits from
    // protocols with requirements. ProtocolProxyEmitter skips proxy emission for these
    // (would produce CS0535 — missing inherited interface members).
    InheritedRequirementsOnly = 1 << 6,
    // This flag indicates a protocol is class-bound (inherits AnyObject).
    // Only class-bound protocols can be bridged via Unmanaged<AnyObject> cast
    // in @_silgen_name wrappers for constrained existential parameters.
    ClassBound = 1 << 7,
    // This flag indicates a class is rooted in an ObjC type hierarchy (e.g., inherits from NSObject).
    // Such classes are projected as C# classes inheriting from the MAUI ObjC binding type
    // (e.g., CoreAnimation.CALayer) instead of using SwiftSafeHandle-based payload management.
    ObjCRooted = 1 << 8,
    // This flag indicates a protocol's methods use Self (τ_0_0) in parameter/return types
    // but the protocol is NOT flagged with HasSelfRequirement. The interface emits AnyType
    // for Self positions, making the constraint unsatisfiable by concrete types (CS0738).
    // Used to skip the constraint in generic where clauses and bound generic validation.
    HasMethodSelfTypeParams = 1 << 9,
    // This flag indicates a struct is non-copyable (~Copyable). Non-copyable types explicitly
    // list Swift.Escapable in their conformances (normal types have both Copyable and Escapable
    // implicitly, unlisted). Used to skip @_cdecl constructor wrappers for cross-module params.
    NonCopyable = 1 << 10,
    // This flag indicates a protocol inherits from Codable (Decodable/Encodable), either
    // directly or transitively through inherited protocols. EveryProtocol can't synthesize
    // Codable conformance, so these protocols must be skipped during conformance emission.
    InheritsCodable = 1 << 11,
    // This flag indicates a frozen struct contains float/double fields (directly or transitively
    // through non-system nested structs). Such structs are ABI-unsafe for CallConvSwift on ARM64:
    // NativeAOT places float fields in GPR instead of FPR (params), and Mono crashes on float
    // struct returns. System structs (CGRect, etc.) are NOT flagged — they have special runtime handling.
    HasFloatFields = 1 << 12,
    // This flag indicates a frozen struct contains Bool fields (directly or transitively).
    // Bool is non-blittable in .NET CallConvSwift — the runtime rejects structs containing Bool
    // with "Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported".
    HasBoolFields = 1 << 13,
    // This flag indicates a Swift value type that freely bridges to an ObjC class via
    // _ObjectiveCBridgeable (e.g., Foundation.URL ↔ NSURL). At the @_cdecl boundary,
    // these types cross as ObjC object pointers (UnsafeMutableRawPointer) instead of
    // attempting to pass the Swift struct directly. Distinct from ObjCBridged (which marks
    // ObjC class wrappers) and nativeType (which controls public API remapping).
    ObjCBridgeable = 1 << 14,
    // This flag indicates a type the emitter will skip entirely (e.g., single-case
    // no-payload enums, which have TypeMetadata.Size == 0). Member-level validators
    // treat references to such types as unsupported so that they don't emit dangling
    // symbol references to a type that will never be generated.
    Unemittable = 1 << 15,
}

/// <summary>
/// Represents a type kind.
/// </summary>
public enum TypeRecordKind
{
    Struct,
    Enum,
    Class,
    Protocol,
    /// <summary>
    /// Existential containers (any Protocol) - fixed-size structs that can be marshalled.
    /// Distinct from Protocol which represents abstract C# interfaces.
    /// </summary>
    Existential,
}

/// <summary>
/// Represents a type within a module, including metadata for interfacing with Swift.
/// </summary>
public record TypeRecord
{
    /// <summary>
    /// The C# type information.
    /// </summary>
    public required CSharpTypeName CSharpTypeName { get; set; }

    /// <summary>
    /// The Swift type identifier.
    /// </summary>
    public required SwiftTypeName SwiftTypeName { get; init; }

    /// <summary>
    /// The Swift metadata accessor.
    /// </summary>
    public required string MetadataAccessor { get; init; }

    /// <summary>
    /// The Swift runtime type information.
    /// </summary>
    public SwiftTypeInfo? SwiftTypeInfo { get; init; }

    /// <summary>
    /// Type flags.
    /// </summary>
    public required TypeRecordFlags Flags { get; set; }

    /// <summary>
    /// The kind of type.
    /// </summary>
    public required TypeRecordKind Kind { get; init; }

    /// <summary>
    /// Optional native type name to use in public method signatures.
    /// When set, the public API exposes this type (e.g., Foundation.NSUrl) instead of the
    /// internal Swift wrapper type (e.g., Swift.Foundation.Data). Conversion happens at the marshalling layer.
    /// </summary>
    public CSharpTypeName? NativeTypeName { get; init; }

    /// <summary>
    /// The raw value type name for RawRepresentable enums (e.g., "Int", "Int32", "String").
    /// Null if the type is not an enum or does not conform to RawRepresentable.
    /// </summary>
    public string? RawValueTypeName { get; init; }

    /// <summary>
    /// The number of members emitted in the C# interface for this protocol.
    /// Only meaningful for Protocol kind records. Null means unknown (e.g., loaded from
    /// an older module database that predates this field). 0 means empty/marker interface.
    /// Used by cross-module conformance emission to avoid CS0535 errors: a type declaring
    /// conformance to a cross-module protocol with members would fail compilation since the
    /// generator cannot produce stubs for cross-module protocol requirements.
    /// </summary>
    public int? EmittedMemberCount { get; init; }

    /// <summary>
    /// The Swift type name of this class's direct superclass, or null for root classes
    /// and non-class types. Used for cross-module inheritance resolution.
    /// </summary>
    public SwiftTypeName? SuperclassTypeName { get; init; }

    /// <summary>
    /// Direct protocol conformances declared by this type. For struct/class/enum kinds
    /// these are the protocols listed on the type's <c>Conformances</c> in ABI JSON.
    /// For Protocol kind, these are the protocol's <c>InheritedProtocols</c> — refining
    /// edges in the protocol graph. The transitive closure is computed at filter time
    /// by walking each entry's own <see cref="ProtocolConformances"/>; we deliberately
    /// store DIRECT edges only to keep the persisted size bounded and the data model
    /// uniform across kinds.
    ///
    /// Used by <see cref="BindingsGeneration.ConcreteProtocolSpecializationEmitter.DoesPairingSatisfyAssociatedTypeConstraints"/>
    /// to verify <c>S.Element : SomeProtocol</c> bounds when the conformer's recorded
    /// Element doesn't exact-match the constraint target. Null means the data wasn't
    /// populated (typically loaded from an older module database that predates this
    /// field) — the filter treats null as "unverifiable" and fails closed for
    /// protocol-conformance bounds whose target is a true protocol.
    /// </summary>
    public IReadOnlyList<SwiftTypeName>? ProtocolConformances { get; init; }

    /// <summary>
    /// The inline byte size of this type when embedded as a field in a frozen struct Buffer.
    /// For XML-loaded types, this comes from the <c>inlineSize</c> attribute.
    /// For module types, this is computed from <see cref="SwiftTypeInfo"/> at parse time.
    /// Used by FrozenStructHandler to emit correctly-sized backing fields (e.g., Swift.String
    /// is 16 bytes but would otherwise default to IntPtr = 8 bytes).
    /// Null means unknown — falls back to IntPtr.Size for RequiresMemoryManagement types.
    /// </summary>
    public int? InlineSize { get; init; }

    /// <summary>
    /// Compact ABI field layout string describing per-field register classification for ARM64 thunks.
    /// Each character represents one field: 'i' = integer (8B), 'f' = float (8B),
    /// 'b' = bool (1B padded to 8B), 'p' = pointer (8B). Fields are comma-separated.
    /// Example: "i,f,i,f" for a struct { Int, Double, Int, Double }.
    /// Computed during parsing from TypeDecl.Properties and persisted in module database XML.
    /// Null means layout is unknown (non-frozen, cross-module without persisted layout, etc.).
    /// When null, functions returning this type cannot use native thunks and must fall back to @_cdecl.
    /// </summary>
    public string? AbiFieldLayout { get; init; }

    /// <summary>
    /// For Protocol kind: the mangled symbol of the protocol descriptor (e.g.
    /// <c>$s6Lottie16AnyInterpolatableMp</c>). Null for non-protocol kinds.
    /// Used by the type-metadata-accessor emitter to construct dynamic
    /// witness-table lookups for Self-requirement / associated-type protocols
    /// that cannot be expressed as a static C# interface — when the constraint
    /// can't be projected we still need to pass a runtime witness table to the
    /// Swift metadata accessor, so we look up the descriptor by symbol and call
    /// <c>swift_conformsToProtocol</c> at runtime.
    /// </summary>
    public string? ProtocolDescriptorSymbol { get; init; }

    /// <summary>
    /// Swift-side canonical identity for this type. Synonym of <see cref="SwiftTypeName"/> — they
    /// always refer to the same instance; no independent storage is intended. Introduced by the
    /// Apple-supplement work so the three conceptual aspects of a type — Swift identity,
    /// managed projection, and ABI carrier — are all addressable as first-class properties on
    /// the record.
    /// </summary>
    public SwiftTypeName SwiftIdentity => SwiftTypeName;

    /// <summary>
    /// Optional override for the managed (consumer-facing) C# projection of this type.
    /// When <c>null</c>, <see cref="EffectiveManagedProjection"/> falls back to
    /// <see cref="CSharpTypeName"/>. Populating this lets the Apple supplement pin the
    /// consumer-visible surface to a type outside the declaring module (for example,
    /// <c>global::Foundation.NSLocale</c> for a Swift <c>Foundation.Locale</c>) without
    /// losing the carrier/identity distinction carried on the same record.
    /// </summary>
    public CSharpTypeName? ManagedProjectionTypeName { get; init; }

    /// <summary>
    /// Optional override for the C# type used as the ABI carrier across the Swift→C
    /// boundary (copy/destroy/pass). When <c>null</c>, <see cref="EffectiveAbiCarrier"/>
    /// falls back to <see cref="CSharpTypeName"/>. Populating this separates "the type
    /// the consumer sees" from "the type actually marshalled" — required for
    /// VWT-backed opaque supplement types whose projection is a surface struct but whose
    /// carrier is a <c>SwiftHandle</c>/opaque payload.
    /// </summary>
    public CSharpTypeName? AbiCarrierTypeName { get; init; }

    /// <summary>
    /// The managed (consumer-facing) C# projection for this type. Returns
    /// <see cref="ManagedProjectionTypeName"/> if set, otherwise <see cref="CSharpTypeName"/>.
    /// </summary>
    public CSharpTypeName EffectiveManagedProjection => ManagedProjectionTypeName ?? CSharpTypeName;

    /// <summary>
    /// The C# type used as the ABI carrier for this type. Returns
    /// <see cref="AbiCarrierTypeName"/> if set, otherwise <see cref="CSharpTypeName"/>.
    /// </summary>
    public CSharpTypeName EffectiveAbiCarrier => AbiCarrierTypeName ?? CSharpTypeName;

    /// <summary>
    /// For <see cref="TypeRecordKind.Class"/> records: signatures of the class instance
    /// methods the emitter actually wrote to C# output (<c>WasEmitted == true</c>).
    /// Populated as a post-emission step on the producing module so a downstream module's
    /// cross-module override path can verify the derived class's <c>override</c> modifier
    /// has a matching parent method before writing C# <c>override</c> (otherwise CS0115).
    /// Null on non-class records or on legacy module databases that predate this field —
    /// callers fall back to trusting Swift's <c>IsOverride</c> bit, which is the prior
    /// v0.8.x behavior.
    /// </summary>
    public IReadOnlyList<EmittedClassMethod>? EmittedClassMethods { get; init; }
}

/// <summary>
/// Compact signature of a class instance method that survived emission. Stored on the
/// declaring class's <see cref="TypeRecord.EmittedClassMethods"/> for cross-module
/// override verification. <see cref="SwiftName"/> + <see cref="ParameterSwiftTypes"/>
/// identify the Swift overload (entries produced via <c>SwiftTypeSpec.ToString()</c>
/// at emission time so the verifier can match by exact spec string without re-parsing).
/// <see cref="CSharpName"/> records the public C# method name post all NameProvider
/// renaming (property collisions, self-returning builders, "Get" prefix, "Async"
/// suffix, etc.) so a downstream module can verify that the parent binding actually
/// emits a method with the same C# name as the derived class — Swift name + parameter
/// types alone aren't sufficient because two classes can produce different C# names
/// for the same Swift method depending on their property/nested-type sets. Empty when
/// loaded from a legacy database that predates this attribute; the verifier treats
/// empty as "skip the C# name check" to preserve compatibility with already-published
/// parent NuGets.
/// </summary>
public sealed record EmittedClassMethod(string SwiftName, string CSharpName, IReadOnlyList<string> ParameterSwiftTypes);
