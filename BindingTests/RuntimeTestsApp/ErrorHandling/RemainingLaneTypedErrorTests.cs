// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ErrorHandling;

/// <summary>
/// The same Swift error must surface as the same C# exception shape no matter which emitted
/// route the member took. <see cref="SyncErrorCascadeTests"/> pins that for the ordinary member
/// routes (native thunk, <c>@_cdecl</c> wrapper, direct call); this class pins it for the four
/// routes that are emitted by their own emitters and therefore had to be wired separately:
/// <list type="bullet">
///   <item>the protocol-proxy witness call (<c>ThrowingWitnessProxy</c>) — a Swift value obtained
///         as an existential and called through the generated proxy;</item>
///   <item>the generic-closure bridge's value-returning arm — a method-generic,
///         closure-bearing member that throws after the callback ran;</item>
///   <item>the generic-closure bridge's void arm — the same member with a <c>void</c>-returning
///         callback, which takes a separate bridge entry point with no result buffer;</item>
///   <item>the concrete-protocol specialization, both its synchronous arm (a static method with
///         a protocol-constrained method generic) and its async-generic-parent arm (an async
///         member on a generic parent, whose error arrives on an unmanaged error callback).</item>
/// </list>
/// Each lane gets two assertions: the exception is <c>SwiftException&lt;TError&gt;</c> carrying
/// the marshalled payload, and the Swift error box behind it is released exactly once. The
/// second is not implied by the first — the classifier hands the box to the exception, which
/// owns the sole release, so a route that released eagerly (or forgot to) still produces the
/// right exception type while corrupting or leaking the box.
///
/// <para>The lifetime probes throw <c>SyncCascadeTrackedClassError</c>, whose payload embeds a
/// registry-counted ref: a lost release keeps the ref live past the drain and an over-release
/// traps in the Swift runtime, so both directions fail rather than merely "did not crash".</para>
/// </summary>
public class RemainingLaneTypedErrorTests : TestBase
{
    public RemainingLaneTypedErrorTests(TestResults results) : base(results) { }

    // ── Lane 1: protocol proxy (witness dispatch) ───────────────────────────────────────

    public void TestProtocolProxyThrowSurfacesTypedException()
    {
        var witness = TestLibFunctions.MakeThrowingWitness();
        AssertEqual(6, witness.TagOrThrow(5), "the non-throwing outcome must be unaffected");

        var caught = CatchTyped<ThrowingWitnessError>(() => witness.TagOrThrow(-1));
        AssertEqual(ThrowingWitnessError.Negative, caught.Error,
            "the proxy route must marshal the thrown enum case, not just its description");
        AssertTrue(caught.ErrorHandle != IntPtr.Zero,
            "the proxy route must carry the live error box like every other route");

        // That enum has a single case, which is also C# `default`, so the assertion above is
        // satisfied by a route that built the right exception type over a payload it never read.
        // The tracked class error carries a value the caller chose, so a default payload reds it.
        ThrowAndDropProxyTrackedError(witness, 41);
    }

    public void TestProtocolProxyErrorBoxIsReleasedExactlyOnce()
    {
        var witness = TestLibFunctions.MakeThrowingWitness();
        LifetimeTracker.RunWithLeakCheck(() =>
        {
            for (int i = 0; i < 25; i++)
                ThrowAndDropProxyTrackedError(witness, i);
            DisplaceResidualCarrierReferences();
        }, "protocol-proxy typed throw");
    }

    // ── Lane 2: generic-closure bridge, value-returning arm ─────────────────────────────

