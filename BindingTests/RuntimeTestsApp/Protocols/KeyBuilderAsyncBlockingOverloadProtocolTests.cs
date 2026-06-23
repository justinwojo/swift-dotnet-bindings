// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression test for AF05 ruling-b on the LEGACY blocking reverse-dispatch receiver (the companion to
/// <see cref="KeyBuilderAsyncOverloadProtocolTests"/>, which covers the real reverse-async witness). The
/// Swift protocol <c>AsyncBlockingOverloadKeys</c> declares BOTH <c>func bar(_:) async -&gt; String</c>
/// and <c>func barAsync(_:) -&gt; String</c>. The async requirement returns a NON-blittable
/// <c>String</c>, which the real-async-witness predicate rejects, so it is satisfied through the legacy
/// blocking witness slot (the receiver blocks the impl's <c>Task</c> with
/// <c>.GetAwaiter().GetResult()</c>).
///
/// Async methods carry a trailing C# <c>CancellationToken</c>, so the two requirements project to two
/// DISTINCT overloads: <c>Task&lt;string&gt; BarAsync(int, CancellationToken)</c> and
/// <c>string BarAsync(int)</c>. The legacy receiver forwards <c>impl.BarAsync(args).GetAwaiter().GetResult()</c>;
/// a BARE argument list binds the SYNC <c>string BarAsync(int)</c> overload (exact arity), whose return is
/// not awaitable — <c>.GetAwaiter()</c> is a CS1061 and the generated proxy fails to compile. The fix
/// threads an explicit <c>default(CancellationToken)</c> through the legacy receiver's impl call so it
/// binds the async overload.
///
/// What this proves, and where:
///   • COMPILE time — the generated <c>AsyncBlockingOverloadKeysProxy</c> compiles only because its
///     <c>Receive_bar_*</c> receiver passes the trailing token; pre-fix it would not build.
///     <see cref="AsyncBlockingOverloadKeysImpl"/> implements both overloads.
///   • RUNTIME (simulator + device) — both requirements reverse-dispatch to their own members:
///     <see cref="TestAsyncBlockingOverloadKeys_SyncRequirement"/> round-trips <c>string BarAsync(int)</c>
///     and <see cref="TestAsyncBlockingOverloadKeys_AsyncRequirement"/> round-trips the async overload
///     through the blocking witness slot. The results are tagged <c>"sync:"</c>/<c>"async:"</c> so a
///     collapse or mis-dispatch between the two is caught.
/// </summary>
public class KeyBuilderAsyncBlockingOverloadProtocolTests : TestBase
{
    public KeyBuilderAsyncBlockingOverloadProtocolTests(TestResults results) : base(results) { }

    /// <summary>
    /// Reverse-dispatch the SYNC requirement <c>barAsync(_:)</c> → C# <c>string BarAsync(int)</c>.
    /// </summary>
    public void TestAsyncBlockingOverloadKeys_SyncRequirement()
    {
        var impl = new AsyncBlockingOverloadKeysImpl();
        var result = Functions.CallAsyncBlockingOverloadBarSync(impl, 7);
        AssertEqual("sync:7", result,
            "Sync BarAsync(int) requirement emits as a distinct overload and reverse-dispatches to the C# impl");
    }

    /// <summary>
    /// Reverse-dispatch the ASYNC requirement <c>bar(_:) async</c> → C#
    /// <c>Task&lt;string&gt; BarAsync(int, CancellationToken)</c> through the legacy blocking witness slot.
    /// The bare-call binding bug (binding the sync overload) is exactly what would have stopped the proxy
    /// from compiling at all.
    /// </summary>
    public async Task TestAsyncBlockingOverloadKeys_AsyncRequirement()
    {
        var impl = new AsyncBlockingOverloadKeysImpl();
        var result = await WithTimeout(
            Functions.CallAsyncBlockingOverloadBarAsync(impl, 7),
            DefaultAsyncTimeout);
        AssertEqual("async:7", result,
            "Async BarAsync(int, CancellationToken) requirement emits as a distinct overload and reverse-dispatches via the legacy blocking witness slot");
        TestLogger.Info($"AsyncBlockingOverloadKeys.AsyncRequirement = {result}");
    }

    /// <summary>
    /// Drive BOTH overloads on the SAME instance: proves the two distinct <c>BarAsync</c> members dispatch
    /// to their respective C# methods (a collapsed key or a bare-call mis-binding would surface here).
    /// </summary>
    public async Task TestAsyncBlockingOverloadKeys_BothOnOneInstance()
    {
        var impl = new AsyncBlockingOverloadKeysImpl();
        var sync = Functions.CallAsyncBlockingOverloadBarSync(impl, 5);
        var async = await WithTimeout(
            Functions.CallAsyncBlockingOverloadBarAsync(impl, 5),
            DefaultAsyncTimeout);
        AssertEqual("sync:5", sync, "Sync overload dispatches to string BarAsync(int)");
        AssertEqual("async:5", async, "Async overload dispatches to Task<string> BarAsync(int, CancellationToken)");
        TestLogger.Info($"AsyncBlockingOverloadKeys.BothOnOneInstance sync={sync} async={async}");
    }
}

// Implements BOTH members of IAsyncBlockingOverloadKeys — proof at compile time that both distinct
// BarAsync overloads emit AND that the legacy blocking receiver binds the async overload (not the sync
// namesake). The results are tagged so a mis-dispatch between the two slots is caught by the assertions.
internal class AsyncBlockingOverloadKeysImpl : IAsyncBlockingOverloadKeys
{
    public string BarAsync(int x) => $"sync:{x}";

    public System.Threading.Tasks.Task<string> BarAsync(int x, System.Threading.CancellationToken cancellationToken = default)
        => System.Threading.Tasks.Task.FromResult($"async:{x}");
}
