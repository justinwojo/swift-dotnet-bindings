// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Typed model for <c>build/baselines/runtime-identity-baseline.json</c>. For every shared runtime
/// lane this is the single source for both scalar counts and per-test non-pass identities. Mirrors
/// the <see cref="SkipSurfaceBaseline"/> ratchet shape (<c>Load</c>/<c>Save</c>/<c>Compare</c>,
/// same-commit-update escape hatch).
///
/// <para><b>Why retain both views.</b> The count comparison (<c>currentPass &lt; baselinePass</c>)
/// nets out per-test churn: a test that flips <c>pass → skip</c> while a sibling flips
/// <c>→ pass</c> leaves the pass count unchanged and stays green. This model gates on the
/// identity of each non-pass test so that churn is caught.</para>
///
/// <para><b>Self-contained by design.</b> This file is BCL-only (no Nuke, no
/// <c>JsonlTestResults</c> dependency) so the build's unit-test project can link-compile and
/// test the pure <c>Compare</c>/<c>FromResults</c> logic directly — the same pattern as
/// <c>ArtifactParityGate</c>. The Nuke build adapts its <c>JsonlTestResults</c> into the
/// <see cref="TestRecord"/> shape at the call site.</para>
///
/// <para><b>What is stored.</b> Per platform key (<c>"simulator"</c>, <c>"device"</c>, …) a
/// pass count plus the verbatim list of <i>non-pass</i> test identities (skips and, normally
/// empty, known-fails). Pass is the default — represented only by the count — so a fully green
/// suite serializes to empty lists and the file stays small and reviewable.</para>
///
/// <para><b>Keying.</b> A test identity is keyed on <c>(Class, Method)</c> only. <c>Reason</c>
/// is stored for human readability but is <i>not</i> part of the key, so re-wording a skip
/// message is not a regression.</para>
///
/// <para><b>Ratchet semantics</b> (mirrors <see cref="SkipSurfaceBaseline.Compare"/>):
/// <list type="bullet">
///   <item><description>A current <c>skip</c>/<c>fail</c>/<c>crash</c> whose <c>(Class, Method)</c>
///     is not in the baseline's corresponding list ⇒ <b>regression</b> (must be added to the
///     baseline in the same commit). This is what catches <c>pass → skip</c>: a previously-passing
///     test is, by construction, absent from the baseline skip list, so its appearance as a current
///     skip is a new key.</description></item>
///   <item><description><c>currentPass &lt; baseline.PassCount</c> ⇒ <b>regression</b> (the existing
///     scalar rule, kept here so the model is self-contained; this is also what catches a
///     <c>pass → absent</c> deletion, since passes are not stored by name).</description></item>
///   <item><description>A baseline skip that is now <c>pass</c> or gone ⇒ <b>improvement</b>.</description></item>
///   <item><description>The pure <see cref="Compare"/> helper returns no regressions for an
///     unseeded platform. The shipping pipeline calls <see cref="GetRequiredCounts"/> first and
///     therefore fails closed when a required shared-lane entry is absent.</description></item>
/// </list></para>
/// </summary>
public record RuntimeIdentityBaseline
{
    [JsonPropertyName("git_sha")] public string GitSha { get; init; } = "";

    /// <summary>
    /// Per-platform identity sets, keyed by the normalized platform key
    /// (<c>"simulator"</c>, <c>"device"</c>, <c>"macos"</c>, …). A plain dictionary round-trips
    /// cleanly here because the keys are simple strings (unlike <see cref="SkipSurfaceBaseline"/>'s
    /// composite keys, which are stored as a flat array).
    /// </summary>
    [JsonRequired]
    [JsonPropertyName("platforms")]
    public IReadOnlyDictionary<string, PlatformIdentities> Platforms { get; init; }
        = new Dictionary<string, PlatformIdentities>();

    /// <summary>A single test result, decoupled from the build's <c>JsonlTestResults</c> parser.</summary>
    public readonly record struct TestRecord(string Class, string Method, string Status, string Reason);

    public record PlatformIdentities
    {
        [JsonRequired]
        [JsonPropertyName("pass_count")] public int PassCount { get; init; }

        /// <summary>Test identities recorded as <c>skip</c> — the expected skip set.</summary>
        [JsonRequired]
        [JsonPropertyName("skips")]
        public IReadOnlyList<TestId> Skips { get; init; } = Array.Empty<TestId>();

