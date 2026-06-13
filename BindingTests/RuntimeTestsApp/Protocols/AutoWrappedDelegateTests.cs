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
    /// Weak-store lifetime contract under GC (Design B2): a plain C# impl
    /// auto-wrapped into a Swift <c>weak var delegate</c> slot is NOT kept alive
    /// by the C# impl. Holding the impl reachable does not anchor the Swift-side
    /// existential, so after GC the weak slot clears and <c>fire()</c> routes
    /// through "no slot" (<c>LastNotifiedSlot == 0</c>).
    ///
    /// <para>
    /// This is the deliberate inverse of the old behaviour. Pre-B2 the auto-wrap
    /// cache + <c>SwiftObjectRegistry.RegisterStrong</c> rooted the proxy forever,
    /// so a dropped/GC'd impl still routed fire() correctly — at the cost of
    /// leaking one proxy per <c>(impl, protocol)</c> pair for the process
    /// lifetime. B2 roots the impl <em>from</em> Swift liveness (a strong
    /// handle-keyed GCHandle in <c>ProxyLifetimeTracker</c>), never the proxy/
    /// existential from impl liveness — a strong impl→proxy link would recreate
    /// exactly the uncollectable cross-boundary cycle B2 exists to break. So the
    /// auto-wrapped proxy is registered only weakly; once nothing strongly holds
    /// it, GC collects it, its finalizer releases the construction <c>+1</c> (R0),
    /// EveryProtocol deinits, and Swift zeroes the weak reference.
    /// </para>
    ///
    /// <para>
    /// Asserting <c>LastNotifiedSlot == 0</c> here is a meaningful regression
    /// guard: if a strong impl→proxy link (or <c>RegisterStrong</c>) is ever
    /// reintroduced, the proxy would survive GC, the weak slot would still
    /// resolve, and this would flip back to slot 1 — flagging the return of the
    /// leak. The complementary positive case — dispatch SURVIVES GC when Swift
    /// strongly retains the existential — lives in
    /// <c>ProxyLifetimeTests.TestStrongSwiftRetainSurvivesImplGc</c>.
    /// </para>
    /// </summary>
    public void TestWeakSwiftStoreIsNotALifetimeAnchor()
    {
        var monitor = new AutoWrappedMonitor();
        var impl = new AutoWrappedDelegateImpl();
        // Assign into the WEAK `delegate` slot only — no strong Swift retain. The
        // auto-wrapped proxy is weakly cached + weakly registered and is rooted by
        // nothing once the setter call returns.
        monitor.Delegate = impl;

        // Collect from a worker thread so a stale conservative reference to the
        // transient proxy in this frame cannot falsely keep it alive.
        ForceGCThorough();

        // The proxy was collected, R0 released, EveryProtocol deinit'd, and Swift
        // zeroed `weak var delegate`. fire() still runs (counter increments) but
        // finds no delegate, so it records slot 0 and never reaches the impl.
        monitor.Fire();
        monitor.Fire();

        AssertEqual(2, monitor.LastFiredValue, "Both fires ran (counter=2) even with the weak slot cleared");
        AssertEqual(0, monitor.LastNotifiedSlot,
            "Weak Swift store is not a lifetime anchor: the auto-wrapped proxy was collected, so fire() routed through no slot (0)");
        AssertFalse(impl.WasCalled, "No dispatch reached the impl once the pure-weak proxy was collected");

        // Keep impl reachable across the GC above to prove the point precisely:
        // a LIVE C# impl does NOT keep a pure-weak auto-wrapped Swift delegate
        // alive — B2 roots impl from Swift, not Swift from impl.
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
