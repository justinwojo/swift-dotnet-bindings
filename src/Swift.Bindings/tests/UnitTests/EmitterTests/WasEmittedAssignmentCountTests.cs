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
/// Source-invariant guard pinning the <c>.WasEmitted = true;</c> assignment population that
/// <c>.claude/rules/constraints.md</c> documents (the "WasEmitted flag" trap constraint).
///
/// <para><c>MethodDecl.WasEmitted</c> / <c>PropertyDecl.WasEmitted</c> is the signal
/// <c>HasMethodInResolvedAncestors</c> / <c>HasPropertyInResolvedAncestors</c> reads to decide whether
/// an inherited member was actually emitted (and therefore whether a derived member is an
/// <c>override</c> vs a fresh declaration). Every emitter that genuinely produces a member MUST stamp
/// it. When that population drifts — a new bridge emitter forgets to stamp, or a stamp is removed —
/// override resolution silently mis-binds, and the constraints.md count goes stale. This test fails
/// the moment the count moves, forcing both the fix and the doc update into the same change.</para>
///
/// <para>The canonical population is the set of real <c>X.WasEmitted = true;</c> assignments. Two
/// textual look-alikes are deliberately NOT assignments and must stay excluded:
/// the <c>bool WasEmitted = true</c> record-default parameter in <c>IMethodBridgeEmitter.cs</c>
/// (a record positional default, not a stamp) and the <c>&lt;c&gt;WasEmitted = true&lt;/c&gt;</c>
/// reference inside a doc-comment in <c>ClosureParamTombstoneEmitter.cs</c>. The matching regex
/// requires a leading member-access dot and a trailing semicolon, so both are excluded structurally
/// rather than by name.</para>
///
/// <para>If this fails: re-run
/// <c>grep -rn '\.WasEmitted = true;' src/Swift.Bindings/src --include='*.cs'</c>, update the two
/// expected totals below AND the "WasEmitted flag" line in <c>.claude/rules/constraints.md</c> to
/// match — they are the same contract stated twice.</para>
/// </summary>
public class WasEmittedAssignmentCountTests
{
    // The documented population — see constraints.md "WasEmitted flag".
    private const int ExpectedAssignmentCount = 23;
    private const int ExpectedFileCount = 12;

    // Leading member-access dot + trailing semicolon: matches `x.WasEmitted = true;` but NOT the
    // `bool WasEmitted = true)` record-default or the `<c>WasEmitted = true</c>` doc-comment.
    private static readonly Regex AssignmentPattern =
        new(@"\.WasEmitted\s*=\s*true\s*;", RegexOptions.Compiled);

    [Fact]
    public void WasEmittedAssignments_MatchDocumentedCount()
    {
        var perFile = CollectAssignmentsPerFile();
        int total = perFile.Sum(kv => kv.Value);
        int fileCount = perFile.Count;

        var breakdown = string.Join(
            Environment.NewLine,
            perFile.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                   .Select(kv => $"    {kv.Value,2}  {kv.Key}"));

        Assert.True(
            total == ExpectedAssignmentCount && fileCount == ExpectedFileCount,
            $"`.WasEmitted = true;` population drifted from the documented {ExpectedAssignmentCount} " +
            $"assignments across {ExpectedFileCount} files to {total} across {fileCount}. " +
            "If this is intentional, update BOTH the constants in this test and the \"WasEmitted flag\" " +
            "line in .claude/rules/constraints.md. Live breakdown:" + Environment.NewLine + breakdown);
    }

    /// <summary>file (repo-relative) → count of real WasEmitted assignments in it.</summary>
    private static Dictionary<string, int> CollectAssignmentsPerFile()
    {
        string sourceRoot = GeneratorSourceRoot();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip build intermediates so a stale copy under bin/obj can't inflate the count.
            if (PathHasSegment(path, "bin") || PathHasSegment(path, "obj"))
                continue;

            int count = AssignmentPattern.Matches(File.ReadAllText(path)).Count;
            if (count > 0)
                // Key by the source-root-relative path (NOT the bare filename): two files sharing a
                // basename in different directories would otherwise clobber each other and silently
                // undercount both the assignment total and the file count.
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
