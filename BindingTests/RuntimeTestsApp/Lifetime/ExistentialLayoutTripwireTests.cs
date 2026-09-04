// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
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
    /// The positive control the handle-resolution tests need in order to mean anything.
    ///
    /// <para>
    /// Resolving a witness table only proves that the size Swift REPORTED equalled the size the
    /// proxy EXPECTED. Two equal numbers say nothing on their own: a tripwire that stopped reading
    /// real layouts — an accessor that returned zero on both sides, or a proxy that had quietly
    /// converged on one shape for every protocol — would satisfy that equality and pass. So this
    /// test reads the two Swift-exported sizes directly and pins them to the concrete word counts
    /// the ABI mandates, and pins the two arms apart from each other.
    /// </para>
    /// </summary>
    public void TestExportedExistentialSizesAreTheRealWordCounts()
    {
        int opaque = (int)ExistentialSizeAccessors.OpaqueSize();
        int classBound = (int)ExistentialSizeAccessors.ClassBoundSize();

        AssertEqual(5 * IntPtr.Size, opaque,
            "an opaque existential is five words: [p0][p1][p2][metadata][witnessTable]");
        AssertEqual(2 * IntPtr.Size, classBound,
            "a class-bound existential is two words: [classRef][witnessTable]");

        AssertEqual(ExistentialLayout.OpaqueSize, opaque,
            "the runtime's opaque constant agrees with what Swift reports for an opaque protocol");
        AssertEqual(ExistentialLayout.ClassBoundSize, classBound,
            "the runtime's class-bound constant agrees with what Swift reports for an AnyObject protocol");

        AssertTrue(opaque != classBound,
            "the two arms report DIFFERENT sizes — an accessor stuck on one constant would make "
            + "every proxy's layout check vacuous");
    }

    /// <summary>
    /// The comparator itself must be able to say no. If <c>Verify</c> accepted anything, every
    /// proxy's check would pass no matter what Swift reported, and the tests above would be
    /// asserting the absence of a mechanism rather than its success.
    /// </summary>
    public void TestLayoutVerifyRejectsTheWrongShape()
    {
        AssertThrows<InvalidOperationException>(
            () => ExistentialLayout.Verify("probe", ExistentialLayout.OpaqueSize, ExistentialLayout.ClassBoundSize),
            "a class-bound-sized container reported for an opaque expectation is rejected");

        AssertThrows<InvalidOperationException>(
            () => ExistentialLayout.Verify("probe", ExistentialLayout.ClassBoundSize, ExistentialLayout.OpaqueSize),
            "an opaque-sized container reported for a class-bound expectation is rejected");

        AssertThrows<InvalidOperationException>(
            () => ExistentialLayout.Verify("probe", ExistentialLayout.OpaqueSize, 0),
            "a zero size — a missing or inert accessor — is rejected rather than treated as a match");

        AssertThrows<InvalidOperationException>(
            () => ExistentialLayout.Verify("probe", ExistentialLayout.OpaqueSize, ExistentialLayout.ObjCSize),
            "the one-word ObjC narrowing is NOT accepted for an opaque expectation");

        // The single deliberate tolerance: a class-bound expectation accepts a pure-@objc
        // protocol's one-word container, because those really are one word wide.
        ExistentialLayout.Verify("probe", ExistentialLayout.ClassBoundSize, ExistentialLayout.ObjCSize);
        ExistentialLayout.Verify("probe", ExistentialLayout.OpaqueSize, ExistentialLayout.OpaqueSize);
    }

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

/// <summary>
/// The same <c>MemoryLayout&lt;any P&gt;.size</c> accessors the generated proxies consult, reached
/// directly so a test can read the reported sizes instead of only observing that a proxy accepted
/// them. Entry points are the wrapper's, so this reads the identical source of truth rather than a
/// second opinion.
///
/// <para>
/// The <c>CallConvs</c> argument is fully qualified deliberately. The test library binds Swift types
/// named <c>Type</c> and <c>CallConvCdecl</c> into <c>SwiftBindingsTestLib</c>, which this assembly
/// imports, so the unqualified forms are ambiguous (CS0104). An ambiguous argument leaves an error
/// type in the attribute, and <c>LibraryImportGenerator</c> then throws while reading it — which
/// takes down EVERY <c>[LibraryImport]</c> in the assembly (CS8785 followed by tens of thousands of
/// CS8795s in the generated bindings), not just this one. Generated code qualifies for the same
/// reason; hand-written P/Invokes here must too.
/// </para>
/// </summary>
internal static partial class ExistentialSizeAccessors
{
    /// <summary><c>ReverseInvariantAlpha</c> — an opaque (non-<c>AnyObject</c>) protocol.</summary>
    [LibraryImport("SwiftBindings", EntryPoint = "Get_EveryProtocol_ReverseInvariantAlpha_ExistentialSize")]
    [UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial nint OpaqueSize();

    /// <summary><c>ReverseStoredDelegate</c> — a class-bound (<c>: AnyObject</c>) protocol.</summary>
    [LibraryImport("SwiftBindings", EntryPoint = "Get_EveryProtocol_ReverseStoredDelegate_ExistentialSize")]
    [UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial nint ClassBoundSize();
}
