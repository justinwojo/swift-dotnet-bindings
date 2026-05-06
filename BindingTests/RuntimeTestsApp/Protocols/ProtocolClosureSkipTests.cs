// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// C# implementation of IEventDelegate for testing proxy wrapping.
/// Implements the non-closure members; OnComplete throws NotSupportedException
/// since closure methods cannot be conformably projected from C# to Swift (SB0003).
/// </summary>
internal class TestEventDelegate : IEventDelegate
{
    private readonly string _name;
    private readonly Func<string, bool> _eventHandler;

    public TestEventDelegate(string name, Func<string, bool> eventHandler)
    {
        _name = name;
        _eventHandler = eventHandler;
    }

    public string DelegateName => _name;

    public bool DidReceiveEvent(string name) => _eventHandler(name);

#pragma warning disable CS0618, SB0003 // Obsolete + SB0003 — testing that it throws
    public void OnComplete(Action handler)
    {
        throw new NotSupportedException("OnComplete is a closure method and cannot be called through the protocol proxy (SB0003).");
    }
#pragma warning restore CS0618, SB0003
}

/// <summary>
/// Tests that protocol closure skipping works correctly.
/// EventDelegate has a closure method (onComplete) that should be skipped,
/// while non-closure members (didReceiveEvent, delegateName) should be emitted.
///
/// Pattern from Starscream, RxSwift, StripeUICore: protocols mixing
/// closure and non-closure methods.
///
/// Verification approach: If the protocol interface is emitted with the
/// non-closure methods, EventRouter can be constructed and its methods
/// called. The closure method should not appear in the C# interface.
/// </summary>
public class ProtocolClosureSkipTests : TestBase
{
    public ProtocolClosureSkipTests(TestResults results) : base(results) { }

    #region EventRouter Construction (Tier 1)

    public void TestEventRouterConstruction()
    {
        var router = new EventRouter();
        AssertNotNull(router, "EventRouter constructed");
        TestLogger.Info("EventRouter construction passed");
    }

    #endregion

    #region EventRouter Methods — No Delegate (Tier 1)

    public void TestRouteEventWithNilDelegate()
    {
        var router = new EventRouter();
        var result = router.RouteEvent("click");
        AssertFalse(result, "RouteEvent with nil delegate returns false");
        TestLogger.Info($"RouteEvent(\"click\") with nil delegate = {result}");
    }

    public void TestGetDelegateNameWithNilDelegate()
    {
        var router = new EventRouter();
        var name = router.GetDelegateName();
        AssertEqual("none", name, "GetDelegateName with nil delegate");
        TestLogger.Info($"GetDelegateName() with nil delegate = \"{name}\"");
    }

    #endregion

    #region C# Implementation via EventDelegateProxy (Tier 2)

    public void TestCSharpImplProxyConstruction()
    {
        var impl = new TestEventDelegate("TestProxy", _ => true);
        var proxy = new EventDelegateProxy(impl);
        AssertNotNull(proxy, "EventDelegateProxy wrapping C# impl constructed");
        TestLogger.Info("EventDelegateProxy(IEventDelegate) construction passed");
    }

    public void TestCSharpImplDelegateName()
    {
        var impl = new TestEventDelegate("MyDelegate", _ => true);
        var proxy = new EventDelegateProxy(impl);
        var name = proxy.DelegateName;
        AssertEqual("MyDelegate", name, "Proxy.DelegateName from C# impl");
        TestLogger.Info($"Proxy.DelegateName = \"{name}\"");
    }

    public void TestCSharpImplDidReceiveEventTrue()
    {
        var impl = new TestEventDelegate("Handler", name => name == "click");
        var proxy = new EventDelegateProxy(impl);
        var result = proxy.DidReceiveEvent("click");
        AssertTrue(result, "Proxy.DidReceiveEvent(\"click\") returns true");
        TestLogger.Info($"Proxy.DidReceiveEvent(\"click\") = {result}");
    }

    public void TestCSharpImplDidReceiveEventFalse()
    {
        var impl = new TestEventDelegate("Handler", name => name == "click");
        var proxy = new EventDelegateProxy(impl);
        var result = proxy.DidReceiveEvent("hover");
        AssertFalse(result, "Proxy.DidReceiveEvent(\"hover\") returns false");
        TestLogger.Info($"Proxy.DidReceiveEvent(\"hover\") = {result}");
    }

#pragma warning disable CS0618, SB0003 // Obsolete + SB0003 — testing that it throws
    public void TestCSharpImplOnCompleteThrowsNotSupported()
    {
        var impl = new TestEventDelegate("Handler", _ => true);
        var proxy = new EventDelegateProxy(impl);
        try
        {
            proxy.OnComplete(() => { });
            // If we get here, the test fails — should have thrown
            AssertTrue(false, "OnComplete should throw NotSupportedException");
        }
        catch (NotSupportedException ex)
        {
            AssertTrue(ex.Message.Contains("SB0003"), "OnComplete throws NotSupportedException with SB0003");
            TestLogger.Info($"OnComplete correctly threw NotSupportedException: {ex.Message}");
        }
    }
#pragma warning restore CS0618, SB0003

