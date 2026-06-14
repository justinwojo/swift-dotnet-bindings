// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
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

    #region Tuple-of-class-element parameters — @_cdecl buffer marshalling (borrowed handle + keep-alive)

    // A tuple PARAMETER whose elements include pure Swift classes is marshalled through the raw
    // @_cdecl buffer: each class element is written as its borrowed (+0) object handle at the
    // element's ABI offset, and the owning ValueTuple is kept alive past the native call. The
    // Swift wrapper retains each element via a typed `.pointee` load, so the borrowed handle
    // survives the call. Value oracles below grade on the round-tripped result, not just liveness.

    public void TestSumBoxedPair_TwoClassElements()
    {
        // Two pointer-width class slots written as borrowed handles at distinct offsets. Both
        // elements' stored values must reach Swift intact.
        var a = new TupleBoxedInt(value: 10);
        var b = new TupleBoxedInt(value: 32);
        var sum = TestLibFunctions.SumBoxedPair((a, b));
        AssertEqual(42, sum, "SumBoxedPair(10, 32)");
        TestLogger.Info($"SumBoxedPair((10, 32)) = {sum}");
    }

    public void TestCombineBoxAndScalar_ClassPlusPrimitive()
    {
        // Mixed element-mix: the class element is written as a borrowed handle (and kept alive),
        // the trailing primitive is written by value. Both must round-trip.
        var box = new TupleBoxedInt(value: 100);
        var combined = TestLibFunctions.CombineBoxAndScalar((box, 23));
        AssertEqual(123, combined, "CombineBoxAndScalar(100, 23)");
        TestLogger.Info($"CombineBoxAndScalar((100, 23)) = {combined}");
    }

    public void TestSumBoxedPair_KeepAliveUnderGCPressure()
    {
        // The borrowed handle is only safe because the owning ValueTuple is GC.KeepAlive'd past
        // the call. Constructing the elements inline (no surviving local reference besides the
        // tuple argument) and forcing a collection on another thread before the call returns is
        // the shape that would surface a missing keep-alive as a use-after-free. A clean
        // round-trip across repeated iterations is the durable no-crash/correct-value oracle.
        for (int i = 0; i < 50; i++)
        {
            var sum = TestLibFunctions.SumBoxedPair((new TupleBoxedInt(value: i), new TupleBoxedInt(value: i + 1)));
            AssertEqual(2 * i + 1, sum, $"SumBoxedPair keep-alive iteration {i}");
            GC.Collect();
        }
        TestLogger.Info("SumBoxedPair keep-alive stress passed");
    }

    #endregion

    #region Tuple-of-String-element parameters — @_cdecl buffer marshalling (16-byte borrowed value)

    // A Swift.String tuple element occupies a 16-byte (two-word) value slot — NOT the @_cdecl
    // String-parameter fast path (utf8 ptr+len). The element is projected as a Swift.SwiftString
    // that owns its storage, so its borrowed 16-byte value is bit-copied into the slot and the
    // owning ValueTuple is GC.KeepAlive'd past the call (same source keep-alive as a class slot).
    // The Swift wrapper's typed `.pointee` load retains each string for the call's duration. Value
    // oracles below grade on the round-tripped string content, not just liveness.

    public void TestJoinStringPair_TwoStringElements()
    {
        // Two 16-byte String slots written as borrowed copies at distinct offsets. Both elements'
        // full UTF-8 content must reach Swift intact (not just a prefix or pointer).
        var joined = TestLibFunctions.JoinStringPair(("hello", "world"));
        AssertEqual("hello|world", joined, "JoinStringPair(hello, world)");
        TestLogger.Info($"JoinStringPair((hello, world)) = {joined}");
    }

    public void TestDescribeLabeledBox_StringPrimitiveClassMix()
    {
        // All three @_cdecl buffer write modes in one allocation: a 16-byte borrowed String value,
        // a by-value primitive, and a borrowed class handle (kept alive). All three must round-trip.
        var box = new TupleBoxedInt(value: 7);
        var described = TestLibFunctions.DescribeLabeledBox(("count", 5, box));
        AssertEqual("count=5+7", described, "DescribeLabeledBox(count, 5, 7)");
        TestLogger.Info($"DescribeLabeledBox((count, 5, 7)) = {described}");
    }

    public void TestJoinStringPair_UnderGCPressure()
    {
        // Each slot holds a borrowed 16-byte copy aliasing the SwiftString element the tuple owns;
        // the owning ValueTuple is the only root, kept alive by the generated GC.KeepAlive. Forcing a
        // collection each iteration is the shape that would surface a missing keep-alive as a
        // use-after-free of the 16-byte borrowed value. A clean round-trip with distinct per-iteration
        // content is the durable no-crash/correct-value oracle. Multi-byte UTF-8 (é) confirms the full
        // two-word value — not an ASCII small-string prefix — round-trips.
        for (int i = 0; i < 50; i++)
        {
            var joined = TestLibFunctions.JoinStringPair(($"kéy{i}", $"val{i}"));
            AssertEqual($"kéy{i}|val{i}", joined, $"JoinStringPair using-lifetime iteration {i}");
            GC.Collect();
        }
        TestLogger.Info("JoinStringPair using-lifetime stress passed");
    }

    #endregion

    #region Tuple-of-composition-existential parameters — @_cdecl buffer marshalling (EC2 borrowed container)

    // A composition existential element (any P & Q, EC2 — two non-marker protocols) occupies a
    // 48-byte (six-word) opaque-existential slot sized by TypeMetadata.GetExistentialTypeMetadata(2).
    // The element is projected as the public composition interface (IAgeableAndNameable), whose only
    // implementer is the Swift-vended {Composition}Proxy. The buffer writer projects each element to
    // its ExistentialContainer2 via ISwiftExistentialConvertible.GetExistentialContainer() — for a
    // composition this is ALWAYS a borrowed (+0) container aliasing the proxy's sole construction +1 —
    // and bit-copies that container into the slot. The owning ValueTuple is GC.KeepAlive'd past the
    // call (same source keep-alive as the class/String slots) so a mid-call finalizer can't release a
    // proxy whose container Swift is still borrowing. The Swift wrapper's typed `.pointee` load
    // reconstructs the tuple of existentials for the call's duration.

    public void TestDescribeNameableAgeablePair_TwoExistentialElements()
    {
        // Two 48-byte EC2 slots written as borrowed containers at distinct metadata offsets. Both
        // composition existentials must round-trip their `name`/`age` fields intact.
        var first = TestLibFunctions.MakeTrackedNameableAgeable(1);
        var second = TestLibFunctions.MakeTrackedNameableAgeable(2);
        var described = TestLibFunctions.DescribeNameableAgeablePair((first, second));
        AssertEqual("Tracked1:1 & Tracked2:2", described, "DescribeNameableAgeablePair(Tracked1, Tracked2)");
        TestLogger.Info($"DescribeNameableAgeablePair((Tracked1, Tracked2)) = {described}");
        (first as IDisposable)?.Dispose();
        (second as IDisposable)?.Dispose();
    }

    public void TestDescribeNameableAgeablePair_UnderGCPressure()
    {
        // Each slot holds a borrowed 48-byte EC2 container aliasing its source proxy's sole +1; the
        // owning ValueTuple is the only root, kept alive by the generated GC.KeepAlive. Forcing a
        // collection right before each borrow is the shape that would surface a missing keep-alive as
        // a use-after-free of the borrowed container. Like the EC2 single-arg probe, this cannot go
        // deterministically red (the caller already roots both proxies to pass and Dispose them), so
        // the oracle is correct round-trip + no leak under induced GC pressure across iterations.
        LifetimeTracker.Reset();
        PassBorrowedExistentialTupleUnderGcPressure(40);
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        GC.Collect();
        LifetimeTracker.AssertNoLeaks("borrowed EC2 tuple args: each proxy must deinit after Dispose; no UAF, no leak");
        TestLogger.Info("DescribeNameableAgeablePair: 40 borrowed EC2 tuple-arg pairs round-tripped under GC pressure; no crash, no leak");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PassBorrowedExistentialTupleUnderGcPressure(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            var first = TestLibFunctions.MakeTrackedNameableAgeable(i);
            var second = TestLibFunctions.MakeTrackedNameableAgeable(i + 1);
            GC.Collect();
            var described = TestLibFunctions.DescribeNameableAgeablePair((first, second));
            var expected = $"Tracked{i}:{i} & Tracked{i + 1}:{i + 1}";
            if (described != expected)
                throw new AssertionException($"borrowed EC2 tuple arg: expected '{expected}', got '{described}'");
            (first as IDisposable)?.Dispose();
            (second as IDisposable)?.Dispose();
        }
    }

    #endregion
}
