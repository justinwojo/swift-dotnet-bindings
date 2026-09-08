// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Retains each launch separately from the last-result-wins JSONL aggregate. Recovery gathers
/// additional class coverage; it cannot replace a failure already observed in this invocation.
/// </summary>
public sealed class RuntimeTestAttempts
{
    readonly string directory;
    public LaunchResult? FirstProductFailure { get; private set; }

    public RuntimeTestAttempts(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
    }

    public sealed record Attempt(int Index, string RunToken, string Directory, LaunchResult Launch);

    /// <summary>Called immediately after launch, before retrieval, diagnostics or another launch.</summary>
    public Attempt RecordLaunch(int index, string runToken, LaunchResult launch)
    {
        if (!Guid.TryParseExact(runToken, "N", out _))
            throw new ArgumentException("Expected a harness run token.", nameof(runToken));
        var attemptDirectory = Path.Combine(directory, $"{index:D2}-{runToken}");
        // Preserve the first product failure even if retaining/retrieving later evidence fails.
        if (launch.Result is TestResult.Crash or TestResult.Timeout or TestResult.Failure)
            FirstProductFailure ??= launch;
        Directory.CreateDirectory(attemptDirectory);
        var attempt = new Attempt(index, runToken, attemptDirectory, launch);
        WriteNew(Path.Combine(attemptDirectory, "console.log"), launch.Output);
        WriteJson(Path.Combine(attemptDirectory, "launch.json"), writer =>
        {
            writer.WriteNumber("attempt", index);
            writer.WriteString("run_token", runToken);
            writer.WriteString("result", launch.Result.ToString());
            if (launch.ExitCode is int exitCode) writer.WriteNumber("exit_code", exitCode);
            else writer.WriteNull("exit_code");
            writer.WriteString("crash_log_path", launch.CrashLogPath);
            writer.WriteBoolean("results_flushed", launch.ResultsFlushed);
            writer.WriteString("console_sha256", Hash(launch.Output));
            writer.WriteBoolean("known_prestart_abort", LaunchDiagnostics.LauncherNeverStartedApp(launch));
        });
        return attempt;
    }

    /// <summary>Retains the original token-validated text, including a token-only/truncated file.</summary>
    public static void RecordResults(Attempt attempt, string? jsonl, string retrieval)
    {
        if (jsonl != null)
        {
            if (!JsonlTestResults.HasMatchingRunToken(jsonl, attempt.RunToken))
                throw new InvalidOperationException("Cannot retain stale JSONL as this attempt's results.");
            WriteNew(Path.Combine(attempt.Directory, "test-results.jsonl"), jsonl);
        }
        WriteJson(Path.Combine(attempt.Directory, "results.json"), writer =>
        {
            writer.WriteString("run_token", attempt.RunToken);
            writer.WriteString("retrieval", retrieval);
            writer.WriteString("jsonl_sha256", jsonl == null ? null : Hash(jsonl));
        });
    }

    public static void RecordRecovery(Attempt attempt, RecoveryPlan plan) =>
        WriteJson(Path.Combine(attempt.Directory, "derived-recovery.json"), writer =>
        {
            writer.WriteBoolean("resume", plan.Resume);
            writer.WriteString("active_class", plan.ActiveTest?.ClassName);
            writer.WriteString("active_test", plan.ActiveTest?.TestName);
            writer.WriteString("reason", plan.Reason);
            writer.WriteStartArray("classes_to_exclude");
            foreach (var cls in plan.ClassesToExclude) writer.WriteStringValue(cls);
            writer.WriteEndArray();
        });

    public LaunchResult FinalResult(LaunchResult last) => FirstProductFailure ?? last;

    public sealed record TestIdentity(string ClassName, string TestName);
    public sealed record RecoveryPlan(bool Resume, TestIdentity? ActiveTest,
        IReadOnlyList<string> ClassesToExclude, string Reason);

