// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Tracks test results for summary reporting.
/// </summary>
public class TestResults
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public int Warnings { get; private set; }
    public List<string> FailedTests { get; } = new();
    public List<string> SkippedTests { get; } = new();
    public Dictionary<string, TimeSpan> TestDurations { get; } = new();

    private readonly object _lock = new();

    public void Pass(string testName, TimeSpan? duration = null)
    {
        lock (_lock)
        {
            Passed++;
            if (duration.HasValue)
            {
                TestDurations[testName] = duration.Value;
            }
            TestLogger.Success($"{testName}" + (duration.HasValue ? $" ({duration.Value.TotalMilliseconds:F0}ms)" : ""));
        }
    }

    public void Fail(string testName, string reason = "", TimeSpan? duration = null)
    {
        lock (_lock)
        {
            Failed++;
            var msg = string.IsNullOrEmpty(reason) ? testName : $"{testName}: {reason}";
            FailedTests.Add(msg);
            if (duration.HasValue)
            {
                TestDurations[testName] = duration.Value;
            }
            TestLogger.Error(msg + (duration.HasValue ? $" ({duration.Value.TotalMilliseconds:F0}ms)" : ""));
        }
    }

    public void Skip(string testName, string reason = "")
    {
        lock (_lock)
        {
            Skipped++;
            var msg = string.IsNullOrEmpty(reason) ? testName : $"{testName}: {reason}";
            SkippedTests.Add(msg);
            TestLogger.Warning($"SKIP: {msg}");
        }
    }

    public void Warn(string message)
    {
        lock (_lock)
        {
            Warnings++;
            TestLogger.Warning(message);
        }
    }

    public bool AllPassed => Failed == 0;

    public int Total => Passed + Failed + Skipped;

    public TimeSpan TotalDuration => TestDurations.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);

    public override string ToString()
    {
        var status = AllPassed ? "ALL TESTS PASSED" : "SOME TESTS FAILED";
        var parts = new List<string> { $"{Passed} passed" };
        if (Failed > 0) parts.Add($"{Failed} failed");
        if (Skipped > 0) parts.Add($"{Skipped} skipped");
        if (Warnings > 0) parts.Add($"{Warnings} warnings");
        return $"{status}: {string.Join(", ", parts)}";
    }
}

/// <summary>
/// Target platform for test execution.
/// </summary>
public enum TestPlatform
{
    /// <summary>iOS Simulator (Mono JIT).</summary>
    Simulator,

    /// <summary>Physical device (NativeAOT).</summary>
    Device
}


/// <summary>
/// Marks tests that are broken everywhere (generator bugs, missing entry points).
/// Always skipped. The reason is visible in test output.
///
/// The reason MUST be one of:
/// - A specific generator bug description (e.g., "UniqueResource is ~Copyable: @_cdecl wrapper needs move semantics")
/// - A reference to a RuntimeLimitations.Limitation that affects both runtimes
///
/// Do NOT use vague runtime blame like "Mono JIT crash" or "NativeAOT issue".
/// See RuntimeLimitations registry (Swift.RuntimeLimitations) for all known upstream bugs.
/// If a crash doesn't match a registered limitation, it is a generator bug.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class SkipAttribute : Attribute
{
    public string Reason { get; }

    public SkipAttribute(string reason)
    {
        Reason = reason;
    }
}

/// <summary>
/// Marks tests that crash on simulator (Mono JIT) but work on device (NativeAOT).
/// Skipped on simulator, runs on device. The reason is visible in test output.
///
/// The reason MUST reference either:
/// - A Mono-specific RuntimeLimitations.Limitation (MonoCallConvSwiftJitAssertion,
///   MonoAsyncSafeHandleLifetime, or NonBlittableCallConvSwiftRejection)
/// - A specific generator bug that only manifests on Mono (prefixed with "Generator bug:")
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class SkipOnSimulatorAttribute : Attribute
{
    public string Reason { get; }

    public SkipOnSimulatorAttribute(string reason)
    {
        Reason = reason;
    }
}

/// <summary>
/// Marks tests that crash on device (NativeAOT) but work on simulator (Mono).
/// Skipped on device, runs on simulator. The reason is visible in test output.
///
/// The reason MUST reference either:
/// - A NativeAOT-specific RuntimeLimitations.Limitation (NativeAotFloatStructParam,
///   NativeAotFloatStructReturn, or NonBlittableCallConvSwiftRejection)
/// - A specific generator bug that only manifests on NativeAOT (prefixed with "Generator bug:")
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class SkipOnDeviceAttribute : Attribute
{
    public string Reason { get; }

    public SkipOnDeviceAttribute(string reason)
    {
        Reason = reason;
    }
}

/// <summary>
/// Marks stress/slow tests. Always runs but can be filtered if needed.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class SlowAttribute : Attribute { }
