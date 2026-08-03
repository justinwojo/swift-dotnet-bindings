// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
// The test library exports a public `Type` on purpose, to prove the generator qualifies the
// BCL names it emits. That makes a bare `Type` ambiguous here, where the reflection type is meant.
using Type = System.Type;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// A base class declares two methods that share a Swift name AND the same
/// projected C# parameter signature but differ only by Swift argument label —
/// <c>process(first value: Int32)</c> and <c>process(second value: Int32)</c>. Each is named from
/// its own argument label — <c>ProcessFirst</c> (+100) and <c>ProcessSecond</c> (+200).
///
/// The override verifier must bind a derived override to the CORRECT disambiguated slot. The
/// hard case is a derived class that overrides ONLY the second overload: its own class body has a
/// single, uncontested <c>process</c>, so a naive name recompute yields the bare <c>Process</c> and
/// matches NEITHER base slot — a silent wrong-vtable-dispatch. The fix makes such an override adopt
/// the ancestor slot's emitted name (resolved by full Swift selector, argument labels included), so
/// it correctly emits <c>override ProcessSecond</c>.
///
/// Each Swift body returns <c>value + a distinct offset</c> so dispatch through a BASE-typed
/// reference proves WHICH Swift body actually ran.
/// </summary>
public class SameModuleOverrideCollisionTests : TestBase
{
    public SameModuleOverrideCollisionTests(TestResults results) : base(results) { }

    #region Direct-reference dispatch (each class through its own static type)

    public void TestBaseDirectDispatch()
    {
        using var b = new CollisionOverrideBase();
        AssertEqual(105, b.ProcessFirst(5), "Base.ProcessFirst -> process(first:) +100");
        AssertEqual(205, b.ProcessSecond(5), "Base.ProcessSecond -> process(second:) +200");
    }

    public void TestDerivedBothDirectDispatch()
    {
        using var d = new CollisionOverrideDerivedBoth();
        AssertEqual(1105, d.ProcessFirst(5), "DerivedBoth.ProcessFirst -> process(first:) +1100");
        AssertEqual(1205, d.ProcessSecond(5), "DerivedBoth.ProcessSecond -> process(second:) +1200");
    }

    public void TestDerivedSecondOnlyDirectDispatch()
    {
        using var d = new CollisionOverrideDerivedSecondOnly();
        // ProcessFirst (first) is NOT overridden -> inherited base body (+100).
        AssertEqual(105, d.ProcessFirst(5), "DerivedSecondOnly.ProcessFirst -> inherited base process(first:) +100");
        // ProcessSecond (second) IS overridden (+2200).
        AssertEqual(2205, d.ProcessSecond(5), "DerivedSecondOnly.ProcessSecond -> overridden process(second:) +2200");
    }

    /// <summary>
    /// Scenario C — derived overrides ONLY <c>process(second:)</c> (so it adopts the base
    /// <c>ProcessSecond</c> slot) AND declares a brand-new <c>processSecond(_:)</c> whose own NATURAL
    /// projected C# name is ALSO <c>ProcessSecond</c>. The adopted override must keep that slot; the
    /// unrelated new sibling, carrying no label, escalates to the type rung
    /// (<c>ProcessSecondWithInt32</c>). Pre-fix the dedup set reserved only the override's locally
    /// computed name and BOTH emitted <c>ProcessSecond</c> → CS0111, so the whole binding failed to
    /// compile. Distinct offsets prove each call reaches the right Swift body.
    /// </summary>
    public void TestDerivedSecondPlusSiblingDirectDispatch()
    {
        using var d = new CollisionOverrideDerivedSecondPlusSibling();
        // ProcessFirst (first) is NOT overridden -> inherited base body (+100).
        AssertEqual(105, d.ProcessFirst(5), "DerivedSecondPlusSibling.ProcessFirst -> inherited base process(first:) +100");
        // ProcessSecond (the ADOPTED slot) IS the overridden process(second:) (+3200).
        AssertEqual(3205, d.ProcessSecond(5),
            "DerivedSecondPlusSibling.ProcessSecond -> overridden process(second:) +3200 (adopted slot)");
        // ProcessSecondWithInt32 (the escalated NEW sibling) -> processSecond(_:) (+3300).
        AssertEqual(3305, d.ProcessSecondWithInt32(5),
            "DerivedSecondPlusSibling.ProcessSecondWithInt32 -> new processSecond(_:) +3300 (escalated off the adopted slot)");
    }

