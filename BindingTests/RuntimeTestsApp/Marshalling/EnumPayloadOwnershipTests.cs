// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Ownership of the payload a generated <c>TryGet&lt;Case&gt;</c> reads. The reader copies the enum
/// (<c>InitializeWithCopy</c>, a +1 on every reference in the payload) and never destroys the
/// copy, so each payload shape has to consume that +1 exactly once: adopt it into the value it
/// returns, or release it once the value is copied out. Every payload in
/// <c>EnumPayloadOwnership.swift</c> is counted by <see cref="LifetimeTracker"/>, so a reference the
/// reader never released stays live (a leak) and one it released too early reads back wrong or
/// crashes. Each probe reads the same enum several times, checks the values, disposes everything it
/// was handed, and then requires every counted allocation to be gone.
/// </summary>
public class EnumPayloadOwnershipTests : TestBase
{
    private const int Rounds = 40;
    private const int Reads = 3;
    private const int ByteCount = 4096;

    public EnumPayloadOwnershipTests(TestResults results) : base(results) { }

    private static void Drain()
    {
        for (int i = 0; i < 6; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.02));
        }
    }

    private void RunProbe(string shape, Func<int> churn)
    {
        Drain();
        LifetimeTracker.Reset();

        var mismatches = LifetimeTracker.RunIsolated(churn);
        Drain();

        AssertEqual(0, mismatches, $"{shape}: every read returns the payload's value");
        LifetimeTracker.AssertNoLeaks($"{shape}: TryGet must consume the enum copy's reference exactly once");
        TestLogger.Info($"{shape}: {Rounds} enums x {Reads} reads balanced");
    }

    private static bool BytesMatch(byte[]? bytes)
    {
        if (bytes is null || bytes.Length != ByteCount)
            return false;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != unchecked((byte)(i * 3 + 1)))
                return false;
        }
        return true;
    }

    public void TestArrayPayloadReadIsBalanced() => RunProbe("[TrackedRef] payload", ReadLists);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadLists()
    {
        int mismatches = 0;
        for (int i = 0; i < Rounds; i++)
        {
            using var payload = TestLibFunctions.EnumPayloadOwnershipList(i);
            for (int r = 0; r < Reads; r++)
            {
                if (!payload.TryGetList(out var list))
                {
                    mismatches++;
                    continue;
                }
                try
                {
                    if (list.Count != 2 || list[0].Tag != i || list[1].Tag != i + 1)
                        mismatches++;
                    foreach (var element in list)
                        element.Dispose();
                }
                finally
                {
                    (list as IDisposable)?.Dispose();
                }
            }
        }
        return mismatches;
    }

    public void TestOptionalPayloadReadIsBalanced() => RunProbe("TrackedRef? payload", ReadMaybes);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadMaybes()
    {
        int mismatches = 0;
        for (int i = 0; i < Rounds; i++)
        {
            using var payload = TestLibFunctions.EnumPayloadOwnershipMaybe(i);
            for (int r = 0; r < Reads; r++)
            {
                if (!payload.TryGetMaybe(out var value) || value is null || value.Tag != i)
                    mismatches++;
                value?.Dispose();
            }
        }
        return mismatches;
    }

    public void TestResultPayloadReadIsBalanced() => RunProbe("Result<TrackedRef, _> payload", ReadOutcomes);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadOutcomes()
    {
        int mismatches = 0;
        for (int i = 0; i < Rounds; i++)
        {
            using var payload = TestLibFunctions.EnumPayloadOwnershipOutcome(i);
            for (int r = 0; r < Reads; r++)
            {
                if (!payload.TryGetOutcome(out var outcome))
                {
                    mismatches++;
                    continue;
                }
                using (outcome)
                {
                    var success = outcome.IsSuccess ? outcome.Success : null;
                    if (success is null || success.Tag != i)
                        mismatches++;
                    success?.Dispose();
                }
            }
        }
        return mismatches;
    }

    public void TestDataPayloadReadIsBalanced() => RunProbe("Data payload", ReadBytes);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadBytes()
    {
        int mismatches = 0;
        for (int i = 0; i < Rounds; i++)
        {
            using var payload = TestLibFunctions.EnumPayloadOwnershipBytes(ByteCount);
            for (int r = 0; r < Reads; r++)
            {
                if (!payload.TryGetBytes(out var bytes) || !BytesMatch(bytes))
                    mismatches++;
            }
        }
        return mismatches;
    }

    public void TestObjCObjectPayloadReadIsBalanced() => RunProbe("NSObject payload", ReadObjects);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadObjects()
    {
        int mismatches = 0;
        for (int i = 0; i < Rounds; i++)
        {
            using var payload = TestLibFunctions.EnumPayloadOwnershipObject(i);
            for (int r = 0; r < Reads; r++)
            {
                if (!payload.TryGetObject(out var value) || value is null)
                {
                    mismatches++;
                    continue;
                }
                using (value)
                {
                    // The payload is typed NSObject; its Objective-C class is the Swift subclass.
                    if (!value.Class.Name.EndsWith("EnumPayloadTrackedObject", StringComparison.Ordinal))
                        mismatches++;
                }
            }
        }
        return mismatches;
    }

    public void TestOptionalObjCObjectPayloadReadIsBalanced() => RunProbe("NSObject? payload", ReadMaybeObjects);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadMaybeObjects()
    {
        int mismatches = 0;
        for (int i = 0; i < Rounds; i++)
        {
            using var payload = TestLibFunctions.EnumPayloadOwnershipMaybeObject(i);
            for (int r = 0; r < Reads; r++)
            {
                if (!payload.TryGetMaybeObject(out var value) || value is null)
                {
                    mismatches++;
                    continue;
                }
                using (value)
                {
                    if (!value.Class.Name.EndsWith("EnumPayloadTrackedObject", StringComparison.Ordinal))
                        mismatches++;
                }
            }
        }
        return mismatches;
    }

    public void TestObjCObjectArrayPayloadReadIsBalanced() => RunProbe("[NSObject] payload", ReadObjectArrays);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadObjectArrays()
    {
        int mismatches = 0;
        for (int i = 0; i < Rounds; i++)
        {
            using var payload = TestLibFunctions.EnumPayloadOwnershipObjects(i);
            for (int r = 0; r < Reads; r++)
            {
                if (!payload.TryGetObjects(out var objects))
                {
                    mismatches++;
                    continue;
                }
                if (objects.Count != 2)
                    mismatches++;
                foreach (var element in objects)
                {
                    if (element is null || !element.Class.Name.EndsWith("EnumPayloadTrackedObject", StringComparison.Ordinal))
                        mismatches++;
                    element?.Dispose();
                }
                (objects as IDisposable)?.Dispose();
            }
        }
        return mismatches;
    }

    public void TestTuplePayloadReadIsBalanced() => RunProbe("(TrackedRef?, Data) payload", ReadPairs);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadPairs()
    {
        int mismatches = 0;
        for (int i = 0; i < Rounds; i++)
        {
            using var payload = TestLibFunctions.EnumPayloadOwnershipPair(i, ByteCount);
            for (int r = 0; r < Reads; r++)
            {
                if (!payload.TryGetPair(out var reference, out var bytes))
                {
                    mismatches++;
                    continue;
                }
                if (reference is null || reference.Tag != i || !BytesMatch(bytes))
                    mismatches++;
                reference?.Dispose();
            }
        }
        return mismatches;
    }
}
