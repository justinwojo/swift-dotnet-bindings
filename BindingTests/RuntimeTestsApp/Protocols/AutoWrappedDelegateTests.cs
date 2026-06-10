// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression tests for justinwojo/swift-dotnet-bindings#16 (performance-overlay library).
///
/// Covers the user-facing contract: a plain C# class implementing a generated protocol
/// interface can be assigned directly to a delegate property, passed to a constructor,
/// or passed to a method — WITHOUT the caller manually constructing the hidden
/// {Protocol}Proxy wrapper. The generator auto-wraps at the call site via the
/// <c>ExistentialContainerFactory.GetOrCreate&lt;T&gt;(value, wrapFallback)</c> overload.
///
/// These tests deliberately never reference <c>AutoWrappedMonitorDelegateProxy</c> —
/// if the generator regresses and stops emitting the wrap fallback, the property
/// setter / constructor / method call will throw
/// <see cref="System.InvalidCastException"/> at the call site and the test will fail
/// with the exact error the original bug report hit.
/// </summary>
public class AutoWrappedDelegateTests : TestBase
{
    public AutoWrappedDelegateTests(TestResults results) : base(results) { }

    /// <summary>
    /// Property setter path: reproduces the exact shape from the performance-overlay
    /// library repro (<c>monitor.Delegate = dele;</c>). The delegate is a plain C# class that
    /// only implements the generated interface. Asserts on <c>LastNotifiedSlot</c>
    /// so the test fails if the weak slot's proxy is missing — a strong-delegate
    /// fallback can no longer silently mask a regression because <c>fire()</c>
    /// dispatches via the weak slot exclusively.
    /// </summary>
    public void TestPlainImplAssignedToDelegateProperty()
    {
        var impl = new AutoWrappedDelegateImpl();
        var monitor = new AutoWrappedMonitor();

        // This line is the exact pattern that used to throw InvalidCastException.
        monitor.Delegate = impl;

        monitor.Fire();

        AssertEqual(1, monitor.LastFiredValue, "Monitor counter incremented to 1");
        AssertEqual(1, monitor.LastNotifiedSlot, "fire() dispatched via the weak `delegate` slot (1)");
        AssertTrue(impl.WasCalled, "Delegate callback fired");
        AssertEqual(1, impl.LastValue, "Delegate received the counter value");
    }

    /// <summary>
    /// Constructor-parameter path: the same existential-auto-wrap logic is emitted
    /// for constructor args via <c>ExistentialProjection.GetParameterPlan</c>.
    /// </summary>
    public void TestPlainImplPassedToConstructor()
    {
        var impl = new AutoWrappedDelegateImpl();

        // Monitor's init(initialDelegate:) takes `any AutoWrappedMonitorDelegate`.
        // Before the fix this throws InvalidCastException from inside the ctor body.
        var monitor = new AutoWrappedMonitor(initialDelegate: impl);

        monitor.Fire();

        AssertEqual(1, monitor.LastFiredValue, "Monitor counter incremented to 1");
        AssertEqual(1, monitor.LastNotifiedSlot, "fire() dispatched via the weak `delegate` slot (1)");
        AssertTrue(impl.WasCalled, "Delegate callback fired from constructor-stored delegate");
        AssertEqual(1, impl.LastValue, "Delegate received the counter value");
    }

    /// <summary>
    /// Method-parameter path: exercises <c>MethodSignature.GetCallArgumentString</c>,
    /// which is a separate emit site from the property setter.
    /// </summary>
    public void TestPlainImplPassedToMethodParameter()
    {
        var impl = new AutoWrappedDelegateImpl();
        var monitor = new AutoWrappedMonitor();

        // FireOnce(_ delegate:, value:) — not stored anywhere on the monitor,
        // a pure one-shot method parameter.
        monitor.FireOnce(impl, value: 99);

        AssertTrue(impl.WasCalled, "Delegate callback fired via method parameter");
        AssertEqual(99, impl.LastValue, "Delegate received the literal value");
    }

