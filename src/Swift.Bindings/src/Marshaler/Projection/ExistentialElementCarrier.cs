// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Single source of truth for the C#→Swift PARAMETER/WRITE-direction element marshalling of an
/// <c>any P</c> existential that lives inside a Swift collection / container carrier
/// (<c>SwiftArray</c>, <c>SwiftSet</c>, <c>SwiftDictionary</c> value, <c>SwiftOptional</c> element,
/// tuple element). Every such carrier has <b>owned element semantics</b> — its value-witness table
/// runs <i>destroy</i> on each element at teardown, and the <c>__owned</c> append/store consumes one
/// +1. So the per-element container MUST own an independent +1: minted via
/// <see cref="ExistentialProjection.GetArrayElementCarrierConversion"/>
/// (<c>CreateOwnedExistential1</c>/<c>CreateOwnedClassCarrier</c>), paired with the carrier element
/// type <see cref="ExistentialProjection.ArrayElementCarrierType"/> so the slot stride agrees
/// (16-byte <c>ClassExistentialContainer1</c> for a class-bound single-protocol existential, the
/// 40-byte opaque <c>ExistentialContainer1</c> otherwise).
///
/// The bare borrowed leaf <see cref="ExistentialProjection.GetParameterElementConversion"/> aliases
/// the proxy's sole construction +1 (R0); inside an owned-semantics carrier that is a double-fault —
/// the <c>__owned</c> consume + VWT destroy <b>over-release</b> the proxy's only retain, and (once
/// Defect G makes proxy registration weak) a GC mid-call releases R0 → use-after-free. It is correct
/// ONLY for genuinely borrowed scalar reads (non-owned forward returns, scalar receiver-callback args).
///
/// <see cref="ArrayProjection"/> and <see cref="DictionaryProjection"/> (value slot) established this
/// pairing; <see cref="SetProjection"/>, <see cref="OptionalProjection"/>, <see cref="TupleProjection"/>
/// and the property/subscript setter visitors route through here so the convention can never drift
/// across the collection projections (the "N identical copies" hazard). The name keeps the historical
/// "ArrayElementCarrier" terminology — it is the existential <i>collection</i>-element carrier, not
/// array-specific.
/// </summary>
internal static class ExistentialElementCarrier
{
    /// <summary>
    /// Owned (+1) carrier conversion for an existential element; the projection's own borrowed
    /// parameter conversion for every non-existential element (a no-op passthrough there).
    /// </summary>
    internal static string? ParamConversion(ITypeProjection element, string elementVar) =>
        element is ExistentialProjection existElem
            ? existElem.GetArrayElementCarrierConversion(elementVar)
            : element.GetParameterElementConversion(elementVar);

    /// <summary>
    /// The Swift-container element generic type for the PARAMETER/WRITE direction: the existential's
    /// stride-correct carrier type, or the caller's non-existential fallback (the element type the
    /// site already used — <c>SwiftContainerGenericType</c> for the Array/Set FromEnumerable container,
    /// <c>MarshalFromSwiftType</c> for the setter-visitor containers).
    /// </summary>
    internal static string CarrierType(ITypeProjection element, string nonExistentialFallback) =>
        element is ExistentialProjection existElem
            ? existElem.ArrayElementCarrierType
            : nonExistentialFallback;
}
