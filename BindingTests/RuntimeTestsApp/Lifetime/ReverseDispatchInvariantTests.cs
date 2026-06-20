// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// End-to-end invariants for the reverse-dispatch lifetime model and the
/// per-module EveryProtocol metadata fix.
///
/// <list type="bullet">
/// <item><b>R1 cross-talk</b>: one C# object implementing two unrelated opaque
/// reverse-dispatch protocols — each EveryProtocol handle must resolve ONLY its
/// own protocol's view (<c>ResolveImpl&lt;T&gt;</c> is <c>impl as T</c>).</item>
/// <item><b>R4 value round-trip</b>: a class-bound stored existential whose
/// C#-impl proxy is collected while Swift still holds the existential — the
/// VALUE must round-trip (the impl is rooted by Swift liveness) even though the
/// Swift-side carrier identity is no longer stable.</item>
/// <item><b>Cross-module metadata</b>: a single C# object implementing one opaque protocol
/// in the MAIN module and one in the DEPENDENCY module — each module's
/// auto-wrapped proxy must stamp its existential with its OWN module's
/// EveryProtocol metadata, not a process-global latch.</item>
/// </list>
/// </summary>
public class ReverseDispatchInvariantTests : TestBase
{
    public ReverseDispatchInvariantTests(TestResults results) : base(results) { }

    private const int GcCycles = 6;