    /// <summary>
    /// Weak-slot survival under GC pressure: assigns the impl to the **weak**
    /// <c>delegate</c> property only — no strong fallback — forces GC, and
    /// asserts that the weak slot still services <c>fire()</c>.
    ///
    /// <para>
    /// Under the impl-anchored lifetime model, the live <c>impl</c> local
    /// anchors the auto-wrapped proxy via <c>ProxyLifetimeTracker</c>, which
    /// keeps the Swift <c>EveryProtocol</c> container retained, which in turn
    /// keeps the Swift-side <c>weak var delegate</c> slot resolvable. GC.Collect
    /// must not sever this chain as long as <c>impl</c> is reachable.
    /// </para>
    ///
    /// <para>
    /// Pre-fix, this test validated the leak-as-feature behaviour: the auto-wrap
    /// cache and <c>SwiftObjectRegistry.RegisterStrong</c> rooted the proxy
    /// forever, so even an impl that was dropped and GC'd still routed fire()
    /// correctly — at the cost of leaking one proxy per <c>(impl, protocol)</c>
    /// pair for the rest of the process. The fix intentionally breaks that
    /// behaviour; the collectibility invariant is covered by
    /// <c>ProxyLifetimeTests</c>. This test now validates the remaining
    /// (correct) invariant: while <c>impl</c> is alive, fire() still dispatches
    /// through the Swift weak slot across a GC cycle.
    /// </para>
    /// </summary>
    public void TestProxySurvivesBeyondLocalScope()
    {
        var monitor = new AutoWrappedMonitor();
        var impl = new AutoWrappedDelegateImpl();
        // Deliberately NOT setting monitor.StrongDelegate — the weak slot is the
        // only path that can keep the impl reachable from Swift's side. As long
        // as `impl` stays alive in this test scope, ProxyLifetimeTracker keeps
        // the proxy (and therefore the Swift weak slot) reachable across GC.
        monitor.Delegate = impl;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // After GC, the weak slot must still resolve and dispatch into the
        // managed implementation because `impl` is still rooted by this frame.
        monitor.Fire();
        monitor.Fire();

        AssertEqual(2, monitor.LastFiredValue, "Two fires after GC reached the delegate (counter=2)");
        AssertEqual(1, monitor.LastNotifiedSlot,
            "fire() routed through the weak slot — proxy survived GC while impl is rooted");

        // Anchor impl past the GC cycles above so the tracker keeps the proxy
        // alive. Dropping this would be the collectibility scenario that
        // ProxyLifetimeTests covers.
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Multi-protocol regression: a single C# instance implements TWO generated
    /// protocol interfaces and gets assigned to two different existential setters
    /// on the same Swift object. The auto-wrap cache must construct a distinct
    /// proxy for each protocol — coalescing them on the impl alone would route
    /// IAutoWrappedSecondaryDelegate dispatch through IAutoWrappedMonitorDelegate's
    /// witness table and the secondary callback would never fire (or would land
    /// on the wrong vtable slot and crash).
    /// </summary>
    public void TestSameImplWrappedForMultipleProtocols()
    {
        var impl = new DualDelegateImpl();
        var dual = new AutoWrappedDualMonitor();

        dual.Primary = impl;     // any AutoWrappedMonitorDelegate
        dual.Secondary = impl;   // any AutoWrappedSecondaryDelegate

        dual.FireBoth(value: 42);

        AssertTrue(impl.PrimaryCalled, "Primary witness table dispatched into the impl");
        AssertEqual(42, impl.PrimaryValue, "Primary received the literal value");
        AssertTrue(impl.SecondaryCalled, "Secondary witness table dispatched into the impl");
        AssertEqual(42, impl.SecondaryValue, "Secondary received the literal value");
    }

    /// <summary>
    /// SwiftDisposeScope+cache regression: assigning a delegate inside an active
    /// dispose scope and then continuing to use it AFTER the scope exits used to
    /// trip <see cref="ObjectDisposedException"/> on the next call. The proxy
    /// constructor unconditionally registered with the active scope, scope dispose
    /// marked the proxy disposed, but the auto-wrap cache still held the disposed
    /// proxy and returned it on the next <c>GetOrCreate</c>. The fix detaches each
    /// newly-created auto-wrap proxy from any active scope inside the cache factory
    /// so the cache owns the lifetime exclusively.
    /// </summary>
    public void TestProxySurvivesActiveDisposeScopeExit()
    {
        var impl = new AutoWrappedDelegateImpl();
        var monitor = new AutoWrappedMonitor();

        // Assign INSIDE a dispose scope and let the scope exit. Without the
        // detach fix the proxy is now disposed but still cached, so the next
        // line throws ObjectDisposedException from GetExistentialContainer().
        using (var scope = new SwiftDisposeScope())
        {
            monitor.Delegate = impl;
        }

        // Reuse the same impl after scope exit — would re-enter the cache and
        // hit the disposed-cached proxy.
        monitor.Delegate = impl;
        monitor.Fire();

        AssertEqual(1, monitor.LastFiredValue, "Monitor counter incremented to 1 after scope exit");
        AssertEqual(1, monitor.LastNotifiedSlot, "fire() dispatched via the weak slot after scope exit");
        AssertTrue(impl.WasCalled, "Delegate callback fired after scope exit");
        AssertEqual(1, impl.LastValue, "Delegate received the counter value after scope exit");
    }
}

/// <summary>
/// C# implementation of two unrelated generated protocol interfaces. The cache
/// regression test asserts that auto-wrapping this single instance for both
/// protocols produces two distinct proxies with two distinct witness tables.
/// </summary>
internal class DualDelegateImpl : IAutoWrappedMonitorDelegate, IAutoWrappedSecondaryDelegate
{
    public bool PrimaryCalled { get; private set; }
    public int PrimaryValue { get; private set; }
    public bool SecondaryCalled { get; private set; }
    public int SecondaryValue { get; private set; }

    public void MonitorDidUpdate(int value)
    {
        PrimaryCalled = true;
        PrimaryValue = value;
    }

    public void SecondaryDidNotify(int value)
    {
        SecondaryCalled = true;
        SecondaryValue = value;
    }
}

/// <summary>
/// Minimal C# implementation of the generated <c>IAutoWrappedMonitorDelegate</c>
/// interface. Critically: implements ONLY the interface. Does not implement
/// ISwiftExistentialConvertible or IExistentialBoxable, does not extend any
/// proxy class, and is never manually wrapped at any call site in the tests.
/// </summary>
internal class AutoWrappedDelegateImpl : IAutoWrappedMonitorDelegate
{
    public bool WasCalled { get; private set; }
    public int LastValue { get; private set; }

    public void MonitorDidUpdate(int value)
    {
        WasCalled = true;
        LastValue = value;
    }
}
