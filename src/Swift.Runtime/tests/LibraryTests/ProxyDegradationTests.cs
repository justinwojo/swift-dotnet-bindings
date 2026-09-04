// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The reporting contract behind the consumer-owned degradation lane. A degraded reverse-dispatch
/// callback is silent by construction — a dropped void call, a <c>nil</c>, a <c>false</c> — so the
/// once-per-carrier report is the ONLY thing that turns "my delegate stopped firing" into something
/// diagnosable. These tests pin the two properties that make it usable: it fires for a carrier the
/// first time and never again (so a per-frame delegate cannot flood a log), and it can never be the
/// reason a callback fails (it is invoked from inside an <c>[UnmanagedCallersOnly]</c> receiver,
/// where a managed throw would abort the process).
///
/// <para>The latch is process-global and this assembly runs tests in parallel, so every test uses a
/// handle value of its own and asserts on that handle: the per-handle return value and the filtered
/// event stream are exact regardless of what else is reporting concurrently, while
/// <see cref="ProxyDegradation.ReportCount"/> is only ever asserted as a lower bound.</para>
/// </summary>
public class ProxyDegradationTests
{
    // Handle values are never dereferenced — they are pure keys here.
    private static IntPtr NewHandle() => new IntPtr(Random.Shared.NextInt64(0x10000, long.MaxValue));

    [Fact]
    public void ReportCollectedImpl_ReportsOncePerCarrier()
    {
        var handle = NewHandle();
        try
        {
            Assert.True(ProxyDegradation.ReportCollectedImpl(handle, "IFoo.Bar()"));
            Assert.False(ProxyDegradation.ReportCollectedImpl(handle, "IFoo.Bar()"));
            // A different member on the same carrier is still the same carrier: the "once" is per
            // proxy, not per member, or a delegate with a dozen requirements would report a dozen
            // times for one lifetime mistake.
            Assert.False(ProxyDegradation.ReportCollectedImpl(handle, "IFoo.Baz()"));
        }
        finally
        {
            ProxyDegradation.Forget(handle);
        }
    }

    [Fact]
    public void ReportCollectedImpl_DistinctCarriersEachReport()
    {
        var first = NewHandle();
        var second = NewHandle();
        try
        {
            Assert.True(ProxyDegradation.ReportCollectedImpl(first, "IFoo.Bar()"));
            Assert.True(ProxyDegradation.ReportCollectedImpl(second, "IFoo.Bar()"));
        }
        finally
        {
            ProxyDegradation.Forget(first);
            ProxyDegradation.Forget(second);
        }
    }

    [Fact]
    public void Forget_ClearsTheLatchSoARecycledHandleStartsClean()
    {
        // Handle values come from the allocator and are recycled. Without the deinit-time Forget, a
        // new conformer box landing on a previously-reported address would inherit "already
        // reported" and its own first degradation would be silent.
        var handle = NewHandle();
        try
        {
            Assert.True(ProxyDegradation.ReportCollectedImpl(handle, "IFoo.Bar()"));
            ProxyDegradation.Forget(handle);
            Assert.True(ProxyDegradation.ReportCollectedImpl(handle, "IFoo.Bar()"));
        }
        finally
        {
            ProxyDegradation.Forget(handle);
        }
    }

    [Fact]
    public void ReportCollectedImpl_RaisesImplCollectedOnceWithTheMemberName()
    {
        var handle = NewHandle();
        var seen = new List<SwiftProxyImplCollectedEventArgs>();
        void OnImplCollected(object? sender, SwiftProxyImplCollectedEventArgs args)
        {
            if (args.Handle != handle)
                return;
            lock (seen)
                seen.Add(args);
        }

        ProxyDegradation.ImplCollected += OnImplCollected;
        try
        {
            ProxyDegradation.ReportCollectedImpl(handle, "IFoo.Bar()");
            ProxyDegradation.ReportCollectedImpl(handle, "IFoo.Bar()");

            Assert.Single(seen);
            Assert.Equal(handle, seen[0].Handle);
            Assert.Equal("IFoo.Bar()", seen[0].Member);
        }
        finally
        {
            ProxyDegradation.ImplCollected -= OnImplCollected;
            ProxyDegradation.Forget(handle);
        }
    }

