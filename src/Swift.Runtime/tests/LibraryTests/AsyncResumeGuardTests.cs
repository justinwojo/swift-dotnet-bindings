// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 37 — mechanical resume-once. A Swift async-closure continuation box is resumed by
/// consuming its <c>+1</c> retain (<c>takeRetainedValue()</c>); resuming a second time is a
/// use-after-free plus a Swift <c>fatalError</c>. The Swift box carries no once-flag, so
/// <see cref="AsyncResumeGuard"/> is the SOLE guarantee that the success path, the error path, and
/// the Start-thunk synchronous failure paths between them resume the box exactly once. The
/// companion root-cause fix is that a post-resume result Dispose that throws must NOT propagate
/// into the completion's catch (which would resume the already-consumed box again) — covered by
/// <see cref="AsyncClosureHelper.DisposeResultQuietly{T}"/>.
/// </summary>
public class AsyncResumeGuardTests
{
    [Fact]
    public void TryClaim_FirstCallWins_AllLaterCallsLose()
    {
        var guard = new AsyncResumeGuard();
        Assert.True(guard.TryClaim());
        Assert.False(guard.TryClaim());
        Assert.False(guard.TryClaim());
    }

    [Fact]
    public async Task TryClaim_UnderContention_ExactlyOneWinner()
    {
        // The whole point of the guard is the success/error/failure races: many threads attempt to
        // resume the same box concurrently and exactly one must win.
        const int threads = 64;
        for (int trial = 0; trial < 500; trial++)
        {
            var guard = new AsyncResumeGuard();
            int winners = 0;
            using var start = new ManualResetEventSlim(false);

            var workers = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
            {
                start.Wait();
                if (guard.TryClaim())
                    Interlocked.Increment(ref winners);
            })).ToArray();

            start.Set();
            await Task.WhenAll(workers);

            Assert.Equal(1, winners);
        }
    }

    [Fact]
    public void DisposeResultQuietly_NormalDisposable_IsDisposed()
    {
        var probe = new DisposeProbe();
        AsyncClosureHelper.DisposeResultQuietly(probe);
        Assert.Equal(1, probe.DisposeCount);
    }

    [Fact]
    public void DisposeResultQuietly_ThrowingDisposable_DoesNotPropagate()
    {
        // Root cause of the double-resume: if a result's Dispose() threw after a successful resume,
        // the exception propagated into the completion's catch and resumed the box a second time.
        // DisposeResultQuietly must swallow it so the single resume stands.
        var probe = new ThrowingDisposeProbe();
        var ex = Record.Exception(() => AsyncClosureHelper.DisposeResultQuietly(probe));
        Assert.Null(ex);
        Assert.Equal(1, probe.DisposeAttempts);
    }

    [Fact]
    public void DisposeResultQuietly_NonDisposable_IsNoOp()
    {
        // Value-type and non-IDisposable results must pass through without boxing surprises.
        var ex = Record.Exception(() => AsyncClosureHelper.DisposeResultQuietly(42));
        Assert.Null(ex);
    }

    private sealed class DisposeProbe : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingDisposeProbe : IDisposable
    {
        public int DisposeAttempts { get; private set; }
        public void Dispose()
        {
            DisposeAttempts++;
            throw new InvalidOperationException("consumer Dispose() is buggy");
        }
    }
}
