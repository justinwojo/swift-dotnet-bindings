// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime regression for the parent-only async VOID CSM gap — the void-return
/// sibling of <see cref="PatParentAsyncMethodsTests"/> (which covers
/// value-RETURNING async on a generic struct parent).
/// <para>
/// Before this work, the parent-only async CSM path hard-rejected
/// void-returning async methods at the pairing validator
/// (<c>IsEmittableParentOnlyAsyncPairing</c> returned <c>false</c> for an
/// empty-tuple return), so each async void method on a generic parent fell
/// through to the catch-all skip and never emitted a wrapper. This is the exact
/// shape of ActivityKit <c>Activity&lt;T&gt;.update</c>/<c>end</c> and TipKit
/// <c>Tips.Event&lt;T&gt;.donate</c>.
/// </para>
/// <para>
/// After the fix, each async void method (throwing and non-throwing,
/// parameterless and <c>Swift.String</c>-parameterized) on
/// <c>Donator&lt;Item: DonationItem&gt;</c> emits a per-conformer NON-generic
/// <c>Task</c> extension whose <c>@_cdecl</c> completion callback carries ONLY
/// the GCHandle context — no result pointer is allocated, passed, or freed.
/// </para>
/// <para>
/// The void methods can't mutate their value-type parent, so they observe their
/// effect through a shared <c>DonationSink</c> reference. Reading
/// <c>sink.Count</c>/<c>sink.Last</c> after the await is the runtime witness
/// that the void body actually ran across the async hop.
/// </para>
/// </summary>
public class PatParentAsyncVoidMethodsTests : TestBase
{
    public PatParentAsyncVoidMethodsTests(TestResults results) : base(results) { }

    public async Task TestStringDonator_DonateAsync_VoidNonThrowingNoParam()
    {
        // Marquee shape: async void, non-throwing, no parameters. The @_cdecl
        // wrapper installs the 1-arg success completion `completion(context)`
        // (no errorCallback, no resultPtr) and the C# extension returns a
        // non-generic Task backed by a plain TaskCompletionSource. The await
        // resolving at all proves the void completion fired; sink.Count/Last
        // prove the Swift body ran.
        using var sink = Functions.MakeDonationSink();
        using var donator = Functions.MakeStringDonator(sink);

        await WithTimeout(donator.DonateAsync(), DefaultAsyncTimeout);

        AssertEqual(1, sink.Count, "DonateAsync — void body recorded exactly once across the async hop");
        AssertEqual("donate", sink.Last, "DonateAsync — void body wrote its sentinel to the shared sink");
    }

    public async Task TestStringDonator_DonateNamedAsync_VoidNonThrowingStringParam()
    {
        // async void, non-throwing, one Swift.String parameter. Exercises the
        // Utf8Slice (ptr,len) param path together with the void completion: the
        // string marshals across, the completion still carries only context.
        using var sink = Functions.MakeDonationSink();
        using var donator = Functions.MakeStringDonator(sink);

        await WithTimeout(donator.DonateNamedAsync("hello"), DefaultAsyncTimeout);

        AssertEqual(1, sink.Count, "DonateNamedAsync — recorded once");
        AssertEqual("hello", sink.Last, "DonateNamedAsync — string parameter round-trips into the sink");
    }

    public async Task TestStringDonator_DonateOrThrowAsync_VoidThrowingSuccessPath()
    {
        // Throwing async void, success path. The wrapper installs BOTH a 1-arg
        // success completion and a 2-arg errorCallback inside a do/catch; the
        // body does not throw here, so the success completion must fire and the
        // errorCallback must stay unfired. No result buffer is ever allocated.
        using var sink = Functions.MakeDonationSink();
        using var donator = Functions.MakeStringDonator(sink);

        await WithTimeout(donator.DonateOrThrowAsync("ok"), DefaultAsyncTimeout);

        AssertEqual(1, sink.Count, "DonateOrThrowAsync(\"ok\") — success path records once");
        AssertEqual("ok", sink.Last, "DonateOrThrowAsync(\"ok\") — success path round-trips the name");
    }

    public async Task TestStringDonator_DonateOrThrowAsync_VoidThrowingErrorPath()
    {
        // Throwing async void, error path. `name == "fail"` drives the catch arm
        // of the wrapper, which calls errorCallback(errorPtr, context). The C#
        // error callback must fault the (non-generic) Task with a SwiftException
        // — NOT TrySetResult — and must do so WITHOUT touching a result buffer
        // (the void holder parks IntPtr.Zero in the result slot, so the error
        // callback's conditional free is a no-op). The body must not record.
        using var sink = Functions.MakeDonationSink();
        using var donator = Functions.MakeStringDonator(sink);

        SwiftException? caught = null;
        try
        {
            await WithTimeout(donator.DonateOrThrowAsync("fail"), DefaultAsyncTimeout);
        }
        catch (SwiftException e)
        {
            caught = e;
        }

        AssertTrue(caught is not null,
            "DonateOrThrowAsync(\"fail\") — void throwing wrapper must fault the Task with SwiftException");
        AssertEqual(0, sink.Count,
            "DonateOrThrowAsync(\"fail\") — error precedes the record, so the sink stays empty");
    }

