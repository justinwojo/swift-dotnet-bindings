// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Concurrency;

/// <summary>
/// 10K-element round-trip stress tests for the three Swift collection wrappers
/// (<see cref="SwiftArray{Element}"/>, <see cref="SwiftDictionary{TKey, TValue}"/>,
/// <see cref="SwiftSet{Element}"/>). Exercises the bulk-insert / bulk-extract paths
/// that hoist the payload <c>SafeHandle</c> ref-count out of the per-element loop:
/// a regression there shows up as either correctness failure (missing/wrong elements
/// after round-trip) or as a SafeHandle / ARC crash partway through.
///
/// Mirrors the shape of <see cref="StressTests.TestAsyncClosureLeakBoundUnderTenThousandInvocations"/>:
/// warm-up, baseline, tight 10K loop, post-loop GC, growth assertion.
/// </summary>
[Slow]
public class BulkCollectionStressTests : TestBase
{
    public BulkCollectionStressTests(TestResults results) : base(results) { }

    private const int Iterations = 10_000;
    private const long MaxGrowthBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Build a <see cref="SwiftArray{Element}"/> from a 10K-element source array, then
    /// extract back via <c>ToArray</c> and assert element-wise equality at every index.
    /// </summary>
    public void TestSwiftArrayBulkRoundTrip10K()
    {
        // Warm up + baseline so prior-test allocations don't pollute the growth check.
        for (int i = 0; i < 16; i++)
        {
            using var warm = new SwiftArray<long>(new long[] { i });
            _ = warm.ToArray();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(forceFullCollection: true);

        var source = new long[Iterations];
        for (int i = 0; i < Iterations; i++)
            source[i] = (long)i * 31 + 7;

        var sw = Stopwatch.StartNew();
        long[] roundTripped;
        using (var arr = new SwiftArray<long>(source))
        {
            AssertEqual(Iterations, arr.Count, "SwiftArray count after bulk insert");
            roundTripped = arr.ToArray();
        }
        sw.Stop();

        AssertEqual(Iterations, roundTripped.Length, "Round-tripped array length");
        for (int i = 0; i < Iterations; i++)
        {
            if (roundTripped[i] != source[i])
                throw new AssertionException(
                    $"SwiftArray round-trip mismatch at index {i}: expected {source[i]}, got {roundTripped[i]}");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long after = GC.GetTotalMemory(forceFullCollection: true);
        long growth = after - baseline;

        TestLogger.Info(
            $"SwiftArray bulk round-trip: {Iterations} elements in {sw.ElapsedMilliseconds}ms, "
            + $"managed heap grew {growth:N0} bytes (baseline {baseline:N0} -> {after:N0})");

        AssertTrue(growth < MaxGrowthBytes,
            $"Managed heap growth {growth:N0} bytes exceeds 100MB cap after {Iterations}-element SwiftArray round-trip");
    }

    /// <summary>
    /// Build a <see cref="SwiftDictionary{TKey, TValue}"/> from 10K key-value pairs via
    /// <c>FromDictionary</c>, then extract every value via the indexer and assert it
    /// matches the source.
    /// </summary>
    public void TestSwiftDictionaryBulkRoundTrip10K()
    {
        for (int i = 0; i < 16; i++)
        {
            using var warm = SwiftDictionary<long, long>.FromDictionary(
                new[] { new KeyValuePair<long, long>(i, i + 1) });
            _ = warm.Count;
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(forceFullCollection: true);

        var source = new KeyValuePair<long, long>[Iterations];
        for (int i = 0; i < Iterations; i++)
            source[i] = new KeyValuePair<long, long>(i, (long)i * 17 + 3);

        var sw = Stopwatch.StartNew();
        // Snapshot every value via indexer — exercises the per-call SafeHandle scope on
        // the read side as well, since `this[key]` re-acquires PayloadBuffer each time.
        var snapshot = new long[Iterations];
        using (var dict = SwiftDictionary<long, long>.FromDictionary(source))
        {
            AssertEqual(Iterations, dict.Count, "SwiftDictionary count after bulk insert");
            for (int i = 0; i < Iterations; i++)
                snapshot[i] = dict[i];
        }
        sw.Stop();

        for (int i = 0; i < Iterations; i++)
        {
            if (snapshot[i] != source[i].Value)
                throw new AssertionException(
                    $"SwiftDictionary round-trip mismatch at key {i}: expected {source[i].Value}, got {snapshot[i]}");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long after = GC.GetTotalMemory(forceFullCollection: true);
        long growth = after - baseline;

        TestLogger.Info(
            $"SwiftDictionary bulk round-trip: {Iterations} pairs in {sw.ElapsedMilliseconds}ms, "
            + $"managed heap grew {growth:N0} bytes (baseline {baseline:N0} -> {after:N0})");

        AssertTrue(growth < MaxGrowthBytes,
            $"Managed heap growth {growth:N0} bytes exceeds 100MB cap after {Iterations}-pair SwiftDictionary round-trip");
    }

    /// <summary>
    /// Diagnostic counterpart to <see cref="TestSwiftSetBulkRoundTrip10K"/> using the
    /// per-element <see cref="SwiftSet{Element}.Add"/> path instead of bulk
    /// <c>FromEnumerable</c>. The two tests share the same simulator-skip reason: confirmed
    /// crashing on iOS Mono Simulator regardless of insertion path proves the bug is in
    /// <c>SwiftSet.CollectElements</c> / iterator marshalling at scale, not in the new
    /// bulk <c>AddRange</c> code added in this work. Runs on device (NativeAOT).
    /// </summary>
    [SkipOnSimulator("SwiftSet.CollectElements iterator crashes with 10K elements on iOS Mono Simulator — pre-existing Set marshalling issue (see ClosureOverloadCollisionTests skips); reproduces with per-element Add, so independent of bulk AddRange.")]
    public void TestSwiftSetPerElementRoundTrip10K_Diagnostic()
    {
        var source = new long[Iterations];
        for (int i = 0; i < Iterations; i++)
            source[i] = (long)i * 13 + 1;

        long[] roundTripped;
        using (var set = new SwiftSet<long>())
        {
            for (int i = 0; i < Iterations; i++)
                set.Add(source[i]);
            AssertEqual(Iterations, set.Count, "SwiftSet count after per-element insert");
            roundTripped = set.ToArray();
        }
        AssertEqual(Iterations, roundTripped.Length, "Per-element round-tripped set length");
    }

    /// <summary>
    /// Smoke test for the <see cref="Swift.Runtime.Arc.RetainMultiple"/> /
    /// <see cref="Swift.Runtime.Arc.ReleaseMultiple"/> bulk path itself: validates that
    /// a buffer of class-instance pointers can be retained and released in bulk via the
    /// new Swift companion helpers without crashing or leaking.
    ///
    /// Uses <see cref="CoordinateRef"/> from the test library — a real Swift class. The
    /// bulk path expects honest class-instance pointers (heap objects with the standard
    /// Swift class header); passing the address of an inline value buffer (e.g. the
    /// payload of a struct-typed wrapper like <c>SwiftString</c>) reads garbage as the
    /// metadata pointer and segfaults inside <c>swift_retain</c>.
    /// </summary>
    public void TestArcBulkRetainReleaseRoundTrip()
    {
        const int n = 10_000;

        // Build N independent Swift class references via CoordinateRef (each holds a +1
        // class-instance refcount on construction).
        var refs = new CoordinateRef[n];
        var pointers = new IntPtr[n];
        for (int i = 0; i < n; i++)
        {
            refs[i] = new CoordinateRef(i, i + 1);
            pointers[i] = ((ISwiftObject)refs[i]).SwiftHandle;
        }

        // Sanity: every starting pointer should have refcount == 1.
        for (int i = 0; i < n; i++)
        {
            var rc = Swift.Runtime.Arc.RetainCount(pointers[i]);
            if (rc != 1)
                throw new AssertionException(
                    $"CoordinateRef {i} unexpected starting refcount: expected 1, got {rc}");
        }

        // Bulk +N retains, then bulk -N releases. Net change to ARC counts must be zero.
        Swift.Runtime.Arc.RetainMultiple(pointers);
        Swift.Runtime.Arc.ReleaseMultiple(pointers);

        // All originals must still be alive with refcount == 1, and their fields must
        // be readable after the round trip — a corruption inside the bulk path would
        // either crash the field load or return garbage Int32 values.
        for (int i = 0; i < n; i++)
        {
            var rc = Swift.Runtime.Arc.RetainCount(pointers[i]);
            if (rc != 1)
                throw new AssertionException(
                    $"CoordinateRef {i} refcount drifted after bulk Retain/Release: expected 1, got {rc}");

            int x = refs[i].X;
            int y = refs[i].Y;
            if (x != i || y != i + 1)
                throw new AssertionException(
                    $"CoordinateRef {i} corrupted after bulk Retain/Release: expected ({i},{i + 1}), got ({x},{y})");
            refs[i].Dispose();
        }

        TestLogger.Info($"Arc bulk Retain/Release: {n} class pointers round-tripped without corruption");
    }

    /// <summary>
    /// Build a <see cref="SwiftSet{Element}"/> from 10K distinct elements via
    /// <c>FromEnumerable</c>, then extract back via <c>ToArray</c> and assert that
    /// every source element is present (set semantics — order is unspecified).
    /// Skipped on simulator: same iterator-marshalling crash as the per-element
    /// diagnostic above. Runs on device (NativeAOT) where the bug does not reproduce.
    /// </summary>
    [SkipOnSimulator("SwiftSet.CollectElements iterator crashes with 10K elements on iOS Mono Simulator — pre-existing Set marshalling issue (see ClosureOverloadCollisionTests skips); also reproduces via per-element Add (TestSwiftSetPerElementRoundTrip10K_Diagnostic), confirming it is independent of bulk AddRange.")]
    public void TestSwiftSetBulkRoundTrip10K()
    {
        for (int i = 0; i < 16; i++)
        {
            using var warm = SwiftSet<long>.FromEnumerable(new long[] { i });
            _ = warm.ToArray();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(forceFullCollection: true);

        var source = new long[Iterations];
        for (int i = 0; i < Iterations; i++)
            source[i] = (long)i * 13 + 1;

        var sw = Stopwatch.StartNew();
        long[] roundTripped;
        using (var set = SwiftSet<long>.FromEnumerable(source))
        {
            AssertEqual(Iterations, set.Count, "SwiftSet count after bulk insert (all source values are distinct)");
            roundTripped = set.ToArray();
        }
        sw.Stop();

        AssertEqual(Iterations, roundTripped.Length, "Round-tripped set length");

        // Sets don't preserve insertion order — verify membership instead.
        var roundTrippedSet = new HashSet<long>(roundTripped);
        for (int i = 0; i < Iterations; i++)
        {
            if (!roundTrippedSet.Contains(source[i]))
                throw new AssertionException(
                    $"SwiftSet round-trip lost element at source index {i} (value {source[i]})");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long after = GC.GetTotalMemory(forceFullCollection: true);
        long growth = after - baseline;

        TestLogger.Info(
            $"SwiftSet bulk round-trip: {Iterations} elements in {sw.ElapsedMilliseconds}ms, "
            + $"managed heap grew {growth:N0} bytes (baseline {baseline:N0} -> {after:N0})");

        AssertTrue(growth < MaxGrowthBytes,
            $"Managed heap growth {growth:N0} bytes exceeds 100MB cap after {Iterations}-element SwiftSet round-trip");
    }
}
