// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;

namespace RuntimeTestsApp.CrossModule;

/// <summary>
/// Bug #14 — same-module class inheritance regression coverage.
/// Verifies that a subclass declared in the main test library that extends a class
/// from the dependency module emits real C# inheritance: the derived class declares
/// <c>: SwiftBindingsTestLibDependency.DependencyBaseEntity</c>, accepts upcast to
/// the base reference, and inherits parent members through C#'s type system rather
/// than re-emitting them on every sibling.
///
/// Compile-time gate: the explicit upcast lines (e.g. <c>DependencyBaseEntity asBase = child;</c>)
/// fail to compile without the fix. Runtime gates verify identity preservation and
/// virtual-dispatch behavior across the boundary.
/// </summary>
public class CrossModuleInheritanceTests : TestBase
{
    public CrossModuleInheritanceTests(TestResults results) : base(results) { }

    public void TestChildIsAssignableToBaseWithoutCast()
    {
        using var child = new LocalChildEntity("alpha", 7);
        // Compile-time assertion: this must compile without a cast.
        DependencyBaseEntity asBase = child;
        AssertNotNull(asBase, "Cross-module upcast preserves reference");
        AssertTrue(asBase is LocalChildEntity, "Reference identity preserved through upcast");
    }

    public void TestChildOverrideDispatchesPolymorphically()
    {
        using var child = new LocalChildEntity("hello", 42);
        // Pass through a Swift function that accepts the cross-module BASE type.
        // The override must dispatch through the C# vtable across the C ABI boundary.
        var desc = SwiftBindingsTestLibDependency.Functions.DescribeBaseEntity(child);
        AssertEqual("Child[hello:42]", desc, "Override dispatches through cross-module base");
    }

    public void TestChildInheritedTagFlowsThroughBase()
    {
        using var child = new LocalChildEntity("z", 99);
        var tag = SwiftBindingsTestLibDependency.Functions.ReadBaseEntityTag(child);
        AssertEqual(99, tag, "Override tag() returns derived value via base call");
    }

    public void TestUpcastRoundTrip()
    {
        using var child = new LocalChildEntity("rt", 5);
        // Swift returns DependencyBaseEntity; the C# binding for the call site must
        // surface that as the cross-module base type. Without the inheritance fix the
        // call would fail to compile (no DependencyBaseEntity ↔ LocalChildEntity
        // relationship in the generated C#).
        DependencyBaseEntity asBase = TestLibFunctions.UpcastChildToBase(child);
        AssertNotNull(asBase, "Round-trip upcast non-null");
    }

    public void TestThreeLevelChain()
    {
        // LocalGrandchildEntity → DependencyMidEntity (cross-module) → DependencyBaseEntity.
        using var grand = new LocalGrandchildEntity("g", 11, true);
        var desc = SwiftBindingsTestLibDependency.Functions.DescribeBaseEntity(grand);
        // Swift's String(describing: Bool) interpolates lowercase "true"/"false".
        AssertEqual("Grand[g:11:true]", desc, "3-level virtual dispatch across cross-module mid-tier");

        var tag = SwiftBindingsTestLibDependency.Functions.ReadBaseEntityTag(grand);
        AssertEqual(11, tag, "Mid-tier override returns midTag through cross-module base");
    }

    public void TestDescribeChildAcceptsDerived()
    {
        using var child = new LocalChildEntity("dx", 3);
        // `describeChild` is in the main module and itself calls describeBaseEntity —
        // the wrapper must accept the derived class without an explicit cast on the C#
        // call site. (The Swift signature already takes LocalChildEntity, so this is
        // mostly a sanity check that nothing got flattened.)
        var result = TestLibFunctions.DescribeChild(child);
        AssertEqual("Child[dx:3]", result, "Free-function call site round-trips");
    }
}