    [Fact]
    public void ReportCollectedImpl_SwallowsAThrowingSubscriber()
    {
        // The report happens mid-way across a native receiver boundary, so an application's
        // diagnostic handler must not be able to turn a degraded callback into a dead process.
        var handle = NewHandle();
        void Throwing(object? sender, SwiftProxyImplCollectedEventArgs args)
        {
            if (args.Handle == handle)
                throw new InvalidOperationException("subscriber blew up");
        }

        ProxyDegradation.ImplCollected += Throwing;
        try
        {
            var exception = Record.Exception(() => ProxyDegradation.ReportCollectedImpl(handle, "IFoo.Bar()"));
            Assert.Null(exception);
        }
        finally
        {
            ProxyDegradation.ImplCollected -= Throwing;
            ProxyDegradation.Forget(handle);
        }
    }

    [Fact]
    public void ReportCollectedImpl_AThrowingSubscriberDoesNotSuppressTheOnesBehindIt()
    {
        // Subscribers are independent diagnostics — an application's own logger and, say, a test
        // probe. Invoking the multicast delegate as one call stops at the first exception, so a
        // single badly-written handler would silently take out every handler registered after it
        // AND make the call report `false` on the call that actually claimed the latch, which no
        // later call can re-claim. Each subscriber gets its own guard, and the return value is
        // decided by the latch alone.
        var handle = NewHandle();
        var secondRan = false;
        void Throwing(object? sender, SwiftProxyImplCollectedEventArgs args)
        {
            if (args.Handle == handle)
                throw new InvalidOperationException("subscriber blew up");
        }
        void Second(object? sender, SwiftProxyImplCollectedEventArgs args)
        {
            if (args.Handle == handle)
                Volatile.Write(ref secondRan, true);
        }

        ProxyDegradation.ImplCollected += Throwing;
        ProxyDegradation.ImplCollected += Second;
        try
        {
            var before = ProxyDegradation.ReportCount;

            Assert.True(ProxyDegradation.ReportCollectedImpl(handle, "IFoo.Bar()"));

            Assert.True(Volatile.Read(ref secondRan),
                "the subscriber registered after the throwing one still ran");
            Assert.True(ProxyDegradation.ReportCount >= before + 1,
                "the carrier's report still counted");
            // The latch was claimed, so the carrier stays silent from here — the throw did not
            // leave it in a state where a later callback would report a second time.
            Assert.False(ProxyDegradation.ReportCollectedImpl(handle, "IFoo.Bar()"));
        }
        finally
        {
            ProxyDegradation.ImplCollected -= Throwing;
            ProxyDegradation.ImplCollected -= Second;
            ProxyDegradation.Forget(handle);
        }
    }

    [Fact]
    public void ReportCount_AdvancesOncePerCarrier()
    {
        // Delta-based: other tests in this assembly may report concurrently and nothing ever lowers
        // the counter, so what this test owns is the DIFFERENCE the two distinct carriers below
        // contribute — three calls, two carriers, two increments.
        var first = NewHandle();
        var second = NewHandle();
        try
        {
            var before = ProxyDegradation.ReportCount;
            ProxyDegradation.ReportCollectedImpl(first, "IFoo.Bar()");
            ProxyDegradation.ReportCollectedImpl(first, "IFoo.Bar()");
            ProxyDegradation.ReportCollectedImpl(second, "IFoo.Bar()");
            Assert.True(ProxyDegradation.ReportCount >= before + 2,
                $"two distinct carriers advance the counter at least twice (before={before}, after={ProxyDegradation.ReportCount})");
        }
        finally
        {
            ProxyDegradation.Forget(first);
            ProxyDegradation.Forget(second);
        }
    }

    [Fact]
    public void CollectedImplError_ReportsAndCarriesADiagnosticMessage()
    {
        // The throwing-requirement terminal. Swift sees an ordinary thrown error, so the message is
        // the only place the real cause can be written down.
        var handle = NewHandle();
        try
        {
            var error = ProxyDegradation.CollectedImplError(handle, "IFoo.ComputeAsync()");

            Assert.IsType<SwiftProxyImplCollectedException>(error);
            Assert.Contains("IFoo.ComputeAsync()", error.Message);
            Assert.Contains(handle.ToString("X"), error.Message);
            // Reported by the act of building the error, so a throwing requirement is as
            // discoverable as a silent no-op.
            Assert.False(ProxyDegradation.ReportCollectedImpl(handle, "IFoo.ComputeAsync()"));
        }
        finally
        {
            ProxyDegradation.Forget(handle);
        }
    }

    [Fact]
    public void SwiftProxyImplCollectedEventArgs_NormalisesANullMember()
    {
        var args = new SwiftProxyImplCollectedEventArgs(new IntPtr(0x1234), null!);
        Assert.Equal(string.Empty, args.Member);
        Assert.Equal(new IntPtr(0x1234), args.Handle);
    }
}
