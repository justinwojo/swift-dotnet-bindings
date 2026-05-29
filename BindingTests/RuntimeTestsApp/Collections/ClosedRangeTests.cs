// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// End-to-end coverage for the <c>SwiftClosedRange&lt;Bound&gt;</c> stdlib generic.
/// Two directions exercised independently:
/// <list type="bullet">
///   <item>C# → Swift: build a <c>SwiftClosedRange</c> managed-side, pass it across
///     <c>@_cdecl</c>; Swift reads each endpoint via Swift's <c>lowerBound</c> /
///     <c>upperBound</c> getters.</item>
///   <item>Swift → C#: Swift builds the range, returns it through <c>@_cdecl</c>;
///     managed reads each endpoint via the wrapper's <c>LowerBound</c> /
///     <c>UpperBound</c> properties.</item>
/// </list>
/// Four Bound types pinned (<c>nint</c>, <c>long</c>, <c>double</c>, <c>SwiftString</c>)
/// so per-instantiation cached metadata and the Stride-vs-Size upperBound offset
/// (well-aligned primitives have stride == size; <c>SwiftString</c> is a frozen
/// 24-byte payload that exercises VWT-managed copy/destroy on a ref-bearing field).
/// </summary>
public class ClosedRangeTests : TestBase
{
    public ClosedRangeTests(TestResults results) : base(results) { }

    #region C# → Swift — bounds passed through ClosedRange parameter

    public void TestClosedRangeIntLowerBoundReadBySwift()
    {
        using var range = new SwiftClosedRange<nint>((nint)3, (nint)17);
        var lower = TestLibFunctions.ClosedRangeLowerInt(range);
        AssertEqual((nint)3, lower, "Swift reads lowerBound of SwiftClosedRange<nint>");
    }

    public void TestClosedRangeIntUpperBoundReadBySwift()
    {
        using var range = new SwiftClosedRange<nint>((nint)3, (nint)17);
        var upper = TestLibFunctions.ClosedRangeUpperInt(range);
        AssertEqual((nint)17, upper, "Swift reads upperBound of SwiftClosedRange<nint>");
    }

    public void TestClosedRangeIntCount()
    {
        // count = upper - lower + 1 — proves Swift sees BOTH bounds correctly, not
        // just a duplicated lower or upper. A swap would yield a negative count and
        // crash Swift's count getter; getting the right positive count means both
        // endpoints sit at the right stride-offset within the payload buffer.
        using var range = new SwiftClosedRange<nint>((nint)10, (nint)20);
        var count = TestLibFunctions.ClosedRangeCountInt(range);
        AssertEqual((nint)11, count, "ClosedRange<nint> count = upper - lower + 1");
    }

    public void TestClosedRangeIntContains()
    {
        using var range = new SwiftClosedRange<nint>((nint)5, (nint)15);
        AssertTrue(TestLibFunctions.ClosedRangeContainsInt(range, (nint)10), "ClosedRange<nint> contains midpoint");
        AssertTrue(TestLibFunctions.ClosedRangeContainsInt(range, (nint)5), "ClosedRange<nint> contains lower (closed)");
        AssertTrue(TestLibFunctions.ClosedRangeContainsInt(range, (nint)15), "ClosedRange<nint> contains upper (closed)");
        AssertTrue(!TestLibFunctions.ClosedRangeContainsInt(range, (nint)4), "ClosedRange<nint> excludes below-lower");
        AssertTrue(!TestLibFunctions.ClosedRangeContainsInt(range, (nint)16), "ClosedRange<nint> excludes above-upper");
    }

    public void TestClosedRangeIntSum()
    {
        // sum of 1..10 = 55. Iteration walks both endpoints — a wrapper that mis-orders
        // the bounds would either crash (Swift asserts lower<=upper in iteration setup)
        // or produce 0/negative if both fields read the same offset.
        using var range = new SwiftClosedRange<nint>((nint)1, (nint)10);
        var sum = TestLibFunctions.ClosedRangeSumInt(range);
        AssertEqual((nint)55, sum, "Sum of integers in ClosedRange<nint> 1...10 is 55");
    }

    #endregion

    #region Swift → C# — bounds read from returned ClosedRange

    public void TestClosedRangeIntReturnedFromSwift()
    {
        using var range = TestLibFunctions.MakeClosedRangeInt(lower: (nint)100, upper: (nint)200);
        AssertEqual((nint)100, range.LowerBound, "Swift-built ClosedRange<nint>.LowerBound");
        AssertEqual((nint)200, range.UpperBound, "Swift-built ClosedRange<nint>.UpperBound");
    }

