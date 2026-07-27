// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Self-contained nullable context so this file compiles identically whether built in the Nuke
// build assembly (Nullable=enable) or link-compiled into the unit-test project (Nullable=disable +
// warnings-as-errors), where the string? annotations would otherwise raise CS8632.
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Parses and aggregates JSONL test result files produced by the runtime test app.
/// Each line is a JSON object: test result, class_done marker, or done summary.
/// Designed to be crash-safe: truncated last lines are ignored gracefully.
/// </summary>
public class JsonlTestResults
{
    public List<TestEntry> Tests { get; } = new();
    public HashSet<string> CompletedClasses { get; } = new();
    public bool Done { get; private set; }
    public int TotalFromSummary { get; private set; }
    public int PassedFromSummary { get; private set; }
    public int FailedFromSummary { get; private set; }
    public int SkippedFromSummary { get; private set; }

    /// <summary>
    /// The per-launch identity token the app stamped as the first line of the file
    /// (<c>{"run_token":"…"}</c>), or null if the file carries none. Null means the file was
    /// written by an app build that predates run tokens, or by a hand-launched app — in either
    /// case it CANNOT be attributed to a harness launch. See <see cref="HasMatchingRunToken"/>.
    /// </summary>
    public string? RunToken { get; private set; }

    public int PassCount => Tests.Count(t => t.Status == "pass");
    public int FailCount => Tests.Count(t => t.Status == "fail");
    public int SkipCount => Tests.Count(t => t.Status == "skip");
    public int CrashCount => Tests.Count(t => t.Status == "crash");

    public record TestEntry(string ClassName, string TestName, string Status, string? Error, int Ms);

