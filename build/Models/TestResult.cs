// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Explicit, because this file is also link-compiled into Swift.Bindings.Unit.Tests, which builds
// with Nullable=disable — without it the CrashLogPath annotation below is a CS8632 error there.
#nullable enable

/// <summary>
/// Outcome of a runtime test launch on simulator, device, or macOS.
/// </summary>
public enum TestResult
{
    Success,
    Failure,
    Crash,
    LaunchFailure,
    Timeout
}

/// <summary>
/// Complete result from a test app launch including output and diagnostics.
/// </summary>
/// <param name="Result">Overall test outcome.</param>
/// <param name="Output">Captured stdout/stderr from the test app.</param>
/// <param name="ExitCode">Process exit code, null if timed out or killed.</param>
/// <param name="CrashLogPath">Path to crash report (.ips file) if a crash was detected.</param>
/// <param name="ResultsFlushed">Whether the RESULTS FLUSHED marker was seen (JSONL is fully written).</param>
public record LaunchResult(
    TestResult Result,
    string Output,
    int? ExitCode,
    string? CrashLogPath,
    bool ResultsFlushed = false);