    public void TestClosedRangeInt64ReturnedFromSwift()
    {
        using var range = TestLibFunctions.MakeClosedRangeInt64(lower: -1_000_000L, upper: 1_000_000L);
        AssertEqual(-1_000_000L, range.LowerBound, "Swift-built ClosedRange<Int64>.LowerBound");
        AssertEqual(1_000_000L, range.UpperBound, "Swift-built ClosedRange<Int64>.UpperBound");
    }

    public void TestClosedRangeDoubleReturnedFromSwift()
    {
        using var range = TestLibFunctions.MakeClosedRangeDouble(lower: 1.5, upper: 9.75);
        AssertEqual(1.5, range.LowerBound, "Swift-built ClosedRange<Double>.LowerBound");
        AssertEqual(9.75, range.UpperBound, "Swift-built ClosedRange<Double>.UpperBound");
    }

    public void TestClosedRangeStringReturnedFromSwift()
    {
        // SwiftString is the non-trivial Bound: 24-byte frozen struct with ref-counted
        // payload. The wrapper allocates 2*stride(SwiftString) = 48 bytes; the value
        // witness table InitializeWithCopy must retain BOTH bound strings independently
        // or a follow-up read would tombstone. A wrong upperBound offset would yield
        // garbled bytes that crash SwiftString's UTF-8 read.
        using var range = TestLibFunctions.MakeClosedRangeString(lower: new SwiftString("alpha"), upper: new SwiftString("omega"));
        AssertEqual("alpha", range.LowerBound.ToString(), "Swift-built ClosedRange<String>.LowerBound");
        AssertEqual("omega", range.UpperBound.ToString(), "Swift-built ClosedRange<String>.UpperBound");
    }

    #endregion

    #region Round-trip — managed → swift → managed

    public void TestClosedRangeIntRoundTripShifted()
    {
        // Build managed-side, pass to Swift, Swift returns a new range with both
        // endpoints shifted by +5. Exercises the full bidirectional cycle in one call:
        // marshal-in (C# wrapper → Swift ClosedRange), Swift-side read of both bounds,
        // Swift-side construct, marshal-out (Swift ClosedRange → C# wrapper).
        using var input = new SwiftClosedRange<nint>((nint)10, (nint)20);
        using var shifted = TestLibFunctions.ShiftedClosedRangeInt(input, delta: (nint)5);
        AssertEqual((nint)15, shifted.LowerBound, "Round-tripped + shifted ClosedRange<nint>.LowerBound");
        AssertEqual((nint)25, shifted.UpperBound, "Round-tripped + shifted ClosedRange<nint>.UpperBound");
    }

    #endregion

    #region Optional<ClosedRange> — Some/None through Optional parameter & return

    // ClosedRange is a handle-backed stdlib wrapper, not a by-value .Buffer struct: the
    // Optional<ClosedRange<Float>> parameter must pack as SwiftOptional<SwiftClosedRange<Float>>
    // (range value + tag byte, metadata-driven), NOT SwiftOptional<SwiftClosedRange<Float>.Buffer>.
    // This is the exact C# → Swift path RealityFoundation's PhysicsRevoluteJoint(angularLimit:)
    // exercises.

    public void TestOptionalClosedRangeFloatSomeLowerBound()
    {
        using var range = new SwiftClosedRange<float>(2.5f, 8.0f);
        var lower = TestLibFunctions.OptionalClosedRangeLowerFloat(range);
        AssertEqual(2.5f, lower, "Swift reads lowerBound of Some(SwiftClosedRange<float>)");
    }

    public void TestOptionalClosedRangeFloatSomeSpan()
    {
        // upper - lower reads BOTH endpoints out of the packed Some payload; a swapped or
        // single-bound pack would yield a wrong (or negative) span.
        using var range = new SwiftClosedRange<float>(2.5f, 8.0f);
        var span = TestLibFunctions.OptionalClosedRangeSpanFloat(range);
        AssertEqual(5.5f, span, "Swift reads upper-lower of Some(SwiftClosedRange<float>)");
    }

