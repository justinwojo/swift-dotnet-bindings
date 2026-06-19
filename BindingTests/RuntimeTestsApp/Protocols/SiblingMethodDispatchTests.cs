// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression tests for sibling-protocol METHOD dispatch —
/// the method analog of <see cref="SiblingPropertyDispatchTests"/>.
///
/// Shape: two class-bound protocols (SiblingMethodOwner, SiblingMethodPeer)
/// declare the same method signature. Methods have no accessor sets, so the
/// EveryProtocol emitter picks the lexicographically-smaller name as OWNER
/// (Owner &lt; Peer), emits the shared method body on its extension, and emits an
/// EMPTY extension for the Peer; Swift's cross-extension witness resolution
/// stitches the Peer's requirement into the owner's body.
///
/// Pre-fix bug: the owner's body hard-coded its OWN global vtable. A C# class
/// implementing only the Peer left the owner vtable nil; dispatch through the
/// Peer existential force-unwrapped that nil function pointer and SIGSEGV'd, or —
/// after any owner proxy primed the owner vtable globally — silently returned the
/// dead-impl "" because the owner's receiver could not locate the Peer's
/// per-instance proxy.
///
/// Fix: the owner body fans out across both sibling vtables, and the owner's C#
/// receiver tries this interface first then each recorded sibling interface, so
/// the Peer proxy is located per-instance regardless of which branch the fan-out
/// takes first.
/// </summary>
public class SiblingMethodDispatchTests : TestBase
{
    public SiblingMethodDispatchTests(TestResults results) : base(results) { }

    /// <summary>
    /// The pre-fix crash: a C# class implementing only the PEER (non-owner).
    /// Swift calls the shared method through the Peer existential; before the fix
    /// this routes to the owner body which force-unwraps a nil
    /// _siblingMethodOwner_vtable pointer (or returns the dead-impl "").
    /// </summary>
    public void TestCallMethodThroughPeerExistential()
    {
        var impl = new SiblingMethodPeerOnlyImpl("peer-value");
        var result = Functions.CallSiblingMethodViaPeer(impl);
        AssertEqual("peer-value", result,
            "Calling shared method via Peer (non-owner) existential returns the C# impl value");
    }

    /// <summary>
    /// Control: a C# class implementing only the OWNER. Owner-body dispatch
    /// through the owner's own vtable. Must work before and after the fix.
    /// </summary>
    public void TestCallMethodThroughOwnerExistential()
    {
        var impl = new SiblingMethodOwnerOnlyImpl("owner-value");
        var result = Functions.CallSiblingMethodViaOwner(impl);
        AssertEqual("owner-value", result,
            "Calling shared method via Owner existential returns the C# impl value");
    }

    /// <summary>
    /// Reverse-order regression: prime the owner vtable globally (it is a
    /// process-wide var), THEN dispatch through the Peer. The owner branch of the
    /// fan-out fires first because its function pointer is non-nil, so this only
    /// passes if the owner's receiver falls back to the Peer interface and finds
    /// this instance's proxy. Without the receiver-side fallback it returns "".
    /// </summary>
    public void TestCallMethodThroughPeerAfterOwnerPrimed()
    {
        var primer = new SiblingMethodOwnerOnlyImpl("primer");
        _ = Functions.CallSiblingMethodViaOwner(primer);

        var peer = new SiblingMethodPeerOnlyImpl("peer-after-owner");
        var result = Functions.CallSiblingMethodViaPeer(peer);
        AssertEqual("peer-after-owner", result,
            "Peer dispatch must succeed after the owner's vtable was registered globally");
    }

    /// <summary>
    /// A single C# class implementing BOTH sibling interfaces. Both vtables get
    /// populated for the same handle; dispatch through either existential must
    /// reach the one impl.
    /// </summary>
    public void TestCallMethodThroughBothOnMultiImpl()
    {
        var impl = new SiblingMethodFullImpl("both");
        var viaOwner = Functions.CallSiblingMethodViaOwner(impl);
        var viaPeer = Functions.CallSiblingMethodViaPeer(impl);
        AssertEqual("both", viaOwner, "Multi-sibling impl: call via Owner existential");
        AssertEqual("both", viaPeer, "Multi-sibling impl: call via Peer existential");
    }

    /// <summary>
    /// Argument-bearing shared method through the PEER existential. Exercises the
    /// fan-out body's argument-copy emission and the receiver path that must
    /// unmarshal the Int32 parameter once before trying each sibling impl.
    /// </summary>
    public void TestCallEchoMethodThroughPeerExistential()
    {
        var impl = new SiblingMethodPeerOnlyImpl("peer");
        var result = Functions.CallSiblingMethodEchoViaPeer(impl, 7);
        AssertEqual("echo:7:peer", result,
            "Argument-bearing shared method via Peer existential round-trips arg + dispatches to C# impl");
    }

