// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// A Swift-backed existential of a refined protocol passed where its base protocol is asked for
/// (<c>RefinedProxyAsBase.swift</c>). The refined proxy's container carries the refined protocol's
/// witness table; Swift reads an <c>any Base</c> through the base table, so passing the container
/// unchanged calls the refined table's base-conformance entry as if it were the base's first
/// requirement. Each value is read through its base and through its own protocol, repeatedly, so a
/// wrong table, a wrong layout or a lost reference shows up as a wrong value or a crash.
/// </summary>
public class RefinedProxyAsBaseTests : TestBase
{
    public RefinedProxyAsBaseTests(TestResults results) : base(results) { }

    public void TestInlineOpaqueRefinedPassedAsBase()
    {
        for (var i = 0; i < 3; i++)
        {
            var refined = TestLibFunctions.RpaMakeRefinedInline(5);
            AssertEqual(11, TestLibFunctions.RpaReadBaseOpaque(refined), "base requirement through the base table");
            AssertEqual(1005, TestLibFunctions.RpaReadRefinedOpaque(refined), "refined requirement through its own table");
            AssertEqual(1005, refined.GetRefinedValue(), "the proxy still dispatches its own requirement");
            (refined as IDisposable)?.Dispose();
        }
    }

    public void TestBoxedOpaqueRefinedPassedAsBase()
    {
        for (var i = 0; i < 3; i++)
        {
            var refined = TestLibFunctions.RpaMakeRefinedBoxed(7);
            AssertEqual(15, TestLibFunctions.RpaReadBaseOpaque(refined), "boxed value's base requirement through the base table");
            AssertEqual(1007, TestLibFunctions.RpaReadRefinedOpaque(refined), "boxed value's refined requirement");
            (refined as IDisposable)?.Dispose();
        }
    }

    public void TestClassBoundRefinedPassedAsBase()
    {
        for (var i = 0; i < 3; i++)
        {
            var refined = TestLibFunctions.RpaMakeRefinedBound(9);
            AssertEqual(19, TestLibFunctions.RpaReadBaseBound(refined), "class-bound base requirement through the base table");
            AssertEqual(19, TestLibFunctions.RpaReadOptionalBaseBound(refined), "and as an optional base");
            AssertEqual(-1, TestLibFunctions.RpaReadOptionalBaseBound(null), "nil stays nil");
            AssertEqual(1009, TestLibFunctions.RpaReadRefinedBound(refined), "class-bound refined requirement");
            (refined as IDisposable)?.Dispose();
        }
    }

    public void TestClassBoundRefinementOfOpaqueBasePassedAsBase()
    {
        // The proxy holds the two-word class layout; the opaque base needs the five-word one.
        for (var i = 0; i < 3; i++)
        {
            var refined = TestLibFunctions.RpaMakeBoundRefinesOpaque(12);
            AssertEqual(25, TestLibFunctions.RpaReadBaseOpaque(refined), "class instance read as the opaque base");
            AssertEqual(1012, refined.GetRefinedValue(), "the proxy still dispatches its own requirement");
            (refined as IDisposable)?.Dispose();
        }
    }
}