    public void TestOptionalClosedRangeFloatNone()
    {
        // null → None tag. The sentinel return (-1) and the explicit nil check both confirm
        // Swift saw .none, not a Some over zeroed bytes.
        var lower = TestLibFunctions.OptionalClosedRangeLowerFloat(null);
        AssertEqual(-1f, lower, "None Optional<ClosedRange<float>> yields nil sentinel");
        AssertTrue(TestLibFunctions.OptionalClosedRangeIsNilFloat(null), "None marshals as nil");
    }

    public void TestOptionalClosedRangeFloatSomeZeroIsNotNil()
    {
        // 0...0 is a legitimate non-nil Some whose payload bytes are all zero — the None tag
        // must come from the tag byte, never inferred from a zeroed payload.
        using var range = new SwiftClosedRange<float>(0.0f, 0.0f);
        AssertTrue(!TestLibFunctions.OptionalClosedRangeIsNilFloat(range), "Some(0...0) is non-nil despite zero payload");
    }

    public void TestOptionalClosedRangeFloatReturnedSome()
    {
        using var range = TestLibFunctions.MakeOptionalClosedRangeFloat(lower: 1.25f, upper: 4.75f, shouldReturn: true);
        AssertTrue(range != null, "Swift-returned Optional<ClosedRange<float>> is Some");
        AssertEqual(1.25f, range!.LowerBound, "Returned Some(ClosedRange<float>).LowerBound");
        AssertEqual(4.75f, range.UpperBound, "Returned Some(ClosedRange<float>).UpperBound");
    }

    public void TestOptionalClosedRangeFloatReturnedNone()
    {
        var range = TestLibFunctions.MakeOptionalClosedRangeFloat(lower: 1.25f, upper: 4.75f, shouldReturn: false);
        AssertTrue(range == null, "Swift-returned Optional<ClosedRange<float>> is None");
    }

    #endregion

    #region [Optional<ClosedRange>] — Some/None elements through array parameter

    // [ClosedRange<Float>?] marshals as SwiftArray<SwiftOptional<SwiftClosedRange<Float>>>: BOTH the
    // array element generic AND the per-element SwiftOptional generic must name the handle-backed
    // wrapper, never a `.Buffer` struct — SwiftClosedRange has no nested `.Buffer`. This is the
    // container-element sibling of the PhysicsRevoluteJoint(angularLimit:) direct-parameter path.

    public void TestOptionalClosedRangeFloatArraySpanSum()
    {
        // [1...3 (span 2), nil (0), 0...5 (span 5)] → 7.0. Reads both endpoints of each Some element
        // out of the packed array buffer; a swapped or single-bound pack would skew the sum.
        using var r1 = new SwiftClosedRange<float>(1.0f, 3.0f);
        using var r2 = new SwiftClosedRange<float>(0.0f, 5.0f);
        var ranges = new SwiftClosedRange<float>?[] { r1, null, r2 };
        var sum = TestLibFunctions.SumOptionalClosedRangeSpansFloat(ranges);
        AssertEqual(7.0f, sum, "Sum of non-nil ClosedRange<float>? spans in array (2.0 + 0 + 5.0)");
    }

    public void TestOptionalClosedRangeFloatArrayNilCount()
    {
        // Two nil entries interleaved with Some — proves None elements pack as the None tag,
        // distinct from a Some over zeroed payload bytes.
        using var r1 = new SwiftClosedRange<float>(1.0f, 3.0f);
        using var r2 = new SwiftClosedRange<float>(2.0f, 9.0f);
        var ranges = new SwiftClosedRange<float>?[] { r1, null, r2, null };
        var nilCount = TestLibFunctions.CountNilClosedRangesFloat(ranges);
        AssertEqual((nint)2, nilCount, "Two nil entries in [ClosedRange<float>?] array");
    }

    public void TestOptionalClosedRangeFloatArrayAllSome()
    {
        // No nils — every element takes the Some branch; verifies both the span sum and a zero nil count.
        using var r1 = new SwiftClosedRange<float>(0.0f, 1.0f);
        using var r2 = new SwiftClosedRange<float>(1.0f, 4.0f);
        var ranges = new SwiftClosedRange<float>?[] { r1, r2 };
        AssertEqual((nint)0, TestLibFunctions.CountNilClosedRangesFloat(ranges), "All-Some array has zero nils");
        AssertEqual(4.0f, TestLibFunctions.SumOptionalClosedRangeSpansFloat(ranges), "All-Some spans 1.0 + 3.0 = 4.0");
    }

    #endregion
}
