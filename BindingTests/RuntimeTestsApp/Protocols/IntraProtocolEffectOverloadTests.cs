// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression test for intra-protocol async/sync effect overloading —
/// the INTRA-protocol twin of <see cref="SiblingMethodDispatchTests"/>'s CROSS-protocol
/// async/sync sibling cases.
///
/// Shape: a SINGLE protocol (<c>IntraEffectTagged</c>) declares BOTH
/// <c>func intraEffectTag(_:) -&gt; Int32</c> and <c>func intraEffectTag(_:) async -&gt; Int32</c>
/// — same name + params + return type, differing only in the <c>async</c> effect. These are
/// two DISTINCT Swift witness-table requirements occupying two SEPARATE vtable slots.
///
/// Pre-fix bug: the three intra-protocol method-identity keys
/// (EveryProtocolEmitter.GetMethodKey, WitnessDispatchEmitter.GetMethodKey,
/// ProtocolSignatureHelper.GetMethodSignatureKey) omitted the <c>async</c> axis, so the two
/// requirements collapsed onto ONE slot — the async requirement's slot was dropped while the
/// C# interface still declared BOTH <c>IntraEffectTag(int)</c> and
/// <c>IntraEffectTagAsync(int, CancellationToken)</c>. That drifts the C# proxy vtable's slot
/// count from Swift's witness-table layout (StructLayout mismatch) and drops a dispatch.
///
/// Fix: all three slot-allocation keys carry the <c>async</c> effect, allocating distinct
/// slots. What this test proves, and where:
///   • COMPILE time — the second (async) slot was allocated: the generated C# interface
///     declares BOTH <c>IntraEffectTag(int)</c> and <c>IntraEffectTagAsync(int, CancellationToken)</c>,
///     so <see cref="IntraEffectTaggedImpl"/> must implement both or the file would not build.
///   • RUNTIME (simulator + device) — BOTH slots dispatch at their correct indices:
///     <see cref="TestIntraEffectTagged_SyncDispatch"/> round-trips through the sync slot, and
///     <see cref="TestIntraEffectTagged_AsyncDispatch"/> round-trips through the async slot via
///     the S13 Pillar C real reverse-async witness — the non-throwing <c>async</c> requirement
///     genuinely suspends on <c>withCheckedContinuation</c> and hands the continuation back to
///     C#. <see cref="TestIntraEffectTagged_BothSlotsOnOneInstance"/> drives BOTH on one instance,
///     proving the two distinct effect-overload slots dispatch with the dual layout intact (a
///     collapsed/misindexed slot would mis-dispatch one of them).
///
/// The async slot was once compile-gated only (the legacy thread-blocking witness hit the
/// confirmed-upstream Mono async assertion, Issue 1); the real reverse-async witness replaces
/// the blocking slot with a continuation handoff, so the async path now runs on Mono.
/// </summary>
public class IntraProtocolEffectOverloadTests : TestBase
{
    public IntraProtocolEffectOverloadTests(TestResults results) : base(results) { }

    /// <summary>
    /// Dispatch the SYNC requirement of a single protocol that ALSO declares an async
    /// same-signature requirement. With both slots allocated, the sync existential call
    /// reaches the C# impl's <c>IntraEffectTag(int)</c> at the correct slot index. Pre-fix
    /// (collapsed slot) this either fails to compile (the dropped async member referenced
    /// downstream) or mis-dispatches.
    /// </summary>
    public void TestIntraEffectTagged_SyncDispatch()
    {
        var impl = new IntraEffectTaggedImpl(multiplier: 3);
        var result = Functions.CallIntraEffectTagSync(impl, 7);
        AssertEqual(21, result,
            "Sync requirement of an intra-protocol async/sync overload dispatches to the C# impl's sync slot (the async slot occupies its own distinct slot)");
    }

