// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Source-invariant guard pinning the single-writer invariant for the WasEmitted emission flag.
///
/// <para><c>MethodDecl.WasEmitted</c> / <c>PropertyDecl.WasEmitted</c> is the signal
/// <c>HasMethodInResolvedAncestors</c> / <c>HasPropertyInResolvedAncestors</c> reads to decide whether
/// an inherited member was actually emitted (and therefore whether a derived member is an
/// <c>override</c> vs a fresh declaration). Every emitter that genuinely produces a member MUST stamp
/// it — and it stamps it by calling <c>MarkEmitted()</c>, the single mutation entry point, NOT by
/// assigning the flag inline. This test enforces that invariant two ways: (a) there are zero raw
/// <c>x.WasEmitted = true;</c> assignments anywhere outside the two decl models (so the only writer is
/// <c>MarkEmitted()</c>), and (b) the <c>MarkEmitted()</c> call-site population is pinned, so a new
/// bridge emitter that forgets to stamp still shows up as a count change.</para>
///
/// <para>The two decl models (<c>MethodDecl.cs</c>, <c>PropertyDecl.cs</c>) carry the canonical writer
/// <c>MarkEmitted() => WasEmitted = true;</c>. That body is dot-less, so it does not match the
/// assignment regex; the model files are nonetheless carved out of the zero-assignment check as a
/// safety margin against a future <c>this.WasEmitted = true</c> writer.</para>
///
/// <para>If (a) fails: an emitter assigned <c>WasEmitted</c> inline — route it through
/// <c>MarkEmitted()</c>. If (b) fails: re-run
/// <c>grep -rn '\.MarkEmitted()' src/Swift.Bindings/src --include='*.cs'</c> and update the two
/// expected totals below (the count moved because an emission point was added or removed).</para>
/// </summary>
public class WasEmittedAssignmentCountTests
{
    // The pinned MarkEmitted call-site population (the former inline-assignment population, now routed
    // through the single writer).
    private const int ExpectedMarkEmittedCallCount = 23;
    private const int ExpectedMarkEmittedFileCount = 12;

    // The two decl models that legitimately hold the raw writer (`MarkEmitted() => WasEmitted = true;`).
    private static readonly string[] DeclModelFiles = { "MethodDecl.cs", "PropertyDecl.cs" };

    // Leading member-access dot + trailing semicolon: matches an inline `x.WasEmitted = true;`
    // assignment but NOT the dot-less `WasEmitted = true;` writer body, the `bool WasEmitted = true)`
    // record-default, or the `<c>WasEmitted = true</c>` doc-comment.
    private static readonly Regex InlineAssignmentPattern =
        new(@"\.WasEmitted\s*=\s*true\s*;", RegexOptions.Compiled);

    // A `.MarkEmitted()` invocation (leading dot) — excludes the dot-less `void MarkEmitted()` definition.
    private static readonly Regex MarkEmittedCallPattern =
        new(@"\.MarkEmitted\(\)", RegexOptions.Compiled);

    [Fact]
    public void MarkEmitted_IsTheOnlyWriter_NoInlineAssignments()
    {
        var offenders = CollectMatchesPerFile(InlineAssignmentPattern)
            .Where(kv => !DeclModelFiles.Contains(Path.GetFileName(kv.Key), StringComparer.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "WasEmitted must only be written through MarkEmitted(); found inline `x.WasEmitted = true;` " +
            "assignments in:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders.Select(kv => $"    {kv.Value,2}  {kv.Key}")));
    }

    [Fact]
    public void MarkEmittedCallSites_MatchDocumentedCount()
    {
        var perFile = CollectMatchesPerFile(MarkEmittedCallPattern);
        int total = perFile.Sum(kv => kv.Value);
        int fileCount = perFile.Count;

        var breakdown = string.Join(
            Environment.NewLine,
            perFile.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                   .Select(kv => $"    {kv.Value,2}  {kv.Key}"));

        Assert.True(
            total == ExpectedMarkEmittedCallCount && fileCount == ExpectedMarkEmittedFileCount,
            $"`.MarkEmitted()` call population drifted from the documented {ExpectedMarkEmittedCallCount} " +
            $"calls across {ExpectedMarkEmittedFileCount} files to {total} across {fileCount}. " +
            "If this is intentional, update the constants in this test to match the new emission-point " +
            "count. Live breakdown:" + Environment.NewLine + breakdown);
    }

    /// <summary>file (repo-relative) → count of <paramref name="pattern"/> matches in it.</summary>
    private static Dictionary<string, int> CollectMatchesPerFile(Regex pattern)
    {
        string sourceRoot = GeneratorSourceRoot();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip build intermediates so a stale copy under bin/obj can't inflate the count.
            if (PathHasSegment(path, "bin") || PathHasSegment(path, "obj"))
                continue;

            int count = pattern.Matches(File.ReadAllText(path)).Count;
            if (count > 0)
                // Key by the source-root-relative path (NOT the bare filename): two files sharing a
                // basename in different directories would otherwise clobber each other and silently
                // undercount both the match total and the file count.
                result[Path.GetRelativePath(sourceRoot, path).Replace('\\', '/')] = count;
        }

        return result;
    }

    private static bool PathHasSegment(string path, string segment) =>
        path.Replace('\\', '/').Split('/').Contains(segment, StringComparer.Ordinal);

    /// <summary>Walk up from the test output dir to the repo root (.nuke), then into the generator source.</summary>
    private static string GeneratorSourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, ".nuke")))
            dir = Path.GetDirectoryName(dir);
        if (dir == null)
            throw new InvalidOperationException(
                $"Cannot find repo root (.nuke directory) walking up from {AppContext.BaseDirectory}");
        return Path.Combine(dir, "src", "Swift.Bindings", "src");
    }
}
