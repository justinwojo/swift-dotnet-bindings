// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// C# implementation of IEventDelegate for testing proxy wrapping.
/// Session 4a: `OnComplete` is now a real receiver — it stores the incoming Action so
/// tests can assert the closure crossed the Swift→C# boundary and (when invoked) the
/// C# → Swift roundtrip via the invoke thunk fires the Swift closure body.
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

    public int OnCompleteCallCount { get; private set; }
    public Action? LastHandler { get; private set; }

    public void OnComplete(Action handler)
    {
        OnCompleteCallCount++;
        LastHandler = handler;
    }
}

/// <summary>
/// C# implementation of IDataLoadingDelegate. onDataLoaded has a String arg in its closure
/// which is not invoke-thunk-compatible — so the proxy maps it to a fatalError stub. The
/// C# overload here is just a stub; tests for this protocol verify the non-closure path.
/// </summary>
internal class TestDataLoadingDelegate : IDataLoadingDelegate
{
    private readonly string _sourceId;

    public TestDataLoadingDelegate(string sourceId)
    {
        _sourceId = sourceId;
    }

    public string GetSourceIdentifier() => _sourceId;

    public void OnDataLoaded(global::System.Action<string, int, bool> handler)
    {
        // Non-dispatchable from Swift (String arg not invoke-thunk-compatible) — the
        // proxy maps Swift's onDataLoaded to a fatalError stub, so this overload is
        // never invoked through the Swift→C# path.
    }
}

/// <summary>
/// C# implementation of INumericDataDelegate. All-primitives multi-arg closure IS
/// invoke-thunk-compatible — captures the handler and invokes it so the test can
/// observe Swift→C# multi-arg dispatch with a roundtrip.
/// </summary>
internal class TestNumericDataDelegate : INumericDataDelegate
{
    private readonly string _tag;

    public TestNumericDataDelegate(string tag)
    {
        _tag = tag;
    }

    public string GetSourceTag() => _tag;

    public int OnNumericDataCallCount { get; private set; }
    public global::System.Action<int, int, bool>? LastHandler { get; private set; }

    public void OnNumericData(global::System.Action<int, int, bool> handler)
    {
        OnNumericDataCallCount++;
        LastHandler = handler;
        handler(7, 11, true);
    }
}

/// <summary>
/// C# implementation of ICompletionDelegate. Captures the (possibly nil) closure so
/// the test can verify Optional&lt;Closure&gt; round-trips correctly through the proxy.
/// </summary>
internal class TestCompletionDelegate : ICompletionDelegate
{
    private readonly string _name;

    public TestCompletionDelegate(string name)
    {
        _name = name;
    }

    public string GetTaskLabel() => _name;

    public int ExecuteCallCount { get; private set; }
    public Action? LastCompletion { get; private set; }
    public bool LastCompletionWasNil { get; private set; }

    public void Execute(Action? completion)
    {
        ExecuteCallCount++;
        LastCompletion = completion;
        LastCompletionWasNil = (completion is null);
        completion?.Invoke();
    }
}

/// <summary>
/// C# implementation of IIntFactoryDelegate. Captures the return-typed closure and
/// invokes it so the test can assert the returned Int32 round-trips back to Swift.
/// </summary>
internal class TestIntFactoryDelegate : IIntFactoryDelegate
{
    public int LastReturned { get; private set; } = -1;

    public void MakeIntFactory(Func<int> factory)
    {
        LastReturned = factory();
    }
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

    // Session 4a covers only the Swift→C# direction: a Swift caller dispatching into the
    // proxy's [UnmanagedCallersOnly] receiver, which wraps the incoming (fnPtr, ctx)
    // IntPtr pair into a managed Action and forwards it to the C# impl. The opposite
    // direction — a C# caller invoking `proxy.OnComplete(action)` and having the proxy
    // marshal the managed Action *into* a Swift closure parameter for the witness call —
    // requires emitting a C#→Swift trampoline and is Session 4b scope. Until that lands,
    // the interface method `EventDelegateProxy.OnComplete(Action)` remains marked SB0003
    // and is exercised end-to-end via `EventRouter.FireOnComplete` instead (see Session 4a
    // region below).

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

    #endregion

    #region Session 4a — Swift → C# Closure Parameter Dispatch

    // Session 4a end-to-end: `() -> Void` closure param on a protocol method flows from
    // a Swift caller through the EveryProtocol cdecl vtable into the C# proxy receiver,
    // which wraps the (fnPtr, ctx) into a managed Action via the per-shape invoker class.
    // Invoking the stored Action calls back into Swift via the @_cdecl invoke thunk.

