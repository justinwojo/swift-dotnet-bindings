// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.ApiManifestGate.cs — F52 ABI-contract ratchet for `nuke binding-tests --compile-only`.
//
// After bindings are generated, the generator writes one `{Module}.api-manifest.json` per
// emitted module (see BindingsGeneration.ApiManifestEmitter), mapping each public member's
// post-collision C# signature to the native entry symbol its P/Invoke binds. This gate scans
// those manifests and diffs them against the committed baseline
// (`build/baselines/api-manifest-baseline.json`), FAILING when a stable `(module, signature)`
// now binds a DIFFERENT symbol — a silent ABI retarget, the exact hazard the F52 content-sorted
// overload-disambiguation rank closes at the source. The manifest + gate are the durable safety
// net: even if a future change reintroduces source-order-sensitive suffixing, the retarget is
// caught here before it ships. Added/removed members are reported but never fail the gate.
//
// Fail-closed by default (consistent with the rest of --compile-only); `--permissive` downgrades
// to warnings for local exploration. Reseed with `nuke SeedApiManifestBaseline` after an
// intentional, reviewed ABI change.
//
// The build project link-compiles individual generator source files rather than referencing the
// generator assembly, so this gate does NOT read the emitter's SchemaVersion constant directly:
// the schema is sourced from the emitted manifests (the live truth) and compared against the
// schema the baseline was seeded at, so an emitter schema bump surfaces here as a reseed prompt.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    AbsolutePath ApiManifestBaselinePath => BaselinesDir / "api-manifest-baseline.json";

    /// <summary>
    /// Runs the API-manifest ratchet against the freshly-generated manifests. Fail-closed unless
    /// <c>--permissive</c>. Invoked from the --compile-only path after the parity gate.
    /// </summary>
    void RunApiManifestGate(bool failClosed)
    {
        Log.Information("=========================================");
        Log.Information(" API-manifest ABI-contract gate");
        Log.Information("=========================================");

        // Resolve current manifests and load the baseline under one fail-open guard so
        // --permissive uniformly downgrades every setup failure (no manifests, a malformed
        // manifest, or a malformed committed baseline) to a warning. Under fail-closed any of
        // these propagates and fails loud.
        ScannedManifests scanned;
        ApiManifestBaseline baseline;
        try
        {
            scanned = ScanApiManifests();
            baseline = ApiManifestBaseline.Load(ApiManifestBaselinePath);
        }
        catch (Exception ex) when (!failClosed)
        {
            Log.Warning("API-manifest gate skipped (--permissive): {Message}", ex.Message);
            return;
        }

        if (scanned.Entries.Count == 0)
        {
            var msg = $"API-manifest gate: no `*.api-manifest.json` found under {BtOutputDir}. " +
                      "Run `nuke binding-tests --compile-only` (regenerates) first.";
            if (failClosed) throw new Exception(msg);
            Log.Warning(msg);
            return;
        }

        if (baseline.Entries.Count == 0)
        {
            var msg = $"API-manifest gate: baseline {ApiManifestBaselinePath.Name} is empty or missing. " +
                      "Seed it once with `nuke SeedApiManifestBaseline`.";
            if (failClosed) throw new Exception(msg);
            Log.Warning(msg);
            return;
        }

        if (baseline.SchemaVersion != scanned.SchemaVersion)
        {
            var msg = $"API-manifest schema mismatch: baseline v{baseline.SchemaVersion} vs emitted " +
                      $"v{scanned.SchemaVersion}. Reseed with `nuke SeedApiManifestBaseline`.";
            if (failClosed) throw new Exception(msg);
            Log.Warning(msg);
            return;
        }

        var (retargets, added, removed) = baseline.Compare(scanned.Entries);

        Log.Information("API-manifest gate: {Current} current member(s), {Baseline} baselined; " +
            "{Added} added, {Removed} removed.", scanned.Entries.Count, baseline.Entries.Count, added.Count, removed.Count);
        foreach (var line in added) Log.Information("  + {Line}", line);
        foreach (var line in removed) Log.Information("  - {Line}", line);

        if (retargets.Count > 0)
        {
            foreach (var line in retargets) Log.Error("  ✗ {Line}", line);
            var msg =
                $"API-manifest ABI-contract gate failed: {retargets.Count} symbol retarget(s) on a stable " +
                $"C# signature. A consumer-visible member silently rebound to different native code. Either " +
                $"fix the regression OR — if this is an intentional, reviewed ABI change — reseed " +
                $"{ApiManifestBaselinePath.Name} with `nuke SeedApiManifestBaseline` in the same commit.";
            // A retarget is the gate's headline failure, but --permissive (local exploration) uniformly
            // downgrades EVERY gate failure to a warning — including this one — matching the setup-failure
            // guards above and the contract documented at the top of this file. CI never passes
            // --permissive (--compile-only is fail-closed by default), so this cannot mask a real retarget
            // on the release path.
            if (failClosed) throw new Exception(msg);
            Log.Warning(msg);
            return;
        }

        Log.Information("API-manifest ABI-contract gate passed (no symbol retargets).");
    }

    /// <summary>
    /// Reads every <c>{Module}.api-manifest.json</c> under <see cref="BtOutputDir"/> and flattens
    /// them into <c>(module, signature, symbol)</c> rows plus the manifests' schema version (all
    /// emitted manifests share one generator, so a single version; the max is taken defensively).
    /// </summary>
    ScannedManifests ScanApiManifests()
    {
        var rows = new List<ApiManifestBaseline.ApiManifestBaselineEntry>();
        int schema = 0;
        if (!Directory.Exists(BtOutputDir)) return new ScannedManifests(schema, rows);

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var path in Directory.EnumerateFiles(BtOutputDir, "*.api-manifest.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var doc = JsonSerializer.Deserialize<EmittedApiManifest>(File.ReadAllText(path), options)
                ?? throw new Exception($"API-manifest gate: failed to parse {path}");
            schema = Math.Max(schema, doc.SchemaVersion);
            foreach (var m in doc.Members)
                rows.Add(new ApiManifestBaseline.ApiManifestBaselineEntry
                {
                    Module = doc.Module,
                    Signature = m.Signature,
                    Symbol = m.Symbol,
                });
        }

        return new ScannedManifests(schema, rows);
    }

    private readonly record struct ScannedManifests(
        int SchemaVersion,
        IReadOnlyList<ApiManifestBaseline.ApiManifestBaselineEntry> Entries);

    /// <summary>
    /// Manual baseline reseeder. Seeds <c>build/baselines/api-manifest-baseline.json</c> from the
    /// current generator output. Run once when this gate lands; thereafter, run again only as part
    /// of an intentional, reviewed ABI change (a member added/removed needs no reseed — only a
    /// retarget does, and that should be reviewed).
    ///
    /// The .After(...) edges satisfy Nuke `--strict`'s total-order-over-sinks requirement; the body
    /// never observes any of them, so the edges are pure ordering. This is a manual-maintenance sink,
    /// so it must be totally ordered against the OTHER maintenance sinks too (SeedSkipSurfaceBaseline →
    /// SeedParityBaseline → RegenStdlibConformances → SeedWrapperStripBaseline); it peels last, after
    /// the full chain. Omitting those edges leaves it co-equal with SeedWrapperStripBaseline and
    /// `--strict` rejects the plan with "Incomplete target definition order".
    /// </summary>
    Target SeedApiManifestBaseline => _ => _
        .After(BindingTests, BehaviorTier, ValidateBlastRadius, X64SimGate,
            SeedSkipSurfaceBaseline, SeedParityBaseline, RegenStdlibConformances, SeedWrapperStripBaseline)
        .Executes(() =>
        {
            var scanned = ScanApiManifests();
            if (scanned.Entries.Count == 0)
                throw new Exception(
                    $"Cannot seed: no `*.api-manifest.json` found under {BtOutputDir}. " +
                    "Run `nuke binding-tests --compile-only` first.");

            var baseline = new ApiManifestBaseline
            {
                SchemaVersion = scanned.SchemaVersion,
                GitSha = ReadHeadShaShort(),
                Entries = scanned.Entries
                    .OrderBy(e => e.Module, StringComparer.Ordinal)
                    .ThenBy(e => e.Signature, StringComparer.Ordinal)
                    .ToList(),
            };
            baseline.Save(ApiManifestBaselinePath);
            Log.Information("Seeded {Path} with {Count} entries (schema v{Schema}).",
                ApiManifestBaselinePath, scanned.Entries.Count, scanned.SchemaVersion);
        });

    // Local DTO mirroring the on-disk shape written by BindingsGeneration.ApiManifestEmitter.
    // Kept private to the gate so the build project needs no reference to the generator's model.
    private sealed class EmittedApiManifest
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("module")] public string Module { get; set; } = "";
        [JsonPropertyName("members")] public List<EmittedApiManifestMember> Members { get; set; } = new();
    }

    private sealed class EmittedApiManifestMember
    {
        [JsonPropertyName("signature")] public string Signature { get; set; } = "";
        [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    }
}
