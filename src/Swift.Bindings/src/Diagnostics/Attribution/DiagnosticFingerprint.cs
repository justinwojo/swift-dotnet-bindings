// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using BindingsGeneration;

namespace BindingsGeneration.Diagnostics;

/// <summary>
/// A position-independent digest of a compile's error set, for detecting when a recovery loop is
/// making no progress.
/// </summary>
/// <remarks>
/// <para>
/// The fingerprint must be independent of position on purpose: every re-emission renders the
/// wrapper afresh, so a given failure lands on a different line each round even when nothing about
/// it changed. Keying on line numbers would make two identical failures look like progress. So the
/// fingerprint is the sorted <em>multiset</em> of normalized error messages — the messages carry
/// the stable content (the type names, the unsatisfied requirement) while the positions are
/// dropped. Multiplicity is kept, not collapsed: withdrawing one of two members that fail with the
/// same generic message ("generic parameter 'T' could not be inferred") drops the count from two
/// to one, which is real progress; collapsing to a distinct set would hide that and mis-fire the
/// no-progress detector after a genuine one-unit recovery.
/// </para>
/// <para>
/// Only errors participate. Warnings do not fail the compile and their presence or absence is not
/// progress; notes are evidence for a primary, never independent failures. Absolute paths inside a
/// message (linker transcripts embed them) are normalized away so a run in a different temp
/// directory does not read as a different failure.
/// </para>
/// </remarks>
public static class DiagnosticFingerprint
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex AbsolutePath = new(@"/[^\s""']+", RegexOptions.Compiled);

    /// <summary>
    /// Computes the fingerprint of the error diagnostics in <paramref name="groups"/>. Two compiles
    /// with the same multiset of error messages (in any order, at any positions) produce the same
    /// string; changing how many times a message occurs changes the fingerprint.
    /// </summary>
    public static string Compute(IReadOnlyList<DiagnosticGroup> groups)
    {
        if (groups is null || groups.Count == 0)
            return EmitterUtility.DeterministicHash8(string.Empty);

        // Group by normalized message and keep the count: the fingerprint is a multiset, so N
        // copies of a message differ from N-1. Sorting by message makes it order-independent.
        var normalized = groups
            .Where(g => g.Primary.Severity == DiagnosticSeverity.Error)
            .Select(g => Normalize(g.Primary.Message))
            .GroupBy(m => m, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()}×{g.Key}");

        return EmitterUtility.DeterministicHash8(string.Join("\n", normalized));
    }

    /// <summary>Normalizes one message: paths elided, whitespace collapsed, trimmed.</summary>
    internal static string Normalize(string message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;
        var withoutPaths = AbsolutePath.Replace(message, "<path>");
        return Whitespace.Replace(withoutPaths, " ").Trim();
    }
}

/// <summary>
/// Decides when a recovery loop should stop iterating on the same granularity and escalate instead.
/// </summary>
/// <remarks>
/// Two independent signals mean "iterating here is not converging": the exact same error set two
/// rounds running (the denylist increment changed nothing the compiler sees), or a round that found
/// errors but could attribute none of them to a unit (nothing to add to the denylist). Either way
/// the fix is to widen the recovery granularity — escalate a member to its type, a type to the
/// module — which the loop does; this detector only names the condition. It is pure so the loop's
/// termination logic is unit-testable without running a compile.
/// </remarks>
public static class NoProgressDetector
{
    /// <summary>True when the two most recent rounds share a fingerprint.</summary>
    public static bool IsRepeatedFingerprint(IReadOnlyList<string> fingerprintHistory)
    {
        if (fingerprintHistory is null || fingerprintHistory.Count < 2)
            return false;
        return string.Equals(
            fingerprintHistory[^1], fingerprintHistory[^2], StringComparison.Ordinal);
    }

    /// <summary>True when a round produced errors but attributed none of them to a unit.</summary>
    public static bool AttributedNothing(AttributionResult latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        return latest.ErrorCount > 0 && latest.Culprits.IsEmpty;
    }

    /// <summary>
    /// True when the loop should escalate granularity rather than iterate again: either a repeated
    /// fingerprint or a round that attributed nothing.
    /// </summary>
    public static bool ShouldEscalate(IReadOnlyList<string> fingerprintHistory, AttributionResult latest)
        => IsRepeatedFingerprint(fingerprintHistory) || AttributedNothing(latest);
}
