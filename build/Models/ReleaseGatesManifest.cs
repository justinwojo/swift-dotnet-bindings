// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Machine-readable result manifest for the composed <c>ReleaseGates</c> target — the record
/// that makes "what the release lane actually proved" inspectable and makes a <b>skipped</b> leg
/// impossible to mistake for a <b>passed</b> one.
///
/// <para><b>Why two dimensions, not one <c>outcome</c>.</b> This invocation always records the
/// device / mixed-pack / mixed-direct / signed-IPA legs as <c>skipped</c> (they are not runnable
/// on a macOS host with no device or signing identity). If a single <c>outcome</c> string were
/// "passed only when every leg passed", a normal run could never be "passed" yet would still exit
/// zero — recreating exactly the "green exit that is not ship-ready" failure this manifest exists
/// to kill. So the model splits <see cref="ExecutionOutcome"/> ("did anything that ran fail?")
/// from <see cref="CatalogCompleteness"/> ("is every recorded skip dispositioned?"), and derives
/// <see cref="ShipReady"/> only from the conjunction. A green process exit is the default (skips
/// are intentional, not failures); an RC decision consults <see cref="ShipReady"/> /
/// <see cref="UndispositionedSkipIds"/> — never a bare <c>$?</c>.</para>
///
/// <para><b>Fail-closed catalog.</b> A dropped, duplicated, or renamed leg row is the silent-pass
/// hole. <see cref="CanonicalCatalog"/> is the frozen inventory of ship-blocking legs the manifest
/// MUST carry; <see cref="Validate"/> requires exactly one entry per catalog id (no missing, no
/// duplicate, no unknown, known status, every skip/fail carrying a reason). Integrity failures are
/// hard failures, never a zero-exit "incomplete".</para>
///
/// <para><b>Derived, never trusted from disk.</b> Aggregation (<see cref="AnyFailed"/>,
/// <see cref="ExecutionOutcome"/>, <see cref="CatalogCompleteness"/>, <see cref="ShipReady"/>,
/// <see cref="UndispositionedSkipIds"/>) is <c>[JsonIgnore]</c> and recomputed from
/// <see cref="Legs"/> on every access — a hand-tampered outcome key in the JSON cannot flip the
/// verdict. The serialized artifact is legs + metadata only.</para>
///
/// <para><b>Self-contained by design.</b> BCL-only (no Nuke) so the build's unit-test project can
/// link-compile and test the pure serialization / aggregation / integrity logic directly — the
/// same pattern as <see cref="RuntimeIdentityBaseline"/> and <c>ValidationBaseline</c>.</para>
/// </summary>
public record ReleaseGatesManifest
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Frozen identity of the ship-blocking leg inventory. Bump only when the set of
    /// legs a release must account for changes; a bump is a deliberate catalog change, and old
    /// manifests fail <see cref="Validate"/> against the new version.</summary>
    public const int CurrentCatalogVersion = 1;

    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonPropertyName("catalog_version")] public int CatalogVersion { get; init; } = CurrentCatalogVersion;
    [JsonPropertyName("generated_utc")] public string GeneratedUtc { get; init; } = "";
    [JsonPropertyName("git_sha")] public string GitSha { get; init; } = "";
    [JsonPropertyName("host")] public string Host { get; init; } = "";

    /// <summary>Human-readable description of what this invocation ran (host-only, no device,
    /// no signing, etc.) so the artifact is self-describing about its own coverage envelope.</summary>
    [JsonPropertyName("invocation")] public string Invocation { get; init; } = "";

    [JsonPropertyName("legs")] public IReadOnlyList<GateLeg> Legs { get; init; } = Array.Empty<GateLeg>();

    // ---- Canonical catalog (the fail-closed inventory) ----

    /// <summary>Stable, versioned identifiers for every ship-blocking leg the manifest must carry.
    /// Stringly-typed on purpose (house style, AOT/source-gen-safe — see <see cref="GateLegStatus"/>).</summary>
    public static class LegIds
    {
        public const string UnitTests = "unit-tests";
        public const string BindingTestsCompileOnly = "binding-tests-compile-only";
        public const string PackGate = "pack-gate";
        public const string AppStoreHygieneStructural = "appstore-hygiene-structural";
        public const string BindingTestsDevice = "binding-tests-device";
        public const string MixedPack = "mixed-pack";
        public const string MixedDirect = "mixed-direct";
        public const string AppStoreHygieneSignedIpa = "appstore-hygiene-signed-ipa";
    }

    /// <summary>The frozen catalog: exactly the legs a <c>ReleaseGates</c> manifest must account
    /// for (catalog v<see cref="CurrentCatalogVersion"/>). The first four run in a macOS-host
    /// invocation; the last four are recorded as dispositionable skips.</summary>
    public static readonly IReadOnlyList<string> CanonicalCatalog = new[]
    {
        LegIds.UnitTests,
        LegIds.BindingTestsCompileOnly,
        LegIds.PackGate,
        LegIds.AppStoreHygieneStructural,
        LegIds.BindingTestsDevice,
        LegIds.MixedPack,
        LegIds.MixedDirect,
        LegIds.AppStoreHygieneSignedIpa,
    };

    /// <summary>The subset the macOS-host, no-device, no-signing invocation actually executes.</summary>
    public static readonly IReadOnlyList<string> ExecutedLegIds = new[]
    {
        LegIds.UnitTests,
        LegIds.BindingTestsCompileOnly,
        LegIds.PackGate,
        LegIds.AppStoreHygieneStructural,
    };

    // ---- Aggregation: derived from Legs, never trusted from disk ([JsonIgnore]) ----

    [JsonIgnore] public bool AnyFailed => Legs.Any(l => l.Status == GateLegStatus.Fail);

    /// <summary>Outcome over the legs that <i>ran</i>: <see cref="OutcomeFailed"/> if any executed
    /// leg failed, else <see cref="OutcomePassed"/>. Says nothing about skipped legs — that is
    /// <see cref="CatalogCompleteness"/>'s job.</summary>
    [JsonIgnore] public string ExecutionOutcome => AnyFailed ? OutcomeFailed : OutcomePassed;

    /// <summary>Skipped catalog legs that carry no disposition — the legs a release decision still
    /// owes an explicit accept/waive/run-before-ship on. Ordered for stable reporting.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> UndispositionedSkipIds =>
        Legs.Where(l => l.Status == GateLegStatus.Skipped && (l.Disposition is null || !l.Disposition.IsResolving))
            .Select(l => l.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    /// <summary><see cref="CompletenessComplete"/> iff no skip is undispositioned; else
    /// <see cref="CompletenessIncomplete"/>. Empty leg list ⇒ complete-vacuously but never
    /// ship-ready (see <see cref="ShipReady"/>).</summary>
    [JsonIgnore]
    public string CatalogCompleteness =>
        UndispositionedSkipIds.Count == 0 ? CompletenessComplete : CompletenessIncomplete;

    /// <summary>The only field an RC decision should trust as "green means shippable": the catalog
    /// is structurally sound (<see cref="IsCatalogSound"/> — full, no dropped/duplicate/malformed
    /// row) AND nothing that ran failed AND every skip is dispositioned. Integrity is folded in on
    /// purpose: a dropped or malformed leg row must never derive a green verdict for a consumer that
    /// trusts this field without separately calling <see cref="Validate"/>.</summary>
    [JsonIgnore]
    public bool ShipReady =>
        IsCatalogSound && !AnyFailed && CatalogCompleteness == CompletenessComplete;

    /// <summary>Pure exit-code policy, testable without hosting Nuke. Non-zero if the catalog is
    /// unsound (a malformed manifest is a hard failure, never a zero-exit "incomplete") or any leg
    /// failed; in <paramref name="requireComplete"/> (RC-strict) mode also non-zero while any skip
    /// is undispositioned. Default mode exits zero on intentional skips of a sound catalog.</summary>
    public int RecommendedExitCode(bool requireComplete = false)
    {
        if (!IsCatalogSound || AnyFailed) return 1;
        if (requireComplete && UndispositionedSkipIds.Count > 0) return 1;
        return 0;
    }

    public const string OutcomeFailed = "failed";
    public const string OutcomePassed = "passed";
    public const string CompletenessComplete = "complete";
    public const string CompletenessIncomplete = "incomplete";

    // ---- Construction ----

    /// <summary>Seeds the full canonical catalog for an orchestrator run. Executed legs start as
    /// <c>fail(orchestrator_not_reached)</c> so that if the orchestrator crashes before running a
    /// leg the persisted manifest is loud (<see cref="AnyFailed"/> ⇒ non-zero), never a silent
    /// missing row; the four not-run legs start as dispositionable skips.</summary>
    public static ReleaseGatesManifest Seed(string generatedUtc = "", string gitSha = "", string host = "", string invocation = "")
    {
        var legs = new List<GateLeg>
        {
            GateLeg.NotReached(LegIds.UnitTests),
            GateLeg.NotReached(LegIds.BindingTestsCompileOnly),
            GateLeg.NotReached(LegIds.PackGate),
            GateLeg.NotReached(LegIds.AppStoreHygieneStructural),
            GateLeg.Skipped(LegIds.BindingTestsDevice,
                "device NativeAOT runtime leg not run in this invocation (host-only, no device)"),
            GateLeg.Skipped(LegIds.MixedPack,
                "mixed (ObjC+Swift) single-PackageReference iOS pack/consume leg not run in this invocation; " +
                "iOS loader / dual-registration is not proven by PackGate's macOS-host coverage"),
            GateLeg.Skipped(LegIds.MixedDirect,
                "mixed (ObjC+Swift) SDK-direct iOS consume leg not run in this invocation"),
            GateLeg.Skipped(LegIds.AppStoreHygieneSignedIpa,
                "signed-IPA TN2435 hygiene leg not run in this invocation (needs a codesigning identity; host-only)"),
        };
        return new ReleaseGatesManifest
        {
            GeneratedUtc = generatedUtc,
            GitSha = gitSha,
            Host = host,
            Invocation = invocation,
            Legs = legs,
        };
    }

    /// <summary>Returns a copy with the leg sharing <paramref name="leg"/>'s id replaced. A leg id
    /// not already present is appended (so a coding slip surfaces as an unknown/duplicate row in
    /// <see cref="Validate"/> rather than being silently swallowed).</summary>
    public ReleaseGatesManifest WithLeg(GateLeg leg)
    {
        var next = new List<GateLeg>(Legs.Count);
        var replaced = false;
        foreach (var existing in Legs)
        {
            if (!replaced && existing.Id == leg.Id)
            {
                next.Add(leg);
                replaced = true;
            }
            else
            {
                next.Add(existing);
            }
        }
        if (!replaced) next.Add(leg);
        return this with { Legs = next };
    }

    // ---- Catalog integrity ----

    /// <summary>Catalog integrity check: exactly one leg per <see cref="CanonicalCatalog"/> id
    /// (no missing, no duplicate, no unknown), a known status, and a reason on every skip/fail.
    /// Returns human-readable errors (empty ⇒ sound). This is the fail-closed guard against a
    /// dropped or malformed leg row masquerading as a green run.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (SchemaVersion != CurrentSchemaVersion)
            errors.Add($"schema_version {SchemaVersion} != expected {CurrentSchemaVersion}");
        if (CatalogVersion != CurrentCatalogVersion)
            errors.Add($"catalog_version {CatalogVersion} != expected {CurrentCatalogVersion}");

        var catalog = new HashSet<string>(CanonicalCatalog, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leg in Legs)
        {
            if (!seen.Add(leg.Id))
                errors.Add($"duplicate leg id '{leg.Id}'");
            if (!catalog.Contains(leg.Id))
                errors.Add($"unknown leg id '{leg.Id}' (not in catalog v{CurrentCatalogVersion})");
            if (!GateLegStatus.All.Contains(leg.Status))
                errors.Add($"leg '{leg.Id}' has unknown status '{leg.Status}'");
            if ((leg.Status == GateLegStatus.Skipped || leg.Status == GateLegStatus.Fail)
                && string.IsNullOrWhiteSpace(leg.Reason))
                errors.Add($"leg '{leg.Id}' is '{leg.Status}' without a reason");
            if (leg.Disposition is { } disp)
            {
                if (!DispositionDecision.All.Contains(disp.Decision))
                    errors.Add($"leg '{leg.Id}' has an unknown disposition decision '{disp.Decision}'");
                if (string.IsNullOrWhiteSpace(disp.By))
                    errors.Add($"leg '{leg.Id}' has a disposition with no 'by' (accountability)");
            }
        }
        foreach (var id in CanonicalCatalog)
            if (!seen.Contains(id))
                errors.Add($"missing required catalog leg '{id}'");

        return errors;
    }

    [JsonIgnore] public bool IsCatalogSound => Validate().Count == 0;

    // ---- Serialization (mirrors RuntimeIdentityBaseline) ----

    public static ReleaseGatesManifest Load(string path)
        => File.Exists(path) ? Parse(File.ReadAllText(path)) : new();

    public static ReleaseGatesManifest Parse(string json)
        => string.IsNullOrWhiteSpace(json)
            ? new()
            : JsonSerializer.Deserialize(json, ReleaseGatesManifestJsonContext.Default.ReleaseGatesManifest)
              ?? new();

    public void Save(string path) => File.WriteAllText(path, ToJson());

    public string ToJson()
        => JsonSerializer.Serialize(this, ReleaseGatesManifestJsonContext.Default.ReleaseGatesManifest);
}

