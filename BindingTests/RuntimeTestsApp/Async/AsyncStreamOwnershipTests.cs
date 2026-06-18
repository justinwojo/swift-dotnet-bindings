// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Ownership regression coverage for <c>SwiftAsyncStream&lt;T&gt;.OnElement</c>. The Swift wrapper
/// passes each element through <c>withUnsafePointer(to: element)</c> — a BORROWED pointer valid only
/// for the callback — while the Swift <c>for await</c> loop still owns its own <c>+1</c> on the element
/// until the iteration ends. The element escapes via the C# channel, so <c>OnElement</c> must copy out
/// an INDEPENDENT reference during the callback (via <c>SwiftMarshal.ExtractCopiedValue</c>), not alias
/// or bitwise-move the borrowed slot.
///
/// Each fixture drives one of the three ownership shapes the borrowed-slot escape used to break:
/// <list type="bullet">
///   <item><b>class element</b> (<c>TrackedRef</c>): the payload word IS the object pointer; the fix
///   dereferences and <c>Arc.Retain</c>s it. The pre-fix bare marshal stored the soon-dead slot address
///   as the handle, so reading <c>.Tag</c> returned garbage / faulted and the wrapper dangled.</item>
///   <item><b>non-frozen struct</b> (<c>TrackedRefStruct</c>, ADOPT/SafeHandle): the fix copies into an
///   independent buffer the wrapper adopts. The pre-fix marshal adopted the borrowed slot the Swift
///   closure frees on return → use-after-free / double-free.</item>
///   <item><b>large heap String</b> (move-on-construction): the fix takes a value-witness <c>+1</c>
///   before the bitwise move. The pre-fix marshal moved a borrowed <c>+0</c> as if it were a
///   transferred <c>+1</c> → double-release of the shared storage.</item>
/// </list>
///
/// For the tracked element types the probe is a hard count: after a full drain plus disposal of the
/// extracted wrappers, the tracked live-count must return to zero (every element allocated by the
/// stream released exactly once). For the String stream the round-tripped value plus survival of the
/// extract+dispose cycle is the signal (an over-release surfaces as a crash).
/// </summary>
public class AsyncStreamOwnershipTests : TestBase
{
    public AsyncStreamOwnershipTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>
    /// Class element: <c>OnElement</c> must dereference the borrowed slot to the object pointer and take
    /// an independent <c>Arc.Retain</c>. Reading <c>.Tag</c> off each drained wrapper asserts the handle
    /// is the real object (not the slot address); the post-drain live-count of zero asserts the retain is
    /// balanced by the wrapper's dispose with no leak and no over-release.
    /// </summary>
    public async Task TestAsyncStreamClassElement_DerefsAndRetains()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        using (var source = new TrackedRefStreamSource())
        {
            var tags = await WithTimeout(Task.Run(async () =>
            {
                var collected = new List<int>();
                await foreach (var item in source.TrackedRefs)
                {
                    collected.Add(item.Tag);
                    item.Dispose();
                }
                return collected;
            }), DefaultAsyncTimeout);

            AssertEqual(3, tags.Count, "AsyncStream<TrackedRef> must yield 3 class elements");
            AssertTrue(tags[0] == 1 && tags[1] == 2 && tags[2] == 3,
                $"AsyncStream<TrackedRef> tags must round-trip [1,2,3]; got [{string.Join(",", tags)}]");
        }

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "AsyncStream<TrackedRef> drain must deref+retain each class element and release it exactly once");
        TestLogger.Info("AsyncStream<class>: 3 elements deref+retained, tags correct, ARC balanced to 0");
    }

    /// <summary>
    /// Non-frozen struct element (ADOPT/SafeHandle): <c>OnElement</c> must copy into an independent
    /// buffer the wrapper adopts, never the borrowed slot the Swift closure frees on return. Reading
    /// <c>.Value</c> exercises the adopted buffer; the post-drain live-count of zero asserts the embedded
    /// <c>TrackedRef</c> is released exactly once (a borrowed-slot adopt would UAF or double-free).
    /// </summary>
    public async Task TestAsyncStreamNonFrozenStructElement_DoesNotAdoptBorrowedSlot()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        using (var source = new TrackedRefStreamSource())
        {
            var values = await WithTimeout(Task.Run(async () =>
            {
                var collected = new List<int>();
                await foreach (var item in source.TrackedStructs)
                {
                    collected.Add(item.Value);
                    item.Dispose();
                }
                return collected;
            }), DefaultAsyncTimeout);

            AssertEqual(3, values.Count, "AsyncStream<TrackedRefStruct> must yield 3 struct elements");
            AssertTrue(values[0] == 1 && values[1] == 2 && values[2] == 3,
                $"AsyncStream<TrackedRefStruct> values must round-trip [1,2,3]; got [{string.Join(",", values)}]");
        }

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0,
            "AsyncStream<TrackedRefStruct> drain must copy into an independent buffer; the embedded ref releases once");
        TestLogger.Info("AsyncStream<non-frozen struct>: 3 elements copied (not borrowed-adopt), ARC balanced to 0");
    }

    /// <summary>
    /// Large heap-backed String element (move-on-construction): <c>OnElement</c> must take a
    /// value-witness <c>+1</c> before the bitwise move so the borrowed storage is not double-released.
    /// The strings exceed the 15-byte small-string inline limit, forcing heap storage with real ARC —
    /// small inline strings have no storage to over-release and would hide the bug. Correct round-trip
    /// values plus survival of the extract+dispose cycle is the signal.
    /// </summary>
    public async Task TestAsyncStreamLargeStringElement_DoesNotOverReleaseBorrow()
    {
        using var source = new TrackedRefStreamSource();

        var strings = await WithTimeout(Task.Run(async () =>
        {
            var collected = new List<string>();
            await foreach (var s in source.LongMessages)
            {
                collected.Add(s.ToString());
                s.Dispose();
            }
            return collected;
        }), DefaultAsyncTimeout);

        var expected = new[]
        {
            string.Concat(System.Linq.Enumerable.Repeat("alpha-", 8)) + "tail0",
            string.Concat(System.Linq.Enumerable.Repeat("bravo-", 8)) + "tail1",
            string.Concat(System.Linq.Enumerable.Repeat("charlie-", 8)) + "tail2",
        };

        AssertEqual(3, strings.Count, "AsyncStream<String> must yield 3 large-string elements");
        for (int i = 0; i < expected.Length; i++)
        {
            AssertEqual(expected[i], strings[i],
                $"AsyncStream<String> large element {i} must round-trip without over-releasing borrowed storage");
        }

        DrainFinalizers();
        TestLogger.Info("AsyncStream<large String>: 3 heap-backed elements round-tripped without over-release");
    }

    /// <summary>
    /// Context-handle lifetime regression (Defect I). <c>GetContext</c> pins the
    /// <c>SwiftAsyncStream&lt;T&gt;</c> with a STRONG <see cref="System.Runtime.InteropServices.GCHandle"/>
    /// so Swift can resolve it across callbacks — that handle is the instance's only managed root while
    /// the producer runs. The completion callback (the last Swift→C# callback) must free it; otherwise the
    /// handle roots the instance forever and a consumer that drains via <c>await foreach</c> (which disposes
    /// only the generated enumerator, never <c>SwiftAsyncStream.Dispose</c>) leaks one stream per property
    /// read. Pre-fix the handle was freed only in <c>Dispose</c>, so a fully-drained never-disposed stream
    /// kept its handle allocated forever; post-fix <c>Complete</c> frees it.
    ///
    /// The assertion reads the handle's allocation state directly (<c>IsContextHandleAllocated</c>) rather
    /// than GC collectability of a WeakReference: Mono's conservative stack scan on the simulator can pin a
    /// fully-drained instance via a stale register/stack reference, so collectability is an unreliable proxy
    /// for handle-freedom even when the runtime has correctly freed the handle. The deterministic probe
    /// observes the exact invariant — handle freed at completion — with no GC dependency.
    /// </summary>
    public async Task TestAsyncStreamContextHandleFreedAfterDrain_NoLeak()
    {
        using var source = new AsyncValueSource();

        // Read the property once and hold the concrete instance for the whole test so the assertion can
        // observe its handle state. await foreach disposes only the generated enumerator (running
        // SignalProducerStop, which does NOT free the context handle), never the stream itself — so the
        // ONLY path that can free the handle here is the completion callback.
        var stream = (SwiftAsyncStream<int>)source.Counts;

        long sum = await WithTimeout(DrainCounts(stream), DefaultAsyncTimeout);
        AssertEqual(60L, sum, "AsyncStream<Int32> Counts must drain 10+20+30 = 60");

        // Complete() completes the channel (unblocking our await-foreach) and THEN frees the handle, both
        // on the Swift executor thread — so the free is concurrent with the consumer's resumption. Poll
        // the handle's allocation state on a bounded budget: the expected case settles within an iteration
        // or two, while a genuine leak (the pre-fix shape — freed only in Dispose, never called by
        // await-foreach) exhausts the budget with the handle still allocated.
        for (int attempt = 0; attempt < 200 && stream.IsContextHandleAllocated; attempt++)
        {
            await Task.Delay(10);
        }

        AssertFalse(stream.IsContextHandleAllocated,
            "completion must free the rooting GCHandle so a fully-drained, never-disposed SwiftAsyncStream " +
            "is not leaked (await-foreach disposes only the enumerator, never SwiftAsyncStream.Dispose — " +
            "pre-fix the handle was freed only in Dispose, rooting one stream per property read)");
        TestLogger.Info("AsyncStream context handle freed at completion on a fully-drained, never-disposed stream (no await-foreach leak)");
    }

    /// <summary>
    /// Context-handle lifetime on the PRODUCER-THREW fault path (Defect I redesign). A throwing stream's
    /// <c>finish(throwing:)</c> routes through the producer-error callback → <c>FaultChannel</c>, which
    /// deliberately does NOT free the rooting handle (it is reachable mid-stream). The Swift wrapper
    /// always invokes <c>completionCallback</c> after its <c>do/catch</c> even on a fault, so the trailing
    /// completion is what frees the handle. This extends the deterministic
    /// <see cref="SwiftAsyncStream{T}.IsContextHandleAllocated"/> probe to the faulted path: a stream that
    /// rethrows at the boundary must still free its context handle, not leak it.
    /// </summary>
    public async Task TestThrowingStreamContextHandleFreedAfterFault_NoLeak()
    {
        using var source = new ThrowingStreamSource();

        // Hold the concrete instance so the assertion can observe its handle. await foreach disposes only
        // the generated enumerator (SignalProducerStop — no free) and FaultChannel does not free either;
        // the ONLY free here is the trailing completion callback.
        var stream = (SwiftAsyncStream<int>)source.ThrowingEvents;

        var collected = new List<int>();
        await WithTimeout(Task.Run(async () =>
        {
            try
            {
                await foreach (var v in stream)
                {
                    collected.Add(v);
                }
            }
            catch (global::Swift.Runtime.SwiftRuntimeException)
            {
                // Expected — the Swift producer threw; the fault rethrows at the boundary.
            }
        }), DefaultAsyncTimeout);

        AssertEqual(3, collected.Count, "throwing stream must yield its 3 pre-fault elements");

        for (int attempt = 0; attempt < 200 && stream.IsContextHandleAllocated; attempt++)
        {
            await Task.Delay(10);
        }

        AssertFalse(stream.IsContextHandleAllocated,
            "the trailing completion callback must free the rooting GCHandle even on a producer-threw " +
            "fault (FaultChannel does NOT free; Complete does) — a faulted stream must not leak its handle");
        TestLogger.Info("Throwing stream context handle freed at completion after a producer-threw fault (no leak)");
    }

    /// <summary>
    /// Producer cancel (Defect I redesign) — element-count guard. A continuously-climbing Swift producer
    /// must STOP producing when the consumer stops iterating (breaking the <c>await foreach</c> disposes
    /// the enumerator → <c>SignalProducerStop</c> → registry task-cancel of the wrapper Task → the
    /// fixture's <c>onTermination</c> cancels the producer). The proof is behavioural: <c>producedCount</c>
    /// freezes after the stop, rather than climbing forever (channel-closed-but-producer-still-running).
    /// </summary>
    public async Task TestCancellableStreamCancelStopsClimbingProducer()
    {
        using var source = new CancellableStreamSource();
        var stream = (SwiftAsyncStream<int>)source.ClimbingCounts;

        int consumed = 0;
        await WithTimeout(Task.Run(async () =>
        {
            await foreach (var v in stream)
            {
                consumed++;
                if (consumed >= 3)
                {
                    break; // disposing the enumerator signals the producer to stop
                }
            }
        }), DefaultAsyncTimeout);

        AssertEqual(3, consumed, "must consume 3 climbing elements before stopping the producer");

        // Let the stop propagate (registry cancel → onTermination → producer Task cancel), then snapshot
        // twice. A producer that ignored the stop would keep climbing across the two reads.
        await Task.Delay(200);
        int first = source.ProducedCount;
        await Task.Delay(250);
        int second = source.ProducedCount;

        AssertEqual(first, second,
            $"stopping the consumer must STOP the Swift producer (producedCount frozen), not leave it " +
            $"producing forever; was {first}, then {second} after a further 250ms");
        TestLogger.Info($"CancellableStream: producer halted at {first} after consumer stop (count frozen, not climbing)");
    }

    /// <summary>
    /// Producer cancel (Defect I redesign) — the registry-isolating leak guard. A producer that yields a
    /// few elements then SUSPENDS INDEFINITELY parks the wrapper's <c>for await</c> with no next element
    /// boundary; completing the channel cannot wake it. ONLY the cancellation registry (C# <c>Cancel()</c>
    /// → <c>SBW_CancelTask</c> → wrapper Task cancel → the fixture's <c>onTermination</c>) drives the
    /// producer to <c>finish()</c> and the wrapper to its completion callback, which frees the rooting
    /// GCHandle. Pre-redesign this exact shape leaked one context handle (and instance) forever — so a
    /// broken producer-cancel leaves the handle allocated and this goes red.
    /// </summary>
    public async Task TestSuspendedStreamContextHandleFreedAfterCancel_NoLeak()
    {
        using var source = new CancellableStreamSource();
        var stream = (SwiftAsyncStream<int>)source.SuspendingCounts;

        // The wrapper produces into the unbounded channel without a consumer: it yields 3 then parks. No
        // await foreach is needed — the rooting handle stays allocated while the producer is suspended.
        for (int attempt = 0; attempt < 200 && source.ProducedCount < 3; attempt++)
        {
            await Task.Delay(10);
        }
        AssertEqual(3, source.ProducedCount, "suspending producer must yield 3 elements then park indefinitely");
        AssertTrue(stream.IsContextHandleAllocated, "the rooting GCHandle is allocated while the producer is parked");

        // Cancel must task-cancel the SUSPENDED producer via the registry so it reaches completion;
        // channel-complete alone cannot wake a producer parked with no next element boundary.
        stream.Cancel();

        for (int attempt = 0; attempt < 200 && stream.IsContextHandleAllocated; attempt++)
        {
            await Task.Delay(10);
        }

        AssertFalse(stream.IsContextHandleAllocated,
            "producer cancel must drive the suspended producer to its completion callback, freeing the " +
            "rooting GCHandle; without it a parked producer never completes and the stream leaks one " +
            "handle per read");
        TestLogger.Info("Suspended stream: Cancel() woke the parked producer to completion; context handle freed (no leak)");
    }

    // Drains the concrete stream via await foreach — which disposes only the generated enumerator, never
    // SwiftAsyncStream.Dispose — and returns the summed elements. Split out only so WithTimeout can wrap
    // the drain Task; the test method keeps its own reference to the stream across the post-drain poll.
    private static async Task<long> DrainCounts(SwiftAsyncStream<int> stream)
    {
        long sum = 0;
        await foreach (var v in stream)
        {
            sum += v;
        }
        return sum;
    }
}
