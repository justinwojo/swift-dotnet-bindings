// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// A base class declares two methods that share a Swift name AND the same
/// projected C# parameter signature but differ only by Swift argument label —
/// <c>process(first value: Int32)</c> and <c>process(second value: Int32)</c>. B15 disambiguates
/// them as <c>Process</c> (first, +100) and <c>Process2</c> (second, +200).
///
/// The override verifier must bind a derived override to the CORRECT collision-suffixed slot. The
/// hard case is a derived class that overrides ONLY the second overload: its own class body has a
/// single <c>process</c>, so a naive name recompute yields <c>Process</c> and hijacks the base's
/// <i>first</i> slot — a silent wrong-vtable-dispatch. The fix makes such an override adopt the
/// ancestor slot's emitted name (resolved by full Swift selector, argument labels included), so it
/// correctly emits <c>override Process2</c>.
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
        AssertEqual(105, b.Process(5), "Base.Process -> process(first:) +100");
        AssertEqual(205, b.Process2(5), "Base.Process2 -> process(second:) +200");
    }

    public void TestDerivedBothDirectDispatch()
    {
        using var d = new CollisionOverrideDerivedBoth();
        AssertEqual(1105, d.Process(5), "DerivedBoth.Process -> process(first:) +1100");
        AssertEqual(1205, d.Process2(5), "DerivedBoth.Process2 -> process(second:) +1200");
    }

    public void TestDerivedSecondOnlyDirectDispatch()
    {
        using var d = new CollisionOverrideDerivedSecondOnly();
        // Process (first) is NOT overridden -> inherited base body (+100).
        AssertEqual(105, d.Process(5), "DerivedSecondOnly.Process -> inherited base process(first:) +100");
        // Process2 (second) IS overridden (+2200).
        AssertEqual(2205, d.Process2(5), "DerivedSecondOnly.Process2 -> overridden process(second:) +2200");
    }

    /// <summary>
    /// Scenario C — derived overrides ONLY <c>process(second:)</c> (so it adopts the base
    /// <c>Process2</c> slot) AND declares a brand-new <c>process2(_:)</c> whose own projected C# name
    /// is ALSO <c>Process2</c>. The adopted override must keep the <c>Process2</c> slot; the unrelated
    /// new sibling must be pushed to a fresh suffix (<c>Process22</c>). Pre-fix the dedup set reserved
    /// only the override's locally computed name and BOTH emitted <c>Process2</c> → CS0111, so the
    /// whole binding failed to compile. Distinct offsets prove each call reaches the right Swift body.
    /// </summary>
    public void TestDerivedSecondPlusSiblingDirectDispatch()
    {
        using var d = new CollisionOverrideDerivedSecondPlusSibling();
        // Process (first) is NOT overridden -> inherited base body (+100).
        AssertEqual(105, d.Process(5), "DerivedSecondPlusSibling.Process -> inherited base process(first:) +100");
        // Process2 (the ADOPTED slot) IS the overridden process(second:) (+3200).
        AssertEqual(3205, d.Process2(5),
            "DerivedSecondPlusSibling.Process2 -> overridden process(second:) +3200 (adopted slot)");
        // Process22 (the disambiguated NEW sibling) -> process2(_:) (+3300).
        AssertEqual(3305, d.Process22(5),
            "DerivedSecondPlusSibling.Process22 -> new process2(_:) +3300 (pushed to a fresh suffix)");
    }

    /// <summary>
    /// Scenario D — derived overrides ONLY <c>process(second:)</c> (adopts base <c>Process2</c>) and
    /// gives the parameter a NON-mappable default (a function call), forcing the generator to
    /// synthesize a zero-arg convenience overload. That trimmed overload must ALSO emit under the
    /// adopted name — <c>Process2()</c>, not the recomputed bare <c>Process()</c>. The convenience
    /// surface lets Swift supply the default (<c>defaultSecondProcessValue()</c> = 9), so it returns
    /// 9 + 4200.
    /// </summary>
    public void TestDerivedSecondDefaultedDirectDispatch()
    {
        using var d = new CollisionOverrideDerivedSecondDefaulted();
        // Process (first) is NOT overridden -> inherited base body (+100).
        AssertEqual(105, d.Process(5), "DerivedSecondDefaulted.Process -> inherited base process(first:) +100");
        // Explicit arg through the overridden second slot (+4200).
        AssertEqual(4210, d.Process2(10), "DerivedSecondDefaulted.Process2(10) -> overridden process(second:) +4200");
        // Zero-arg convenience overload: Swift supplies defaultSecondProcessValue() = 9, then +4200.
        AssertEqual(4209, d.Process2(),
            "DerivedSecondDefaulted.Process2() -> trimmed convenience overload supplies the Swift default (9) +4200");
    }

    /// <summary>
    /// Scenario E — the REVERSE-declaration-order twin of Scenario C: the new <c>process2(_:)</c>
    /// sibling is declared BEFORE the <c>override process(second:)</c>. The emitted C# shape must be
    /// identical to Scenario C regardless of source order — the adopted <c>Process2</c> slot still
    /// goes to the override and the new sibling is still pushed to <c>Process22</c>. With only the
    /// in-loop reservation this order produced two <c>Process2</c> members → CS0111; the up-front
    /// pre-reservation fixes it.
    /// </summary>
    public void TestDerivedSiblingFirstDirectDispatch()
    {
        using var d = new CollisionOverrideDerivedSiblingFirst();
        // Process (first) is NOT overridden -> inherited base body (+100).
        AssertEqual(105, d.Process(5), "DerivedSiblingFirst.Process -> inherited base process(first:) +100");
        // Process2 (the ADOPTED slot) IS the overridden process(second:) (+4200), regardless of decl order.
        AssertEqual(4205, d.Process2(5),
            "DerivedSiblingFirst.Process2 -> overridden process(second:) +4200 (adopted slot, sibling declared first)");
        // Process22 (the disambiguated NEW sibling, declared FIRST in source) -> process2(_:) (+4300).
        AssertEqual(4305, d.Process22(5),
            "DerivedSiblingFirst.Process22 -> new process2(_:) +4300 (pushed to a fresh suffix despite being declared first)");
    }

    #endregion

    #region Base-typed virtual dispatch (the real override proof)

    /// <summary>
    /// Scenario B — derived overrides BOTH overloads. Through a base reference, each call must
    /// reach the derived body. DerivedBoth self-computes Process/Process2 from its own two-sibling
    /// class body, so this exercises the EmittedCSharpName-parity path.
    /// </summary>
    public void TestDerivedBothVirtualDispatchThroughBase()
    {
        // Static type is the base; runtime type is the derived — so the calls go through the vtable.
        using CollisionOverrideBase b = new CollisionOverrideDerivedBoth();
        AssertEqual(1105, b.Process(5), "base.Process -> DerivedBoth override of process(first:)");
        AssertEqual(1205, b.Process2(5), "base.Process2 -> DerivedBoth override of process(second:)");
    }

    /// <summary>
    /// Scenario A — derived overrides ONLY the second overload. This is the bug shape: pre-fix the
    /// derived emitted <c>override Process</c> (hijacking the base's first slot), so
    /// <c>base.Process(5)</c> returned 2205 (wrong body) and <c>base.Process2(5)</c> returned 205
    /// (override lost). Post-fix the derived emits <c>override Process2</c>:
    ///   - base.Process(5)  -> 105   (first slot NOT overridden -> inherited base process(first:))
    ///   - base.Process2(5) -> 2205  (second slot overridden -> derived process(second:))
    /// </summary>
    public void TestDerivedSecondOnlyVirtualDispatchThroughBase()
    {
        // Static type is the base; runtime type is the derived — so the calls go through the vtable.
        using CollisionOverrideBase b = new CollisionOverrideDerivedSecondOnly();
        AssertEqual(105, b.Process(5),
            "base.Process must reach the INHERITED first slot (process(first:) +100), not the derived override");
        AssertEqual(2205, b.Process2(5),
            "base.Process2 must reach the DERIVED override of the second slot (process(second:) +2200)");
    }

    /// <summary>
    /// Scenario C — through a base reference, <c>Process2</c> must reach the derived override of
    /// <c>process(second:)</c> (+3200). The adopted name had to win the correct vtable slot for this
    /// to dispatch; the unrelated new <c>Process22</c> sibling (not a base member) is unreachable
    /// through the base reference and is covered by the direct-dispatch test above.
    /// </summary>
    public void TestDerivedSecondPlusSiblingVirtualDispatchThroughBase()
    {
        using CollisionOverrideBase b = new CollisionOverrideDerivedSecondPlusSibling();
        AssertEqual(105, b.Process(5),
            "base.Process must reach the INHERITED first slot (process(first:) +100)");
        AssertEqual(3205, b.Process2(5),
            "base.Process2 must reach the DERIVED override of the second slot (process(second:) +3200)");
    }

    /// <summary>
    /// Scenario D — through a base reference, the explicit-arg <c>Process2(int)</c> must reach the
    /// derived override of <c>process(second:)</c> (+4200). The base declares no zero-arg overload, so
    /// the convenience <c>Process2()</c> is a derived-only surface (covered by direct dispatch above).
    /// </summary>
    public void TestDerivedSecondDefaultedVirtualDispatchThroughBase()
    {
        using CollisionOverrideBase b = new CollisionOverrideDerivedSecondDefaulted();
        AssertEqual(105, b.Process(5),
            "base.Process must reach the INHERITED first slot (process(first:) +100)");
        AssertEqual(4210, b.Process2(10),
            "base.Process2(10) must reach the DERIVED override of the second slot (process(second:) +4200)");
    }

    /// <summary>
    /// Scenario E — through a base reference, <c>Process2</c> must reach the derived override of
    /// <c>process(second:)</c> (+4200) even though the new <c>process2(_:)</c> sibling is declared
    /// first. Proves the adopted name won the correct vtable slot under reverse declaration order.
    /// </summary>
    public void TestDerivedSiblingFirstVirtualDispatchThroughBase()
    {
        using CollisionOverrideBase b = new CollisionOverrideDerivedSiblingFirst();
        AssertEqual(105, b.Process(5),
            "base.Process must reach the INHERITED first slot (process(first:) +100)");
        AssertEqual(4205, b.Process2(5),
            "base.Process2 must reach the DERIVED override of the second slot (process(second:) +4200)");
    }

    #endregion

    #region Emitted override shape (reflection)

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(CollisionOverrideBase))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(CollisionOverrideDerivedSecondOnly))]
    public void TestDerivedSecondOnlyOverridesProcess2Slot()
    {
        // The second-slot override must be named Process2 and declared on the derived type
        // (DeclaringType == derived), proving it adopted the ancestor's suffixed slot name.
        var process2 = typeof(CollisionOverrideDerivedSecondOnly)
            .GetMethod("Process2", new[] { typeof(int) });
        AssertNotNull(process2, "DerivedSecondOnly exposes Process2(int)");
        AssertEqual(
            nameof(CollisionOverrideDerivedSecondOnly),
            process2!.DeclaringType!.Name,
            "Process2 is declared (overridden) on DerivedSecondOnly, not merely inherited");

        // The base still exposes both distinct collision-suffixed slots.
        AssertNotNull(
            typeof(CollisionOverrideBase).GetMethod("Process", new[] { typeof(int) }),
            "Base exposes Process(int) (first slot)");
        AssertNotNull(
            typeof(CollisionOverrideBase).GetMethod("Process2", new[] { typeof(int) }),
            "Base exposes Process2(int) (second slot)");
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(CollisionOverrideDerivedSecondPlusSibling))]
    public void TestDerivedSecondPlusSiblingEmitsBothSlots()
    {
        // The adopted override keeps the base-suffixed Process2 slot...
        var process2 = typeof(CollisionOverrideDerivedSecondPlusSibling)
            .GetMethod("Process2", new[] { typeof(int) });
        AssertNotNull(process2, "DerivedSecondPlusSibling exposes Process2(int) (adopted override slot)");
        AssertEqual(
            nameof(CollisionOverrideDerivedSecondPlusSibling),
            process2!.DeclaringType!.Name,
            "Process2 is declared (overridden) on DerivedSecondPlusSibling");

        // ...and the unrelated new sibling was pushed to a fresh, non-colliding suffix.
        var process22 = typeof(CollisionOverrideDerivedSecondPlusSibling)
            .GetMethod("Process22", new[] { typeof(int) });
        AssertNotNull(process22, "DerivedSecondPlusSibling exposes Process22(int) (disambiguated new sibling)");
        AssertEqual(
            nameof(CollisionOverrideDerivedSecondPlusSibling),
            process22!.DeclaringType!.Name,
            "Process22 is declared on DerivedSecondPlusSibling (new method, not inherited)");
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(CollisionOverrideDerivedSecondDefaulted))]
    public void TestDerivedSecondDefaultedConvenienceOverloadAdoptsName()
    {
        // The trimmed zero-arg convenience overload must adopt the suffixed name (Process2()),
        // NOT the recomputed bare Process(). A Process() zero-arg would mean the overload emitter
        // dropped the adopted name and paired the convenience surface with the wrong base slot.
        AssertNotNull(
            typeof(CollisionOverrideDerivedSecondDefaulted).GetMethod("Process2", Type.EmptyTypes),
            "DerivedSecondDefaulted exposes the zero-arg convenience overload Process2()");
        AssertNull(
            typeof(CollisionOverrideDerivedSecondDefaulted).GetMethod("Process", Type.EmptyTypes),
            "No bare Process() convenience overload — the adopted name was not dropped");
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(CollisionOverrideDerivedSiblingFirst))]
    public void TestDerivedSiblingFirstEmitsBothSlots()
    {
        // Reverse declaration order must emit the same two slots as Scenario C: the adopted override
        // keeps Process2 and the (source-first) new sibling is pushed to Process22 — no duplicate, no
        // CS0111, order-independent.
        var process2 = typeof(CollisionOverrideDerivedSiblingFirst)
            .GetMethod("Process2", new[] { typeof(int) });
        AssertNotNull(process2, "DerivedSiblingFirst exposes Process2(int) (adopted override slot)");
        AssertEqual(
            nameof(CollisionOverrideDerivedSiblingFirst),
            process2!.DeclaringType!.Name,
            "Process2 is declared (overridden) on DerivedSiblingFirst");

        var process22 = typeof(CollisionOverrideDerivedSiblingFirst)
            .GetMethod("Process22", new[] { typeof(int) });
        AssertNotNull(process22, "DerivedSiblingFirst exposes Process22(int) (disambiguated new sibling declared first)");
        AssertEqual(
            nameof(CollisionOverrideDerivedSiblingFirst),
            process22!.DeclaringType!.Name,
            "Process22 is declared on DerivedSiblingFirst (new method, not inherited)");
    }

    #endregion
}
