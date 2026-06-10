// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression tests for justinwojo/swift-dotnet-bindings#40 (ad-network SDK delegate crash).
///
/// Shape: a child protocol that inherits all its requirements from a parent protocol
/// with no new requirements of its own — <c>protocol ChildDelegate: ParentDelegate {}</c>.
/// The Swift API exposes a property typed as the child protocol; Swift's witness
/// dispatch routes inherited calls through the parent's protocol vtable.
///
/// Two layered bugs converge here. Both must be fixed for the callback to reach
/// the user's C# implementation:
///
///   Layer 1 — parent vtable nil: only the child proxy's cctor runs when the
///   user assigns a C# impl, so the parent's Swift <c>_p_vtable</c> module-global
///   is never populated. Swift force-unwraps the nil function pointer and crashes
///   (the exact symptom in the issue #40 bug report).
///
///   Layer 2 — receiver cross-proxy lookup: even with the vtable populated, the
///   parent receiver's <c>TryGetProxy&lt;ParentProxy&gt;</c> returns null when the
///   registered proxy is the sibling <c>ChildProxy</c> — they have no class
///   inheritance. The callback is silently dropped.
///
/// These tests never reference any <c>*Proxy</c> class directly: a regression in
/// either layer surfaces as either an unhandled <c>SIGTRAP</c> (layer 1) or
/// <c>LastSlotFired == 0</c> with <c>ImplCalled == false</c> (layer 2).
/// </summary>
public class InheritedDelegateDispatchTests : TestBase
{
    public InheritedDelegateDispatchTests(TestResults results) : base(results) { }

    /// <summary>
    /// The issue #40 repro: assign a C# class implementing the CHILD interface to a
    /// child-typed delegate property, then call a Swift method that dispatches the
    /// inherited parent requirement through that delegate. Pre-fix this crashes
    /// on the nil parent vtable. The fix forces the parent proxy's cctor in the
    /// child proxy's <c>InitializeVtable</c> and routes the receiver lookup through
    /// <c>IProtocolProxyImpl&lt;TInterface&gt;</c> so the child-typed proxy is found
    /// for the parent-typed receiver.
    /// </summary>
    public void TestChildDelegateDeliversInheritedCallback()
    {
        var impl = new InheritedChildDelegateImpl();
        var source = new InheritedDelegateSource();

        source.ChildDelegate = impl;
        source.FireViaChild(value: 7);

        AssertEqual(1, source.LastSlotFired, "fireViaChild routed through the weak child slot (1)");
        AssertTrue(impl.ParentCalled, "Inherited parentDidNotify reached the C# impl");
        AssertEqual(7, impl.LastValue, "C# impl received the literal value");
    }

    /// <summary>
    /// Control: dispatching through a parent-typed property has always worked
    /// because the parent proxy IS what gets registered. This test exists so a
    /// regression in basic delegate dispatch doesn't get blamed on the inherited-
    /// protocol fix.
    /// </summary>
    public void TestParentDelegateDirectDispatch()
    {
        var impl = new InheritedParentDelegateImpl();
        var source = new InheritedDelegateSource();

        source.ParentDelegate = impl;
        source.FireViaParent(value: 42);

        AssertEqual(2, source.LastSlotFired, "fireViaParent routed through the weak parent slot (2)");
        AssertTrue(impl.ParentCalled, "Direct parent dispatch reached the C# impl");
        AssertEqual(42, impl.LastValue, "C# impl received the literal value");
    }

    /// <summary>
    /// Strong-storage variant of the child-delegate path. Lets the test drop its
    /// local impl reference and still receive a callback (the strong slot keeps
    /// the proxy alive). Mirrors the AutoWrappedDelegate strong-slot test so the
    /// inherited-dispatch fix doesn't accidentally degrade strong-storage paths.
    /// </summary>
    public void TestStrongChildDelegateDeliversInheritedCallback()
    {
        var impl = new InheritedChildDelegateImpl();
        var source = new InheritedDelegateSource();

        source.StrongChildDelegate = impl;
        source.FireViaStrongChild(value: 99);

        AssertEqual(3, source.LastSlotFired, "fireViaStrongChild routed through the strong child slot (3)");
        AssertTrue(impl.ParentCalled, "Inherited parentDidNotify reached the C# impl through strong storage");
        AssertEqual(99, impl.LastValue, "C# impl received the literal value");
    }

