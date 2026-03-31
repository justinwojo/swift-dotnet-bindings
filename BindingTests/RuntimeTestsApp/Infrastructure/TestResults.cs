// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.Json;

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Tracks test results for summary reporting.
/// Optionally writes crash-safe JSONL output (one JSON object per line, flushed after each test).
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

    // JSONL output for crash-safe structured results
    private StreamWriter? _jsonlWriter;
    private string? _currentClassName;
    private int _currentClassTestCount;

    /// <summary>
    /// Initializes JSONL output to the specified file path.
    /// The file is created/overwritten and each test result is appended + flushed.
    /// </summary>
    public void InitializeJsonl(string filePath)
    {
        _jsonlWriter = new StreamWriter(filePath, append: false) { AutoFlush = false };
    }

    /// <summary>
    /// Sets the current class being tested. Call before running each class's tests.
    /// </summary>
    public void BeginClass(string className)
    {
        lock (_lock)
        {
            // Emit class_done for previous class if there was one
            if (_currentClassName != null)
                WriteClassDone();

            _currentClassName = className;
            _currentClassTestCount = 0;
        }
    }

    /// <summary>
    /// Emits a class_done record for the current class. Called automatically by BeginClass
    /// for the previous class, and must be called after the last class finishes.
    /// </summary>
    public void EndClass()
    {
        lock (_lock)
        {
            if (_currentClassName != null)
            {
                WriteClassDone();
                _currentClassName = null;
            }
        }
    }

    /// <summary>
    /// Writes the final summary record and flushes. Call after all tests complete.
    /// Returns the path to the JSONL file, or null if JSONL was not initialized.
    /// </summary>
    public void FinalizeJsonl()
    {
        lock (_lock)
        {
            // Ensure last class is closed
            if (_currentClassName != null)
                WriteClassDone();
            _currentClassName = null;

            if (_jsonlWriter != null)
            {
                WriteJsonlLine(new { done = true, total = Total, passed = Passed, failed = Failed, skipped = Skipped });
                _jsonlWriter.Flush();
                _jsonlWriter.Dispose();
                _jsonlWriter = null;
            }
        }
    }

    private void WriteClassDone()
    {
        WriteJsonlLine(new { class_done = _currentClassName, tests_run = _currentClassTestCount });
    }

    private void WriteJsonlLine(object record)
    {
        if (_jsonlWriter == null) return;
        try
        {
            var json = JsonSerializer.Serialize(record);
            _jsonlWriter.WriteLine(json);
            _jsonlWriter.Flush();
        }
        catch
        {
            // Best-effort: don't let JSONL failures crash the test runner
        }
    }

    /// <summary>
    /// Extracts the class name and method name from a fully-qualified test name (ClassName.MethodName).
    /// </summary>
    private static (string className, string methodName) SplitTestName(string testName)
    {
        var dotIndex = testName.IndexOf('.');
        if (dotIndex > 0 && dotIndex < testName.Length - 1)
            return (testName[..dotIndex], testName[(dotIndex + 1)..]);
        return ("", testName);
    }

    public void Pass(string testName, TimeSpan? duration = null)
    {
        lock (_lock)
        {
            Passed++;
            _currentClassTestCount++;
            if (duration.HasValue)
            {
                TestDurations[testName] = duration.Value;
            }
            TestLogger.Success($"{testName}" + (duration.HasValue ? $" ({duration.Value.TotalMilliseconds:F0}ms)" : ""));

            var (className, methodName) = SplitTestName(testName);
            var ms = duration.HasValue ? (int)duration.Value.TotalMilliseconds : 0;
            WriteJsonlLine(new { @class = className, test = methodName, status = "pass", ms });
        }
    }

    public void Fail(string testName, string reason = "", TimeSpan? duration = null)
    {
        lock (_lock)
        {
            Failed++;
            _currentClassTestCount++;
            var msg = string.IsNullOrEmpty(reason) ? testName : $"{testName}: {reason}";
            FailedTests.Add(msg);
            if (duration.HasValue)
            {
                TestDurations[testName] = duration.Value;
            }
            TestLogger.Error(msg + (duration.HasValue ? $" ({duration.Value.TotalMilliseconds:F0}ms)" : ""));

            var (className, methodName) = SplitTestName(testName);
            var ms = duration.HasValue ? (int)duration.Value.TotalMilliseconds : 0;
            WriteJsonlLine(new { @class = className, test = methodName, status = "fail", error = reason, ms });
        }
    }

    public void Skip(string testName, string reason = "")
    {
        lock (_lock)
        {
            Skipped++;
            _currentClassTestCount++;
            var msg = string.IsNullOrEmpty(reason) ? testName : $"{testName}: {reason}";
            SkippedTests.Add(msg);
            TestLogger.Warning($"SKIP: {msg}");

            var (className, methodName) = SplitTestName(testName);
            WriteJsonlLine(new { @class = className, test = methodName, status = "skip", reason });
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
