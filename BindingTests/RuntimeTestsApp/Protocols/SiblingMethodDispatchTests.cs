// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression tests for sibling-protocol METHOD dispatch (audit item 1, Bug #2) —
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

    // MARK: - Sibling-method NAME divergence (Codex r1 Medium)
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
