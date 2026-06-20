// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression test for the projected-key builder's protocol-path async <c>CancellationToken</c>
/// inclusion (AF05 ruling b). The Swift protocol <c>AsyncOverloadKeys</c> declares BOTH
/// <c>func foo(_:) async -&gt; Int32</c> and <c>func fooAsync(_:) -&gt; Int32</c>. Async methods emit a
/// trailing C# <c>CancellationToken</c>, so these project to two DISTINCT overloads:
/// <c>Task&lt;int&gt; FooAsync(int, CancellationToken)</c> and <c>int FooAsync(int)</c>.
///
/// Pre-fix bug: the protocol projected-C# overload key OMITTED the trailing <c>CancellationToken</c>
/// (the class/default paths already included it), so both requirements keyed to <c>FooAsync(int)</c>
/// and the protocol requirement-dedup SILENTLY DROPPED the second — one <c>FooAsync</c> member (and
/// its proxy witness forwarding) never emitted, so reverse-dispatching the dropped requirement
/// mis-routes. Fix: the merged key builder appends <c>CancellationToken</c> for async on ALL paths,
/// so the keys diverge and BOTH members emit.
///
/// What this proves, and where:
///   • COMPILE time — BOTH overloads emit on <c>IAsyncOverloadKeys</c>: <see cref="AsyncOverloadKeysImpl"/>
///     implements both <c>Task&lt;int&gt; FooAsync(int, CancellationToken)</c> and <c>int FooAsync(int)</c>;
///     if the dedup still dropped one, the interface would declare a single <c>FooAsync</c> and this
///     file would not build the same way.
///   • RUNTIME (simulator + device) — both requirements dispatch at their own slots:
///     <see cref="TestAsyncOverloadKeys_SyncRequirement"/> round-trips the sync <c>FooAsync(int)</c>, and
///     <see cref="TestAsyncOverloadKeys_AsyncRequirement"/> round-trips the async overload through the
///     S13 Pillar C real reverse-async witness. The async result is offset by +1000 so a collapse/
///     mis-dispatch between the two is caught. <see cref="TestAsyncOverloadKeys_BothOnOneInstance"/>
///     drives both on one instance.
/// </summary>
public class KeyBuilderAsyncOverloadProtocolTests : TestBase
{
    public KeyBuilderAsyncOverloadProtocolTests(TestResults results) : base(results) { }

    /// <summary>
    /// Reverse-dispatch the SYNC requirement <c>fooAsync(_:)</c> → C# <c>int FooAsync(int)</c>. Pre-fix,
    /// if this was the dropped member, its proxy witness is missing and the call mis-routes.
    /// </summary>
    public void TestAsyncOverloadKeys_SyncRequirement()
    {
        var impl = new AsyncOverloadKeysImpl(multiplier: 2);
        var result = Functions.CallAsyncOverloadFooSync(impl, 7);
        AssertEqual(14, result,
            "Sync FooAsync(int) requirement emits as a distinct overload and reverse-dispatches to the C# impl");
    }

    /// <summary>
    /// Reverse-dispatch the ASYNC requirement <c>foo(_:) async</c> → C#
    /// <c>Task&lt;int&gt; FooAsync(int, CancellationToken)</c> through the real reverse-async witness. The
    /// await genuinely suspends the Swift task until C# resumes the continuation.
    /// </summary>
    public async Task TestAsyncOverloadKeys_AsyncRequirement()
    {
        var impl = new AsyncOverloadKeysImpl(multiplier: 2);
        var result = await WithTimeout(
            Functions.CallAsyncOverloadFooAsync(impl, 7),
            DefaultAsyncTimeout);
        AssertEqual(7 * 2 + 1000, result,
            "Async FooAsync(int, CancellationToken) requirement emits as a distinct overload and reverse-dispatches via the real reverse-async witness");
        TestLogger.Info($"AsyncOverloadKeys.AsyncRequirement = {result}");
    }

    /// <summary>
    /// Drive BOTH overloads on the SAME instance: proves the two distinct <c>FooAsync</c> members
    /// dispatch to their respective C# methods (a collapsed key would have dropped one of them).
    /// </summary>
    public async Task TestAsyncOverloadKeys_BothOnOneInstance()
    {
        var impl = new AsyncOverloadKeysImpl(multiplier: 4);
        var sync = Functions.CallAsyncOverloadFooSync(impl, 5);
        var async = await WithTimeout(
            Functions.CallAsyncOverloadFooAsync(impl, 5),
            DefaultAsyncTimeout);
        AssertEqual(20, sync, "Sync overload dispatches to int FooAsync(int)");
        AssertEqual(1020, async, "Async overload dispatches to Task<int> FooAsync(int, CancellationToken)");
        TestLogger.Info($"AsyncOverloadKeys.BothOnOneInstance sync={sync} async={async}");
    }
}

// Implements BOTH members of IAsyncOverloadKeys — proof at compile time that both distinct FooAsync
// overloads emit. The async result is offset by +1000 so a mis-dispatch between the two slots is
// caught by the assertions.
internal class AsyncOverloadKeysImpl : IAsyncOverloadKeys
{
    private readonly int _multiplier;
    public AsyncOverloadKeysImpl(int multiplier) => _multiplier = multiplier;

    public int FooAsync(int x) => x * _multiplier;

    public System.Threading.Tasks.Task<int> FooAsync(int x, System.Threading.CancellationToken cancellationToken = default)
        => System.Threading.Tasks.Task.FromResult(x * _multiplier + 1000);
}