    public void TestGenericClosureBridgeReturningArmThrowSurfacesTypedException()
    {
        using var reader = new DatabaseReader("outer");
        using var source = new DatabaseReader("primary");

        // readThenThrow runs the callback to completion and only then throws, so the bridge is
        // on its throw-after-callback path with a live result buffer to reclaim — the arm whose
        // error handling this lane rewires.
        var caught = CatchTyped<DatabaseReadError>(
            () => reader.ReadThenThrow<DatabaseReader>(db => db, source));
        AssertEqual(DatabaseReadError.AfterRead, caught.Error,
            "the bridge's returning arm must marshal the thrown enum case");
        AssertTrue(caught.ErrorHandle != IntPtr.Zero,
            "the bridge's returning arm must carry the live error box");

        // Single-case enum again: the assertion above cannot fail an unread payload. The tracked
        // variant takes the same returning arm and carries a value only marshalling produces.
        ThrowAndDropBridgeTrackedError(returningArm: true);
    }

    // ── Lane 3: generic-closure bridge, void arm ────────────────────────────────────────

    public void TestGenericClosureBridgeVoidArmThrowSurfacesTypedException()
    {
        using var reader = new DatabaseReader("outer");
        using var source = new DatabaseReader("primary");

        // A void-returning callback selects the bridge's separate void entry point, which has no
        // result buffer — a distinct emission site from the returning arm above.
        var caught = CatchTyped<SyncCascadeTrackedClassError>(
            () => reader.ReadThenThrowTracked(db => { }, source));
        using var payload = caught.Error;
        AssertNotNull(payload, "the bridge's void arm must marshal the class payload");
        AssertEqual(77, payload!.Code, "the payload must carry the value the fixture threw");
        AssertTrue(caught.ErrorHandle != IntPtr.Zero,
            "the bridge's void arm must carry the live error box");
    }

    public void TestGenericClosureBridgeErrorBoxIsReleasedExactlyOnce()
    {
        LifetimeTracker.RunWithLeakCheck(() =>
        {
            // Both arms in one probe: they release the same box through the same helper, and a
            // lost release on either leaves the tracked ref live at the drain.
            for (int i = 0; i < 25; i++)
                ThrowAndDropBridgeTrackedError(returningArm: i % 2 == 0);
            DisplaceResidualCarrierReferences();
        }, "generic-closure-bridge typed throw");
    }

    // ── Lane 4: concrete-protocol specialization, synchronous arm ───────────────────────

    public void TestConcreteSpecializationThrowSurfacesTypedException()
    {
        AssertTrue(ThrowingBytesNamespace.FitsWithin(new byte[] { 1, 2, 3 }, 10),
            "the non-throwing outcome must be unaffected");

        // The payload-bearing case is the load-bearing one: a classifier that matched the arm but
        // read nothing would still produce the right exception type with a default payload.
        var caught = CatchTyped<BytesValidationError>(
            () => ThrowingBytesNamespace.FitsWithin(new byte[0x1001], 10));
        using var payload = caught.Error;
        AssertNotNull(payload, "the specialization route must marshal the complex-enum payload");
        AssertEqual(BytesValidationError.CaseTag.TooLarge, payload!.Tag,
            "the thrown case must survive marshalling");
        AssertTrue(payload.TryGetTooLarge(out var reported), "the associated value must be readable");
        AssertEqual((nint)0x1001, reported, "the associated value must survive marshalling");
        AssertTrue(caught.ErrorHandle != IntPtr.Zero,
            "the specialization route must carry the live error box");
    }

    public void TestConcreteSpecializationErrorBoxIsReleasedExactlyOnce()
    {
        LifetimeTracker.RunWithLeakCheck(() =>
        {
            for (int i = 0; i < 25; i++)
                ThrowAndDropSpecializationTrackedError(i + 1);
            DisplaceResidualCarrierReferences();
        }, "concrete-specialization typed throw");
    }

    // ── Lane 5: concrete-protocol specialization, async generic parent ──────────────────

