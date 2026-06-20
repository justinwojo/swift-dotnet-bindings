// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.WrapperStrip.cs — the harness Swift-wrapper post-processing leg + its fail-closed gate.
//
// Session 7b retired the bespoke harness stripper (the old `SwiftSourceStripper`: a hand-
// maintained 107-entry `PreservedProtocols` allowlist plus a swiftc-error retry oracle). It was
// a SECOND, divergent post-processor that over-stripped valid EveryProtocol conformances (their
// witness getters then surfaced as runtime `EntryPointNotFoundException`) and under-detected the
// internal-receiver case the generator's own scrub removes.
//
// The harness now scrubs the generated wrapper with the generator's OWN
// `SwiftWrapperPostProcessor.Process` — the exact oracle the generator-own wrapper compile uses
// (SwiftWrapperCompiler.cs) — reading the persisted `internalTypeNames` from `wrapper-context.json`.
// Same source + same scrub + same internal-type set ⇒ the harness wrapper is identical to the
// generator-own wrapper by construction. `Process` keeps every valid conformance (Pattern 1 only
// strips a conformance whose body references an internal/Swift-unavailable type) and removes
// exactly the blocks that cannot compile in a separate wrapper module.
//
// What it strips is gated, not silent: the committed baseline
// (`BindingTests/baselines.json` → `wrapper_stripped_count`) records the allowed
// `StrippedBlockTotal`, and the gate fails on any INCREASE — a NEW uncompilable emission the
// generator should never have produced (fix it at emission, not by stripping more). The count is
// now 0: Step 8a closed the sync internal-receiver case (`InternalHolder.describe`) AT EMISSION
// via `WrapperValidation.GetMemberRejectionReason` arm 2b (`parent_module_internal`) — the
// rejected `@_cdecl` wrapper falls back to a direct CallConvSwift P/Invoke instead of being
// emitted-then-scrubbed, so `Process` has nothing left to strip. The async / closure / operator
// internal-receiver shapes have no clean fallback and remain post-processor-scoped, so any of
// those re-appearing trips the fail-on-increase. Fail-closed mirrors the artifact-parity gate:
// `Strict || !Permissive`.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BindingsGeneration;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

/// <summary>
/// Thrown when the harness wrapper build finds the generator emitted MORE uncompilable Swift than
/// the committed baseline allows — a regression in the emitter, never something the harness should
/// paper over by stripping more.
/// </summary>
public sealed class WrapperStripTripwireException : Exception
{
    public WrapperStripTripwireException(string message) : base(message) { }
}

partial class Build
{
    /// <summary>The committed baseline file holding <c>wrapper_stripped_count</c> (and other test-pipeline baselines).</summary>
    AbsolutePath WrapperStripBaselinePath => BindingTestsDir / "baselines.json";

