// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests;

public sealed class RuntimeTestAttemptsTests : IDisposable
{
    const string Token = "e45ace89fa59472495f59c9eab2bf609";
    const string ActiveClass = "InstanceDirectThrowControlTests";
    const string ActiveMethod = "TestThrowingInstanceDirectCall";
    readonly string directory = Path.Combine(Path.GetTempPath(), "runtime-attempt-tests-" + Guid.NewGuid().ToString("N"));
    readonly TestClassInventory inventory;
    readonly HashSet<string> eligible = new() { ActiveClass, "NextTests" };

    public RuntimeTestAttemptsTests()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "inventory.txt");
        File.WriteAllText(path, $"{ActiveClass}.{ActiveMethod}\nNextTests.TestNext\n");
        inventory = TestClassInventory.Load(path);
    }

    static string CapturedAbort()
    {
        using var stream = typeof(RuntimeTestAttemptsTests).Assembly.GetManifestResourceStream(
            "LaunchDiagnostics.device-sigabrt.txt") ?? throw new InvalidOperationException("Missing retained console fixture.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static LaunchResult Launch(TestResult observed, string output) =>
        new(LaunchDiagnostics.ClassifyFinalOutput(observed, output), output, null, null);

    RuntimeTestAttempts.RecoveryPlan Plan(LaunchResult result, JsonlTestResults? jsonl = null,
        int attempt = 0, int launcherAborts = 0) =>
        RuntimeTestAttempts.PlanRecovery(result, jsonl, inventory, eligible, new HashSet<string>(),
            attempt, maxRetries: 5, launcherAborts: launcherAborts);

    [Fact]
    public void CapturedFirstTestCrash_WithOnlyToken_RetainsEvidenceAndFailureAfterPassingRecovery()
    {
        var console = CapturedAbort();
        Assert.Equal("4c140c11a618c1c5f5e203d1f964ae451d007732bded0f43ec9a732c21983693",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(console))).ToLowerInvariant());
        var history = new RuntimeTestAttempts(Path.Combine(directory, "attempts"));
        var crash = Launch(TestResult.LaunchFailure, console);
        var first = history.RecordLaunch(0, Token, crash);
        var tokenOnly = $"{{\"run_token\":\"{Token}\"}}";
        RuntimeTestAttempts.RecordResults(first, tokenOnly, "token-validated");
        var original = JsonlTestResults.Parse(tokenOnly);
        var plan = Plan(crash, original);
        RuntimeTestAttempts.RecordRecovery(first, plan);
        Assert.True(plan.Resume);
        Assert.Equal(new RuntimeTestAttempts.TestIdentity(ActiveClass, ActiveMethod), plan.ActiveTest);
        Assert.Equal(new[] { ActiveClass }, plan.ClassesToExclude);
        Assert.Empty(original.Tests); // No invented app result for the interrupted invocation.

        var aggregate = new JsonlTestResults();
        aggregate.Merge(original);
        aggregate.Merge(JsonlTestResults.Parse("{\"class\":\"NextTests\",\"test\":\"TestNext\",\"status\":\"pass\"}"));
        Assert.Equal(1, aggregate.PassCount);
        Assert.Equal(0, aggregate.CrashCount);
        var passed = Launch(TestResult.Success, "RESULTS FLUSHED\nTEST SUCCESS");
        history.RecordLaunch(1, Guid.NewGuid().ToString("N"), passed);
        Assert.Same(crash, history.FinalResult(passed));
        Assert.Equal(TestResult.Crash, history.FinalResult(passed).Result);
        Assert.Equal(Encoding.UTF8.GetBytes(console), File.ReadAllBytes(Path.Combine(first.Directory, "console.log")));
        Assert.Equal(Encoding.UTF8.GetBytes(tokenOnly), File.ReadAllBytes(Path.Combine(first.Directory, "test-results.jsonl")));
        Assert.Contains(ActiveClass, File.ReadAllText(Path.Combine(first.Directory, "derived-recovery.json")));
        Assert.Throws<IOException>(() => RuntimeTestAttempts.RecordResults(first, tokenOnly, "again"));
        Assert.Equal(tokenOnly, File.ReadAllText(Path.Combine(first.Directory, "test-results.jsonl")));
    }

    [Theory]
    [InlineData("App terminated due to signal 6.")]
    [InlineData("[TEST] --- UnknownTests.Unknown ---\nApp terminated due to signal 6.")]
    [InlineData("[TEST] --- InstanceDirectThrowControlTests.TestThrowingInstanceDirectCall ---\n[PASS] InstanceDirectThrowControlTests.TestThrowingInstanceDirectCall (1ms)\nApp terminated due to signal 6.")]
    [InlineData("[TEST] --- InstanceDirectThrowControlTests.TestThrowingInstanceDirectCall ---\n[TEST] --- UnknownTests.Unknown ---\nApp terminated due to signal 6.")]
    public void MissingUnfinishedInventoryIdentity_StopsWithoutExcludingAnyClass(string output)
    {
        var plan = Plan(Launch(TestResult.LaunchFailure, output));
        Assert.False(plan.Resume);
        Assert.Null(plan.ActiveTest);
        Assert.Empty(plan.ClassesToExclude);
    }

    [Theory]
    [InlineData("[0.123s] [TEST] --- InstanceDirectThrowControlTests.TestThrowingInstanceDirectCall ---")]
    [InlineData("[0.123s] [TEST] --- InstanceDirectThrowControlTests.TestThrowingInstanceDirectCall (flake detect: 3x) ---")]
    public void KnownUnfinishedInvocation_ResumesOnlyIndependentClasses(string output)
    {
        var plan = Plan(Launch(TestResult.Timeout, output));
        Assert.True(plan.Resume);
        Assert.Equal(new[] { ActiveClass }, plan.ClassesToExclude);
    }

    [Theory]
    [InlineData("{\"class\":\"InstanceDirectThrowControlTests\",\"test\":\"TestThrowingInstanceDirectCall\",\"status\":\"pass\"}")]
    [InlineData("{\"class_done\":\"InstanceDirectThrowControlTests\"}")]
    public void JsonlCompletionContradictsUnfinishedConsole_Stop(string jsonl)
    {
        var result = Launch(TestResult.Timeout, $"[TEST] --- {ActiveClass}.{ActiveMethod} ---");
        Assert.False(Plan(result, JsonlTestResults.Parse(jsonl)).Resume);
    }

    [Fact]
    public void TimeoutCannotBecomePassingRecoveryOrLateSummary()
    {
        var history = new RuntimeTestAttempts(Path.Combine(directory, "attempts"));
        var timeout = Launch(TestResult.Timeout, $"[TEST] --- {ActiveClass}.{ActiveMethod} ---\nRESULTS FLUSHED\nTEST SUCCESS");
        history.RecordLaunch(0, Token, timeout);
        Assert.Equal(TestResult.Timeout, timeout.Result);
        Assert.False(Plan(timeout).Resume);
        var passed = Launch(TestResult.Success, "TEST SUCCESS");
        Assert.Equal(TestResult.Timeout, history.FinalResult(passed).Result);
    }

    [Fact]
    public void KnownPrestartAbort_IsNotAProductFailureAndKeepsSeparateRetryBudget()
    {
        var history = new RuntimeTestAttempts(Path.Combine(directory, "attempts"));
        var abort = Launch(TestResult.LaunchFailure, "The application failed to launch. com.apple.dt.CoreDeviceError error 10002");
        var attempt = history.RecordLaunch(0, Token, abort);
        RuntimeTestAttempts.RecordResults(attempt, null, "not-retrieved-known-prestart-abort");
        Assert.True(LaunchDiagnostics.LauncherNeverStartedApp(abort));
        Assert.Null(history.FirstProductFailure);
        Assert.False(Plan(abort).Resume);
        Assert.False(Plan(Launch(TestResult.Crash, CapturedAbort()), attempt: 5).Resume);
        Assert.True(Plan(Launch(TestResult.Crash, CapturedAbort()), attempt: 5, launcherAborts: 1).Resume);
        var passed = Launch(TestResult.Success, "TEST SUCCESS");
        Assert.Same(passed, history.FinalResult(passed));
    }

    [Fact]
    public void StaleResultsAreNeverRetainedAsCurrentAttemptJsonl()
    {
        var history = new RuntimeTestAttempts(Path.Combine(directory, "attempts"));
        var attempt = history.RecordLaunch(0, Token, Launch(TestResult.Crash, CapturedAbort()));
        Assert.Throws<InvalidOperationException>(() => RuntimeTestAttempts.RecordResults(attempt,
            "{\"run_token\":\"another-run\"}", "token-validated"));
        Assert.False(File.Exists(Path.Combine(attempt.Directory, "test-results.jsonl")));
        Assert.True(File.Exists(Path.Combine(attempt.Directory, "console.log")));
        Assert.NotNull(history.FirstProductFailure);
    }

    [Fact]
    public void IncompleteProductFailure_WithKnownInvocation_PreservesIndependentRecoveryAndStickyFailure()
    {
        var output = $"[TEST] --- {ActiveClass}.{ActiveMethod} ---\nUnhandled exception. System.InvalidOperationException";
        var result = Launch(TestResult.LaunchFailure, output);
        Assert.Equal(TestResult.Failure, result.Result); // Do not invent a native crash.
        Assert.True(Plan(result).Resume);
        Assert.False(LaunchDiagnostics.LauncherNeverStartedApp(result));
        var history = new RuntimeTestAttempts(Path.Combine(directory, "attempts"));
        history.RecordLaunch(0, Token, result);
        Assert.Equal(TestResult.Failure, history.FinalResult(Launch(TestResult.Success, "TEST SUCCESS")).Result);
    }

    [Theory]
    [InlineData("Unhandled exception. System.InvalidOperationException")]
    [InlineData("[INFO] === InstanceDirectThrowControlTests ===\nUnhandled exception. System.TypeInitializationException")]
    [InlineData("[TEST] --- InstanceDirectThrowControlTests.TestThrowingInstanceDirectCall ---\nTEST FAILURE")]
    [InlineData("[TEST] --- InstanceDirectThrowControlTests.TestThrowingInstanceDirectCall ---\n[PASS] InstanceDirectThrowControlTests.TestThrowingInstanceDirectCall (1ms)\nUnhandled exception.")]
    public void IncompleteOrCompletedFailure_WithoutUnfinishedIdentity_CannotResume(string output)
        => Assert.False(Plan(Launch(TestResult.LaunchFailure, output)).Resume);

    [Fact]
    public void FinalJsonlSummary_PreventsIncompleteConsoleRecovery()
    {
        var result = Launch(TestResult.LaunchFailure, $"[TEST] --- {ActiveClass}.{ActiveMethod} ---\nUnhandled exception.");
        Assert.False(Plan(result, JsonlTestResults.Parse("{\"done\":true}")).Resume);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