    /// <summary>
    /// Control for the argument-bearing method: through the OWNER existential.
    /// </summary>
    public void TestCallEchoMethodThroughOwnerExistential()
    {
        var impl = new SiblingMethodOwnerOnlyImpl("owner");
        var result = Functions.CallSiblingMethodEchoViaOwner(impl, 42);
        AssertEqual("echo:42:owner", result,
            "Argument-bearing shared method via Owner existential round-trips arg + dispatches to C# impl");
    }

    // MARK: - Sibling-method NAME divergence
    //
    // SiblingNameOwner also declares a `collidingTag` property colliding with its
    // `collidingTag(_:)` method, so its interface renames the method to
    // `CollidingTagMethod(int)` while SiblingNamePeer (no property) keeps the plain
    // `CollidingTag(int)`. The owner's fan-out receiver must call the PEER's OWN name
    // when falling back; reusing the owner's renamed name would emit a call the Peer
    // interface never defined (caught loud at the compile gate as CS1061). These
    // round-trips prove the per-protocol name resolution dispatches correctly.

    /// <summary>
    /// Dispatch through the Peer (non-owner) existential. The owner-body fan-out's
    /// sibling fallback into the Peer interface must use the Peer's own `CollidingTag`,
    /// not the owner's renamed `CollidingTagMethod`.
    /// </summary>
    public void TestSiblingNameDivergence_PeerExistential()
    {
        var impl = new SiblingNamePeerOnlyImpl("peer-name");
        var result = Functions.CallSiblingNameViaPeer(impl, 5);
        AssertEqual("name:5:peer-name", result,
            "Sibling-fallback resolves the Peer's own CollidingTag name (not the owner's renamed CollidingTagMethod)");
    }

    /// <summary>
    /// Control: dispatch through the OWNER existential, whose method is renamed
    /// `CollidingTagMethod` because the `CollidingTag` property took the slot.
    /// </summary>
    public void TestSiblingNameDivergence_OwnerExistential()
    {
        var impl = new SiblingNameOwnerOnlyImpl("owner-name");
        var result = Functions.CallSiblingNameViaOwner(impl, 9);
        AssertEqual("name:9:owner-name", result,
            "Owner existential dispatches to the renamed CollidingTagMethod; the CollidingTag property is distinct");
    }

    /// <summary>
    /// Reverse-order regression: prime the owner vtable globally, then dispatch
    /// through the Peer — the owner branch fires first, so success requires the
    /// owner's receiver to fall back to the Peer interface under the Peer's own name.
    /// </summary>
    public void TestSiblingNameDivergence_PeerAfterOwnerPrimed()
    {
        var primer = new SiblingNameOwnerOnlyImpl("primer");
        _ = Functions.CallSiblingNameViaOwner(primer, 1);

        var peer = new SiblingNamePeerOnlyImpl("peer-after-owner");
        var result = Functions.CallSiblingNameViaPeer(peer, 3);
        AssertEqual("name:3:peer-after-owner", result,
            "Peer dispatch resolves the Peer's own name even after the owner vtable was primed globally");
    }

    // MARK: - Async/sync effect-overload sibling divergence
    //
    // SyncRefineModifier refines AsyncRefineModifierBase; both declare refineModify
    // differing only in the `async` effect, so they project to DIFFERENT C# members
    // (`RefineModify(int)` vs `RefineModifyAsync(int, CancellationToken)`). When the C#
    // receiver sibling-fallback grouping omitted `async`, the two collapsed into one
    // group and the sync receiver fanned out into IAsyncRefineModifierBase emitting
    // `impl.RefineModify(...)` against an interface that only declares
    // `RefineModifyAsync` -> CS1061 at the compile gate. The fix carries `async` in BOTH
    // grouping keys: the async base satisfies the real reverse-async witness predicate
    // (S13 Pillar C), so its EveryProtocol witness KEEPS `async` and emits a genuine
    // `func refineModify(_:) async -> Int32`, while the sync refinement emits
    // `func refineModify(_:) -> Int32` — two DISTINCT effect overloads, no redeclaration.
    // Both paths now round-trip at runtime: the sync receiver dispatches `RefineModify`
    // under ISyncRefineModifier's own name, and the async base dispatches `RefineModifyAsync`
    // through the real reverse-async witness.

    /// <summary>
    /// Dispatch the SYNC requirement of a sync-protocol-refining-an-async-protocol
    /// through the sync existential. The receiver must call ISyncRefineModifier's own
    /// `RefineModify`, not fan out into the async base's `RefineModify` (which the async
    /// interface never declares — the CS1061 regression locus).
    /// </summary>
    public void TestAsyncSyncRefine_SyncDispatch()
    {
        var impl = new SyncRefineModifierImpl(100);
        var result = Functions.CallRefineModifySync(impl, 7);
        AssertEqual(107, result,
            "Sync-refine receiver dispatches RefineModify under its own name (async/sync effect-overloads stay in distinct sibling groups)");
    }

