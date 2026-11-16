// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Callbacks Swift creates from managed delegates and invokes on its own schedule: later, from its
/// own threads, from inside another callback, after the managed side dropped the delegate. Each
/// escaping callback's context is counted through the runtime's destroy hook, so every test can
/// require exactly one destroy per construction rather than "did not crash".
/// </summary>
/// <remarks>
/// Every closure-taking member of <see cref="CallbackRelay"/> takes the closure <c>@_cdecl</c>
/// wrapper; each test asserts that before it runs and logs the route of the closure-free members
/// it calls (a native thunk where one is emitted).
/// </remarks>
public class CallbackAcceptanceTests : TestBase
{
    // Destroys per delegate, keyed weakly so the table never keeps a delegate alive.
    private static readonly ConditionalWeakTable<object, StrongBox<int>> s_destroys = new();
    private static int s_observerInstalled;

    public CallbackAcceptanceTests(TestResults results) : base(results)
    {
        if (Interlocked.Exchange(ref s_observerInstalled, 1) == 0)
            SwiftClosureContext.ContextDestroyed += OnDestroyed;
    }

    private static void OnDestroyed(IntPtr context)
    {
        var target = GCHandle.FromIntPtr(context).Target;
        if (target != null && s_destroys.TryGetValue(target, out var count))
            Interlocked.Increment(ref count.Value);
    }

    /// <summary>Counts the destroys of every context Swift builds for <paramref name="callback"/>.</summary>
    private static StrongBox<int> Watch(Delegate callback) => s_destroys.GetValue(callback, _ => new StrongBox<int>());

    /// <summary>Asserts that the closure-taking <paramref name="members"/> enter through the closure <c>@_cdecl</c> wrapper.</summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(CallbackRelay))]
    private static void AssertRelayRoutes(params string[] members)
    {
        foreach (var member in members)
            ImportRoutes.AssertRoute(typeof(CallbackRelay), member, ImportRoute.Cdecl);
    }

    /// <summary>Logs the route of the closure-free <paramref name="members"/> the test also calls.</summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(CallbackRelay))]
    private static void LogRelayRoutes(params string[] members)
    {
        foreach (var member in members)
            ImportRoutes.RouteOf(typeof(CallbackRelay), member);
    }

    private sealed class Offset
    {
        private readonly long _by;
        public Offset(long by) => _by = by;
        public long Apply(long value) => value + _by;
    }

    /// <summary>Two callbacks of one signature, in one call and in two slots: each reaches its own target.</summary>
    public void TestSameSignatureCallbacksReachTheirOwnTargets()
    {
        AssertRelayRoutes("both", "store");
        LogRelayRoutes("invoke");
        using var relay = new CallbackRelay();
        AssertEqual(11L * 1000 + 12L, relay.Both(v => v + 1, v => v + 2, 10), "both callbacks in one call");

        Func<long, long> first = new Offset(100).Apply;
        Func<long, long> second = new Offset(200).Apply;
        var firstDestroys = Watch(first);
        var secondDestroys = Watch(second);
        relay.Store(0, first);
        relay.Store(1, second);
        AssertEqual(105L, relay.Invoke(0, 5), "slot 0 reaches the first callback");
        AssertEqual(205L, relay.Invoke(1, 5), "slot 1 reaches the second callback");
        relay.ClearAll();
        AssertEqual(1, firstDestroys.Value, "first context destroyed once");
        AssertEqual(1, secondDestroys.Value, "second context destroyed once");
    }

    /// <summary>
    /// Escaping callbacks invoked from many Swift threads at once: every result is right, and after
    /// the relay drops them each context is destroyed exactly once.
    /// </summary>
    public void TestConcurrentInvocationFromSwiftThreads()
    {
        AssertRelayRoutes("store");
        LogRelayRoutes("invokeConcurrently");
        using var relay = new CallbackRelay();
        const int slots = 4, threads = 8, iterations = 500;
        var callbacks = new Func<long, long>[slots];
        var destroys = new StrongBox<int>[slots];
        for (int s = 0; s < slots; s++)
        {
            long k = s + 1;
            callbacks[s] = v => v * k + 1;
            destroys[s] = Watch(callbacks[s]);
            relay.Store(s, callbacks[s]);
        }
        long n = threads * iterations;
        for (int s = 0; s < slots; s++)
        {
            long expected = (s + 1) * (n * (n - 1) / 2) + n;
            AssertEqual(expected, relay.InvokeConcurrently(s, threads, iterations), $"slot {s}: concurrent sum");
            AssertEqual(0, destroys[s].Value, $"slot {s}: the stored context is not destroyed by invocation");
        }
        relay.ClearAll();
        for (int s = 0; s < slots; s++)
            AssertEqual(1, destroys[s].Value, $"slot {s}: destroyed exactly once");
    }

