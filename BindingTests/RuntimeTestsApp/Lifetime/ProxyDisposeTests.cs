// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Tests for protocol proxy Dispose() lifecycle: proper cleanup of
/// EveryProtocol + SwiftObjectRegistry, double-dispose safety,
/// and ObjectDisposedException guards on post-dispose access.
///
/// NOTE: Tests using the C# impl constructor (new HasValueProxy(impl))
/// require the SwiftBindings wrapper library at runtime for witness table
/// P/Invoke resolution. This is not yet wired up in BindingTests, so
/// those tests are Tier 3 (expected to fail until wrapper lib is bundled).
/// Container-based tests (ExistentialContainer1 path) work without the
/// wrapper lib and are Tier 2.
/// </summary>
public class ProxyDisposeTests : TestBase
{
    public ProxyDisposeTests(TestResults results) : base(results) { }

    // ---- 0.10.0 Layer C lifetime harness (populated by Bundles 1 and 3) ------
    //
    // Long-running / GC-pressure assertions for this class are gated by
    // `TestRunFlags.Lifetime` — set via `nuke binding-tests --lifetime`. Off by
    // default for inner-loop simulator runs; enabled unconditionally on the
    // integration serial gate. The 0.10.0 SafeHandle-refcount and
    // closure-lifetime bundles will populate methods here that loop a repro
    // pattern ~10k times with `GC.Collect()` between runs and assert
    // deterministic Swift alloc/dealloc counters return to baseline,
    // `CFGetRetainCount` returns to baseline for bridged ObjC objects, RSS
    // stays under a budget, and no finalizer-thread exceptions are logged.
    // Layer C — lifetime harness: exercises proxy dispose on the container path.

    #region Container-Path Tests (Tier 2 — no wrapper lib needed)

    public void TestProxyFromContainerDisposeIsSafe()
    {
        // Construct from a zeroed ExistentialContainer1 (Swift → C# direction).
        // _everyProtocol is null in this path — Dispose should be a safe no-op.
        var container = default(ExistentialContainer1);
        var proxy = new HasValueProxy(container);

        // Dispose should not crash (no EveryProtocol to clean up)
        proxy.Dispose();

        TestLogger.Info("Proxy from container dispose is safe (no-op)");
    }

    public void TestProxyFromContainerDoubleDisposeIsSafe()
    {
        var container = default(ExistentialContainer1);
        var proxy = new HasValueProxy(container);

        proxy.Dispose();
        // Second dispose should be safe (idempotent)
        proxy.Dispose();

        TestLogger.Info("Proxy from container double-dispose is safe");
    }

