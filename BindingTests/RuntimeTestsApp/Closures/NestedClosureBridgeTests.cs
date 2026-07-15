// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Runtime tests for NestedClosureBridge multi-outer-closure support. Covers the
/// single-wrapper path where one Swift wrapper handles N outer closures, each with
/// a nested inner completion. Before #10, NCB rejected any method with more than
/// one outer closure.
/// </summary>
public class NestedClosureBridgeTests : TestBase
{
    public NestedClosureBridgeTests(TestResults results) : base(results) { }

    public void TestRunOneOuterInvoked()
    {
        var host = new NestedClosureHost();
        int outerArg = -1;
        bool innerSeen = false;
        host.RunOne((arg, inner) =>
        {
            outerArg = arg;
            innerSeen = inner != null;
        });
        AssertEqual(7, outerArg, "RunOne outer received arg=7");
        AssertTrue(innerSeen, "RunOne outer received non-null inner");
    }

    public void TestRunTwoBothOutersInvokedInOrder()
    {
        var host = new NestedClosureHost();
        var order = new List<int>();
        host.RunTwo(
            first: (arg, _) => order.Add(arg),
            second: (arg, _) => order.Add(arg));
        AssertEqual(2, order.Count, "RunTwo invoked both outers");
        AssertEqual(10, order[0], "RunTwo first outer received arg=10");
        AssertEqual(20, order[1], "RunTwo second outer received arg=20");
    }

    public void TestRunThreeAllOutersInvokedInOrder()
    {
        var host = new NestedClosureHost();
        var order = new List<int>();
        host.RunThree(
            first: (arg, _) => order.Add(arg),
            second: (arg, _) => order.Add(arg),
            third: (arg, _) => order.Add(arg));
        AssertEqual(3, order.Count, "RunThree invoked all three outers");
        AssertEqual(100, order[0], "RunThree first outer received arg=100");
        AssertEqual(200, order[1], "RunThree second outer received arg=200");
        AssertEqual(300, order[2], "RunThree third outer received arg=300");
    }

    private const int GcCycles = 6;
    private const int CanaryRounds = 10;
    private const int MaxResidualCanaries = 2;

    private static void ForceGc()
    {
        // Run the collection on a worker thread so this frame's conservative stack
        // roots (Mono) can't pin the objects under test.
        var worker = new System.Threading.Thread(ForceGcWorker) { IsBackground = true };
        worker.Start();
        worker.Join();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ForceGcWorker()
    {
        for (int i = 0; i < GcCycles; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// Core lifetime regression for the escaping inner-closure box: each call leaves the
    /// canary alive while the managed inner delegate is reachable (the Swift-side +1 box
    /// transferred to the delegate's finalizable owner), and once the delegates are dropped
    /// and finalized the count must return to baseline. Pre-fix the +1 was never released,
    /// so the count grew by one per call forever.
    /// </summary>
    public void TestEscapingInnerBoxReleasedAfterDelegateCollected()
    {
        ForceGc(); // settle owners left over from other tests
        int baseline = NestedClosureHost.GetCountLiveInnerCanaries();

        RunCanaryRoundsAndDropDelegates(out int innerInvocations);
        AssertEqual(CanaryRounds, innerInvocations, "Every round invoked its inner completion");
        AssertEqual(baseline + CanaryRounds, NestedClosureHost.GetCountLiveInnerCanaries(),
            "While the inner delegates are reachable, every canary must stay alive (the +1 box is held, not prematurely released)");

        ForceGc();
        int residual = NestedClosureHost.GetCountLiveInnerCanaries() - baseline;
        AssertTrue(residual <= MaxResidualCanaries,
            $"Escaping inner boxes must be released once their delegates are collected; got {residual} residual canaries after GC (tolerance {MaxResidualCanaries} for conservative stack scanning)");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunCanaryRoundsAndDropDelegates(out int innerInvocations)
    {
        var host = new NestedClosureHost();
        int invocations = 0;
        var held = new List<Action<int>>();
        for (int i = 0; i < CanaryRounds; i++)
        {
            host.RunEscapingInnerCanary((arg, inner) =>
            {
                inner(arg); // exercise the box while the owner pins it
                invocations++;
                held.Add(inner); // keep the delegate (and its adopted box) reachable
            });
        }
        innerInvocations = invocations;
        held.Clear(); // drop every inner delegate; the boxes become collectible
    }

    /// <summary>
    /// The ownership transfer must not release early: while the managed inner delegate is
    /// still reachable, a full GC must NOT drop the box, and the delegate must remain
    /// callable afterwards (use-after-free would crash or misdispatch here).
    /// </summary>
    public void TestEscapingInnerDelegateSurvivesGcAndStaysCallable()
    {
        ForceGc();
        int baseline = NestedClosureHost.GetCountLiveInnerCanaries();

        var host = new NestedClosureHost();
        Action<int>? heldInner = null;
        host.RunEscapingInnerCanary((arg, inner) => heldInner = inner);
        AssertTrue(heldInner != null, "Outer closure received its inner completion");
        AssertEqual(baseline + 1, NestedClosureHost.GetCountLiveInnerCanaries(),
            "Canary alive while the inner delegate is held");

        ForceGc();
        AssertEqual(baseline + 1, NestedClosureHost.GetCountLiveInnerCanaries(),
            "A reachable inner delegate must keep the Swift box alive across a full GC (no premature finalizer release)");

        heldInner!(42); // still dispatches into the (alive) Swift closure — no use-after-free
        GC.KeepAlive(host);
    }

    public void TestRunTwoInnerInvocationsSurvive()
    {
        var host = new NestedClosureHost();
        int invocations = 0;
        host.RunTwo(
            first: (_, inner) => { inner(111); invocations++; },
            second: (_, inner) => { inner(222); invocations++; });
        AssertEqual(2, invocations, "Both outer closures invoked their inners without crashing");
    }

    public void TestRunThreeInnerInvocationsSurvive()
    {
        var host = new NestedClosureHost();
        int invocations = 0;
        host.RunThree(
            first: (_, inner) => { inner(1); invocations++; },
            second: (_, inner) => { inner(2); invocations++; },
            third: (_, inner) => { inner(3); invocations++; });
        AssertEqual(3, invocations, "All three outer closures invoked their inners without crashing");
    }
}
