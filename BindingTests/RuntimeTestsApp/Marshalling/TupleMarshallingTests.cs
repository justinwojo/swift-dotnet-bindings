// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Swift.Runtime;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for tuple marshalling: 2-tuple, 3-tuple, 7-tuple, named tuples, tuple methods.
/// </summary>
public class TupleMarshallingTests : TestBase
{
    public TupleMarshallingTests(TestResults results) : base(results) { }

    #region Tier 3 — Mono JIT limitation: ValueTuple not blittable through P/Invoke

    public void TestBasicPairCreation()
    {
        var pair = TestLibFunctions.MakePair(10, 20);
        AssertEqual(10, pair.Item1, "Pair.Item1");
        AssertEqual(20, pair.Item2, "Pair.Item2");
        TestLogger.Info($"MakePair(10, 20) = ({pair.Item1}, {pair.Item2})");
    }

    public void TestNamedPair()
    {
        var named = TestLibFunctions.MakeNamedPair();
        AssertEqual(10, named.x, "Named pair x");
        AssertEqual(20, named.y, "Named pair y");
        TestLogger.Info($"MakeNamedPair() = (x: {named.x}, y: {named.y})");
    }

    public void TestTriple()
    {
        var triple = TestLibFunctions.MakeTriple(1, 2, 3);
        AssertEqual(1, triple.Item1, "Triple.Item1");
        AssertEqual(2, triple.Item2, "Triple.Item2");
        AssertEqual(3, triple.Item3, "Triple.Item3");
        TestLogger.Info("MakeTriple passed");
    }

    public void TestSeptuple()
    {
        var sep = TestLibFunctions.MakeSeptuple(1, 2, 3, 4, 5, 6, 7);
        AssertEqual(1, sep.Item1, "Sep.Item1");
        AssertEqual(4, sep.Item4, "Sep.Item4");
        AssertEqual(7, sep.Item7, "Sep.Item7");
        TestLogger.Info("MakeSeptuple passed");
    }

    public void TestSumPair()
    {
        var sum = TestLibFunctions.SumPair((3, 7));
        AssertEqual(10, sum, "SumPair(3, 7)");
        TestLogger.Info($"SumPair((3, 7)) = {sum}");
    }

    public void TestMixedPair()
    {
        var mixed = TestLibFunctions.MakeMixedPair(42, true);
        AssertEqual(42, mixed.Item1, "Mixed.Item1");
        AssertTrue(mixed.Item2, "Mixed.Item2");
        TestLogger.Info("MakeMixedPair passed");
    }

    public void TestDivmod()
    {
        var result = TestLibFunctions.Divmod(17, 5);
        AssertEqual(3, result.quotient, "Divmod quotient");
        AssertEqual(2, result.remainder, "Divmod remainder");
        TestLogger.Info($"Divmod(17, 5) = (q: {result.quotient}, r: {result.remainder})");
    }

    public void TestMinmax()
    {
        var result = TestLibFunctions.Minmax(42, 7);
        AssertEqual(7, result.min, "Minmax min");
        AssertEqual(42, result.max, "Minmax max");
        TestLogger.Info($"Minmax(42, 7) = (min: {result.min}, max: {result.max})");
    }

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

    #region Tuple returns under effects — ABI Coverage Grid (throws × async × optional-element)

    // These close the confirmed grid-corner gap: the existing fixtures cover plain and
    // non-throwing async tuple returns, but never a tuple return *under an effect*
    // (throws / async throws) nor a tuple whose element list contains an Optional. Each
    // test carries a value oracle so the cell grades on behaviour, not just "didn't crash".

    public void TestDivmodThrowing_Success()
    {
        // throws × tuple-return, happy path. errorPtr stays null; both elements round-trip.
        var result = TestLibFunctions.DivmodThrowing(17, 5);
        AssertEqual(3, result.quotient, "DivmodThrowing quotient");
        AssertEqual(2, result.remainder, "DivmodThrowing remainder");
        TestLogger.Info($"DivmodThrowing(17, 5) = (q: {result.quotient}, r: {result.remainder})");
    }