    /// <summary>
    /// Force a GC on a worker thread (the main thread blocks on Join with a
    /// minimal live-local footprint) so Mono's conservative stack scan does not
    /// pin the dropped impl. Mirrors <see cref="ProxyLifetimeTests"/>'s helper.
    /// </summary>
    private static void ForceGc()
    {
        var worker = new System.Threading.Thread(ForceGcWorker) { IsBackground = true };
        worker.Start();
        worker.Join();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceGcWorker()
    {
        var scratch = new object[256];
        for (int i = 0; i < scratch.Length; i++)
            scratch[i] = new object();
        GC.KeepAlive(scratch);

        for (int i = 0; i < GcCycles; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    // ---- R1: cross-talk invariant -----------------------------------------

    /// <summary>
    /// A single C# object implements two UNRELATED opaque reverse-dispatch
    /// protocols. Each is wrapped into its own EveryProtocol (one handle per
    /// protocol); the receiver resolves the impl via
    /// <c>ResolveImpl&lt;ITheProtocol&gt;(handle)</c>. The sentinels differ by
    /// protocol (+100 vs +200), so a resolver that returned the wrong view (or
    /// trapped on a cross-protocol cast) surfaces as a wrong value here.
    /// </summary>
    public void TestCrossTalkResolvesOwnProtocolView()
    {
        var harness = new ReverseInvariantHarness();
        var impl = new DualReverseImpl();

        var alpha = harness.PingAlpha(impl, value: 10);
        var beta = harness.PingBeta(impl, value: 10);

        AssertEqual(110, alpha, "alpha handle resolves the alpha view (10 + 100)");
        AssertEqual(210, beta, "beta handle resolves the beta view (10 + 200)");
        // Explicit no-cross-talk checks: neither view leaked into the other.
        AssertTrue(alpha != 210, "alpha view did NOT resolve as beta");
        AssertTrue(beta != 110, "beta view did NOT resolve as alpha");

        TestLogger.Info($"[ReverseInvariant] cross-talk: alpha={alpha}, beta={beta}");
        GC.KeepAlive(impl);
        GC.KeepAlive(harness);
    }

    // ---- R4: stored-existential value round-trip after proxy death ---------

    /// <summary>
    /// Assign a C# impl to a class-bound stored existential, drop every managed
    /// reference to the impl/proxy, force GC, and prove the value still
    /// round-trips. Under B2 the impl is rooted by Swift liveness (Swift's
    /// <c>storedDelegate</c> retain keeps the EveryProtocol alive, so the strong
    /// impl GCHandle is not freed), so Swift can still dispatch into it.
    /// Re-vending the existential mints a FRESH C# carrier — identity is no
    /// longer stable under B2 — but its value must still round-trip.
    /// </summary>
    public void TestStoredExistentialValueRoundTripsAfterProxyDeath()
    {
        var harness = new ReverseInvariantHarness();
        StoreAndDropProxy(harness);

        // Collect the now-unreferenced C#-impl proxy. The impl itself must NOT
        // be collected: it is rooted by the tracker's strong GCHandle for as
        // long as Swift's storedDelegate holds the existential.
        ForceGc();

        // Swift-side dispatch into the stored existential — the pure B2 path
        // (impl rooted by Swift liveness, resolved via ResolveImpl).
        AssertEqual(1005, harness.InvokeStored(value: 5),
            "stored existential value round-trips after the C#-impl proxy was collected");

        // Re-vend: reading the existential back wraps it into a fresh C# carrier.
        // Identity is intentionally NOT asserted (B2 documented behaviour change);
        // the VALUE must round-trip through the re-wrapped carrier.
        var revended = harness.StoredDelegate;
        AssertNotNull(revended, "stored existential re-vends a non-null carrier");
        AssertEqual(1007, revended!.StoredValue(7),
            "re-vended carrier round-trips the value (value, not identity)");

        TestLogger.Info("[ReverseInvariant] R4 stored existential value round-trip held after proxy GC");
        GC.KeepAlive(harness);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StoreAndDropProxy(ReverseInvariantHarness harness)
    {
        var impl = new StoredReverseDelegateImpl();
        harness.StoredDelegate = impl;
        GC.KeepAlive(impl);
        // impl and its auto-wrapped proxy fall out of scope on return. Only
        // Swift's stored existential keeps the impl reachable (tracker GCHandle).
    }

    // ---- Finding 33: per-module metadata, two modules in one process ------

    /// <summary>
    /// One C# object implements an opaque protocol from the MAIN module
    /// (<c>ReverseInvariantAlpha</c>) and one from the DEPENDENCY module
    /// (<c>DepReverseValue</c>). Each is auto-wrapped into its own module's
    /// EveryProtocol, whose opaque existential carries the metadata word from
    /// that module's <c>NativeMethods.GetEveryProtocolMetadata()</c>. The
    /// dependency call STORES the existential (a value-witness copy keyed on the
    /// metadata word) before dispatching, so a wrong-module metadata word would
    /// corrupt the copy rather than merely mislabel a type. Dispatching through
    /// both modules in the same process, in both orders, proves neither poisoned
    /// the other (the pre-Finding-33 process-global latch failure mode).
    /// </summary>
    public void TestTwoModuleOpaqueMetadataIsPerModule()
    {
        var mainHarness = new ReverseInvariantHarness();
        var depHarness = new SwiftBindingsTestLibDependency.DepReverseValueHarness();
        var impl = new DualModuleReverseImpl();

        // Main-module opaque reverse dispatch (carries main's metadata).
        AssertEqual(115, mainHarness.PingAlpha(impl, value: 15),
            "main-module opaque reverse dispatch (15 + 100)");

        // Dependency-module opaque reverse dispatch with a stored value-witness
        // copy through the dependency module's own metadata (15 + 3000).
        AssertEqual(3015, depHarness.RoundTripStored(impl, value: 15),
            "dep-module opaque reverse dispatch + stored copy through per-module metadata");

        // Re-dispatch the main module AFTER the dependency module to prove the
        // dependency module's metadata did not overwrite a shared latch.
        AssertEqual(120, mainHarness.PingAlpha(impl, value: 20),
            "main-module metadata intact after dependency-module dispatch");
        AssertEqual(3020, depHarness.PingDepValue(impl, value: 20),
            "dep-module metadata intact after main-module dispatch");

        TestLogger.Info("[ReverseInvariant] Finding 33: main + dependency opaque metadata stayed per-module");
        GC.KeepAlive(impl);
        GC.KeepAlive(mainHarness);
        GC.KeepAlive(depHarness);
    }
}

/// <summary>
/// R1 fixture: one managed object implementing two unrelated opaque protocols.
/// Distinct per-protocol offsets make a cross-resolved call observable.
/// </summary>
internal sealed class DualReverseImpl : IReverseInvariantAlpha, IReverseInvariantBeta
{
    public int AlphaValue(int value) => value + 100;
    public int BetaValue(int value) => value + 200;
}

/// <summary>R4 fixture: class-bound stored existential conformer.</summary>
internal sealed class StoredReverseDelegateImpl : IReverseStoredDelegate
{
    public int StoredValue(int value) => value + 1000;
}

/// <summary>
/// Finding-33 fixture: one managed object conforming to a MAIN-module opaque
/// protocol and a DEPENDENCY-module opaque protocol simultaneously.
/// </summary>
internal sealed class DualModuleReverseImpl : IReverseInvariantAlpha, SwiftBindingsTestLibDependency.IDepReverseValue
{
    public int AlphaValue(int value) => value + 100;
    public int DepValue(int value) => value + 3000;
}
