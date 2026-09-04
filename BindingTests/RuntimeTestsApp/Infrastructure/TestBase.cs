// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Base class for runtime tests with common utilities.
/// </summary>
public abstract class TestBase
{
    protected TestResults Results { get; }
    protected string TestClassName { get; }

    /// <summary>
    /// True when the current process is running as Mac Catalyst on x86_64 (Rosetta
    /// on Apple Silicon, or native Intel). Used to honor [SkipOnCatalystX64] without
    /// adding a new TestPlatform enum value or CLI flag.
    /// </summary>
    private static readonly bool IsMacCatalystX64 =
        OperatingSystem.IsMacCatalyst() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>
    /// True when the current process runs on the Mono runtime — iOS/tvOS Simulator or Mac
    /// Catalyst (JIT or interpreter). These are the only configurations that can hit the upstream
    /// Mono "Issue 1" <c>!ji-&gt;async</c> JIT assertion (jit-info.c:918). False on macOS (CoreCLR)
    /// and physical device (NativeAOT), neither of which is Mono. Used to honor
    /// <see cref="SkipOnMonoJitAttribute"/> independently of the <c>--platform</c> CLI flag: the
    /// harness launches the macOS run with <c>--platform simulator</c> too, so keying a Mono-JIT
    /// skip off that flag would wrongly suppress it on CoreCLR. <c>SwiftRuntimeInfo.IsMonoRuntime</c>
    /// recognizes the Simulator (via its "simulator" RID check) but misses <c>maccatalyst-*</c>
    /// RIDs, so Catalyst is added via <see cref="OperatingSystem.IsMacCatalyst"/>.
    /// </summary>
    internal static readonly bool IsMonoJitRuntime =
        Swift.Runtime.SwiftRuntimeInfo.IsMonoRuntime || OperatingSystem.IsMacCatalyst();

