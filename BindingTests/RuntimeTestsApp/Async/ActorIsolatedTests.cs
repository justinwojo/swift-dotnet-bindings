// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Swift.Runtime;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for actor-isolated instance methods. Custom <c>actor</c> types and per-member
/// <c>@GlobalActor</c> isolation require hopping to the actor's executor — the generator
/// routes these through the async <c>@_cdecl</c> wrapper pipeline, exposing them as
/// <see cref="Task{T}"/>-returning C# methods.
/// </summary>
public class ActorIsolatedTests : TestBase
{
    public ActorIsolatedTests(TestResults results) : base(results) { }

    #region Counter (sync-on-actor → async in C#)

    public async Task TestCounter_IncrementRoundTrips()
    {
        var counter = Functions.CreateCounter();
        var first = await WithTimeout(counter.IncrementAsync(), DefaultAsyncTimeout);
        AssertEqual(1, first, "First increment should return 1");

        var second = await WithTimeout(counter.IncrementAsync(), DefaultAsyncTimeout);
        AssertEqual(2, second, "Second increment must observe prior mutation");

        counter.Dispose();
    }

    public async Task TestCounter_InitialCountHonored()
    {
        var counter = Functions.CreateCounterWithInitial(100);
        var count = await WithTimeout(counter.GetCountAsync(), DefaultAsyncTimeout);
        AssertEqual(100, count, "Actor init(initialCount:) should seed state");
        counter.Dispose();
    }

    public async Task TestCounter_DecrementAcrossAwait()
    {
        var counter = Functions.CreateCounterWithInitial(3);
        var a = await WithTimeout(counter.DecrementAsync(), DefaultAsyncTimeout);
        var b = await WithTimeout(counter.DecrementAsync(), DefaultAsyncTimeout);
        var c = await WithTimeout(counter.DecrementAsync(), DefaultAsyncTimeout);
        AssertEqual(2, a, "First decrement");
        AssertEqual(1, b, "Second decrement");
        AssertEqual(0, c, "Third decrement");
        counter.Dispose();
    }

    public async Task TestCounter_AddWithParameter()
    {
        var counter = Functions.CreateCounter();
        var result = await WithTimeout(counter.AddAsync(7), DefaultAsyncTimeout);
        AssertEqual(7, result, "Add with parameter should update actor state");

        var next = await WithTimeout(counter.AddAsync(3), DefaultAsyncTimeout);
        AssertEqual(10, next, "Add should accumulate across awaits");
        counter.Dispose();
    }

    public async Task TestCounter_MixedIsolatedAndNonisolated()
    {
        var counter = Functions.CreateCounter();
        // nonisolated sync APIs still work alongside async isolated ones
        AssertEqual("Counter", counter.TypeName, "nonisolated property still sync");
        var n = await WithTimeout(counter.IncrementAsync(), DefaultAsyncTimeout);
        AssertEqual(1, n, "isolated increment after nonisolated access");
        AssertEqual("Counter actor", counter.GetDescription(), "nonisolated method still sync");
        counter.Dispose();
    }

    #endregion

    #region AsyncProcessor (actor with mix of sync-isolated and async-isolated)

    public async Task TestAsyncProcessor_SyncIsolatedResultCount()
    {
        var processor = Functions.CreateAsyncProcessor();
        var initial = await WithTimeout(processor.ResultCountAsync(), DefaultAsyncTimeout);
        AssertEqual(0, initial, "New AsyncProcessor should have zero results");
        processor.Dispose();
    }

    public async Task TestAsyncProcessor_ProcessUpdatesResultCount()
    {
        var processor = Functions.CreateAsyncProcessor();
        var result = await WithTimeout(processor.ProcessAsync("input-1"), DefaultAsyncTimeout);
        AssertEqual("Processed: input-1", result, "process(input:) should return wrapped input");

        var count = await WithTimeout(processor.ResultCountAsync(), DefaultAsyncTimeout);
        AssertEqual(1, count, "resultCount() must observe the process() mutation");
        processor.Dispose();
    }

    #endregion

    #region ActorVault (throwing isolated method)

    public async Task TestActorVault_StoreAndReveal()
    {
        var vault = Functions.CreateActorVault();
        await WithTimeout(vault.StoreAsync("token", "42"), DefaultAsyncTimeout);
        var value = await WithTimeout(vault.RevealAsync("token"), DefaultAsyncTimeout);
        AssertEqual("42", value, "Stored value should round-trip through actor");
        vault.Dispose();
    }

