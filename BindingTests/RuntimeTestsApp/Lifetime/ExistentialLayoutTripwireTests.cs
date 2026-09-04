// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// End-to-end coverage for the existential-layout tripwire.
///
/// <para>
/// A generated proxy picks its existential-container shape at emission time from parsed ABI facts:
/// two words (<c>[classRef][witnessTable]</c>) for a class-bound protocol, five
/// (<c>[p0][p1][p2][metadata][witnessTable]</c>) for an opaque one. When those facts are wrong the
/// proxy writes the witness table into a word Swift does not read, Swift dispatches through a null
/// witness table, and the process traps inside the framework with no managed frame. The Swift
/// wrapper exports <c>MemoryLayout&lt;any P&gt;.size</c>, which settles the shape unambiguously, and
/// each proxy compares it against its own choice the first time it resolves its witness table.
/// </para>
///
/// <para>
/// These are positive controls: resolving the handle is what ARMS the check, so a proxy whose
/// emitted layout disagreed with Swift would throw here instead of returning a handle. One proxy of
/// each shape is exercised — <see cref="ReverseInvariantAlphaProxy"/> is opaque,
/// <see cref="ReverseStoredDelegateProxy"/> is class-bound (<c>: AnyObject</c>) — so a regression
/// that flipped either arm's expected size reds this class rather than passing vacuously. The
/// round-trip tests then confirm that a proxy whose check passed still dispatches, which is the
/// property the check is a proxy for.
/// </para>
/// </summary>
public class ExistentialLayoutTripwireTests : TestBase
{
    public ExistentialLayoutTripwireTests(TestResults results) : base(results) { }

    /// <summary>
    /// The opaque arm: the proxy stamps its module's EveryProtocol metadata and writes the witness
    /// table into the container's last word. Resolving the handle runs the size check against the
    /// five-word expectation.
    /// </summary>
    public void TestOpaqueProxyWitnessTableResolvesUnderLayoutCheck()
    {
        var handle = ReverseInvariantAlphaProxy.ProtocolWitnessTableHandle;

        AssertTrue(handle != IntPtr.Zero,
            "opaque proxy resolves a non-null witness table with its layout check satisfied");
    }

    /// <summary>
    /// The class-bound arm: the proxy writes the witness table into word 1 of a two-word container.
    /// Resolving the handle runs the size check against the two-word expectation.
    /// </summary>
    public void TestClassBoundProxyWitnessTableResolvesUnderLayoutCheck()
    {
        var handle = ReverseStoredDelegateProxy.ProtocolWitnessTableHandle;

        AssertTrue(handle != IntPtr.Zero,
            "class-bound proxy resolves a non-null witness table with its layout check satisfied");
    }

    /// <summary>
    /// The check runs once and caches nothing it rejected: a second read returns the same handle,
    /// and does not re-throw or re-resolve.
    /// </summary>
    public void TestWitnessTableHandleIsStableAcrossReads()
    {
        var first = ReverseInvariantAlphaProxy.ProtocolWitnessTableHandle;
        var second = ReverseInvariantAlphaProxy.ProtocolWitnessTableHandle;

        AssertEqual(first, second, "witness table handle is cached after the first resolution");
    }

    /// <summary>
    /// The property the layout check stands in for: a container built on the opaque arm actually
    /// dispatches into Swift and back. A wrong-arm container would trap here rather than return the
    /// +100 sentinel.
    /// </summary>
    public void TestOpaqueContainerDispatchesAfterLayoutCheck()
    {
        var harness = new ReverseInvariantHarness();
        var impl = new LayoutProbeImpl();

        var result = harness.PingAlpha(impl, value: 7);

        AssertEqual(107, result, "opaque existential dispatches through the checked container");
    }

    /// <summary>
    /// The class-bound counterpart: the stored existential round-trips its +1000 sentinel through a
    /// two-word container whose witness table sits in word 1.
    /// </summary>
    public void TestClassBoundContainerDispatchesAfterLayoutCheck()
    {
        var harness = new ReverseInvariantHarness();
        harness.StoredDelegate = new LayoutProbeImpl();

        var result = harness.InvokeStored(value: 7);

        AssertEqual(1007, result, "class-bound existential dispatches through the checked container");
    }

    /// <summary>
    /// One C# object implementing both shapes, so the two arms are exercised against the same impl
    /// and a cross-arm confusion cannot hide behind two separate objects.
    /// </summary>
    private sealed class LayoutProbeImpl : IReverseInvariantAlpha, IReverseStoredDelegate
    {
        public int AlphaValue(int value) => value + 100;

        public int StoredValue(int value) => value + 1000;
    }
}
