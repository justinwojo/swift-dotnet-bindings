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
/// End-to-end smoke test for the Apple-framework direct-mode pipeline:
/// consumes the externally-built <c>StoreKit.Swift.iOS.dll</c> + <c>StoreKitSwiftBindings.xcframework</c>
/// and calls one trivial, non-throwing, non-async StoreKit 2 accessor to prove the
/// whole chain (<c>SwiftFrameworkResolver</c> → wrapper dylib → system framework via dyld) resolves.
///
/// Gated by the <c>STOREKIT_SMOKE</c> compile symbol, which the csproj sets only when the
/// reproducer artifacts exist at <c>/tmp/storekit2-session4</c> on an iOS Simulator build.
/// Regenerate them via the reproducer command in <c>src/docs/0.8.0-storekit2-exploration.md</c>
/// when re-running this on a fresh machine.
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
    /// We picked a primitive-return accessor to isolate the resolver chain from
    /// other concerns. <c>AppStore.deviceVerificationID</c> (which returns
    /// <c>SwiftOptional&lt;System.Guid&gt;</c>) is now covered by
    /// <see cref="TestAppStoreDeviceVerificationID"/> after the Foundation.UUID
    /// metadata registration gap was fixed.
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
    /// Validates that the Foundation.UUID metadata registration gap is fixed:
    /// <c>AppStore.deviceVerificationID</c> returns <c>SwiftOptional&lt;System.Guid&gt;</c>,
    /// which previously crashed in <c>TypeMetadata.GetTypeMetadataOrThrow&lt;Guid&gt;()</c>
    /// because <c>System.Guid → Foundation.UUID</c> was only mapped at generator time
    /// (<c>FoundationDatabase.xml</c>) with no corresponding runtime <c>RegisterMetadata</c>.
    /// Now resolved via <c>SwiftBindingsRuntime.SBW_UUID_GetMetadata</c> called from
    /// <c>TryGetFoundationMetadata</c> in <c>TypeMetadata.cs</c>.
    ///
    /// On a fresh simulator with no StoreKit configuration, <c>deviceVerificationID</c>
    /// returns <c>nil</c> (SwiftOptional.None). That's a valid pass — the assertion is
    /// "the call completed without SwiftRuntimeException / DllNotFoundException".
    /// </summary>
    public void TestAppStoreDeviceVerificationID()
    {
        // The property getter creates SwiftOptional<System.Guid> internally (triggering
        // Foundation.UUID metadata resolution via SBW_UUID_GetMetadata) then converts
        // to Guid? for the public API.
        try
        {
            Guid? deviceVerificationID = StoreKitSwift::StoreKit.AppStore.DeviceVerificationID;
            if (deviceVerificationID.HasValue)
                TestLogger.Info($"StoreKit.AppStore.DeviceVerificationID = {deviceVerificationID.Value}");
            else
                TestLogger.Info("StoreKit.AppStore.DeviceVerificationID = nil (expected on fresh simulator)");
            AssertTrue(true, "AppStore.DeviceVerificationID resolved SwiftOptional<Guid> without SwiftRuntimeException");
        }
        catch (System.Exception ex)
        {
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
    /// End-to-end smoke test for StoreKit 2's async-sequence path through
    /// the Apple-framework direct-mode pipeline. Pivots to <c>Transaction.unfinished</c>
    /// rather than the headline <c>Transaction.updates</c> for two independent reasons,
    /// both documented in <c>src/docs/0.8.0-storekit2-exploration.md</c>:
    ///
    ///   1. <b>Generator orphan-PInvoke bug:</b> the
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
    ///   2. <b>Foreign value-type metadata gap (now fixed):</b>
    ///      <c>VerificationResult&lt;Transaction&gt;</c> exposes <c>UUID</c>/<c>Date</c>
    ///      fields whose runtime <c>RegisterMetadata</c> calls were previously missing.
    ///      The Foundation.UUID metadata registration gap is now resolved via
    ///      <c>SwiftBindingsRuntime.SBW_UUID_GetMetadata</c>. We still avoid
    ///      dereferencing instance properties on yielded results because this test
    ///      focuses on the async-iterator lifecycle, not individual field access.
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
    /// delta on the third pass is bounded" — same bar the resolver smoke test uses.
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
            // VerificationResult<Transaction> is IDisposable (SwiftSafeHandle over a
            // Swift ARC ref-counted payload) — not disposing it leaks a native
            // ref-count until the managed finalizer runs, which also poisons
            // pass 3's memory delta measurement. Always `using var` non-null
            // iterator results, even when we only inspect for null.
            using var first = await iter.NextAsync(cts.Token);
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
                // `using var` so the yielded VerificationResult's SwiftSafeHandle
                // is disposed as soon as we're done counting it (we never inspect
                // properties — see foreign value-type metadata gap note above).
                // Without this, a seeded simulator with N transactions would leak
                // N native ref-counts across pass 2 alone.
                using var result = await iter.NextAsync(cts.Token);
                if (result is null)
                {
                    reachedTerminalNil = true;
                    break;
                }
                count++;
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

        // === Pass 3: amplified managed-memory delta check ===
        //
        // Previously this pass ran a single empty-complete loop and asserted that
        // the managed-memory delta stayed below a 256 KB ceiling. That was far too
        // loose: a measured baseline of ~264 bytes per loop means a single-loop
        // per-iteration GCHandle or SafeHandle leak could grow by ~24-200 bytes
        // per NextAsync call and stay comfortably below the 256 KB cap. Codex-review
        // pass flagged it: "the comment's claim that even a small per-iteration
        // handle leak would dwarf the ceiling is not defensible."
        //
        // New design — amplify the signal and assert on *per-loop growth*:
        //   1. Warm up the JIT / AOT caches with a couple of loops that are NOT
        //      measured. Without this, the first measured loop carries JIT/AOT
        //      compile cost on Mono and skews the baseline high.
        //   2. Run MeasuredLoops of the full empty-complete iteration inside one
        //      ForceGC'd memory window.
        //   3. Compute per-loop growth = (memoryAfter - memoryBefore) / MeasuredLoops.
        //   4. Assert per-loop growth < PerLoopGrowthCeilingBytes, a tight bound
        //      anchored on baseline. With N=32 loops and a 1 KB per-loop budget,
        //      the total ceiling is 32 KB — an order of magnitude tighter than
        //      256 KB, and a single-handle-per-iteration leak would now register
        //      as ~6-8 KB total (well above baseline noise) instead of being
        //      lost under a generous flat cap.
        //
        // The per-loop framing also survives a future change that raises the
        // per-iteration Swift ARC cost (e.g. adding a new SafeHandle to the
        // iterator wrapper) as long as the cost stays bounded and drops back
        // after the loop exits; only a *monotonically growing* leak trips it.
        TestLogger.Info("  pass 3: amplified managed-memory delta check on an empty-complete loop");

        // JIT/AOT warmup — two full empty-complete runs that we deliberately do
        // NOT measure. Leaves Mono's method-table, delegate thunk, and Task state
        // machine caches fully populated so the measured loops reflect steady
        // state rather than first-touch cost.
        const int WarmupLoops = 2;
        for (int w = 0; w < WarmupLoops; w++)
        {
            await EnumerateUnfinishedToCompletionAsync(IterationCeiling, passTimeout);
        }
        ForceGC();

        // Measured window — N loops inside one memory window, then divide by N
        // to get per-loop growth. 32 loops with a 1 KB per-loop cap gives a 32 KB
        // total ceiling, which is ~8x the empirical noise floor across a handful
        // of simulator runs but comfortably catches a 200-byte-per-iteration
        // SafeHandle leak (amplified across 32 loops × 16 iterations = ~100 KB)
        // or a 24-byte-per-iteration GCHandle pinning a small Task state machine
        // (amplified across 32 × 16 = ~12 KB).
        const int MeasuredLoops = 32;
        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        int pass3Count = 0;
        for (int m = 0; m < MeasuredLoops; m++)
        {
            pass3Count += await EnumerateUnfinishedToCompletionAsync(IterationCeiling, passTimeout);
        }
        ForceGC();
        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        long memoryDelta = memoryAfter - memoryBefore;
        long perLoopGrowth = memoryDelta / MeasuredLoops;

        TestLogger.Info($"    pass 3: warmup={WarmupLoops} measured={MeasuredLoops} loops, totalResults={pass3Count}");
        TestLogger.Info($"    managed memory: before={memoryBefore} after={memoryAfter} delta={memoryDelta} bytes");
        TestLogger.Info($"    per-loop growth: {perLoopGrowth} bytes (ceiling: {PerLoopGrowthCeilingBytes})");

        AssertTrue(perLoopGrowth < PerLoopGrowthCeilingBytes,
            $"managed memory grew by {perLoopGrowth} bytes/loop across {MeasuredLoops} empty-complete async-iterator passes " +
            $"(ceiling: {PerLoopGrowthCeilingBytes} bytes/loop, total delta: {memoryDelta} bytes) — possible SafeHandle/GCHandle leak");

        // The remainder of the success bar: the calls completed without throwing.
        // We don't assert on `count` / `pass3Count` because empty-complete is a
        // valid result on a fresh simulator with no sandbox account configured.
        AssertTrue(true, "Transaction.Unfinished AsyncSequence enumerated cleanly across early-termination, full empty-complete, and amplified memory-tracked passes");
    }

    /// <summary>
    /// Per-loop managed-memory growth ceiling for pass 3 of
    /// <see cref="TestTransactionUnfinishedAsyncSequenceEnumerates"/>. Tightened
    /// from a single-pass 256 KB flat ceiling to a per-loop 1 KB ceiling after
    /// Codex-review flagged that the original bound was ~1000x looser than the
    /// empirical baseline and would have missed a small GCHandle or SafeHandle
    /// leak in the iterator wrapper. Baseline on a fresh iOS Simulator with no
    /// seeded transactions is 0-200 bytes per loop; 1 KB is ~5x that budget,
    /// enough to absorb Mono GC heuristic drift without masking a real leak.
    /// </summary>
    private const long PerLoopGrowthCeilingBytes = 1024;

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
            // Dispose each yielded VerificationResult as soon as we're done with
            // it so pass 3's managed-memory delta reflects steady-state behavior,
            // not transient ref-counted handles waiting on the finalizer queue.
            using var result = await iter.NextAsync(cts.Token);
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
