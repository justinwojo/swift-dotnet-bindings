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
    /// internal Swift wrapper type (e.g., Swift.Data). Conversion happens at the marshalling layer.
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
}
