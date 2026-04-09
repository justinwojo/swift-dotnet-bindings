// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if STOREKIT_SMOKE
extern alias StoreKitSwift;

using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SmokeTests;

/// <summary>
/// Session 5 end-to-end smoke test for the Apple-framework direct-mode pipeline:
/// consumes the externally-built <c>StoreKit.Swift.iOS.dll</c> + <c>StoreKitSwiftBindings.xcframework</c>
/// and calls one trivial, non-throwing, non-async StoreKit 2 accessor to prove the
/// whole chain (<c>SwiftFrameworkResolver</c> → wrapper dylib → system framework via dyld) resolves.
///
/// Gated by the <c>STOREKIT_SMOKE</c> compile symbol, which the csproj sets only when the
/// Session 4 artifacts exist at <c>/tmp/storekit2-session4</c> on an iOS Simulator build.
/// Regenerate them via the reproducer command in <c>src/docs/0.8.0-storekit2-exploration.md</c>
/// (Session 4 section) when re-running this on a fresh machine.
/// </summary>
public class StoreKitSmokeTests : TestBase
{
    public StoreKitSmokeTests(TestResults results) : base(results) { }

    /// <summary>
    /// The minimum viable success signal for the Apple-framework direct-mode pipeline:
    /// a single <c>LibraryImport("StoreKitSwiftBindings")</c> call resolves, the wrapper
    /// dylib pulls <c>/System/Library/Frameworks/StoreKit.framework/StoreKit</c> into
    /// the process as a transitive dependency (dyld resolves it via the absolute path
    /// baked into the wrapper's load commands at link time), the <c>@_cdecl</c> thunk
    /// runs inside the wrapper, calls <c>StoreKit.AppStore.canMakePayments</c> on the
    /// real StoreKit 2 API, and returns a plain <c>bool</c>.
    ///
    /// We specifically picked a primitive-return accessor rather than
    /// <c>AppStore.deviceVerificationID</c> (the originally planned candidate) because
    /// the latter returns <c>SwiftOptional&lt;System.Guid&gt;</c> whose cctor reaches
    /// <c>TypeMetadata.GetTypeMetadataOrThrow&lt;System.Guid&gt;()</c>, and
    /// <c>System.Guid → Foundation.UUID</c> is currently only mapped at generator time
    /// (<c>FoundationDatabase.xml</c>) — there is no runtime <c>RegisterMetadata</c>
    /// call for it. That's an orthogonal Swift.Runtime gap that deserves its own
    /// follow-up session; this smoke test exists solely to verify the resolver chain.
    ///
    /// The value itself is allowed to be either true or false — an iOS Simulator with
    /// no StoreKit configuration legitimately reports <c>false</c>. The assertion is
    /// solely "the call completed without DllNotFoundException /
    /// EntryPointNotFoundException".
    /// </summary>
    public void TestAppStoreCanMakePayments()
    {
        // The Microsoft.iOS ObjC bindings also expose StoreKit.AppStore, so we route
        // through the StoreKitSwift extern alias to pick the Swift-side type.
        try
        {
            bool canMakePayments = StoreKitSwift::StoreKit.AppStore.CanMakePayments;
            TestLogger.Info($"StoreKit.AppStore.CanMakePayments = {canMakePayments}");
            AssertTrue(true, "AppStore.CanMakePayments call completed without DllNotFound/EntryPointNotFound");
        }
        catch (System.Exception ex)
        {
            // Log the full exception chain so we can see what's actually wrong beyond
            // the wrapping TargetInvocationException the reflection invoker produces.
            var inner = ex;
            var depth = 0;
            while (inner != null)
            {
                TestLogger.Info($"  [ex{depth}] {inner.GetType().FullName}: {inner.Message}");
                if (inner.StackTrace != null)
                    TestLogger.Info($"  [ex{depth}] stack: {inner.StackTrace}");
                inner = inner.InnerException;
                depth++;
            }
            throw;
        }
    }

