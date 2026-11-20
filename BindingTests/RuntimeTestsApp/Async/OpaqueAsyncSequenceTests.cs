// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// A property typed <c>some AsyncSequence&lt;Element, Failure&gt;</c> projects as
/// <c>IAsyncEnumerable&lt;Element&gt;</c>: the elements arrive typed, a non-Never failure rethrows at
/// the <c>await foreach</c>, and a Never failure completes cleanly. Covers the nonisolated actor
/// property shape and a plain class property.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("ios18.0")]
[System.Runtime.Versioning.SupportedOSPlatform("tvos18.0")]
[System.Runtime.Versioning.SupportedOSPlatform("maccatalyst18.0")]
[System.Runtime.Versioning.SupportedOSPlatform("macos15.0")]
public class OpaqueAsyncSequenceTests : TestBase
{
    public OpaqueAsyncSequenceTests(TestResults results) : base(results) { }

    public async Task TestNonisolatedActorProperty_YieldsTypedElements()
    {
        using var meter = new OpaqueLevelMeter(4);

        IAsyncEnumerable<OpaqueSequenceLevel> levels = meter.Levels;
        var collected = await WithTimeout(Task.Run(async () =>
        {
            var list = new List<(int Index, float Value)>();
            await foreach (var level in levels)
                list.Add((level.Index, level.Value));
            return list;
        }), DefaultAsyncTimeout);

        AssertEqual(4, collected.Count, "Levels must yield every element of the hidden sequence");
        for (int i = 0; i < collected.Count; i++)
        {
            AssertEqual(i, collected[i].Index, $"level {i} index");
            AssertEqual(i * 0.5f, collected[i].Value, $"level {i} value");
        }
    }

    public async Task TestThrowingFailure_RethrowsAfterTheElementsBeforeIt()
    {
        using var meter = new OpaqueLevelMeter(5);

        var collected = new List<int>();
        Exception? caught = null;
        try
        {
            await WithTimeout(Task.Run(async () =>
            {
                await foreach (var level in meter.InterruptedLevels)
                    collected.Add(level.Index);
            }), DefaultAsyncTimeout);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        AssertTrue(collected.Count == 2 && collected[0] == 0 && collected[1] == 1,
            $"InterruptedLevels must yield [0,1] before failing; got [{string.Join(",", collected)}]");
        AssertTrue(caught is global::Swift.Runtime.SwiftRuntimeException,
            $"the sequence's error must surface as SwiftRuntimeException; got {caught?.GetType().Name ?? "nothing"}");
        AssertTrue(caught!.Message.Contains("interrupted"),
            $"the error must carry the Swift description; got '{caught.Message}'");
    }

    public async Task TestNeverFailure_CompletesCleanly()
    {
        using var meter = new OpaqueLevelMeter(3);
        using var source = new OpaqueTickSource(4);

        var actorSum = await WithTimeout(SumAsync(meter.Ticks), DefaultAsyncTimeout);
        var classSum = await WithTimeout(SumAsync(source.Ticks), DefaultAsyncTimeout);

        AssertEqual(6L, actorSum, "actor Ticks must drain 1+2+3");
        AssertEqual(10L, classSum, "class Ticks must drain 1+2+3+4");
    }

    private static Task<long> SumAsync(IAsyncEnumerable<int> ticks) => Task.Run(async () =>
    {
        long sum = 0;
        await foreach (var tick in ticks)
            sum += tick;
        return sum;
    });
}