    /// <summary>
    /// True when the current process runs on <b>Mono full-AOT on a physical Apple device</b> — the
    /// .NET-for-iOS default device runtime (rid <c>ios-arm64</c>/<c>tvos-arm64</c>, no
    /// <c>PublishAot</c>), reached by <c>nuke binding-tests --device --mono-aot</c>.
    ///
    /// <para>It is a third runtime, not a synonym for either neighbour:</para>
    /// <list type="bullet">
    ///   <item>It IS Mono, so <see cref="IsMonoJitRuntime"/> is also true here and every
    ///   runtime-detected Mono skip applies exactly as it does on the Simulator.</item>
    ///   <item>It is NOT the Simulator: no JIT, the app is AOT-compiled and ILLink-trimmed, so
    ///   AOT/trimming reflection behavior applies the way it does under NativeAOT.</item>
    ///   <item>It is NOT NativeAOT: the CLI-flag-keyed <c>[SkipOnDevice]</c> skips (which describe
    ///   the NativeAOT Release app) deliberately do not apply — see
    ///   <see cref="TestPlatform.DeviceMonoAot"/>.</item>
    /// </list>
    ///
    /// <para>Detected at runtime rather than from the <c>--platform</c> flag, so it stays honest if
    /// the harness ever launches the wrong bundle: it requires the live runtime to report Mono AND
    /// a non-simulator, non-Catalyst Apple RID. <c>SwiftRuntimeInfo.IsMonoRuntime</c> can only
    /// answer this correctly because the app injects the build-time
    /// <c>Swift.Runtime.IsNativeAot</c> AppContext switch (see RuntimeTestsApp.csproj); without it
    /// the heuristic cannot tell device Mono full-AOT from NativeAOT.</para>
    /// </summary>
    internal static readonly bool IsMonoAotRuntime =
        Swift.Runtime.SwiftRuntimeInfo.IsMonoRuntime
        && !OperatingSystem.IsMacCatalyst()
        && !RuntimeInformation.RuntimeIdentifier.Contains("simulator", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One-line runtime-flavor banner the host gate reads back out of the app's console output to
    /// confirm the lane it launched is the lane that actually ran.
    /// </summary>
    internal static string RuntimeFlavorDescription =>
        $"IsMonoRuntime={Swift.Runtime.SwiftRuntimeInfo.IsMonoRuntime}, " +
        $"IsNativeAotRuntime={Swift.Runtime.SwiftRuntimeInfo.IsNativeAotRuntime}, " +
        $"IsMonoAot={IsMonoAotRuntime}, Rid={RuntimeInformation.RuntimeIdentifier}";

    protected TestBase(TestResults results)
    {
        Results = results;
        TestClassName = GetType().Name;
    }

    /// <summary>
    /// Runs all test methods in this class using compile-time discovered descriptors.
    /// Class-level skip is handled by the caller (Program.cs) before instantiation.
    /// This method handles method-level skip attributes and invocation.
    /// </summary>
    public async Task RunAllTestsAsync(TestClassDescriptor descriptor, TestPlatform platform = TestPlatform.Simulator, bool flakeDetect = false)
    {
        foreach (var method in descriptor.Methods)
        {
            var testName = $"{TestClassName}.{method.Name}";

            // Check method-level [Skip] — always skipped
            if (method.Skip != null)
            {
                Results.Skip(testName, method.Skip);
                continue;
            }

            // Check method-level [SkipOnSimulator] — skipped on simulator, runs on device
            if (method.SkipOnSim != null && platform == TestPlatform.Simulator)
            {
                Results.Skip(testName, $"Simulator: {method.SkipOnSim}");
                continue;
            }

            // Check method-level [SkipOnDevice] — skipped on device, runs on simulator
            if (method.SkipOnDevice != null && platform == TestPlatform.Device)
            {
                Results.Skip(testName, $"Device: {method.SkipOnDevice}");
                continue;
            }

            // Check method-level [SkipOnMonoJit] — skipped only when running on Mono (Simulator
            // or Catalyst), where the upstream !ji->async JIT assertion (Issue 1) can fire. Runs
            // on macOS (CoreCLR) and device (NativeAOT). Detected at runtime (IsMonoJitRuntime),
            // NOT from the --platform flag, so it is not over-applied on the macOS run — which the
            // harness also launches with --platform simulator. See SkipOnMonoJitAttribute.
            if (method.SkipOnMonoJit != null && IsMonoJitRuntime)
            {
                Results.Skip(testName, $"MonoJit: {method.SkipOnMonoJit}");
                continue;
            }

            // Check method-level [SkipOnCatalystX64] — skipped on maccatalyst-x64 only.
            // Detected at runtime via OperatingSystem.IsMacCatalyst() +
            // RuntimeInformation.ProcessArchitecture so we don't need a separate
            // CLI flag / TestPlatform enum value. Runs everywhere else (including
            // osx-x64 under the same Rosetta layer, which passes cleanly).
            if (method.SkipOnCatalystX64 != null && IsMacCatalystX64)
            {
                Results.Skip(testName, $"MacCatalyst-x64: {method.SkipOnCatalystX64}");
                continue;
            }

            if (flakeDetect)
            {
                await RunTestWithFlakeDetectionAsync(method);
            }
            else
            {
                await RunTestMethodAsync(method);
            }

            // On a physical device (either runtime), yield to the iOS run loop periodically.
            if (platform is TestPlatform.Device or TestPlatform.DeviceMonoAot)
                Foundation.NSRunLoop.Current.RunUntil(Foundation.NSDate.FromTimeIntervalSinceNow(0.001));
        }
    }

    private async Task RunTestMethodAsync(TestMethodDescriptor method)
    {
        var testName = $"{TestClassName}.{method.Name}";
        TestLogger.Test($"--- {testName} ---");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await method.Invoker(this);
            stopwatch.Stop();
            Results.Pass(testName, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            TestLogger.Exception(ex, testName);
            Results.Fail(testName, ex.Message, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Runs a test method 3 times for flake detection. If results are inconsistent
    /// (some runs pass, some fail), the test is reported as FLAKY and fails the suite.
    /// </summary>
    private async Task RunTestWithFlakeDetectionAsync(TestMethodDescriptor method)
    {
        const int runs = 3;
        var testName = $"{TestClassName}.{method.Name}";
        var passCount = 0;
        var failCount = 0;
        string? lastError = null;
        var totalElapsed = TimeSpan.Zero;

        TestLogger.Test($"--- {testName} (flake detect: {runs}x) ---");

        for (int i = 0; i < runs; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await method.Invoker(this);
                stopwatch.Stop();
                totalElapsed += stopwatch.Elapsed;
                passCount++;
                TestLogger.Debug($"  Run {i + 1}/{runs}: PASS ({stopwatch.Elapsed.TotalMilliseconds:F0}ms)");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                totalElapsed += stopwatch.Elapsed;
                failCount++;
                lastError = ex.Message;
                TestLogger.Debug($"  Run {i + 1}/{runs}: FAIL ({stopwatch.Elapsed.TotalMilliseconds:F0}ms) - {lastError}");
            }
        }

        if (passCount == runs)
        {
            // All runs passed — stable pass
            Results.Pass(testName, totalElapsed);
        }
        else if (failCount == runs)
        {
            // All runs failed — stable failure
            Results.Fail(testName, lastError ?? "Unknown error", totalElapsed);
        }
        else
        {
            // Inconsistent results — flaky test
            Results.Fail(testName, $"FLAKY: passed {passCount}/{runs}, failed {failCount}/{runs} - {lastError}", totalElapsed);
        }
    }

    #region Assertion Helpers

    protected void AssertEqual<T>(T expected, T actual, string message = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            var msg = string.IsNullOrEmpty(message)
                ? $"Expected {expected}, got {actual}"
                : $"{message}: Expected {expected}, got {actual}";
            throw new AssertionException(msg);
        }
    }

    protected void AssertTrue(bool condition, string message = "")
    {
        if (!condition)
        {
            throw new AssertionException(message.Length > 0 ? message : "Condition was false");
        }
    }

    protected void AssertFalse(bool condition, string message = "")
    {
        if (condition)
        {
            throw new AssertionException(message.Length > 0 ? message : "Condition was true");
        }
    }

    protected void AssertNotNull<T>(T? value, string message = "") where T : class
    {
        if (value == null)
        {
            throw new AssertionException(message.Length > 0 ? message : "Value was null");
        }
    }

    protected void AssertNull<T>(T? value, string message = "") where T : class
    {
        if (value != null)
        {
            throw new AssertionException(message.Length > 0 ? message : $"Value was not null: {value}");
        }
    }

    protected void AssertApproxEqual(double expected, double actual, double tolerance = 0.001, string message = "")
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            var msg = string.IsNullOrEmpty(message)
                ? $"Expected ~{expected}, got {actual} (tolerance: {tolerance})"
                : $"{message}: Expected ~{expected}, got {actual} (tolerance: {tolerance})";
            throw new AssertionException(msg);
        }
    }