    /// <summary>
    /// Repeated dispatch on the same child-typed delegate: pre-fix the SECOND call
    /// might succeed if the parent vtable got populated by some other path (e.g.
    /// an earlier test instantiated a parent proxy). The fix makes every call
    /// idempotent regardless of test ordering.
    /// </summary>
    public void TestRepeatedInheritedCallbacks()
    {
        var impl = new InheritedChildDelegateImpl();
        var source = new InheritedDelegateSource();
        source.ChildDelegate = impl;

        source.FireViaChild(value: 1);
        source.FireViaChild(value: 2);
        source.FireViaChild(value: 3);

        AssertTrue(impl.ParentCalled, "Inherited parentDidNotify reached the C# impl");
        AssertEqual(3, impl.LastValue, "Last value observed by the C# impl");
        AssertEqual(3, impl.CallCount, "All three inherited callbacks were delivered");
    }
}

internal class InheritedChildDelegateImpl : IInheritedChildDelegate
{
    public bool ParentCalled { get; private set; }
    public int LastValue { get; private set; }
    public int CallCount { get; private set; }

    public void ParentDidNotify(int value)
    {
        ParentCalled = true;
        LastValue = value;
        CallCount++;
    }
}

internal class InheritedParentDelegateImpl : IInheritedParentDelegate
{
    public bool ParentCalled { get; private set; }
    public int LastValue { get; private set; }

    public void ParentDidNotify(int value)
    {
        ParentCalled = true;
        LastValue = value;
    }
}

/// <summary>
/// 3-level chain: Grandchild → Child → Parent → AnyObject. Verifies the ancestor
/// proxy cctor cascade composes across more than one level and that the receiver's
/// covariant <c>IProtocolProxyImpl&lt;TInterface&gt;</c> lookup finds a grandchild-typed
/// proxy when the parent receiver does the lookup.
/// </summary>
public class InheritedDelegate3LevelTests : TestBase
{
    public InheritedDelegate3LevelTests(TestResults results) : base(results) { }

    public void TestGrandchildDelegateDeliversInheritedCallback()
    {
        var impl = new InheritedGrandchildDelegateImpl();
        var source = new InheritedDelegate3LevelSource();

        source.GrandchildDelegate = impl;
        source.FireViaGrandchild(value: 13);

        AssertEqual(1, source.LastSlotFired, "fireViaGrandchild routed through the weak grandchild slot");
        AssertTrue(impl.ParentCalled, "Inherited parentDidNotify reached the C# impl through 3 levels of inheritance");
        AssertEqual(13, impl.LastValue, "C# impl received the literal value");
    }
}

internal class InheritedGrandchildDelegateImpl : IInheritedGrandchildDelegate
{
    public bool ParentCalled { get; private set; }
    public int LastValue { get; private set; }

    public void ParentDidNotify(int value)
    {
        ParentCalled = true;
        LastValue = value;
    }
}

/// <summary>
/// Non-empty child: the child protocol carries its own requirement on top of the
/// inherited parent requirement. Verifies that adding child-own dispatch on top of
/// inherited-parent dispatch doesn't regress either path.
/// </summary>
public class InheritedNonEmptyChildTests : TestBase
{
    public InheritedNonEmptyChildTests(TestResults results) : base(results) { }

    public void TestNonEmptyChildInheritedParentMethod()
    {
        var impl = new InheritedNonEmptyChildDelegateImpl();
        var source = new InheritedNonEmptyChildSource();

        source.ChildDelegate = impl;
        source.FireParentViaChild(value: 21);

        AssertEqual(1, source.LastSlotFired, "fireParentViaChild routed through the child slot");
        AssertTrue(impl.ParentCalled, "Inherited parentDidNotify reached the C# impl");
        AssertFalse(impl.ChildCalled, "childDidNotify was NOT invoked by fireParentViaChild");
        AssertEqual(21, impl.LastParentValue, "Parent slot received the literal value");
    }

    public void TestNonEmptyChildOwnMethod()
    {
        var impl = new InheritedNonEmptyChildDelegateImpl();
        var source = new InheritedNonEmptyChildSource();

        source.ChildDelegate = impl;
        source.FireChildOwnMethod(value: 55);

        AssertEqual(2, source.LastSlotFired, "fireChildOwnMethod routed through the child slot");
        AssertTrue(impl.ChildCalled, "childDidNotify reached the C# impl");
        AssertFalse(impl.ParentCalled, "parentDidNotify was NOT invoked by fireChildOwnMethod");
        AssertEqual(55, impl.LastChildValue, "Child slot received the literal value");
    }
}