    /// <summary>
    /// Parses a JSONL string (one JSON object per line). Skips malformed lines gracefully.
    /// </summary>
    public static JsonlTestResults Parse(string jsonlContent)
    {
        var results = new JsonlTestResults();
        if (string.IsNullOrWhiteSpace(jsonlContent))
            return results;

        foreach (var line in jsonlContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line.Trim());
                var root = doc.RootElement;

                if (root.TryGetProperty("run_token", out var runToken))
                {
                    results.RunToken = runToken.GetString();
                }
                else if (root.TryGetProperty("class_done", out var classDone))
                {
                    results.CompletedClasses.Add(classDone.GetString()!);
                }
                else if (root.TryGetProperty("done", out _))
                {
                    results.Done = true;
                    if (root.TryGetProperty("total", out var total)) results.TotalFromSummary = total.GetInt32();
                    if (root.TryGetProperty("passed", out var passed)) results.PassedFromSummary = passed.GetInt32();
                    if (root.TryGetProperty("failed", out var failed)) results.FailedFromSummary = failed.GetInt32();
                    if (root.TryGetProperty("skipped", out var skipped)) results.SkippedFromSummary = skipped.GetInt32();
                }
                else if (root.TryGetProperty("class", out var cls) && root.TryGetProperty("test", out var test))
                {
                    var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
                    var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                    var ms = root.TryGetProperty("ms", out var m) ? m.GetInt32() : 0;
                    results.Tests.Add(new TestEntry(cls.GetString()!, test.GetString()!, status, error, ms));
                }
            }
            catch (JsonException)
            {
                // Truncated/malformed line — skip gracefully (crash-safe by design)
            }
        }

        return results;
    }

    /// <summary>
    /// Parses a JSONL file from disk. Returns empty results if file doesn't exist.
    /// </summary>
    public static JsonlTestResults ParseFile(string filePath)
    {
        if (!File.Exists(filePath))
            return new JsonlTestResults();
        return Parse(File.ReadAllText(filePath));
    }

    /// <summary>
    /// Extracts the <c>run_token</c> value from a JSONL string, or null if no token line is
    /// present. The app writes the token as the first line, but this scans every line so a
    /// harmless leading log line (or a future record ordering change) cannot hide it.
    /// </summary>
    public static string? ExtractRunToken(string? jsonlContent)
    {
        if (string.IsNullOrWhiteSpace(jsonlContent))
            return null;

        foreach (var line in jsonlContent!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line.Trim());
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("run_token", out var token) &&
                    token.ValueKind == JsonValueKind.String)
                {
                    return token.GetString();
                }
            }
            catch (JsonException)
            {
                // Truncated/malformed line — keep scanning (crash-safe by design).
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true only if <paramref name="jsonlContent"/> proves it was produced by the launch
    /// identified by <paramref name="expectedRunToken"/>.
    /// </summary>
    /// <remarks>
    /// <b>Why this exists.</b> The runtime-test app writes its results into its own <b>persistent</b>
    /// data container, which survives reinstall on both simulator and physical device. When a launch
    /// fails outright — e.g. <c>devicectl</c> returning CoreDeviceError 10002 / NSPOSIXErrorDomain 22
    /// (EINVAL), where the process never starts — the harness's sandbox copy still succeeds and hands
    /// back the <em>previous</em> run's file. Scoring that as the current run's result produces a
    /// fully green gate for a run that executed nothing. A per-launch token minted by the harness,
    /// stamped into the file by the app, and checked here is what makes recovered results provably
    /// belong to the launch that was just attempted.
    ///
    /// <b>Fail-closed by design — do NOT "tolerate" a missing token.</b> A file with no
    /// <c>run_token</c> line at all is rejected, not trusted. A token-less file is exactly what a
    /// stale pre-token artifact looks like, so accepting it would reopen the false-green hole for
    /// precisely the case this guard exists to close. If an older app build is in the sandbox, the
    /// correct outcome is "no results recovered" (and a rebuild), not "assume it's ours". The same
    /// goes for an empty <paramref name="expectedRunToken"/>: with nothing to match against there is
    /// no proof to be had, so the answer is false.
    /// </remarks>
    public static bool HasMatchingRunToken(string? jsonlContent, string? expectedRunToken)
        => TokensMatch(ExtractRunToken(jsonlContent), expectedRunToken);

    /// <summary>
    /// Instance form of <see cref="HasMatchingRunToken"/> for callers that already parsed the file
    /// (the host-side macOS / Mac Catalyst paths read the artifact straight off disk). Same
    /// fail-closed rule: a null <see cref="RunToken"/> is a rejection, never a pass.
    /// </summary>
    public bool MatchesRunToken(string? expectedRunToken) => TokensMatch(RunToken, expectedRunToken);

    /// <summary>
    /// The single place the token rule is expressed: both sides must be present AND equal.
    /// Two nulls are NOT a match — that pairing is what a stale token-less artifact plus a
    /// mis-plumbed harness would look like, and it must not read as proof of anything.
    /// </summary>
    static bool TokensMatch(string? actual, string? expected)
        => !string.IsNullOrEmpty(actual)
           && !string.IsNullOrEmpty(expected)
           && string.Equals(actual, expected, StringComparison.Ordinal);

    /// <summary>
    /// Merges another set of results into this one. Used for aggregating multiple runs
    /// after crash recovery. Last result for a given test wins (dedup by ClassName.TestName).
    /// </summary>
    /// <remarks>
    /// <see cref="RunToken"/> is deliberately NOT merged: it identifies one launch's file, while an
    /// aggregate spans several launches with different tokens. Each file is validated against its own
    /// launch token at recovery time (see <see cref="HasMatchingRunToken"/>), before it ever reaches
    /// this method, so the aggregate has no use for a single token and must not appear to carry one.
    /// </remarks>
    public void Merge(JsonlTestResults other)
    {
        // Build lookup of existing tests
        var existing = new Dictionary<string, int>();
        for (int i = 0; i < Tests.Count; i++)
        {
            var key = $"{Tests[i].ClassName}.{Tests[i].TestName}";
            existing[key] = i;
        }

        foreach (var test in other.Tests)
        {
            var key = $"{test.ClassName}.{test.TestName}";
            if (existing.TryGetValue(key, out var idx))
                Tests[idx] = test; // Last result wins
            else
            {
                existing[key] = Tests.Count;
                Tests.Add(test);
            }
        }

        foreach (var cls in other.CompletedClasses)
            CompletedClasses.Add(cls);

        if (other.Done) Done = true;
        if (other.TotalFromSummary > 0) TotalFromSummary = other.TotalFromSummary;
        if (other.PassedFromSummary > 0) PassedFromSummary = other.PassedFromSummary;
        if (other.FailedFromSummary > 0) FailedFromSummary = other.FailedFromSummary;
        if (other.SkippedFromSummary > 0) SkippedFromSummary = other.SkippedFromSummary;
    }

    /// <summary>
    /// Identifies the class that was running when the process crashed.
    /// This is the class with test records but no class_done marker.
    /// Returns null if all classes completed (no crash mid-class).
    /// </summary>
    public string? FindCrashingClass()
    {
        var classesWithTests = Tests.Select(t => t.ClassName).Distinct().ToHashSet();
        return classesWithTests.Except(CompletedClasses).FirstOrDefault();
    }

    /// <summary>
    /// Synthesizes CRASHED entries for unfinished methods in the crashing class.
    /// Uses the inventory to know which methods were expected but never reported.
    /// </summary>
    public void SynthesizeCrashEntries(string crashingClass, TestClassInventory inventory)
    {
        var reportedMethods = Tests
            .Where(t => t.ClassName == crashingClass)
            .Select(t => t.TestName)
            .ToHashSet();

        var allMethods = inventory.GetMethods(crashingClass);
        foreach (var method in allMethods)
        {
            if (!reportedMethods.Contains(method))
            {
                Tests.Add(new TestEntry(crashingClass, method, "crash", "Process crashed", 0));
            }
        }
    }

    /// <summary>
    /// Returns all class names that have crash-status entries.
    /// </summary>
    public IReadOnlyList<string> CrashedClasses
        => Tests.Where(t => t.Status == "crash").Select(t => t.ClassName).Distinct().ToList();

    /// <summary>
    /// Returns a summary string for logging.
    /// </summary>
    public override string ToString()
        => $"JSONL: {PassCount} pass, {FailCount} fail, {SkipCount} skip, {CrashCount} crash (done={Done})";

    /// <summary>
    /// Outcome of a console-output scan — the fallback path when JSONL recovery fails.
    /// <see cref="CompletedClasses"/> is every class that printed a result line
    /// (<c>[PASS]</c>/<c>[FAIL]</c>/<c>[WARN] SKIP:</c>) and is therefore safe to skip on
    /// the next retry. <see cref="Failures"/> is the (class, method) identity of every
    /// <c>[FAIL]</c> line; these must be carried into the verdict so a failure recovered
    /// from the console cannot be silently dropped from an otherwise-green run.
    /// </summary>
    public sealed record ConsoleScanResult(
        HashSet<string> CompletedClasses,
        IReadOnlyList<(string ClassName, string TestName)> Failures);

    /// <summary>
    /// Fallback: scans console output lines like "[PASS] ClassName.TestMethod" when JSONL
    /// recovery fails. Returns the set of classes that produced any result line (used to skip
    /// already-completed classes on the next retry) together with the (class, method) identity
    /// of every <c>[FAIL]</c> line so the failure survives into the final verdict.
    /// </summary>
    public static ConsoleScanResult ParseClassesFromConsole(string consoleOutput)
    {
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<(string ClassName, string TestName)>();
        var failureKeys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(consoleOutput))
            return new ConsoleScanResult(completed, failures);

        foreach (var line in consoleOutput.Split('\n'))
        {
            // Real lines carry a leading "[<elapsed>s] " timestamp (TestLogger), so the status
            // marker is NOT at the start — locate it anywhere in the line. This mirrors the
            // Contains-anywhere matching the crash-diagnostic counters use on the same output;
            // a first-"] "-prefix match would only ever see the timestamp's bracket and match
            // nothing. Order matters: check [FAIL] before [PASS] so a failure is never mistaken
            // for a pass.
            int markerIdx;
            string marker;
            var isFail = false;
            if ((markerIdx = line.IndexOf("[FAIL]", StringComparison.Ordinal)) >= 0)
            {
                marker = "[FAIL]";
                isFail = true;
            }
            else if ((markerIdx = line.IndexOf("[PASS]", StringComparison.Ordinal)) >= 0)
            {
                marker = "[PASS]";
            }
            else if ((markerIdx = line.IndexOf("[WARN] SKIP:", StringComparison.Ordinal)) >= 0)
            {
                marker = "[WARN] SKIP:";
            }
            else
            {
                continue;
            }

            // Text after the marker is "ClassName.TestMethod[: reason][ (Nms)]".
            var after = line.Substring(markerIdx + marker.Length).TrimStart();

            var dot = after.IndexOf('.');
            if (dot <= 0) continue;

            var className = after.Substring(0, dot);
            // Guard against status-category banner/summary lines ("[PASS] === ALL TESTS PASSED ===",
            // "[FAIL]   - Class.Method"): a real per-test line starts with an uppercase class name.
            if (!char.IsUpper(className[0]))
                continue;

            completed.Add(className);

            // A [FAIL] line is positive evidence the class ran AND failed. Capture the method
            // identity so the failure can be replayed into the verdict even when JSONL recovery
            // lost it — that lossy path is the entire reason this console fallback exists.
            if (isFail)
            {
                var testName = ExtractTestName(after, dot);
                if (failureKeys.Add($"{className}.{testName}"))
                    failures.Add((className, testName));
            }
        }
        return new ConsoleScanResult(completed, failures);
    }

    /// <summary>
    /// Extracts the test-method token after the class dot, stopping at the first whitespace,
    /// ':' or '(' — so "Foo (3ms)" and "Foo: boom" both yield "Foo". Falls back to a sentinel
    /// when no token is present.
    /// </summary>
    static string ExtractTestName(string after, int dot)
    {
        var start = dot + 1;
        var end = after.Length;
        for (int i = start; i < after.Length; i++)
        {
            var c = after[i];
            if (char.IsWhiteSpace(c) || c == ':' || c == '(') { end = i; break; }
        }
        var name = after.Substring(start, end - start);
        return name.Length > 0 ? name : "(unknown)";
    }

    /// <summary>
    /// Records a test failure recovered from a console "[FAIL]" line — used when JSONL recovery
    /// fails mid-crash but the failure survived in the console log. Deduplicated by
    /// ClassName.TestName, so replaying an already-recorded result is a no-op.
    /// </summary>
    public void AddConsoleFailure(string className, string testName)
    {
        var key = $"{className}.{testName}";
        if (Tests.Any(t => $"{t.ClassName}.{t.TestName}" == key))
            return;
        Tests.Add(new TestEntry(className, testName, "fail", "Recovered from console [FAIL] (JSONL lost)", 0));
    }
}