/// <summary>One recorded gate leg. Status is a validated string (not a C# enum) to match house
/// style and keep source-gen / AOT / nullable-disable happy.</summary>
public record GateLeg
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = GateLegStatus.Skipped;
    [JsonPropertyName("reason_code")] public string ReasonCode { get; init; } = GateLegReasonCode.None;
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";

    /// <summary>Whether an unaddressed skip/fail on this leg should block a ship decision. Every
    /// catalog leg is ship-blocking today; the field exists so an informational leg can later be
    /// added without conflating it with a blocking one.</summary>
    [JsonPropertyName("required_for_ship")] public bool RequiredForShip { get; init; } = true;

    /// <summary>Repo-relative path to the captured leg output, when one was written.</summary>
    [JsonPropertyName("log")] public string? Log { get; init; }

    [JsonPropertyName("duration_ms")] public long? DurationMs { get; init; }

    /// <summary>Disposition overlay for a skip (accept / waive / run-before-ship). Always
    /// <c>null</c> in v1 — the socket exists so a release decision can be attached without a second
    /// sidecar format.</summary>
    [JsonPropertyName("disposition")] public LegDisposition? Disposition { get; init; }

    public static GateLeg Pass(string id, long? durationMs = null, string? log = null) =>
        new()
        {
            Id = id,
            Status = GateLegStatus.Pass,
            ReasonCode = GateLegReasonCode.None,
            Reason = "",
            DurationMs = durationMs,
            Log = log,
        };

    public static GateLeg Fail(string id, string reason,
        string reasonCode = GateLegReasonCode.LegFailed, long? durationMs = null, string? log = null) =>
        new()
        {
            Id = id,
            Status = GateLegStatus.Fail,
            ReasonCode = reasonCode,
            Reason = reason,
            DurationMs = durationMs,
            Log = log,
        };

    public static GateLeg Skipped(string id, string reason,
        string reasonCode = GateLegReasonCode.NotRunInThisInvocation) =>
        new()
        {
            Id = id,
            Status = GateLegStatus.Skipped,
            ReasonCode = reasonCode,
            Reason = reason,
        };

    /// <summary>An executed leg the orchestrator never reached (crashed / aborted before running
    /// it). Recorded as a <b>fail</b> so an incomplete run is loud rather than a silent gap.</summary>
    public static GateLeg NotReached(string id) =>
        Fail(id, "leg not reached — the orchestrator did not run it", GateLegReasonCode.OrchestratorNotReached);
}

