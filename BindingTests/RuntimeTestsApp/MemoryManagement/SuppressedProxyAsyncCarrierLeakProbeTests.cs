// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
// The test library exports a public `Type` on purpose, to prove the generator qualifies the
// BCL names it emits. That makes a bare `Type` ambiguous here, where the reflection type is meant.
using Type = System.Type;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Leak probe for the async <b>fault-path</b> result-carrier release on suppressed-proxy
/// existential returns. The Swift fixtures live in
/// <c>BindingTests/Sources/SwiftBindingsTestLib/MemoryManagement/SuppressedProxyAsyncCarrierLeak.swift</c>.
///
/// <para>
/// When an async Swift function returns a protocol existential whose universal C# proxy was
/// suppressed (the <c>Boxable</c> / <c>TrackedClassBoxable</c> <c>init()</c>-requirement shape),
/// the generated completion callback cannot marshal the result, so it faults the awaiting Task
/// with <see cref="NotSupportedException"/>. But the Swift async wrapper has already written the
/// result into the carrier via <c>initializeMemory(as: &lt;Existential&gt;.self, repeating: result,
/// count: 1)</c> — a value-witness +1 on the payload. The completion callback must release that
/// carrier +1 on the fault path BEFORE <c>SBW_Free</c> reclaims the raw allocation, or the
/// payload's retain is orphaned every call.
/// </para>
///
/// <para>
/// Each suppressed conformer embeds a <see cref="LifetimeTracker"/>-counted TrackedRef, so an
/// orphaned carrier +1 shows up as a non-zero live count after the faulting calls and a GC drain —
/// not merely "does not crash". Because every member here always faults (the proxy is suppressed),
/// the probe is deterministic: RED before the carrier-release fix (the embedded refs pin), GREEN
/// after. The trivial value-struct conformer <c>BoxableIntCell</c> the channel tests use would mask
/// the leak (its existential destroy is a no-op), so these probes vend class-backed conformers.
/// </para>
///
/// <para>
/// The dispose loops run in <c>[MethodImpl(NoInlining)]</c> async helpers so the completed (faulted)
/// state machines are collectible before the leak assertion, and each awaited call is bounded by
/// <c>WithTimeout(DefaultAsyncTimeout)</c> so a regressed completion callback fails the probe bounded
/// instead of hanging the run.
/// </para>
/// </summary>
public class SuppressedProxyAsyncCarrierLeakProbeTests : TestBase
{
    public SuppressedProxyAsyncCarrierLeakProbeTests(TestResults results) : base(results) { }

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
    /// Invokes a suppressed-proxy async existential producer through reflection and returns its Task.
    /// The producer carries <c>[Obsolete(error: true, DiagnosticId = "SB0006")]</c> (the throwing-getter
    /// surface policy compile-poisons every produce-throw member, async included), so a direct call would
    /// be a compile error. The faulting Task body is retained as the leak-correct runtime backstop, and
    /// this probe reflectively invokes exactly that backstop to observe carrier release on the fault path.
    /// The <see cref="DynamicallyAccessedMembersAttribute"/> on <paramref name="type"/> roots the public
    /// methods for NativeAOT so the device leg finds the producer instead of a trimmed null.
    /// </summary>
    private static Task InvokeFaultingProducer(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        string methodName, int arg)
    {
        var m = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"Suppressed-proxy async producer '{methodName}' not found on {type.Name} (trimmed?).");
        var ps = m.GetParameters();
        var args = new object?[ps.Length];
        args[0] = arg;
        for (int k = 1; k < ps.Length; k++)
            args[k] = ps[k].ParameterType == typeof(CancellationToken)
                ? CancellationToken.None
                : Type.Missing;
        // The producer returns a Task that faults inside the state machine (it does not throw
        // synchronously), so Invoke returns the faulted Task normally and the awaiting caller observes
        // the original NotSupportedException — not a TargetInvocationException wrapper.
        return (Task)m.Invoke(null, args)!;
    }

    /// <summary>
    /// Scalar OPAQUE existential <c>async -&gt; any Boxable</c>: the faulting completion callback must
    /// value-witness-Destroy the <c>ExistentialContainer1</c> carrier so the embedded TrackedRef's +1
    /// is released. A leak pins one ref per faulted call.
    /// </summary>
    public async Task TestAsyncScalarExistentialFaultReleasesCarrier()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        await FaultScalarAsync(50);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks(
            "async -> any Boxable fault path must release the ExistentialContainer1 carrier's +1 " +
            "(the embedded TrackedRef) before SBW_Free; a leak pins one ref per faulted call");
        TestLogger.Info("async -> any Boxable: 50 faulted awaits, all carrier refs released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task FaultScalarAsync(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            try
            {
                await WithTimeout(InvokeFaultingProducer(typeof(TestLibFunctions), "FetchTrackedBoxableScalarAsync", i), DefaultAsyncTimeout);
                throw new AssertionException(
                    "FetchTrackedBoxableScalar must fault the Task with NotSupportedException " +
                    "(BoxableProxy suppressed); no exception was thrown.");
            }
            catch (NotSupportedException)
            {
                // Expected: the suppressed-proxy arm faults after releasing the carrier.
            }
        }
    }

    /// <summary>
    /// COLLECTION existential <c>async -&gt; [any Boxable]</c>: the carrier holds a +1 on the array's
    /// copy-on-write storage backing every element. The faulting completion callback must
    /// value-witness-Destroy the <c>SwiftArray&lt;ExistentialContainer1&gt;</c> carrier, or the storage —
    /// and every embedded TrackedRef — leaks per faulted call.
    /// </summary>
    public async Task TestAsyncCollectionExistentialFaultReleasesCarrier()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int elementsPerCall = 5;
        await FaultArrayAsync(50, elementsPerCall);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks(
            "async -> [any Boxable] fault path must value-witness-Destroy the SwiftArray carrier's " +
            "retain on the CoW storage before SBW_Free; a leak pins every element per faulted call");
        TestLogger.Info($"async -> [any Boxable]: 50 faulted awaits x {elementsPerCall} elements, all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task FaultArrayAsync(int iterations, int elementsPerCall)
    {
        for (int i = 0; i < iterations; i++)
        {
            try
            {
                await WithTimeout(InvokeFaultingProducer(typeof(TestLibFunctions), "FetchTrackedBoxableArrayAsync", elementsPerCall), DefaultAsyncTimeout);
                throw new AssertionException(
                    "FetchTrackedBoxableArray must fault the Task with NotSupportedException " +
                    "(per-element BoxableProxy suppressed); no exception was thrown.");
            }
            catch (NotSupportedException)
            {
                // Expected.
            }
        }
    }

    /// <summary>
    /// DICTIONARY existential <c>async -&gt; [int, any Boxable]</c>: the carrier holds a +1 on the
    /// dictionary's copy-on-write storage backing every value. The faulting completion callback must
    /// value-witness-Destroy the <c>SwiftDictionary</c> carrier, or the storage — and every embedded
    /// TrackedRef value — leaks per faulted call. Exercises the shared collection arm
    /// (<c>BuildCollectionCarrierMarshalLines</c>) on a dictionary shape, not just an array; a
    /// <c>Set&lt;any Boxable&gt;</c> twin is infeasible since <c>Boxable</c> is not <c>Hashable</c>.
    /// </summary>
    public async Task TestAsyncDictionaryExistentialFaultReleasesCarrier()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int entriesPerCall = 5;
        await FaultDictionaryAsync(50, entriesPerCall);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks(
            "async -> [int, any Boxable] fault path must value-witness-Destroy the SwiftDictionary " +
            "carrier's retain on the CoW storage before SBW_Free; a leak pins every value per faulted call");
        TestLogger.Info($"async -> [int, any Boxable]: 50 faulted awaits x {entriesPerCall} entries, all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task FaultDictionaryAsync(int iterations, int entriesPerCall)
    {
        for (int i = 0; i < iterations; i++)
        {
            try
            {
                await WithTimeout(InvokeFaultingProducer(typeof(TestLibFunctions), "FetchTrackedBoxableDictionaryAsync", entriesPerCall), DefaultAsyncTimeout);
                throw new AssertionException(
                    "FetchTrackedBoxableDictionary must fault the Task with NotSupportedException " +
                    "(per-value BoxableProxy suppressed); no exception was thrown.");
            }
            catch (NotSupportedException)
            {
                // Expected.
            }
        }
    }

    /// <summary>
    /// OPTIONAL-of-COLLECTION existential <c>async -&gt; [any Boxable]?</c> — the already-shipped
    /// suppressed arm, kept as a regression guard. Same carrier +1 on the inner array's CoW storage;
    /// the faulting callback must Destroy the carrier before faulting.
    /// </summary>
    public async Task TestAsyncOptionalCollectionExistentialFaultReleasesCarrier()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int elementsPerCall = 5;
        await FaultOptionalArrayAsync(50, elementsPerCall);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks(
            "async -> [any Boxable]? fault path must value-witness-Destroy the carrier's retain on " +
            "the inner array's CoW storage before SBW_Free; a leak pins every element per faulted call");
        TestLogger.Info($"async -> [any Boxable]?: 50 faulted awaits x {elementsPerCall} elements, all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task FaultOptionalArrayAsync(int iterations, int elementsPerCall)
    {
        for (int i = 0; i < iterations; i++)
        {
            try
            {
                await WithTimeout(InvokeFaultingProducer(typeof(TestLibFunctions), "FetchTrackedBoxableArrayOptionalAsync", elementsPerCall), DefaultAsyncTimeout);
                throw new AssertionException(
                    "FetchTrackedBoxableArrayOptional must fault the Task with NotSupportedException " +
                    "(per-element BoxableProxy suppressed); no exception was thrown.");
            }
            catch (NotSupportedException)
            {
                // Expected.
            }
        }
    }

    /// <summary>
    /// Scalar CLASS-BOUND existential <c>async -&gt; any TrackedClassBoxable</c>: the carrier is a
    /// 16-byte <c>ClassExistentialContainer1</c> whose word 0 is a bare class reference. The faulting
    /// completion callback must release it via <c>swift_unknownObjectRelease</c> (the class-bound arm,
    /// distinct from the opaque value-witness Destroy), deallocating the cell and its embedded
    /// TrackedRef. A leak pins one ref per faulted call.
    /// </summary>
    public async Task TestAsyncClassBoundExistentialFaultReleasesCarrier()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        await FaultClassBoundScalarAsync(50);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks(
            "async -> any TrackedClassBoxable fault path must release the ClassExistentialContainer1 " +
            "carrier's class reference via unknownObjectRelease before SBW_Free; a leak pins one ref " +
            "per faulted call");
        TestLogger.Info("async -> any TrackedClassBoxable: 50 faulted awaits, all carrier refs released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task FaultClassBoundScalarAsync(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            try
            {
                await WithTimeout(InvokeFaultingProducer(typeof(TestLibFunctions), "FetchTrackedClassBoxableScalarAsync", i), DefaultAsyncTimeout);
                throw new AssertionException(
                    "FetchTrackedClassBoxableScalar must fault the Task with NotSupportedException " +
                    "(class-bound proxy suppressed); no exception was thrown.");
            }
            catch (NotSupportedException)
            {
                // Expected.
            }
        }
    }
}
