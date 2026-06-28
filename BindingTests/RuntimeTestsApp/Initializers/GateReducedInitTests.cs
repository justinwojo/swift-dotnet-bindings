// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Initializers;

/// <summary>
/// Runtime gate for the pre-gate trailing-default rescue on a thunk-eligible
/// constructor (<c>GateReducedInitHost</c>).
///
/// The full <c>init(value:edges:)</c> has an unbindable trailing array parameter
/// (<c>[SwiftUI.Edge]</c>) that carries a <c>= []</c> default, so the rescue
/// synthesizes a reduced <c>init(value:)</c>. That reduced single-Int initializer on a
/// plain class is native-thunk-eligible: a thunk would <c>bl</c> the full-ABI symbol
/// with the <c>edges</c> register uninitialized, so reading <c>edges.count</c>
/// dereferences a garbage array-buffer pointer and faults. The reduced decl is forced
/// onto the @_cdecl path (which calls the initializer by name, letting Swift fill
/// <c>edges = []</c>), so <c>total</c> equals the supplied value. A regression to the
/// thunk path crashes here.
/// </summary>
public class GateReducedInitTests : TestBase
{
    public GateReducedInitTests(TestResults results) : base(results) { }

    public void TestReducedInitFillsTrailingDefault()
    {
        // Named argument disambiguates the reduced (nint value) ctor from the
        // internal (SwiftHandle handle) ctor, which a bare integer literal also matches.
        using var host = new GateReducedInitHost(value: 5);
        AssertEqual(5, (int)host.Total, "Reduced init(value:) fills edges = [] so total == value (count 0)");
    }

    public void TestReducedInitDistinctValueRoundTrips()
    {
        using var host = new GateReducedInitHost(value: 42);
        AssertEqual(42, (int)host.Total, "Reduced init(value:) round-trips a distinct value");
    }
}
