// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Layer A coverage for <see cref="SwiftAsyncCancellation.NextCancelKey"/>.
/// The cancellation registry key MUST be collision-free for the lifetime of the
/// process: a recyclable GCHandle cookie let a just-completed task's
/// <c>defer { _sbwUnregisterTask }</c> evict a newer task that reused the freed
/// cookie, and let a racing cancel hit unrelated work. The replacement is a strictly
/// increasing process-wide counter. These assertions are relative (monotonic /
/// distinct / non-zero) rather than absolute, because the counter is static and other
/// tests in the same process may have advanced it — only the invariants matter.
/// </summary>
public class SwiftAsyncCancellationTests
{
    [Fact]
    public void NextCancelKey_IsStrictlyMonotonic()
    {
        long previous = SwiftAsyncCancellation.NextCancelKey();
        for (int i = 0; i < 10_000; i++)
        {
            long next = SwiftAsyncCancellation.NextCancelKey();
            Assert.True(next > previous,
                $"key must strictly increase: got {next} after {previous}");
            previous = next;
        }
    }

    [Fact]
    public void NextCancelKey_NeverReturnsZeroSentinel()
    {
        // 0 is reserved as a sentinel (a never-issued key), so every issued key is >= 1.
        for (int i = 0; i < 1_000; i++)
            Assert.True(SwiftAsyncCancellation.NextCancelKey() > 0, "0 is a reserved sentinel and must never be issued");
    }

    [Fact]
    public void NextCancelKey_SkipsZeroOnWraparound()
    {
        // 64-bit wraparound is unreachable in practice, but the "0 is never issued" sentinel
        // guarantee must still hold if the counter ever passes through 0. Drive the private
        // counter to -1 by reflection so the next Interlocked.Increment lands on exactly 0,
        // and assert the issued key skips 0 (advancing to 1). A landing on a negative value is
        // allowed — only 0 is reserved — so a wrap from long.MaxValue returns long.MinValue
        // unchanged. The other tests in this class run sequentially (same xUnit collection)
        // and read their own baseline first, so the temporary mutation here cannot perturb
        // them; the original value is restored regardless.
        var field = typeof(SwiftAsyncCancellation).GetField("s_nextCancelKey",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("s_nextCancelKey field not found");

        long original = (long)field.GetValue(null)!;
        try
        {
            field.SetValue(null, -1L);
            Assert.Equal(1L, SwiftAsyncCancellation.NextCancelKey());

            field.SetValue(null, long.MaxValue);
            Assert.Equal(long.MinValue, SwiftAsyncCancellation.NextCancelKey());
        }
        finally
        {
            field.SetValue(null, original);
        }
    }

    [Fact]
    public async Task NextCancelKey_IsDistinctUnderConcurrency()
    {
        // The recyclable-cookie bug surfaced under concurrency: two in-flight tasks
        // colliding on the same key. Hammer the counter from many threads and assert
        // every issued key is unique — the property that makes registry eviction safe.
        const int threads = 8;
        const int perThread = 25_000;
        var keys = new ConcurrentBag<long>();

        await Task.WhenAll(Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
                keys.Add(SwiftAsyncCancellation.NextCancelKey());
        })));

        int total = threads * perThread;
        Assert.Equal(total, keys.Count);
        Assert.Equal(total, keys.Distinct().Count());
        Assert.DoesNotContain(0L, keys);
    }
}