    /// <summary>
    /// Scenario D — derived overrides ONLY <c>process(second:)</c> (adopts base <c>ProcessSecond</c>) and
    /// gives the parameter a NON-mappable default (a function call), forcing the generator to
    /// synthesize a zero-arg convenience overload. That trimmed overload must ALSO emit under the
    /// adopted name — <c>ProcessSecond()</c>, not a recomputed bare <c>Process()</c>. The convenience
    /// surface lets Swift supply the default (<c>defaultSecondProcessValue()</c> = 9), so it returns
    /// 9 + 4200.
    /// </summary>
    public void TestDerivedSecondDefaultedDirectDispatch()
    {
        using var d = new CollisionOverrideDerivedSecondDefaulted();
        // ProcessFirst (first) is NOT overridden -> inherited base body (+100).
        AssertEqual(105, d.ProcessFirst(5), "DerivedSecondDefaulted.ProcessFirst -> inherited base process(first:) +100");
        // Explicit arg through the overridden second slot (+4200).
        AssertEqual(4210, d.ProcessSecond(10), "DerivedSecondDefaulted.ProcessSecond(10) -> overridden process(second:) +4200");
        // Zero-arg convenience overload: Swift supplies defaultSecondProcessValue() = 9, then +4200.
        AssertEqual(4209, d.ProcessSecond(),
            "DerivedSecondDefaulted.ProcessSecond() -> trimmed convenience overload supplies the Swift default (9) +4200");
    }

    /// <summary>
    /// Scenario E — the REVERSE-declaration-order twin of Scenario C: the new <c>processSecond(_:)</c>
    /// sibling is declared BEFORE the <c>override process(second:)</c>. The emitted C# shape must be
    /// identical to Scenario C regardless of source order — the adopted <c>ProcessSecond</c> slot still
    /// goes to the override and the new sibling still escalates to <c>ProcessSecondWithInt32</c>. With
    /// only the in-loop reservation this order produced two <c>ProcessSecond</c> members → CS0111; the
    /// up-front pre-reservation fixes it.
    /// </summary>
    public void TestDerivedSiblingFirstDirectDispatch()
    {
        using var d = new CollisionOverrideDerivedSiblingFirst();
        // ProcessFirst (first) is NOT overridden -> inherited base body (+100).
        AssertEqual(105, d.ProcessFirst(5), "DerivedSiblingFirst.ProcessFirst -> inherited base process(first:) +100");
        // ProcessSecond (the ADOPTED slot) IS the overridden process(second:) (+4200), regardless of decl order.
        AssertEqual(4205, d.ProcessSecond(5),
            "DerivedSiblingFirst.ProcessSecond -> overridden process(second:) +4200 (adopted slot, sibling declared first)");
        // ProcessSecondWithInt32 (the escalated NEW sibling, declared FIRST in source) -> processSecond(_:) (+4300).
        AssertEqual(4305, d.ProcessSecondWithInt32(5),
            "DerivedSiblingFirst.ProcessSecondWithInt32 -> new processSecond(_:) +4300 (escalated despite being declared first)");
    }

    #endregion

    #region Base-typed virtual dispatch (the real override proof)

    /// <summary>
    /// Scenario B — derived overrides BOTH overloads. Through a base reference, each call must
    /// reach the derived body. DerivedBoth self-computes ProcessFirst/ProcessSecond from its own two-sibling
    /// class body, so this exercises the EmittedCSharpName-parity path.
    /// </summary>
    public void TestDerivedBothVirtualDispatchThroughBase()
    {
        // Static type is the base; runtime type is the derived — so the calls go through the vtable.
        using CollisionOverrideBase b = new CollisionOverrideDerivedBoth();
        AssertEqual(1105, b.ProcessFirst(5), "base.ProcessFirst -> DerivedBoth override of process(first:)");
        AssertEqual(1205, b.ProcessSecond(5), "base.ProcessSecond -> DerivedBoth override of process(second:)");
    }