    public async Task TestAsyncGenericParentThrowSurfacesTypedException()
    {
        using var sink = TestLibFunctions.MakeDonationSink();
        using var donator = TestLibFunctions.MakeStringDonator(sink);

        // The error arrives on an unmanaged error callback that faults the Task, so the typed
        // shape has to be built inside that callback rather than at the call site.
        SwiftException<SyncCascadeTrackedClassError>? typed = null;
        SwiftException? untyped = null;
        try
        {
            await WithTimeout(donator.DonateOrThrowTrackedAsync("abcd"), DefaultAsyncTimeout);
        }
        catch (SwiftException<SyncCascadeTrackedClassError> e) { typed = e; }
        catch (SwiftException e) { untyped = e; }

        AssertTrue(untyped is null,
            $"the async-generic-parent route must not fall back to the untyped shape: {untyped?.Message}");
        AssertNotNull(typed, "DonateOrThrowTrackedAsync(\"abcd\") must fault the Task with the typed exception");
        using var payload = typed!.Error;
        AssertNotNull(payload, "the async error callback must marshal the class payload");
        AssertEqual(4, payload!.Code, "the payload must carry the name length the fixture threw");
        AssertTrue(typed.ErrorHandle != IntPtr.Zero,
            "the async-generic-parent route must carry the live error box");
        AssertEqual(0, sink.Count, "the throw precedes any record, so the sink stays empty");
    }

    public async Task TestAsyncGenericParentErrorBoxIsReleasedExactlyOnce()
    {
        using var sink = TestLibFunctions.MakeDonationSink();
        using var donator = TestLibFunctions.MakeStringDonator(sink);

        // Settle the preceding test's carrier BEFORE the window opens. The last exception this
        // lane produced stays reachable until the next call through it — a drain alone does not
        // release it — so its deallocation would otherwise land inside this probe with no matching
        // allocation and read as a negative live count. The displacing throws carry an error that
        // embeds nothing tracked, so this can only remove a stale release from the window; the 12
        // allocations below still have to balance.
        await DisplaceResidualAsyncCarrierReferences(donator);
        LifetimeTracker.Reset();
        try
        {
            for (int i = 0; i < 12; i++)
                await ThrowAndDropAsyncTrackedError(donator);

            // Same displacement at the far end of the window: without it the final iteration's
            // carrier is still reachable — from the lane itself and, on a conservatively scanned
            // thread, from a spilled slot of the frames the churn just ran in — when the assertion
            // drains. It only drops references and allocates nothing tracked, so a box that was
            // never released still fails below.
            await DisplaceResidualAsyncCarrierReferences(donator);
        }
        finally
        {
            LifetimeTracker.AssertNoLeaks("async-generic-parent typed throw");
        }
    }

    // ── Churn helpers ───────────────────────────────────────────────────────────────────

    // Kept out of the probe frames so neither the exception nor the payload is still rooted by a
    // live local when the leak assertion drains.

    private void ThrowAndDropProxyTrackedError(IThrowingWitness witness, int code)
    {
        var caught = CatchTyped<SyncCascadeTrackedClassError>(() => witness.TrackedThrow(code));
        using var payload = caught.Error;
        AssertNotNull(payload, "class payload must be marshalled");
        AssertEqual(code, payload!.Code, "the payload must carry the thrown value");
    }

    private void ThrowAndDropBridgeTrackedError(bool returningArm)
    {
        using var reader = new DatabaseReader("churn");
        using var source = new DatabaseReader("churn-source");
        var caught = returningArm
            ? CatchTyped<SyncCascadeTrackedClassError>(
                () => reader.ReadThenThrowTracked<DatabaseReader>(db => db, source))
            : CatchTyped<SyncCascadeTrackedClassError>(
                () => reader.ReadThenThrowTracked(db => { }, source));
        using var payload = caught.Error;
        AssertNotNull(payload, "class payload must be marshalled");
        AssertEqual(77, payload!.Code, "the payload must carry the thrown value");
    }

