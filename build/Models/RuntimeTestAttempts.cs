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
        Directory.CreateDirectory(attemptDirectory);
        // The hung app's stacks go beside its console first, so the failure this launch becomes
        // names files that exist.
        launch = RecordHungStacks(attemptDirectory, launch);
        // Preserve the first product failure even if retaining/retrieving later evidence fails.
        if (launch.Result is TestResult.Crash or TestResult.Timeout or TestResult.Failure)
            FirstProductFailure ??= launch;
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
            if (launch.HungStacks is { } stacks)
            {
                writer.WriteStartObject("hung_process_stacks");
                writer.WriteString("target", stacks.Target);
                if (stacks.Pid is int pid) writer.WriteNumber("pid", pid);
                else writer.WriteNull("pid");
                writer.WriteStartArray("samples");
                for (var i = 0; i < stacks.Samples.Count; i++)
                {
                    var sample = stacks.Samples[i];
                    writer.WriteStartObject();
                    writer.WriteNumber("index", sample.Index);
                    writer.WriteString("started_at", sample.StartedAt);
                    writer.WriteNumber("duration_ms", (long)sample.Duration.TotalMilliseconds);
                    writer.WriteString("tool", sample.Tool);
                    writer.WriteString("outcome", sample.Outcome);
                    writer.WriteString("file", Path.GetFileName(stacks.Files[i]));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        });
        return attempt;
    }

    /// <summary>The file name of hung-app sample <paramref name="index"/> in an attempt directory.</summary>
    public static string HungStacksFileName(int index) => $"hung-stacks-{index}.txt";

    /// <summary>
    /// Writes each hung-app sample to <see cref="HungStacksFileName"/> and returns the launch with the
    /// stacks pointing at those files. The launch's result and exit code are untouched.
    /// </summary>
    static LaunchResult RecordHungStacks(string attemptDirectory, LaunchResult launch)
    {
        if (launch.HungStacks is not { } stacks)
            return launch;
        var files = new List<string>();
        foreach (var sample in stacks.Samples)
        {
            var path = Path.Combine(attemptDirectory, HungStacksFileName(sample.Index));
            WriteNew(path,
                $"# {stacks.Target}, pid {stacks.Pid?.ToString() ?? "?"}: sample {sample.Index} of {stacks.Samples.Count}, " +
                $"{sample.Tool}, {sample.Outcome}, started {sample.StartedAt:O}, {sample.Duration.TotalSeconds:0.0} s\n" +
                sample.Text);
            files.Add(path);
        }
        return launch with { HungStacks = stacks with { Files = files } };
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
        if (active == null)
            return PlanClassLevelRecovery(launch, results, inventory, eligible, excluded, attempt, maxRetries, launcherAborts);
        if (!eligible.Contains(active.ClassName) || excluded.Contains(active.ClassName))
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

    /// <summary>The test name a class-level interruption reports: the class marker printed, no test started.</summary>
    public const string ClassSetup = "<class setup>";

    /// <summary>
    /// The fallback when the console shows no unfinished test. A crash can land outside every test:
    /// in a class's setup (its lane probe, static initialization or constructor, after its
    /// <c>=== Class ===</c> marker and before its first test) or between tests. The plan excludes
    /// every eligible class that started in this attempt, so nothing that ran is rerun and replaced;
    /// it names the interrupted class only when that class's marker is the last thing the console
    /// shows. A launch that started no new class stops, so every resume makes progress.
    /// </summary>
    static RecoveryPlan PlanClassLevelRecovery(LaunchResult launch, JsonlTestResults? results,
        TestClassInventory inventory, IReadOnlySet<string> eligible, IReadOnlySet<string> excluded,
        int attempt, int maxRetries, int launcherAborts)
    {
        var (started, setup) = FindStartedClasses(launch.Output, inventory);
        var classes = new HashSet<string>(started.Where(c => eligible.Contains(c) && !excluded.Contains(c)), StringComparer.Ordinal);
        if (classes.Count == 0)
            return new(false, null, Array.Empty<string>(), "No eligible, inventory-validated unfinished invocation; stop without guessing.");
        TestIdentity? interrupted = null;
        if (setup != null && classes.Contains(setup) && results?.CompletedClasses.Contains(setup) != true)
            interrupted = new(setup, ClassSetup);
        var ordered = classes.OrderBy(c => c, StringComparer.Ordinal).ToArray();
        if (!eligible.Except(excluded).Except(classes).Any())
            return new(false, interrupted, ordered, "No independent classes remain.");
        if (CrashRecoveryBudget.IsExhausted(attempt, maxRetries, launcherAborts))
            return new(false, interrupted, ordered, "Crash-recovery budget exhausted.");
        return new(true, interrupted, ordered, interrupted != null
            ? "Interrupted in class setup; exclude every class this attempt started and continue; the original product failure remains sticky."
            : "Interrupted outside any test; exclude every class this attempt started and continue; the original product failure remains sticky.");
    }

    static readonly Regex ClassStart = new(@"\[INFO\] === ([^\s.]+) ===\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Every inventory class whose marker this attempt printed, and the class whose marker is the
    /// last event before the output ends (no test started, no final summary after it), if any.
    /// </summary>
    static (IReadOnlyList<string> Started, string? Setup) FindStartedClasses(string output, TestClassInventory inventory)
    {
        var started = new List<string>();
        string? setup = null;
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("RESULTS FLUSHED", StringComparison.Ordinal))
                break;
            if (line.Contains("[TEST] --- ", StringComparison.Ordinal))
            {
                setup = null;
                continue;
            }
            var marker = ClassStart.Match(line);
            if (!marker.Success || !inventory.ClassNames.Contains(marker.Groups[1].Value))
                continue;
            setup = marker.Groups[1].Value;
            if (!started.Contains(setup)) started.Add(setup);
        }
        return (started, setup);
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
