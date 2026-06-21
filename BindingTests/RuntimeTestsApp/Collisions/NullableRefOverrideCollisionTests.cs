// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Nullable-reference-erasure override collision. Complements
/// <see cref="SameModuleOverrideCollisionTests"/>, which uses the LABEL-based collision trigger.
/// This exercises the OTHER B15 trigger: a non-optional class parameter and an optional class
/// parameter erase to the SAME projected C# nullable-reference signature.
/// <c>transform(_ w: RefBox)</c> and <c>transform(_ w: RefBox?)</c> both project to
/// <c>Transform(RefBox)</c>, so the overload group is content-sort disambiguated by full Swift
/// signature: the optional overload (<c>Optional&lt;RefBox&gt;</c>) sorts first and keeps the
/// natural <c>Transform</c> (+200); the non-optional overload takes the suffixed <c>Transform2</c>
/// (+100).
///
/// Mapping verified against the generated P/Invokes (signatures and wrapper bodies):
///   - <c>Transform(RefBox? w)</c> -> Swift <c>transform(_ w: RefBox?)</c> -> (w?.value ?? 0) + 200
///   - <c>Transform2(RefBox w)</c> -> Swift <c>transform(_ w: RefBox)</c>  -> w.value + 100
///
/// The hard case: <c>NullableRefOverrideDerived</c> overrides ONLY the non-optional overload — i.e.
/// the SUFFIXED slot (+2200). Its own class body has a single <c>transform</c>, so a naive name
/// recompute yields <c>Transform</c> and hijacks the base's natural-named (optional) slot — a silent
/// wrong-vtable-dispatch. The fix makes the override adopt the ancestor slot's emitted name (resolved
/// by full Swift selector, param types included), so it correctly emits <c>override Transform2</c>.
/// Dispatch through a BASE-typed reference proves WHICH Swift body actually ran.
/// </summary>
public class NullableRefOverrideCollisionTests : TestBase
{
    public NullableRefOverrideCollisionTests(TestResults results) : base(results) { }

    #region Direct-reference dispatch (each class through its own static type)

    public void TestBaseDirectDispatch()
    {
        using var b = new NullableRefOverrideBase();
        using var box = new RefBox(10);
        AssertEqual(210, b.Transform(box), "Base.Transform -> transform(_ w: RefBox?) = value + 200");
        AssertEqual(200, b.Transform(null), "Base.Transform(nil) -> (nil ?? 0) + 200");
        AssertEqual(110, b.Transform2(box), "Base.Transform2 -> transform(_ w: RefBox) = value + 100");
    }

    public void TestDerivedDirectDispatch()
    {
        using var d = new NullableRefOverrideDerived();
        using var box = new RefBox(10);
        // Transform (optional, natural slot) is NOT overridden -> inherited base body (+200).
        AssertEqual(210, d.Transform(box), "Derived.Transform -> inherited base transform(_ w: RefBox?) = value + 200");
        AssertEqual(200, d.Transform(null), "Derived.Transform(nil) -> inherited (nil ?? 0) + 200");
        // Transform2 (non-optional, suffixed slot) IS overridden (+2200).
        AssertEqual(2210, d.Transform2(box), "Derived.Transform2 -> overridden transform(_ w: RefBox) = value + 2200");
    }

    #endregion

    #region Base-typed virtual dispatch (the real override proof)

    /// <summary>
    /// The bug shape: pre-fix the derived emitted <c>override Transform</c> (hijacking the base's
    /// natural-named/optional slot), so <c>base.Transform(box)</c> returned 2210 (wrong body) and
    /// <c>base.Transform2(box)</c> returned 110 (override lost). Post-fix the derived emits
    /// <c>override Transform2</c>:
    ///   - base.Transform(box)   -> 210   (natural slot NOT overridden -> inherited base transform(_:?))
    ///   - base.Transform2(box)  -> 2210  (suffixed slot overridden -> derived transform(_:))
    /// </summary>
    public void TestDerivedVirtualDispatchThroughBase()
    {
        // Static type is the base; runtime type is the derived — calls go through the vtable.
        using NullableRefOverrideBase b = new NullableRefOverrideDerived();
        using var box = new RefBox(10);
        AssertEqual(210, b.Transform(box),
            "base.Transform must reach the INHERITED natural slot (transform(_ w: RefBox?) +200), not the derived override");
        AssertEqual(200, b.Transform(null),
            "base.Transform(nil) must reach the inherited natural slot ((nil ?? 0) + 200)");
        AssertEqual(2210, b.Transform2(box),
            "base.Transform2 must reach the DERIVED override of the suffixed slot (transform(_ w: RefBox) +2200)");
    }

    #endregion

    #region Emitted override shape (reflection)

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(NullableRefOverrideBase))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(NullableRefOverrideDerived))]
    public void TestDerivedOverridesTransform2Slot()
    {
        // The override must be named Transform2 and declared on the derived type (DeclaringType ==
        // derived), proving it adopted the ancestor's suffixed slot name rather than hijacking Transform.
        // (Nullable reference types erase at runtime, so both overloads' RefBox? / RefBox params reflect
        // as typeof(RefBox); the discriminator is the C# NAME and the declaring type, not nullability.)
        var transform2 = typeof(NullableRefOverrideDerived)
            .GetMethod("Transform2", new[] { typeof(RefBox) });
        AssertNotNull(transform2, "Derived exposes Transform2(RefBox)");
        AssertEqual(
            nameof(NullableRefOverrideDerived),
            transform2!.DeclaringType!.Name,
            "Transform2 is declared (overridden) on NullableRefOverrideDerived, not merely inherited");

        // The derived must NOT declare its own Transform (natural slot) — that slot is inherited, not hijacked.
        var transform = typeof(NullableRefOverrideDerived)
            .GetMethod("Transform", new[] { typeof(RefBox) });
        AssertNotNull(transform, "Derived still exposes Transform(RefBox?) (inherited)");
        AssertEqual(
            nameof(NullableRefOverrideBase),
            transform!.DeclaringType!.Name,
            "Transform (natural slot) is declared on the BASE — the override did not hijack it");

        // The base exposes both distinct collision-suffixed slots.
        AssertNotNull(
            typeof(NullableRefOverrideBase).GetMethod("Transform", new[] { typeof(RefBox) }),
            "Base exposes Transform(RefBox?) (natural slot)");
        AssertNotNull(
            typeof(NullableRefOverrideBase).GetMethod("Transform2", new[] { typeof(RefBox) }),
            "Base exposes Transform2(RefBox) (suffixed slot)");
    }

    #endregion
}