    public void TestProxyFromContainerPropertyAfterDisposeThrows()
    {
        var container = default(ExistentialContainer1);
        var proxy = new HasValueProxy(container);
        proxy.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = proxy.Value;
        }, "Property get after dispose should throw ObjectDisposedException");

        TestLogger.Info("Property access after dispose correctly throws ObjectDisposedException");
    }

    public void TestProxyFromContainerMethodAfterDisposeThrows()
    {
        var container = default(ExistentialContainer1);
        var proxy = new HasValueProxy(container);
        proxy.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = proxy.GetValue();
        }, "Method call after dispose should throw ObjectDisposedException");

        TestLogger.Info("Method call after dispose correctly throws ObjectDisposedException");
    }

    public void TestProxyFromContainerSetValueAfterDisposeThrows()
    {
        var container = default(ExistentialContainer1);
        var proxy = new HasValueProxy(container);
        proxy.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            proxy.SetValue(99);
        }, "SetValue after dispose should throw ObjectDisposedException");

        TestLogger.Info("SetValue after dispose correctly throws ObjectDisposedException");
    }

    #endregion

    #region C#-Impl-Path Tests (Tier 3 — requires wrapper lib for witness table)

    /// <summary>
    /// Simple IHasValue implementation for proxy construction.
    /// No Swift callback dependency — purely C#-side.
    /// </summary>
    private class SimpleHasValue : IHasValue
    {
        private int _value;

        public SimpleHasValue(int initialValue = 42)
        {
            _value = initialValue;
        }

        public int Value
        {
            get => _value;
            set => _value = value;
        }

        public int GetValue() => _value;
        public void SetValue(int newValue) => _value = newValue;
    }

    public void TestProxyDisposeReleasesNativeReference()
    {
        // Design B2: a C#-impl proxy registers WEAKLY (SwiftObjectRegistry.Register,
        // not RegisterStrong) and roots its impl by Swift-liveness through
        // ProxyLifetimeTracker, keyed on the EveryProtocol handle. The proxy owns the
        // construction +1 (R0); Dispose releases it exactly once, which drives Swift's
        // last retain to zero -> EveryProtocol.deinit -> OnEveryProtocolDeinit, freeing
        // the impl root and unregistering the (weak) registry entry. So the observable
        // is NOT a StrongCount delta (the superseded RegisterStrong model) — it is:
        //   1. construction leaves StrongCount unchanged (weak registration), and
        //   2. while alive the proxy is discoverable and its impl is rooted by handle, and
        //   3. Dispose releases R0 -> deinit -> impl root freed + entry unregistered.
        //
        // Drain pending finalizers so prior tests' torn-down proxies don't race the
        // registry/tracker samples below.
        ForceGC();
        var initialStrong = SwiftObjectRegistry.StrongCount;
        var initialWeak = SwiftObjectRegistry.Count;

        var proxy = new HasValueProxy(new SimpleHasValue(99));
        var handle = ((ISwiftObject)proxy).SwiftHandle;

        // (1) Weak registration: construction must NOT bump StrongCount.
        AssertEqual(initialStrong, SwiftObjectRegistry.StrongCount,
            "C#-impl proxy must register WEAKLY: StrongCount must not change on construction (B2)");

        // (2) While alive: proxy is discoverable in the weak registry by its
        //     EveryProtocol handle, and the impl is rooted + resolvable by that handle.
        AssertTrue(SwiftObjectRegistry.TryGetProxy<HasValueProxy>(handle, out var found) && ReferenceEquals(found, proxy),
            "Proxy must be weak-registered and discoverable by its EveryProtocol handle while alive");
        AssertNotNull(ProxyLifetimeTracker.ResolveImpl<IHasValue>(handle),
            "Impl must be rooted and resolvable by handle while the proxy holds R0");
        GC.KeepAlive(proxy);

        // (3) Dispose releases R0 exactly once -> deinit -> impl root freed + unregister.
        proxy.Dispose();
        ForceGC();

        AssertNull(ProxyLifetimeTracker.ResolveImpl<IHasValue>(handle),
            "Impl root must be freed after Dispose drives EveryProtocol.deinit");
        AssertFalse(SwiftObjectRegistry.TryGetProxy<HasValueProxy>(handle, out _),
            "Registry entry must be unregistered after Dispose drives EveryProtocol.deinit");
        AssertEqual(initialWeak, SwiftObjectRegistry.Count,
            "Weak registry count must return to baseline after dispose");
    }

    public void TestProxyDoubleDisposeIsSafe()
    {
        var proxy = new HasValueProxy(new SimpleHasValue(10));

        // First dispose
        proxy.Dispose();

        // Second dispose — should not throw
        proxy.Dispose();

        TestLogger.Info("Double-dispose on proxy did not crash");
    }

    public void TestProxyPropertyAccessAfterDisposeThrows()
    {
        var proxy = new HasValueProxy(new SimpleHasValue(42));
        proxy.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = proxy.Value;
        }, "Property get after dispose should throw ObjectDisposedException");

        TestLogger.Info("Property access after dispose correctly throws ObjectDisposedException");
    }

    public void TestProxyMethodAccessAfterDisposeThrows()
    {
        var proxy = new HasValueProxy(new SimpleHasValue(42));
        proxy.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = proxy.GetValue();
        }, "Method call after dispose should throw ObjectDisposedException");

        TestLogger.Info("Method call after dispose correctly throws ObjectDisposedException");
    }

    #endregion
}