    /// <summary>
    /// Scenario A — derived overrides ONLY the second overload. This is the bug shape: pre-fix the
    /// derived emitted <c>override ProcessFirst</c> (hijacking the base's first slot), so
    /// <c>base.ProcessFirst(5)</c> returned 2205 (wrong body) and <c>base.ProcessSecond(5)</c> returned 205
    /// (override lost). Post-fix the derived emits <c>override ProcessSecond</c>:
    ///   - base.ProcessFirst(5)  -> 105   (first slot NOT overridden -> inherited base process(first:))
    ///   - base.ProcessSecond(5) -> 2205  (second slot overridden -> derived process(second:))
    /// </summary>
    public void TestDerivedSecondOnlyVirtualDispatchThroughBase()
    {
        // Static type is the base; runtime type is the derived — so the calls go through the vtable.
        using CollisionOverrideBase b = new CollisionOverrideDerivedSecondOnly();
        AssertEqual(105, b.ProcessFirst(5),
            "base.ProcessFirst must reach the INHERITED first slot (process(first:) +100), not the derived override");
        AssertEqual(2205, b.ProcessSecond(5),
            "base.ProcessSecond must reach the DERIVED override of the second slot (process(second:) +2200)");
    }

    /// <summary>
    /// Scenario C — through a base reference, <c>ProcessSecond</c> must reach the derived override of
    /// <c>process(second:)</c> (+3200). The adopted name had to win the correct vtable slot for this
    /// to dispatch; the unrelated new <c>ProcessSecondWithInt32</c> sibling (not a base member) is unreachable
    /// through the base reference and is covered by the direct-dispatch test above.
    /// </summary>
    public void TestDerivedSecondPlusSiblingVirtualDispatchThroughBase()
    {
        using CollisionOverrideBase b = new CollisionOverrideDerivedSecondPlusSibling();
        AssertEqual(105, b.ProcessFirst(5),
            "base.ProcessFirst must reach the INHERITED first slot (process(first:) +100)");
        AssertEqual(3205, b.ProcessSecond(5),
            "base.ProcessSecond must reach the DERIVED override of the second slot (process(second:) +3200)");
    }

    /// <summary>
    /// Scenario D — through a base reference, the explicit-arg <c>ProcessSecond(int)</c> must reach the
    /// derived override of <c>process(second:)</c> (+4200). The base declares no zero-arg overload, so
    /// the convenience <c>ProcessSecond()</c> is a derived-only surface (covered by direct dispatch above).
    /// </summary>
    public void TestDerivedSecondDefaultedVirtualDispatchThroughBase()
    {
        using CollisionOverrideBase b = new CollisionOverrideDerivedSecondDefaulted();
        AssertEqual(105, b.ProcessFirst(5),
            "base.ProcessFirst must reach the INHERITED first slot (process(first:) +100)");
        AssertEqual(4210, b.ProcessSecond(10),
            "base.ProcessSecond(10) must reach the DERIVED override of the second slot (process(second:) +4200)");
    }

    /// <summary>
    /// Scenario E — through a base reference, <c>ProcessSecond</c> must reach the derived override of
    /// <c>process(second:)</c> (+4200) even though the new <c>secondSlot(_:)</c> sibling is declared
    /// first. Proves the adopted name won the correct vtable slot under reverse declaration order.
    /// </summary>
    public void TestDerivedSiblingFirstVirtualDispatchThroughBase()
    {
        using CollisionOverrideBase b = new CollisionOverrideDerivedSiblingFirst();
        AssertEqual(105, b.ProcessFirst(5),
            "base.ProcessFirst must reach the INHERITED first slot (process(first:) +100)");
        AssertEqual(4205, b.ProcessSecond(5),
            "base.ProcessSecond must reach the DERIVED override of the second slot (process(second:) +4200)");
    }

    #endregion

