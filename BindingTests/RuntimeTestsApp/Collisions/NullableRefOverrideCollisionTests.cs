// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// P1-21 (Scenario A — nullable-reference-erasure override collision). Complements
/// <see cref="SameModuleOverrideCollisionTests"/>, which uses the LABEL-based collision trigger.
/// This exercises the OTHER B15 trigger: a non-optional class parameter and an optional class
/// parameter erase to the SAME projected C# nullable-reference signature.
/// <c>transform(_ w: RefBox)</c> and <c>transform(_ w: RefBox?)</c> both project to
/// <c>Transform(RefBox)</c>, so B15 disambiguates them as <c>Transform</c> (+100, non-optional) and
/// <c>Transform2</c> (+200, optional).
///
/// Mapping verified against the generated P/Invokes (signatures and wrapper bodies):
///   - <c>Transform(RefBox w)</c>   -> Swift <c>transform(_ w: RefBox)</c>  -> w.value + 100
///   - <c>Transform2(RefBox? w)</c> -> Swift <c>transform(_ w: RefBox?)</c> -> (w?.value ?? 0) + 200
///
/// Scenario A is the hard case: <c>NullableRefOverrideDerived</c> overrides ONLY the second (optional)
/// overload (+2200). Its own class body has a single <c>transform</c>, so a naive name recompute
/// yields <c>Transform</c> and hijacks the base's FIRST slot — a silent wrong-vtable-dispatch. The fix
/// makes the override adopt the ancestor slot's emitted name (resolved by full Swift selector, param
/// types included), so it correctly emits <c>override Transform2</c>. Dispatch through a BASE-typed
/// reference proves WHICH Swift body actually ran.
/// </summary>
public class NullableRefOverrideCollisionTests : TestBase
{
    public NullableRefOverrideCollisionTests(TestResults results) : base(results) { }

    #region Direct-reference dispatch (each class through its own static type)

    public void TestBaseDirectDispatch()
    {
        using var b = new NullableRefOverrideBase();
        using var box = new RefBox(10);
        AssertEqual(110, b.Transform(box), "Base.Transform -> transform(_ w: RefBox) = value + 100");
        AssertEqual(210, b.Transform2(box), "Base.Transform2 -> transform(_ w: RefBox?) = value + 200");
        AssertEqual(200, b.Transform2(null), "Base.Transform2(nil) -> (nil ?? 0) + 200");
    }

    public void TestDerivedDirectDispatch()
    {
        using var d = new NullableRefOverrideDerived();
        using var box = new RefBox(10);
        // Transform (first/non-optional) is NOT overridden -> inherited base body (+100).
        AssertEqual(110, d.Transform(box), "Derived.Transform -> inherited base transform(_ w: RefBox) = value + 100");
        // Transform2 (second/optional) IS overridden (+2200).
        AssertEqual(2210, d.Transform2(box), "Derived.Transform2 -> overridden transform(_ w: RefBox?) = value + 2200");
        AssertEqual(2200, d.Transform2(null), "Derived.Transform2(nil) -> (nil ?? 0) + 2200");
    }

    #endregion

    #region Base-typed virtual dispatch (the real override proof)

    /// <summary>
    /// The bug shape: pre-fix the derived emitted <c>override Transform</c> (hijacking the base's
    /// first/non-optional slot), so <c>base.Transform(box)</c> returned 2210 (wrong body) and
    /// <c>base.Transform2(box)</c> returned 210 (override lost). Post-fix the derived emits
    /// <c>override Transform2</c>:
    ///   - base.Transform(box)   -> 110   (first slot NOT overridden -> inherited base transform(_:))
    ///   - base.Transform2(box)  -> 2210  (second slot overridden -> derived transform(_:?))
    /// </summary>
    public void TestDerivedVirtualDispatchThroughBase()
    {
        // Static type is the base; runtime type is the derived — calls go through the vtable.
        using NullableRefOverrideBase b = new NullableRefOverrideDerived();
        using var box = new RefBox(10);
        AssertEqual(110, b.Transform(box),
            "base.Transform must reach the INHERITED first slot (transform(_ w: RefBox) +100), not the derived override");
        AssertEqual(2210, b.Transform2(box),
            "base.Transform2 must reach the DERIVED override of the second slot (transform(_ w: RefBox?) +2200)");
        AssertEqual(2200, b.Transform2(null),
            "base.Transform2(nil) must reach the DERIVED override ((nil ?? 0) + 2200)");
    }

    #endregion

    #region Emitted override shape (reflection)

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(NullableRefOverrideBase))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(NullableRefOverrideDerived))]
    public void TestDerivedOverridesTransform2Slot()
    {
        // The override must be named Transform2 and declared on the derived type (DeclaringType ==
        // derived), proving it adopted the ancestor's suffixed slot name rather than hijacking Transform.
        var transform2 = typeof(NullableRefOverrideDerived)
            .GetMethod("Transform2", new[] { typeof(RefBox) });
        AssertNotNull(transform2, "Derived exposes Transform2(RefBox?)");
        AssertEqual(
            nameof(NullableRefOverrideDerived),
            transform2!.DeclaringType!.Name,
            "Transform2 is declared (overridden) on NullableRefOverrideDerived, not merely inherited");

        // The derived must NOT declare its own Transform (first slot) — that slot is inherited, not hijacked.
        var transform = typeof(NullableRefOverrideDerived)
            .GetMethod("Transform", new[] { typeof(RefBox) });
        AssertNotNull(transform, "Derived still exposes Transform(RefBox) (inherited)");
        AssertEqual(
            nameof(NullableRefOverrideBase),
            transform!.DeclaringType!.Name,
            "Transform (first slot) is declared on the BASE — the override did not hijack it");

        // The base exposes both distinct collision-suffixed slots.
        AssertNotNull(
            typeof(NullableRefOverrideBase).GetMethod("Transform", new[] { typeof(RefBox) }),
            "Base exposes Transform(RefBox) (first slot)");
        AssertNotNull(
            typeof(NullableRefOverrideBase).GetMethod("Transform2", new[] { typeof(RefBox) }),
            "Base exposes Transform2(RefBox?) (second slot)");
    }

    #endregion
}
