// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Tracks test results for summary reporting with tier support.
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
/// Test tier definitions for categorizing tests by execution time budget.
/// </summary>
public enum TestTier
{
    /// <summary>PR gate tests - fast smoke tests, &lt; 30 seconds total.</summary>
    Tier1 = 1,

    /// <summary>Merge gate tests - full matrix coverage minus stress, &lt; 3 minutes total.</summary>
    Tier2 = 2,

    /// <summary>Nightly tests - everything including stress tests, &lt; 15 minutes total.</summary>
    Tier3 = 3
}

/// <summary>
/// Attribute to mark test tier.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class TestTierAttribute : Attribute
{
    public TestTier Tier { get; }

    public TestTierAttribute(TestTier tier)
    {
        Tier = tier;
    }
}