    #region Emitted override shape (reflection)

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(CollisionOverrideBase))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(CollisionOverrideDerivedSecondOnly))]
    public void TestDerivedSecondOnlyOverridesSecondSlot()
    {
        // The second-slot override must be named ProcessSecond and declared on the derived type
        // (DeclaringType == derived), proving it adopted the ancestor's disambiguated slot name.
        var secondSlot = typeof(CollisionOverrideDerivedSecondOnly)
            .GetMethod("ProcessSecond", new[] { typeof(int) });
        AssertNotNull(secondSlot, "DerivedSecondOnly exposes ProcessSecond(int)");
        AssertEqual(
            nameof(CollisionOverrideDerivedSecondOnly),
            secondSlot!.DeclaringType!.Name,
            "ProcessSecond is declared (overridden) on DerivedSecondOnly, not merely inherited");

        // The base still exposes both distinct label-derived slots.
        AssertNotNull(
            typeof(CollisionOverrideBase).GetMethod("ProcessFirst", new[] { typeof(int) }),
            "Base exposes ProcessFirst(int) (first slot)");
        AssertNotNull(
            typeof(CollisionOverrideBase).GetMethod("ProcessSecond", new[] { typeof(int) }),
            "Base exposes ProcessSecond(int) (second slot)");

        // And nothing on the base carries a rank: no bare name (both siblings are labelled) and no
        // numeric suffix.
        AssertNull(
            typeof(CollisionOverrideBase).GetMethod("Process", new[] { typeof(int) }),
            "no bare Process — both siblings are labelled, so the bare name has no owner");
        AssertNull(
            typeof(CollisionOverrideBase).GetMethod("Process2", new[] { typeof(int) }),
            "no Process2 — numeric suffixes are not part of the public surface");
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(CollisionOverrideDerivedSecondPlusSibling))]
    public void TestDerivedSecondPlusSiblingEmitsBothSlots()
    {
        // The adopted override keeps the base's ProcessSecond slot...
        var secondSlot = typeof(CollisionOverrideDerivedSecondPlusSibling)
            .GetMethod("ProcessSecond", new[] { typeof(int) });
        AssertNotNull(secondSlot, "DerivedSecondPlusSibling exposes ProcessSecond(int) (adopted override slot)");
        AssertEqual(
            nameof(CollisionOverrideDerivedSecondPlusSibling),
            secondSlot!.DeclaringType!.Name,
            "ProcessSecond is declared (overridden) on DerivedSecondPlusSibling");

        // ...and the unrelated new sibling escalated to a fresh, non-colliding type-derived name.
        var siblingSlot = typeof(CollisionOverrideDerivedSecondPlusSibling)
            .GetMethod("ProcessSecondWithInt32", new[] { typeof(int) });
        AssertNotNull(siblingSlot, "DerivedSecondPlusSibling exposes ProcessSecondWithInt32(int) (disambiguated new sibling)");
        AssertEqual(
            nameof(CollisionOverrideDerivedSecondPlusSibling),
            siblingSlot!.DeclaringType!.Name,
            "ProcessSecondWithInt32 is declared on DerivedSecondPlusSibling (new method, not inherited)");
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(CollisionOverrideDerivedSecondDefaulted))]
    public void TestDerivedSecondDefaultedConvenienceOverloadAdoptsName()
    {
        // The trimmed zero-arg convenience overload must adopt the disambiguated name (ProcessSecond()),
        // NOT a recomputed bare Process(). A zero-arg Process() would mean the overload emitter dropped
        // the adopted name and paired the convenience surface with no base slot at all.
        AssertNotNull(
            typeof(CollisionOverrideDerivedSecondDefaulted).GetMethod("ProcessSecond", Type.EmptyTypes),
            "DerivedSecondDefaulted exposes the zero-arg convenience overload ProcessSecond()");
        AssertNull(
            typeof(CollisionOverrideDerivedSecondDefaulted).GetMethod("Process", Type.EmptyTypes),
            "No bare Process() convenience overload — the adopted name was not dropped");
        AssertNull(
            typeof(CollisionOverrideDerivedSecondDefaulted).GetMethod("ProcessFirst", Type.EmptyTypes),
            "No ProcessFirst() convenience overload — the convenience surface belongs to the SECOND slot");
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(CollisionOverrideDerivedSiblingFirst))]
    public void TestDerivedSiblingFirstEmitsBothSlots()
    {
        // Reverse declaration order must emit the same two slots as Scenario C: the adopted override
        // keeps ProcessSecond and the (source-first) new sibling escalates to ProcessSecondWithInt32 —
        // no duplicate, no CS0111, order-independent.
        var secondSlot = typeof(CollisionOverrideDerivedSiblingFirst)
            .GetMethod("ProcessSecond", new[] { typeof(int) });
        AssertNotNull(secondSlot, "DerivedSiblingFirst exposes ProcessSecond(int) (adopted override slot)");
        AssertEqual(
            nameof(CollisionOverrideDerivedSiblingFirst),
            secondSlot!.DeclaringType!.Name,
            "ProcessSecond is declared (overridden) on DerivedSiblingFirst");

        var siblingSlot = typeof(CollisionOverrideDerivedSiblingFirst)
            .GetMethod("ProcessSecondWithInt32", new[] { typeof(int) });
        AssertNotNull(siblingSlot, "DerivedSiblingFirst exposes ProcessSecondWithInt32(int) (disambiguated new sibling declared first)");
        AssertEqual(
            nameof(CollisionOverrideDerivedSiblingFirst),
            siblingSlot!.DeclaringType!.Name,
            "ProcessSecondWithInt32 is declared on DerivedSiblingFirst (new method, not inherited)");
    }

    #endregion
}
