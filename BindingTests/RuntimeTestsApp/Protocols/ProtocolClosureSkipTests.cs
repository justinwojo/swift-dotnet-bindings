// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices.Swift;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// C# implementation of IEventDelegate for testing proxy wrapping.
/// `OnComplete` is now a real receiver — it stores the incoming Action so
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
/// Shape 1: C# implementation of IThrowingIntDelegate. The proxy
/// surfaces the Swift `(Int32) throws -> Int32` closure as a managed
/// `Func&lt;int, SwiftResult&lt;int, SwiftError&gt;&gt;`, exposing both the success
/// and failure paths to C# code without throwing across the ABI boundary.
/// </summary>
internal class TestThrowingIntDelegate : IThrowingIntDelegate
{
    public int CallCount { get; private set; }
    public Func<int, Swift.SwiftResult<int, SwiftError>>? LastCallback { get; private set; }

    public void ProcessInt(Func<int, Swift.SwiftResult<int, SwiftError>> callback)
    {
        CallCount++;
        LastCallback = callback;
    }
}

/// <summary>
/// Shape 3: C# implementation of IHasCallbackDelegate. Exposes a
/// plain auto-property as the Handler storage; the proxy's setter receiver
/// writes into it from Swift (Swift→C# direction) and the proxy's getter
/// receiver reads from it for Swift to invoke (C#→Swift direction).
/// `CSharpHandlerFiredCount` lets tests observe whether the materialised
/// Swift closure actually invoked the underlying C# Action.
/// </summary>
internal class TestCallbackDelegate : IHasCallbackDelegate
{
    public global::System.Action? Handler { get; set; }
    public int CSharpHandlerFiredCount { get; private set; }

    public void SetCSharpAction()
    {
        Handler = () => { CSharpHandlerFiredCount++; };
    }
}

/// <summary>
/// Shape 4: C# implementation of IHandlerFactoryDelegate. The
/// proxy receiver for <c>MakeHandler()</c> calls this method on every Swift
/// invocation; the returned Action is pinned via GCHandle and surfaced to
/// Swift as a (fnPtr, ctx) pair that Swift wraps into a real
/// <c>() -&gt; Void</c> closure. <c>FactoryCallCount</c> tracks how many times
/// Swift drove the factory; each call should yield a fresh Action.
/// </summary>
internal class TestHandlerFactoryDelegate : IHandlerFactoryDelegate
{
    public int FactoryCallCount { get; private set; }
    public int CSharpHandlerFiredCount { get; private set; }
    public bool ReturnNullHandler { get; set; }

    public global::System.Action MakeHandler()
    {
        FactoryCallCount++;
        if (ReturnNullHandler)
            return null!;
        return () => { CSharpHandlerFiredCount++; };
    }
}

internal class TestAsyncIntDelegate : IAsyncIntDelegate
{
    public int RunAsyncCallCount { get; private set; }
    public global::System.Func<global::System.Threading.Tasks.Task<int>>? LastHandler { get; private set; }
    public int LastObservedValue { get; private set; } = -1;

    public void RunAsync(global::System.Func<global::System.Threading.Tasks.Task<int>> handler)
    {
        RunAsyncCallCount++;
        LastHandler = handler;
        var t = handler();
        t.Wait();
        LastObservedValue = t.Result;
    }
}

/// <summary>
/// C# implementation of <see cref="IMultiShapeDelegate"/> exercising every supported
/// closure/property/method shape in one delegate — mirrors the surface of real-world
/// consumer protocols (Nuke <c>ImagePipelineDelegate</c>, BlinkIDUX <c>CameraModel</c>).
/// Counters expose every dispatch path so the test can prove the six receivers
/// reached the real impl rather than a fatalError stub.
/// </summary>
internal class TestMultiShapeDelegate : IMultiShapeDelegate
{
    private int _pipelineState;
    private bool _isTorchEnabled;
    private global::System.Action? _onPipelineStateChange;

    public int PipelineStateGetCallCount { get; private set; }
    public int IsTorchEnabledSetCallCount { get; private set; }
    public int OnPipelineStateChangeSetCallCount { get; private set; }
    public int OnPipelineStateChangeGetCallCount { get; private set; }
    public int MakePipelineStateReaderCallCount { get; private set; }
    public int RunDiagnosticsAsyncCallCount { get; private set; }
    public int ProcessPipelineStateThrowingCallCount { get; private set; }
    public int LastObservedAsyncValue { get; private set; } = -1;
    public bool? LastThrowingResultIsSuccess { get; private set; }
    public int LastThrowingSuccessValue { get; private set; } = -1;
    public bool LastThrowingFailureHadNonNullError { get; private set; }

    public TestMultiShapeDelegate(int initialPipelineState = 0)
    {
        _pipelineState = initialPipelineState;
    }

    public int PipelineState
    {
        get
        {
            PipelineStateGetCallCount++;
            return _pipelineState;
        }
    }