/// <summary>Disposition overlay attached to a skip so a release decision is machine-checkable.
/// Always null in v1; the shape is fixed now so "disposition every skip before ship" needs no new
/// format later.</summary>
public record LegDisposition
{
    [JsonPropertyName("decision")] public string Decision { get; init; } = "";
    [JsonPropertyName("by")] public string By { get; init; } = "";
    [JsonPropertyName("at")] public string At { get; init; } = "";
    [JsonPropertyName("note")] public string Note { get; init; } = "";

    /// <summary>Whether this disposition actually <i>resolves</i> a skip's coverage gap — true only
    /// for a <see cref="DispositionDecision.Resolving"/> decision (accept / waive) carrying an owner
    /// in <see cref="By"/>. This is the fail-closed gate: an empty <c>{}</c>, an unknown decision,
    /// or a still-pending <see cref="DispositionDecision.RunBeforeShip"/> leaves the skip counted as
    /// undispositioned (see <see cref="ReleaseGatesManifest.UndispositionedSkipIds"/>) so it can
    /// never silently clear the catalog-completeness / ship-ready checks.</summary>
    [JsonIgnore]
    public bool IsResolving =>
        DispositionDecision.Resolving.Contains(Decision) && !string.IsNullOrWhiteSpace(By);
}

/// <summary>Closed set of disposition decisions. A skip is only <i>resolved</i> (removed from
/// <see cref="ReleaseGatesManifest.UndispositionedSkipIds"/>) by a <see cref="Resolving"/> decision;
/// <see cref="RunBeforeShip"/> is a known-but-non-resolving acknowledgment that the leg must still
/// run before ship. An unknown decision (including an empty one) fails <see cref="ReleaseGatesManifest.Validate"/>.</summary>
public static class DispositionDecision
{
    /// <summary>The skip is acceptable as-is for this release — resolves the coverage gap.</summary>
    public const string Accepted = "accepted";
    /// <summary>The skip is waived for this release — resolves the coverage gap.</summary>
    public const string Waived = "waived";
    /// <summary>Acknowledged, but the leg MUST still run before ship — does NOT resolve the gap.</summary>
    public const string RunBeforeShip = "run-before-ship";