    /// <summary>
    /// A stored callback makes a bridged forward call whose C callback runs a full collection, and a
    /// second stored callback re-enters the relay to invoke the first. Both nestings return their
    /// own results, and the contexts are destroyed once each.
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(SwiftBridgeProbe))]
    public void TestReentrantCallbackCollectsInsideABridgedCall()
    {
        AssertRelayRoutes("store");
        LogRelayRoutes("invoke");
        ImportRoutes.AssertRoute(typeof(SwiftBridgeProbe), "accumulate", ImportRoutes.Bridged(withoutEvidence: ImportRoute.Cdecl));
        using var relay = new CallbackRelay();
        using var probe = new SwiftBridgeProbe(total: 10);
        long innerSeen = 0;
        Func<long, long> bridged = v => probe.Accumulate(v, w =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            innerSeen = w;
            return w * 2;
        });
        Func<long, long> reentrant = v => relay.Invoke(0, v) + 1000;
        var bridgedDestroys = Watch(bridged);
        var reentrantDestroys = Watch(reentrant);
        relay.Store(0, bridged);
        relay.Store(1, reentrant);

        AssertEqual(20L, relay.Invoke(0, 5), "stored callback -> bridged call -> collecting callback");
        AssertEqual(5L, innerSeen, "the innermost callback saw its own argument");
        AssertEqual(1026L, relay.Invoke(1, 3), "stored callback -> relay -> stored callback -> bridged call");
        AssertEqual(26L, probe.Total, "the bridged receiver holds both deltas");
        relay.ClearAll();
        AssertEqual(1, bridgedDestroys.Value, "bridged-call context destroyed once");
        AssertEqual(1, reentrantDestroys.Value, "re-entering context destroyed once");
    }

    /// <summary>
    /// Swift keeps a callback after the managed side has dropped every reference and collected: the
    /// callback still runs later with its own state, and once Swift releases it the context is
    /// destroyed exactly once and the target becomes collectable.
    /// </summary>
    public void TestStoredCallbackOutlivesItsManagedReferences()
    {
        AssertRelayRoutes("store");
        LogRelayRoutes("invoke", "clear");
        using var relay = new CallbackRelay();
        var (target, destroys) = LifetimeTracker.RunIsolated(() =>
        {
            var offset = new Offset(7);
            Func<long, long> callback = offset.Apply;
            var count = Watch(callback);
            relay.Store(0, callback);
            return (new WeakReference(offset), count);
        });
        ForceGCThorough();
        AssertTrue(target.IsAlive, "Swift's context keeps the callback's target alive");
        AssertEqual(0, destroys.Value, "nothing destroyed while Swift holds the callback");
        AssertEqual(12L, relay.Invoke(0, 5), "Swift invokes the callback after the managed side dropped it");
        relay.Clear(0);
        AssertEqual(1, destroys.Value, "destroyed exactly once when Swift releases it");
        ForceGCThorough();
        AssertFalse(target.IsAlive, "the target is collectable after Swift released the context");
        AssertEqual(long.MinValue, relay.Invoke(0, 5), "the cleared slot is empty");
    }

    /// <summary>
    /// One managed delegate wrapped many times at once from several threads: every wrapping is its
    /// own context, all of them reach the delegate, and each is destroyed exactly once.
    /// </summary>
    public void TestOneDelegateWrappedManyTimesConcurrently()
    {
        AssertRelayRoutes("store");
        LogRelayRoutes("invokeAll");
        using var relay = new CallbackRelay();
        const int threads = 8, perThread = 50;
        Func<long, long> shared = v => v + 3;
        var destroys = Watch(shared);
        using var start = new Barrier(threads);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            start.SignalAndWait();
            try
            {
                for (int i = 0; i < perThread; i++)
                    relay.Store(t * perThread + i, shared);
            }
            catch (Exception ex)
            {
                failures.Enqueue($"thread {t}: {ex}");
            }
        })).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        AssertTrue(failures.IsEmpty, string.Join("; ", failures));

        AssertEqual(threads * perThread, relay.StoredCount, "every wrapping was stored");
        AssertEqual(0, destroys.Value, "no wrapping destroyed while stored");
        AssertEqual(threads * perThread * 4L, relay.InvokeAll(1), "every wrapping reaches the delegate");
        relay.ClearAll();
        AssertEqual(threads * perThread, destroys.Value, "each wrapping destroyed exactly once");
    }

    /// <summary>
    /// A callback that throws: Swift receives an error, catches it, and the error object is gone
    /// once the catch ends; the next call through the same callback succeeds.
    /// </summary>
    public void TestThrowingCallbackErrorIsReleasedAndTheNextCallSucceeds()
    {
        AssertRelayRoutes("invokeThrowing");
        using var relay = new CallbackRelay();
        Func<long, long> throwing = v => v < 0 ? throw new InvalidOperationException("rejected") : v * 10;
        for (int i = 1; i <= 10; i++)
        {
            AssertEqual(50L, relay.InvokeThrowing(5, throwing), $"round {i}: success before the error");
            AssertEqual(-1L, relay.InvokeThrowing(-1, throwing), $"round {i}: Swift caught the error");
            AssertEqual(i, (int)relay.CaughtErrors, $"round {i}: one error per throw");
            AssertTrue(relay.LastErrorReleased, $"round {i}: the caught error was released when the catch ended");
            AssertEqual(70L, relay.InvokeThrowing(7, throwing), $"round {i}: success after the error");
        }
    }

    /// <summary>
    /// A callback that returns an error it does not own, cooperatively, several times: the
    /// callback only lends the error, so each throw hands Swift its own reference and Swift's
    /// catch releases exactly that one. The error survives every round and dies once, when its
    /// owner releases it.
    /// </summary>
    public void TestCooperativeCallbackErrorStaysWithItsOwner()
    {
        AssertRelayRoutes("invokeThrowing");
        using var relay = new CallbackRelay();
        var owned = (IntPtr)TestLibFunctions.MakeCallbackTrackedError();
        int deinits = TestLibFunctions.GetCallbackTrackedErrorDeinits();
        SwiftError error;
        unsafe { error = new SwiftError((void*)owned); }
        for (int i = 1; i <= 5; i++)
        {
            AssertEqual(-1L, relay.InvokeThrowing(5, _ => Swift.SwiftResult<long, SwiftError>.FromFailure(error)),
                $"round {i}: Swift caught the cooperative error");
            AssertEqual(i, (int)relay.CaughtErrors, $"round {i}: one error per throw");
            AssertEqual(deinits, TestLibFunctions.GetCallbackTrackedErrorDeinits(), $"round {i}: not destroyed while owned");
        }
        AssertEqual(70L, relay.InvokeThrowing(7, v => Swift.SwiftResult<long, SwiftError>.FromSuccess(v * 10)), "success after the errors");
        Arc.UnknownObjectRelease(owned);
        AssertEqual(deinits + 1, TestLibFunctions.GetCallbackTrackedErrorDeinits(), "destroyed once, by its owner's release");
    }

    /// <summary>
    /// Callback results Swift reads back in two general registers (16 bytes) and through an
    /// indirect result buffer (32 bytes).
    /// </summary>
    public void TestCallbackResultsInTwoRegistersAndIndirect()
    {
        AssertRelayRoutes("invokePair", "invokeQuad");
        using var relay = new CallbackRelay();
        AssertEqual(3L * 1000 + 4L, relay.InvokePair(3, v => new CallbackPair(v, v + 1)), "two-register result");
        AssertEqual(-9L * 1000 + 9L, relay.InvokePair(9, v => new CallbackPair(-v, v)), "two-register result, signed");
        AssertEqual(1L * 1000 + 2L * 100 + 3L * 10 + 4L,
            relay.InvokeQuad(1, v => new CallbackQuad(v, v + 1, v + 2, v + 3)), "indirect result");
    }
}
