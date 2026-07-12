// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for Swift AsyncSequence → .NET IAsyncEnumerable&lt;T&gt; projection.
/// Without this projection, <c>await foreach (var x in seq)</c> fails to
/// compile for every Swift type that conforms to AsyncSequence (StoreKit
/// Transactions, MusicKit MusicSubscription.Updates, async event streams).
/// The generator must emit a <c>GetAsyncEnumerator</c> adapter
/// that bridges the Swift iterator's <c>NextAsync(ct) -&gt; Task&lt;T?&gt;</c>
/// shape to <c>IAsyncEnumerator&lt;T&gt;</c>.
/// </summary>
public class AsyncSequenceTests : TestBase
{
    public AsyncSequenceTests(TestResults results) : base(results) { }

    /// <summary>
    /// Diagnostic smoke test (runs first alphabetically): exercises the
    /// raw Swift surface — <c>MakeAsyncIterator()</c> + a single
    /// <c>NextAsync()</c> — without going through the IAsyncEnumerable
    /// state machine. If this hangs, the bug is in the wrapper /
    /// async-method emit; if only the <c>await foreach</c> tests hang,
    /// the bug is in the iterator-method bridge.
    /// </summary>
    public async Task Test01_CounterSequenceManualSingleNext()
    {
        var seq = new CounterSequence(3);
        var iter = seq.MakeAsyncIterator();
        var first = await iter.NextAsync(default);
        AssertEqual(1, first, "first NextAsync");
        TestLogger.Info($"Manual single NextAsync -> {first}");
    }

    /// <summary>
    /// Diagnostic smoke test: drains the iterator manually with three
    /// successive <c>NextAsync()</c> calls. Verifies the mutating-async
    /// path holds across calls (the AsyncIteratorProtocol regression
    /// that bumpAsync covers, but on the actual iterator type).
    /// </summary>
    public async Task Test02_CounterSequenceManualDrain()
    {
        var seq = new CounterSequence(3);
        var iter = seq.MakeAsyncIterator();
        var a = await iter.NextAsync(default);
        var b = await iter.NextAsync(default);
        var c = await iter.NextAsync(default);
        AssertEqual(1, a, "1st");
        AssertEqual(2, b, "2nd");
        AssertEqual(3, c, "3rd");
        TestLogger.Info($"Manual drain NextAsync -> [{a}, {b}, {c}]");
    }

    /// <summary>
    /// Diagnostic: <c>await foreach</c> via the cast-to-IAsyncEnumerable
    /// path (no <c>WithCancellation</c>). Isolates whether the hang is
    /// in the bridge itself or in the WithCancellation adapter chain.
    /// </summary>
    public async Task Test03_CounterSequenceBareAwaitForeach()
    {
        var seq = new CounterSequence(3);
        var collected = new List<int>();
        await foreach (var item in seq)
        {
            collected.Add(item);
            TestLogger.Info($"  bare-await-foreach yielded {item}");
            if (collected.Count >= 5) break;  // safety
        }
        AssertEqual(3, collected.Count, "bare await foreach count");
    }

    /// <summary>
    /// Diagnostic: drive <c>IAsyncEnumerator&lt;T&gt;</c> manually via
    /// <c>MoveNextAsync</c>. If this hangs at the first MoveNextAsync,
    /// the bridge implementation is the culprit (not the
    /// <c>await foreach</c> macro).
    /// </summary>
    public async Task Test04_CounterSequenceManualMoveNext()
    {
        var seq = new CounterSequence(3);
        IAsyncEnumerable<int> ien = seq;
        await using var en = ien.GetAsyncEnumerator(default);
        var ok1 = await en.MoveNextAsync();
        AssertTrue(ok1, "first MoveNextAsync");
        AssertEqual(1, en.Current, "first Current");
        TestLogger.Info($"  Test04 first MoveNextAsync ok, Current={en.Current}");
    }

