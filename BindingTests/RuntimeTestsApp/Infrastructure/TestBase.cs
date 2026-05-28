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

            // On device (NativeAOT), yield to the iOS run loop periodically.
            if (platform == TestPlatform.Device)
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