    public void SetPipelineState(int value) => _pipelineState = value;

    public bool IsTorchEnabled
    {
        get => _isTorchEnabled;
        set
        {
            IsTorchEnabledSetCallCount++;
            _isTorchEnabled = value;
        }
    }

    public global::System.Action? OnPipelineStateChange
    {
        get
        {
            OnPipelineStateChangeGetCallCount++;
            return _onPipelineStateChange;
        }
        set
        {
            OnPipelineStateChangeSetCallCount++;
            _onPipelineStateChange = value;
        }
    }

    public global::System.Action MakePipelineStateReader()
    {
        MakePipelineStateReaderCallCount++;
        return () => { _pipelineState += 1; };
    }

    public void RunDiagnosticsAsync(global::System.Func<global::System.Threading.Tasks.Task<int>> handler)
    {
        RunDiagnosticsAsyncCallCount++;
        var t = handler();
        t.Wait();
        LastObservedAsyncValue = t.Result;
    }

    public unsafe void ProcessPipelineStateThrowing(global::System.Func<int, Swift.SwiftResult<int, SwiftError>> handler)
    {
        ProcessPipelineStateThrowingCallCount++;
        var result = handler(10);
        LastThrowingResultIsSuccess = result.IsSuccess;
        if (result.IsSuccess)
        {
            LastThrowingSuccessValue = result.Success;
            _pipelineState = result.Success;
        }
        else
        {
            // The throwing-closure cdecl thunk always passRetained()s a non-null error
            // on the failure branch; treat any non-null pointer as a real failure payload.
            LastThrowingFailureHadNonNullError = result.Failure.Value != null;
        }
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

    // The receiver covers only the Swift→C# direction: a Swift caller dispatching into the
    // proxy's [UnmanagedCallersOnly] receiver, which wraps the incoming (fnPtr, ctx)
    // IntPtr pair into a managed Action and forwards it to the C# impl. The opposite
    // direction — a C# caller invoking `proxy.OnComplete(action)` and having the proxy
    // marshal the managed Action *into* a Swift closure parameter for the witness call —
    // requires emitting a C#→Swift trampoline (handled by the multi-arg fixtures). Here,
    // the interface method `EventDelegateProxy.OnComplete(Action)` remains marked SB0003
    // we exercise the receiver end-to-end via `EventRouter.FireOnComplete` instead (see the
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

    #region Swift → C# Closure Parameter Dispatch

    // End-to-end: `() -> Void` closure param on a protocol method flows from
    // a Swift caller through the EveryProtocol cdecl vtable into the C# proxy receiver,
    // which wraps the (fnPtr, ctx) into a managed Action via the per-shape invoker class.
    // Invoking the stored Action calls back into Swift via the @_cdecl invoke thunk.

    public void TestEventRouter_FireOnComplete_StoresActionInCSharpImpl()
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
        TestLogger.Info("End-to-end: Swift onComplete(handler:) reached the C# impl");
    }

    public void TestEventRouter_FireOnComplete_InvokingStoredActionFiresSwiftClosure()
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
        TestLogger.Info("Roundtrip: C# Action.Invoke() drove the Swift closure body");
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

    #region Multi-Arg / Return-Typed / Optional Closure Dispatch

