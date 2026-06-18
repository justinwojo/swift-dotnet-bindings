// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The async-result carrier-ownership decision: a single, testable source of truth for the two
/// booleans that drive every async wrapper's memory handling.
/// </summary>
/// <remarks>
/// <para>
/// Swift's async start-thunk always allocates the result carrier and writes the value via
/// <c>initializeMemory(as:repeating:count:1)</c>, which runs the type's value witness and performs
/// a <c>+1</c> retain on internal references. The C# completion callback must release that <c>+1</c>
/// unless ownership is transferred wholesale to a SafeHandle. <see cref="CallbackTakesOwnership"/>
/// and <see cref="CarrierNeedsDestroy"/> encode that decision; the Swift renderer's behaviour is
/// fixed (always initialize), so the ownership algebra is purely a C#-side concern.
/// </para>
/// <para>
/// This was previously duplicated verbatim in two async renderers —
/// <c>AsyncHarnessEmitter.EmitAsyncWrapper</c> and
/// <c>AsyncMethodGenericBridgeEmitter.EmitComplexValueDispatch</c> — where the algebra could drift
/// silently. Centralizing it here is the S13 Pillar A "single source" for async result ownership.
/// The narrower concrete-specialization async-parent path
/// (<c>ConcreteProtocolSpecializationEmitter.AsyncGenericParent</c>) does NOT participate: it admits
/// only blittable-primitive and non-frozen-struct returns and frees its carrier by a distinct
/// blittable-vs-SafeHandle rule, so it has no ownership selector to unify.
/// </para>
/// </remarks>
public sealed record AsyncResultPlan
{
    /// <summary>
    /// The completion callback owns the carrier's payload (it constructs a managed wrapper that
    /// adopts the value), so the carrier must NOT be value-witness-destroyed before <c>SBW_Free</c>.
    /// True for non-frozen structs and complex (non-simple) enums.
    /// </summary>
    public required bool CallbackTakesOwnership { get; init; }

    /// <summary>
    /// The carrier holds non-trivial internal references that nothing else releases, so it must be
    /// value-witness-destroyed before <c>SBW_Free</c> to avoid leaking the <c>+1</c>. The broader
    /// set: callback-owned types plus frozen-structs-projected-as-class, plus (via
    /// <see cref="AsyncResultPlanner.WidenDestroyForOptionalPayload"/>) Optionals wrapping any of those.
    /// </summary>
    public required bool CarrierNeedsDestroy { get; init; }
}

/// <summary>
/// Classifies the async-result carrier-ownership decision (<see cref="AsyncResultPlan"/>) from a
/// return type's <see cref="TypeRecord"/>. The single source the async renderers route through.
/// </summary>
public static class AsyncResultPlanner
{
    /// <summary>
    /// The core ownership algebra for a complex async return type. Pure function of the return
    /// record's kind and frozen/simple flags — non-frozen structs and complex enums are
    /// callback-owned; those plus frozen-structs-projected-as-class need the carrier destroyed.
    /// </summary>
    /// <remarks>
    /// RequiresMemoryManagement is not set on non-frozen structs by the parser (only on frozen
    /// structs containing reference-type fields), so classification is purely kind + frozen/simple
    /// flags here. Callers apply their own up-front guards (class / ObjC-bridged / optional-reference
    /// shapes are handled on separate paths and never reach this algebra).
    /// </remarks>
    public static AsyncResultPlan ClassifyCarrierOwnership(TypeRecord returnRecord)
    {
        bool isNonFrozenStruct = returnRecord.Kind == TypeRecordKind.Struct
            && !MarshallingHelpers.IsTypeFrozen(returnRecord);
        bool isComplexEnum = returnRecord.Kind == TypeRecordKind.Enum
            && !returnRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
        bool isFrozenAsClass = MarshallingHelpers.IsFrozenStructProjectedAsClass(returnRecord);

        bool callbackTakesOwnership = isNonFrozenStruct || isComplexEnum;
        bool carrierNeedsDestroy = callbackTakesOwnership || isFrozenAsClass;
        return new AsyncResultPlan
        {
            CallbackTakesOwnership = callbackTakesOwnership,
            CarrierNeedsDestroy = carrierNeedsDestroy,
        };
    }

    /// <summary>
    /// Optional&lt;value-type&gt; widening for <see cref="AsyncResultPlan.CarrierNeedsDestroy"/>:
    /// returns true when <paramref name="returnSpec"/> is <c>Optional&lt;T&gt;</c> whose inner T has
    /// a non-trivial value witness (frozen-as-class, non-frozen struct, or complex enum).
    /// </summary>
    /// <remarks>
    /// On the plain SwiftOptional&lt;T&gt;.ToNullable path the Swift-side <c>initializeMemory</c> runs
    /// Optional&lt;T&gt;'s copy witness, so for <c>.some</c> the embedded non-trivial payload holds its
    /// own <c>+1</c>. SwiftOptional&lt;T&gt;'s NewFromPayload performs its own InitializeWithCopy into a
    /// managed buffer, so the carrier's <c>+1</c> must still be released. Callers guard the
    /// non-Optional and reference-inner shapes before calling; this returns false for those.
    /// </remarks>
    public static bool WidenDestroyForOptionalPayload(TypeSpec returnSpec, ITypeDatabase typeDatabase)
    {
        if (!WrapperValidation.IsOptionalType(returnSpec))
            return false;

        var innerSpec = MarshallingHelpers.UnwrapOptionalTypeSpec(returnSpec);
        if (innerSpec == null || !typeDatabase.TryGetTypeRecord(innerSpec, out var innerRecord))
            return false;

        bool innerIsFrozenAsClass = MarshallingHelpers.IsFrozenStructProjectedAsClass(innerRecord);
        bool innerIsNonFrozenStruct = innerRecord.Kind == TypeRecordKind.Struct
            && !MarshallingHelpers.IsTypeFrozen(innerRecord);
        bool innerIsComplexEnum = innerRecord.Kind == TypeRecordKind.Enum
            && !innerRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
        return innerIsFrozenAsClass || innerIsNonFrozenStruct || innerIsComplexEnum;
    }
}