    public void TestEventRouter_FireOnComplete_StoresActionInCSharpImpl_Session4a()
    {
        var impl = new TestEventDelegate("FireTest", _ => true);
        var proxy = new EventDelegateProxy(impl);

        var router = new EventRouter();
        router.Delegate = proxy;

        // Swift `EventRouter.fireOnComplete` builds a closure and passes it to
        // `delegate?.onComplete(handler:)`. The proxy receiver should wrap the
        // closure and the C# impl should observe one OnComplete call with a
        // non-null Action.
        router.FireOnComplete("tag1");

        AssertEqual(1, impl.OnCompleteCallCount, "C# impl received exactly one OnComplete dispatch from Swift");
        AssertNotNull(impl.LastHandler, "C# impl captured the Swift closure as a managed Action");
        TestLogger.Info("Session 4a end-to-end: Swift onComplete(handler:) reached the C# impl");
    }

    public void TestEventRouter_FireOnComplete_InvokingStoredActionFiresSwiftClosure_Session4a()
    {
        var impl = new TestEventDelegate("Roundtrip", _ => true);
        var proxy = new EventDelegateProxy(impl);

        var router = new EventRouter();
        router.Delegate = proxy;

        // Pre-condition: Swift's `lastHandlerTag` starts empty.
        AssertEqual("", router.LastHandlerTag, "EventRouter.lastHandlerTag starts empty");

        router.FireOnComplete("roundtrip-tag");

        AssertNotNull(impl.LastHandler, "C# impl captured the Swift closure as a managed Action");

        // C# → Swift roundtrip: invoking the stored Action calls the @_cdecl invoke
        // thunk emitted by EveryProtocolEmitter.EmitProtocolClosureInvokeThunks, which
        // reconstructs the original Swift closure from (fnPtr, ctx) and runs its body.
        // The Swift closure mutates EventRouter.lastHandlerTag.
        impl.LastHandler!.Invoke();

        AssertEqual("roundtrip-tag", router.LastHandlerTag,
            "Invoking stored Action ran the Swift closure body via the cdecl invoke thunk");
        TestLogger.Info("Session 4a roundtrip: C# Action.Invoke() drove the Swift closure body");
    }