    /// <summary>
    /// Dispatch the async BASE requirement through the real reverse-async witness. The instance
    /// is a SyncRefineModifier impl, which conforms to the async base via refinement, so the
    /// async-base witness dispatches to its <c>RefineModifyAsync</c> — independently of the sync
    /// slot exercised above. The await genuinely suspends until C# resumes the boxed continuation.
    /// </summary>
    public async Task TestAsyncSyncRefine_AsyncBaseDispatch()
    {
        var impl = new SyncRefineModifierImpl(100);
        var result = await WithTimeout(
            Functions.CallRefineModifyViaAsyncBaseAsync(impl, 7),
            DefaultAsyncTimeout);
        AssertEqual(1107, result,
            "Async-base requirement dispatches to RefineModifyAsync via the real reverse-async witness (distinct effect overload from the sync slot)");
        TestLogger.Info($"AsyncSyncRefine.AsyncBase = {result}");
    }

    /// <summary>
    /// Drive BOTH effect overloads on the SAME instance: the sync slot via the sync existential
    /// and the async base via the real reverse-async witness. Proves the refinement's two
    /// distinct effect-overload witnesses dispatch to their respective C# members.
    /// </summary>
    public async Task TestAsyncSyncRefine_BothEffectsOnOneInstance()
    {
        var impl = new SyncRefineModifierImpl(100);
        var sync = Functions.CallRefineModifySync(impl, 7);
        var async = await WithTimeout(
            Functions.CallRefineModifyViaAsyncBaseAsync(impl, 7),
            DefaultAsyncTimeout);
        AssertEqual(107, sync, "Sync slot dispatches to RefineModify on the dual-effect instance");
        AssertEqual(1107, async, "Async base dispatches to RefineModifyAsync on the dual-effect instance");
        TestLogger.Info($"AsyncSyncRefine.BothEffects sync={sync} async={async}");
    }

    // MARK: - Unrelated (non-refining) async/sync same-signature group
    //
    // MixedFanAsyncOwner (async) and MixedFanSyncPeer (sync) declare the same
    // mixedFanModify(_:) -> Int32 with NO refinement between them. The async requirement
    // satisfies the real reverse-async witness predicate (S13 Pillar C), so its EveryProtocol
    // witness KEEPS `async` and emits a genuine `func mixedFanModify(_:) async -> Int32`, while
    // the sync peer emits `func mixedFanModify(_:) -> Int32`. Because the owner/peer grouping
    // key carries `async` for the real-async witness and omits it for the sync peer, the two
    // protocols land in DISTINCT owner groups — each emitting its OWN effect-overload witness
    // body (no longer one shared group). The C# sibling-fallback grouping likewise carries
    // `async`, keeping the requirements DISTINCT C# members (MixedFanModifyAsync vs
    // MixedFanModify) with no CS1061 cross-fallback. Complements the refinement shape above
    // (SyncRefineModifier: AsyncRefineModifierBase) with the INDEPENDENT-protocols case: both
    // effect-overload witnesses coexist on EveryProtocol and each round-trips at runtime.

    /// <summary>
    /// Dispatch the SYNC peer requirement of an UNRELATED async/sync same-signature group
    /// through the sync existential. The sync peer emits its own `func mixedFanModify(_:) ->
    /// Int32` witness, reaching this C# impl under MixedFanSyncPeer's own MixedFanModify member,
    /// independently of the async owner's distinct witness.
    /// </summary>
    public void TestMixedFanUnrelated_SyncDispatch()
    {
        var impl = new MixedFanSyncPeerImpl(50);
        var result = Functions.CallMixedFanViaSyncPeer(impl, 7);
        AssertEqual(57, result,
            "Unrelated async/sync group: sync-peer dispatch round-trips through the sync peer's own witness");
    }

    /// <summary>
    /// Dispatch the ASYNC owner requirement of the same UNRELATED group through the real
    /// reverse-async witness. The async owner emits its own `func mixedFanModify(_:) async ->
    /// Int32` distinct effect-overload witness, suspending until C# resumes the boxed
    /// continuation — proving it dispatches independently of the unrelated sync peer.
    /// </summary>
    public async Task TestMixedFanUnrelated_AsyncOwnerDispatch()
    {
        var impl = new MixedFanAsyncOwnerImpl(50);
        var result = await WithTimeout(
            Functions.CallMixedFanViaAsyncOwnerAsync(impl, 7),
            DefaultAsyncTimeout);
        AssertEqual(57, result,
            "Unrelated async/sync group: async-owner dispatch round-trips through the real reverse-async witness (distinct from the sync peer)");
        TestLogger.Info($"MixedFanUnrelated.AsyncOwner = {result}");
    }