internal class InheritedNonEmptyChildDelegateImpl : IInheritedNonEmptyChildDelegate
{
    public bool ParentCalled { get; private set; }
    public bool ChildCalled { get; private set; }
    public int LastParentValue { get; private set; }
    public int LastChildValue { get; private set; }

    public void ParentDidNotify(int value)
    {
        ParentCalled = true;
        LastParentValue = value;
    }

    public void ChildDidNotify(int value)
    {
        ChildCalled = true;
        LastChildValue = value;
    }
}

/// <summary>
/// Cross-module variant: parent protocol lives in SwiftBindingsTestLibDependency,
/// child protocol lives here and inherits across the module boundary. The issue #40
/// repro's third remaining shape — the C# class implements only the child
/// interface and Swift dispatches the inherited cross-module method through the
/// parent's witness table.
/// </summary>
public class CrossModuleInheritedDelegateTests : TestBase
{
    public CrossModuleInheritedDelegateTests(TestResults results) : base(results) { }

    public void TestCrossModuleInheritedChildDeliversCallback()
    {
        var impl = new CrossModuleInheritedChildDelegateImpl();
        var source = new CrossModuleInheritedDelegateSource();

        source.ChildDelegate = impl;
        source.FireViaCrossModuleChild(value: 17);

        AssertEqual(1, source.LastSlotFired, "fireViaCrossModuleChild routed through the child slot");
        AssertTrue(impl.ParentCalled, "Inherited crossModuleDidNotify reached the C# impl across module boundary");
        AssertEqual(17, impl.LastValue, "C# impl received the literal value");
    }
}

internal class CrossModuleInheritedChildDelegateImpl : ICrossModuleInheritedChildDelegate
{
    public bool ParentCalled { get; private set; }
    public int LastValue { get; private set; }

    public void CrossModuleDidNotify(int value)
    {
        ParentCalled = true;
        LastValue = value;
    }
}

/// <summary>
/// Transitive cross-module ancestor (H1): local child → cross-module parent →
/// cross-module grandparent. Pre-H1 the child proxy's cctor only forced the
/// direct cross-module parent's vtable population, so the grandparent's
/// <c>_p_vtable</c> in the dep module stayed nil and dispatching the inherited
/// grandparent method through the child-typed slot force-unwrapped nil. The H1
/// fix walks ancestors transitively via BFS, populating BOTH levels.
/// </summary>
public class CrossModuleTransitiveDelegateTests : TestBase
{
    public CrossModuleTransitiveDelegateTests(TestResults results) : base(results) { }

    /// <summary>
    /// Control case — direct cross-module parent dispatch. Works pre-H1.
    /// A regression here is a regression in the existing direct-ancestor
    /// path, not in the H1 transitive walk.
    /// </summary>
    public void TestTransitiveChildDeliversDirectParentCallback()
    {
        var impl = new CrossModuleTransitiveChildDelegateImpl();
        var source = new CrossModuleTransitiveDelegateSource();

        source.ChildDelegate = impl;
        source.FireParentViaTransitiveChild(value: 23);

        AssertEqual(1, source.LastSlotFired, "fireParentViaTransitiveChild routed through the child slot");
        AssertTrue(impl.ParentCalled, "Inherited crossModuleParentDidNotify reached the C# impl");
        AssertEqual(23, impl.LastParentValue, "Parent slot received the literal value");
    }

    /// <summary>
    /// The H1 gate — dispatching the TRANSITIVE grandparent's method through
    /// the child slot. Pre-H1 this crashes (grandparent vtable nil); post-H1
    /// the BFS over cross-module ancestors populates both levels' vtable
    /// storage from the child proxy's cctor.
    /// </summary>
    public void TestTransitiveChildDeliversGrandparentCallback()
    {
        var impl = new CrossModuleTransitiveChildDelegateImpl();
        var source = new CrossModuleTransitiveDelegateSource();

        source.ChildDelegate = impl;
        source.FireGrandparentViaTransitiveChild(value: 91);

        AssertEqual(2, source.LastSlotFired, "fireGrandparentViaTransitiveChild routed through the child slot");
        AssertTrue(impl.GrandparentCalled, "Inherited crossModuleGrandparentDidNotify reached the C# impl through 2 cross-module hops");
        AssertEqual(91, impl.LastGrandparentValue, "Grandparent slot received the literal value");
    }
}

internal class CrossModuleTransitiveChildDelegateImpl : ICrossModuleTransitiveChildDelegate
{
    public bool ParentCalled { get; private set; }
    public bool GrandparentCalled { get; private set; }
    public int LastParentValue { get; private set; }
    public int LastGrandparentValue { get; private set; }

