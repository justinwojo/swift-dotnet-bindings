// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Concurrency;

/// <summary>
/// Stress tests for concurrent access, rapid allocation/deallocation,
/// and GC pressure during active Swift interop calls.
/// All tests are Tier 3 (nightly).
/// </summary>
// Nightly-only: stress tests for concurrent access, rapid alloc/dealloc, GC pressure
[Slow]
public class StressTests : TestBase
{
    public StressTests(TestResults results) : base(results) { }

    #region Parallel Method Calls

    public void TestParallelDescribeOnSameAnimal()
    {
        // 10 threads calling Describe() on the same Animal — no corruption
        var animal = TestLibFunctions.CreateAnimal("Shared", "Bark");
        var errors = new List<string>();
        var lockObj = new object();

        var threads = new Thread[10];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                try
                {
                    for (int j = 0; j < 50; j++)
                    {
                        var result = animal.GetDescribe();
                        if (result == null || !result.Contains("Shared"))
                        {
                            lock (lockObj)
                            {
                                errors.Add($"Unexpected result: {result}");
                            }
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        errors.Add($"Exception: {ex.Message}");
                    }
                }
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(10));

        AssertTrue(errors.Count == 0,
            errors.Count > 0 ? $"Parallel errors: {string.Join("; ", errors)}" : "No errors");

        TestLogger.Info("10 threads x 50 calls on same Animal completed without corruption");
    }

    public void TestParallelSpeakOnSameAnimal()
    {
        var animal = TestLibFunctions.CreateAnimal("Speaker", "Woof");
        var errors = new List<string>();
        var lockObj = new object();

        var threads = new Thread[10];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                try
                {
                    for (int j = 0; j < 50; j++)
                    {
                        var result = animal.GetSpeak();
                        if (result == null || !result.Contains("Woof"))
                        {
                            lock (lockObj)
                            {
                                errors.Add($"Unexpected speak: {result}");
                            }
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        errors.Add($"Exception: {ex.Message}");
                    }
                }
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(10));

        AssertTrue(errors.Count == 0,
            errors.Count > 0 ? $"Parallel speak errors: {string.Join("; ", errors)}" : "No errors");

        TestLogger.Info("10 threads x 50 Speak() calls completed without corruption");
    }

    #endregion

    #region Parallel Property Access

    public void TestParallelPropertyReadOnSameAnimal()
    {
        var animal = TestLibFunctions.CreateAnimal("PropertyTest", "Sound");
        var errors = new List<string>();
        var lockObj = new object();

        var threads = new Thread[10];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                try
                {
                    for (int j = 0; j < 50; j++)
                    {
                        var name = animal.Name.ToString();
                        if (name != "PropertyTest")
                        {
                            lock (lockObj)
                            {
                                errors.Add($"Unexpected name: {name}");
                            }
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        errors.Add($"Exception: {ex.Message}");
                    }
                }
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(10));

        AssertTrue(errors.Count == 0,
            errors.Count > 0 ? $"Parallel property errors: {string.Join("; ", errors)}" : "No errors");

        TestLogger.Info("10 threads x 50 property reads completed without corruption");
    }

    #endregion

    #region Parallel Object Creation

