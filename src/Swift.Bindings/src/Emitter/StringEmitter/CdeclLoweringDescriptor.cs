// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The per-parameter @_cdecl lowering category — the single classification decision that the
/// Swift-wrapper producer (<see cref="CdeclParamMapper.Describe"/>) makes once per parameter, so
/// the wrapper-signature text, body reconstruction, and call-site expression all derive from one
/// branch instead of re-deciding the category in each consumer.
/// </summary>
/// <remarks>
/// The values mirror the ordered if-chain in <see cref="CdeclParamMapper.Describe"/> one-to-one.
/// They describe how a parameter crosses the @_cdecl boundary on the <em>Swift</em> side; the C#
/// P/Invoke side (<c>PInvokeEmitter.HandleArguments</c>) is a separate emission mechanism that
/// builds <c>MarshalledType</c> records and is intentionally NOT collapsed into this enum — see
/// the type-level remarks on <see cref="CdeclLoweringDescriptor"/>.
/// </remarks>
internal enum CdeclParamCategory
{
    Primitive,
    Bool,
    AnyObject,
    OptionalAny,
    ProtocolExistential,
    OptionalReference,
    OptionalBlittablePrimitive,
    OptionalOpaque,
    ObjCBridgeableContainer,
    OptionalObjCBridgeableContainer,
    GenericContainer,
    Date,
    Data,
    String,
    ClassPointer,
    ObjCBridgedClassPointer,
    ObjCBridgeableValue,
    ProtocolTypeRecord,
    SimpleEnum,
    ComplexEnum,
    NonFrozenStruct,
    SystemFrozenStruct,
    ObjCBridgedValueStruct,
    CustomFrozenStruct,
    RawBufferPointer,
    NonCopyableBorrow,
    NonCopyableConsume,
    Inout,
    Fallback,
}

/// <summary>
/// The single source of truth for how one parameter lowers across the @_cdecl boundary on the
/// <em>Swift</em> side. Produced once by <see cref="CdeclParamMapper.Describe"/> and consumed by the
/// Swift-wrapper emitters (signature + body) via the projecting <see cref="CdeclParamMapper.Map"/>/
/// <c>MapInout</c> shims.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Category"/> plus the three Swift-text fields (<see cref="CdeclParam"/>,
/// <see cref="Reconstruction"/>, <see cref="CallArg"/>) and <see cref="WriteBack"/> are the
/// load-bearing single-source: every Swift-side consumer reads them so the wrapper signature, the
/// body local, the call argument, and the inout write-back can never drift apart.
/// </para>
/// <para>
/// This descriptor is deliberately Swift-side-only. The C# P/Invoke emitter
/// (<c>PInvokeEmitter.HandleArguments</c>) classifies parameters through its own richer
/// <c>MarshalledType</c> union — a distinct emission mechanism that is intentionally NOT folded into
/// this descriptor, because collapsing it would force a (type, name) tuple to stand in for the
/// semantic <c>MarshalledType</c> variants (<c>ObjCBridged</c>, <c>FrozenBuffer</c>,
/// <c>SimpleEnum</c>, …) and lose information. For the few categories whose @_cdecl boundary splits a
/// single Swift value into several C ABI words (SwiftString / Foundation.Data → <c>_w0</c>/<c>_w1</c>;
/// UnsafeRawBufferPointer → <c>Ptr</c>/<c>Len</c>), the two sides agree on that shared name contract by
/// <em>recomputation</em> — each runs its own deterministic classifier and reads equal-by-recomputation
/// names — not by a threaded instance.
/// </para>
/// </remarks>
internal readonly record struct CdeclLoweringDescriptor(
    CdeclParamCategory Category,
    string CdeclParam,
    string? Reconstruction,
    string CallArg,
    bool NeedsUnsafe,
    string? WriteBack);