    /// <summary>
    /// Runs the generator's canonical wrapper post-processor over each generated wrapper file,
    /// writes the cleaned source into <paramref name="cleanedDir"/> for compilation, and returns a
    /// manifest of what was stripped. This REPLACES the bespoke harness stripper — same oracle,
    /// same <paramref name="internalTypeNames"/>, same <paramref name="currentModuleName"/> the
    /// generator-own wrapper compile uses, so the two wrappers match by construction.
    /// </summary>
    /// <param name="swiftFiles">Generated wrapper <c>.swift</c> files (SwiftUI bridge already excluded).</param>
    /// <param name="cleanedDir">Destination for post-processed source (the <c>.wrapper-build</c> dir).</param>
    /// <param name="internalTypeNames">Internal type names from <c>wrapper-context.json</c>; null skips internal stripping.</param>
    /// <param name="currentModuleName">The UNDERLYING Swift module (e.g. <c>SwiftBindingsTestLib</c>) — NOT the wrapper module name — so a <c>&lt;module&gt;.X</c> internal reference is matched.</param>
    /// <param name="site">Which leg this is, for the manifest + diagnostics.</param>
    WrapperStripManifest RunWrapperPostProcess(
        IReadOnlyList<string> swiftFiles,
        AbsolutePath cleanedDir,
        HashSet<string>? internalTypeNames,
        string currentModuleName,
        string site)
    {
        int strippedBlockTotal = 0;
        var bySubCause = new Dictionary<string, int>(StringComparer.Ordinal);
        var strippedSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var swiftFile in swiftFiles)
        {
            var content = File.ReadAllText(swiftFile);
            var result = SwiftWrapperPostProcessor.Process(
                content,
                internalTypeNames,
                onSafetyNetWarning: w => Log.Warning("Wrapper post-process safety-net ({Site}): {Warning}", site, w),
                currentModuleName: currentModuleName);

            strippedBlockTotal += result.StrippedBlockCount;
            foreach (var kv in result.StrippedBlocksBySubCause)
                bySubCause[kv.Key.ToString()] = bySubCause.GetValueOrDefault(kv.Key.ToString()) + kv.Value;
            strippedSymbols.UnionWith(result.StrippedSymbols);

            if (!string.IsNullOrWhiteSpace(result.CleanedContent))
                File.WriteAllText(cleanedDir / Path.GetFileName(swiftFile), result.CleanedContent);

            if (result.StrippedBlockCount > 0)
                Log.Information("  Post-processed {File} ({Site}): stripped {Count} block(s).",
                    Path.GetFileName(swiftFile), site, result.StrippedBlockCount);
        }

