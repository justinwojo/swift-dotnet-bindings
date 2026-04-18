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

    #endregion
}
