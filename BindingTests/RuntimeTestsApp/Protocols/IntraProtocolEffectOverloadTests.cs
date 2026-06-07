// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression test for intra-protocol async/sync effect overloading (audit §6 #12) —
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
///   • RUNTIME (simulator + device) — the SYNC slot dispatches at the correct index:
///     <see cref="TestIntraEffectTagged_SyncDispatch"/> round-trips through the sync slot; a
///     collapsed/misindexed slot would mis-dispatch or fail to compile.
/// The async slot is NOT invoked at runtime — there is deliberately no async driver. An async
/// requirement reverse-dispatched over CallConvSwift hits the confirmed-upstream Mono async
/// assertion (Issue 1), so exercising it would be a known-bad path, not a regression signal;
/// the compile-time slot-count guarantee plus the sync-slot round-trip are the durable gate.
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
}

// Implements BOTH members of the single protocol IIntraEffectTagged: the sync
// IntraEffectTag(int) and the async IntraEffectTagAsync(int, CancellationToken). Only the
// sync path is exercised at runtime; the async member's presence proves the second slot was
// allocated (the interface declares both, so the impl must satisfy both).
internal class IntraEffectTaggedImpl : IIntraEffectTagged
{
    private readonly int _multiplier;
    public IntraEffectTaggedImpl(int multiplier) { _multiplier = multiplier; }

    public int IntraEffectTag(int n) => n * _multiplier;

    public System.Threading.Tasks.Task<int> IntraEffectTagAsync(int n, System.Threading.CancellationToken cancellationToken = default)
        => System.Threading.Tasks.Task.FromResult(n * _multiplier + 1000);
}
