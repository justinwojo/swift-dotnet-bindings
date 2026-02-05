// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Base class for runtime tests with common utilities.
/// </summary>
public abstract class TestBase
{
    protected TestResults Results { get; }
    protected string TestClassName { get; }

    protected TestBase(TestResults results)
    {
        Results = results;
        TestClassName = GetType().Name;
    }

    /// <summary>
    /// Runs all test methods in this class that match the specified tier.
    /// </summary>
    public async Task RunAllTestsAsync(TestTier maxTier = TestTier.Tier3)
    {
        // Class-level tier serves as default for methods without their own attribute
        var classTierAttr = GetType().GetCustomAttribute<TestTierAttribute>();
        var classTier = classTierAttr?.Tier ?? TestTier.Tier1;

        var methods = GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.StartsWith("Test") && m.GetParameters().Length == 0)
            .ToList();

        foreach (var method in methods)
        {
            // Method-level tier overrides class-level tier
            var tierAttr = method.GetCustomAttribute<TestTierAttribute>();
            var methodTier = tierAttr?.Tier ?? classTier;

            if (methodTier > maxTier)
            {
                Results.Skip($"{TestClassName}.{method.Name}", $"Tier {(int)methodTier} > max tier {(int)maxTier}");
                continue;
            }

            await RunTestMethodAsync(method);
        }
    }

    private async Task RunTestMethodAsync(MethodInfo method)
    {
        var testName = $"{TestClassName}.{method.Name}";
        TestLogger.Test($"--- {testName} ---");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = method.Invoke(this, null);
            if (result is Task task)
            {
                await task;
            }
            stopwatch.Stop();
            Results.Pass(testName, stopwatch.Elapsed);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            stopwatch.Stop();
            TestLogger.Exception(tie.InnerException, testName);
            Results.Fail(testName, tie.InnerException.Message, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            TestLogger.Exception(ex, testName);
            Results.Fail(testName, ex.Message, stopwatch.Elapsed);
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
