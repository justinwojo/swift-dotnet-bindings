// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Threading;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using Swift.Runtime;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression tests for sibling-protocol REAL-ASYNC method dispatch — the async
/// analog of <see cref="SiblingMethodDispatchTests"/> for the S13 Pillar C
/// continuation-handoff witness.
///
/// Shape: two class-bound protocols (AsyncSiblingOwner, AsyncSiblingPeer) declare
/// the SAME real-async-eligible signature, so they form ONE owner group; the
/// lexicographically-smaller protocol (Owner &lt; Peer) owns the real-async witness
/// body and the Peer gets an empty stitched extension.
///
/// Pre-fix bug: the real-async owner witness force-unwrapped its OWN widened vtable
/// slot, and the owner receiver resolved only the owner interface. A C# impl
/// conforming to ONLY the Peer left the owner slot nil → dispatch through the Peer
/// existential SIGSEGV'd on the nil function pointer; and after any owner proxy
/// primed the owner's process-wide vtable, the owner branch fired but its receiver
/// could not locate the Peer's per-instance proxy and FailFast'd a live impl.
///
/// Fix: the real-async owner witness fans out across both sibling widened vtable
/// slots, and the owner's real-async receiver resolves across the primary then each
/// recorded sibling interface — the EmitMethodFanOutBody + ComputeSiblingMethodFallbacks
/// path the sync witness already used, applied to the continuation-handoff slot.
/// </summary>
public class AsyncSiblingMethodDispatchTests : TestBase
{
    public AsyncSiblingMethodDispatchTests(TestResults results) : base(results) { }

    /// <summary>
    /// The pre-fix crash: a C# class implementing only the PEER (non-owner). Swift
    /// awaits the shared real-async method through the Peer existential; before the fix
    /// this routes to the owner body which force-unwraps a nil owner vtable pointer.
    /// </summary>
    public async Task TestAsyncSibling_PeerExistential()
    {
        var impl = new AsyncSiblingPeerOnlyImpl(1000);
        var result = await WithTimeout(
            Functions.CallAsyncSiblingViaPeerAsync(impl, 7),
            DefaultAsyncTimeout);
        AssertEqual(1007, result,
            "Real-async shared method via Peer (non-owner) existential resumes with the C# impl value");
        TestLogger.Info($"AsyncSibling.Peer = {result}");
    }

    /// <summary>
    /// Control: a C# class implementing only the OWNER. Owner-body dispatch through the
    /// owner's own widened slot. Must work before and after the fix.
    /// </summary>
    public async Task TestAsyncSibling_OwnerExistential()
    {
        var impl = new AsyncSiblingOwnerOnlyImpl(2000);
        var result = await WithTimeout(
            Functions.CallAsyncSiblingViaOwnerAsync(impl, 7),
            DefaultAsyncTimeout);
        AssertEqual(2007, result,
            "Real-async shared method via Owner existential resumes with the C# impl value");
        TestLogger.Info($"AsyncSibling.Owner = {result}");
    }

    /// <summary>
    /// Reverse-order regression (Case B): prime the owner vtable globally (it is a
    /// process-wide var), THEN dispatch through the Peer. The owner branch of the fan-out
    /// fires first because its function pointer is now non-nil, so this only passes if the
    /// owner's real-async receiver falls back to the Peer interface and resolves this
    /// instance's proxy. Without the receiver-side fallback it FailFasts the live impl.
    /// </summary>
    public async Task TestAsyncSibling_PeerAfterOwnerPrimed()
    {
        var primer = new AsyncSiblingOwnerOnlyImpl(0);
        _ = await WithTimeout(
            Functions.CallAsyncSiblingViaOwnerAsync(primer, 1),
            DefaultAsyncTimeout);

        var peer = new AsyncSiblingPeerOnlyImpl(3000);
        var result = await WithTimeout(
            Functions.CallAsyncSiblingViaPeerAsync(peer, 7),
            DefaultAsyncTimeout);
        AssertEqual(3007, result,
            "Real-async Peer dispatch must resume the live Peer impl even after the owner vtable was primed globally");
        TestLogger.Info($"AsyncSibling.PeerAfterOwnerPrimed = {result}");
    }