    public async Task TestIntDonator_DonateAsync_VoidNonThrowingSecondConformer()
    {
        // Second closed conformer: Item = IntDonationItem. Must be a DISTINCT
        // extension method (own cdecl symbol, own extension class) from the
        // StringDonationItem pairing — not a shared method switching on metadata.
        // Confirms parent-only void specialization closes per-conformer.
        using var sink = Functions.MakeDonationSink();
        using var donator = Functions.MakeIntDonator(sink);

        await WithTimeout(donator.DonateAsync(), DefaultAsyncTimeout);

        AssertEqual(1, sink.Count, "Int-conformer DonateAsync — void body recorded once");
        AssertEqual("donate", sink.Last, "Int-conformer DonateAsync — sentinel written");
    }

    public async Task TestVoidAsync_CrossConformerInterleavedCompleteIndependently()
    {
        // Two-conformer separation under interleaved continuation: kick off both
        // void awaits (each over its OWN sink) before either completes. Catches
        // a class of bugs where a per-conformer success completion dispatches to
        // the wrong TaskCompletionSource (e.g. via shared static state rather
        // than the per-invocation GCHandle holder). Each Task must resolve
        // independently and each sink must reflect only its own donator.
        using var stringSink = Functions.MakeDonationSink();
        using var intSink = Functions.MakeDonationSink();
        using var stringDonator = Functions.MakeStringDonator(stringSink);
        using var intDonator = Functions.MakeIntDonator(intSink);

        var stringTask = stringDonator.DonateNamedAsync("from-string");
        var intTask = intDonator.DonateNamedAsync("from-int");

        await WithTimeout(stringTask, DefaultAsyncTimeout);
        await WithTimeout(intTask, DefaultAsyncTimeout);

        AssertEqual(1, stringSink.Count, "Interleaved — string sink recorded exactly its own donation");
        AssertEqual("from-string", stringSink.Last, "Interleaved — string sink holds its own payload");
        AssertEqual(1, intSink.Count, "Interleaved — int sink recorded exactly its own donation");
        AssertEqual("from-int", intSink.Last, "Interleaved — int sink holds its own payload");
    }

    public async Task TestStringDonator_DonateAsync_RepeatedAwaitsAccumulate()
    {
        // Sequential re-entry on one donator/sink: each await must register,
        // launch, complete, and unregister its own cancel-task entry cleanly so
        // the next call starts from a clean producer-registry slot. Three awaits
        // accumulate to Count == 3 — a leak or mis-dispatch in the holder/cancel
        // bookkeeping would drop or double-count one.
        using var sink = Functions.MakeDonationSink();
        using var donator = Functions.MakeStringDonator(sink);

        await WithTimeout(donator.DonateAsync(), DefaultAsyncTimeout);
        await WithTimeout(donator.DonateNamedAsync("second"), DefaultAsyncTimeout);
        await WithTimeout(donator.DonateOrThrowAsync("third"), DefaultAsyncTimeout);

        AssertEqual(3, sink.Count, "Repeated void awaits accumulate to three recorded calls");
        AssertEqual("third", sink.Last, "Repeated void awaits — last write wins");
    }

    public async Task TestStringDonator_DonateAfterDelayAsync_VoidCancellationFaultsAndSkipsRecord()
    {
        // Void CANCELLATION path. A pre-canceled token makes the extension's
        // cancellation registration fire synchronously: it calls SBW_CancelTask
        // (recording a pending cancel in the producer registry BEFORE the Swift
        // Task is assigned) and TaskCompletionSource.TrySetCanceled. The launched
        // Swift Task is cancelled at birth, Task.sleep returns immediately, and the
        // Task.isCancelled guard suppresses the record. Two things must hold:
        //   (1) the awaited (non-generic) Task faults with OperationCanceledException
        //       — TrySetCanceled won the first-writer race against the success
        //       completion that still fires afterwards;
        //   (2) the shared sink stays empty — the void body never recorded.
        // This is the void analogue of the value-returning parent-async cancel
        // wiring, exercising SBW_CancelTask / _sbwAssignTask / the single-handle
        // free under cancellation that the success completion performs.
        using var sink = Functions.MakeDonationSink();
        using var donator = Functions.MakeStringDonator(sink);

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        System.OperationCanceledException? caught = null;
        try
        {
            await WithTimeout(donator.DonateAfterDelayAsync(cts.Token), DefaultAsyncTimeout);
        }
        catch (System.OperationCanceledException e)
        {
            caught = e;
        }

        AssertTrue(caught is not null,
            "DonateAfterDelayAsync(pre-canceled) — void cancel path must fault the Task with OperationCanceledException");
        AssertEqual(0, sink.Count,
            "DonateAfterDelayAsync(pre-canceled) — the isCancelled guard suppresses the record, so the sink stays empty");
    }
}