    public void CrossModuleParentDidNotify(int value)
    {
        ParentCalled = true;
        LastParentValue = value;
    }

    public void CrossModuleGrandparentDidNotify(int value)
    {
        GrandparentCalled = true;
        LastGrandparentValue = value;
    }
}

/// <summary>
/// Cross-module parent with a non-dispatchable closure property (H2). The
/// cross-module C# vtable struct must apply the same membership filter
/// (<c>ProtocolVtableMembers</c>) the Swift wrapper applies — otherwise the
/// layouts diverge by one slot and invoking the inherited non-closure method
/// through the child slot reads a misaligned function pointer. Pre-H2 this
/// crashed; post-H2 the layouts match.
/// </summary>
public class CrossModuleClosurePropertyDelegateTests : TestBase
{
    public CrossModuleClosurePropertyDelegateTests(TestResults results) : base(results) { }

    public void TestClosurePropertyChildDeliversNonClosureCallback()
    {
        var impl = new CrossModuleClosurePropertyChildDelegateImpl();
        var source = new CrossModuleClosurePropertyDelegateSource();

        source.ChildDelegate = impl;
        source.FireNonClosureViaChild(value: 31);

        AssertEqual(1, source.LastSlotFired, "fireNonClosureViaChild routed through the child slot");
        AssertTrue(impl.NonClosureCalled, "Inherited nonClosureDidNotify reached the C# impl across the module boundary");
        AssertEqual(31, impl.LastValue, "C# impl received the literal value");
    }
}

internal class CrossModuleClosurePropertyChildDelegateImpl : ICrossModuleClosurePropertyChildDelegate
{
    public bool NonClosureCalled { get; private set; }
    public int LastValue { get; private set; }

    // The closure property is required by the inherited interface but is
    // non-dispatchable — the generator skips its vtable slot AND its
    // cross-module receiver (so Swift never writes through it). The C# impl
    // still has to declare the property to satisfy the interface; the test
    // never reads it.
    public Action<int>? ClosureCallback { get; set; }

    public void NonClosureDidNotify(int value)
    {
        NonClosureCalled = true;
        LastValue = value;
    }
}

/// <summary>
/// Cross-module parent with a two-closure method (filtered by
/// <c>ProtocolVtableMembers.IncludesMethod</c> via
/// <c>IsDispatchableClosureMethod</c>'s "exactly one dispatchable closure
/// param" gate) declared BEFORE a dispatchable method. The Swift wrapper
/// vtable struct and the C# cross-module-parent vtable struct both increment
/// the slot index before applying the filter, so the dispatchable method
/// lands at slot 1. The cross-module parent cctor must use the same
/// ordering — pre-fix it filtered first, then incremented, so the cctor
/// assigned the dispatchable method to slot 0 while both structs expected it
/// at slot 1, producing either a generated-C# compile failure or a
/// misaligned function pointer at dispatch time.
/// </summary>
public class CrossModuleSkippedMethodDelegateTests : TestBase
{
    public CrossModuleSkippedMethodDelegateTests(TestResults results) : base(results) { }

    public void TestSkippedMethodChildDeliversDispatchableCallback()
    {
        var impl = new CrossModuleSkippedMethodChildDelegateImpl();
        var source = new CrossModuleSkippedMethodDelegateSource();

        source.ChildDelegate = impl;
        source.FireDispatchableViaChild(value: 71);

        AssertEqual(1, source.LastSlotFired, "fireDispatchableViaChild routed through the child slot");
        AssertTrue(impl.DispatchableCalled, "Inherited dispatchableAfterSkippedMethod reached the C# impl across the module boundary");
        AssertEqual(71, impl.LastValue, "C# impl received the literal value at the correct vtable slot");
    }
}

internal class CrossModuleSkippedMethodChildDelegateImpl : ICrossModuleSkippedMethodChildDelegate
{
    public bool DispatchableCalled { get; private set; }
    public int LastValue { get; private set; }

    // The two-closure method is required by the inherited parent interface
    // but is non-dispatchable — the generator skips its vtable slot AND its
    // cross-module receiver. The C# impl still has to declare it to satisfy
    // the interface; the test never invokes it through any path.
    public void TwoClosureSkip(Action<int> first, Action<int> second)
    {
        // intentionally empty
    }

    public void DispatchableAfterSkippedMethod(int value)
    {
        DispatchableCalled = true;
        LastValue = value;
    }
}
