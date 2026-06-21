// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

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
/// the type-level note on <see cref="CdeclLoweringDescriptor.PInvokeParams"/>.
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
    CustomFrozenStruct,
    RawBufferPointer,
    NonCopyableBorrow,
    NonCopyableConsume,
    Inout,
    Fallback,
}

/// <summary>
/// One C# P/Invoke parameter contributed by a multi-word @_cdecl lowering, as a (type, name-suffix)
/// shape. Used only for the categories whose @_cdecl boundary splits a single Swift value into
/// several C ABI words with a shared name contract the C# side must reproduce verbatim
/// (e.g. SwiftString / Foundation.Data → <c>_w0</c>/<c>_w1</c>; UnsafeRawBufferPointer →
/// <c>Ptr</c>/<c>Len</c>; @_cdecl-property String → <c>Utf8Ptr</c>/<c>Utf8Len</c>). The C# leg
/// applies its own parameter base name to <see cref="NameSuffix"/>; the descriptor never bakes in a
/// base name because the Swift side keys off the demangled label while the C# side keys off
/// <c>NameProvider.GetCSharpParameterName</c>.
/// </summary>
internal readonly record struct CdeclPInvokeParam(string CSharpType, string NameSuffix);

/// <summary>
/// The single source of truth for how one parameter lowers across the @_cdecl boundary. Produced
/// once by <see cref="CdeclParamMapper.Describe"/> and consumed by the Swift-wrapper emitters
/// (signature + body) via the projecting <see cref="CdeclParamMapper.Map"/>/<c>MapInout</c> shims.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Category"/> plus the three Swift-text fields (<see cref="CdeclParam"/>,
/// <see cref="Reconstruction"/>, <see cref="CallArg"/>) and <see cref="WriteBack"/> are the
/// load-bearing single-source: every Swift-side consumer reads them so the wrapper signature, the
/// body local, the call argument, and the inout write-back can never drift apart.
/// </para>
/// <para>
/// <see cref="PInvokeParams"/> and <see cref="SharedLocalNames"/> carry ONLY the cross-file
/// multi-word name contract (the categories listed on <see cref="CdeclPInvokeParam"/>). The C#
/// P/Invoke emitter classifies parameters through its own richer <c>MarshalledType</c> union, which
/// is a distinct emission mechanism and is deliberately not folded into this descriptor; collapsing
/// it would force a (type, name) tuple to stand in for the semantic <c>MarshalledType</c> variants
/// (<c>ObjCBridged</c>, <c>FrozenBuffer</c>, <c>SimpleEnum</c>, …) and lose information. The two
/// sides agree by recomputation, not by a threaded instance: each calls the deterministic
/// <see cref="CdeclParamMapper.Describe"/> and reads equal-by-recomputation names.
/// </para>
/// </remarks>
internal readonly record struct CdeclLoweringDescriptor(
    CdeclParamCategory Category,
    string CdeclParam,
    string? Reconstruction,
    string CallArg,
    IReadOnlyList<CdeclPInvokeParam> PInvokeParams,
    IReadOnlyList<string> SharedLocalNames,
    bool NeedsUnsafe,
    string? WriteBack)
{
    /// <summary>Shared empty list so the common (no multi-word contract) arms allocate nothing.</summary>
    internal static readonly IReadOnlyList<CdeclPInvokeParam> NoPInvokeParams = System.Array.Empty<CdeclPInvokeParam>();

    /// <summary>Shared empty list for arms with no leg-C shared local names.</summary>
    internal static readonly IReadOnlyList<string> NoSharedLocalNames = System.Array.Empty<string>();
}
