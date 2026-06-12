// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime coverage for the open-generic *constrained free functions* in Generics/Functions.swift
/// (and describeConstrained in Generics/Constraints.swift). These emit as open-generic
/// <c>&lt;T&gt;</c> methods that pass the payload plus TypeMetadata and one ProtocolWitnessTable
/// handle per constraint to a <c>CallConvSwift</c> P/Invoke with no <c>@_cdecl</c> wrapper — the
/// generic-free-function fallback path, stamped <c>[Obsolete(SB0001)]</c>. They were emitted and
/// compiled but exercised by no test; this class reaches them on both runtimes.
///
/// ABI Coverage Grid — generics corner. These cells map directly onto the CSM-filter latents the
/// grid is meant to reach: single-protocol constraint (<c>constrained</c>), protocol composition
/// <c>P &amp; Q</c> (<c>multiConstrained</c> / <c>describeConstrained</c>), and a multi-type-param
/// where-clause (<c>compareIdentifiables&lt;T, U&gt; where T: Describable</c>).
///
/// The pre-existing <c>pair&lt;T, U&gt;</c> tests use the identical [Obsolete] CallConvSwift
/// fallback shape, so CS0618 is suppressed locally per call, matching that precedent.
/// </summary>
public class ConstrainedGenericFreeFunctionTests : TestBase
{
    public ConstrainedGenericFreeFunctionTests(TestResults results) : base(results) { }

    // MARK: - constrained<T: Describable> — single protocol constraint, String return

    public void TestConstrained_SingleConstraint_StructConformer_RoundTripsDescription()
    {
        using var item = new SimpleItem(id: "node-7", label: "alpha");
#pragma warning disable CS0618 // [Obsolete] — CallConvSwift fallback for generic free functions
        var result = TestLibFunctions.Constrained(item);
#pragma warning restore CS0618
        AssertEqual("[node-7] alpha", result, "constrained<SimpleItem>");
    }

    public void TestConstrained_SingleConstraint_ClassConformer_RoundTripsDescription()
    {
        using var entity = new MultiProtocolEntity(id: "ent-3", name: "beta");
#pragma warning disable CS0618
        var result = TestLibFunctions.Constrained(entity);
#pragma warning restore CS0618
        AssertEqual("[ent-3] beta", result, "constrained<MultiProtocolEntity>");
    }

    // MARK: - multiConstrained<T: Describable & TestIdentifiable> — protocol composition P & Q

    public void TestMultiConstrained_Composition_StructConformer_RoundTrips()
    {
        using var item = new SimpleItem(id: "node-7", label: "alpha");
#pragma warning disable CS0618
        var result = TestLibFunctions.MultiConstrained(item);
#pragma warning restore CS0618
        // Swift: "[\(item.id)] \(item.describe())" where describe() == "[node-7] alpha".
        AssertEqual("[node-7] [node-7] alpha", result, "multiConstrained<SimpleItem>");
    }

    public void TestMultiConstrained_Composition_ClassConformer_RoundTrips()
    {
        using var entity = new MultiProtocolEntity(id: "ent-3", name: "beta");
#pragma warning disable CS0618
        var result = TestLibFunctions.MultiConstrained(entity);
#pragma warning restore CS0618
        AssertEqual("[ent-3] [ent-3] beta", result, "multiConstrained<MultiProtocolEntity>");
    }

    // MARK: - describeConstrained<T> where T: Describable, T: TestIdentifiable — multi where-clause

    public void TestDescribeConstrained_MultiWhereClause_RoundTrips()
    {
        using var item = new SimpleItem(id: "id-42", label: "gamma");
#pragma warning disable CS0618
        var result = TestLibFunctions.DescribeConstrained(item);
#pragma warning restore CS0618
        AssertEqual("[id-42] [id-42] gamma", result, "describeConstrained<SimpleItem>");
    }

    // MARK: - compareIdentifiables<T, U> where T: Describable — 2 type params + where-clause, Bool

    public void TestCompareIdentifiables_TwoParams_SameId_ReturnsTrue()
    {
        using var a = new SimpleItem(id: "match", label: "alpha");
        using var b = new MultiProtocolEntity(id: "match", name: "beta");
#pragma warning disable CS0618
        var result = TestLibFunctions.CompareIdentifiables(a, b);
#pragma warning restore CS0618
        AssertTrue(result, "compareIdentifiables same id");
    }

    public void TestCompareIdentifiables_TwoParams_DistinctId_ReturnsFalse()
    {
        using var a = new SimpleItem(id: "left", label: "alpha");
        using var b = new MultiProtocolEntity(id: "right", name: "beta");
#pragma warning disable CS0618
        var result = TestLibFunctions.CompareIdentifiables(a, b);
#pragma warning restore CS0618
        AssertFalse(result, "compareIdentifiables distinct id");
    }

    // NOTE: sumTwo<T: Summable>(_:_:) -> T is deliberately NOT covered here. `Summable` is a
    // Self-requirement protocol (`func add(_ other: Self) -> Self`), so its projected C# interface
    // `ISummable` carries `Swift.AnyType Add(Swift.AnyType)` and NO value-type conformer is emitted
    // as `: ISummable` (the witness table is registered for Swift-internal/existential use only).
    // The open-generic `SumTwo<T> where T : ISummable` therefore has no satisfying C# argument and
    // is uncallable from C# by any path — a by-design boundary of Self-requirement protocol
    // constraints, graded `by-design-gray` in the ABI coverage grid, not a green round-trip cell.
}
