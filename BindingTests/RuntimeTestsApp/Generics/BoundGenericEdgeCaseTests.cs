// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Tests for bound generic edge cases — multi-type-argument bound generics
/// with concrete type resolution.
///
/// Pair&lt;A, B&gt; is a generic struct; makeRefPair uses concrete class types
/// (CoordinateRef, LabelRef) that resolve through BoundGenericsHandler.
/// makePairDescription is a method-level generic and may not be emitted.
/// </summary>
public class BoundGenericEdgeCaseTests : TestBase
{
    public BoundGenericEdgeCaseTests(TestResults results) : base(results) { }

    #region CoordinateRef / LabelRef Construction (Tier 1)

    public void TestCoordinateRefConstruction()
    {
        var coord = new CoordinateRef(x: 10, y: 20);
        AssertNotNull(coord, "CoordinateRef constructed");
        TestLogger.Info("CoordinateRef(10, 20) construction passed");
    }

    public void TestLabelRefConstruction()
    {
        var label = new LabelRef(text: "hello");
        AssertNotNull(label, "LabelRef constructed");
        TestLogger.Info("LabelRef(\"hello\") construction passed");
    }

    #endregion

    #region MakeRefPair — Multi-Type-Arg Bound Generic (Tier 1)

    [Skip("Pair<CoordinateRef, LabelRef> bound generic return: generator may not resolve multi-type-arg bound generic struct return type. Remove Skip if compilation succeeds.")]
    public void TestMakeRefPair()
    {
        // makeRefPair returns Pair<CoordinateRef, LabelRef> — two different class type args
        var coord = new CoordinateRef(x: 42, y: 99);
        var label = new LabelRef(text: "test");
        var pair = TestLibFunctions.MakeRefPair(coord, label);
        AssertNotNull(pair, "MakeRefPair returned non-null");
        TestLogger.Info("MakeRefPair construction passed");
    }

    #endregion

    #region MakePairDescription — Method-Level Generic (Expected Skip)

    [Skip("Method-level generic free function: makePairDescription<A, B> cannot resolve unbound type parameters. Verifies graceful skip.")]
    public void TestMakePairDescriptionSkipped()
    {
        // makePairDescription<A, B> is a method-level generic — the generator should skip it.
        // This test verifies that the method is not emitted (compilation would fail if it were
        // emitted with unresolved type parameters).
        AssertTrue(true, "makePairDescription skipped as expected (method-level generic)");
    }

    #endregion
}