        /// <summary>
        /// Test identities recorded as <c>fail</c>/<c>crash</c>. Normally empty — a live fail/crash
        /// is already forced to a hard failure upstream by the <c>effectiveResult</c> switch. Kept
        /// so the model is symmetric and a deliberately-tracked known-fail can be baselined.
        /// </summary>
        [JsonRequired]
        [JsonPropertyName("known_fails")]
        public IReadOnlyList<TestId> KnownFails { get; init; } = Array.Empty<TestId>();
    }

    /// <summary>Scalar view mechanically derived from one identity record.</summary>
    public readonly record struct BaselineCounts(int Pass, int Skip, int Fail, int Crash);

    public record TestId
    {
        [JsonPropertyName("class")] public string Class { get; init; } = "";
        [JsonPropertyName("method")] public string Method { get; init; } = "";
        [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    }

    public static RuntimeIdentityBaseline Load(string path)
        => File.Exists(path) ? Parse(File.ReadAllText(path)) : new();

    /// <summary>Parses baseline JSON (empty/whitespace ⇒ a fresh empty baseline).</summary>
    public static RuntimeIdentityBaseline Parse(string json)
        => string.IsNullOrWhiteSpace(json)
            ? new()
            : JsonSerializer.Deserialize(json, RuntimeIdentityBaselineJsonContext.Default.RuntimeIdentityBaseline)
              ?? new();

    public void Save(string path) => File.WriteAllText(path, ToJson());

    public string ToJson()
        => JsonSerializer.Serialize(this, RuntimeIdentityBaselineJsonContext.Default.RuntimeIdentityBaseline);

    /// <summary>
    /// Collapses a run into the per-platform identity set used both for seeding and for the
    /// green-only auto-update. Duplicate <c>(Class, Method)</c> rows (e.g. crash-recovery merges)
    /// are de-duplicated last-wins, matching <c>AbiGridReporter</c>'s per-runtime index.
    /// </summary>
    public static PlatformIdentities FromResults(IReadOnlyList<TestRecord> tests)
    {
        var byKey = DedupByIdentity(tests);
        return new PlatformIdentities
        {
            PassCount = byKey.Values.Count(t => t.Status == "pass"),
            Skips = byKey.Values
                .Where(t => t.Status == "skip")
                .OrderBy(t => t.Class, StringComparer.Ordinal)
                .ThenBy(t => t.Method, StringComparer.Ordinal)
                .Select(t => new TestId { Class = t.Class, Method = t.Method, Reason = t.Reason })
                .ToList(),
            KnownFails = byKey.Values
                .Where(t => t.Status is "fail" or "crash")
                .OrderBy(t => t.Class, StringComparer.Ordinal)
                .ThenBy(t => t.Method, StringComparer.Ordinal)
                .Select(t => new TestId { Class = t.Class, Method = t.Method, Reason = t.Reason })
                .ToList(),
        };
    }

    /// <summary>
    /// Returns the scalar view of a required platform entry. Missing or structurally invalid
    /// identity data fails closed so a shipping lane cannot silently become ungraded.
    /// </summary>
    public BaselineCounts GetRequiredCounts(string platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            throw new InvalidDataException("Runtime identity baseline platform key is empty.");
        if (Platforms == null || !Platforms.TryGetValue(platform, out var identities) || identities == null)
            throw new InvalidDataException(
                $"Runtime identity baseline has no '{platform}' entry; the lane cannot be graded.");

        ValidateIdentities(platform, identities);
        return new BaselineCounts(
            identities.PassCount,
            identities.Skips.Count,
            identities.KnownFails.Count,
            Crash: 0);
    }

    /// <summary>Returns a copy with one platform's identity set replaced/added.</summary>
    public RuntimeIdentityBaseline WithPlatform(string platform, PlatformIdentities identities)
    {
        var next = new Dictionary<string, PlatformIdentities>(StringComparer.Ordinal);
        foreach (var (k, v) in Platforms)
            next[k] = v;
        next[platform] = identities;
        return this with { Platforms = next };
    }

