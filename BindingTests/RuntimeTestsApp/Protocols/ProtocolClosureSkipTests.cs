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

    [SkipOnSimulator("Mono JIT async assertion (upstream Issue 1) — vtable String callback in RouteEvent triggers !ji->async crash")]
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

    [SkipOnSimulator("Mono JIT async assertion (upstream Issue 1) — vtable String callback in GetDelegateName triggers !ji->async crash")]
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
}