    public void TestParallelAnimalCreation()
    {
        // 10 threads each creating 100 Animals — no crash
        var errors = new List<string>();
        var lockObj = new object();
        var totalCreated = 0;

        var threads = new Thread[10];
        for (int i = 0; i < threads.Length; i++)
        {
            var threadIndex = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    for (int j = 0; j < 100; j++)
                    {
                        var animal = TestLibFunctions.CreateAnimal(
                            $"T{threadIndex}A{j}", $"S{threadIndex}");
                        var name = animal.Name.ToString();
                        if (name != $"T{threadIndex}A{j}")
                        {
                            lock (lockObj)
                            {
                                errors.Add($"Thread {threadIndex}: Expected T{threadIndex}A{j}, got {name}");
                            }
                            return;
                        }
                        Interlocked.Increment(ref totalCreated);
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        errors.Add($"Thread {threadIndex}: {ex.Message}");
                    }
                }
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30));

        AssertEqual(1000, totalCreated, "All 1000 animals created");
        AssertTrue(errors.Count == 0,
            errors.Count > 0 ? $"Creation errors: {string.Join("; ", errors.Take(5))}" : "No errors");

        TestLogger.Info($"10 threads x 100 animals = {totalCreated} created without crash");
    }

    public void TestParallelUniqueResourceCreation()
    {
        var errors = new List<string>();
        var lockObj = new object();
        var totalCreated = 0;

        var threads = new Thread[10];
        for (int i = 0; i < threads.Length; i++)
        {
            var threadIndex = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    for (int j = 0; j < 100; j++)
                    {
                        var id = threadIndex * 1000 + j;
                        var resource = new UniqueResource(id);
                        if (resource.Id != id)
                        {
                            lock (lockObj)
                            {
                                errors.Add($"Thread {threadIndex}: Expected id {id}, got {resource.Id}");
                            }
                            return;
                        }
                        Interlocked.Increment(ref totalCreated);
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        errors.Add($"Thread {threadIndex}: {ex.Message}");
                    }
                }
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30));

        AssertEqual(1000, totalCreated, "All 1000 resources created");
        AssertTrue(errors.Count == 0,
            errors.Count > 0 ? $"Resource creation errors: {string.Join("; ", errors.Take(5))}" : "No errors");

        TestLogger.Info($"10 threads x 100 resources = {totalCreated} created without crash");
    }

    #endregion

    #region Rapid Alloc/Dealloc

    public void TestRapidAllocDealloc()
    {
        // Create and dispose in tight loop — 1000 iterations
        for (int i = 0; i < 1000; i++)
        {
            var animal = TestLibFunctions.CreateAnimal($"Rapid{i}", "Sound");
            _ = animal.Name.ToString();
            animal.Dispose();
        }

        // Verify system is still healthy
        var final = TestLibFunctions.CreateAnimal("AfterRapid", "OK");
        AssertEqual("AfterRapid", final.Name.ToString(), "System healthy after rapid alloc/dealloc");

        TestLogger.Info("1000 rapid alloc/dealloc cycles completed");
    }

    public void TestRapidResourceAllocDealloc()
    {
        for (int i = 0; i < 1000; i++)
        {
            var resource = new UniqueResource(i);
            _ = resource.Id;
            resource.Dispose();
        }

        var final = new UniqueResource(9999);
        AssertEqual(9999, final.Id, "System healthy after rapid resource alloc/dealloc");

        TestLogger.Info("1000 rapid UniqueResource alloc/dealloc cycles completed");
    }

    #endregion

    #region GC Pressure During Active Calls

    public void TestGCPressureDuringMethodCalls()
    {
        // Background GC thread while foreground calls methods
        var animal = TestLibFunctions.CreateAnimal("GCStress", "Roar");
        var errors = new List<string>();
        var lockObj = new object();
        var running = true;

        // Background GC pressure thread
        var gcThread = new Thread(() =>
        {
            while (running)
            {
                _ = new byte[8192];
                if (Thread.CurrentThread.ManagedThreadId % 10 == 0)
                {
                    GC.Collect(0, GCCollectionMode.Forced, false);
                }
            }
        });
        gcThread.IsBackground = true;
        gcThread.Start();

        try
        {
            // Foreground: 500 method calls under GC pressure
            for (int i = 0; i < 500; i++)
            {
                try
                {
                    var name = animal.Name.ToString();
                    if (name != "GCStress")
                    {
                        lock (lockObj)
                        {
                            errors.Add($"Iteration {i}: Expected GCStress, got {name}");
                        }
                        break;
                    }

                    var describe = animal.GetDescribe();
                    if (describe == null || !describe.Contains("GCStress"))
                    {
                        lock (lockObj)
                        {
                            errors.Add($"Iteration {i}: Describe unexpected: {describe}");
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        errors.Add($"Iteration {i}: {ex.Message}");
                    }
                    break;
                }
            }
        }
        finally
        {
            running = false;
            gcThread.Join(TimeSpan.FromSeconds(5));
        }

        AssertTrue(errors.Count == 0,
            errors.Count > 0 ? $"GC pressure errors: {string.Join("; ", errors)}" : "No errors");

        TestLogger.Info("500 method calls under GC pressure completed without corruption");
    }

    public void TestGCPressureDuringObjectCreation()
    {
        var errors = new List<string>();
        var lockObj = new object();
        var running = true;

        // Background GC pressure
        var gcThread = new Thread(() =>
        {
            while (running)
            {
                _ = new byte[8192];
                GC.Collect(0, GCCollectionMode.Forced, false);
                Thread.Sleep(1);
            }
        });
        gcThread.IsBackground = true;
        gcThread.Start();

        try
        {
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    var animal = TestLibFunctions.CreateAnimal($"GC{i}", "Test");
                    var name = animal.Name.ToString();
                    if (name != $"GC{i}")
                    {
                        lock (lockObj)
                        {
                            errors.Add($"Iteration {i}: Expected GC{i}, got {name}");
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        errors.Add($"Iteration {i}: {ex.Message}");
                    }
                    break;
                }
            }
        }
        finally
        {
            running = false;
            gcThread.Join(TimeSpan.FromSeconds(5));
        }

        AssertTrue(errors.Count == 0,
            errors.Count > 0 ? $"GC creation errors: {string.Join("; ", errors)}" : "No errors");

        TestLogger.Info("200 object creations under GC pressure completed");
    }

    #endregion

    #region Mixed Operations Stress

    public void TestMixedOperationsStress()
    {
        // Combine creates, reads, writes, disposes in parallel
        var errors = new List<string>();
        var lockObj = new object();

        var threads = new Thread[5];
        for (int i = 0; i < threads.Length; i++)
        {
            var threadIndex = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    for (int j = 0; j < 100; j++)
                    {
                        // Create
                        var animal = TestLibFunctions.CreateAnimal(
                            $"Mix{threadIndex}_{j}", "Sound");

                        // Read property
                        var name = animal.Name.ToString();

                        // Write property
                        animal.Name = new SwiftString($"Modified{threadIndex}_{j}");

                        // Read again
                        var modified = animal.Name.ToString();
                        if (modified != $"Modified{threadIndex}_{j}")
                        {
                            lock (lockObj)
                            {
                                errors.Add($"T{threadIndex}I{j}: Expected Modified{threadIndex}_{j}, got {modified}");
                            }
                            return;
                        }

                        // Call method
                        var describe = animal.GetDescribe();
                        if (describe == null)
                        {
                            lock (lockObj)
                            {
                                errors.Add($"T{threadIndex}I{j}: Describe returned null");
                            }
                            return;
                        }

                        // Dispose every other iteration
                        if (j % 2 == 0)
                        {
                            animal.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        errors.Add($"Thread {threadIndex}: {ex.Message}");
                    }
                }
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30));

        AssertTrue(errors.Count == 0,
            errors.Count > 0 ? $"Mixed ops errors: {string.Join("; ", errors.Take(5))}" : "No errors");

        TestLogger.Info("5 threads x 100 mixed operations completed without errors");
    }

    #endregion

    #region Async Closure Leak Bound (Session D)

    /// <summary>
    /// Validates the leak-based lifetime model documented in
    /// <c>AsyncClosureHelper</c> doesn't grow pathologically under realistic use.
    /// Per-invocation the helper leaks one <c>AsyncThrowingClosureState&lt;T&gt;</c>
    /// plus its <c>GCHandle</c> — bounded by invocation count, not time. 10K tight-
    /// loop calls must stay well under 100MB of managed heap growth.
    /// </summary>
    public async Task TestAsyncClosureLeakBoundUnderTenThousandInvocations()
    {
        const int iterations = 10_000;
        const long maxGrowthBytes = 100L * 1024 * 1024;

        // Warm up + baseline after a full GC so we don't blame the bridge for
        // unrelated prior-test allocations.
        for (int i = 0; i < 16; i++)
        {
            await Functions.CallAsyncThrowingClosureAsync(() => Task.FromResult(i));
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(forceFullCollection: true);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int local = i;
            var result = await Functions.CallAsyncThrowingClosureAsync(
                () => Task.FromResult(local));
            if (result != local)
                throw new AssertionException(
                    $"Invocation {local} returned {result} (expected {local})");
        }
        sw.Stop();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long after = GC.GetTotalMemory(forceFullCollection: true);
        long growth = after - baseline;

        TestLogger.Info(
            $"AsyncClosure leak bound: {iterations} invocations in {sw.ElapsedMilliseconds}ms, "
            + $"managed heap grew {growth:N0} bytes (baseline {baseline:N0} -> {after:N0})");

        AssertTrue(growth < maxGrowthBytes,
            $"Managed heap growth {growth:N0} bytes exceeds 100MB cap after {iterations} async-closure invocations");
    }

    #endregion
}
