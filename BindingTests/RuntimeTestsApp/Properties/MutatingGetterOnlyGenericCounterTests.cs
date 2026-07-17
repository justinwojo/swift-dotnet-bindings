// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Bug (g) round-trip: a `mutating get`-only computed property on a GENERIC struct
/// (Resolver's exact repro shape, 2 sites). Because the parent is generic, its getter
/// wrapper threads metadata/PWT params through <c>PropertyWrapperEmitter</c>'s generic
/// static-dispatch path — a different code path than both the non-generic getter wrapper
/// and the protocol witness-dispatch path. Pre-fix, this generic path unconditionally
/// bound the reconstructed receiver as an immutable `let`, which swiftc rejects for a
/// `mutating get`. The fix binds `var` when the getter is mutating (or a setter exists).
/// This test reads <c>Snapshot</c> twice on the SAME instance to prove the underlying
/// Swift-side mutation (the call counter increment) actually took hold rather than the
/// getter silently no-op'ing on a copy.
/// </summary>
public class MutatingGetterOnlyGenericCounterTests : TestBase
{
    public MutatingGetterOnlyGenericCounterTests(TestResults results) : base(results) { }

    public void TestSnapshot_MutatingGetIncrementsAcrossReads()
    {
        using var counter = TestLibFunctions.MakeMutatingGetterOnlyGenericCounter(0);

        var first = counter.Snapshot;
        AssertEqual(1, first, "First snapshot read must observe the mutating get's first increment");

        var second = counter.Snapshot;
        AssertEqual(2, second, "Second snapshot read must observe the SAME instance's mutation, not a reset copy");

        var third = counter.Snapshot;
        AssertEqual(3, third, "Third snapshot read confirms the mutating get keeps incrementing in place");
    }

    public void TestElement_ReturnsConstructorValue()
    {
        using var counter = TestLibFunctions.MakeMutatingGetterOnlyGenericCounter(99);
        AssertEqual(99, counter.Element, "Element must round-trip the seed passed to the factory");
    }
}