    /// <summary>
    /// A single C# class implementing BOTH sibling interfaces. Both widened vtables get
    /// populated for the same handle; dispatch through either existential must reach the
    /// one impl.
    /// </summary>
    public async Task TestAsyncSibling_BothOnMultiImpl()
    {
        var impl = new AsyncSiblingFullImpl(4000);
        var viaOwner = await WithTimeout(
            Functions.CallAsyncSiblingViaOwnerAsync(impl, 7),
            DefaultAsyncTimeout);
        var viaPeer = await WithTimeout(
            Functions.CallAsyncSiblingViaPeerAsync(impl, 7),
            DefaultAsyncTimeout);
        AssertEqual(4007, viaOwner, "Multi-sibling real-async impl: resume via Owner existential");
        AssertEqual(4007, viaPeer, "Multi-sibling real-async impl: resume via Peer existential");
    }

    /// <summary>
    /// Deferred completion through the Peer existential: a genuine yield before producing
    /// the value still resumes the boxed continuation cleanly via the fanned-out peer slot.
    /// </summary>
    public async Task TestAsyncSibling_PeerDeferred()
    {
        var impl = new AsyncSiblingPeerOnlyImpl(5000, defer: true);
        var result = await WithTimeout(
            Functions.CallAsyncSiblingViaPeerAsync(impl, 7),
            DefaultAsyncTimeout);
        AssertEqual(5007, result,
            "Real-async Peer dispatch resumes after an awaited yield in the C# impl");
        TestLogger.Info($"AsyncSibling.PeerDeferred = {result}");
    }

    /// <summary>
    /// Throwing real-async sibling through the PEER existential — proves the fan-out +
    /// receiver fallback are effect-agnostic (throwing box wraps CheckedContinuation&lt;T,
    /// Error&gt;). Happy path: the impl returns a value, exercising the success resume on
    /// the fanned-out peer slot.
    /// </summary>
    public async Task TestAsyncSiblingThrowing_PeerExistential()
    {
        var impl = new AsyncSiblingThrowingPeerOnlyImpl(6000);
        var result = await WithTimeout(
            Functions.CallAsyncSiblingThrowingViaPeerAsync(impl, 7),
            DefaultAsyncTimeout);
        AssertEqual(6007, result,
            "Throwing real-async shared method via Peer existential resumes with the C# impl value");
        TestLogger.Info($"AsyncSiblingThrowing.Peer = {result}");
    }

    /// <summary>
    /// Throwing reverse-order regression (Case B): prime the throwing owner vtable, then
    /// dispatch the throwing method through the Peer. Same receiver-fallback requirement as
    /// the non-throwing Case B, on the throwing widened slot.
    /// </summary>
    public async Task TestAsyncSiblingThrowing_PeerAfterOwnerPrimed()
    {
        var primer = new AsyncSiblingThrowingOwnerOnlyImpl(0);
        _ = await WithTimeout(
            Functions.CallAsyncSiblingThrowingViaOwnerAsync(primer, 1),
            DefaultAsyncTimeout);

        var peer = new AsyncSiblingThrowingPeerOnlyImpl(7000);
        var result = await WithTimeout(
            Functions.CallAsyncSiblingThrowingViaPeerAsync(peer, 7),
            DefaultAsyncTimeout);
        AssertEqual(7007, result,
            "Throwing real-async Peer dispatch must resume the live Peer impl even after the owner vtable was primed globally");
        TestLogger.Info($"AsyncSiblingThrowing.PeerAfterOwnerPrimed = {result}");
    }

