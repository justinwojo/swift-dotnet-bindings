// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Runtime gate for <c>UnrecordedTypeOverloads.swift</c>: overloads with the same labels that differ
/// only at an existential or closure position. The second overload was once dropped as a duplicate
/// signature, and the first carried the second's parameter names. Each overload is called with named
/// arguments, so a dropped overload or a parameter-name bleed fails the build of this app, and each
/// call is checked to reach its own Swift body.
/// </summary>
public class UnrecordedTypeOverloadTests : TestBase
{
    public UnrecordedTypeOverloadTests(TestResults results) : base(results) { }

    /// <summary>A .NET conformer, so the existential overload is also reached through a proxy.</summary>
    private sealed class ManagedMeasure : IOverloadMeasure
    {
        private readonly nint _value;
        public ManagedMeasure(nint value) => _value = value;
        public nint Measure() => _value;
    }

    public void TestMethodExistentialOverloadScalesTheMeasure()
    {
        using var combiner = new OverloadCombiner();
        using var ruler = new OverloadRuler(4);
        AssertEqual((nint)12, combiner.Combine(factor: 3, shape: ruler), "existential overload multiplies by the Swift conformer's measure");
        AssertEqual((nint)15, combiner.Combine(factor: 3, shape: new ManagedMeasure(5)), "existential overload multiplies by the .NET conformer's measure");
    }

    public void TestMethodClosureOverloadAddsTheResult()
    {
        using var combiner = new OverloadCombiner();
        AssertEqual((nint)7, combiner.Combine(offset: 3, source: () => 4), "closure overload adds what the closure returns");
    }

    public void TestFreeFunctionOverloadsReachTheirOwnBodies()
    {
        using var ruler = new OverloadRuler(4);
        AssertEqual((nint)12, TestLibFunctions.OverloadCombine(factor: 3, shape: ruler), "existential overload multiplies");
        AssertEqual((nint)7, TestLibFunctions.OverloadCombine(offset: 3, source: () => 4), "closure overload adds");
    }
}
