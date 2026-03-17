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
/// P/Invoke resolution. This is not yet wired up in TestFramework, so
/// those tests are Tier 3 (expected to fail until wrapper lib is bundled).
/// Container-based tests (ExistentialContainer1 path) work without the
/// wrapper lib and are Tier 2.
/// </summary>
public class ProxyDisposeTests : TestBase
{
    public ProxyDisposeTests(TestResults results) : base(results) { }

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

    [MonoJitCrash] // HasValueProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
    public void TestProxyDisposeReleasesStrongReference()
    {
        var initialCount = SwiftObjectRegistry.StrongCount;

        var proxy = new HasValueProxy(new SimpleHasValue(99));
        var afterCreate = SwiftObjectRegistry.StrongCount;
        AssertEqual(initialCount + 1, afterCreate, "StrongCount should increase by 1 after proxy creation");

        proxy.Dispose();
        var afterDispose = SwiftObjectRegistry.StrongCount;
        AssertEqual(initialCount, afterDispose, "StrongCount should return to initial value after dispose");
    }

    [MonoJitCrash] // HasValueProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
    public void TestProxyDoubleDisposeIsSafe()
    {
        var proxy = new HasValueProxy(new SimpleHasValue(10));

        // First dispose
        proxy.Dispose();

        // Second dispose — should not throw
        proxy.Dispose();

        TestLogger.Info("Double-dispose on proxy did not crash");
    }

    [MonoJitCrash] // HasValueProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
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

    [MonoJitCrash] // HasValueProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
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