    /// <summary>
    /// Diagnostic: drain past the limit. The 4th call must return null
    /// (Optional&lt;Int32&gt;.none from Swift). If it returns 0 or the
    /// previous value, the Optional&lt;Int32&gt; marshalling is broken
    /// for the async-callback path.
    /// </summary>
    /// <summary>
    /// Diagnostic: async free function that returns <c>nil</c>. If C# sees
    /// <c>0</c> instead of <c>null</c>, the bug is in the async-callback
    /// Optional&lt;Int32&gt; marshal path (NOT the iterator bridge).
    /// </summary>
    public async Task Test06_AsyncTopLevelReturnNoneInt()
    {
        var x = await SwiftBindingsTestLib.Functions.GetSbwAsyncReturnNoneIntAsync();
        TestLogger.Info($"  sbwAsyncReturnNoneInt -> {(x.HasValue ? x.Value.ToString() : "null")}");
        AssertTrue(!x.HasValue, $"Async None must surface as null, was {(x.HasValue ? x.Value.ToString() : "null")}");
    }

    /// <summary>
    /// Diagnostic: async free function that returns <c>Some(7)</c>. Pairs
    /// with <c>Test06</c> to confirm the Some-side still round-trips.
    /// </summary>
    public async Task Test07_AsyncTopLevelReturnSomeSeven()
    {
        var x = await SwiftBindingsTestLib.Functions.GetSbwAsyncReturnSomeSevenAsync();
        TestLogger.Info($"  sbwAsyncReturnSomeSeven -> {(x.HasValue ? x.Value.ToString() : "null")}");
        AssertEqual(7, x, "Some(7) must round-trip");
    }

    public async Task Test05_CounterSequenceDrainPastLimit()
    {
        var seq = new CounterSequence(2);
        var iter = seq.MakeAsyncIterator();
        var a = await iter.NextAsync(default);
        var b = await iter.NextAsync(default);
        var c = await iter.NextAsync(default); // expect null
        TestLogger.Info($"  drain-past-limit: a={a?.ToString() ?? "null"}, b={b?.ToString() ?? "null"}, c={c?.ToString() ?? "null"}");
        AssertEqual(1, a, "1st");
        AssertEqual(2, b, "2nd");
        AssertTrue(c == null, $"3rd must be null but was {c?.ToString() ?? "null"}");
    }

    /// <summary>
    /// Canonical <c>await foreach</c> consumption against a Swift
    /// AsyncSequence. The generator emits an IAsyncEnumerable&lt;Int32&gt;
    /// adoption + GetAsyncEnumerator adapter; without it this test would
    /// fail to compile (CS8412).
    /// </summary>
    public async Task TestCounterSequenceAwaitForEach()
    {
        var seq = new CounterSequence(5);

        var collected = new List<int>();
        await foreach (var item in seq.WithCancellation(default))
        {
            collected.Add(item);
        }

        AssertEqual(5, collected.Count, "CounterSequence must yield exactly upTo elements");
        AssertEqual(1, collected[0], "first element");
        AssertEqual(5, collected[4], "last element");
        TestLogger.Info($"CounterSequence(5) await foreach -> [{string.Join(", ", collected)}]");
    }

    /// <summary>
    /// AsyncSequence Element projection for a non-frozen struct. Mirrors
    /// the StoreKit <c>Transaction.Transactions</c> →
    /// <c>VerificationResult&lt;Transaction&gt;</c> and the MusicKit
    /// <c>MusicSubscription.Updates</c> → <c>MusicSubscription</c> shape:
    /// the Element surfaces in C# as a SafeHandle-backed reference type.
    /// </summary>
    public async Task TestScoreUpdateSequenceAwaitForEach()
    {
        var seq = new ScoreUpdateSequence(3);

        var rounds = new List<int>();
        var points = new List<int>();
        await foreach (var update in seq)
        {
            rounds.Add(update.Round);
            points.Add(update.Points);
        }

        AssertEqual(3, rounds.Count, "ScoreUpdateSequence yields one element per round");
        AssertEqual(1, rounds[0], "first round");
        AssertEqual(10, points[0], "first round points");
        AssertEqual(3, rounds[2], "third round");
        AssertEqual(30, points[2], "third round points");
        TestLogger.Info($"ScoreUpdateSequence(3) await foreach -> rounds=[{string.Join(", ", rounds)}], points=[{string.Join(", ", points)}]");
    }

