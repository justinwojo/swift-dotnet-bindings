// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Nullable-reference-erasure override collision. Complements
/// <see cref="SameModuleOverrideCollisionTests"/>, which uses the LABEL-based collision trigger.
/// This exercises the OTHER trigger for the secondary projected-C# dedup: a non-optional class
/// parameter and an optional class parameter erase to the SAME projected C# nullable-reference
/// signature. <c>transform(_ w: RefBox)</c> and <c>transform(_ w: RefBox?)</c> both project to
/// <c>Transform(RefBox)</c>. Neither overload carries a label, so there is nothing to disambiguate
/// them BY label; the group falls through to the type rung and each is named from its own SWIFT
/// parameter type — <c>TransformWithRefBox</c> (+100) and <c>TransformWithOptionalRefBox</c> (+200).
/// The Swift types are what distinguish them; the projected C# types are identical by construction,
/// which is exactly why the group collided.
///
/// Mapping verified against the generated P/Invokes (signatures and wrapper bodies):
///   - <c>TransformWithRefBox(RefBox w)</c>   -> Swift <c>transform(_ w: RefBox)</c>  -> w.value + 100
///   - <c>TransformWithOptionalRefBox(RefBox? w)</c> -> Swift <c>transform(_ w: RefBox?)</c> -> (w?.value ?? 0) + 200
///
/// Scenario A is the hard case: <c>NullableRefOverrideDerived</c> overrides ONLY the optional
/// overload (+2200). Its own class body has a single, uncontested <c>transform</c>, so a naive name
/// recompute yields the bare <c>Transform</c> and matches neither base slot — a silent
/// wrong-vtable-dispatch. The fix makes the override adopt the ancestor slot's emitted name
/// (resolved by full Swift selector, param types included), so it correctly emits
/// <c>override TransformWithOptionalRefBox</c>. Dispatch through a BASE-typed reference proves WHICH
/// Swift body actually ran.
/// </summary>
public class NullableRefOverrideCollisionTests : TestBase
{
    public NullableRefOverrideCollisionTests(TestResults results) : base(results) { }

    #region Direct-reference dispatch (each class through its own static type)

    public void TestBaseDirectDispatch()
    {
        using var b = new NullableRefOverrideBase();
        using var box = new RefBox(10);
        AssertEqual(110, b.TransformWithRefBox(box), "Base.TransformWithRefBox -> transform(_ w: RefBox) = value + 100");
        AssertEqual(210, b.TransformWithOptionalRefBox(box), "Base.TransformWithOptionalRefBox -> transform(_ w: RefBox?) = value + 200");
        AssertEqual(200, b.TransformWithOptionalRefBox(null), "Base.TransformWithOptionalRefBox(nil) -> (nil ?? 0) + 200");
    }

    public void TestDerivedDirectDispatch()
    {
        using var d = new NullableRefOverrideDerived();
        using var box = new RefBox(10);
        // TransformWithRefBox (first/non-optional) is NOT overridden -> inherited base body (+100).
        AssertEqual(110, d.TransformWithRefBox(box), "Derived.TransformWithRefBox -> inherited base transform(_ w: RefBox) = value + 100");
        // TransformWithOptionalRefBox (second/optional) IS overridden (+2200).
        AssertEqual(2210, d.TransformWithOptionalRefBox(box), "Derived.TransformWithOptionalRefBox -> overridden transform(_ w: RefBox?) = value + 2200");
        AssertEqual(2200, d.TransformWithOptionalRefBox(null), "Derived.TransformWithOptionalRefBox(nil) -> (nil ?? 0) + 2200");
    }

    #endregion

    #region Base-typed virtual dispatch (the real override proof)

    /// <summary>
    /// The bug shape: pre-fix the derived emitted <c>override TransformWithRefBox</c> (hijacking the base's
    /// first/non-optional slot), so <c>base.TransformWithRefBox(box)</c> returned 2210 (wrong body) and
    /// <c>base.TransformWithOptionalRefBox(box)</c> returned 210 (override lost). Post-fix the derived emits
    /// <c>override TransformWithOptionalRefBox</c>:
    ///   - base.TransformWithRefBox(box)   -> 110   (first slot NOT overridden -> inherited base transform(_:))
    ///   - base.TransformWithOptionalRefBox(box)  -> 2210  (second slot overridden -> derived transform(_:?))
    /// </summary>
    public void TestDerivedVirtualDispatchThroughBase()
    {
        // Static type is the base; runtime type is the derived — calls go through the vtable.
        using NullableRefOverrideBase b = new NullableRefOverrideDerived();
        using var box = new RefBox(10);
        AssertEqual(110, b.TransformWithRefBox(box),
            "base.TransformWithRefBox must reach the INHERITED first slot (transform(_ w: RefBox) +100), not the derived override");
        AssertEqual(2210, b.TransformWithOptionalRefBox(box),
            "base.TransformWithOptionalRefBox must reach the DERIVED override of the second slot (transform(_ w: RefBox?) +2200)");
        AssertEqual(2200, b.TransformWithOptionalRefBox(null),
            "base.TransformWithOptionalRefBox(nil) must reach the DERIVED override ((nil ?? 0) + 2200)");
    }

    #endregion

    #region Emitted override shape (reflection)

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(NullableRefOverrideBase))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(NullableRefOverrideDerived))]
    public void TestDerivedOverridesOptionalSlot()
    {
        // The override must be named TransformWithOptionalRefBox and declared on the derived type
        // (DeclaringType == derived), proving it adopted the ancestor slot's emitted name rather than
        // recomputing a bare Transform that matches no base slot.
        var optionalSlot = typeof(NullableRefOverrideDerived)
            .GetMethod("TransformWithOptionalRefBox", new[] { typeof(RefBox) });
        AssertNotNull(optionalSlot, "Derived exposes TransformWithOptionalRefBox(RefBox?)");
        AssertEqual(
            nameof(NullableRefOverrideDerived),
            optionalSlot!.DeclaringType!.Name,
            "TransformWithOptionalRefBox is declared (overridden) on NullableRefOverrideDerived, not merely inherited");

        // The derived must NOT declare its own TransformWithRefBox — that slot is inherited, not hijacked.
        var nonOptionalSlot = typeof(NullableRefOverrideDerived)
            .GetMethod("TransformWithRefBox", new[] { typeof(RefBox) });
        AssertNotNull(nonOptionalSlot, "Derived still exposes TransformWithRefBox(RefBox) (inherited)");
        AssertEqual(
            nameof(NullableRefOverrideBase),
            nonOptionalSlot!.DeclaringType!.Name,
            "TransformWithRefBox is declared on the BASE — the override did not hijack it");

        // The base exposes both distinct type-derived slots, and neither is a bare/numeric name.
        AssertNotNull(
            typeof(NullableRefOverrideBase).GetMethod("TransformWithRefBox", new[] { typeof(RefBox) }),
            "Base exposes TransformWithRefBox(RefBox)");
        AssertNotNull(
            typeof(NullableRefOverrideBase).GetMethod("TransformWithOptionalRefBox", new[] { typeof(RefBox) }),
            "Base exposes TransformWithOptionalRefBox(RefBox?)");
        AssertNull(
            typeof(NullableRefOverrideBase).GetMethod("Transform2", new[] { typeof(RefBox) }),
            "no Transform2 — numeric suffixes are not part of the public surface");
    }

    #endregion
}