    public void TestDataLoadingDelegateProxy_MultiArgClosureStaticCtorWires()
    {
        // Multi-arg closure shape: pre-fix would have left
        // Func_onDataLoaded_0 unassigned. Forcing the static ctor verifies the
        // wiring runs without TypeInitializationException.
        global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(DataLoadingDelegateProxy).TypeHandle);
        AssertNotNull(typeof(DataLoadingDelegateProxy), "DataLoadingDelegateProxy static ctor ran cleanly");
    }

    /// <summary>
    /// Vtable-slot-collision regression: when a protocol has a non-dispatchable closure
    /// method *and* a non-closure method, the Swift vtable struct must omit the closure
    /// slot. If C# still declared the closure slot, every subsequent C# slot would be
    /// shifted one pointer-width past the address Swift reads — Swift's call into the
    /// non-closure method would land on the closure-receiver function pointer (wrong
    /// signature → crash or garbage data).
    ///
    /// DataLoadingDelegate has:
    ///   - onDataLoaded(handler: @escaping (String, Int32, Bool) -> Void) — NON-dispatchable (has args)
    ///   - sourceIdentifier() -> String                                   — non-closure method
    ///
    /// Pre-fix: C# struct = { func_onDataLoaded_0, func_sourceIdentifier_1 } (offsets 0, 8)
    ///          Swift struct = { func_sourceIdentifier_1 } (offset 0)
    ///          → Swift reads sourceIdentifier from offset 0 → lands on onDataLoaded fnPtr → crash.
    ///
    /// Post-fix: C# struct mirrors Swift's omission. Both have only sourceIdentifier at offset 0.
    /// </summary>
    public void TestDataLoader_GetSourceId_NonClosureSlotReadsCorrectFunctionPointer_VtableSlotCollision()
    {
        var impl = new TestDataLoadingDelegate("data-source-42");
        var proxy = new DataLoadingDelegateProxy(impl);

        var loader = new DataLoader();
        loader.Delegate = proxy;

        // Swift calls `delegate?.sourceIdentifier()` through the vtable. If the C# struct
        // still declared the skipped closure slot, this call would land on the wrong
        // function pointer and either crash or return garbage.
        var sourceId = loader.GetSourceId();

        AssertEqual("data-source-42", sourceId,
            "Swift→C# dispatch of sourceIdentifier() lands on the correct vtable slot (no closure-slot shift)");
        TestLogger.Info("Vtable-slot-collision regression: non-closure slot dispatched correctly");
    }

    public void TestCompletionDelegateProxy_OptionalClosureStaticCtorWires()
    {
        // Optional<Closure> shape: same wiring path, same regression target.
        global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(CompletionDelegateProxy).TypeHandle);
        AssertNotNull(typeof(CompletionDelegateProxy), "CompletionDelegateProxy static ctor ran cleanly");
    }

    #endregion

    #region Session 4b — Multi-Arg / Return-Typed / Optional Closure Dispatch

    public void TestNumericDataLoader_FireOnNumericData_MultiArgClosureRoundtrip_Session4b()
    {
        var impl = new TestNumericDataDelegate("numeric-source");
        var proxy = new NumericDataDelegateProxy(impl);

        var loader = new NumericDataLoader();
        loader.Delegate = proxy;

        // Swift `NumericDataLoader.fireOnNumericData` builds a multi-arg primitives-only
        // closure and dispatches through the proxy. The C# impl invokes it with (7, 11, true),
        // round-tripping back into the Swift closure body which mutates lastA/lastB/lastFlag.
        loader.FireOnNumericData();

        AssertEqual(1, impl.OnNumericDataCallCount, "C# impl received exactly one OnNumericData dispatch from Swift");
        AssertNotNull(impl.LastHandler, "C# impl captured the Swift closure as a managed Action<int,int,bool>");
        AssertEqual(7, loader.LastA, "Swift closure body received first int arg from C# Invoke");
        AssertEqual(11, loader.LastB, "Swift closure body received second int arg from C# Invoke");
        AssertTrue(loader.LastFlag, "Swift closure body received bool arg from C# Invoke");
        TestLogger.Info("Session 4b multi-arg roundtrip: 3 primitive args crossed Swift→C#→Swift via invoke thunk");
    }

    public void TestTaskRunner_FireExecute_WithNonNilCompletion_Session4b()
    {
        var impl = new TestCompletionDelegate("non-nil-task");
        var proxy = new CompletionDelegateProxy(impl);

        var runner = new TaskRunner();
        runner.Delegate = proxy;

        runner.FireExecute(true);

        AssertEqual(1, impl.ExecuteCallCount, "C# impl received Execute dispatch");
        AssertFalse(impl.LastCompletionWasNil, "Optional<Closure> non-nil arrived as non-null Action");
        AssertEqual(1, runner.CompletionFiredCount, "Swift closure body fired via C#→Swift invoke thunk");

        // Lifetime probe (regression sentinel for the reabstraction trap): invoke the
        // stored closure AFTER FireExecute returns and the Swift extension frame has
        // unwound. The original `if var localVar = param { withUnsafeBytes(of: &localVar) … }`
        // unwrap pattern materialized a partial-application context that was deallocated
        // on extension-scope exit — so this second invocation would have SIGSEGV'd
        // inside `$sIeg_ytIegr_TR`. The inout-bytes-on-Optional fix keeps the original
        // (fn, ctx) pair pointed at live storage; Optional<Closure> is always escaping,
        // so SwiftEscapingClosure retained the ctx via Arc.Retain and the call must
        // succeed.
        impl.LastCompletion!();
        AssertEqual(2, runner.CompletionFiredCount, "Optional<Closure> remains callable after Swift extension frame unwinds");
        TestLogger.Info("Session 4b Optional<Closure> non-nil: roundtrip + post-return lifetime probe completed");
    }

    public void TestTaskRunner_FireExecute_WithNilCompletion_Session4b()
    {
        var impl = new TestCompletionDelegate("nil-task");
        var proxy = new CompletionDelegateProxy(impl);

        var runner = new TaskRunner();
        runner.Delegate = proxy;

        runner.FireExecute(false);

        AssertEqual(1, impl.ExecuteCallCount, "C# impl received Execute dispatch with nil completion");
        AssertTrue(impl.LastCompletionWasNil, "Optional<Closure> nil arrived as null Action");
        AssertEqual(0, runner.CompletionFiredCount, "No Swift closure body fired (nil completion)");
        TestLogger.Info("Session 4b Optional<Closure> nil: nil round-tripped as null without sentinel");
    }

    public void TestIntFactoryRouter_FireMakeFactory_ReturnTypedClosure_Session4b()
    {
        var impl = new TestIntFactoryDelegate();
        var proxy = new IntFactoryDelegateProxy(impl);

        var router = new IntFactoryRouter();
        router.Delegate = proxy;

        router.FireMakeFactory(value: 1234);

        AssertEqual(1234, impl.LastReturned, "C# impl invoked Swift closure and observed returned Int32");
        AssertEqual(1234, router.LastReturnedValue, "Swift closure body recorded returned value");
        TestLogger.Info("Session 4b return-typed closure: Int32 return crossed C#→Swift→C# via invoke thunk");
    }

    #endregion
}