    /// <summary>
    /// Dispatch the ASYNC requirement of the same single protocol through the real reverse-async
    /// witness (S13 Pillar C). The await genuinely suspends the Swift task until C# resumes the
    /// boxed continuation, reaching the impl's <c>IntraEffectTagAsync(int)</c> at the SECOND
    /// (async) slot — distinct from the sync slot. Pre-fix this slot was collapsed onto the sync
    /// slot or compile-gated only.
    /// </summary>
    public async Task TestIntraEffectTagged_AsyncDispatch()
    {
        var impl = new IntraEffectTaggedImpl(multiplier: 3);
        var result = await WithTimeout(
            Functions.CallIntraEffectTagAsync(impl, 7),
            DefaultAsyncTimeout);
        AssertEqual(7 * 3 + 1000, result,
            "Async requirement dispatches to the C# impl's async slot via the real reverse-async witness (distinct from the sync slot)");
        TestLogger.Info($"IntraEffectTagged.AsyncDispatch = {result}");
    }

    /// <summary>
    /// Drive BOTH the sync and the async requirement on the SAME protocol instance. Proves the
    /// two distinct effect-overload slots dispatch to their respective C# members with the dual
    /// vtable layout intact — a collapsed slot would mis-dispatch one of the two.
    /// </summary>
    public async Task TestIntraEffectTagged_BothSlotsOnOneInstance()
    {
        var impl = new IntraEffectTaggedImpl(multiplier: 4);
        var sync = Functions.CallIntraEffectTagSync(impl, 5);
        var async = await WithTimeout(
            Functions.CallIntraEffectTagAsync(impl, 5),
            DefaultAsyncTimeout);
        AssertEqual(20, sync, "Sync slot dispatches to IntraEffectTag on the dual-slot instance");
        AssertEqual(1020, async, "Async slot dispatches to IntraEffectTagAsync on the dual-slot instance");
        TestLogger.Info($"IntraEffectTagged.BothSlots sync={sync} async={async}");
    }

    /// <summary>
    /// Deferred completion of the async slot: a genuine yield before producing the value still
    /// resumes the continuation. The legacy thread-blocked slot could not yield; the continuation
    /// handoff completes cleanly.
    /// </summary>
    public async Task TestIntraEffectTagged_AsyncDeferredCompletion()
    {
        var impl = new IntraEffectTaggedImpl(multiplier: 2, deferAsync: true);
        var result = await WithTimeout(
            Functions.CallIntraEffectTagAsync(impl, 9),
            DefaultAsyncTimeout);
        AssertEqual(9 * 2 + 1000, result,
            "Async slot resumes after an awaited yield in the C# impl");
        TestLogger.Info($"IntraEffectTagged.AsyncDeferred = {result}");
    }
}

// Implements BOTH members of the single protocol IIntraEffectTagged: the sync
// IntraEffectTag(int) and the async IntraEffectTagAsync(int, CancellationToken). BOTH paths are
// exercised at runtime — the sync member through the sync slot, the async member through the
// real reverse-async witness. The async result is offset by +1000 so a mis-dispatch between the
// two slots is caught by the assertion. When deferAsync is set the async member yields before
// returning, exercising a genuine suspend/resume rather than an immediately-completed Task.
internal class IntraEffectTaggedImpl : IIntraEffectTagged
{
    private readonly int _multiplier;
    private readonly bool _deferAsync;
    public IntraEffectTaggedImpl(int multiplier, bool deferAsync = false)
    {
        _multiplier = multiplier;
        _deferAsync = deferAsync;
    }

    public int IntraEffectTag(int n) => n * _multiplier;

    public System.Threading.Tasks.Task<int> IntraEffectTagAsync(int n, System.Threading.CancellationToken cancellationToken = default)
        => _deferAsync
            ? DeferredAsync(n)
            : System.Threading.Tasks.Task.FromResult(n * _multiplier + 1000);

    private async System.Threading.Tasks.Task<int> DeferredAsync(int n)
    {
        await System.Threading.Tasks.Task.Yield();
        return n * _multiplier + 1000;
    }
}