    /// <summary>
    /// Diffs a platform's current results against the baseline. See the class-level ratchet
    /// semantics. Returns no regressions for a platform that has no baseline entry yet (inert until
    /// seeded), so wiring this gate in before the baseline is seeded never produces a false red.
    /// </summary>
    public (IReadOnlyList<string> Regressions, IReadOnlyList<string> Improvements) Compare(
        string platform, IReadOnlyList<TestRecord> currentTests)
    {
        var regressions = new List<string>();
        var improvements = new List<string>();

        if (!Platforms.TryGetValue(platform, out var baselinePlat))
            return (regressions, improvements);

        var currentByKey = DedupByIdentity(currentTests);
        var baseSkips = new HashSet<(string, string)>(
            baselinePlat.Skips.Select(s => (s.Class, s.Method)));
        var baseFails = new HashSet<(string, string)>(
            baselinePlat.KnownFails.Select(s => (s.Class, s.Method)));

        // A current non-pass identity not present in the baseline's matching list is a new key
        // ⇒ regression. (A `pass → skip` is caught here because a previously-passing test is, by
        // construction, absent from the baseline skip list.)
        foreach (var (key, entry) in currentByKey)
        {
            switch (entry.Status)
            {
                case "skip" when !baseSkips.Contains(key):
                    regressions.Add(
                        $"NEW skip {key.Item1}.{key.Item2} on {platform} " +
                        $"('{entry.Reason}') — was passing or is a new test; add to baseline if intentional");
                    break;
                case "fail" when !baseFails.Contains(key):
                case "crash" when !baseFails.Contains(key):
                    regressions.Add(
                        $"NEW {entry.Status} {key.Item1}.{key.Item2} on {platform} ('{entry.Reason}')");
                    break;
            }
        }

        // A baseline skip that is now pass or gone ⇒ improvement.
        foreach (var skip in baselinePlat.Skips)
        {
            var key = (skip.Class, skip.Method);
            if (!currentByKey.TryGetValue(key, out var cur))
                improvements.Add($"GONE skip {skip.Class}.{skip.Method} on {platform} (test removed)");
            else if (cur.Status == "pass")
                improvements.Add($"RESOLVED skip {skip.Class}.{skip.Method} on {platform} (now passing)");
        }

        // Scalar pass-count floor, kept so the model is self-contained and so a `pass → absent`
        // deletion (passes are not stored by identity) is still caught.
        var currentPass = currentByKey.Values.Count(t => t.Status == "pass");
        if (currentPass < baselinePlat.PassCount)
            regressions.Add(
                $"pass count dropped on {platform}: {baselinePlat.PassCount} → {currentPass}");

        return (regressions, improvements);
    }

    private static Dictionary<(string, string), TestRecord> DedupByIdentity(
        IReadOnlyList<TestRecord> tests)
    {
        if (tests == null)
            throw new ArgumentNullException(nameof(tests));

        var byKey = new Dictionary<(string, string), TestRecord>();
        foreach (var t in tests)
        {
            if (string.IsNullOrWhiteSpace(t.Class) || string.IsNullOrWhiteSpace(t.Method))
                throw new InvalidDataException("Runtime result identity must contain non-empty class and method names.");
            if (t.Status is not ("pass" or "skip" or "fail" or "crash"))
                throw new InvalidDataException(
                    $"Runtime result {t.Class}.{t.Method} has unsupported status '{t.Status}'.");
            byKey[(t.Class, t.Method)] = t; // last wins (crash-recovery merge already applied)
        }
        return byKey;
    }

    private static void ValidateIdentities(string platform, PlatformIdentities identities)
    {
        if (identities.PassCount < 0)
            throw new InvalidDataException(
                $"Runtime identity baseline '{platform}' has negative pass_count {identities.PassCount}.");
        if (identities.Skips == null)
            throw new InvalidDataException($"Runtime identity baseline '{platform}' has null skips.");
        if (identities.KnownFails == null)
            throw new InvalidDataException($"Runtime identity baseline '{platform}' has null known_fails.");

        var seen = new HashSet<(string Class, string Method)>();
        ValidateList(identities.Skips, "skips", platform, seen);
        ValidateList(identities.KnownFails, "known_fails", platform, seen);
    }

    private static void ValidateList(
        IReadOnlyList<TestId> identities,
        string listName,
        string platform,
        HashSet<(string Class, string Method)> seen)
    {
        foreach (var identity in identities)
        {
            if (identity == null || string.IsNullOrWhiteSpace(identity.Class) ||
                string.IsNullOrWhiteSpace(identity.Method))
            {
                throw new InvalidDataException(
                    $"Runtime identity baseline '{platform}' contains an incomplete {listName} identity.");
            }

            if (!seen.Add((identity.Class, identity.Method)))
            {
                throw new InvalidDataException(
                    $"Runtime identity baseline '{platform}' repeats identity " +
                    $"{identity.Class}.{identity.Method} across its non-pass lists.");
            }
        }
    }
}

/// <summary>Source-generation context for <see cref="RuntimeIdentityBaseline"/> — keeps
/// (de)serialization AOT-safe (no reflection) so the model link-compiles cleanly into the
/// IsAotCompatible unit-test project, mirroring <c>ParityBaselineJsonContext</c>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RuntimeIdentityBaseline))]
internal partial class RuntimeIdentityBaselineJsonContext : JsonSerializerContext
{
}