    private void ThrowAndDropSpecializationTrackedError(int length)
    {
        var caught = CatchTyped<SyncCascadeTrackedClassError>(
            () => ThrowingBytesNamespace.TrackedThrowOnBytes(new byte[length]));
        using var payload = caught.Error;
        AssertNotNull(payload, "class payload must be marshalled");
        AssertEqual(length, payload!.Code, "the payload must carry the byte count the fixture threw");
    }

    private async Task ThrowAndDropAsyncTrackedError(Donator<StringDonationItem> donator)
    {
        // Observe the faulted Task rather than rethrowing through the awaiter: the assertion under
        // test is the exception's identity and the box's ownership, and an AggregateException read
        // reaches both without an exception unwind on every iteration of the churn.
        var task = donator.DonateOrThrowTrackedAsync("abcde");
        await WithTimeout(task.ContinueWith(static _ => { },
            TaskContinuationOptions.ExecuteSynchronously), DefaultAsyncTimeout);

        AssertTrue(task.IsFaulted, "the throwing async member must fault its Task");
        var typed = task.Exception?.InnerException as SwiftException<SyncCascadeTrackedClassError>;
        AssertNotNull(typed, $"expected the typed exception, got {task.Exception?.InnerException?.GetType().Name}");
        using var payload = typed!.Error;
        AssertNotNull(payload, "class payload must be marshalled");
        AssertEqual(5, payload!.Code, "the payload must carry the name length the fixture threw");
    }

    // Same displacement, driven through the async lane so the overwritten slots are the ones the
    // async churn actually used. DonateOrThrowAsync("fail") faults with an error that embeds
    // nothing LifetimeTracker-counted, so this cannot turn a genuine leak green.
    private async Task DisplaceResidualAsyncCarrierReferences(Donator<StringDonationItem> donator)
    {
        for (int i = 0; i < 4; i++)
        {
            var task = donator.DonateOrThrowAsync("fail");
            await WithTimeout(task.ContinueWith(static _ => { },
                TaskContinuationOptions.ExecuteSynchronously), DefaultAsyncTimeout);
            AssertTrue(task.IsFaulted, "the displacing async throw must still fault its Task");
        }
    }

    // The collector scans this thread conservatively, so a reference to the churn's final
    // exception can survive in a spilled slot or a callee-saved register of the frames it ran in —
    // and that exception owns the error box, which owns the tracked ref. Re-running comparable
    // throws whose errors embed nothing tracked overwrites those slots with untracked references.
    // This only ever drops references and allocates nothing tracked, so an unbalanced retain on
    // the tracked path still fails the assertion.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DisplaceResidualCarrierReferences()
    {
        for (int i = 0; i < 4; i++)
        {
            var enumShaped = CatchTyped<BytesValidationError>(
                () => ThrowingBytesNamespace.FitsWithin(new byte[0x1001], 10));
            using (var enumPayload = enumShaped.Error)
                AssertNotNull(enumPayload, "the displacing throw must still marshal its payload");

            var structShaped = CatchTyped<SyncCascadeStructError>(
                () => TestLibFunctions.SyncCascadeLoadConfig("/etc/bad"));
            using (var structPayload = structShaped.Error)
                AssertNotNull(structPayload, "the displacing throw must still marshal its payload");
        }
    }

    // Runs the action, requires it to throw, and requires the thrown exception to be the typed
    // shape for TError. Catching plain SwiftException and then type-testing gives a failure
    // message that names what actually arrived — which is the whole distinction under test.
    private SwiftException<TError> CatchTyped<TError>(Action action)
    {
        try
        {
            action();
        }
        catch (SwiftException<TError> typed)
        {
            return typed;
        }
        catch (SwiftException untyped)
        {
            throw new AssertionException(
                $"Expected SwiftException<{typeof(TError).Name}> but got untyped SwiftException: {untyped.Message}");
        }
        throw new AssertionException($"Expected SwiftException<{typeof(TError).Name}> but nothing was thrown");
    }
}
