// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib.StreamTasks;

namespace RuntimeTestsApp.Async;

/// <summary>
/// A class nested in an extension of a caseless enum, whose stream members name their element
/// through a member-type chain (<c>Outer.Inner.Stream.Continuation</c>, <c>AsyncStream&lt;Outer.Inner.Event&gt;</c>).
/// The wrapper has to spell those chains exactly for the type to bind at all; these tests confirm the
/// bound members still reach the same Swift instance and stream.
/// </summary>
public class NestedTypeAsyncStreamTests : TestBase
{
    public NestedTypeAsyncStreamTests(TestResults results) : base(results) { }

    public async Task TestEventsYieldedThroughNestedContinuationReachTheConsumer()
    {
        using var watcher = new Watcher();
        AssertFalse(watcher.HasContinuation, "no continuation before the stream is opened");

        watcher.Start();
        AssertTrue(watcher.HasContinuation, "opening the stream stores its continuation");

        watcher.Emit(3);
        watcher.Emit(5);

        var deadline = DateTime.UtcNow + DefaultAsyncTimeout;
        while (watcher.ReceivedCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        AssertEqual(2, watcher.ReceivedCount, "both yielded events reach the Swift consumer");
    }

    public async Task TestStaticNestedStreamPropertyEnumeratesInOrder()
    {
        var values = new List<nint>();
        await WithTimeout(Task.Run(async () =>
        {
            await foreach (var value in Watcher.Ticks)
                values.Add(value);
        }), DefaultAsyncTimeout);

        AssertEqual(2, values.Count, "the stream finishes after its two values");
        AssertEqual((nint)1, values[0], "first value");
        AssertEqual((nint)2, values[1], "second value");
    }
}
