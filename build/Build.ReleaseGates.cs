// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.ReleaseGates.cs — composed release-gate orchestrator + result manifest
//
// Runs the release-relevant gate legs and writes a machine-readable manifest
// (ReleaseGatesManifest) recording every leg as pass | fail | skipped(reason).
// The point is to make the release lane prove what the gate catalog can prove,
// and to make a "skipped" leg impossible to mistake for a "passed" one.
//
// Composition, not redesign: no existing gate target is modified. The three
// heavyweight legs run as subprocesses of the ALREADY-BUILT _build assembly
// (typeof(Build).Assembly.Location) — never a recursive `dotnet nuke` / `dotnet
// run --project build/_build.csproj`, which would rebuild _build while the
// parent is live and multiply Compile across the legs. The appstore-hygiene
// STRUCTURAL leg has no dedicated target (its own target also runs the heavy
// device-IPA leg on a signing host), so it runs in-process via the extracted
// RunAppStoreHygieneStructuralOnly() helper.
//
// The heavy device / mixed-pack / mixed-direct / signed-IPA legs are recorded
// as skipped("not run in this invocation") so a release decision must explicitly
// disposition them. This target is intentionally NOT wired into CI — it is the
// RC-checklist primitive.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    [Parameter("ReleaseGates: also fail the target when any recorded skip is undispositioned (RC-strict mode). " +
               "Default off — intentional skips exit zero; ship-readiness is read from the manifest, not $?.")]
    readonly bool RequireComplete;

    [Parameter("ReleaseGatesAttest: catalog leg id to attest/disposition (e.g. binding-tests-device). " +
               "Omit (with --require-complete) to only check the persisted manifest.")]
    readonly string? Leg;

    [Parameter("ReleaseGatesAttest: attested result — 'pass' flips an attended leg to pass with evidence; " +
               "'waived'/'accepted' attaches a resolving disposition to a not-run skip.")]
    readonly string? Result;

    [Parameter("ReleaseGatesAttest: who is recording this (accountability, stamped into the manifest).")]
    readonly string? By;

    [Parameter("ReleaseGatesAttest: evidence path (leg log / artifact). Required for --result pass.")]
    readonly string? Evidence;

    [Parameter("ReleaseGatesAttest: manifest path override (defaults to the canonical release-gates artifact). " +
               "Point at a scratch copy for a dry run so the real artifact is untouched.")]
    readonly string? Manifest;

    AbsolutePath ReleaseGatesScratch => RootDirectory / "artifacts" / "release-gates";
    AbsolutePath ReleaseGatesManifestPath => ReleaseGatesScratch / "release-gates-manifest.json";

    // NOTE: intentionally NOT .DependsOn(Compile). Each executed leg subprocess builds what it needs
    // — Test and PackGate DependsOn(Compile); the BindingTests compile-only path runs its own
    // regen+compile pipeline — and the legs run sequentially against a shared on-disk build tree, so
    // an earlier leg's Compile persists for later ones. Keeping Compile off the orchestrator means a
    // build failure inside a leg is captured as that leg's manifest entry rather than aborting the
    // orchestrator before the manifest is written. The in-process structural leg self-provisions via DotNetPack.
    Target ReleaseGates => _ => _
        // Pure ordering edge: like SeedApiManifestBaseline, this is a standalone sink with no
        // dependents, so Nuke's total-order-over-sinks requirement rejects the plan ("Incomplete
        // target definition order") unless it is ordered against the other maintenance sinks. It
        // peels after the seed chain's last link (SeedApiManifestBaseline); the body never observes
        // the edge, and .After (unlike .DependsOn) does not pull the seed target into a solo run.
        .After(SeedApiManifestBaseline)
        .Description("Composes the release-relevant gate legs (unit tests, strict compile-only " +
                     "binding-tests, PackGate, appstore-hygiene structural) and writes a JSON result " +
                     "manifest recording every leg as pass|fail|skipped. Not wired into CI.")
        .Executes(() =>
        {
            var scratch = ReleaseGatesScratch;
            if (Directory.Exists(scratch)) scratch.DeleteDirectory();   // never leave stale evidence looking current
            scratch.CreateDirectory();

            // Seed the full canonical catalog up front: executed legs start as fail(not-reached) so an
            // orchestrator crash leaves a loud, honest manifest; the four not-run legs start as skips.
            var manifest = ReleaseGatesManifest.Seed(
                generatedUtc: DateTime.UtcNow.ToString("O"),
                gitSha: ReadHeadShaShort(),
                host: Environment.MachineName,
                invocation: "release-gates (macOS host; no device, no signing, no mixed/IPA legs)");
            manifest.Save(ReleaseGatesManifestPath);

            try
            {
                manifest = RunComposedTargetLeg(manifest, ReleaseGatesManifest.LegIds.UnitTests,
                    new[] { "Test" });
                manifest = RunComposedTargetLeg(manifest, ReleaseGatesManifest.LegIds.BindingTestsCompileOnly,
                    new[] { "BindingTests", "--strict", "--compile-only" });
                manifest = RunComposedTargetLeg(manifest, ReleaseGatesManifest.LegIds.PackGate,
                    new[] { "PackGate" });
                manifest = RunStructuralHygieneLeg(manifest);
            }
            finally
            {
                // Always persist the best-known manifest, even if the orchestrator threw outside a
                // leg's own catch — the artifact is the whole point.
                manifest.Save(ReleaseGatesManifestPath);
                LogReleaseGatesSummary(manifest);
            }

            var integrity = manifest.Validate();
            if (integrity.Count > 0)
            {
                foreach (var err in integrity)
                    Log.Error("  release-gates catalog integrity: {Error}", err);
                Assert.Fail($"ReleaseGates: manifest failed catalog integrity ({integrity.Count} error(s)) — " +
                            $"a leg row was dropped, duplicated, or malformed. Manifest: {ReleaseGatesManifestPath}");
            }

            if (manifest.AnyFailed)
            {
                var failed = manifest.Legs.Where(l => l.Status == GateLegStatus.Fail).Select(l => l.Id);
                Assert.Fail($"ReleaseGates: leg(s) failed [{string.Join(", ", failed)}]. " +
                            $"execution_outcome={manifest.ExecutionOutcome}, ship_ready={manifest.ShipReady}. " +
                            $"Manifest: {ReleaseGatesManifestPath}");
            }

            if (RequireComplete && manifest.UndispositionedSkipIds.Count > 0)
                Assert.Fail($"ReleaseGates --require-complete: {manifest.UndispositionedSkipIds.Count} " +
                            $"undispositioned skip(s): {string.Join(", ", manifest.UndispositionedSkipIds)}. " +
                            $"Manifest: {ReleaseGatesManifestPath}");

            Log.Information("ReleaseGates OK — execution_outcome={Outcome}, catalog_completeness={Completeness}, " +
                            "ship_ready={ShipReady}. Manifest: {Path}",
                manifest.ExecutionOutcome, manifest.CatalogCompleteness, manifest.ShipReady, ReleaseGatesManifestPath);
        });

    // Write path for the attended legs the macOS-host orchestrator seeds as skips. `ReleaseGates`
    // always wipes+reseeds, so its own --require-complete can never observe a post-hoc attest; this
    // target instead records the attended result into the ALREADY-PERSISTED manifest and evaluates
    // --require-complete against that loaded manifest (no reseed). Intended sequence:
    // orchestrate once -> attest N -> check; never re-orchestrate after attesting (the wipe is
    // intentional). Not wired into CI — an RC-checklist primitive like ReleaseGates itself.
    Target ReleaseGatesAttest => _ => _
        // Pure ordering edge continuing the sink chain: attest peels after ReleaseGates (the intended
        // sequence is orchestrate-once -> attest-N -> check). Satisfies Nuke's total-order-over-sinks
        // requirement without pulling ReleaseGates into a solo attest run (.After, not .DependsOn).
        .After(ReleaseGates)
        .Description("Records an ATTENDED release-gate leg's result into the persisted ReleaseGates " +
                     "manifest: --result pass flips an attended leg to pass with evidence; " +
                     "--result waived|accepted attaches a resolving disposition to a not-run skip. " +
                     "Refuses unknown legs, an evidence-less pass, and overwriting a real executed " +
                     "pass/fail. Honors --require-complete against the LOADED manifest (no reseed). " +
                     "Omit --leg/--result to only check. Not wired into CI.")
        .Executes(() =>
        {
            string manifestPath = Manifest is { Length: > 0 }
                ? Path.GetFullPath(Manifest, RootDirectory)
                : ReleaseGatesManifestPath;

            if (!File.Exists(manifestPath))
                Assert.Fail($"ReleaseGatesAttest: no manifest at {manifestPath}. Run `nuke release-gates` " +
                            "first — this target records attended results into an existing manifest.");

            var manifest = ReleaseGatesManifest.Load(manifestPath);

            // Refuse to mutate an already-broken artifact — fail loud with its integrity errors.
            var preErrors = manifest.Validate();
            if (preErrors.Count > 0)
            {
                foreach (var e in preErrors) Log.Error("  loaded-manifest integrity: {Error}", e);
                Assert.Fail($"ReleaseGatesAttest: loaded manifest is not catalog-sound ({preErrors.Count} " +
                            $"error(s)); refusing to attest onto it. Manifest: {manifestPath}");
            }

            var hasLeg = Leg is { Length: > 0 };
            var hasResult = Result is { Length: > 0 };
            if (hasLeg != hasResult)
                Assert.Fail("ReleaseGatesAttest: --leg and --result must be given together " +
                            "(or omit both to only check the manifest).");

            if (hasLeg)
            {
                var legId = Leg!;
                var result = Result!;
                if (result != GateLegStatus.Pass
                    && result != DispositionDecision.Accepted
                    && result != DispositionDecision.Waived)
                    Assert.Fail($"ReleaseGatesAttest: --result '{result}' is not one of " +
                                $"pass | {DispositionDecision.Waived} | {DispositionDecision.Accepted}.");

                var by = By ?? "";
                var atUtc = DateTime.UtcNow.ToString("O");
                try
                {
                    manifest = result == GateLegStatus.Pass
                        ? manifest.AttestPass(legId, by, Evidence ?? "", atUtc)
                        : manifest.DispositionSkip(legId, result, by, atUtc,
                            note: Evidence is { Length: > 0 } ? $"evidence: {Evidence}" : "");
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    Assert.Fail($"ReleaseGatesAttest: {ex.Message}");
                }

                // Defensive: our own write must keep the catalog sound before it hits disk.
                var postErrors = manifest.Validate();
                if (postErrors.Count > 0)
                {
                    foreach (var e in postErrors) Log.Error("  post-attest integrity: {Error}", e);
                    Assert.Fail($"ReleaseGatesAttest: the attest produced an unsound manifest " +
                                $"({postErrors.Count} error(s)); not saving. Manifest: {manifestPath}");
                }

                manifest.Save(manifestPath);
                Log.Information("ReleaseGatesAttest: recorded leg '{Leg}' as '{Result}' by '{By}' -> {Path}",
                    legId, result, by, manifestPath);
            }
            else
            {
                Log.Information("ReleaseGatesAttest: check-only (no --leg/--result). Manifest: {Path}", manifestPath);
            }

            LogReleaseGatesSummary(manifest);

            if (RequireComplete && manifest.RecommendedExitCode(requireComplete: true) != 0)
            {
                // Surface ALL three reasons a require-complete check can fail (a failed leg, an unsound
                // catalog, an undispositioned skip) — not just the skip list, so a run blocked by a
                // failed executed leg is not mis-read as "just needs more attests".
                var failedIds = manifest.Legs.Where(l => l.Status == GateLegStatus.Fail).Select(l => l.Id);
                Assert.Fail($"ReleaseGatesAttest --require-complete: manifest is not ship-ready — " +
                            $"execution_outcome={manifest.ExecutionOutcome}, ship_ready={manifest.ShipReady}, " +
                            $"catalog_sound={manifest.IsCatalogSound}, failed legs: [{string.Join(", ", failedIds)}], " +
                            $"undispositioned skips: [{string.Join(", ", manifest.UndispositionedSkipIds)}]. " +
                            $"Manifest: {manifestPath}");
            }

            Log.Information("ReleaseGatesAttest OK — ship_ready={ShipReady}, catalog_completeness={Completeness}. " +
                            "Manifest: {Path}", manifest.ShipReady, manifest.CatalogCompleteness, manifestPath);
        });

    // Runs one existing gate TARGET as a subprocess of the already-built _build assembly and records
    // its exit code as pass/fail. Never `dotnet nuke` / `dotnet run` (that rebuilds _build live).
    ReleaseGatesManifest RunComposedTargetLeg(ReleaseGatesManifest manifest, string legId, string[] targetAndFlags)
    {
        var buildDll = typeof(Build).Assembly.Location;
        var argLine = ArgumentEscaper.Join(new[] { buildDll }.Concat(targetAndFlags).ToArray());
        var logPath = ReleaseGatesScratch / $"{legId}.log";
        var relLog = RelativeManifestLog(logPath);
        Log.Information("=== release-gates leg '{Leg}': dotnet {Args} ===", legId, argLine);

        var sw = Stopwatch.StartNew();
        GateLeg leg;
        try
        {
            var proc = ProcessTasks.StartProcess("dotnet", argLine, workingDirectory: RootDirectory, logOutput: false)
                .AssertWaitForExit();
            sw.Stop();
            // Capture BOTH stdout and stderr (in emission order) — the failure reason points the
            // operator at this log, so a child that diagnoses to stderr must not leave it empty.
            File.WriteAllText(logPath, string.Join(Environment.NewLine, proc.Output.Select(o => o.Text)));
            leg = proc.ExitCode == 0
                ? GateLeg.Pass(legId, sw.ElapsedMilliseconds, relLog)
                : GateLeg.Fail(legId,
                    $"`dotnet {argLine}` exited {proc.ExitCode} — see {relLog}",
                    GateLegReasonCode.LegFailed, sw.ElapsedMilliseconds, relLog);
        }
        catch (Exception ex)
        {
            sw.Stop();
            File.WriteAllText(logPath, ex.ToString());
            leg = GateLeg.Fail(legId,
                $"leg threw before completing: {ex.Message}",
                GateLegReasonCode.LegFailed, sw.ElapsedMilliseconds, relLog);
        }

        var updated = manifest.WithLeg(leg);
        updated.Save(ReleaseGatesManifestPath);   // incremental persistence between legs
        Log.Information("    leg '{Leg}' -> {Status} ({Ms} ms)", legId, leg.Status, leg.DurationMs);
        return updated;
    }

    // The appstore-hygiene STRUCTURAL checks run in-process (no dedicated structural-only target exists;
    // --appstore-hygiene would also run the heavy device-IPA leg on a signing host). Behaviour-identical
    // to the structural prefix of RunAppStoreHygieneLeg.
    ReleaseGatesManifest RunStructuralHygieneLeg(ReleaseGatesManifest manifest)
    {
        const string legId = ReleaseGatesManifest.LegIds.AppStoreHygieneStructural;
        Log.Information("=== release-gates leg '{Leg}': appstore-hygiene structural checks (in-process) ===", legId);

        var sw = Stopwatch.StartNew();
        GateLeg leg;
        try
        {
            RunAppStoreHygieneStructuralOnly();
            sw.Stop();
            leg = GateLeg.Pass(legId, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            leg = GateLeg.Fail(legId,
                $"structural hygiene checks failed: {ex.Message}",
                GateLegReasonCode.LegFailed, sw.ElapsedMilliseconds);
        }

        var updated = manifest.WithLeg(leg);
        updated.Save(ReleaseGatesManifestPath);
        Log.Information("    leg '{Leg}' -> {Status} ({Ms} ms)", legId, leg.Status, leg.DurationMs);
        return updated;
    }

    void LogReleaseGatesSummary(ReleaseGatesManifest manifest)
    {
        Log.Information("================ release-gates summary ================");
        foreach (var leg in manifest.Legs)
        {
            // Surface the attended marker so an attested pass and a resolving disposition are visibly
            // distinct from a bare orchestrator pass / an undispositioned skip in the human summary.
            var detail = leg.Attestation is { } att
                ? $"attested pass by {att.By} ({leg.Log})"
                : leg.Disposition is { IsResolving: true } d
                    ? $"{d.Decision} by {d.By}"
                    : leg.Status == GateLegStatus.Pass ? "" : leg.Reason;
            Log.Information("  {Status,-8} {Leg,-30} {Detail}", leg.Status, leg.Id, detail);
        }
        Log.Information("  execution_outcome={Outcome}  catalog_completeness={Completeness}  ship_ready={ShipReady}",
            manifest.ExecutionOutcome, manifest.CatalogCompleteness, manifest.ShipReady);
        if (manifest.UndispositionedSkipIds.Count > 0)
            Log.Information("  undispositioned skips: {Skips}", string.Join(", ", manifest.UndispositionedSkipIds));
        Log.Information("======================================================");
    }

    string RelativeManifestLog(AbsolutePath logPath)
        => Path.GetRelativePath(RootDirectory, logPath).Replace('\\', '/');
}
