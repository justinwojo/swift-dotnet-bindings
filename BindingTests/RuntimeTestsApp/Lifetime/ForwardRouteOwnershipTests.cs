// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Ownership across each forward route a generated member can take into Swift: a native bridge, a
/// <c>@_cdecl</c> wrapper and a native thunk. Every test first asserts the route of the members it
/// calls, so a routing change fails the test instead of quietly moving it onto another route.
/// </summary>
/// <remarks>
/// <para>
/// The route probes: <see cref="SwiftBridgeProbe"/> is bridged where the platform's regeneration
/// has lowering evidence (iOS, Mac Catalyst, tvOS) and takes its <c>@_cdecl</c> wrapper elsewhere;
/// <see cref="CCallbackLedger"/> is the same shape on its wrapper everywhere; the native thunk route
/// carries free functions over plain classes (<c>forwardRoute*</c>) and members of an internal class
/// (<see cref="NativeThunkReceiver"/>).
/// </para>
/// <para>
/// Shapes a route cannot carry are not tested on it: the native thunk emitter refuses closure and
/// <c>inout</c> parameters (<c>NativeThunkEmitter.CanEmitThunk</c>), and an escaping Swift closure
/// parameter always takes the closure <c>@_cdecl</c> wrapper, so escaping-context ownership is
/// asserted on that route in <see cref="RuntimeTestsApp.Closures.CallbackAcceptanceTests"/>.
/// </para>
/// </remarks>
public unsafe class ForwardRouteOwnershipTests : TestBase
{
    private const long TrackedErrorSentinel = -424_242;

    public ForwardRouteOwnershipTests(TestResults results) : base(results) { }

