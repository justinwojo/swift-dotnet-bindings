// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Swift.Runtime;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for async tuple returns with generic elements (GenericContext threading through
/// the async tuple pipeline) and for the working surface of async-bearing generic types.
///
/// Part A: AsyncTupleWorker — non-generic async tuple returns (regression).
/// Part B: AsyncGenericContainer&lt;T&gt; — the synchronous, CallConvCdecl surface
///         (construction + StoredValue generic-static-dispatch property) round-trips.
///         Its async methods (processAsync / fetchOrThrow / fetchPair) are intentionally
///         NOT bound: a @_silgen_name async wrapper on a generic-parent extension is itself
///         a generic instance method, so Swift expects `self` + the parent's type metadata
///         in the implicit self / metadata registers while a fixed CallConvSwift P/Invoke can
///         only pass them as trailing IntPtr args — an ABI mismatch that SIGSEGVs. The
///         generator suppresses those members at source; the "properly suppressed"
///         invariant is asserted in Swift.Bindings.Unit.Tests
///         (MemberValidationPipelineTests.ValidateMethodEmission_AsyncOnGenericParent_*)
///         rather than here, because a dropped member cannot be called from C#. The correct
///         long-term fix is a generic-static-dispatch @_cdecl async bridge (the async analog
///         of the StoredValue getter).
/// </summary>
public class AsyncGenericTupleTests : TestBase
{
    public AsyncGenericTupleTests(TestResults results) : base(results) { }

    #region AsyncTupleWorker — Non-generic async tuple regression

    public async Task TestAsyncTupleWorker_IntPair()
    {
        var worker = new AsyncTupleWorker("worker");
        var (a, b) = await WithTimeout(worker.FetchIntPairAsync(), DefaultAsyncTimeout);
        AssertEqual(10, a, "First element should be 10");
        AssertEqual(20, b, "Second element should be 20");
        TestLogger.Info($"AsyncTupleWorker.FetchIntPair() = ({a}, {b})");
    }

    public async Task TestAsyncTupleWorker_LabeledPair()
    {
        var worker = new AsyncTupleWorker("hello");
        var (label, number) = await WithTimeout(worker.FetchLabeledPairAsync(), DefaultAsyncTimeout);
        AssertEqual("hello", label, "Label should match constructor value");
        AssertEqual(42, number, "Number should be 42");
        TestLogger.Info($"AsyncTupleWorker.FetchLabeledPair() = ('{label}', {number})");
    }

    #endregion

    #region AsyncGenericContainer<T> — working CallConvCdecl surface survives async suppression

    // The async members of AsyncGenericContainer<T> are ABI-unsafe on the legacy
    // open-generic surface and are suppressed by the generator (see class summary +
    // AsyncGenericParentSuppressionTests). This test guards that the suppression is
    // surgical: the type's synchronous, generic-static-dispatch CallConvCdecl surface
    // (constructor + StoredValue) still binds and round-trips through the same parent
    // type metadata the async path could not thread.

    public Task TestAsyncGenericContainer_StoredValueRoundTrips()
    {
        var container = new AsyncGenericContainer<NumberItem>(new NumberItem(42));
        var stored = container.StoredValue;
        AssertEqual(42, stored.Value, "StoredValue should round-trip the constructor payload");
        AssertEqual("number:42", stored.Label, "StoredValue payload should expose its Swift-computed label");
        TestLogger.Info($"AsyncGenericContainer<NumberItem>.StoredValue.Value = {stored.Value}");
        return Task.CompletedTask;
    }

    public Task TestAsyncGenericContainer_StoredValueSetter()
    {
        var container = new AsyncGenericContainer<NumberItem>(new NumberItem(1));
        container.StoredValue = new NumberItem(99);
        AssertEqual(99, container.StoredValue.Value, "StoredValue setter should replace the payload");
        TestLogger.Info($"AsyncGenericContainer<NumberItem>.StoredValue after set = {container.StoredValue.Value}");
        return Task.CompletedTask;
    }

    #endregion
}
