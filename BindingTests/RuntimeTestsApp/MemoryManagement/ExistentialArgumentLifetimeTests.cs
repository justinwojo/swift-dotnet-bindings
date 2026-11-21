// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// A borrowed class-bound existential argument must stay alive for the whole call. A plain C#
/// conformer reaches Swift through the proxy the binding wraps it in, and the proxy holds the
/// conformer box's only reference, so the call has to keep the proxy reachable until Swift returns:
/// keeping the user's conformer alive is not enough, because the conformer does not refer to its
/// proxy. Each conformer here forces a collection from inside the call (Swift calls back into it),
/// and Swift reports -1 when the box it borrowed was destroyed meanwhile.
///
/// <para>A Swift class conformer is boxed fresh for each call, and the borrowing callee never releases
/// that box, so the caller has to destroy it after the call: the tracked conformer must deinitialize
/// once its C# wrapper is disposed.</para>
/// </summary>
public class ExistentialArgumentLifetimeTests : TestBase
{
    public ExistentialArgumentLifetimeTests(TestResults results) : base(results) { }

    // Class-bound existential arguments have no bridge plan yet, so the direct members refuse before
    // Swift runs on the engines that mishandle a signature the planner did not lower.
    private const SwiftTransitionFacts ClassBoundShapes = SwiftTransitionFacts.LoweredSignature;

    /// <summary>A C# conformer that collects garbage while Swift is inside the call.</summary>
    private sealed class CollectingProbe : ICallLifetimeProbe
    {
        public CollectingProbe(int tag) => ProbeTag = tag;

        public int ProbeTag { get; }

        public int MidCalls { get; private set; }

        public void MidCall()
        {
            MidCalls++;
            DrainFinalizers();
        }
    }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>
    /// Runs <paramref name="call"/>, which must work wherever the engine runs its direct transition,
    /// and must refuse before Swift runs everywhere else.
    /// </summary>
    private void Run(string member, Action call)
    {
        if (SwiftTransitionGuard.IsSupported(ClassBoundShapes))
        {
            call();
            return;
        }
        SwiftTransitionUnsupportedException? refusal = null;
        try
        {
            call();
        }
        catch (SwiftTransitionUnsupportedException ex)
        {
            refusal = ex;
        }
        AssertNotNull(refusal, $"{member} must refuse on an engine that mishandles its direct transition");
        AssertTrue(refusal!.Member.Contains(member, StringComparison.Ordinal), $"refusal names the member: {refusal.Member}");
    }

    public void TestManagedConformerSurvivesACollectionDuringTheDirectCall()
    {
        var probe = new CollectingProbe(40);
        Run("ProbeTagAcrossCall", () =>
        {
            // A fresh conformer each pass, so every call builds a fresh proxy that only the call holds.
            for (int i = 0; i < 8; i++)
            {
                var fresh = new CollectingProbe(40 + i);
                AssertEqual(40 + i + 2, TestLibFunctions.ProbeTagAcrossCall(fresh, "ab", null),
                    $"pass {i}: the box Swift borrowed must survive a collection inside the call");
                AssertEqual(1, fresh.MidCalls, $"pass {i}: Swift called back into the conformer");
            }
            AssertEqual(40, TestLibFunctions.ProbeTagAcrossCall(probe, null, null), "the same conformer passed again");
        });
    }

    public void TestManagedConformerSurvivesACollectionDuringTheDirectMethodCall()
    {
        using var reader = new CallLifetimeProbeReader(1000);
        Run("CallLifetimeProbeReader.Read", () =>
        {
            for (int i = 0; i < 8; i++)
            {
                var fresh = new CollectingProbe(i);
                AssertEqual(1000 + i + 1, reader.Read(fresh, "x", null),
                    $"pass {i}: the box the method borrowed must survive a collection inside the call");
            }
        });
    }

    public void TestManagedConformerSurvivesACollectionDuringTheWrappedCall()
    {
        // The C-convention wrapper already threads a keep-alive; the parity control for the direct arm.
        for (int i = 0; i < 8; i++)
        {
            var fresh = new CollectingProbe(10 + i);
            AssertEqual(10 + i + 3, TestLibFunctions.ProbeTagAcrossCall(fresh, "abc"),
                $"pass {i}: the box the wrapper borrowed must survive a collection inside the call");
        }
    }

    public void TestSwiftConformerBoxIsReleasedAfterTheDirectCall()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        LifetimeTracker.RunIsolated(() =>
        {
            using var box = new CallLifetimeProbeBox(7);
            using var reader = new CallLifetimeProbeReader(100);
            Run("ProbeTagAcrossCall", () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    AssertEqual(7, TestLibFunctions.ProbeTagAcrossCall(box, null, null), $"function pass {i}");
                    AssertEqual(107, reader.Read(box, null, null), $"method pass {i}");
                }
            });
            LifetimeTracker.AssertLiveCount(1, "the Swift conformer is alive while C# owns it");
        });
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("each borrowed pass of a Swift conformer must destroy the box it made");
    }
}