    /// <summary>
    /// Verifies that the generated GetAsyncEnumerator returns an
    /// <see cref="IAsyncEnumerator{T}"/>, satisfying the canonical
    /// <c>System.Collections.Generic.IAsyncEnumerable&lt;T&gt;</c> contract
    /// rather than a Swift-only iterator type. Without IAsyncEnumerable
    /// adoption the cast below would fail at compile time, defeating the
    /// purpose of the bridge.
    /// </summary>
    public async Task TestCounterSequenceImplementsIAsyncEnumerable()
    {
        IAsyncEnumerable<int> seq = new CounterSequence(3);

        var sum = 0;
        await using var enumerator = seq.GetAsyncEnumerator(default);
        while (await enumerator.MoveNextAsync())
        {
            sum += enumerator.Current;
        }

        AssertEqual(6, sum, "1+2+3 from CounterSequence(3)");
        TestLogger.Info($"CounterSequence(3) IAsyncEnumerable sum = {sum}");
    }

    /// <summary>
    /// AsyncSequence Element projection for a Swift stdlib type that goes
    /// through TypeProjectionFactory: the iterator's <c>NextAsync</c>
    /// returns <c>Task&lt;string?&gt;</c> (StringProjection.PublicType), so
    /// the IAsyncEnumerable&lt;T&gt; bridge MUST also surface
    /// <c>IAsyncEnumerable&lt;string&gt;</c> — otherwise the
    /// <c>yield return</c> of the projected value into a raw-typed
    /// <c>IAsyncEnumerable&lt;Swift.SwiftString&gt;</c> fails CS0029 at
    /// compile time. Pins the projection-aware element-type translation
    /// in <c>AsyncSequenceHandler.TranslateElementTypeToCSharp</c>.
    /// </summary>
    public async Task TestLabelSequence_ElementProjectsToString()
    {
        // Compile-time assertion: the generated IAsyncEnumerable adoption
        // MUST be IAsyncEnumerable<string>, not IAsyncEnumerable<SwiftString>.
        // If the element-type translation regresses, this assignment fails
        // with CS0029 and the test never reaches runtime.
        IAsyncEnumerable<string> seq = new LabelSequence(3);

        var collected = new List<string>();
        await foreach (var label in seq)
        {
            collected.Add(label);
        }

        AssertEqual(3, collected.Count, "LabelSequence yields one label per index");
        AssertEqual("label-1", collected[0], "first label");
        AssertEqual("label-2", collected[1], "second label");
        AssertEqual("label-3", collected[2], "third label");
        TestLogger.Info($"LabelSequence(3) -> [{string.Join(", ", collected)}]");
    }

    /// <summary>
    /// Cooperative cancellation: the generator threads the
    /// CancellationToken into NextAsync(ct), so a token that's already
    /// cancelled (or trips before iteration completes) must terminate the
    /// loop deterministically rather than hang.
    /// </summary>
    public async Task TestCounterSequenceCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var seq = new CounterSequence(100);

        var collected = new List<int>();
        try
        {
            await foreach (var item in seq.WithCancellation(cts.Token))
            {
                collected.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected — the Swift bridge surfaces cancellation as
            // OperationCanceledException, and the test passes either way:
            // the contract is "do not iterate to completion".
        }

        AssertTrue(collected.Count < 100, "Cancelled iteration must short-circuit before upTo=100");
        TestLogger.Info($"CounterSequence(100) cancelled after {collected.Count} elements");
    }

