// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Internal;

/// <summary>
/// Pairs with <c>BindingTests/Sources/SwiftBindingsTestLib/Internal/UnderscoreParentNestedTypes.swift</c>.
/// The regression was a generation failure (nested types of an underscore-suppressed parent were
/// registered as silent tombstones no handler emitted), so the compile gate is the primary check.
/// This proves the public host that reaches the suppressed table still binds and round-trips.
/// </summary>
public class UnderscoreParentNestedTypesTests : TestBase
{
    public UnderscoreParentNestedTypesTests(TestResults results) : base(results) { }

    public void TestHostBackedBySuppressedTableRoundTrips()
    {
        var host = new UnderscoreParentHost(42);
        AssertEqual(42L, (long)host.Capacity, "capacity read through the internal table");
    }
}
