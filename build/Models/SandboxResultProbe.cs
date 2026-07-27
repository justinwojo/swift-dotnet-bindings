// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Self-contained nullable context so this file compiles identically whether built in the Nuke
// build assembly or link-compiled into the unit-test project.
#nullable enable

using System;
using System.Collections.Generic;

/// <summary>
/// Picks the results file that belongs to THIS launch out of the candidate paths an app's sandbox
/// may hold it at, with the run-token fail-closed rule applied per candidate.
///
/// <para><b>Why more than one candidate.</b> The runners write to
/// <c>Environment.SpecialFolder.MyDocuments</c>, which does not resolve to
/// <c>&lt;container&gt;/Documents</c> on every Apple platform — tvOS has no persistent per-app
/// Documents directory, so the runtime maps it to <c>&lt;container&gt;/Library/Caches/Documents</c>.
/// The harness probes both shapes rather than branching on platform.</para>
///
/// <para><b>The defect this closes.</b> The probe used to abandon the whole search the moment its
/// FIRST existing candidate carried a mismatched token, so a stale <c>Documents/</c> file left behind
/// by an earlier iOS run masked the live tvOS results sitting at the <c>Library/Caches/</c> path: a
/// complete, correctly-tokened result was thrown away and the leg fell back to console-scraping or a
/// blind class skip. A token mismatch disqualifies THAT candidate; it says nothing about the others.
/// </para>
///
/// <para><b>What is preserved.</b> The fail-closed property that motivated the token check in the
/// first place: the app's data container survives install, so an untokened or stale-tokened file may
/// never be scored as this launch's results. If no candidate carries a matching token the probe
/// reports no content at all — never a best-effort fallback to the stale file — and every rejected
/// candidate is returned so the caller can log it and a stale container stays visible.</para>
/// </summary>
public static class SandboxResultProbe
{
    /// <summary>A candidate that existed but belongs to some earlier launch.</summary>
    /// <param name="Path">Where the stale file was found.</param>
    /// <param name="ActualToken">The token it carries, or <c>&lt;none&gt;</c> when it carries none.</param>
    public readonly record struct StaleCandidate(string Path, string ActualToken);

    /// <summary>The outcome of one probe: at most one accepted file, plus every rejected candidate.</summary>
    public sealed class ProbeOutcome
    {
        /// <summary>The accepted file's contents, or null when no candidate carried a matching token.</summary>
        public string? Content { get; init; }

        /// <summary>Where <see cref="Content"/> came from, for logging. Null when nothing was accepted.</summary>
        public string? AcceptedPath { get; init; }

        /// <summary>
        /// Candidates that existed but carried the wrong token, in probe order. Populated whether or
        /// not a later candidate was accepted — a stale file is worth reporting either way.
        /// </summary>
        public IReadOnlyList<StaleCandidate> Rejected { get; init; } = Array.Empty<StaleCandidate>();
    }

    /// <summary>
    /// Probes <paramref name="candidatePaths"/> in order and returns the first one whose contents
    /// carry <paramref name="expectedRunToken"/>.
    /// </summary>
    /// <param name="candidatePaths">Paths to try, most-likely first.</param>
    /// <param name="expectedRunToken">The token this launch handed the app via <c>--run-token</c>.</param>
    /// <param name="readIfPresent">
    /// Reads a candidate's contents, returning null when it does not exist. Injected so the decision
    /// logic is testable without a simulator: the caller supplies the file (or device-copy) access.
    /// </param>
    public static ProbeOutcome Probe(
        IEnumerable<string> candidatePaths,
        string expectedRunToken,
        Func<string, string?> readIfPresent)
    {
        var rejected = new List<StaleCandidate>();

        foreach (var path in candidatePaths)
        {
            var content = readIfPresent(path);
            if (content == null)
                continue;

            if (!JsonlTestResults.HasMatchingRunToken(content, expectedRunToken))
            {
                rejected.Add(new StaleCandidate(path, JsonlTestResults.ExtractRunToken(content) ?? "<none>"));
                continue;
            }

            return new ProbeOutcome { Content = content, AcceptedPath = path, Rejected = rejected };
        }

        return new ProbeOutcome { Rejected = rejected };
    }
}
