// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;

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

                if (root.TryGetProperty("class_done", out var classDone))
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
    /// Merges another set of results into this one. Used for aggregating multiple runs
    /// after crash recovery. Last result for a given test wins (dedup by ClassName.TestName).
    /// </summary>
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
}