    /// <summary>
    /// A Swift <c>next() async throws</c> that throws mid-stream must surface as a
    /// .NET exception at the <c>await foreach</c> boundary. The bridge only
    /// <c>await</c>s <c>NextAsync(ct)</c>, so a faulted Task propagates through the
    /// generated async iterator state machine and rethrows. The pre-throw elements
    /// must still be yielded in order before the exception. Mirrors StoreKit
    /// <c>Transaction.Transactions</c> whose verification can fail mid-stream.
    /// </summary>
    public async Task TestThrowingSequenceRethrowsAsDotNetException()
    {
        var seq = new ThrowingCounterSequence(3); // yields 1, 2, then throws on the 3rd next()

        var collected = new List<int>();
        Exception? caught = null;
        try
        {
            await foreach (var item in seq)
            {
                collected.Add(item);
                if (collected.Count >= 10) break; // safety: fixture must throw before this
            }
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        AssertTrue(caught != null,
            "A Swift throw inside next() must surface as a .NET exception under await foreach");
        AssertEqual(2, collected.Count,
            "The two pre-throw elements must be yielded in order before the exception");
        AssertEqual(1, collected[0], "first pre-throw element");
        AssertEqual(2, collected[1], "second pre-throw element");
        TestLogger.Info($"ThrowingCounterSequence rethrew {caught?.GetType().Name} after {collected.Count} elements");
    }

    /// <summary>
    /// Mid-stream cancellation must stop the sequence AND leak nothing. The Element
    /// is a <c>TrackedRef</c> (a class counted by <see cref="LifetimeTracker"/>): after
    /// cancelling partway through and disposing each drained element, the bridge's
    /// <c>finally</c> disposes the Swift iterator and every produced element must
    /// ARC-release exactly once, so the tracked live-count returns to zero. A leak
    /// (an undisposed element, or the iterator retaining produced elements past
    /// cancel) leaves the count above zero and this goes red. Pairs the
    /// cancellation-stops assertion with the ownership assertion the plain
    /// <c>CounterSequence</c> cancel test cannot make.
    /// </summary>
    public async Task TestTrackedRefSequenceMidStreamCancelNoLeak()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        using var cts = new CancellationTokenSource();
        var collected = new List<int>();

        await WithTimeout(Task.Run(async () =>
        {
            var seq = new TrackedRefSequence(100);
            try
            {
                await foreach (var item in seq.WithCancellation(cts.Token))
                {
                    collected.Add(item.Tag);
                    item.Dispose();
                    if (collected.Count >= 2)
                    {
                        cts.Cancel(); // cancel mid-stream — the next NextAsync(ct) must stop iteration
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected — mid-stream cancel surfaces as OperationCanceledException.
            }
        }), DefaultAsyncTimeout);

        AssertTrue(collected.Count < 100, "mid-stream cancel must short-circuit before upTo=100");
        AssertTrue(collected.Count >= 2, "must drain the elements produced before the cancel");
        AssertTrue(collected[0] == 1 && collected[1] == 2,
            $"TrackedRef tags must round-trip [1,2]; got [{string.Join(",", collected)}]");

        // AssertNoLeaks drains finalizers and, if anything still looks live, evicts stale
        // conservative-stack roots before re-checking — so a genuine leak still fails at
        // exact-zero while a Mono phantom straggler from the create-and-abandon churn doesn't
        // flake this red. Stronger than a bare AssertLiveCount snapshot for this shape.
        LifetimeTracker.AssertNoLeaks(
            "mid-stream cancel must dispose the Swift iterator (bridge finally) and leak no TrackedRef element");
        TestLogger.Info($"TrackedRefSequence mid-stream cancel after {collected.Count} elements, ARC balanced to 0");
    }

    /// <summary>
    /// An AsyncSequence reached INDIRECTLY through a computed property must still
    /// carry the <c>IAsyncEnumerable&lt;T&gt;</c> surface so <c>await foreach</c> works
    /// on the member. This is the real StoreKit/MusicKit shape
    /// (<c>Transaction.updates</c>, <c>Storefront.updates</c>) — consumers never
    /// construct the sequence directly.
    /// </summary>
    public async Task TestAsyncSequenceExposedAsProperty()
    {
        var provider = new AsyncSequenceProvider(4);

        var collected = new List<int>();
        await foreach (var item in provider.Counters)
        {
            collected.Add(item);
        }

        AssertEqual(4, collected.Count, "property-exposed AsyncSequence must yield seed elements");
        AssertEqual(1, collected[0], "first element");
        AssertEqual(4, collected[3], "last element");
        TestLogger.Info($"AsyncSequenceProvider.Counters await foreach -> [{string.Join(", ", collected)}]");
    }

    /// <summary>
    /// An AsyncSequence returned from a method must also carry the
    /// <c>IAsyncEnumerable&lt;T&gt;</c> surface so <c>await foreach</c> works on the
    /// return value. Complements the property path.
    /// </summary>
    public async Task TestAsyncSequenceReturnedFromMethod()
    {
        var provider = new AsyncSequenceProvider(0);

        var collected = new List<int>();
        await foreach (var item in provider.MakeCounters(3))
        {
            collected.Add(item);
        }

        AssertEqual(3, collected.Count, "method-returned AsyncSequence must yield upTo elements");
        AssertEqual(1, collected[0], "first element");
        AssertEqual(3, collected[2], "last element");
        TestLogger.Info($"AsyncSequenceProvider.MakeCounters(3) await foreach -> [{string.Join(", ", collected)}]");
    }

    /// <summary>Forces finalization so <see cref="LifetimeTracker"/> observes released refs.</summary>
    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }
}