        return WrapperStripManifest.Build(site, strippedBlockTotal, bySubCause, strippedSymbols);
    }

    /// <summary>
    /// Reads <c>internalTypeNames</c> from the generator-persisted <c>wrapper-context.json</c> next
    /// to the wrapper source. Returns null if the file is absent (the generator wrote nothing to
    /// strip — <c>Process</c> then runs without internal-type stripping, matching old verbatim). A
    /// corrupt/unparseable file THROWS (fail-loud) rather than returning null: a null would silently
    /// skip internal-type stripping and emit an uncompilable wrapper. AOT-safe (<see cref="JsonDocument"/>,
    /// no reflection).
    /// </summary>
    static HashSet<string>? LoadInternalTypeNames(AbsolutePath contextPath)
    {
        if (!File.Exists(contextPath))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(contextPath));
            if (!doc.RootElement.TryGetProperty("internalTypeNames", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in arr.EnumerateArray())
            {
                var s = e.GetString();
                if (!string.IsNullOrEmpty(s))
                    set.Add(s);
            }
            return set;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Corrupt wrapper context {contextPath}: {ex.Message}. Re-run `nuke regenerate-bindings`.", ex);
        }
    }

    /// <summary>
    /// Reads the committed <c>wrapper_stripped_count</c> baseline. Absent/unreadable ⇒ -1, which the
    /// gate treats as "unbaselined" and fails closed on any strip (forces an explicit seed).
    /// </summary>
    int LoadWrapperStripBaseline()
    {
        if (!File.Exists(WrapperStripBaselinePath))
            return -1;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(WrapperStripBaselinePath));
            if (doc.RootElement.TryGetProperty("wrapper_stripped_count", out var v) &&
                v.ValueKind == JsonValueKind.Number)
                return v.GetInt32();
        }
        catch (JsonException) { /* fall through to unbaselined */ }
        return -1;
    }

    /// <summary>
    /// Fail-closed gate: the leg's <see cref="WrapperStripManifest.StrippedBlockTotal"/> must not
    /// EXCEED the committed baseline. An increase means the generator emitted a NEW wrapper that
    /// cannot compile in a separate module — a generator/emitter defect to fix at emission, never a
    /// reason to strip more. Fail-closed under <c>Strict || !Permissive</c> (the <c>--compile-only</c>
    /// contract); <c>--permissive</c> downgrades to a warning for local exploration.
    /// </summary>
    void EnforceWrapperStripTripwire(WrapperStripManifest manifest, int baseline, string site)
    {
        if (baseline >= 0 && manifest.StrippedBlockTotal <= baseline)
        {
            Log.Information(
                "Wrapper-strip gate ({Site}): {Count} block(s) stripped by the generator's post-processor (baseline {Baseline}).",
                site, manifest.StrippedBlockTotal, baseline);
            return;
        }

        bool failClosed = Strict || !Permissive;
        var subCauses = manifest.BySubCause.Count > 0
            ? string.Join(", ", manifest.BySubCause.Select(c => $"{c.SubCause}×{c.Count}"))
            : "none";
        var symbols = manifest.StrippedSymbols.Count > 0
            ? string.Join(", ", manifest.StrippedSymbols.Take(12)) + (manifest.StrippedSymbols.Count > 12 ? $"; … (+{manifest.StrippedSymbols.Count - 12} more)" : "")
            : "none";

        var message = baseline < 0
            ? $"Wrapper-strip gate ({site}): no committed baseline (wrapper_stripped_count missing from "
                + $"{WrapperStripBaselinePath}). The generator's post-processor stripped {manifest.StrippedBlockTotal} block(s) "
                + $"[{subCauses}; symbols: {symbols}]. Seed the baseline with `nuke seed-wrapper-strip-baseline`."
            : $"Wrapper-strip gate ({site}): the generator's post-processor stripped {manifest.StrippedBlockTotal} block(s), "
                + $"ABOVE the committed baseline of {baseline} [{subCauses}; symbols: {symbols}]. An increase means the generator "
                + $"emitted a NEW wrapper that cannot compile in a separate module — fix it at emission, do not strip more. "
                + $"If the increase is legitimate, reseed with `nuke seed-wrapper-strip-baseline`.";

        if (failClosed)
            throw new WrapperStripTripwireException(message);

        Log.Warning("{Message} (downgraded by --permissive.)", message);
    }

    /// <summary>
    /// THE migration oracle (Session 7b). The harness-built wrapper must export the IDENTICAL set of
    /// EveryProtocol witness-table getters as the generator's OWN wrapper. The artifact-parity gate
    /// (<see cref="RunParityGate"/>) diffs the generator-own wrapper — where every getter is always
    /// present — so it is structurally blind to a getter the harness drops; this positive set-equality
    /// diff is not. A getter in the generator-own wrapper but ABSENT from the harness wrapper is
    /// precisely the over-strip → runtime <c>EntryPointNotFoundException</c> failure class the
    /// inversion exists to prevent. Identical-by-construction now (same <c>Process</c>, same source),
    /// kept as a cheap structural backstop against future divergence. Fail-closed by default;
    /// <c>--permissive</c> warns.
    /// </summary>
    /// <param name="outputDir">The BindingTests output dir holding both wrapper xcframeworks.</param>
    /// <param name="harnessModule">Harness wrapper module/xcframework name (e.g. <c>SwiftBindings</c>).</param>
    /// <param name="generatorModule">Generator-own wrapper module/xcframework name (e.g. <c>SwiftBindingsTestLibSwiftBindings</c>).</param>
    /// <param name="site">Which leg is being checked, for diagnostics.</param>
    void EnforceWrapperGetterParity(AbsolutePath outputDir, string harnessModule, string generatorModule, string site)
    {
        var generatorBin = FindFrameworkBinary(outputDir / $"{generatorModule}.xcframework", generatorModule);
        var harnessBin = FindFrameworkBinary(outputDir / $"{harnessModule}.xcframework", harnessModule);
        if (generatorBin == null || harnessBin == null)
        {
            Log.Warning(
                "Wrapper getter-parity ({Site}): skipped — generator-own wrapper {Gen} and harness wrapper {Harness}.",
                site,
                generatorBin == null ? "MISSING" : "present",
                harnessBin == null ? "MISSING" : "present");
            return;
        }

        // @_cdecl getter symbol names are module-name-independent, so the two wrappers — built
        // from the SAME generated .swift, now scrubbed by the SAME Process — must export the
        // identical getter set.
        var generatorGetters = ArtifactParityGate.ParseNmSymbols(RunNm(generatorBin))
            .Where(ArtifactParityGate.IsWitnessTableSymbol).ToHashSet(StringComparer.Ordinal);
        var harnessGetters = ArtifactParityGate.ParseNmSymbols(RunNm(harnessBin))
            .Where(ArtifactParityGate.IsWitnessTableSymbol).ToHashSet(StringComparer.Ordinal);

        var missing = generatorGetters.Except(harnessGetters).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var extra = harnessGetters.Except(generatorGetters).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (missing.Count == 0 && extra.Count == 0)
        {
            Log.Information(
                "Wrapper getter-parity ({Site}): {Count} EveryProtocol witness getters identical across harness + generator wrappers.",
                site, harnessGetters.Count);
            return;
        }

        bool failClosed = Strict || !Permissive;
        var parts = new List<string>();
        if (missing.Count > 0)
            parts.Add($"{missing.Count} getter(s) the generator-own wrapper exports but the harness wrapper does NOT "
                + $"(over-strip → EntryPointNotFoundException): {string.Join(", ", missing.Take(12))}"
                + (missing.Count > 12 ? $"; … (+{missing.Count - 12} more)" : ""));
        if (extra.Count > 0)
            parts.Add($"{extra.Count} getter(s) only in the harness wrapper: {string.Join(", ", extra.Take(12))}"
                + (extra.Count > 12 ? $"; … (+{extra.Count - 12} more)" : ""));

        var message =
            $"Wrapper getter-parity ({site}): the harness-built wrapper '{harnessModule}' and the generator-own "
            + $"wrapper '{generatorModule}' export DIFFERENT EveryProtocol witness-getter sets — {string.Join("; ", parts)}. "
            + $"Both are scrubbed by the same Process, so a divergence is a generator/emitter defect.";

        if (failClosed)
            throw new WrapperStripTripwireException(message);

        Log.Warning("{Message} (downgraded by --permissive.)", message);
    }

    /// <summary>
    /// Reseeds <c>wrapper_stripped_count</c> in <c>BindingTests/baselines.json</c> to the block count
    /// the generator's post-processor currently strips from the main wrapper. Run after an
    /// intentional, reviewed emitter change that legitimately changes the count (e.g. an emission
    /// gate that closes a previously-stripped category). Recomputes from the current output rather
    /// than trusting a prior build artifact. Other baseline fields are preserved verbatim.
    /// </summary>
    Target SeedWrapperStripBaseline => _ => _
        .After(BindingTests)
        .Executes(() =>
        {
            var swiftFiles = Directory.GetFiles(BtOutputDir, "*.swift")
                .Where(f => !f.EndsWith(".SwiftUIBridge.swift"))
                .ToList();
            if (swiftFiles.Count == 0)
                throw new InvalidOperationException(
                    $"No wrapper Swift files in {BtOutputDir} — run `nuke regenerate-bindings` first.");

            var seedDir = BtOutputDir / ".wrapper-build-seed";
            if (Directory.Exists(seedDir))
                ((AbsolutePath)seedDir).DeleteDirectory();
            seedDir.CreateDirectory();
            var internalTypeNames = LoadInternalTypeNames(BtOutputDir / "wrapper-context.json");
            var manifest = RunWrapperPostProcess(swiftFiles, seedDir, internalTypeNames, ModuleName, "seed");
            ((AbsolutePath)seedDir).DeleteDirectory();

            var root = JsonNode.Parse(File.ReadAllText(WrapperStripBaselinePath))!.AsObject();
            var previous = root["wrapper_stripped_count"]?.GetValue<int>();
            root["wrapper_stripped_count"] = manifest.StrippedBlockTotal;
            File.WriteAllText(WrapperStripBaselinePath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

            Log.Information(
                "Seeded {Path}: wrapper_stripped_count {Prev} -> {Count} ({SubCauses}).",
                WrapperStripBaselinePath, previous?.ToString() ?? "unset", manifest.StrippedBlockTotal,
                manifest.BySubCause.Count > 0
                    ? string.Join(", ", manifest.BySubCause.Select(c => $"{c.SubCause}x{c.Count}"))
                    : "none");
        });
}