    public void TestDivmodThrowing_DivideByZeroThrows()
    {
        // throws × tuple-return, error path. The Swift error must surface as SwiftException
        // *before* any tuple is materialized — proves the errorPtr check precedes the return-
        // buffer read in the generated throwing wrapper.
        try
        {
            TestLibFunctions.DivmodThrowing(1, 0);
            throw new AssertionException("DivmodThrowing(_, 0) should throw");
        }
        catch (SwiftException ex)
        {
            AssertTrue(ex.Message.Contains("divideByZero"),
                $"Error message should contain Swift error description, got: {ex.Message}");
            TestLogger.Info($"DivmodThrowing(1, 0) threw with message: {ex.Message}");
        }
    }

    public async Task TestDivmodThrowingAsync_Success()
    {
        // async × throws × tuple-return, happy path. The async continuation must deliver the
        // tuple back through the result buffer after the await resumes.
        var result = await WithTimeout(
            TestLibFunctions.DivmodThrowingAsync(17, 5), DefaultAsyncTimeout);
        AssertEqual(3, result.quotient, "DivmodThrowingAsync quotient");
        AssertEqual(2, result.remainder, "DivmodThrowingAsync remainder");
        TestLogger.Info($"DivmodThrowingAsync(17, 5) = (q: {result.quotient}, r: {result.remainder})");
    }

    public async Task TestDivmodThrowingAsync_DivideByZeroThrows()
    {
        // async × throws × tuple-return, error path. The error must propagate through the
        // async resume as a faulted Task → awaited SwiftException, not a crash.
        var caught = false;
        try
        {
            await WithTimeout(TestLibFunctions.DivmodThrowingAsync(1, 0), DefaultAsyncTimeout);
        }
        catch (SwiftException ex)
        {
            caught = true;
            AssertTrue(ex.Message.Contains("divideByZero"),
                $"Error message should contain Swift error description, got: {ex.Message}");
            TestLogger.Info($"DivmodThrowingAsync(1, 0) threw with message: {ex.Message}");
        }
        AssertTrue(caught, "DivmodThrowingAsync(_, 0) should throw SwiftException");
    }

    public void TestSpanBounds_NonEmpty_SomeUpper()
    {
        // Optional-element tuple `(lower, upper?)`, Some-side. A non-empty span returns the
        // upper bound; the C# projection is `(int lower, int? upper)`.
        var result = TestLibFunctions.SpanBounds(2, 8);
        AssertEqual(2, result.lower, "SpanBounds lower");
        AssertTrue(result.upper.HasValue, "SpanBounds upper should be Some for a non-empty span");
        AssertEqual(8, result.upper!.Value, "SpanBounds upper value");
        TestLogger.Info($"SpanBounds(2, 8) = (lower: {result.lower}, upper: {result.upper})");
    }

    public void TestSpanBounds_Empty_NilUpper()
    {
        // Optional-element tuple `(lower, upper?)`, None-side. An empty span (lo == hi) returns
        // nil for the upper bound — must surface as a null int? without disturbing `lower`.
        var result = TestLibFunctions.SpanBounds(5, 5);
        AssertEqual(5, result.lower, "SpanBounds lower");
        AssertTrue(!result.upper.HasValue, "SpanBounds upper should be nil for an empty span");
        TestLogger.Info($"SpanBounds(5, 5) = (lower: {result.lower}, upper: null)");
    }

    public void TestMakePointWithTag_StructElement()
    {
        // mixed primitive+struct element-mix: a frozen blittable struct embedded as the first
        // tuple element. Both the struct's fields and the trailing primitive must round-trip.
        var result = TestLibFunctions.MakePointWithTag(3.0, 4.0, 7);
        AssertEqual(3.0, result.point.X, "MakePointWithTag point.X");
        AssertEqual(4.0, result.point.Y, "MakePointWithTag point.Y");
        AssertEqual(7, result.tag, "MakePointWithTag tag");
        TestLogger.Info($"MakePointWithTag(3, 4, 7) = (({result.point.X}, {result.point.Y}), {result.tag})");
    }

    #endregion
}