    [DllImport("SwiftBindingsTestLib", EntryPoint = "bt_native_thunk_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateThunkReceiver(int child);

    [DllImport("SwiftBindingsTestLib", EntryPoint = "bt_native_thunk_set_hook", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetThunkHook(IntPtr hook);

    private static NativeThunkReceiver NewThunkReceiver()
        => (NativeThunkReceiver)SwiftObjectHelper<NativeThunkReceiver>.NewFromPayload(CreateThunkReceiver(child: 0));

    private static ImportRoute ProbeRoute => ImportRoutes.Bridged(withoutEvidence: ImportRoute.Cdecl);

    // ---- routes -----------------------------------------------------------------------------------

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(SwiftBridgeProbe))]
    private static void AssertBridgeProbeRoute(string member) => ImportRoutes.AssertRoute(typeof(SwiftBridgeProbe), member, ProbeRoute);

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(CCallbackLedger))]
    private static void AssertLedgerRoute(string member) => ImportRoutes.AssertRoute(typeof(CCallbackLedger), member, ImportRoute.Cdecl);

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(NativeThunkReceiver))]
    private static void AssertThunkReceiverRoute(string member) => ImportRoutes.AssertRoute(typeof(NativeThunkReceiver), member, ImportRoute.Thunk);

    // The module's free functions are one large class: root only the imports these tests read, as
    // rooting every non-public method of it overwhelms the NativeAOT compiler.
    [DynamicDependency("PInvoke_forwardRouteRead_81C743A7(System.Runtime.InteropServices.SafeHandle)", typeof(TestLibFunctions))]
    [DynamicDependency("PInvoke_forwardRouteReadOrThrow_0AAC4DFA(System.Runtime.InteropServices.SafeHandle,System.Int64,System.IntPtr@)", typeof(TestLibFunctions))]
    [DynamicDependency("PInvoke_forwardRouteBorrowStruct_12ABA2EC(System.Runtime.InteropServices.SafeHandle)", typeof(TestLibFunctions))]
    [DynamicDependency("PInvoke_forwardRouteConsumeStruct_C2115466(System.Runtime.InteropServices.SafeHandle)", typeof(TestLibFunctions))]
    private static void AssertFreeFunctionRoute(string member, ImportRoute expected = ImportRoute.Thunk)
    {
        var actual = ImportRoutes.RouteOfRooted(typeof(TestLibFunctions), member);
        if (actual != expected)
            throw new AssertionException($"TestLibFunctions.{member} takes the {expected} route: Expected {expected}, got {actual}");
    }

    // ---- a thrown error is released exactly once -------------------------------------------------

    /// <summary>
    /// success, tracked error, success on one receiver through the bridge, 20 times over. The error
    /// is a class holding a tracked reference, so a box the exception never releases leaves it live
    /// and a box released twice traps in the Swift runtime.
    /// </summary>
    public void TestBridgeReleasesTheThrownErrorOnce()
    {
        AssertBridgeProbeRoute("accumulate");
        RunTrackedErrorCycles("bridge", () =>
        {
            using var probe = new SwiftBridgeProbe(total: 0);
            for (int i = 1; i <= Cycles; i++)
            {
                AssertEqual(2L * i - 1, probe.Accumulate(1, v => v), $"cycle {i}: success before the error");
                AssertThrows<SwiftException>(() => probe.Accumulate(0, _ => TrackedErrorSentinel), $"cycle {i}: tracked error");
                AssertEqual(2L * i, probe.Accumulate(1, v => v), $"cycle {i}: success after the error");
            }
        });
    }

    /// <inheritdoc cref="TestBridgeReleasesTheThrownErrorOnce"/>
    public void TestCdeclWrapperReleasesTheThrownErrorOnce()
    {
        AssertLedgerRoute("accumulate");
        RunTrackedErrorCycles("cdecl", () =>
        {
            using var ledger = new CCallbackLedger(total: 0);
            for (int i = 1; i <= Cycles; i++)
            {
                AssertEqual(2L * i - 1, ledger.Accumulate(1, v => v), $"cycle {i}: success before the error");
                AssertThrows<SwiftException>(() => ledger.Accumulate(0, _ => TrackedErrorSentinel), $"cycle {i}: tracked error");
                AssertEqual(2L * i, ledger.Accumulate(1, v => v), $"cycle {i}: success after the error");
            }
        });
    }

    /// <summary>
    /// The thunk route, twice: an instance member of an internal class, and a free function over a
    /// borrowed class argument. Each throws the tracked class error between two successes.
    /// </summary>
    public void TestNativeThunkReleasesTheThrownErrorOnce()
    {
        AssertThunkReceiverRoute("addThrowing");
        AssertFreeFunctionRoute("forwardRouteReadOrThrow");
        RunTrackedErrorCycles("thunk", () =>
        {
            using var receiver = NewThunkReceiver();
            using var token = new ForwardRouteToken(value: 40);
            for (int i = 1; i <= Cycles / 2; i++)
            {
                AssertEqual(22, receiver.AddThrowing(5), $"cycle {i}: receiver success before the error");
                AssertThrows<SwiftException>(() => receiver.AddThrowing((int)TrackedErrorSentinel), $"cycle {i}: receiver tracked error");
                AssertEqual(22, receiver.AddThrowing(5), $"cycle {i}: receiver success after the error");

                AssertEqual(42L, TestLibFunctions.ForwardRouteReadOrThrow(token, 2), $"cycle {i}: function success before the error");
                AssertThrows<SwiftException>(() => TestLibFunctions.ForwardRouteReadOrThrow(token, TrackedErrorSentinel), $"cycle {i}: function tracked error");
                AssertEqual(42L, TestLibFunctions.ForwardRouteReadOrThrow(token, 2), $"cycle {i}: function success after the error");
            }
        });
    }

    private const int Cycles = 20;

    private void RunTrackedErrorCycles(string route, Action churn)
    {
        LifetimeTracker.RunWithLeakCheck(churn, $"{route}: tracked class error");
        var stats = LifetimeTracker.GetStats();
        TestLogger.Info($"{route}: tracked errors allocated {stats.allocations}, released {stats.deallocations}");
        AssertEqual(Cycles, stats.allocations, $"{route}: every tracked throw allocated one error");
        AssertEqual(Cycles, stats.deallocations, $"{route}: every thrown error was released exactly once");
    }

    // ---- borrowed self and SafeHandle arguments stay balanced ------------------------------------

    private const int BorrowCalls = 64;

    /// <summary>
    /// N calls through the bridge leave the receiver's and a class argument's Swift retain counts
    /// where they were, and each wrapper's Dispose then releases its reference at once: a
    /// DangerousAddRef without its DangerousRelease would keep the SafeHandle open past Dispose.
    /// </summary>
    public void TestBridgeKeepsSelfAndArgumentsBalanced()
    {
        AssertBridgeProbeRoute("accumulate");
        AssertBridgeProbeRoute("derive");
        var probe = new SwiftBridgeProbe(total: 0);
        var argument = new TrackedObject(objectId: 7, label: "arg");
        AssertBalanced("bridge self", probe, () => probe.Accumulate(1, v => v));
        AssertBalanced("bridge argument", argument, () => { using var derived = probe.Derive(argument, v => v + 1); });
        AssertDisposeReleasesAtOnce("bridge self", probe);
        AssertDisposeReleasesAtOnce("bridge argument", argument);
    }

    /// <inheritdoc cref="TestBridgeKeepsSelfAndArgumentsBalanced"/>
    public void TestCdeclWrapperKeepsSelfAndArgumentsBalanced()
    {
        AssertLedgerRoute("accumulate");
        AssertLedgerRoute("derive");
        var ledger = new CCallbackLedger(total: 0);
        var argument = new TrackedObject(objectId: 7, label: "arg");
        AssertBalanced("cdecl self", ledger, () => ledger.Accumulate(1, v => v));
        AssertBalanced("cdecl argument", argument, () => { using var derived = ledger.Derive(argument, v => v + 1); });
        AssertDisposeReleasesAtOnce("cdecl self", ledger);
        AssertDisposeReleasesAtOnce("cdecl argument", argument);
    }

    /// <inheritdoc cref="TestBridgeKeepsSelfAndArgumentsBalanced"/>
    public void TestNativeThunkKeepsSelfAndArgumentsBalanced()
    {
        AssertThunkReceiverRoute("add");
        AssertFreeFunctionRoute("forwardRouteRead");
        var receiver = NewThunkReceiver();
        var token = new ForwardRouteToken(value: 9);
        int deinits = TestLibFunctions.ForwardRouteTokenDeinits();
        AssertBalanced("thunk self", receiver, () => receiver.Add(1));
        AssertBalanced("thunk argument", token, () => TestLibFunctions.ForwardRouteRead(token));
        AssertDisposeReleasesAtOnce("thunk self", receiver);
        AssertDisposeReleasesAtOnce("thunk argument", token);
        AssertEqual(deinits + 1, TestLibFunctions.ForwardRouteTokenDeinits(), "the token deinits exactly once after its last release");
    }

    private void AssertBalanced(string context, ISwiftObject wrapper, Action call)
    {
        var handle = wrapper.SwiftHandle;
        var before = Arc.RetainCount(handle);
        for (int i = 0; i < BorrowCalls; i++)
            call();
        var after = Arc.RetainCount(handle);
        TestLogger.Info($"{context}: retain count {before} before and {after} after {BorrowCalls} calls");
        AssertEqual(before, after, $"{context}: {BorrowCalls} calls leave the Swift retain count unchanged");
    }

    /// <summary>
    /// Takes a reference of its own, disposes the wrapper, and requires the count to drop by exactly
    /// the wrapper's one reference right away; then releases its own.
    /// </summary>
    private void AssertDisposeReleasesAtOnce(string context, ISwiftObject wrapper)
    {
        var handle = wrapper.SwiftHandle;
        Arc.Retain(handle);
        try
        {
            var before = Arc.RetainCount(handle);
            wrapper.Dispose();
            AssertEqual(before - 1, Arc.RetainCount(handle),
                $"{context}: Dispose releases the wrapper's reference at once (every DangerousAddRef was released)");
        }
        finally
        {
            Arc.Release(handle);
        }
    }

    // ---- a nested callback collects, then calls the same route again ------------------------------

    /// <summary>
    /// Inside the bridge's C callback, a full collection runs and then a second bridged call on
    /// another receiver: the outer call must still return its own result to its own receiver, and
    /// a third call afterwards must find the callback slot as it should be.
    /// </summary>
    public void TestBridgeNestedCallbackCollectsAndCallsTheBridgeAgain()
    {
        AssertBridgeProbeRoute("accumulate");
        using var outer = new SwiftBridgeProbe(total: 100);
        using var inner = new SwiftBridgeProbe(total: 1000);
        long nested = 0;
        var result = outer.Accumulate(5, v =>
        {
            CollectNow();
            nested = inner.Accumulate(v, w => w * 2);
            return v * 3;
        });
        AssertEqual(115L, result, "outer call returns its own result");
        AssertEqual(1010L, nested, "nested call returns its own result");
        AssertEqual(115L, outer.Total, "outer receiver holds the outer delta");
        AssertEqual(1010L, inner.Total, "inner receiver holds the nested delta");
        AssertEqual(116L, outer.Accumulate(1, v => v), "the next call uses its own callback");
    }

    /// <inheritdoc cref="TestBridgeNestedCallbackCollectsAndCallsTheBridgeAgain"/>
    public void TestCdeclNestedCallbackCollectsAndCallsTheWrapperAgain()
    {
        AssertLedgerRoute("accumulate");
        using var outer = new CCallbackLedger(total: 100);
        using var inner = new CCallbackLedger(total: 1000);
        long nested = 0;
        var result = outer.Accumulate(5, v =>
        {
            CollectNow();
            nested = inner.Accumulate(v, w => w * 2);
            return v * 3;
        });
        AssertEqual(115L, result, "outer call returns its own result");
        AssertEqual(1010L, nested, "nested call returns its own result");
        AssertEqual(115L, outer.Total, "outer receiver holds the outer delta");
        AssertEqual(1010L, inner.Total, "inner receiver holds the nested delta");
        AssertEqual(116L, outer.Accumulate(1, v => v), "the next call uses its own callback");
    }

    [ThreadStatic] private static int t_hookDepth;
    private static NativeThunkReceiver? s_nestedReceiver;
    private static int s_nestedResult;
    private static string? s_nestedFailure;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void NestedThunkHook()
    {
        // The nested call runs the hook too; only the outermost one recurses.
        if (t_hookDepth > 0)
            return;
        t_hookDepth++;
        try
        {
            CollectNow();
            s_nestedResult = s_nestedReceiver!.Add(1);
        }
        catch (Exception ex)
        {
            s_nestedFailure = ex.ToString();
        }
        finally
        {
            t_hookDepth--;
        }
    }

    /// <summary>
    /// The thunk's target calls a C hook first; the hook collects and then makes a second thunk call
    /// on another receiver. The outer call must still return its own result.
    /// </summary>
    public void TestNativeThunkNestedCallbackCollectsAndCallsTheThunkAgain()
    {
        AssertThunkReceiverRoute("add");
        using var outer = NewThunkReceiver();
        using var inner = NewThunkReceiver();
        s_nestedReceiver = inner;
        s_nestedResult = 0;
        s_nestedFailure = null;
        SetThunkHook((IntPtr)(delegate* unmanaged[Cdecl]<void>)&NestedThunkHook);
        try
        {
            AssertEqual(22, outer.Add(5), "outer call returns its own result");
        }
        finally
        {
            SetThunkHook(IntPtr.Zero);
            s_nestedReceiver = null;
        }
        AssertNull(s_nestedFailure, "the nested thunk call completed");
        AssertEqual(18, s_nestedResult, "nested call returns its own result");
        AssertEqual(23, outer.Add(6), "the outer receiver keeps working");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectNow()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // ---- inout String --------------------------------------------------------------------------

    /// <summary>
    /// The failable initializer takes <c>inout String</c> and a borrowed struct and may throw. Through
    /// its route, success, throw and success must each write the string back, and only the two
    /// successes build a payload that is destroyed exactly once.
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(FailableLifetimeValue))]
    public void TestInoutStringSurvivesSuccessErrorSuccess()
    {
        ImportRoutes.AssertRoute(typeof(FailableLifetimeValue), "init", ImportRoutes.Bridged(withoutEvidence: ImportRoute.Direct));
        using var argument = new FailableLifetimeArgument(41);
        int deinits = TestLibFunctions.ReadFailablePayloadDeinits();
        int entries = TestLibFunctions.ReadFailableEntries();

        string first = "first";
        AssertTrue(FailableLifetimeValue.TryCreate(ref first, argument, 1, out var a), "first success");
        AssertEqual("failable initializer entered", first, "first success writes the string back");

        string failing = "failing";
        AssertThrows<SwiftException>(() => FailableLifetimeValue.TryCreate(ref failing, argument, 2, out _), "the throw");
        AssertEqual("failable initializer entered", failing, "the throw still writes the string back");

        string second = "second";
        AssertTrue(FailableLifetimeValue.TryCreate(ref second, argument, 1, out var b), "second success");
        AssertEqual("failable initializer entered", second, "second success writes the string back");

        AssertEqual(entries + 3, TestLibFunctions.ReadFailableEntries(), "three entries into Swift");
        AssertEqual(deinits, TestLibFunctions.ReadFailablePayloadDeinits(), "both payloads are live");
        AssertEqual(41, a!.Number, "first value");
        AssertEqual(41, b!.Number, "second value");
        a.Dispose();
        AssertEqual(deinits + 1, TestLibFunctions.ReadFailablePayloadDeinits(), "first payload destroyed once");
        b.Dispose();
        AssertEqual(deinits + 2, TestLibFunctions.ReadFailablePayloadDeinits(), "second payload destroyed once");
        a.Dispose();
        b.Dispose();
        AssertEqual(deinits + 2, TestLibFunctions.ReadFailablePayloadDeinits(), "repeated disposal is inert");
    }

    // ---- non-trivial struct by value and inout --------------------------------------------------

    /// <summary>
    /// A non-trivial, address-only struct through a <c>@_cdecl</c> initializer (one argument
    /// <c>inout</c>, one by value) and a bridged mutating method (<c>inout</c>). Each value's class
    /// field deinits exactly when its last wrapper is disposed, never earlier and never twice.
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(InoutCell_PInvoke))]
    public void TestNonTrivialStructByValueAndInoutDestroyedOnce()
    {
        ImportRoutes.AssertRoute(typeof(InoutCell_PInvoke), "init", ImportRoute.Cdecl);
        ImportRoutes.AssertRoute(typeof(InoutCell_PInvoke), "exchange", ImportRoutes.Bridged(withoutEvidence: ImportRoute.Direct));
        int start = TestLibFunctions.GetOwnershipStructDeinitCount();
        int Deinits() => TestLibFunctions.GetOwnershipStructDeinitCount() - start;

        var a = new OwnershipStructValue(1);
        var b = new OwnershipStructValue(2);
        var originalA = a;
        var cell = new InoutCell<OwnershipStructValue>(ref a, b);
        AssertEqual(2, a.Number, "the inout argument now holds the replacement");
        using (var stored = cell.Stored)
            AssertEqual(1, stored.Number, "the cell holds the original value");
        AssertEqual(0, Deinits(), "no value destroyed by the initializer");

        originalA.Dispose();
        b.Dispose();
        AssertEqual(0, Deinits(), "each value is still held: 1 by the cell, 2 by the written-back argument");

        var c = new OwnershipStructValue(3);
        var originalC = c;
        cell.Exchange(ref c);
        AssertEqual(1, c.Number, "the inout argument now holds the cell's old value");
        using (var stored = cell.Stored)
            AssertEqual(3, stored.Number, "the cell holds the exchanged value");
        AssertEqual(0, Deinits(), "no value destroyed by the exchange");

        originalC.Dispose();
        AssertEqual(0, Deinits(), "3 is still held by the cell");
        c.Dispose();
        AssertEqual(1, Deinits(), "1 destroyed once its last wrapper is gone");
        cell.Dispose();
        AssertEqual(2, Deinits(), "3 destroyed with the cell");
        a.Dispose();
        AssertEqual(3, Deinits(), "2 destroyed with the written-back argument");
        cell.Dispose();
        a.Dispose();
        c.Dispose();
        AssertEqual(3, Deinits(), "repeated disposal is inert");
    }

    /// <summary>
    /// The same struct borrowed through a native thunk and consumed through the <c>@_cdecl</c> wrapper
    /// (the thunk route declines a consuming parameter): the caller's copy survives both calls, and
    /// its field deinits exactly once when the caller disposes it.
    /// </summary>
    public void TestNonTrivialStructThroughNativeThunkDestroyedOnce()
    {
        AssertFreeFunctionRoute("forwardRouteBorrowStruct");
        AssertFreeFunctionRoute("forwardRouteConsumeStruct", ImportRoute.Cdecl);
        int start = TestLibFunctions.GetOwnershipStructDeinitCount();
        var value = new OwnershipStructValue(5);
        for (int i = 0; i < BorrowCalls; i++)
        {
            AssertEqual(5, TestLibFunctions.ForwardRouteBorrowStruct(value), $"borrow {i}");
            AssertEqual(5, TestLibFunctions.ForwardRouteConsumeStruct(value), $"consume {i}");
        }
        AssertEqual(start, TestLibFunctions.GetOwnershipStructDeinitCount(), "the caller's copy survives every call");
        AssertEqual(5, value.Number, "the caller's copy still reads");
        value.Dispose();
        AssertEqual(start + 1, TestLibFunctions.GetOwnershipStructDeinitCount(), "destroyed once with the caller's copy");
    }
}
