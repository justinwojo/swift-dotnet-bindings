// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Self-contained nullable context so this file compiles identically whether built in the Nuke
// build assembly or link-compiled into the unit-test project.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>The prefix the runtime test apps log their results path behind, once per launch.</summary>
    public const string AnnouncementMarker = "JSONL output: ";

    const string ResultsFileName = "test-results.jsonl";

    /// <summary>
    /// The results path a simulator app announced in its own console output
    /// (<c>JSONL output: &lt;path&gt;</c>), or null when it announced none this harness may read.
    ///
    /// <para><b>Why the harness needs it.</b> The container path normally comes from
    /// <c>simctl get_app_container</c>, a separate CoreSimulator query made after the app has been
    /// terminated. On a loaded CI runner that query has returned nothing for a run that completed
    /// and flushed its results, and the leg was then failed for having no results artifact. The app
    /// printed where it wrote the file at the start of that same launch, and a simulator container is
    /// an ordinary host directory, so that path is a second way to reach the file.</para>
    ///
    /// <para><b>What it does not relax.</b> The announced path is only another candidate for
    /// <see cref="Probe"/>, so the file it names is accepted only when it carries this launch's run
    /// token. It is also confined to the app data containers of the simulator being tested (the
    /// <paramref name="udid"/> path segment), names the results file, and may not climb out with
    /// <c>..</c>; any other announcement is ignored rather than read.</para>
    /// </summary>
    /// <param name="appOutput">This launch's captured console output.</param>
    /// <param name="udid">The simulator the launch ran on.</param>
    public static string? ParseAnnouncedResultsPath(string? appOutput, string udid)
    {
        if (string.IsNullOrEmpty(appOutput) || string.IsNullOrEmpty(udid))
            return null;

        string? announced = null;
        foreach (var line in appOutput.Split('\n'))
        {
            var at = line.IndexOf(AnnouncementMarker, StringComparison.Ordinal);
            if (at < 0)
                continue;
            // Last announcement wins: a launch announces once, and anything later in the same
            // capture is closer to the file the run actually finished writing.
            announced = line.Substring(at + AnnouncementMarker.Length).Trim();
        }

        if (string.IsNullOrEmpty(announced) || !announced.StartsWith("/", StringComparison.Ordinal))
            return null;
        if (!announced.EndsWith("/" + ResultsFileName, StringComparison.Ordinal))
            return null;
        if (announced.Contains("/../") || announced.Contains("/./"))
            return null;
        if (announced.IndexOf($"/{udid}/data/Containers/Data/Application/", StringComparison.OrdinalIgnoreCase) < 0)
            return null;

        return announced;
    }

    /// <summary>
    /// The candidate results paths for a simulator launch: both container shapes under
    /// <paramref name="containerPath"/> (when the container lookup succeeded), then the app's own
    /// <paramref name="announcedPath"/> when it is not already one of them. Container-derived paths
    /// come first because they are the harness's own query; the announcement is the fallback.
    /// </summary>
    public static IReadOnlyList<string> SimulatorCandidates(string? containerPath, string? announcedPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(containerPath))
        {
            // MyDocuments is <container>/Documents on iOS and <container>/Library/Caches/Documents
            // on tvOS (no persistent per-app Documents directory there).
            candidates.Add(System.IO.Path.Combine(containerPath, "Documents", ResultsFileName));
            candidates.Add(System.IO.Path.Combine(containerPath, "Library", "Caches", "Documents", ResultsFileName));
        }

        if (!string.IsNullOrEmpty(announcedPath) && !candidates.Contains(announcedPath, StringComparer.Ordinal))
            candidates.Add(announcedPath);

        return candidates;
    }
}