    /// <summary>
    /// Session 6 end-to-end smoke test for StoreKit 2's async-sequence path through
    /// the Apple-framework direct-mode pipeline. Pivots to <c>Transaction.unfinished</c>
    /// rather than the headline <c>Transaction.updates</c> for two independent reasons,
    /// both documented in the Session 6 outcome of <c>0.8.0-storekit2-exploration.md</c>:
    ///
    ///   1. <b>Generator orphan-PInvoke bug (filed as Session 6 follow-up):</b> the
    ///      generator emits the <c>[LibraryImport]</c> declaration for
    ///      <c>SBW_Get_StoreKit_Transaction_updates</c> but drops the private wrapper
    ///      method AND the public <c>Updates</c> property — there is literally no
    ///      <c>StoreKit.Transaction.Updates</c> symbol in the generated <c>StoreKit.cs</c>
    ///      to call. The same orphan pattern hits <c>Storefront.updates</c> and
    ///      <c>Product.SubscriptionInfo.Status.updates</c>. Until that bug is fixed,
    ///      <c>Transaction.unfinished</c> is the closest drop-in proxy: same return
    ///      type (<c>Transaction.Transactions</c>), same <c>MakeAsyncIterator</c>,
    ///      same <c>NextAsync</c>, same <c>VerificationResult&lt;Transaction&gt;</c>
    ///      element type, same <c>SBW_StoreKit_AsyncIterator_next_675F1A37_async</c>
    ///      entry point — exercising it validates the entire async-iterator wrapper
    ///      code path that <c>Transaction.updates</c> would also use.
    ///
    ///   2. <b>Foreign value-type metadata gap (Session 5 hazard, dodged here):</b>
    ///      <c>VerificationResult&lt;Transaction&gt;</c> exposes <c>UUID</c>/<c>Date</c>
    ///      fields whose runtime <c>RegisterMetadata</c> calls are missing. We avoid
    ///      tripping the gap by NOT dereferencing any instance properties on the
    ///      yielded results — the test only counts iterations and verifies the
    ///      iterator empty-completes. The static initializers for <c>Transactions</c>,
    ///      <c>AsyncIterator</c>, and <c>VerificationResult&lt;TSignedType&gt;</c>
    ///      themselves do NOT touch <c>SwiftOptional&lt;Guid&gt;</c> /
    ///      <c>SwiftOptional&lt;DateTime&gt;</c>, so loading and iterating the
    ///      sequence is safe — only field access on the yielded result would crash.
    ///
    /// On a fresh iOS Simulator with no purchases, <c>Transaction.unfinished</c>
    /// empty-completes immediately (zero VerificationResults yielded, then nil).
    /// That is a valid pass — the success criterion is "iteration completes cleanly,
    /// no <c>EntryPointNotFoundException</c>, no <c>SwiftRuntimeException</c>, no
    /// Mono abort, no double-free on early termination." Reading an actual
    /// transaction is gravy.
    ///
    /// ARC verification: the test runs the iteration THREE times — once with early
    /// termination (one MoveNext call, then dispose), once to full empty-complete,
    /// and once more under managed-memory tracking. The early-termination pass
    /// exercises the partial-iterator dispose path; the empty-complete pass
    /// exercises the terminal-completion dispose path. Native ARC ref-count
    /// inspection isn't surfaced through <c>SwiftSafeHandle</c> in this repo, so
    /// the success bar is "no managed exception, no Mono abort, managed memory
    /// delta on the third pass is bounded" — same bar Session 5 used for the
    /// resolver smoke test.
    /// </summary>
    public async Task TestTransactionUnfinishedAsyncSequenceEnumerates()
    {
        // Hard ceiling for unexpected sandbox state — if the simulator somehow has
        // 1000+ unfinished transactions (it shouldn't, but a developer might have
        // a StoreKit configuration file with seeded data), we want to bound the
        // loop rather than hang the test. Hitting the ceiling is treated as a
        // FAILURE, not a soft warning, because it means the iterator never returned
        // its terminal-nil signal — the very thing we're trying to validate.
        const int IterationCeiling = 16;

        // Per-pass timeout budget. One CancellationTokenSource is allocated per
        // pass and its token is reused across every NextAsync call inside the
        // loop, rather than constructing a fresh CTS per call. This (a) keeps
        // pass 3's managed-memory measurement from being polluted by 16x CTS
        // allocations and (b) gives a single coherent deadline for the entire
        // pass instead of resetting the budget on each iteration.
        var passTimeout = DefaultAsyncTimeout;

        // === Pass 1: early-terminate after one NextAsync call ===
        // Exercises the dispose path on a partially-iterated AsyncSequence.
        // If the SafeHandle release / Swift ARC release double-frees on early
        // termination, this pass crashes inside Dispose().
        TestLogger.Info("  pass 1: early-terminate after first NextAsync");
        {
            using var seq = StoreKitSwift::StoreKit.Transaction.Unfinished;
            using var iter = seq.MakeAsyncIterator();
            using var cts = new CancellationTokenSource(passTimeout);
            var first = await iter.NextAsync(cts.Token);
            TestLogger.Info($"    first NextAsync returned: {(first is null ? "null (empty stream)" : "non-null VerificationResult")}");
            // Dispose scopes fall out of the using blocks here — early termination.
        }

        // GC between passes to encourage Mono finalizers on any orphaned managed
        // wrappers. If the previous pass leaked a SafeHandle that's still pinned
        // by an in-flight async callback, this is where we'd surface the issue.
        ForceGC();

        // === Pass 2: enumerate to terminal completion ===
        // Exercises the dispose path on a fully-drained AsyncSequence and validates
        // that the iterator's terminal-nil signal correctly propagates through the
        // generated NextAsync's TaskCompletionSource → C# null check.
        TestLogger.Info("  pass 2: enumerate to terminal completion");
        int count = 0;
        bool reachedTerminalNil = false;
        {
            using var seq = StoreKitSwift::StoreKit.Transaction.Unfinished;
            using var iter = seq.MakeAsyncIterator();
            using var cts = new CancellationTokenSource(passTimeout);
            while (count < IterationCeiling)
            {
                var result = await iter.NextAsync(cts.Token);
                if (result is null)
                {
                    reachedTerminalNil = true;
                    break;
                }
                count++;
                // Deliberately do NOT touch any property on `result` — see foreign
                // value-type metadata gap note in the XML doc above. Just count.
            }
        }
        TestLogger.Info($"    enumerated {count} VerificationResult entries before terminal completion");
        // Hitting the ceiling without seeing the terminal nil means we never
        // proved the iterator's empty-completion signal works. That's a failure,
        // not a noisy log line, because validating the terminal-nil path is
        // half the point of the test.
        AssertTrue(reachedTerminalNil,
            $"Transaction.Unfinished iterator did not return terminal nil within {IterationCeiling} iterations — terminal-completion path is unverified");

        ForceGC();

        // === Pass 3: managed-memory delta check ===
        // Run the same full empty-complete loop a third time inside a memory tracker.
        // We can't measure native ARC ref counts without dropping into SafeHandle
        // internals, but we can confirm the managed allocation footprint of running
        // an additional pass doesn't grow unboundedly. A small or negative delta is
        // the success signal — large positive growth across an empty-complete loop
        // would indicate a managed-side leak (SafeHandle not disposed, GCHandle
        // still pinned, etc.).
        TestLogger.Info("  pass 3: managed-memory delta check on a fresh empty-complete pass");
        // Measure inline around `await` rather than wrapping in TrackMemory(Action) —
        // the latter would force a `.GetAwaiter().GetResult()` on the iOS main thread,
        // which sync-over-async-deadlocks the test runner.
        ForceGC();
        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        int pass3Count = await EnumerateUnfinishedToCompletionAsync(IterationCeiling, passTimeout);
        ForceGC();
        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        long memoryDelta = memoryAfter - memoryBefore;
        TestLogger.Info($"    pass 3 enumerated {pass3Count} VerificationResult entries");
        TestLogger.Info($"    managed memory: before={memoryBefore} after={memoryAfter} delta={memoryDelta} bytes");

        // Soft cap on the managed-memory delta: 256 KB. Empirically pass 3 sees
        // a delta of ~264 bytes on an empty-complete loop on a fresh simulator,
        // so a 256 KB ceiling leaves three orders of magnitude of headroom for
        // legitimate noise (Mono GC heuristics, JIT cache, etc.) while still
        // catching a real leak — a per-iteration SafeHandle leak across 16
        // iterations would dwarf this even with small handles.
        const long MemoryDeltaCeilingBytes = 256 * 1024;
        AssertTrue(memoryDelta < MemoryDeltaCeilingBytes,
            $"managed memory grew by {memoryDelta} bytes across an empty-complete async-iterator pass (ceiling: {MemoryDeltaCeilingBytes} bytes) — possible SafeHandle/GCHandle leak");

        // The remainder of the success bar matches Session 5: the calls completed
        // without throwing. We don't assert on `count` / `pass3Count` because
        // empty-complete is a valid result on a fresh simulator with no sandbox
        // account configured.
        AssertTrue(true, "Transaction.Unfinished AsyncSequence enumerated cleanly across early-termination, full empty-complete, and memory-tracked passes");
    }

    /// <summary>
    /// Helper for pass 3 of <see cref="TestTransactionUnfinishedAsyncSequenceEnumerates"/>.
    /// Runs a full empty-complete iteration of <c>Transaction.Unfinished</c> and
    /// returns the count of yielded results. Does not touch result properties.
    /// Uses a single <see cref="CancellationTokenSource"/> for the whole pass so
    /// the managed-memory measurement isn't polluted by per-iteration CTS allocs.
    /// </summary>
    private static async Task<int> EnumerateUnfinishedToCompletionAsync(int ceiling, TimeSpan passTimeout)
    {
        using var seq = StoreKitSwift::StoreKit.Transaction.Unfinished;
        using var iter = seq.MakeAsyncIterator();
        using var cts = new CancellationTokenSource(passTimeout);
        int count = 0;
        while (count < ceiling)
        {
            var result = await iter.NextAsync(cts.Token);
            if (result is null)
            {
                break;
            }
            count++;
        }
        return count;
    }
}

#endif