    /// <summary>
    /// Deferred completion of the async owner: a genuine yield before producing the value still
    /// resumes the boxed continuation cleanly.
    /// </summary>
    public async Task TestMixedFanUnrelated_AsyncOwnerDeferred()
    {
        var impl = new MixedFanAsyncOwnerImpl(50, defer: true);
        var result = await WithTimeout(
            Functions.CallMixedFanViaAsyncOwnerAsync(impl, 7),
            DefaultAsyncTimeout);
        AssertEqual(57, result,
            "Unrelated async/sync group: async-owner resumes after an awaited yield in the C# impl");
        TestLogger.Info($"MixedFanUnrelated.AsyncOwnerDeferred = {result}");
    }
}

internal class SiblingMethodOwnerOnlyImpl : ISiblingMethodOwner
{
    private readonly string _tag;
    public SiblingMethodOwnerOnlyImpl(string tag) { _tag = tag; }
    public string GetSiblingMethodValue() => _tag;
    public string SiblingMethodEcho(int n) => $"echo:{n}:{_tag}";
}

internal class SiblingMethodPeerOnlyImpl : ISiblingMethodPeer
{
    private readonly string _tag;
    public SiblingMethodPeerOnlyImpl(string tag) { _tag = tag; }
    public string GetSiblingMethodValue() => _tag;
    public string SiblingMethodEcho(int n) => $"echo:{n}:{_tag}";
}

internal class SiblingMethodFullImpl : ISiblingMethodOwner, ISiblingMethodPeer
{
    private readonly string _tag;
    public SiblingMethodFullImpl(string tag) { _tag = tag; }
    public string GetSiblingMethodValue() => _tag;
    public string SiblingMethodEcho(int n) => $"echo:{n}:{_tag}";
}

// Peer (non-owner) of the name-divergence group: keeps the plain CollidingTag name.
internal class SiblingNamePeerOnlyImpl : ISiblingNamePeer
{
    private readonly string _tag;
    public SiblingNamePeerOnlyImpl(string tag) { _tag = tag; }
    public string CollidingTag(int n) => $"name:{n}:{_tag}";
}

// Owner of the name-divergence group: the `CollidingTag` property forces the method
// to be renamed `CollidingTagMethod`. The property is just there to trigger the rename.
internal class SiblingNameOwnerOnlyImpl : ISiblingNameOwner
{
    private readonly string _tag;
    public SiblingNameOwnerOnlyImpl(string tag) { _tag = tag; }
    public string CollidingTag => $"prop:{_tag}";
    public string CollidingTagMethod(int n) => $"name:{n}:{_tag}";
}

// Sync refinement of an async protocol. ISyncRefineModifier inherits
// IAsyncRefineModifierBase, so the impl must satisfy BOTH the sync `RefineModify` and the
// inherited async `RefineModifyAsync` — only the sync path is exercised at runtime.
internal class SyncRefineModifierImpl : ISyncRefineModifier
{
    private readonly int _bias;
    public SyncRefineModifierImpl(int bias) { _bias = bias; }
    public int RefineModify(int n) => n + _bias;
    public System.Threading.Tasks.Task<int> RefineModifyAsync(int n, System.Threading.CancellationToken cancellationToken = default)
        => System.Threading.Tasks.Task.FromResult(n + _bias + 1000);
}

// Sync peer of the UNRELATED async/sync group. Implements ONLY the sync interface (no
// refinement, so it does not inherit the async owner's interface). Its proxy populates the
// sync peer's per-protocol vtable, dispatched by the sync peer's own witness.
internal class MixedFanSyncPeerImpl : IMixedFanSyncPeer
{
    private readonly int _bias;
    public MixedFanSyncPeerImpl(int bias) { _bias = bias; }
    public int MixedFanModify(int n) => n + _bias;
}

// Async owner of the UNRELATED async/sync group. Implements ONLY the async interface. Its real
// reverse-async witness suspends and hands the boxed continuation back to C#, distinct from the
// unrelated sync peer's witness. When defer is set the impl yields before returning, exercising
// a genuine suspend/resume rather than an immediately-completed Task.
internal class MixedFanAsyncOwnerImpl : IMixedFanAsyncOwner
{
    private readonly int _bias;
    private readonly bool _defer;
    public MixedFanAsyncOwnerImpl(int bias, bool defer = false) { _bias = bias; _defer = defer; }

    public System.Threading.Tasks.Task<int> MixedFanModifyAsync(int n, System.Threading.CancellationToken cancellationToken = default)
        => _defer
            ? DeferredAsync(n)
            : System.Threading.Tasks.Task.FromResult(n + _bias);

    private async System.Threading.Tasks.Task<int> DeferredAsync(int n)
    {
        await System.Threading.Tasks.Task.Yield();
        return n + _bias;
    }
}
