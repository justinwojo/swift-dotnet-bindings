// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Receiver-lifetime probe for the parent-only ASYNC concrete-specialization path.
///
/// <para>The generated extension hands the receiver's native storage to a <c>@_cdecl</c> wrapper
/// that copies <c>self_.pointee</c> into a local and then launches a Swift <c>Task</c> capturing
/// that local BY VALUE. Two separate questions follow, and this class answers both at runtime
/// rather than by reading the emitted wrapper. First: does the captured copy retain the receiver's
/// reference fields, so the awaited body reads live memory after the caller's own storage is gone?
/// If it does, the managed side needs its handle held only across the SYNCHRONOUS entry, and a
/// lease spanning the awaited task would pin storage nobody reads. Second: is that synchronous
/// window actually protected — can a consumer disposing the receiver from another thread free the
/// buffer out from under the wrapper's read?</para>
///
/// <para><see cref="AsyncRefBag{TItem}"/> is the shape fixture's reference-carrying sibling: same
/// constraints, same emission arm, but its stored <c>AsyncSelfRefPayload</c> feeds the shared
/// allocation counters, so the copy's lifetime is observable instead of merely argued.</para>
/// </summary>
public class PatParentAsyncSelfLifetimeTests : TestBase
{
    public PatParentAsyncSelfLifetimeTests(TestResults results) : base(results) { }

    /// <summary>
    /// The lifetime evidence. The receiver is disposed the instant the synchronous entry returns —
    /// which synchronously destroys its native buffer and releases the reference field it held —
    /// while the Swift task is still suspended. If the task's captured copy retains the payload the
    /// tracked object is still live at that point; if the capture were a borrow of the caller's
    /// storage, the count would already be zero and the awaited body would be reading freed memory.
    /// </summary>
    public async Task TestAsyncCsmTaskCopyRetainsTheDisposedReceiversReference()
    {
        LifetimeTracker.Reset();

        var bag = Functions.MakeAsyncRefBagMockStringItem(21);
        LifetimeTracker.AssertLiveCount(1, "the receiver's payload is live once its wrapper exists");
        AssertEqual(21, bag.PayloadTag(), "the receiver must read back the tag it was built with");

        var pending = bag.RespondAfterSuspensionAsync();
        bag.Dispose();

        LifetimeTracker.AssertLiveCount(1,
            "the task's by-value copy of the receiver must keep the payload alive after the caller's storage is destroyed");

        var response = await WithTimeout(pending, DefaultAsyncTimeout);
        AssertEqual("ok", response.S.ToString(), "the awaited body must still produce its conformer-substituted payload");

        LifetimeTracker.AssertLiveCount(0, "the completed task must release the copy it captured");

        TestLogger.Info("async CSM: the task's captured receiver copy retained the payload past the caller's disposal");
    }

    /// <summary>
    /// The race. A second thread disposes the receiver while this one issues the call, so the
    /// dispose lands in or around the synchronous entry — the window in which the wrapper reads
    /// <c>self_.pointee</c>. Two outcomes are legitimate: the entry got the handle first and the
    /// awaited body produces the right payload, or the dispose got there first and the call is
    /// refused before it starts. Freeing the buffer while the read is in flight is neither — it is
    /// a use-after-free, and what it produces is a corrupted payload or a crash, not an exception.
    /// </summary>
    public async Task TestAsyncCsmReceiverDisposedRacingTheSynchronousEntry()
    {
        const int Iterations = 200;
        int completed = 0;
        int refused = 0;

        for (int i = 0; i < Iterations; i++)
        {
            var bag = Functions.MakeAsyncRefBagMockStringItem(i);
            using var ready = new ManualResetEventSlim(false);

            var disposer = Task.Run(() =>
            {
                ready.Wait();
                bag.Dispose();
            });

            Task<StringResponse>? pending = null;
            try
            {
                ready.Set();
                pending = bag.RespondImmediatelyAsync();
            }
            catch (ObjectDisposedException)
            {
                refused++;
            }

            await disposer;

            if (pending != null)
            {
                var response = await WithTimeout(pending, DefaultAsyncTimeout);
                AssertEqual("ok", response.S.ToString(),
                    "a call that won the race against disposal must produce an intact payload");
                completed++;
            }
        }

        AssertEqual(Iterations, completed + refused,
            "every iteration must either complete with an intact payload or be refused outright");
        AssertTrue(completed > 0, "the race must let at least some calls through, or it proves nothing");

        TestLogger.Info($"async CSM entry race: {completed} completed, {refused} refused by disposal, 0 corrupted");
    }

    /// <summary>
    /// The same race widened to the suspension window, on the second conformer so the leased entry
    /// is covered on more than one specialization. Here the dispose lands squarely AFTER the entry
    /// returned, which is a consumer's ordinary right — the receiver is a value the call already
    /// copied — so every iteration must complete, and with the payload substituted for this
    /// conformer rather than the other one.
    /// </summary>
    public async Task TestAsyncCsmReceiverDisposedDuringSuspensionStillCompletes()
    {
        for (int i = 0; i < 8; i++)
        {
            var bag = Functions.MakeAsyncRefBagMockIntItem(i);
            var pending = bag.RespondAfterSuspensionAsync();

            var disposer = Task.Run(() => bag.Dispose());
            await disposer;

            var response = await WithTimeout(pending, DefaultAsyncTimeout);
            AssertEqual((nint)42, response.N,
                "the awaited body must produce its own conformer's payload after the receiver was disposed");
        }

        TestLogger.Info("async CSM: disposal during the suspension window left eight awaited bodies intact");
    }

    /// <summary>
    /// Churn with the receiver kept alive across the await, as the ordinary consumer writes it. The
    /// counters are what carry the assertion: an entry that over-held the receiver would strand a
    /// payload per iteration, which a single-iteration test cannot distinguish from a slow release.
    /// </summary>
    public async Task TestAsyncCsmRepeatedCallsLeaveNoStrandedReceiverCopies()
    {
        LifetimeTracker.Reset();

        for (int i = 0; i < 16; i++)
        {
            using var bag = Functions.MakeAsyncRefBagMockStringItem(i);
            var response = await WithTimeout(bag.RespondImmediatelyAsync(), DefaultAsyncTimeout);
            AssertEqual("ok", response.S.ToString(), "each awaited body must produce its payload");
            AssertEqual(i, bag.PayloadTag(), "the receiver must survive its own async call intact");
        }

        LifetimeTracker.AssertLiveCount(0, "sixteen async calls must leave no receiver copy stranded");

        TestLogger.Info("async CSM: sixteen call-and-await iterations released every captured receiver copy");
    }
}