    protected void AssertThrows<TException>(Action action, string message = "") where TException : Exception
    {
        try
        {
            action();
            throw new AssertionException(message.Length > 0 ? message : $"Expected {typeof(TException).Name} but no exception was thrown");
        }
        catch (TException)
        {
            // Expected
        }
    }

    #endregion

    #region GC Helpers

    /// <summary>
    /// Forces a full GC collection and waits for finalizers.
    /// Use this to deterministically test cleanup behavior.
    /// </summary>
    protected static void ForceGC()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// Forces collection from a throwaway worker thread, scrubbing its own stack
    /// first, then running several blocking+compacting full collections with a
    /// finalizer drain between each. Use this (not <see cref="ForceGC"/>) when a
    /// test asserts that a transient object was actually collected: under Mono's
    /// conservative stack scan a stale reference to the object can linger in the
    /// test thread's frame/registers and falsely keep it alive. Running the GC on
    /// a separate thread whose stack never touched the object defeats that.
    /// </summary>
    protected static void ForceGCThorough(int cycles = 6)
    {
        var worker = new System.Threading.Thread(() =>
        {
            // Scrub the worker's own stack with a throwaway allocation loop so a
            // leftover managed pointer cannot pose as a conservative root.
            var scratch = new object[256];
            for (int i = 0; i < scratch.Length; i++)
                scratch[i] = new object();
            GC.KeepAlive(scratch);

            for (int i = 0; i < cycles; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
        })
        { IsBackground = true };
        worker.Start();
        worker.Join();
    }

    /// <summary>
    /// Creates GC pressure by allocating temporary objects.
    /// Useful for testing that handles survive GC correctly.
    /// </summary>
    protected static void CreateGCPressure(int iterations = 1000)
    {
        for (int i = 0; i < iterations; i++)
        {
            _ = new byte[1024];
        }
        ForceGC();
    }

    #endregion

    #region Timeout Helpers

    /// <summary>
    /// Runs an async operation with a timeout.
    /// </summary>
    protected static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout)
    {
        var timeoutTask = Task.Delay(timeout);
        var completedTask = await Task.WhenAny(task, timeoutTask);

        if (completedTask == timeoutTask)
        {
            throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds:F1}s");
        }

        return await task;
    }

    /// <summary>
    /// Runs an async operation with a timeout.
    /// </summary>
    protected static async Task WithTimeout(Task task, TimeSpan timeout)
    {
        var timeoutTask = Task.Delay(timeout);
        var completedTask = await Task.WhenAny(task, timeoutTask);

        if (completedTask == timeoutTask)
        {
            throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds:F1}s");
        }

        await task;
    }

    /// <summary>
    /// Default async operation timeout (5 seconds per the enhancement plan).
    /// </summary>
    protected static readonly TimeSpan DefaultAsyncTimeout = TimeSpan.FromSeconds(5);

    #endregion

    #region Memory Tracking

    /// <summary>
    /// Gets current managed memory usage.
    /// </summary>
    protected static long GetManagedMemory()
    {
        ForceGC();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>
    /// Tracks memory before and after an action to detect leaks.
    /// </summary>
    protected static (long before, long after, long delta) TrackMemory(Action action)
    {
        var before = GetManagedMemory();
        action();
        var after = GetManagedMemory();
        return (before, after, after - before);
    }

    #endregion
}

/// <summary>
/// Exception thrown by assertion helpers.
/// </summary>
public class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}