    public void TestSetCSharpImplOnRouterAndRouteEvent()
    {
        var receivedEvents = new List<string>();
        var impl = new TestEventDelegate("LiveDelegate", name =>
        {
            receivedEvents.Add(name);
            return true;
        });
        var proxy = new EventDelegateProxy(impl);

        var router = new EventRouter();
        router.Delegate = proxy;

        var result = router.RouteEvent("tap");
        AssertTrue(result, "RouteEvent through C# impl returns true");
        TestLogger.Info($"RouteEvent(\"tap\") through C# delegate = {result}");
    }

    public void TestSetCSharpImplOnRouterGetDelegateName()
    {
        var impl = new TestEventDelegate("CustomDelegate", _ => false);
        var proxy = new EventDelegateProxy(impl);

        var router = new EventRouter();
        router.Delegate = proxy;

        var name = router.GetDelegateName();
        AssertEqual("CustomDelegate", name, "GetDelegateName through C# impl");
        TestLogger.Info($"GetDelegateName() through C# delegate = \"{name}\"");
    }

    #endregion

    #region DataLoadingDelegate — Multi-Arg Closure Tuple Unwrapping (Tier 1)

    // These tests verify that protocols with multi-argument closure methods compile
    // correctly. The EveryProtocol closure stub must render `(String, Int32, Bool) -> Void`
    // NOT `((String, Int32, Bool)) -> Void` (tuple-wrapped). If the stub has wrong syntax,
    // the Swift wrapper won't compile and DataLoader won't exist as a type.

    public void TestDataLoaderConstruction()
    {
        var loader = new DataLoader();
        AssertNotNull(loader, "DataLoader constructed (multi-arg closure protocol compiles)");
        TestLogger.Info("DataLoader construction passed — multi-arg closure stub compiled correctly");
    }

    public void TestDataLoaderGetSourceIdWithNilDelegate()
    {
        var loader = new DataLoader();
        var sourceId = loader.GetSourceId();
        AssertEqual("unknown", sourceId, "GetSourceId with nil delegate");
        TestLogger.Info($"GetSourceId() with nil delegate = \"{sourceId}\"");
    }

    #endregion

    #region CompletionDelegate — Optional Closure @escaping Suppression (Tier 1)

    // These tests verify that protocols with optional closure parameters compile
    // correctly. The EveryProtocol closure stub must NOT emit `@escaping` on
    // `Optional<Closure>` — optional closures are always escaping in Swift.
    // If @escaping is emitted, Swift rejects it as invalid syntax.

    public void TestTaskRunnerConstruction()
    {
        var runner = new TaskRunner();
        AssertNotNull(runner, "TaskRunner constructed (optional closure protocol compiles)");
        TestLogger.Info("TaskRunner construction passed — optional closure stub compiled correctly");
    }

    public void TestTaskRunnerGetTaskNameWithNilDelegate()
    {
        var runner = new TaskRunner();
        var name = runner.GetTaskName();
        AssertEqual("idle", name, "GetTaskName with nil delegate");
        TestLogger.Info($"GetTaskName() with nil delegate = \"{name}\"");
    }

    #endregion

    #region Empty-Proxy-Vtable Bug — Closure-Skipped Vtable Slots Are Wired

    // Layer-A regression for bug-0.10.0-empty-proxy-vtables-for-closure-protocol-methods.
    //
    // Pre-fix: the proxy declared a `Func_<closureMethod>_N` field but the static ctor
    // never assigned it, so the Swift→C# vtable slot held a null function pointer.
    // Swift's witness dispatch into that slot SIGSEGV'd silently — a programming error
    // that the consumer had no way to diagnose because the C# side compiled and the
    // proxy registered fine.
    //
    // Post-fix: the static ctor wires the slot to an observable-failure trampoline
    // (`throw new NotSupportedException` from inside [UnmanagedCallersOnly]). We can't
    // assert the throw at runtime without process-terminating the test, but the
    // generated-code shape is locked down by the matching unit tests in
    // ProtocolProxyEmitterTests / ProtocolHandlerOutputTests. The runtime checks here
    // exercise the static-ctor execution path: pre-fix the static ctor failed silently
    // in a different way for some shapes (multi-arg-closure / optional-closure types
    // landed differently in the local-vtable struct layout), so even the construction
    // path is a regression target.

    public void TestEventDelegateProxy_StaticCtorWiresClosureSlot()
    {
        // Construct a proxy to force the static ctor + local-vtable init to run.
        // The slot wiring (`Func_onComplete_1 = &Receive_onComplete_1,`) executes
        // here. Pre-fix this line was missing entirely.
        var impl = new TestEventDelegate("Slot", _ => true);
        var proxy = new EventDelegateProxy(impl);
        AssertNotNull(proxy, "EventDelegateProxy with closure-skipped onComplete constructs cleanly");
    }

    public void TestDataLoadingDelegateProxy_MultiArgClosureStaticCtorWires()
    {
        // Multi-arg closure shape: pre-fix would have left
        // Func_onDataLoaded_0 unassigned. Forcing the static ctor verifies the
        // wiring runs without TypeInitializationException.
        global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(DataLoadingDelegateProxy).TypeHandle);
        AssertNotNull(typeof(DataLoadingDelegateProxy), "DataLoadingDelegateProxy static ctor ran cleanly");
    }

    public void TestCompletionDelegateProxy_OptionalClosureStaticCtorWires()
    {
        // Optional<Closure> shape: same wiring path, same regression target.
        global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(CompletionDelegateProxy).TypeHandle);
        AssertNotNull(typeof(CompletionDelegateProxy), "CompletionDelegateProxy static ctor ran cleanly");
    }

    #endregion
}