    /// <summary>
    /// Uses the sequential runner's unfinished invocation marker. A result-bearing class is not
    /// necessarily complete, and a missing JSONL class_done record is not an invocation marker.
    /// </summary>
    public static RecoveryPlan PlanRecovery(LaunchResult launch, JsonlTestResults? results,
        TestClassInventory inventory, IReadOnlySet<string> eligible, IReadOnlySet<string> excluded,
        int attempt, int maxRetries, int launcherAborts)
    {
        if (launch.Result is not (TestResult.Crash or TestResult.Timeout) &&
            !LaunchDiagnostics.IsIncompleteProductFailure(launch.Result, launch.Output))
            return new(false, null, Array.Empty<string>(), "No incomplete product termination to recover.");
        if (results?.Done == true)
            return new(false, null, Array.Empty<string>(), "App JSONL contains a final summary; stop.");

        var active = FindActiveTest(launch.Output, inventory);
        if (active == null || !eligible.Contains(active.ClassName) || excluded.Contains(active.ClassName))
            return new(false, null, Array.Empty<string>(), "No eligible, inventory-validated unfinished invocation; stop without guessing.");

        // A flushed terminal result/class_done contradicts an apparently unfinished console
        // invocation (e.g. a lost console result line). Do not blame that already-finished test.
        if (results != null && (results.CompletedClasses.Contains(active.ClassName) ||
            results.Tests.Any(t => t.ClassName == active.ClassName && t.TestName == active.TestName)))
            return new(false, null, Array.Empty<string>(), "JSONL records completion of the apparent active invocation; stop.");

        var classes = new HashSet<string>(StringComparer.Ordinal) { active.ClassName };
        if (results != null)
            classes.UnionWith(results.CompletedClasses.Where(eligible.Contains));
        // A directly observed console failure must not be rerun and replaced by a later pass.
        // Keep its original text in the attempt, not a fabricated app JSONL record.
        foreach (var (cls, test) in JsonlTestResults.ParseClassesFromConsole(launch.Output).Failures)
            if (eligible.Contains(cls) && inventory.GetMethods(cls).Contains(test)) classes.Add(cls);

        var ordered = classes.OrderBy(c => c, StringComparer.Ordinal).ToArray();
        if (!eligible.Except(excluded).Except(classes).Any())
            return new(false, active, ordered, "No independent classes remain.");
        if (CrashRecoveryBudget.IsExhausted(attempt, maxRetries, launcherAborts))
            return new(false, active, ordered, "Crash-recovery budget exhausted.");
        return new(true, active, ordered, "Continue independent classes; the original product failure remains sticky.");
    }

    static readonly Regex Start = new(
        @"\[TEST\] --- ([^\s.]+)\.([^\s.]+)(?: \(flake detect: [1-9][0-9]*x\))? ---\s*$",
        RegexOptions.CultureInvariant);
    static readonly Regex Terminal = new(
        @"\[(?:PASS|FAIL)\]\s+([^\s.]+)\.([^\s.:]+)(?:[\s:]|$)|\[WARN\] SKIP:\s+([^\s.]+)\.([^\s.:]+)(?:[\s:]|$)",
        RegexOptions.CultureInvariant);

    static TestIdentity? FindActiveTest(string output, TestClassInventory inventory)
    {
        TestIdentity? active = null;
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("RESULTS FLUSHED", StringComparison.Ordinal) ||
                line.Contains("TEST SUCCESS", StringComparison.Ordinal) ||
                line.Contains("TEST FAILURE", StringComparison.Ordinal))
            {
                active = null;
                continue;
            }
            if (line.Contains("[TEST] --- ", StringComparison.Ordinal))
            {
                var start = Start.Match(line);
                // An unrecognized later invocation invalidates the older identity too.
                active = start.Success && inventory.GetMethods(start.Groups[1].Value).Contains(start.Groups[2].Value)
                    ? new(start.Groups[1].Value, start.Groups[2].Value) : null;
                continue;
            }
            var terminal = Terminal.Match(line);
            if (!terminal.Success) continue;
            var cls = terminal.Groups[1].Success ? terminal.Groups[1].Value : terminal.Groups[3].Value;
            var test = terminal.Groups[2].Success ? terminal.Groups[2].Value : terminal.Groups[4].Value;
            // Tests run serially. A later real terminal marker means an earlier start is stale,
            // even when an intervening start line was lost from the console.
            if (inventory.GetMethods(cls).Contains(test)) active = null;
        }
        return active;
    }

    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    static void WriteJson(string path, Action<Utf8JsonWriter> write)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        write(writer);
        writer.WriteEndObject();
    }
    static void WriteNew(string path, string value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(value);
    }
}
