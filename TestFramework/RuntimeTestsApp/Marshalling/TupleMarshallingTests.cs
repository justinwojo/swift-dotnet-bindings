// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for tuple marshalling: 2-tuple, 3-tuple, 7-tuple, named tuples, tuple methods.
/// </summary>
public class TupleMarshallingTests : TestBase
{
    public TupleMarshallingTests(TestResults results) : base(results) { }

    #region Tier 1 — Smoke Tests

    [TestTier(TestTier.Tier1)]
    public void TestBasicPairCreation()
    {
        var pair = SwiftBindingsTestLib.MakePair(10, 20);
        AssertEqual(10, pair.Item1, "Pair.Item1");
        AssertEqual(20, pair.Item2, "Pair.Item2");
        TestLogger.Info($"MakePair(10, 20) = ({pair.Item1}, {pair.Item2})");
    }

    [TestTier(TestTier.Tier1)]
    public void TestNamedPair()
    {
        var named = SwiftBindingsTestLib.MakeNamedPair();
        AssertEqual(10, named.x, "Named pair x");
        AssertEqual(20, named.y, "Named pair y");
        TestLogger.Info($"MakeNamedPair() = (x: {named.x}, y: {named.y})");
    }

    #endregion

    #region Tier 2 — Functional Tests

    [TestTier(TestTier.Tier2)]
    public void TestTriple()
    {
        var triple = SwiftBindingsTestLib.MakeTriple(1, 2, 3);
        AssertEqual(1, triple.Item1, "Triple.Item1");
        AssertEqual(2, triple.Item2, "Triple.Item2");
        AssertEqual(3, triple.Item3, "Triple.Item3");
        TestLogger.Info("MakeTriple passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestSeptuple()
    {
        var sep = SwiftBindingsTestLib.MakeSeptuple(1, 2, 3, 4, 5, 6, 7);
        AssertEqual(1, sep.Item1, "Sep.Item1");
        AssertEqual(4, sep.Item4, "Sep.Item4");
        AssertEqual(7, sep.Item7, "Sep.Item7");
        TestLogger.Info("MakeSeptuple passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestSumPair()
    {
        var sum = SwiftBindingsTestLib.SumPair((3, 7));
        AssertEqual(10, sum, "SumPair(3, 7)");
        TestLogger.Info($"SumPair((3, 7)) = {sum}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestMixedPair()
    {
        var mixed = SwiftBindingsTestLib.MakeMixedPair(42, true);
        AssertEqual(42, mixed.Item1, "Mixed.Item1");
        AssertTrue(mixed.Item2, "Mixed.Item2");
        TestLogger.Info("MakeMixedPair passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestDivmod()
    {
        var result = SwiftBindingsTestLib.GetDivmod(17, 5);
        AssertEqual(3, result.quotient, "Divmod quotient");
        AssertEqual(2, result.remainder, "Divmod remainder");
        TestLogger.Info($"Divmod(17, 5) = (q: {result.quotient}, r: {result.remainder})");
    }

    [TestTier(TestTier.Tier2)]
    public void TestMinmax()
    {
        var result = SwiftBindingsTestLib.GetMinmax(42, 7);
        AssertEqual(7, result.min, "Minmax min");
        AssertEqual(42, result.max, "Minmax max");
        TestLogger.Info($"Minmax(42, 7) = (min: {result.min}, max: {result.max})");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTupleReturnerMethods()
    {
        var returner = new TupleReturner(3, 7);
        var pair = returner.GetAsTuple();
        AssertEqual(3, pair.Item1, "AsTuple.Item1");
        AssertEqual(7, pair.Item2, "AsTuple.Item2");

        var staticPair = TupleReturner.MakePair(100, 200);
        AssertEqual(100, staticPair.Item1, "Static MakePair.Item1");
        AssertEqual(200, staticPair.Item2, "Static MakePair.Item2");
        TestLogger.Info("TupleReturner methods passed");
    }

    #endregion
}