    public void TestNumericDataLoader_FireOnNumericData_MultiArgClosureRoundtrip()
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
        TestLogger.Info("Multi-arg roundtrip: 3 primitive args crossed Swift→C#→Swift via invoke thunk");
    }

    public void TestTaskRunner_FireExecute_WithNonNilCompletion()
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
        TestLogger.Info("Optional<Closure> non-nil: roundtrip + post-return lifetime probe completed");
    }

    public void TestTaskRunner_FireExecute_WithNilCompletion()
    {
        var impl = new TestCompletionDelegate("nil-task");
        var proxy = new CompletionDelegateProxy(impl);

        var runner = new TaskRunner();
        runner.Delegate = proxy;

        runner.FireExecute(false);

        AssertEqual(1, impl.ExecuteCallCount, "C# impl received Execute dispatch with nil completion");
        AssertTrue(impl.LastCompletionWasNil, "Optional<Closure> nil arrived as null Action");
        AssertEqual(0, runner.CompletionFiredCount, "No Swift closure body fired (nil completion)");
        TestLogger.Info("Optional<Closure> nil: nil round-tripped as null without sentinel");
    }

    public void TestIntFactoryRouter_FireMakeFactory_ReturnTypedClosure()
    {
        var impl = new TestIntFactoryDelegate();
        var proxy = new IntFactoryDelegateProxy(impl);

        var router = new IntFactoryRouter();
        router.Delegate = proxy;

        router.FireMakeFactory(value: 1234);

        AssertEqual(1234, impl.LastReturned, "C# impl invoked Swift closure and observed returned Int32");
        AssertEqual(1234, router.LastReturnedValue, "Swift closure body recorded returned value");
        TestLogger.Info("Return-typed closure: Int32 return crossed C#→Swift→C# via invoke thunk");
    }

    #endregion

    #region Throwing Closure (Shape 1)

    // Shape 1: a `(Int32) throws -> Int32` closure on a protocol method.
    // The cdecl invoke thunk uses an explicit `_errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>`
    // parameter (NOT SwiftError register convention — that's a CallConvSwift artifact). On the
    // C# side the proxy emits a P/Invoke with `out IntPtr errorOut` and wraps the result in
    // `SwiftResult<T, SwiftError>` via an unsafe invoker class. Success and failure paths are
    // observable from C# without crossing the ABI boundary with a managed exception.

    public unsafe void TestThrowingIntRouter_FireProcessInt_SuccessPathRoundtrip()
    {
        var impl = new TestThrowingIntDelegate();
        var proxy = new ThrowingIntDelegateProxy(impl);

        var router = new ThrowingIntRouter();
        router.Delegate = proxy;

        // Swift `ThrowingIntRouter.fireProcessInt` builds a `(Int32) throws -> Int32`
        // closure and passes it to `delegate?.processInt(callback:)`. The proxy receiver
        // wraps (fnPtr, ctx) into a `Func<int, SwiftResult<int, SwiftError>>` via the
        // throwing-aware invoker class. The C# impl captures the func.
        router.FireProcessInt();

        AssertEqual(1, impl.CallCount, "C# impl received exactly one ProcessInt dispatch from Swift");
        AssertNotNull(impl.LastCallback, "C# impl captured the throwing Swift closure as Func<int, SwiftResult<int, SwiftError>>");

        // Drive the success path: non-negative input → Swift returns input*2 without throwing.
        var success = impl.LastCallback!.Invoke(7);
        AssertTrue(success.IsSuccess, "Non-negative input is success case (SwiftResult.IsSuccess)");
        AssertFalse(success.IsFailure, "Non-negative input is not failure case");
        AssertEqual(14, success.Success, "Swift closure body doubled the input via &* multiplication");
        TestLogger.Info("Shape 1: success roundtrip through cdecl errorOut thunk + SwiftResult.FromSuccess");
    }

    public unsafe void TestThrowingIntRouter_FireProcessInt_FailurePathSurfacesError()
    {
        var impl = new TestThrowingIntDelegate();
        var proxy = new ThrowingIntDelegateProxy(impl);

        var router = new ThrowingIntRouter();
        router.Delegate = proxy;

        router.FireProcessInt();
        AssertNotNull(impl.LastCallback, "Throwing closure captured");

        // Drive the failure path: negative input → Swift throws ThrowingProcessorError.
        // The cdecl thunk catches the error, writes `_errorOut.pointee = passRetained(...)`,
        // and returns the default Int32(0). C# observes a non-zero errorOut pointer and
        // wraps it in SwiftError, then SwiftResult.FromFailure surfaces it as a Failure case.
        var failure = impl.LastCallback!.Invoke(-3);
        AssertTrue(failure.IsFailure, "Negative input is failure case (SwiftResult.IsFailure)");
        AssertFalse(failure.IsSuccess, "Negative input is not success case");

        // The Failure payload is a SwiftError whose Value is the raw AnyObject pointer for
        // the boxed Swift error. We can't decode the error type from C# without additional
        // metadata, but a non-null pointer proves the error round-tripped through the cdecl
        // errorOut → SwiftError → SwiftResult chain.
        var swiftErr = failure.Failure;
        AssertTrue(swiftErr.Value != null, "SwiftError carries a non-null AnyObject pointer for the boxed Swift error");
        TestLogger.Info("Shape 1: failure roundtrip through cdecl errorOut thunk + SwiftResult.FromFailure");
    }

    #endregion

    #region Closure Property (Shape 3)

    // Shape 3: an `Optional<() -> Void>` property on a protocol.
    // The proxy emits two [UnmanagedCallersOnly] receivers + one per-property
    // @_cdecl invoke thunk:
    //   - Receive_handler_set : Swift→C# direction. Swift calls the setter through
    //     the EveryProtocol vtable, passing (rawFn, rawCtx). For rawFn==0 the
    //     receiver assigns null to the C# impl; otherwise it wraps the pair via
    //     `SwiftEscapingClosure<Action>.FromSwift` (retains Swift ctx) and stores
    //     the resulting Action on the impl.
    //   - Receive_handler_get : C#→Swift direction. Swift calls the getter through
    //     the vtable, the receiver allocates a 16-byte buffer, pins the C# Action
    //     in a GCHandle, and writes (thunkPtr, gchandle) so Swift can materialise
    //     a real `() -> Void` closure pointing back at the C# delegate.
    //   - _PropClosureThunk_handler : the @_cdecl entry point Swift fires when the
    //     materialised closure runs; it looks up the GCHandle and calls the Action.

    /// <summary>
    /// Swift → C# direction (setter): Swift assigns a closure to `delegate.handler`.
    /// The proxy receiver wraps (fnPtr, ctx) into a managed Action via
    /// `SwiftEscapingClosure<Action>.FromSwift`, which Arc-retains the Swift context.
    /// We then invoke the stored Action and observe the Swift closure body running.
    /// </summary>
    public void TestCallbackRouter_SetHandlerFromSwift_NonNil_SetsImplHandler()
    {
        var impl = new TestCallbackDelegate();
        var proxy = new HasCallbackDelegateProxy(impl);

        var router = new CallbackRouter();
        router.Delegate = proxy;

        // Pre-conditions: no handler set, fire count starts at zero.
        AssertEqual(0, router.SwiftHandlerFiredCount, "router.SwiftHandlerFiredCount starts at zero");

        // Swift assigns its own closure into delegate.handler. The proxy's
        // Receive_handler_set wraps (fnPtr, ctx) and assigns a managed Action.
        router.SetHandlerFromSwift(toNil: false);

        AssertNotNull(impl.Handler, "Setter from Swift produced a non-null managed Action on the C# impl");

        // Invoke the captured Action — this calls _InvokeClosureThunk_<hash>(fnPtr, ctx),
        // which runs the Swift closure body and increments swiftHandlerFiredCount.
        impl.Handler!.Invoke();
        AssertEqual(1, router.SwiftHandlerFiredCount, "Invoking the captured Action fires the Swift closure body once");

        // Lifetime probe: invoke again after the Swift extension frame has unwound.
        // SwiftEscapingClosure retains the Swift context, so the closure must remain
        // callable. This mirrors the Optional<Closure> reabstraction-trap regression
        // we hit in the Optional<Closure> dispatch fixtures.
        impl.Handler!.Invoke();
        AssertEqual(2, router.SwiftHandlerFiredCount, "Captured Action remains callable across multiple invocations");
        TestLogger.Info("Shape 3: Swift→C# setter assigned a non-null Action that round-trips into Swift");
    }

    /// <summary>
    /// Swift → C# direction (setter, nil): Swift assigns nil to `delegate.handler`.
    /// rawFn arrives as IntPtr.Zero so the receiver assigns null to the impl, and
    /// no SwiftEscapingClosure is constructed.
    /// </summary>
    public void TestCallbackRouter_SetHandlerFromSwift_Nil_ClearsImplHandler()
    {
        var impl = new TestCallbackDelegate();
        impl.SetCSharpAction(); // pre-seed so we can observe the null overwrite
        var proxy = new HasCallbackDelegateProxy(impl);

        var router = new CallbackRouter();
        router.Delegate = proxy;

        AssertNotNull(impl.Handler, "Pre-condition: impl.Handler starts non-null");

        // Swift `delegate?.handler = nil` flows through the setter receiver with
        // rawFn == IntPtr.Zero → receiver assigns null.
        router.SetHandlerFromSwift(toNil: true);

        AssertTrue(impl.Handler is null, "Setter-from-Swift with nil cleared the C# impl handler");
        TestLogger.Info("Shape 3: Swift→C# setter with nil cleared the C# Action");
    }

    /// <summary>
    /// Same nil path, but driven by `ClearHandlerOnDelegate` — equivalent Swift body
    /// but a different witness call. Locks in the bare nil-setter dispatch.
    /// </summary>
    public void TestCallbackRouter_ClearHandlerOnDelegate_ClearsImplHandler()
    {
        var impl = new TestCallbackDelegate();
        impl.SetCSharpAction();
        var proxy = new HasCallbackDelegateProxy(impl);

        var router = new CallbackRouter();
        router.Delegate = proxy;

        AssertNotNull(impl.Handler, "Pre-condition: impl.Handler starts non-null");

        router.ClearHandlerOnDelegate();

        AssertTrue(impl.Handler is null, "ClearHandlerOnDelegate cleared the C# impl handler via the setter receiver");
        TestLogger.Info("Shape 3: ClearHandlerOnDelegate cleared the C# Action via the nil setter path");
    }

    /// <summary>
    /// C# → Swift direction (getter): C# pre-populates impl.Handler with a managed
    /// Action. Swift calls `delegate?.handler?()`, which goes through the getter
    /// receiver. The receiver allocates a GCHandle on the Action and returns
    /// (thunkPtr, gchandle). Swift wraps that pair into a real `() -> Void`
    /// closure via `_sbWrapClosureContext` and invokes it — which fires
    /// `_PropClosureThunk_handler`, looks up the GCHandle, and calls the Action.
    /// </summary>
    public void TestCallbackRouter_InvokeHandler_FiresCSharpActionViaGetter()
    {
        var impl = new TestCallbackDelegate();
        impl.SetCSharpAction();
        var proxy = new HasCallbackDelegateProxy(impl);

        var router = new CallbackRouter();
        router.Delegate = proxy;

        AssertEqual(0, impl.CSharpHandlerFiredCount, "Pre-condition: C# Action has not fired");

        // Swift `delegate?.handler?()` invokes the getter then calls the materialised closure.
        router.InvokeHandler();

        AssertEqual(1, impl.CSharpHandlerFiredCount, "Swift invocation of materialised closure fired the underlying C# Action exactly once");

        // Second invocation drives a fresh getter call → fresh GCHandle → second
        // thunk firing. Asserts the getter is not single-shot (no accidental
        // GCHandle.Free during dispatch).
        router.InvokeHandler();
        AssertEqual(2, impl.CSharpHandlerFiredCount, "Repeated InvokeHandler keeps firing the C# Action");
        TestLogger.Info("Shape 3: C#→Swift getter materialised a closure that called back into the C# Action");
    }

    /// <summary>
    /// Lifecycle: when `impl.Handler` is null, the getter returns a buffer with
    /// fnPtr=0, and Swift treats the property as `nil`, so `delegate?.handler?()`
    /// is a no-op — no GCHandle is allocated and no thunk fires.
    /// </summary>
    public void TestCallbackRouter_InvokeHandler_NoOpWhenImplHandlerIsNil()
    {
        var impl = new TestCallbackDelegate();
        // Leave impl.Handler null.
        var proxy = new HasCallbackDelegateProxy(impl);

        var router = new CallbackRouter();
        router.Delegate = proxy;

        router.InvokeHandler();

        AssertEqual(0, impl.CSharpHandlerFiredCount, "InvokeHandler is a no-op when the C# impl handler is null");
        TestLogger.Info("Shape 3: getter signalled nil correctly when impl.Handler was null");
    }

    /// <summary>
    /// Round-trip: Swift assigns its own closure into the C# impl, then the C#
    /// side invokes the captured Action AND the same impl exposes its own Action
    /// to Swift through `InvokeHandler`. Mixed-direction sentinel.
    /// </summary>
    public void TestCallbackRouter_BothDirectionsRoundTrip()
    {
        var impl = new TestCallbackDelegate();
        var proxy = new HasCallbackDelegateProxy(impl);

        var router = new CallbackRouter();
        router.Delegate = proxy;

        // Step 1: Swift→C# setter. Stash a Swift closure in impl.Handler.
        router.SetHandlerFromSwift(toNil: false);
        AssertNotNull(impl.Handler, "Swift→C# setter stored a managed Action");
        AssertEqual(0, router.SwiftHandlerFiredCount, "Swift closure has not yet fired");

        // Step 2: C# invokes the stored (Swift-backed) Action.
        impl.Handler!.Invoke();
        AssertEqual(1, router.SwiftHandlerFiredCount, "C# invoking stored Action fires the Swift closure body");

        // Step 3: Now replace impl.Handler with a pure C# Action and let Swift
        // invoke it through the getter path.
        impl.SetCSharpAction();
        AssertEqual(0, impl.CSharpHandlerFiredCount, "C# action fire count starts at zero after overwrite");

        router.InvokeHandler();
        AssertEqual(1, impl.CSharpHandlerFiredCount, "Swift's InvokeHandler called the C# Action via the getter path");
        TestLogger.Info("Shape 3: bidirectional round-trip completed (Swift→C# setter + C#→Swift getter)");
    }

    #endregion

    #region Closure-Returning Method (Shape 4)

    /// <summary>
    /// Drives the Swift→C# closure-returning method dispatch. Swift calls
    /// <c>delegate?.makeHandler()</c>, the proxy receiver invokes the C# impl,
    /// pins the returned Action via GCHandle, and surfaces (fnPtr, ctx) to Swift.
    /// Swift materialises that pair into a real <c>() -> Void</c> closure and
    /// fires it once, which should call back into the C# Action via the
    /// per-method cdecl thunk.
    /// </summary>
    public void TestHandlerFactoryRouter_FetchAndInvokeHandler_FiresCSharpAction()
    {
        var impl = new TestHandlerFactoryDelegate();
        var proxy = new HandlerFactoryDelegateProxy(impl);

        var router = new HandlerFactoryRouter();
        router.Delegate = proxy;

        AssertEqual(0, impl.FactoryCallCount, "Pre-condition: factory has not been driven");
        AssertEqual(0, impl.CSharpHandlerFiredCount, "Pre-condition: C# Action has not fired");

        router.FetchAndInvokeHandler();

        AssertEqual(1, impl.FactoryCallCount, "Swift drove the factory exactly once");
        AssertEqual(1, impl.CSharpHandlerFiredCount, "Swift invoking the materialised closure fired the C# Action exactly once");
        AssertEqual(1, router.LastHandlerFiredCount, "Swift's post-invoke counter incremented");
        TestLogger.Info("Shape 4: Swift→C# closure-returning dispatch round-tripped through cdecl thunk");
    }

    /// <summary>
    /// Lifetime sentinel: matches the Optional&lt;Closure&gt; reabstraction trap
    /// the Optional<Closure> dispatch fixtures hit. The materialised closure must survive past the Swift
    /// extension frame that produced it — `fetchHoldAndFireLater()` captures
    /// the closure into a local, then fires it later. If the GCHandle/box
    /// lifetime is broken, this segfaults.
    /// </summary>
    public void TestHandlerFactoryRouter_FetchHoldAndFireLater_Survives()
    {
        var impl = new TestHandlerFactoryDelegate();
        var proxy = new HandlerFactoryDelegateProxy(impl);

        var router = new HandlerFactoryRouter();
        router.Delegate = proxy;

        router.FetchHoldAndFireLater();

        AssertEqual(1, impl.FactoryCallCount, "Factory was driven exactly once");
        AssertEqual(1, impl.CSharpHandlerFiredCount, "C# Action fired even after the Swift extension frame returned");
        AssertEqual(1, router.LastHandlerFiredCount, "Post-invoke counter incremented");
        TestLogger.Info("Shape 4: held-and-fired-later did not crash — GCHandle/box lifetime is sound");
    }

    /// <summary>
    /// Each Swift `makeHandler()` call should drive a *fresh* C# invocation
    /// of MakeHandler — the path must not cache or single-shot the closure.
    /// This is the Shape 4 analogue of Shape 3's repeat-invoke assertion.
    /// </summary>
    public void TestHandlerFactoryRouter_MultipleFetches_FreshActionsEachCall()
    {
        var impl = new TestHandlerFactoryDelegate();
        var proxy = new HandlerFactoryDelegateProxy(impl);

        var router = new HandlerFactoryRouter();
        router.Delegate = proxy;

        router.FetchAndInvokeHandler();
        router.FetchAndInvokeHandler();
        router.FetchAndInvokeHandler();

        AssertEqual(3, impl.FactoryCallCount, "Each Swift call drove a fresh C# MakeHandler invocation");
        AssertEqual(3, impl.CSharpHandlerFiredCount, "Each materialised closure fired the C# Action");
        AssertEqual(3, router.LastHandlerFiredCount, "Swift's post-invoke counter saw all three calls");
        TestLogger.Info("Shape 4: repeated factory drives produce independent closures");
    }

    #endregion

    #region Async Closure Parameter

    /// <summary>
    /// Swift's <c>fireRunAsync</c> builds a Swift async closure that returns a
    /// fixed Int32 and passes it through the protocol delegate. The C# proxy
    /// receiver wraps the Swift closure pair (fnPtr, ctx) into a
    /// <c>Func&lt;Task&lt;int&gt;&gt;</c> backed by a TaskCompletionSource bridge,
    /// then calls <c>impl.RunAsync(handler)</c>. The C# impl awaits the handler
    /// and observes the Int32 the Swift closure produced.
    /// </summary>
    public void TestAsyncIntRouter_FireRunAsync_AwaitedHandlerReturnsInt32()
    {
        var impl = new TestAsyncIntDelegate();
        var proxy = new AsyncIntDelegateProxy(impl);

        var router = new AsyncIntRouter();
        router.Delegate = proxy;

        router.FireRunAsync(42);

        AssertEqual(1, impl.RunAsyncCallCount, "Swift drove RunAsync exactly once");
        AssertEqual(42, impl.LastObservedValue, "C# awaited the Swift async closure and observed the returned Int32");
        AssertEqual(42, router.LastValueProduced, "Swift's lastValueProduced reflects the closure body ran");
        TestLogger.Info("Async closure dispatch round-tripped through the TCS bridge");
    }

    /// <summary>
    /// Repeat invocation should drive a fresh async-closure pair each time —
    /// the TaskCompletionSource bridge and Swift @_cdecl Task spawner must not
    /// cache or single-shot. Three calls should produce three independent
    /// observable values on the C# side.
    /// </summary>
    public void TestAsyncIntRouter_FireRunAsync_RepeatInvocation_FreshClosuresEachCall()
    {
        var impl = new TestAsyncIntDelegate();
        var proxy = new AsyncIntDelegateProxy(impl);

        var router = new AsyncIntRouter();
        router.Delegate = proxy;

        router.FireRunAsync(1);
        AssertEqual(1, impl.LastObservedValue, "First Swift fire awaited and observed value 1");

        router.FireRunAsync(2);
        AssertEqual(2, impl.LastObservedValue, "Second Swift fire awaited and observed value 2");

        router.FireRunAsync(3);
        AssertEqual(3, impl.LastObservedValue, "Third Swift fire awaited and observed value 3");

        AssertEqual(3, impl.RunAsyncCallCount, "All three Swift drives reached the C# RunAsync impl");
        AssertEqual(3, router.LastValueProduced, "Swift's lastValueProduced reflects the most recent closure body");
        TestLogger.Info("Repeated async-closure drives produce independent TCS bridges");
    }

    /// <summary>
    /// The handler captured in <c>LastHandler</c> after the Swift call returned
    /// is a <c>Func&lt;Task&lt;int&gt;&gt;</c> backed by a <c>SwiftEscapingClosure</c>
    /// wrapper that holds an ARC retain on the Swift closure context. The handler
    /// reference must survive the Swift frame return without premature collection.
    /// </summary>
    public void TestAsyncIntRouter_FireRunAsync_HandlerReferenceSurvivesSwiftReturn()
    {
        var impl = new TestAsyncIntDelegate();
        var proxy = new AsyncIntDelegateProxy(impl);

        var router = new AsyncIntRouter();
        router.Delegate = proxy;

        router.FireRunAsync(99);

        AssertNotNull(impl.LastHandler, "Captured handler reference is non-null after Swift frame return");
        AssertEqual(99, impl.LastObservedValue, "Observed value matches what Swift's closure returned");
        TestLogger.Info("Handler reference held by C# impl outlives Swift extension frame");
    }

    #endregion

    #region Multi-Shape Composite (regression sentinel)

    // A single delegate composing every supported closure/property/method shape —
    // mirrors the richness of real consumer protocols (Nuke ImagePipelineDelegate,
    // BlinkIDUX CameraModel). Every member must dispatch through a real vtable
    // receiver, not an `EveryProtocol: closure method` fatalError stub. If any
    // shape regresses to the fatalError path, the corresponding test crashes the
    // Swift frame and surfaces a hard failure here.

    /// <summary>
    /// Drives Swift → C# read of the blittable Int32 property. The proxy
    /// receiver loads the value from the C# impl and returns it through the
    /// EveryProtocol witness table.
    /// </summary>
    public void TestMultiShapeRouter_ReadPipelineState_ReachesInt32Getter()
    {
        var impl = new TestMultiShapeDelegate(initialPipelineState: 17);
        var proxy = new MultiShapeDelegateProxy(impl);

        var router = new MultiShapeRouter();
        router.Delegate = proxy;

        var read = router.ReadPipelineState();

        AssertEqual(17, read, "Swift readPipelineState saw the value the C# impl reported");
        AssertTrue(impl.PipelineStateGetCallCount >= 1, "Int32 getter receiver was driven at least once");
    }

    /// <summary>
    /// Drives Swift → C# write of the blittable Bool property. The proxy
    /// receiver reads the incoming value through valuePtr and forwards it
    /// to the C# impl's setter.
    /// </summary>
    public void TestMultiShapeRouter_ToggleTorch_ReachesBoolSetter()
    {
        var impl = new TestMultiShapeDelegate();
        var proxy = new MultiShapeDelegateProxy(impl);

        var router = new MultiShapeRouter();
        router.Delegate = proxy;

        AssertFalse(impl.IsTorchEnabled, "Torch starts disabled on the C# impl");

        router.ToggleTorch(true);
        AssertTrue(impl.IsTorchEnabled, "Swift → C# setter routed the Bool true to the impl");
        AssertEqual(1, impl.IsTorchEnabledSetCallCount, "Bool setter receiver fired once");

        router.ToggleTorch(false);
        AssertFalse(impl.IsTorchEnabled, "Swift → C# setter routed the Bool false on second call");
        AssertEqual(2, impl.IsTorchEnabledSetCallCount, "Bool setter receiver fired again");
    }

    /// <summary>
    /// Drives Swift → C# read of the Optional&lt;Closure&gt; property via the
    /// getter receiver. Swift then invokes the closure, which marshals back into
    /// the stored C# Action and increments the local fire counter.
    /// </summary>
    public void TestMultiShapeRouter_DrivePipelineStateChange_ReachesOptionalClosureGetter()
    {
        var impl = new TestMultiShapeDelegate();
        var proxy = new MultiShapeDelegateProxy(impl);

        var router = new MultiShapeRouter();
        router.Delegate = proxy;

        int csFireCount = 0;
        impl.OnPipelineStateChange = () => csFireCount += 1;

        AssertEqual(0, router.PipelineStateChangeFireCount, "Swift fire counter starts at zero");

        router.DrivePipelineStateChange();

        AssertTrue(impl.OnPipelineStateChangeGetCallCount >= 1, "Optional closure getter receiver was driven");
        AssertEqual(1, csFireCount, "Swift materialised the closure and invoked the C# Action");
        AssertEqual(1, router.PipelineStateChangeFireCount, "Swift's post-invoke counter incremented");
    }

    /// <summary>
    /// Drives Swift → C# write of the Optional&lt;Closure&gt; property to non-nil
    /// then to nil through the proxy setter receiver. Mirrors the single-shape
    /// CallbackRouter.SetHandlerFromSwift pattern so the composite sentinel
    /// actually observes the multi-shape proxy setter dispatch, not direct C#
    /// property assignment.
    /// </summary>
    public void TestMultiShapeRouter_OptionalClosureProperty_NilAndNonNilSetterRoundTrip()
    {
        var impl = new TestMultiShapeDelegate();
        var proxy = new MultiShapeDelegateProxy(impl);

        var router = new MultiShapeRouter();
        router.Delegate = proxy;

        int setterCountBefore = impl.OnPipelineStateChangeSetCallCount;
        router.SetOnPipelineStateChangeFromSwift(toNil: false);
        AssertNotNull(impl.OnPipelineStateChange, "Swift assigned a non-nil Swift closure → managed Action on the impl");
        AssertTrue(impl.OnPipelineStateChangeSetCallCount > setterCountBefore, "Proxy setter receiver fired for the non-nil Swift assignment");

        int setterCountMid = impl.OnPipelineStateChangeSetCallCount;
        router.SetOnPipelineStateChangeFromSwift(toNil: true);
        AssertNull(impl.OnPipelineStateChange, "Swift assigning nil clears the managed handler on the impl");
        AssertTrue(impl.OnPipelineStateChangeSetCallCount > setterCountMid, "Proxy setter receiver fired again for the nil assignment");
    }

    /// <summary>
    /// Drives Swift → C# closure-returning method (Shape 4). The proxy receiver
    /// invokes the C# impl's MakePipelineStateReader, captures the returned
    /// Action, marshals it back as a Swift <c>() -> Void</c>, and Swift calls it.
    /// </summary>
    public void TestMultiShapeRouter_DriveReadViaFactory_ReachesClosureReturningMethod()
    {
        var impl = new TestMultiShapeDelegate(initialPipelineState: 5);
        var proxy = new MultiShapeDelegateProxy(impl);

        var router = new MultiShapeRouter();
        router.Delegate = proxy;

        router.DriveReadViaFactory();

        AssertEqual(1, impl.MakePipelineStateReaderCallCount, "Swift drove the closure factory exactly once");
        AssertEqual(6, router.LastReadPipelineState, "C# factory closure incremented PipelineState then Swift re-read 6");
    }

    /// <summary>
    /// Drives Swift → C# async-closure parameter (Shape 2). Swift builds a
    /// <c>() async -&gt; Int32</c> producing the supplied value, the proxy
    /// receiver bridges it to <c>Func&lt;Task&lt;int&gt;&gt;</c>, and the C# impl
    /// awaits it.
    /// </summary>
    public void TestMultiShapeRouter_DriveDiagnostics_ReachesAsyncClosureReceiver()
    {
        var impl = new TestMultiShapeDelegate();
        var proxy = new MultiShapeDelegateProxy(impl);

        var router = new MultiShapeRouter();
        router.Delegate = proxy;

        router.DriveDiagnostics(31);

        AssertEqual(1, impl.RunDiagnosticsAsyncCallCount, "Swift drove the async closure receiver exactly once");
        AssertEqual(31, impl.LastObservedAsyncValue, "C# awaited the Swift async closure and observed the Int32 it produced");
        AssertEqual(31, router.LastAsyncValue, "Swift's post-await counter saw the closure body ran");
    }

    /// <summary>
    /// Drives Swift → C# throwing-closure parameter (Shape 1) twice — once for
    /// the success branch and once for the failure branch. The proxy receiver
    /// wraps the Swift closure as <c>Func&lt;int, SwiftResult&lt;int, SwiftError&gt;&gt;</c>;
    /// the C# impl invokes it and the result reflects whether Swift threw.
    /// </summary>
    public unsafe void TestMultiShapeRouter_DriveProcessThrowing_BothBranchesObservable()
    {
        var impl = new TestMultiShapeDelegate();
        var proxy = new MultiShapeDelegateProxy(impl);

        var router = new MultiShapeRouter();
        router.Delegate = proxy;

        router.DriveProcessThrowing(false);
        AssertEqual(1, impl.ProcessPipelineStateThrowingCallCount, "Swift drove the throwing receiver once for success branch");
        AssertEqual(true, impl.LastThrowingResultIsSuccess, "Success branch produced a SwiftResult.IsSuccess=true");
        AssertEqual(11, impl.LastThrowingSuccessValue, "Success-branch closure returned v &+ 1 for v=10");

        router.DriveProcessThrowing(true);
        AssertEqual(2, impl.ProcessPipelineStateThrowingCallCount, "Swift drove the throwing receiver again for failure branch");
        AssertEqual(false, impl.LastThrowingResultIsSuccess, "Failure branch produced a SwiftResult.IsSuccess=false");
        AssertTrue(impl.LastThrowingFailureHadNonNullError, "Failure branch produced a SwiftError with a non-null Value (retained Swift error pointer)");
    }

    #endregion
}