    public static readonly IReadOnlyList<string> All = new[] { Accepted, Waived, RunBeforeShip };
    public static readonly IReadOnlyList<string> Resolving = new[] { Accepted, Waived };
}

/// <summary>Validated status values for <see cref="GateLeg.Status"/> — constants, not a C# enum
/// (matches <c>RuntimeIdentityBaseline.TestRecord.Status</c>; keeps source-gen + AOT clean).</summary>
public static class GateLegStatus
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Skipped = "skipped";

    public static readonly IReadOnlyList<string> All = new[] { Pass, Fail, Skipped };
}

/// <summary>Closed set of skip/fail reason codes (free-text detail lives in
/// <see cref="GateLeg.Reason"/>) so future disposition automation is not parsing prose.</summary>
public static class GateLegReasonCode
{
    /// <summary>Executed and passed — no reason code.</summary>
    public const string None = "";
    public const string LegFailed = "leg_failed";
    public const string OrchestratorNotReached = "orchestrator_not_reached";
    public const string NotRunInThisInvocation = "not_run_in_this_invocation";
}

/// <summary>Source-generation context — keeps (de)serialization AOT-safe (no reflection) so the
/// model link-compiles cleanly into the IsAotCompatible unit-test project, mirroring
/// <c>RuntimeIdentityBaselineJsonContext</c>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReleaseGatesManifest))]
internal partial class ReleaseGatesManifestJsonContext : JsonSerializerContext
{
}