    /// <summary>
    /// Throwing FAULT through the PEER existential (Grok L-new-1): a throwing C# peer impl that
    /// faults must resume the boxed CheckedContinuation&lt;Int32, Error&gt; WITH the error through the
    /// fanned-out peer slot — not FailFast and not hang — surfacing back through the forward async
    /// bridge as a <see cref="SwiftException"/>. The solo throwing-error channel is covered by
    /// <c>AsyncReverseWitnessTests.TestReverseAsyncWitnessPropagatesError</c>; the throwing-sibling
    /// HAPPY path is covered above. This is the one uncovered arm: the error resume specifically when
    /// the impl was located via the receiver's sibling-interface fallback (the `__asyncFunc`
    /// peer-resolution path) rather than a solo single-resolve.
    /// </summary>
    public async Task TestAsyncSiblingThrowing_PeerExistentialFaults()
    {
        var impl = new AsyncSiblingThrowingPeerFaultImpl("sibling-boom");
        try
        {
            await WithTimeout(
                Functions.CallAsyncSiblingThrowingViaPeerAsync(impl, 7),
                DefaultAsyncTimeout);
            throw new AssertionException("Expected SwiftException but no exception was thrown");
        }
        catch (SwiftException ex)
        {
            if (!ex.Message.Contains("sibling-boom"))
                throw new AssertionException($"Expected 'sibling-boom' in message, got: {ex.Message}");
            TestLogger.Info($"AsyncSiblingThrowing.PeerFault threw SwiftException: {ex.Message}");
        }
    }
}

// Peer-only (non-owner) impl of the non-throwing pair. Its proxy populates ONLY the
// AsyncSiblingPeer widened vtable. When defer is set the impl yields before returning,
// exercising a genuine suspend/resume rather than an immediately-completed Task.
internal class AsyncSiblingPeerOnlyImpl : IAsyncSiblingPeer
{
    private readonly int _bias;
    private readonly bool _defer;
    public AsyncSiblingPeerOnlyImpl(int bias, bool defer = false) { _bias = bias; _defer = defer; }

    public Task<int> SiblingModifyAsync(int n, CancellationToken cancellationToken = default)
        => _defer ? DeferredAsync(n) : Task.FromResult(n + _bias);

    private async Task<int> DeferredAsync(int n)
    {
        await Task.Yield();
        return n + _bias;
    }
}

// Owner-only impl of the non-throwing pair. Its proxy populates the AsyncSiblingOwner
// widened vtable — used both as the owner control and as the global-vtable primer.
internal class AsyncSiblingOwnerOnlyImpl : IAsyncSiblingOwner
{
    private readonly int _bias;
    public AsyncSiblingOwnerOnlyImpl(int bias) { _bias = bias; }

    public Task<int> SiblingModifyAsync(int n, CancellationToken cancellationToken = default)
        => Task.FromResult(n + _bias);
}

// Impl conforming to BOTH sibling interfaces — both widened vtables populated for one handle.
internal class AsyncSiblingFullImpl : IAsyncSiblingOwner, IAsyncSiblingPeer
{
    private readonly int _bias;
    public AsyncSiblingFullImpl(int bias) { _bias = bias; }

    public Task<int> SiblingModifyAsync(int n, CancellationToken cancellationToken = default)
        => Task.FromResult(n + _bias);
}

// Peer-only impl of the throwing pair.
internal class AsyncSiblingThrowingPeerOnlyImpl : IAsyncSiblingThrowingPeer
{
    private readonly int _bias;
    public AsyncSiblingThrowingPeerOnlyImpl(int bias) { _bias = bias; }

    public Task<int> SiblingThrowingModifyAsync(int n, CancellationToken cancellationToken = default)
        => Task.FromResult(n + _bias);
}

// Owner-only impl of the throwing pair — control + primer for the throwing Case B.
internal class AsyncSiblingThrowingOwnerOnlyImpl : IAsyncSiblingThrowingOwner
{
    private readonly int _bias;
    public AsyncSiblingThrowingOwnerOnlyImpl(int bias) { _bias = bias; }

    public Task<int> SiblingThrowingModifyAsync(int n, CancellationToken cancellationToken = default)
        => Task.FromResult(n + _bias);
}

// Peer-only impl of the throwing pair that FAULTS — exercises the error resume through the
// fanned-out peer slot (L-new-1), the throwing-sibling analog of AsyncReverseComputeImpl's error
// arm. Yields first so the fault arrives on a genuinely suspended continuation.
internal class AsyncSiblingThrowingPeerFaultImpl : IAsyncSiblingThrowingPeer
{
    private readonly string _message;
    public AsyncSiblingThrowingPeerFaultImpl(string message) { _message = message; }

    public async Task<int> SiblingThrowingModifyAsync(int n, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new System.InvalidOperationException(_message);
    }
}