    public async Task TestActorVault_RevealMissingThrows()
    {
        var vault = Functions.CreateActorVault();
        try
        {
            await WithTimeout(vault.RevealAsync("absent"), DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException for missing key");
        }
        catch (SwiftException ex)
        {
            TestLogger.Info($"ActorVault.RevealAsync(absent) threw SwiftException: {ex.Message}");
        }
        vault.Dispose();
    }

    #endregion

    #region ActorEventStream (actor-isolated AsyncStream property)

    public async Task TestActorEventStream_IsolatedAsyncStreamPropertyAccessible()
    {
        // Primary goal: the actor-isolated non-async AsyncStream property is
        // emitted (previously skipped by HasClosureUnsafeTupleElements /
        // actor-parent gates). Accessing the getter must return a live
        // IAsyncEnumerable without faulting. Full round-trip iteration of an
        // actor-isolated AsyncStream crossing the .NET/Swift boundary is a
        // separate marshalling concern and is not in scope here.
        var source = Functions.CreateActorEventStream();

        var stream = source.Events;
        AssertTrue(stream != null, "Events getter must return non-null IAsyncEnumerable");

        AssertEqual("ActorEventStream", source.PassthroughLabel.ToString(),
            "nonisolated label should still be synchronous");

        await WithTimeout(source.EmitAsync(11), DefaultAsyncTimeout);
        await WithTimeout(source.EndAsync(), DefaultAsyncTimeout);

        source.Dispose();
    }

    public async Task TestActorEventStream_CompletionClosesIterator()
    {
        // Regression for the AsyncStream completion-callback plumbing. Before the fix
        // the emitted `_OnComplete` was a no-op, so `channel.Writer.TryComplete()` was
        // never called and `await foreach` hung forever after Swift's continuation
        // finished. The 5s timeout here is the contract: an iterator that has seen all
        // emitted values AND the finish() must exit promptly.
        var source = Functions.CreateActorEventStream();
        try
        {
            var produce = Task.Run(async () =>
            {
                await source.EmitAsync(7);
                await source.EmitAsync(8);
                await source.EndAsync();
            });

            // Build the list inside the consumer task and return it — avoids sharing
            // a mutable List<int> across threads. Safe today under Task.WhenAll, but
            // collect-after-join removes the cross-thread write/read entirely so a
            // future refactor can't regress into a Heisenbug.
            var consume = Task.Run(async () =>
            {
                var local = new List<int>();
                await foreach (var value in source.Events)
                {
                    local.Add(value);
                }
                return local;
            });

            await WithTimeout(Task.WhenAll(produce, consume), DefaultAsyncTimeout);
            var received = await consume;

            AssertEqual(2, received.Count, "Iterator should have received both emitted values");
            AssertEqual(7, received[0], "First emitted value");
            AssertEqual(8, received[1], "Second emitted value");
        }
        finally
        {
            source.Dispose();
        }
    }

    #endregion

    #region WorkItem (async-throws at Swift source level — shell-stub)
    // BlinkIDUX.CaptureService shape. See Actors.swift scope note: executor isolation
    // is deferred; the wrapper dispatches through `Task { await self.method() }`.

    public async Task TestWorkItem_RunIncrementsAcrossAwaits()
    {
        var item = Functions.CreateWorkItem();
        var first = await WithTimeout(item.RunAsync(), DefaultAsyncTimeout);
        AssertEqual(1, first, "First run should observe the 0→1 increment");

        var second = await WithTimeout(item.RunAsync(), DefaultAsyncTimeout);
        AssertEqual(2, second, "Second run should observe the 1→2 increment");

        var observed = await WithTimeout(item.RunCountAsync(), DefaultAsyncTimeout);
        AssertEqual(2, observed, "runCount() should see both mutations");
        item.Dispose();
    }

    public async Task TestWorkItem_StopCausesSubsequentRunToThrow()
    {
        var item = Functions.CreateWorkItem();
        await WithTimeout(item.StopAsync(), DefaultAsyncTimeout);
        try
        {
            await WithTimeout(item.RunAsync(), DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException after stop()");
        }
        catch (SwiftException ex)
        {
            TestLogger.Info($"WorkItem.RunAsync after StopAsync threw SwiftException: {ex.Message}");
        }
        item.Dispose();
    }

    #endregion
}
