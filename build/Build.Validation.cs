// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    // --- Computed validation paths ---
    AbsolutePath GeneratorProject => RootDirectory / "src" / "Swift.Bindings" / "src" / "Swift.Bindings.csproj";
    AbsolutePath RuntimeProject => RootDirectory / "src" / "Swift.Runtime" / "src" / "Swift.Runtime.csproj";
    AbsolutePath AppleSupplementProject => RootDirectory / "src" / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj";
    AbsolutePath GeneratorDll => RootDirectory / "src" / "Swift.Bindings" / "src" / "bin" / "Debug" / DotNetTfm / "Swift.Bindings.dll";

    AbsolutePath GetRuntimeDll(string platform)
    {
        var tfm = ApplePlatform.FromName(platform).GetTfm();
        return RootDirectory / "src" / "Swift.Runtime" / "src" / "bin" / "Debug" / tfm / "Swift.Runtime.dll";
    }

    AbsolutePath GetAppleSupplementDll(string platform)
    {
        var tfm = ApplePlatform.FromName(platform).GetTfm();
        return RootDirectory / "src" / "Swift.Bindings.Apple" / "bin" / "Debug" / tfm / "SwiftBindings.Apple.dll";
    }

    // ============================================================
    // Validate target — library validation compile gate
    // ============================================================

    Target Validate => _ => _
        .After(Clean, Fetch, BindingTests, ValidateBlastRadius)
        .Triggers(PackGate, BehaviorTier)
        .Executes(async () =>
        {
            // --- Fetch if requested ---
            if (FetchFirst)
            {
                Log.Information("=== Fetching libraries ===");
                RunFetch();
                Log.Information("");
            }

            var manifest = ValidationManifest.Load(ManifestPath);
            var targets = manifest.ExpandTargets(Filter, Tier, LibrariesDir);

            // --- Resolve parallel job count ---
            int maxJobs;
            if (Serial)
                maxJobs = 1;
            else if (Jobs > 0)
                maxJobs = Jobs;
            else
            {
                var cores = Environment.ProcessorCount;
                maxJobs = cores > 4 ? cores - 2 : Math.Max(cores, 1);
            }
            maxJobs = Math.Clamp(maxJobs, 1, 16);

            // --- Branch-scoped output directory ---
            var branch = GetGitBranch();
            var outputBase = (AbsolutePath)Path.Combine(Path.GetTempPath(), $"binding-validation-{branch}");

            // --- Prerequisites ---
            Log.Information("=== Library Validation ===");
            Log.Information("");

            if (!File.Exists(ManifestPath))
            {
                Log.Error("Manifest not found: {Path}", ManifestPath);
                Assert.Fail("Manifest not found");
            }

            // --- Quick mode: validate output exists ---
            if (Quick)
            {
                if (!Directory.Exists(outputBase))
                {
                    Log.Error("No existing output at {Path} — run without --quick first", outputBase);
                    Assert.Fail("No cached output for --quick mode");
                }

                var baseline = ValidationBaseline.Load(BaselinePath);
                if (baseline.GitSha != "" && baseline.GitSha != GetGitShortSha())
                    Log.Warning("Generator changed since last run ({Previous} -> {Current}) — results may be stale",
                        baseline.GitSha, GetGitShortSha());
            }
            else
            {
                // Check .libraries/ exists — but only if at least one filtered target
                // needs a fetched xcframework. Apple-framework targets resolve the SDK
                // on-demand via xcrun, so `nuke validate --filter ActivityKit` should
                // work on a clean checkout with no .libraries/ directory.
                var needsLibrariesDir = targets.Any(t => t.Mode != "apple-framework");
                if (needsLibrariesDir && !Directory.Exists(LibrariesDir))
                {
                    Log.Error(".libraries/ not found. Run first: nuke fetch");
                    Assert.Fail(".libraries/ directory not found");
                }
            }

            // --- Filter targets by xcframework availability ---
            var availableTargets = new List<ValidationTarget>();
            var manualMissing = new List<string>();

            foreach (var target in targets)
            {
                if (Quick)
                {
                    // In --quick mode, don't require xcframeworks — use cached /tmp output
                    availableTargets.Add(target);
                }
                else if (target.Mode == "apple-framework")
                {
                    // apple-framework targets resolve the SDK on-demand at generate time
                    // via xcrun, so the .libraries/ xcframework check does not apply.
                    availableTargets.Add(target);
                }
                else if (!Directory.Exists(target.XcframeworkPath))
                {
                    if (target.Mode == "manual")
                        manualMissing.Add(target.Name);
                    else
                        Log.Warning("  {Name}: xcframework not found — skipping", target.Name);
                }
                else
                {
                    availableTargets.Add(target);
                }
            }

            if (availableTargets.Count == 0)
            {
                if (manualMissing.Count > 0)
                    Log.Warning("All matching targets are manual and missing xcframeworks: {Targets}",
                        string.Join(" ", manualMissing));
                else if (Filter != null)
                    Log.Warning("No libraries match filter: {Filter}", Filter);
                else
                    Log.Error("No libraries available. Run: nuke fetch");
                return;
            }

            var totalTargets = availableTargets.Count;
            Log.Debug("Runtime version: {Version}", GetRuntimeVersion());
            Log.Debug("Git SHA: {Sha}", GetGitShortSha());
            Log.Debug("Tier: {Tier}", Tier == 0 ? "all" : Tier.ToString());
            Log.Debug("Workers: {Workers}", maxJobs);
            Log.Debug("Targets: {Count}", totalTargets);
            if (manualMissing.Count > 0)
                Log.Debug("Manual (missing): {Count} ({Targets})", manualMissing.Count,
                    string.Join(" ", manualMissing));
            Log.Information("");

            // --- Build Generator ---
            if (!Quick)
                BuildGeneratorIfChanged(outputBase);

            // --- Determine which targets have declared dependencies ---
            // Apple-framework targets resolve cross-module qualifications via dep
            // module databases threaded inline by GenerateAppleFrameworkTarget; their
            // C# compile gate is standalone (sibling-framework CS0234s filtered by
            // CountNonTransitiveCsErrors). Skip them here so they don't fall into the
            // cascading-dep gate, which expects a built DLL for each dep — apple-
            // framework deps are system frameworks resolved at runtime, not user-built
            // assemblies, so the cascade has nothing to wait on.
            var hasDeps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lib in manifest.Libraries)
            {
                if (lib.Mode == "apple-framework") continue;
                foreach (var prod in lib.Products)
                    if (prod.Dependencies is { Count: > 0 })
                        hasDeps.Add(prod.Framework);
            }

            // --- Build framework-to-library-name mapping ---
            var fwToLib = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var lib in manifest.Libraries)
                foreach (var prod in lib.Products)
                    fwToLib[prod.Framework] = lib.Name;

            // --- Longest-first scheduling ---
            var baseline2 = ValidationBaseline.Load(BaselinePath);
            var displayTargets = availableTargets.ToList(); // Preserve manifest order for output
            var sortedTargets = maxJobs > 1
                ? availableTargets
                    .OrderByDescending(t =>
                        baseline2.Gate.Libraries.TryGetValue(t.Name, out var bl) ? bl.Lines : 0)
                    .ToList()
                : availableTargets;

            // --- Results tracking ---
            var results = new ConcurrentDictionary<string, TargetResult>();

            // --- Binding Pipeline ---
            Log.Information("--- Binding Pipeline ---");

            var semaphore = new SemaphoreSlim(maxJobs);

            // === Generate All Bindings (parallel) ===
            Log.Debug("Phase 3a: Generating {Count} targets with {Jobs} parallel workers...",
                totalTargets, maxJobs);
            var phase3aStart = DateTime.UtcNow;

            await Task.WhenAll(sortedTargets.Select(async target =>
            {
                await semaphore.WaitAsync();
                try { await Task.Run(() => GenerateTarget(target, outputBase, results, manifest)); }
                finally { semaphore.Release(); }
            }));

            Log.Debug("Phase 3a completed in {Seconds}s",
                (int)(DateTime.UtcNow - phase3aStart).TotalSeconds);

            // Display generate results in manifest order
            foreach (var target in displayTargets)
            {
                if (results.TryGetValue(target.Name, out var r) && r.GenOutput != null)
                    Log.Information("{Output}", r.GenOutput);
            }

            // === Compile Swift Wrappers (parallel) ===
            Log.Information("");
            Log.Debug("Phase 3b: Compiling Swift wrappers with {Jobs} parallel workers...", maxJobs);
            var phase3bStart = DateTime.UtcNow;

            await Task.WhenAll(sortedTargets.Select(async target =>
            {
                await semaphore.WaitAsync();
                try { await Task.Run(() => CompileWrapper(target, outputBase, results, manifest, fwToLib)); }
                finally { semaphore.Release(); }
            }));

            Log.Debug("Phase 3b completed in {Seconds}s",
                (int)(DateTime.UtcNow - phase3bStart).TotalSeconds);

            // Display wrapper compile results and compute swift counters
            int swiftPassed = 0, swiftFailed = 0, swiftNoWrapper = 0;
            foreach (var target in displayTargets)
            {
                if (results.TryGetValue(target.Name, out var r) && r.SwiftOutput != null)
                    Log.Information("{Output}", r.SwiftOutput);
                var sw = results.GetValueOrDefault(target.Name)?.SwiftCompile ?? "unknown";
                switch (sw)
                {
                    case "ok": swiftPassed++; break;
                    case "fail": swiftFailed++; break;
                    case "no_wrapper": swiftNoWrapper++; break;
                }
            }

            var swiftTested = swiftPassed + swiftFailed;
            if (swiftTested > 0)
            {
                var noWrapNote = swiftNoWrapper > 0 ? $" ({swiftNoWrapper} ObjC/no wrapper)" : "";
                if (swiftFailed == 0)
                    Log.Information("Swift wrapper: {Passed}/{Tested} passed{Note}",
                        swiftPassed, swiftTested, noWrapNote);
                else
                    Log.Error("Swift wrapper: {Passed}/{Tested} passed, {Failed} failed{Note}",
                        swiftPassed, swiftTested, swiftFailed, noWrapNote);
            }

            // === C# Compile Gate ===
            Log.Information("");
            Log.Information("--- Compile Gate ---");

            // Split into non-dep and dep targets
            var nonDepTargets = sortedTargets.Where(t => !hasDeps.Contains(t.Name.Split('@')[0])).ToList();
            var depTargets = sortedTargets.Where(t => hasDeps.Contains(t.Name.Split('@')[0])).ToList();

            // Compile non-dep targets in parallel
            if (nonDepTargets.Count > 0)
            {
                Log.Debug("Phase 3c: Compiling {Count} standalone targets with {Jobs} parallel workers...",
                    nonDepTargets.Count, maxJobs);
                var phase3cStart = DateTime.UtcNow;

                await Task.WhenAll(nonDepTargets.Select(async target =>
                {
                    await semaphore.WaitAsync();
                    try { await Task.Run(() => CompileTarget(target, outputBase, results)); }
                    finally { semaphore.Release(); }
                }));

                Log.Debug("Phase 3c standalone completed in {Seconds}s",
                    (int)(DateTime.UtcNow - phase3cStart).TotalSeconds);
            }

            // Display standalone compile results
            int compilePassed = 0, compileFailed = 0, compileNoOutput = 0;
            foreach (var target in displayTargets)
            {
                if (hasDeps.Contains(target.Name.Split('@')[0])) continue;
                if (results.TryGetValue(target.Name, out var r) && r.CompileOutput != null)
                    Log.Information("{Output}", r.CompileOutput);
                var comp = r?.Compile ?? "unknown";
                switch (comp)
                {
                    case "ok":
                    case "known_errors":
                        compilePassed++; break;
                    case "fail":
                    case "regressed":
                    case "infra_fail":
                        compileFailed++; break;
                    default:
                        compileNoOutput++; break;
                }
            }

            var compileTested = compilePassed + compileFailed + compileNoOutput;
            if (compileTested > 0)
            {
                Log.Information("");
                if (compileFailed == 0 && compileNoOutput == 0)
                    Log.Information("Compile gate (standalone): {Passed}/{Tested} passed",
                        compilePassed, compileTested);
                else if (compileFailed == 0)
                    Log.Information("Compile gate (standalone): {Passed}/{Tested} passed ({NoOutput} no output)",
                        compilePassed, compileTested, compileNoOutput);
                else
                    Log.Error("Compile gate (standalone): {Passed}/{Tested} passed, {Failed} failed",
                        compilePassed, compileTested, compileFailed);
            }

            // === Cascading Dependency Gate ===
            int depPassed = 0, depFailed = 0, depSkipped = 0, depTotal = 0;

            if (depTargets.Count > 0)
            {
                Log.Information("");
                Log.Information("--- Dependency Gate ---");

                var closures = ComputeTransitiveDependencyClosures(manifest);
                var runTargetNames = new HashSet<string>(availableTargets.Select(t => t.Name));

                // Build pending list
                var pending = new List<(string TargetName, string[] DepNames)>();
                foreach (var (targetName, depList) in closures)
                {
                    if (!runTargetNames.Contains(targetName)) continue;
                    depTotal++;
                    pending.Add((targetName, depList));
                }

                // Cascading resolution
                while (pending.Count > 0)
                {
                    bool progress = false;
                    var nextPending = new List<(string TargetName, string[] DepNames)>();

                    foreach (var (targetName, depList) in pending)
                    {
                        var outdir = outputBase / targetName;

                        // Find main C# source file
                        var csFile = FindMainCsFile(outdir);
                        if (csFile == null)
                        {
                            depSkipped++;
                            Log.Warning("  {Name}: no C# source", targetName);
                            continue;
                        }

                        // Locate all transitive dependency DLLs
                        var missingDeps = new List<string>();
                        var foundRefs = new List<(string Name, string DllPath)>();

                        foreach (var dep in depList)
                        {
                            var depDll = FindDependencyDll(dep, outputBase);
                            if (depDll != null)
                                foundRefs.Add((dep, depDll));
                            else
                                missingDeps.Add(dep);
                        }

                        // If any dependency DLL is missing, defer to next round
                        if (missingDeps.Count > 0)
                        {
                            nextPending.Add((targetName, depList));
                            continue;
                        }

                        // Resolve platform settings
                        var depPlatform = "ios";
                        var depFwBase = targetName;
                        if (targetName.Contains('@'))
                        {
                            depPlatform = targetName.Split('@')[1];
                            depFwBase = targetName.Split('@')[0];
                        }
                        var platform = ApplePlatform.FromName(depPlatform);
                        var runtimeDll = GetRuntimeDll(depPlatform);

                        // Create dep test csproj with AssemblyName
                        var csBasename = Path.GetFileName(csFile);
                        var depCsproj = outdir / "_dep_test.csproj";
                        var refElements = string.Join("\n",
                            foundRefs.Select(r =>
                                $"    <Reference Include=\"{r.Name}\"><HintPath>{r.DllPath}</HintPath></Reference>"));

                        var appleDll = GetAppleSupplementDll(depPlatform);
                        WriteDependencyCsproj(depCsproj, platform.GetTfm(), platform.MinOsVersion,
                            runtimeDll, appleDll, depFwBase, csBasename, refElements);

                        // Restore + build
                        RunDotnetRestore(depCsproj);
                        var (buildExit, csErrors, buildOutput) = RunDotnetBuild(depCsproj);

                        var depDisplayDeps = string.Join(" ", depList);
                        var depSwiftMarker = GetSwiftMarker(targetName, results);

                        if (buildExit == 0 && csErrors == 0)
                        {
                            depPassed++;
                            progress = true;
                            Log.Information("  {Name} + [{Deps}]: OK{Swift}",
                                targetName, depDisplayDeps, depSwiftMarker);
                            SetResult(results, targetName, compile: "ok", depCompile: "ok", errors: 0);
                        }
                        else if (buildExit != 0 && csErrors == 0)
                        {
                            var infraErr = ExtractInfraError(buildOutput);
                            depFailed++;
                            SetResult(results, targetName, compile: "infra_fail", errors: 0);
                            Log.Error("  {Name} + [{Deps}]: build failure{Swift}",
                                targetName, depDisplayDeps, depSwiftMarker);
                            if (Verbose && infraErr != null)
                                Log.Debug("    {Error}", infraErr);
                        }
                        else
                        {
                            depFailed++;
                            SetResult(results, targetName, compile: "fail", errors: csErrors);
                            Log.Error("  {Name} + [{Deps}]: {Errors} errors{Swift}",
                                targetName, depDisplayDeps, csErrors, depSwiftMarker);
                            if (Verbose)
                                ShowCsErrors(buildOutput, 5);
                        }
                    }

                    pending = nextPending;
                    if (!progress) break;
                }

                // Report unresolved
                foreach (var (targetName, depList) in pending)
                {
                    depSkipped++;
                    SetResult(results, targetName, compile: "skip", errors: 0);
                    Log.Warning("  {Name} + [{Deps}]: skipped (dependencies not resolved)",
                        targetName, string.Join(" ", depList));
                }

                Log.Information("");
                if (depTotal > 0)
                {
                    var depTested = depPassed + depFailed;
                    if (depTested == 0)
                        Log.Warning("Dependency gate: {Total} targets, all skipped (dependencies not compiled)",
                            depTotal);
                    else if (depFailed == 0 && depSkipped == 0)
                        Log.Information("Dependency gate: {Passed}/{Total} passed", depPassed, depTotal);
                    else if (depFailed == 0)
                        Log.Information("Dependency gate: {Passed}/{Tested} tested, passed ({Skipped} skipped — dependencies not compiled)",
                            depPassed, depTested, depSkipped);
                    else
                        Log.Error("Dependency gate: {Passed}/{Tested} tested, {Failed} failed",
                            depPassed, depTested, depFailed);
                }
            }

            Log.Information("");

            // === Baseline & Regression Detection ===
            bool isFullRun = Filter == null && Tier == 0;

            // Build current library results for baseline
            var currentResults = new Dictionary<string, ValidationBaseline.LibraryResult>();
            foreach (var target in displayTargets)
            {
                var r = results.GetValueOrDefault(target.Name);
                currentResults[target.Name] = new ValidationBaseline.LibraryResult
                {
                    Compile = r?.Compile ?? "unknown",
                    Errors = r?.Errors ?? 0,
                    Lines = r?.Lines ?? 0,
                    DepCompile = r?.DepCompile ?? "none",
                    SwiftCompile = r?.SwiftCompile ?? "unknown",
                };
            }

            // Collect skip metrics from binding-report.json files
            var skipMetrics = CollectSkipMetrics(outputBase, displayTargets.Select(t => t.Name).ToHashSet());

            // Load previous baseline for comparison
            var prevBaseline = ValidationBaseline.Load(BaselinePath);

            // Baseline is saved AFTER the regression gate below (Finding 29). Saving it here —
            // before the gate — let a failing run ratchet its own failures into the baseline, so
            // the next run printed "No regressions detected" and the detector self-erased. Only a
            // fully-green unfiltered run updates the baseline now.

            // Log skip metrics summary
            if (skipMetrics.TotalEmittedMembers > 0 || skipMetrics.TotalSkippedMembers > 0)
            {
                Log.Information("--- Skip Metrics ---");
                Log.Information("  Emitted: {Emitted}  Skipped: {Skipped}  Rate: {Rate}%",
                    skipMetrics.TotalEmittedMembers, skipMetrics.TotalSkippedMembers,
                    skipMetrics.SkipRatePct);
                if (skipMetrics.SkipReasons.Count > 0)
                {
                    foreach (var (reason, count) in skipMetrics.SkipReasons.OrderByDescending(kv => kv.Value).Take(5))
                        Log.Debug("    {Count,5}  {Reason}", count, reason);
                }

                if (skipMetrics.PostProcessorSubCauses.Count > 0)
                {
                    Log.Information("  Post-processor strips: {Summary}",
                        string.Join(", ", skipMetrics.PostProcessorSubCauses
                            .OrderBy(kv => kv.Key)
                            .Select(kv => $"{kv.Key}={kv.Value}")));

                    if (prevBaseline.SkipMetrics.PostProcessorSubCauses.Count > 0)
                    {
                        // Per-bucket thresholds: "Other" is the safety-net bucket
                        // (EveryProtocol() placeholders, .load(as: @escaping)) that
                        // should never fire in normal operation, so any non-zero
                        // increase is a real regression. "NSInvocation" is also
                        // tightly bounded — a +1 there is a new ObjC-unavailable
                        // type leaking through. "InternalType" is the noisy
                        // post-processor residue that absorbs body-reference
                        // strips, so a small absolute tolerance keeps the warning
                        // useful as the residue inventory drifts.
                        foreach (var (cause, curr) in skipMetrics.PostProcessorSubCauses)
                        {
                            prevBaseline.SkipMetrics.PostProcessorSubCauses.TryGetValue(cause, out var prev);
                            int allowedDelta = cause switch
                            {
                                "Other" => 0,
                                "NSInvocation" => 0,
                                "InternalType" => 5,
                                _ => 5,
                            };
                            if (curr > prev + allowedDelta)
                            {
                                Log.Warning("Post-processor sub-cause '{Cause}' increased: {Prev} -> {Curr} (+{Delta})",
                                    cause, prev, curr, curr - prev);
                            }
                        }
                    }
                }

                // Warn if skip count increased vs previous baseline
                if (prevBaseline.SkipMetrics.TotalSkippedMembers > 0 &&
                    skipMetrics.TotalSkippedMembers > prevBaseline.SkipMetrics.TotalSkippedMembers)
                {
                    Log.Warning("Skip count increased: {Prev} -> {Curr} (+{Delta})",
                        prevBaseline.SkipMetrics.TotalSkippedMembers,
                        skipMetrics.TotalSkippedMembers,
                        skipMetrics.TotalSkippedMembers - prevBaseline.SkipMetrics.TotalSkippedMembers);
                }
                else if (prevBaseline.SkipMetrics.TotalSkippedMembers > 0 &&
                         skipMetrics.TotalSkippedMembers < prevBaseline.SkipMetrics.TotalSkippedMembers)
                {
                    Log.Information("Skip count improved: {Prev} -> {Curr} (-{Delta})",
                        prevBaseline.SkipMetrics.TotalSkippedMembers,
                        skipMetrics.TotalSkippedMembers,
                        prevBaseline.SkipMetrics.TotalSkippedMembers - skipMetrics.TotalSkippedMembers);
                }
                Log.Information("");
            }

            // === Regression Detection ===
            int regressionCount = 0;
            if (prevBaseline.Gate.Libraries.Count > 0)
            {
                Log.Information("--- Regression Check ---");
                var (regressions, improvements, drift) = prevBaseline.Compare(currentResults, isFullRun);
                regressionCount = regressions.Count;

                foreach (var r in regressions)
                {
                    Log.Error("REGRESSION: {R}", r);
                    // Extract the target name for diagnostic hint
                    var targetName = r.Split(':')[0].Trim();
                    Log.Information("  -> Diagnose:  nuke validate --filter {Name} --verbose", targetName);
                    Log.Information("  -> After fix: add unit/integration test reproducing the pattern");
                    Log.Information("  -> Verify:    nuke validate");
                    Log.Information("");
                }

                foreach (var i in improvements)
                    Log.Information("IMPROVED: {I}", i);
                foreach (var d in drift)
                    Log.Warning("LINE DRIFT: {D}", d);

                if (regressions.Count == 0 && improvements.Count == 0 && drift.Count == 0)
                    Log.Information("No regressions detected");

                Log.Information("");
            }

            // === Summary ===
            Log.Information("=== Summary ===");

            var overallPassed = compilePassed + depPassed;
            var overallFailed = totalTargets - overallPassed - compileNoOutput;
            if (overallFailed < 0)
                // The standalone-compile and dependency gates are meant to partition the
                // target set, so passes should never exceed the total. A negative value
                // means a target was counted by both gates — surface it instead of
                // rounding it up into the success branch.
                Log.Error("  Overall: {Passed}/{Total} — count inconsistency: a target is double-counted across the compile and dependency gates",
                    overallPassed, totalTargets);
            else if (overallFailed == 0 && compileNoOutput == 0)
                Log.Information("  Overall: {Passed}/{Total} passed", overallPassed, totalTargets);
            else
                Log.Error("  Overall: {Passed}/{Total} passed, {Failed} failed",
                    overallPassed, totalTargets, overallFailed);

            if (compileFailed == 0 && compileNoOutput == 0)
                Log.Information("  Compile (standalone): {Passed}/{Tested} passed",
                    compilePassed, compileTested);
            else if (compileFailed == 0)
                Log.Information("  Compile (standalone): {Passed}/{Tested} passed, {NoOutput} no output",
                    compilePassed, compileTested, compileNoOutput);
            else
                Log.Error("  Compile (standalone): {Passed}/{Tested} passed, {Failed} failed",
                    compilePassed, compileTested, compileFailed);

            if (depTotal > 0)
            {
                var depTested = depPassed + depFailed;
                if (depTested == 0)
                    Log.Debug("  Dependencies: {Total} targets, all skipped", depTotal);
                else if (depFailed == 0)
                    Log.Information("  Dependencies: {Passed}/{Tested} tested, passed",
                        depPassed, depTested);
                else
                    Log.Error("  Dependencies: {Passed}/{Tested} tested, {Failed} failed",
                        depPassed, depTested, depFailed);
            }

            if (swiftTested > 0)
            {
                var noWrapNote = swiftNoWrapper > 0 ? $" ({swiftNoWrapper} ObjC/no wrapper)" : "";
                if (swiftFailed == 0)
                    Log.Information("  Swift wrapper: {Passed}/{Tested} passed{Note}",
                        swiftPassed, swiftTested, noWrapNote);
                else
                    Log.Error("  Swift wrapper: {Passed}/{Tested} passed, {Failed} failed{Note}",
                        swiftPassed, swiftTested, swiftFailed, noWrapNote);
            }

            // Tier info
            if (Filter != null)
                Log.Information("  Tier: {Tier} (filtered: {Count} targets matching '{Filter}')",
                    Tier == 0 ? "all" : Tier.ToString(), totalTargets, Filter);
            else if (Tier == 0)
                Log.Information("  Tier: all");
            else
                Log.Information("  Tier: {Tier}", Tier);

            Log.Information("");

            // === Gate (Finding 29) ===
            // The verdict is computed from compile/dependency failures AND regressions; the
            // baseline is then ratcheted only on a fully-green unfiltered run. Saving the baseline
            // before the gate (the old behavior) persisted a failing run's failures, after which
            // the next run printed "No regressions detected" — an advisory, self-erasing detector.
            bool validationFailed =
                compileFailed > 0 || compileNoOutput > 0 || depFailed > 0 || regressionCount > 0;

            if (isFullRun && !validationFailed)
            {
                // Preserve the existing runtime_tests baseline (populated by a separate
                // nuke binding-tests --sim run) so a validate pass doesn't stomp it to null.
                var newBaseline = new ValidationBaseline
                {
                    GitSha = GetGitShortSha(),
                    Gate = new() { Libraries = currentResults },
                    SkipMetrics = skipMetrics,
                    RuntimeTests = prevBaseline.RuntimeTests
                };
                newBaseline.Save(BaselinePath);
                Log.Debug("  Baseline: {Path} (updated — green run)", BaselinePath);
            }
            else if (isFullRun)
                Log.Warning("  Baseline: {Path} (NOT updated — validation failed; prior baseline preserved)", BaselinePath);
            else
                Log.Debug("  Baseline: {Path} (not updated — filtered/tier run)", BaselinePath);

            if (validationFailed)
                Assert.Fail(
                    $"Validation failed: {compileFailed + depFailed} compile failures, " +
                    $"{compileNoOutput} no output, {regressionCount} regressions");
        });

    // ============================================================
    // Generate Bindings
    // ============================================================

    void GenerateTarget(ValidationTarget target, AbsolutePath outputBase,
        ConcurrentDictionary<string, TargetResult> results, ValidationManifest manifest)
    {
        if (target.Mode == "apple-framework")
        {
            GenerateAppleFrameworkTarget(target, outputBase, results, manifest);
            return;
        }

        var outdir = outputBase / target.Name;
        var result = GetOrCreateResult(results, target.Name);

        if (!Quick)
        {
            if (Directory.Exists(outdir))
                ((AbsolutePath)outdir).DeleteDirectory();
            Directory.CreateDirectory(outdir);

            var genStart = DateTime.UtcNow;
            var verbosity = Verbose ? "1" : "0";

            try
            {
                var process = ProcessTasks.StartProcess(
                    "dotnet", $"\"{GeneratorDll}\" --skip-wrapper-compilation --xcframework \"{target.XcframeworkPath}\" -o \"{outdir}\" --platform {target.Platform} -v {verbosity}",
                    logOutput: false);
                process.AssertWaitForExit();

                var hasCs = Directory.GetFiles(outdir, "*.cs")
                    .Any(f => !f.EndsWith(".Wrappers.cs") && !f.EndsWith(".SwiftUIBridge.cs"));

                if (process.ExitCode == 0 && hasCs)
                {
                    result.Gen = "ok";
                }
                else
                {
                    result.Gen = "fail";
                    if (Verbose)
                    {
                        var output = process.Output.Select(o => o.Text).ToList();
                        result.GenVerbose = string.Join("\n", output.TakeLast(5));
                    }
                }
            }
            catch (Exception ex)
            {
                result.Gen = "fail";
                Log.Debug("  {Name}: generation exception: {Message}", target.Name, ex.Message);
            }

            result.GenSeconds = (int)(DateTime.UtcNow - genStart).TotalSeconds;
        }
        else
        {
            if (Directory.Exists(outdir))
            {
                result.Gen = "cached";
                result.GenSeconds = 0;
            }
            else
            {
                result.Gen = "missing";
                result.Compile = "skip";
                result.Errors = 0;
                result.Lines = 0;
                result.SwiftCompile = "unknown";
                result.GenOutput = $"  {target.Name}: no cached output";
                return;
            }
        }

        // Count generated lines
        var csFile = FindMainCsFile(outdir);
        result.Lines = csFile != null ? File.ReadLines(csFile).Count() : 0;

        // Format generation result
        if (result.GenVerbose != null)
            result.GenOutput = $"    {result.GenVerbose}\n";

        if (result.Gen is "ok" or "cached")
            result.GenOutput = $"  {target.Name}: generated ({result.Lines} lines, {result.GenSeconds}s)";
        else
            result.GenOutput = $"  {target.Name}: gen failed ({result.GenSeconds}s)";
    }

    // ============================================================
    // Generate Bindings (apple-framework mode): xcrun → digester → generator
    // ============================================================

    void GenerateAppleFrameworkTarget(ValidationTarget target, AbsolutePath outputBase,
        ConcurrentDictionary<string, TargetResult> results, ValidationManifest manifest)
    {
        var outdir = outputBase / target.Name;
        var result = GetOrCreateResult(results, target.Name);
        var genStart = DateTime.UtcNow;

        if (Quick)
        {
            // Cached path mirrors the standard GenerateTarget fallback — trust that
            // a prior non-quick run populated the output dir.
            if (Directory.Exists(outdir))
            {
                result.Gen = "cached";
                result.GenSeconds = 0;
            }
            else
            {
                result.Gen = "missing";
                result.Compile = "skip";
                result.Errors = 0;
                result.Lines = 0;
                result.SwiftCompile = "unknown";
                result.GenOutput = $"  {target.Name}: no cached output";
                return;
            }
            result.Lines = FindMainCsFile(outdir) is { } cached ? File.ReadLines(cached).Count() : 0;
            result.SwiftCompile = CheckSwiftWrapper(outdir);
            result.GenOutput = $"  {target.Name}: generated ({result.Lines} lines, {result.GenSeconds}s)";
            return;
        }

        if (Directory.Exists(outdir))
            outdir.DeleteDirectory();
        Directory.CreateDirectory(outdir);

        var frameworkModule = target.FrameworkModule ?? target.Name;
        var platform = ApplePlatform.FromName(target.Platform);

        // Captured below when the generator subprocess actually runs; remains null
        // when the digester or path-resolution steps throw before genProc starts.
        List<string>? genOutputLines = null;

        try
        {
            // Step 1: resolve SDK root via xcrun. iOS/tvOS use simulator SDKs for
            // validation; macOS/MacCatalyst use the shared macosx SDK.
            var sdkPath = RunAppleCapture("xcrun", $"--sdk {platform.SimulatorSdkName} --show-sdk-path");
            if (string.IsNullOrWhiteSpace(sdkPath))
                throw new Exception($"xcrun --sdk {platform.SimulatorSdkName} --show-sdk-path returned empty. Install Xcode or run `xcode-select --switch`.");

            // Step 2: locate swiftinterface + tbd. MacCatalyst prefers the iOSSupport
            // overlay but falls back to the regular System/Library path when the
            // overlay ships an empty stub (MusicKit, CryptoKit, etc.).
            var (swiftinterfacePath, tbdPath, frameworkDir) = ResolveAppleFrameworkPaths(sdkPath, platform, frameworkModule);

            if (!File.Exists(swiftinterfacePath))
                throw new Exception($"{frameworkModule} swiftinterface not found at {swiftinterfacePath} (SDK {sdkPath}).");
            if (!File.Exists(tbdPath))
                throw new Exception($"{frameworkModule}.tbd not found at {tbdPath} (SDK {sdkPath}).");

            // Step 3: dump the framework ABI. MacCatalyst needs extra framework search
            // paths (iOSSupport overlay + regular System/Library) to resolve cross-
            // framework references — same rule the SDK targets apply.
            var abiJsonPath = outdir / $"{frameworkModule}.abi.json";
            var digesterTarget = platform.SimulatorTarget;
            var digesterArgs = new List<string>
            {
                "swift-api-digester",
                "-dump-sdk",
                "-module", frameworkModule,
                "-target", digesterTarget,
                "-sdk", $"\"{sdkPath}\"",
            };
            if (platform.Name == "maccatalyst")
            {
                digesterArgs.Add("-F");
                digesterArgs.Add($"\"{sdkPath}/System/iOSSupport/System/Library/Frameworks\"");
                digesterArgs.Add("-F");
                digesterArgs.Add($"\"{sdkPath}/System/Library/Frameworks\"");
            }
            digesterArgs.Add("-o");
            digesterArgs.Add($"\"{abiJsonPath}\"");

            var digesterProc = ProcessTasks.StartProcess("xcrun", string.Join(" ", digesterArgs),
                workingDirectory: outdir, logOutput: false);
            digesterProc.AssertWaitForExit();
            if (digesterProc.ExitCode != 0 || !File.Exists(abiJsonPath))
            {
                result.Gen = "fail";
                if (Verbose)
                {
                    var tail = digesterProc.Output.Select(o => o.Text).TakeLast(5);
                    result.GenVerbose = string.Join("\n", tail);
                }
                Log.Debug("  {Name}: swift-api-digester failed (exit {Exit})", target.Name, digesterProc.ExitCode);
                FinishAppleFrameworkGenerate(target, outdir, result, genStart, genOutputLines);
                return;
            }

            // Step 3b: generate dep module databases inline.
            //
            // Each apple-framework target produces its own deps' module database XMLs as a
            // prelude inside its own outdir (.deps/<DepModule>/<DepModule>Database.xml), then
            // threads them into the primary generator via --module-database. This is
            // deterministic under parallelism — every dependent self-contains its dep DB
            // generation, no cross-task ordering is required, and a `--filter` that excludes
            // the dep still works because we don't rely on the dep target running.
            //
            // The trade-off vs topological scheduling is duplicated work when both dep and
            // dependent are in the same run: the dep generates fully as its own target AND
            // inline as a prelude here. Acceptable because the inline pass uses
            // --skip-wrapper-compilation + --sdk-mode so it only runs the parser + emitter
            // (cheap) and skips wrapper compile + csproj.
            var depDatabasePaths = new List<AbsolutePath>();
            var depDbFailures = new List<string>();
            if (target.Dependencies.Count > 0)
            {
                foreach (var depFwName in target.Dependencies)
                {
                    var (depDb, depFailure) = GenerateAppleFrameworkDependencyDatabase(
                        depFwName, sdkPath, platform, outdir, manifest);
                    if (depDb != null)
                        depDatabasePaths.Add(depDb);
                    else if (depFailure != null)
                        depDbFailures.Add($"{depFwName}: {depFailure}");
                }
            }

            // A declared apple-framework dep that fails to produce its module DB must
            // fail the primary target — running the generator without the dep DB
            // silently regresses cross-module qualification and the CS error filter
            // hides the resulting CS0234s as "transitive sibling-framework noise."
            // Fail-closed so the breakage surfaces in the validation summary.
            if (depDbFailures.Count > 0)
            {
                result.Gen = "fail";
                result.GenVerbose = "Dep DB generation failed:\n  " +
                    string.Join("\n  ", depDbFailures);
                FinishAppleFrameworkGenerate(target, outdir, result, genStart, genOutputLines: null);
                return;
            }

            // Step 4: invoke the generator in direct mode. No --sdk-mode (we WANT the
            // csproj + wrapper xcframework emitted) and no --skip-wrapper-compilation
            // (the wrapper compiles inline so the wrapper-compile step is a no-op for this mode).
            var verbosity = Verbose ? "1" : "0";
            var libraryNameArg = $@"\@rpath/{frameworkModule}.framework/{frameworkModule}";
            var genArgs = new List<string>
            {
                $"\"{GeneratorDll}\"",
                $"-a \"{abiJsonPath}\"",
                $"-d \"{tbdPath}\"",
                $"-t \"{tbdPath}\"",
                $"-s \"{swiftinterfacePath}\"",
                $"-l \"{libraryNameArg}\"",
                $"--platform {platform.Name}",
            };
            if (platform.HasSimulatorPlistVariant && platform.Name != "maccatalyst")
                genArgs.Add("--platform-target simulator");
            if (!string.IsNullOrWhiteSpace(target.PlatformVersion))
                genArgs.Add($"--platform-version {target.PlatformVersion}");
            if (!string.IsNullOrWhiteSpace(target.NamespacePattern))
                genArgs.Add($"--namespace-pattern \"{target.NamespacePattern}\"");
            foreach (var depDbPath in depDatabasePaths)
                genArgs.Add($"--module-database \"{depDbPath}\"");
            genArgs.Add($"-o \"{outdir}\"");
            genArgs.Add($"-v {verbosity}");

            var genProc = ProcessTasks.StartProcess("dotnet", string.Join(" ", genArgs),
                workingDirectory: outdir, logOutput: false);
            genProc.AssertWaitForExit();

            // Snapshot generator output once — the wrapper compile runs inline inside
            // genProc, so swiftc diagnostics surface here. Threaded into
            // FinishAppleFrameworkGenerate so swift_compile failures carry a real
            // diagnostic instead of an opaque "fail".
            genOutputLines = genProc.Output.Select(o => o.Text).ToList();

            var hasCs = Directory.GetFiles(outdir, "*.cs")
                .Any(f => !f.EndsWith(".Wrappers.cs") && !f.EndsWith(".SwiftUIBridge.cs"));

            if (genProc.ExitCode == 0 && hasCs)
            {
                result.Gen = "ok";
            }
            else
            {
                result.Gen = "fail";
                if (Verbose)
                {
                    result.GenVerbose = string.Join("\n", genOutputLines.TakeLast(5));
                }
            }
        }
        catch (Exception ex)
        {
            result.Gen = "fail";
            Log.Debug("  {Name}: apple-framework generation exception: {Message}", target.Name, ex.Message);
        }

        FinishAppleFrameworkGenerate(target, outdir, result, genStart, genOutputLines);
    }

    // ============================================================
    // Generate a dependency's module database XML (apple-framework mode)
    // as a prelude to the dependent's primary generation.
    // ============================================================
    //
    // Apple `@_implementationOnly` umbrella re-exports (e.g. RealityKit re-exports
    // RealityFoundation.Entity as RealityKit.Entity) require the dep's TypeRecords
    // to be loaded into the dependent's TypeDatabase so the umbrella fallback in
    // TryGetTypeRecordInternal can rewrite the qualification. xcframework mode
    // already handles this via --framework-dependency + ABI-JSON parsing; apple-
    // framework mode resolves SDK frameworks directly via xcrun and never had a
    // dep-loading path. This helper is that path.
    //
    // Outputs land in <primaryOutdir>/.deps/<DepModule>/. The dep generator runs
    // with --skip-wrapper-compilation (we only need the XML, not a built dylib)
    // and --sdk-mode (no csproj — the dep isn't shipping from this validation
    // run, only its database is being consumed). Returns the emitted
    // <DepModule>Database.xml path, or null on failure.
    /// <summary>
    /// Generates the dep's module database for cross-module umbrella resolution.
    /// Returns (Path: db, Error: null) on success, (Path: null, Error: reason) on failure.
    /// A non-null error is a declared-dep failure — the caller fails the primary
    /// target so the breakage surfaces in the validation summary instead of being
    /// silently absorbed by the CS error filter.
    /// </summary>
    (AbsolutePath? Path, string? Error) GenerateAppleFrameworkDependencyDatabase(
        string depFrameworkModule, string sdkPath, ApplePlatform platform,
        AbsolutePath primaryOutdir, ValidationManifest manifest)
    {
        var depOutdir = primaryOutdir / ".deps" / depFrameworkModule;
        var dbPath = depOutdir / $"{depFrameworkModule}Database.xml";

        if (Directory.Exists(depOutdir))
            ((AbsolutePath)depOutdir).DeleteDirectory();
        Directory.CreateDirectory(depOutdir);

        var (depSwiftinterface, depTbd, _) = ResolveAppleFrameworkPaths(sdkPath, platform, depFrameworkModule);
        if (!File.Exists(depSwiftinterface) || !File.Exists(depTbd))
        {
            var reason = "swiftinterface or tbd missing in SDK";
            Log.Warning("  Dep {Dep}: {Reason} — skipping dep DB generation", depFrameworkModule, reason);
            return (null, reason);
        }

        // Look up dep's manifest entry so dep generation honors the dep's own
        // platform-version / namespace-pattern, not the dependent's. Without this
        // a dep configured with a different namespace pattern would emit
        // TypeRecords whose CSharpTypeName.Namespace mismatches what the
        // dependent's emitter expects after umbrella rewrite.
        var depEntry = manifest.Libraries
            .Where(l => l.Mode == "apple-framework")
            .SelectMany(l => l.Products.Select(p => (Library: l, Product: p)))
            .FirstOrDefault(t => t.Product.Framework == depFrameworkModule);

        // swift-api-digester for the dep's ABI JSON
        var depAbiJson = depOutdir / $"{depFrameworkModule}.abi.json";
        var digesterTarget = platform.SimulatorTarget;
        var digesterArgs = new List<string>
        {
            "swift-api-digester",
            "-dump-sdk",
            "-module", depFrameworkModule,
            "-target", digesterTarget,
            "-sdk", $"\"{sdkPath}\"",
        };
        if (platform.Name == "maccatalyst")
        {
            digesterArgs.Add("-F");
            digesterArgs.Add($"\"{sdkPath}/System/iOSSupport/System/Library/Frameworks\"");
            digesterArgs.Add("-F");
            digesterArgs.Add($"\"{sdkPath}/System/Library/Frameworks\"");
        }
        digesterArgs.Add("-o");
        digesterArgs.Add($"\"{depAbiJson}\"");

        var digesterProc = ProcessTasks.StartProcess("xcrun", string.Join(" ", digesterArgs),
            workingDirectory: depOutdir, logOutput: false);
        digesterProc.AssertWaitForExit();
        if (digesterProc.ExitCode != 0 || !File.Exists(depAbiJson))
        {
            var reason = $"swift-api-digester failed (exit {digesterProc.ExitCode})";
            Log.Warning("  Dep {Dep}: {Reason} — failing dep DB generation",
                depFrameworkModule, reason);
            return (null, reason);
        }

        // Generator pass: --skip-wrapper-compilation skips swiftc, --sdk-mode skips
        // csproj emission. We only need the <Module>Database.xml output, which
        // Program.cs writes via ModuleDatabaseEmitter at the end of generation.
        var verbosity = Verbose ? "1" : "0";
        var depLib = $@"\@rpath/{depFrameworkModule}.framework/{depFrameworkModule}";
        var depPlatformVersion = depEntry.Library?.PlatformVersion;
        var depNamespacePattern = depEntry.Product?.NamespacePattern;
        var genArgs = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"-a \"{depAbiJson}\"",
            $"-d \"{depTbd}\"",
            $"-t \"{depTbd}\"",
            $"-s \"{depSwiftinterface}\"",
            $"-l \"{depLib}\"",
            $"--platform {platform.Name}",
            "--skip-wrapper-compilation",
            "--sdk-mode",
        };
        if (platform.HasSimulatorPlistVariant && platform.Name != "maccatalyst")
            genArgs.Add("--platform-target simulator");
        if (!string.IsNullOrWhiteSpace(depPlatformVersion))
            genArgs.Add($"--platform-version {depPlatformVersion}");
        if (!string.IsNullOrWhiteSpace(depNamespacePattern))
            genArgs.Add($"--namespace-pattern \"{depNamespacePattern}\"");
        genArgs.Add($"-o \"{depOutdir}\"");
        genArgs.Add($"-v {verbosity}");

        var genProc = ProcessTasks.StartProcess("dotnet", string.Join(" ", genArgs),
            workingDirectory: depOutdir, logOutput: false);
        genProc.AssertWaitForExit();
        if (genProc.ExitCode != 0 || !File.Exists(dbPath))
        {
            var reason = $"generator failed (exit {genProc.ExitCode}, db={File.Exists(dbPath)})";
            Log.Warning("  Dep {Dep}: {Reason} — failing dep DB generation",
                depFrameworkModule, reason);
            if (Verbose)
            {
                var tail = string.Join("\n", genProc.Output.Select(o => o.Text).TakeLast(5));
                Log.Debug("    dep generator output tail:\n{Tail}", tail);
            }
            return (null, reason);
        }

        return (dbPath, null);
    }

    void FinishAppleFrameworkGenerate(ValidationTarget target, AbsolutePath outdir,
        TargetResult result, DateTime genStart, List<string>? genOutputLines)
    {
        var platform = ApplePlatform.FromName(target.Platform);
        var frameworkModule = target.FrameworkModule ?? target.Name;
        var csFile = FindMainCsFile(outdir);
        result.Lines = csFile != null ? File.ReadLines(csFile).Count() : 0;

        // Direct mode compiles the wrapper inline, so its status is determined here
        // rather than in the wrapper-compile step.
        if (result.Gen is "ok")
        {
            result.SwiftCompile = CheckSwiftWrapper(outdir);

            // iOS/tvOS ship both simulator and device slices. The simulator pass above
            // proves the simulator wrapper compiles; run a second pass against the
            // device SDK to exercise the device slice (direct mode rejects
            // --wrapper-architectures all, so we must invoke the generator twice).
            // If either slice fails, report the aggregate as fail so packaging bugs
            // (e.g., missing embedded Info.plist on the device slice) are caught.
            if (platform.HasDeviceSlice && result.SwiftCompile == "ok")
            {
                var deviceStatus = GenerateAppleFrameworkDeviceSlice(
                    target, outdir, platform, frameworkModule);
                if (deviceStatus != "ok")
                    result.SwiftCompile = "fail";
            }

            // Surface swiftc diagnostics whenever the wrapper compile failed. The
            // generator catches its own swift wrapper exceptions and re-emits them as
            // SWIFTBIND050 warnings on stderr; without this capture the gate prints
            // only `swift:fail` with no clue what swiftc actually said.
            if (result.SwiftCompile == "fail" && genOutputLines != null)
                result.SwiftVerbose = ExtractSwiftDiagnosticLines(genOutputLines);
        }
        else
        {
            result.SwiftCompile = "unknown";
        }

        result.GenSeconds = (int)(DateTime.UtcNow - genStart).TotalSeconds;

        if (result.GenVerbose != null)
            result.GenOutput = $"    {result.GenVerbose}\n";

        result.GenOutput = result.Gen switch
        {
            "ok" => $"  {target.Name}: generated ({result.Lines} lines, {result.GenSeconds}s)",
            _ => $"  {target.Name}: gen failed ({result.GenSeconds}s)"
                 + (result.GenVerbose != null ? $"\n    {result.GenVerbose}" : ""),
        };

        // Render swift compile output for apple-framework targets here (CompileWrapper
        // is a no-op for this mode). Mirrors the formatting used by
        // CompileWrapper so all targets read consistently in the gate output.
        result.SwiftOutput = result.SwiftCompile switch
        {
            "ok" => $"  {target.Name}: [swift:ok]",
            "fail" => (result.SwiftVerbose != null ? $"    {result.SwiftVerbose}\n" : "") +
                      $"  {target.Name}: [swift:fail]",
            "no_wrapper" => $"  {target.Name}: [no wrapper]",
            _ => null
        };
    }

    // Second-pass device-slice wrapper compile + merge for iOS/tvOS apple-framework
    // targets. This deliberately replicates the SDK's packaging path in
    // _CompileAppleFrameworkSecondWrapperSlice (src/Swift.Bindings.Sdk/Sdk/Sdk.targets):
    // compile the device slice via `swiftc -emit-library` (which does NOT write an
    // embedded Info.plist into the .framework dir), then merge with the existing
    // simulator slice via `xcodebuild -create-xcframework`. The merged xcframework
    // replaces the sim-only one in outdir. CheckSwiftWrapper then verifies each
    // slice's .framework carries both binary + Info.plist — a dropped device plist
    // surfaces as swift_compile: fail, which is the whole point of this gate.
    //
    // Returns "ok" / "fail" / "no_sdk" / "no_wrapper_source".
    string GenerateAppleFrameworkDeviceSlice(ValidationTarget target, AbsolutePath outdir,
        ApplePlatform platform, string frameworkModule)
    {
        try
        {
            var sdkPath = RunAppleCapture("xcrun",
                $"--sdk {platform.DeviceSdkName} --show-sdk-path");
            if (string.IsNullOrWhiteSpace(sdkPath))
                return "no_sdk";

            var wrapperModule = $"{frameworkModule}SwiftBindings";
            var xcframework = outdir / $"{wrapperModule}.xcframework";
            if (!Directory.Exists(xcframework))
                return "fail";

            var firstSliceFramework = outdir
                / $"{wrapperModule}.xcframework"
                / platform.SimulatorSliceId
                / $"{wrapperModule}.framework";
            if (!Directory.Exists(firstSliceFramework))
                return "fail";

            var wrapperSources = Directory
                .EnumerateFiles(outdir, "*.Wrapper.swift", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(outdir, "*.Wrapper.Thunk.swift",
                    SearchOption.TopDirectoryOnly))
                .ToList();
            if (wrapperSources.Count == 0)
                return "no_wrapper_source";

            var mergeDir = outdir / ".merge_slices";
            if (Directory.Exists(mergeDir)) mergeDir.DeleteDirectory();
            var secondFrameworkDir = mergeDir / "second" / $"{wrapperModule}.framework";
            Directory.CreateDirectory(secondFrameworkDir);

            // Device target triple: mirror Sdk.targets _AFW_OtherTarget, which uses
            // %(SwiftAppleFrameworkTarget.MinDeploymentVersion) — the framework's
            // minimum deployment floor (default from SwiftAppleFrameworkMinDeploymentVersion:
            // 15.0 for ios/tvos/maccatalyst, 12.0 for macos). target.PlatformVersion is the
            // Apple workload/TFM version (e.g. 26.2) and must NOT be used here; that would
            // hide availability / wrapper-compilation failures that only surface at the
            // SDK's real deployment floor. platform.MinOsVersion carries exactly the same
            // defaults as the SDK's MinDeploymentVersion property.
            var deviceTarget = $"arm64-apple-{platform.TfmSuffix}{platform.MinOsVersion}";

            // Recompile NativeThunk .arm64.s files for the device slice — mirrors
            // Sdk.targets _CompileAppleFrameworkSecondWrapperSlice. The generator's
            // first-slice .arm64.o objects were built against the simulator target
            // and would carry the wrong LC_BUILD_VERSION if reused; without this step
            // the validator path silently drops thunk symbols from the device binary,
            // which is the exact shape of Issue B.
            var thunkSources = Directory
                .EnumerateFiles(outdir, "*.arm64.s", SearchOption.TopDirectoryOnly)
                .ToList();
            var thunkObjects = new List<string>();
            if (thunkSources.Count > 0)
            {
                var thunkStagingDir = mergeDir / "second" / "thunks";
                Directory.CreateDirectory(thunkStagingDir);
                foreach (var asm in thunkSources)
                {
                    var objPath = thunkStagingDir / (Path.GetFileNameWithoutExtension(asm) + ".o");
                    // clang takes -isysroot (not -sdk, which is a swiftc/xcrun flag).
                    // Mirror NativeThunkCompiler.CompileAssemblyFile exactly so the
                    // validator exercises the same compile command the SDK ships.
                    var clangArgs = string.Join(" ", new[]
                    {
                        $"--sdk {platform.DeviceSdkName}",
                        "clang",
                        "-c", $"\"{asm}\"",
                        $"-o \"{objPath}\"",
                        $"-target {deviceTarget}",
                        $"-isysroot \"{sdkPath}\"",
                    });
                    var clangProc = ProcessTasks.StartProcess("xcrun", clangArgs,
                        workingDirectory: outdir, logOutput: false);
                    clangProc.AssertWaitForExit();
                    if (clangProc.ExitCode != 0)
                        return "fail";
                    thunkObjects.Add(objPath);
                }
            }

            var sourcesArg = string.Join(" ",
                wrapperSources.Select(s => $"\"{s}\""));
            var thunkObjectsArg = thunkObjects.Count > 0
                ? " " + string.Join(" ", thunkObjects.Select(o => $"\"{o}\""))
                : "";
            var binaryPath = secondFrameworkDir / wrapperModule;
            var installName = $@"\@rpath/{wrapperModule}.framework/{wrapperModule}";

            // When thunk objects are linked, the linker needs -framework <OriginalModule>
            // so `bl` targets inside the thunk assembly (Tj dispatch thunks, type
            // metadata accessors) can resolve. Mirrors the thunkLinkerFlags branch in
            // SwiftWrapperCompiler.InvokeSwiftCompiler.
            var thunkFrameworkFlag = thunkObjects.Count > 0
                ? $"-Xlinker -framework -Xlinker {frameworkModule} "
                : "";

            var swiftcArgs = string.Join(" ", new[]
            {
                $"--sdk {platform.DeviceSdkName}",
                "swiftc",
                "-emit-library",
                $"-target {deviceTarget}",
                $"-sdk \"{sdkPath}\"",
                "-strict-concurrency=minimal",
                $"-module-name {wrapperModule}",
                thunkFrameworkFlag.TrimEnd(),
                "-Xlinker -install_name",
                $"-Xlinker {installName}",
                $"-o \"{binaryPath}\"",
                sourcesArg + thunkObjectsArg,
            });
            var swiftcProc = ProcessTasks.StartProcess("xcrun", swiftcArgs,
                workingDirectory: outdir, logOutput: false);
            swiftcProc.AssertWaitForExit();
            if (swiftcProc.ExitCode != 0)
                return "fail";

            // Mirror Sdk.targets _CompileAppleFrameworkSecondWrapperSlice: write the
            // device slice's embedded Info.plist before merging so xcodebuild's
            // -create-xcframework preserves a complete framework bundle. Without this
            // the gate path would diverge from the shipped SDK path and the
            // CheckSwiftWrapper plist assertion would still stay red after the fix.
            if (platform.DevicePlistPlatform is string devicePlistPlatform)
            {
                PlistGenerator.WriteFrameworkPlist(
                    secondFrameworkDir / "Info.plist",
                    bundleId: $"com.swiftbindings.{wrapperModule}",
                    bundleName: wrapperModule,
                    executableName: wrapperModule,
                    minOs: platform.MinOsVersion,
                    plistPlatform: devicePlistPlatform);
            }

            var mergedXcframework = mergeDir / "merged.xcframework";
            var mergeArgs = string.Join(" ", new[]
            {
                "-create-xcframework",
                $"-framework \"{firstSliceFramework}\"",
                $"-framework \"{secondFrameworkDir}\"",
                $"-output \"{mergedXcframework}\"",
            });
            var mergeProc = ProcessTasks.StartProcess("xcodebuild", mergeArgs,
                workingDirectory: outdir, logOutput: false);
            mergeProc.AssertWaitForExit();
            if (mergeProc.ExitCode != 0 || !Directory.Exists(mergedXcframework))
                return "fail";

            // Swap the sim-only xcframework for the merged one so CheckSwiftWrapper
            // inspects both slices.
            xcframework.DeleteDirectory();
            Directory.Move(mergedXcframework, xcframework);
            mergeDir.DeleteDirectory();

            return CheckSwiftWrapper(outdir);
        }
        catch (Exception ex)
        {
            Log.Debug("  {Name}: device-slice merge exception: {Message}",
                target.Name, ex.Message);
            return "fail";
        }
    }

    static (AbsolutePath SwiftInterface, AbsolutePath Tbd, AbsolutePath FrameworkDir)
        ResolveAppleFrameworkPaths(string sdkPath, ApplePlatform platform, string module)
    {
        var sdkRoot = (AbsolutePath)sdkPath;
        var subpath = platform.Name == "maccatalyst"
            ? "System/iOSSupport/System/Library/Frameworks"
            : "System/Library/Frameworks";
        var frameworkDir = sdkRoot / subpath / $"{module}.framework";
        var swiftmoduleDir = frameworkDir / "Modules" / $"{module}.swiftmodule";

        // MacCatalyst fallback: the iOSSupport overlay sometimes ships an empty stub
        // framework (MusicKit, CryptoKit) with no swiftmodule. Fall back to the regular
        // macOS SDK path, which carries both the macos and ios-macabi slices.
        if (platform.Name == "maccatalyst" && !Directory.Exists(swiftmoduleDir))
        {
            frameworkDir = sdkRoot / "System/Library/Frameworks" / $"{module}.framework";
            swiftmoduleDir = frameworkDir / "Modules" / $"{module}.swiftmodule";
        }

        // System frameworks on macOS/MacCatalyst ship with the arm64e (pointer-auth)
        // variant rather than plain arm64. Try the exact suffix first; if missing,
        // try arm64e.
        var moduleSuffix = platform.SimulatorModuleSuffix;
        var swiftinterface = swiftmoduleDir / $"{moduleSuffix}.swiftinterface";
        if (!File.Exists(swiftinterface) && moduleSuffix.StartsWith("arm64-"))
        {
            var alt = swiftmoduleDir / $"arm64e-{moduleSuffix["arm64-".Length..]}.swiftinterface";
            if (File.Exists(alt))
                swiftinterface = alt;
        }

        var tbd = frameworkDir / $"{module}.tbd";
        return (swiftinterface, tbd, frameworkDir);
    }

    /// <summary>
    /// Extracts the most useful swiftc diagnostic lines from generator output for display
    /// when a wrapper compile fails. Looks for line:col-formatted swift errors first,
    /// then falls back to SWIFTBIND050 / "compilation failed" markers, and finally to
    /// the last few non-empty lines so the gate never reports an opaque failure with
    /// no diagnostic. Returns null when the output is empty.
    /// </summary>
    static string? ExtractSwiftDiagnosticLines(IEnumerable<string> outputLines)
    {
        var lines = outputLines
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.TrimEnd())
            .ToList();
        if (lines.Count == 0)
            return null;

        // Primary: any line containing a Swift compiler diagnostic file:line:col error/warning.
        var diagnostics = lines
            .Where(t => Regex.IsMatch(t, @"\.swift:\d+:\d+: (?:error|warning):"))
            .Distinct()
            .Take(8)
            .ToList();

        // Secondary: SWIFTBIND050 / generic compile-failure markers carry the truncated
        // swiftc stderr that the generator surfaces via logger.LogWarning.
        if (diagnostics.Count == 0)
        {
            diagnostics = lines
                .Where(t => t.Contains("SWIFTBIND050", StringComparison.Ordinal)
                            || t.Contains("Swift wrapper compilation failed", StringComparison.Ordinal)
                            || t.Contains("error: no such module", StringComparison.Ordinal))
                .Distinct()
                .Take(8)
                .ToList();
        }

        // Last resort: tail of output so failures are never silent.
        if (diagnostics.Count == 0)
            diagnostics = lines.TakeLast(8).ToList();

        return string.Join("\n", diagnostics);
    }

    static string RunAppleCapture(string file, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new Exception($"Failed to start {file} {args}");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new Exception($"{file} {args} exited with {proc.ExitCode}. stderr: {stderr.Trim()}");
        return stdout.Trim();
    }

    // ============================================================
    // Compile Swift Wrappers
    // ============================================================

    void CompileWrapper(ValidationTarget target, AbsolutePath outputBase,
        ConcurrentDictionary<string, TargetResult> results,
        ValidationManifest manifest, Dictionary<string, string> fwToLib)
    {
        var outdir = outputBase / target.Name;
        var result = GetOrCreateResult(results, target.Name);

        // Skip if generation failed or missing
        if (result.Gen is not ("ok" or "cached"))
        {
            result.SwiftCompile = "unknown";
            return;
        }

        // apple-framework direct mode compiles the wrapper inline during generation
        // (no --skip-wrapper-compilation). The SwiftCompile status is stamped by the
        // generator pass; nothing to do here.
        if (target.Mode == "apple-framework")
            return;

        if (!Quick)
        {
            var cmdArgs = new List<string>
            {
                $"\"{GeneratorDll}\"",
                "--compile-wrapper-only",
                "--xcframework", $"\"{target.XcframeworkPath}\"",
                "-o", $"\"{outdir}\"",
                "--platform", target.Platform,
                "-v", Verbose ? "1" : "0"
            };

            // Collect all dependency xcframeworks (declared + wrapper_deps + sibling)
            var addedDeps = new HashSet<string>(StringComparer.Ordinal);

            // Declared dependencies + wrapper_deps
            var allDeps = (target.Dependencies ?? Array.Empty<string>())
                .Concat(target.WrapperDeps ?? Array.Empty<string>())
                .Distinct();

            foreach (var depFwName in allDeps)
            {
                if (fwToLib.TryGetValue(depFwName, out var depLibName))
                {
                    var depXcfw = LibrariesDir / depLibName / $"{depFwName}.xcframework";
                    if (Directory.Exists(depXcfw))
                    {
                        cmdArgs.Add("--framework-dependency");
                        cmdArgs.Add($"\"{depXcfw}\"");
                        addedDeps.Add($"{depFwName}.xcframework");
                    }
                }
            }

            // Add sibling xcframeworks (same library directory) for transitive deps
            var libDir = Path.GetDirectoryName(target.XcframeworkPath.ToString());
            var selfXcfw = Path.GetFileName(target.XcframeworkPath.ToString());
            if (libDir != null && Directory.Exists(libDir))
            {
                foreach (var sibling in Directory.GetDirectories(libDir, "*.xcframework"))
                {
                    var siblingBase = Path.GetFileName(sibling);
                    if (siblingBase == selfXcfw) continue;
                    if (addedDeps.Contains(siblingBase)) continue;
                    cmdArgs.Add("--framework-dependency");
                    cmdArgs.Add($"\"{sibling}\"");
                    addedDeps.Add(siblingBase);
                }
            }

            try
            {
                var process = ProcessTasks.StartProcess(
                    "dotnet", string.Join(" ", cmdArgs),
                    logOutput: false);
                process.AssertWaitForExit();

                var swiftStatus = CheckSwiftWrapper(outdir);
                result.SwiftCompile = swiftStatus;

                // Always capture swiftc diagnostics when the wrapper compile fails. The
                // generator catches wrapper-compile exceptions and surfaces them via
                // logger.LogWarning (SWIFTBIND050), so the truncated swiftc stderr lands
                // in process.Output. Without this capture the gate prints only `swift:fail`
                // with no diagnostic — which is how three Mac Catalyst targets stayed
                // silently broken since the apple-framework tier was first added.
                if (swiftStatus == "fail")
                    result.SwiftVerbose = ExtractSwiftDiagnosticLines(process.Output.Select(o => o.Text));
            }
            catch (Exception ex)
            {
                result.SwiftCompile = "fail";
                result.SwiftVerbose = $"wrapper compilation exception: {ex.Message}";
                Log.Debug("  {Name}: wrapper compilation exception: {Message}", target.Name, ex.Message);
            }

            // Format swift wrapper result
            result.SwiftOutput = result.SwiftCompile switch
            {
                "ok" => $"  {target.Name}: [swift:ok]",
                "fail" => (result.SwiftVerbose != null ? $"    {result.SwiftVerbose}\n" : "") +
                          $"  {target.Name}: [swift:fail]",
                "no_wrapper" => $"  {target.Name}: [no wrapper]",
                _ => null
            };
        }
        else
        {
            // --quick mode: check cached wrapper status
            result.SwiftCompile = CheckSwiftWrapper(outdir);
        }
    }

    // ============================================================
    // Compile C# (standalone, non-dependency)
    // ============================================================

    void CompileTarget(ValidationTarget target, AbsolutePath outputBase,
        ConcurrentDictionary<string, TargetResult> results)
    {
        var outdir = outputBase / target.Name;
        var result = GetOrCreateResult(results, target.Name);

        // Skip if generation failed or missing
        if (result.Gen is not ("ok" or "cached"))
        {
            result.Compile = "skip";
            result.Errors = 0;
            return;
        }

        var platform = ApplePlatform.FromName(target.Platform);

        // Find .csproj to compile — prefer platform-specific .Swift.{PackageSuffix}.csproj
        string? csprojFile = null;
        var platformCsprojs = Directory.GetFiles(outdir, $"*.Swift.{platform.PackageSuffix}.csproj");
        if (platformCsprojs.Length > 0)
        {
            csprojFile = platformCsprojs[0];
        }
        else
        {
            var allCsprojs = Directory.GetFiles(outdir, "*.csproj")
                .Where(f => !f.EndsWith("Test.csproj") && !f.EndsWith("_dep_test.csproj"))
                .ToArray();
            if (allCsprojs.Length > 0)
                csprojFile = allCsprojs[0];
        }

        // Fallback csproj when no csproj generated
        if (csprojFile == null)
        {
            var hasCs = Directory.GetFiles(outdir, "*.cs")
                .Any(f => !f.EndsWith(".Wrappers.cs") && !f.EndsWith(".SwiftUIBridge.cs"));
            if (hasCs)
            {
                WriteFallbackCsproj(outdir, target.Platform);
                csprojFile = Path.Combine(outdir, "Test.csproj");
            }
        }

        if (csprojFile == null)
        {
            result.Compile = "no_csproj";
            result.Errors = 0;
            result.CompileOutput = $"  {target.Name}: no .csproj generated";
            return;
        }

        // Patch .csproj to use local Swift.Runtime DLL
        PatchCsprojRuntime(csprojFile, target.Platform);

        // Restore if no assets file (fallback csproj needs this)
        var assetsFile = Path.Combine(outdir, "obj", "project.assets.json");
        if (!File.Exists(assetsFile))
            RunDotnetRestore(csprojFile);

        // Compile
        var (buildExit, csErrors, buildOutput) = RunDotnetBuild(csprojFile);

        // apple-framework bindings reference types from sibling framework bindings
        // (Swift.Foundation, CoreLocation, etc.) that aren't present in the validation
        // sandbox. Filter those transitive CS0234 errors so real emitter bugs (e.g.,
        // RoomPlan's simd.simd_float3<float> — CS0246/CS0305) remain visible.
        if (target.Mode == "apple-framework")
            csErrors = CountNonTransitiveCsErrors(buildOutput, target.Name);

        // Detect non-CS build failures (e.g., NETSDK1004, MSB errors)
        if (buildExit != 0 && csErrors == 0)
        {
            var infraError = ExtractInfraError(buildOutput);
            if (infraError != null)
            {
                result.Compile = "infra_fail";
                result.Errors = 0;
                result.CompileOutput = $"  {target.Name}: build infrastructure failure\n    {infraError}";
                return;
            }
        }

        result.Errors = csErrors;
        var swiftMarker = GetSwiftMarker(target.Name, results);

        if (csErrors == 0)
        {
            result.Compile = "ok";
            result.CompileOutput = $"  {target.Name}: OK{swiftMarker} ({result.Lines} lines, {result.GenSeconds}s)";
        }
        else if (target.KnownErrors > 0 && csErrors <= target.KnownErrors)
        {
            result.Compile = "known_errors";
            result.CompileOutput = $"  {target.Name}: {csErrors} errors (known, expected {target.KnownErrors}){swiftMarker} ({result.Lines} lines, {result.GenSeconds}s)";
        }
        else if (target.KnownErrors > 0 && csErrors > target.KnownErrors)
        {
            result.Compile = "regressed";
            result.CompileOutput = $"  {target.Name}: {csErrors} errors (expected {target.KnownErrors} — REGRESSED){swiftMarker} ({result.Lines} lines)";
            if (Verbose)
                result.CompileOutput += "\n" + FormatCsErrors(buildOutput, 10);
        }
        else
        {
            result.Compile = "fail";
            result.CompileOutput = $"  {target.Name}: {csErrors} errors{swiftMarker} ({result.Lines} lines)";
            if (Verbose)
                result.CompileOutput += "\n" + FormatCsErrors(buildOutput, 10);
        }
    }

    // ============================================================
    // Helper: Generator fingerprinting + conditional build
    // ============================================================

    void BuildGeneratorIfChanged(AbsolutePath outputBase)
    {
        var buildStamp = outputBase / ".build-stamp";
        var fingerprint = ComputeSourceFingerprint();

        if (File.Exists(buildStamp) &&
            File.ReadAllText(buildStamp).Trim() == fingerprint &&
            File.Exists(GeneratorDll))
        {
            Log.Debug("Generator unchanged — skipping build");
        }
        else
        {
            Log.Information("--- Building generator + runtime ---");
            try
            {
                ProcessTasks.StartProcess(
                        "dotnet", $"build \"{GeneratorProject}\" -v quiet",
                        logOutput: false)
                    .AssertWaitForExit()
                    .AssertZeroExitCode();

                ProcessTasks.StartProcess(
                        "dotnet", $"build \"{RuntimeProject}\" -v quiet",
                        logOutput: false)
                    .AssertWaitForExit()
                    .AssertZeroExitCode();

                // SwiftBindings.Apple supplement: the generator emits a
                // <PackageReference Include="SwiftBindings.Apple"> line for any binding
                // that resolves a Swift-only Apple type (e.g. Foundation.DateComponents).
                // The 18.x.x nupkg isn't on any feed during local validation, so we build
                // the in-tree project and PatchCsprojRuntime swaps the PackageReference
                // for a raw <Reference HintPath=...> to the built DLL.
                ProcessTasks.StartProcess(
                        "dotnet", $"build \"{AppleSupplementProject}\" -v quiet",
                        logOutput: false)
                    .AssertWaitForExit()
                    .AssertZeroExitCode();

                Log.Information("Generator built");
                Directory.CreateDirectory(outputBase);
                File.WriteAllText(buildStamp, fingerprint);
            }
            catch (Exception ex)
            {
                Log.Error("Generator build failed: {Message}", ex.Message);
                Assert.Fail("Generator build failed");
            }
        }
        Log.Information("");
    }

    string ComputeSourceFingerprint()
    {
        var generatorSrc = RootDirectory / "src" / "Swift.Bindings" / "src";
        var runtimeSrc = RootDirectory / "src" / "Swift.Runtime" / "src";
        var appleSupplementSrc = RootDirectory / "src" / "Swift.Bindings.Apple";
        // apple-types-manifest.json is an embedded resource in the generator DLL and
        // also the source-of-truth input for Swift.Bindings.Apple's codegen target.
        // Changes must invalidate the fingerprint so both get rebuilt.
        var appleManifestDir = RootDirectory / "src" / "Swift.Bindings.Sdk" / "tools" / "apple-types-manifest";

        var files = new List<string>();
        foreach (var dir in new[] { generatorSrc.ToString(), runtimeSrc.ToString(), appleSupplementSrc.ToString() })
        {
            if (!Directory.Exists(dir)) continue;
            files.AddRange(Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")));
            files.AddRange(Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")));
            files.AddRange(Directory.EnumerateFiles(dir, "*.props", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")));
            files.AddRange(Directory.EnumerateFiles(dir, "*.targets", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")));
        }

        if (Directory.Exists(appleManifestDir))
        {
            files.AddRange(Directory.EnumerateFiles(appleManifestDir, "*.json", SearchOption.TopDirectoryOnly));
        }

        files.Sort(StringComparer.Ordinal);

        using var sha = SHA256.Create();
        foreach (var file in files)
        {
            // Include file path as separator to avoid collisions from different file sets
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(file + "\n");
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            var content = File.ReadAllBytes(file);
            sha.TransformBlock(content, 0, content.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    // ============================================================
    // Helper: Transitive dependency closures
    // ============================================================

    Dictionary<string, string[]> ComputeTransitiveDependencyClosures(ValidationManifest manifest)
    {
        // Build direct dependency map (target-name -> dep-target-names)
        var depMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var lib in manifest.Libraries)
        {
            // Apple-framework targets are validated standalone (their deps are system
            // frameworks resolved at runtime, not user-built DLLs the cascade can wait
            // on) and are excluded from the dep gate by the `hasDeps` classifier above.
            // Mirror that exclusion here so a dep-declaring apple-framework target is not
            // counted by both gates — otherwise the Overall tally double-counts it.
            if (lib.Mode == "apple-framework") continue;
            var platforms = lib.Platforms ?? ["ios"];
            foreach (var prod in lib.Products)
            {
                if (prod.Dependencies is not { Count: > 0 }) continue;
                foreach (var plat in platforms)
                {
                    var targetName = plat == "ios" ? prod.Framework : $"{prod.Framework}@{plat}";
                    var depTargets = prod.Dependencies
                        .Select(d => plat == "ios" ? d : $"{d}@{plat}")
                        .ToList();
                    depMap[targetName] = depTargets;
                }
            }
        }

        // Compute transitive closures
        var closures = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var fw in depMap.Keys)
        {
            var allDeps = new HashSet<string>(StringComparer.Ordinal);
            ComputeClosure(fw, depMap, allDeps);
            if (allDeps.Count > 0)
                closures[fw] = allDeps.Order().ToArray();
        }
        return closures;
    }

    static void ComputeClosure(
        string fw, Dictionary<string, List<string>> depMap,
        HashSet<string> seen, HashSet<string>? visiting = null)
    {
        visiting ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visiting.Add(fw)) return; // Cycle detected — stop recursion
        if (!depMap.TryGetValue(fw, out var deps)) return;
        foreach (var dep in deps)
        {
            if (seen.Add(dep))
                ComputeClosure(dep, depMap, seen, visiting);
        }
    }

    // ============================================================
    // Helper: Find dependency DLL for cascading compilation
    // ============================================================

    string? FindDependencyDll(string dep, AbsolutePath outputBase)
    {
        var depBase = dep.Split('@')[0];
        var depPlatform = dep.Contains('@') ? dep.Split('@')[1] : "ios";
        var platform = ApplePlatform.FromName(depPlatform);
        var depOutdir = outputBase / dep;
        var binDir = Path.Combine(depOutdir, "bin");

        if (!Directory.Exists(binDir)) return null;

        // Prioritize specific names over generic Test.dll
        foreach (var dllName in new[] { $"{depBase}.dll", $"{depBase}.Swift.{platform.PackageSuffix}.dll", "Test.dll" })
        {
            var found = Directory.EnumerateFiles(binDir, dllName, SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("Swift.Runtime.dll"))
                .FirstOrDefault();
            if (found != null) return found;
        }
        return null;
    }

    // ============================================================
    // Helper: csproj generation and patching
    // ============================================================

    void WriteFallbackCsproj(AbsolutePath outdir, string platformName)
    {
        var csFile = FindMainCsFile(outdir);
        if (csFile == null) return;

        var csBasename = Path.GetFileName(csFile);
        var platform = ApplePlatform.FromName(platformName);
        var runtimeDll = GetRuntimeDll(platformName);

        File.WriteAllText(outdir / "Test.csproj",
$"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>{platform.GetTfm()}</TargetFramework>
    <SupportedOSPlatformVersion>{platform.MinOsVersion}</SupportedOSPlatformVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <NoWarn>0169;CA1420</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="Swift.Runtime">
      <HintPath>{runtimeDll}</HintPath>
    </Reference>
  </ItemGroup>
  <ItemGroup>
    <Compile Include="{csBasename}" />
  </ItemGroup>
</Project>
""");
    }

    void WriteDependencyCsproj(string path, string tfm, string minOs,
        AbsolutePath runtimeDll, AbsolutePath appleDll, string assemblyName, string csFilename, string refElements)
    {
        // SwiftBindings.Apple is included unconditionally: any binding whose upstream
        // dependency resolves a Swift-only Apple type will pull it in via the generated
        // bindings, and this dep-test csproj consumes that compiled DLL. Harmless if
        // the referenced .cs file doesn't touch the supplement.
        File.WriteAllText(path,
$"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>{tfm}</TargetFramework>
    <SupportedOSPlatformVersion>{minOs}</SupportedOSPlatformVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <NoWarn>0169;CA1420</NoWarn>
    <AssemblyName>{assemblyName}</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="Swift.Runtime">
      <HintPath>{runtimeDll}</HintPath>
    </Reference>
    <Reference Include="SwiftBindings.Apple">
      <HintPath>{appleDll}</HintPath>
    </Reference>
{refElements}
  </ItemGroup>
  <ItemGroup>
    <Compile Include="{csFilename}" />
  </ItemGroup>
</Project>
""");
    }

    void PatchCsprojRuntime(string csprojFile, string platformName)
    {
        var content = File.ReadAllText(csprojFile);
        var runtimeDll = GetRuntimeDll(platformName);
        var replacement = $"<Reference Include=\"Swift.Runtime\"><HintPath>{runtimeDll}</HintPath></Reference>";

        // Replace PackageReference for SwiftBindings.Runtime or Swift.Runtime
        // Handle both self-closing (<.../>) and paired (<...>...</...>) forms
        content = Regex.Replace(content,
            @"<PackageReference\s+Include=""SwiftBindings\.Runtime""[^/]*/\s*>",
            replacement);
        content = Regex.Replace(content,
            @"<PackageReference\s+Include=""SwiftBindings\.Runtime""[^>]*>.*?</PackageReference>",
            replacement, RegexOptions.Singleline);
        content = Regex.Replace(content,
            @"<PackageReference\s+Include=""Swift\.Runtime""[^/]*/\s*>",
            replacement);
        content = Regex.Replace(content,
            @"<PackageReference\s+Include=""Swift\.Runtime""[^>]*>.*?</PackageReference>",
            replacement, RegexOptions.Singleline);

        // SwiftBindings.Apple supplement is emitted as a plain PackageReference with
        // version 18.x.x — unpublished during local validation. Swap it for a raw
        // <Reference HintPath=...> to the in-tree build so NuGet restore doesn't NU1101.
        var appleDll = GetAppleSupplementDll(platformName);
        var appleReplacement = $"<Reference Include=\"SwiftBindings.Apple\"><HintPath>{appleDll}</HintPath></Reference>";
        content = Regex.Replace(content,
            @"<PackageReference\s+Include=""SwiftBindings\.Apple""[^/]*/\s*>",
            appleReplacement);
        content = Regex.Replace(content,
            @"<PackageReference\s+Include=""SwiftBindings\.Apple""[^>]*>.*?</PackageReference>",
            appleReplacement, RegexOptions.Singleline);

        File.WriteAllText(csprojFile, content);
    }

    // ============================================================
    // Helper: Swift wrapper status check
    // ============================================================

    static string CheckSwiftWrapper(AbsolutePath outdir)
    {
        // Only count wrapper .swift files — exclude .SwiftUIBridge.swift
        var swiftFile = Directory.GetFiles(outdir, "*.swift")
            .Where(f => !f.EndsWith(".SwiftUIBridge.swift"))
            .FirstOrDefault();

        if (swiftFile == null) return "no_wrapper";

        // Each *.framework dir produced by the wrapper pipeline must contain BOTH
        // the compiled binary AND an embedded Info.plist. A missing per-slice plist
        // is the failure tracked as Issue 1 — the SDK's merge
        // target builds the device slice via `swiftc -emit-library` which emits
        // only a binary, so the merged xcframework can ship with a slice that
        // lacks Info.plist and is uninstallable on device.
        var frameworkDirs = Directory.EnumerateDirectories(
                outdir, "*SwiftBindings.framework", SearchOption.AllDirectories)
            .ToList();

        if (frameworkDirs.Count == 0) return "fail";

        foreach (var fwDir in frameworkDirs)
        {
            var moduleName = Path.GetFileNameWithoutExtension(fwDir);
            var binary = Path.Combine(fwDir, moduleName);
            var plist = Path.Combine(fwDir, "Info.plist");
            if (!File.Exists(binary) || !File.Exists(plist))
                return "fail";
        }

        // NativeThunk regression guard (Issue B). Every `_thunk_*` symbol declared
        // in the generator's *.arm64.s output must be defined in every shipped
        // wrapper slice. The shape of Issue B was: first-slice path linked thunks,
        // second-slice (device) path did not, so device binaries shipped with the
        // symbols missing and crashed at dispatch. Asserting both slices contain
        // every thunk symbol makes that regression surface as swift_compile: fail.
        var expectedThunks = CollectExpectedThunkSymbols(outdir);
        if (expectedThunks.Count > 0)
        {
            foreach (var fwDir in frameworkDirs)
            {
                var moduleName = Path.GetFileNameWithoutExtension(fwDir);
                var binary = Path.Combine(fwDir, moduleName);
                var definedSymbols = ReadDefinedSymbols(binary);
                foreach (var thunk in expectedThunks)
                {
                    if (!definedSymbols.Contains(thunk))
                        return "fail";
                }
            }
        }

        return "ok";
    }

    // Collect `_thunk_*` symbols declared in the generator's *.arm64.s files.
    // NativeThunkEmitter writes each thunk as `.globl _thunk_<module>_<hash>`.
    static HashSet<string> CollectExpectedThunkSymbols(AbsolutePath outdir)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asm in Directory.EnumerateFiles(outdir, "*.arm64.s",
                     SearchOption.TopDirectoryOnly))
        {
            foreach (var line in File.ReadLines(asm))
            {
                var trimmed = line.TrimStart();
                const string prefix = ".globl ";
                if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var sym = trimmed.Substring(prefix.Length).Trim();
                if (sym.StartsWith("_thunk_", StringComparison.Ordinal))
                    symbols.Add(sym);
            }
        }
        return symbols;
    }

    // Read the set of defined (text or data) symbols from a Mach-O binary via
    // `nm -j -U`. `-U` omits undefined symbols; `-j` emits just the name so we
    // match exactly the `_thunk_*` names from the assembly source.
    static HashSet<string> ReadDefinedSymbols(string binaryPath)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nm",
                Arguments = $"-j -U \"{binaryPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return defined;
            while (!proc.StandardOutput.EndOfStream)
            {
                var line = proc.StandardOutput.ReadLine();
                if (!string.IsNullOrWhiteSpace(line))
                    defined.Add(line.Trim());
            }
            proc.WaitForExit();
        }
        catch { /* guard is best-effort — swallow to avoid masking the real validation result */ }
        return defined;
    }

    // ============================================================
    // Helper: dotnet build/restore wrappers
    // ============================================================

    static void RunDotnetRestore(string csprojFile)
    {
        try
        {
            ProcessTasks.StartProcess(
                    "dotnet", $"restore \"{csprojFile}\" -v quiet",
                    logOutput: false)
                .AssertWaitForExit();
        }
        catch { /* restore failures are handled by build */ }
    }

    static (int ExitCode, int CsErrors, string Output) RunDotnetBuild(string csprojFile)
    {
        var process = ProcessTasks.StartProcess(
            "dotnet", $"build \"{csprojFile}\" -p:EnableDefaultCompileItems=false --no-restore -v quiet",
            logOutput: false);
        process.AssertWaitForExit();

        var output = string.Join("\n", process.Output.Select(o => o.Text));
        var csErrors = process.Output
            .Select(o => o.Text)
            .Where(l => l.Contains("error CS"))
            .Distinct()
            .Count();

        return (process.ExitCode, csErrors, output);
    }

    // ============================================================
    // Helper: CS error formatting
    // ============================================================

    static string? ExtractInfraError(string buildOutput)
    {
        foreach (var line in buildOutput.Split('\n'))
        {
            if (line.Contains("error ", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("error CS"))
                return line.Trim();
        }
        return null;
    }

    static void ShowCsErrors(string buildOutput, int max)
    {
        var errors = buildOutput.Split('\n')
            .Where(l => l.Contains("error CS"))
            .Take(max);
        foreach (var e in errors)
            Log.Debug("    {Error}", e.Trim());
    }

    static string FormatCsErrors(string buildOutput, int max)
    {
        var errors = buildOutput.Split('\n')
            .Where(l => l.Contains("error CS"))
            .Take(max)
            .Select(e => $"    {e.Trim()}");
        var result = string.Join("\n", errors);
        var total = buildOutput.Split('\n').Count(l => l.Contains("error CS"));
        if (total > max)
            result += $"\n    ... and {total - max} more";
        return result;
    }

    // Count CS errors excluding transitive framework-binding misses. Apple-framework
    // bindings reference types from sibling framework bindings (Swift.Foundation,
    // RealityFoundation.Entity, ARKit.ARRaycastQueryTarget, …) that aren't present in
    // the validation sandbox — a single framework is compiled in isolation, so the
    // C# compiler reports the unresolvable namespace. Those errors are expected
    // side-effects of standalone compilation, not generator bugs. Real emitter bugs
    // (CS0246/CS0305 etc. against types the framework declares itself, plus CS0535
    // missing-implementation diagnostics) remain visible.
    //
    // Filter shapes:
    //   * CS0234 "does not exist in the namespace 'X'" — bare unresolved reference
    //     into a sibling apple framework's namespace.
    //   * CS0246 "type or namespace name 'X' could not be found" — the sibling
    //     namespace itself is unknown (no using/reference in the validation csproj).
    // X must be the umbrella module's own namespace (apple-framework registry hit)
    // for the filter to apply, so generator bugs that produce arbitrary unresolved
    // identifiers stay counted.
    static int CountNonTransitiveCsErrors(string buildOutput, string? currentTargetName = null)
    {
        return buildOutput.Split('\n')
            .Where(l => l.Contains("error CS"))
            .Where(l => !IsTransitiveSiblingFrameworkError(l, currentTargetName))
            .Distinct()
            .Count();
    }

    static readonly Regex _cs0234NamespaceRegex = new(
        @"error CS0234.*namespace '([^']+)'", RegexOptions.Compiled);
    static readonly Regex _cs0246IdentifierRegex = new(
        @"error CS0246.*type or namespace name '([^']+)'", RegexOptions.Compiled);

    static bool IsTransitiveSiblingFrameworkError(string line, string? currentTargetName = null)
    {
        // Legacy shape: pre-umbrella-threading bindings emitted Swift.Foundation /
        // Swift.CoreLocation references. Those CS0234s remain part of the filter so
        // older targets keep clean.
        if (line.Contains("error CS0234") && line.Contains("namespace 'Swift'"))
            return true;

        var ns0234 = _cs0234NamespaceRegex.Match(line);
        if (ns0234.Success
            && IsKnownAppleFrameworkNamespace(ns0234.Groups[1].Value)
            && !IsCurrentTargetNamespace(ns0234.Groups[1].Value, currentTargetName))
            return true;

        var id0246 = _cs0246IdentifierRegex.Match(line);
        if (id0246.Success
            && IsKnownAppleFrameworkNamespace(id0246.Groups[1].Value)
            && !IsCurrentTargetNamespace(id0246.Groups[1].Value, currentTargetName))
            return true;

        return false;
    }

    /// <summary>
    /// A CS0234/CS0246 against the namespace of the framework currently being validated
    /// is a real generator bug (e.g. an emitted reference like `RealityKit.IEvent` inside
    /// the RealityKit binding itself), not a transitive sibling-framework miss. Allow it
    /// to count even though the namespace is in the apple-framework registry.
    /// </summary>
    static bool IsCurrentTargetNamespace(string namespaceCandidate, string? currentTargetName)
    {
        if (string.IsNullOrEmpty(currentTargetName))
            return false;
        var name = namespaceCandidate;
        if (name.StartsWith("Swift.", StringComparison.Ordinal))
            name = name.Substring("Swift.".Length);
        // Apple-framework target names carry a "@platform" suffix (e.g. "Foundation@macos")
        // while the emitted C# namespace is the bare module (e.g. "Foundation"). Strip the
        // suffix so non-iOS runs don't fall through to the sibling-framework noise filter.
        var bareTarget = currentTargetName;
        var atIdx = bareTarget.IndexOf('@');
        if (atIdx >= 0)
            bareTarget = bareTarget.Substring(0, atIdx);
        return string.Equals(name, bareTarget, StringComparison.Ordinal);
    }

    static bool IsKnownAppleFrameworkNamespace(string namespaceCandidate)
    {
        // Match the umbrella module's own namespace, including Swift.<Module> shapes
        // generated for compiled bindings.
        var name = namespaceCandidate;
        if (name.StartsWith("Swift.", StringComparison.Ordinal))
            name = name.Substring("Swift.".Length);
        return _appleFrameworkModules.Value.Contains(name);
    }

    static readonly Lazy<HashSet<string>> _appleFrameworkModules = new(LoadAppleFrameworkModules);

    static HashSet<string> LoadAppleFrameworkModules()
    {
        var modules = new HashSet<string>(StringComparer.Ordinal);
        var jsonPath = Path.Combine(
            NukeBuild.RootDirectory, "src", "Swift.Bindings", "src", "Data", "apple-frameworks.json");
        if (!File.Exists(jsonPath))
            return modules;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.TryGetProperty("frameworks", out var frameworks)
                && frameworks.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var fw in frameworks.EnumerateArray())
                {
                    if (fw.TryGetProperty("module", out var m)
                        && m.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        modules.Add(m.GetString()!);
                    }
                }
            }
        }
        catch
        {
            // Filter degrades to "no apple-framework matches" — generator-bug detection
            // remains correct, just noisier with sibling-framework noise.
        }

        return modules;
    }

    // ============================================================
    // Helper: Find main .cs file (excludes Wrappers and SwiftUIBridge)
    // ============================================================

    static string? FindMainCsFile(AbsolutePath outdir)
    {
        if (!Directory.Exists(outdir)) return null;
        return Directory.GetFiles(outdir, "*.cs")
            .Where(f => !f.EndsWith(".Wrappers.cs") && !f.EndsWith(".SwiftUIBridge.cs"))
            .FirstOrDefault();
    }

    // ============================================================
    // Helper: Swift marker for display
    // ============================================================

    static string GetSwiftMarker(string name, ConcurrentDictionary<string, TargetResult> results)
    {
        var sw = results.GetValueOrDefault(name)?.SwiftCompile ?? "unknown";
        return sw switch
        {
            "ok" => " [swift:ok]",
            "fail" => " [swift:fail]",
            _ => ""
        };
    }

    // ============================================================
    // Helper: Git info
    // ============================================================

    string GetGitBranch()
    {
        try
        {
            var process = ProcessTasks.StartProcess(
                "git", $"-C \"{RootDirectory}\" rev-parse --abbrev-ref HEAD",
                logOutput: false);
            process.AssertWaitForExit();
            return process.Output.StdToText().Trim().Replace('/', '-');
        }
        catch { return "default"; }
    }

    string GetGitShortSha()
    {
        try
        {
            var process = ProcessTasks.StartProcess(
                "git", $"-C \"{RootDirectory}\" rev-parse --short HEAD",
                logOutput: false);
            process.AssertWaitForExit();
            return process.Output.StdToText().Trim();
        }
        catch { return "unknown"; }
    }

    // ============================================================
    // Helper: Runtime version extraction
    // ============================================================

    string GetRuntimeVersion()
    {
        // The in-tree version is the SwiftBindingsSdkVersion default in Directory.Build.props
        // (0.0.0-dev), which single-sources every package version. The generator's
        // DefaultSwiftRuntimeVersion is baked from this same property, so reading the props
        // default reports what an un-overridden build would emit.
        var props = RootDirectory / "Directory.Build.props";
        if (!File.Exists(props)) return "unknown";

        var match = Regex.Match(File.ReadAllText(props),
            @"<SwiftBindingsSdkVersion[^>]*>([^<]*)</SwiftBindingsSdkVersion>");
        return match.Success ? match.Groups[1].Value : "unknown";
    }

    // ============================================================
    // Helper: Collect skip metrics from binding-report.json files
    // ============================================================

    ValidationBaseline.SkipMetricsBaseline CollectSkipMetrics(AbsolutePath outputBase, IReadOnlySet<string> targetNames)
    {
        int totalEmitted = 0, totalSkipped = 0, failedReports = 0, failedManifests = 0;
        var skipReasons = new Dictionary<string, int>();
        var subCauses = new Dictionary<string, int>();

        if (!Directory.Exists(outputBase))
            return new ValidationBaseline.SkipMetricsBaseline();

        // Only aggregate reports from targets validated in this run, not stale cached outputs
        var reportFiles = targetNames
            .Select(name => Path.Combine(outputBase, name, "binding-report.json"))
            .Where(File.Exists);

        foreach (var reportFile in reportFiles)
        {
            try
            {
                var json = File.ReadAllText(reportFile);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("EmittedMembers", out var em) &&
                    em.ValueKind == System.Text.Json.JsonValueKind.Number &&
                    em.TryGetInt32(out var emVal))
                    totalEmitted += emVal;
                if (root.TryGetProperty("SkippedMembers", out var sm) &&
                    sm.ValueKind == System.Text.Json.JsonValueKind.Number &&
                    sm.TryGetInt32(out var smVal))
                    totalSkipped += smVal;

                if (root.TryGetProperty("SkippedItems", out var items) &&
                    items.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("Reason", out var reason))
                        {
                            var r = reason.GetString() ?? "Unknown";
                            skipReasons[r] = skipReasons.GetValueOrDefault(r) + 1;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failedReports++;
                Log.Debug("Failed to parse {File}: {Message}", reportFile, ex.Message);
            }
        }

        // Post-processor sub-cause histogram lives on the binding-artifact-manifest, not the
        // binding-report — the manifest aggregates wrapper-compile state, the report aggregates
        // emission state. Read both so the baseline carries both signals.
        var manifestFiles = targetNames
            .Select(name => Path.Combine(outputBase, name, "binding-artifact-manifest.json"))
            .Where(File.Exists);

        foreach (var manifestFile in manifestFiles)
        {
            try
            {
                var json = File.ReadAllText(manifestFile);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("Wrapper", out var wrapper) ||
                    wrapper.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                if (!wrapper.TryGetProperty("PostProcessorStrippedBlocksBySubCause", out var bucket) ||
                    bucket.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                foreach (var prop in bucket.EnumerateObject())
                {
                    if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Number ||
                        !prop.Value.TryGetInt32(out var count))
                        continue;
                    subCauses[prop.Name] = subCauses.GetValueOrDefault(prop.Name) + count;
                }
            }
            catch (Exception ex)
            {
                failedManifests++;
                Log.Debug("Failed to parse {File}: {Message}", manifestFile, ex.Message);
            }
        }

        if (failedReports > 0)
            Log.Warning("Skip metrics: {Count} binding-report.json file(s) could not be parsed",
                failedReports);
        if (failedManifests > 0)
            Log.Warning("Skip metrics: {Count} binding-artifact-manifest.json file(s) could not be parsed",
                failedManifests);

        var total = totalEmitted + totalSkipped;
        return new ValidationBaseline.SkipMetricsBaseline
        {
            TotalEmittedMembers = totalEmitted,
            TotalSkippedMembers = totalSkipped,
            SkipRatePct = total > 0 ? Math.Round((double)totalSkipped / total * 100, 1) : 0,
            SkipReasons = skipReasons.OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            PostProcessorSubCauses = subCauses
                .OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
        };
    }

    // ============================================================
    // Helper: Result tracking
    // ============================================================

    static TargetResult GetOrCreateResult(ConcurrentDictionary<string, TargetResult> results, string name)
        => results.GetOrAdd(name, _ => new TargetResult());

    static void SetResult(ConcurrentDictionary<string, TargetResult> results, string name,
        string? compile = null, string? depCompile = null, int? errors = null)
    {
        var r = GetOrCreateResult(results, name);
        if (compile != null) r.Compile = compile;
        if (depCompile != null) r.DepCompile = depCompile;
        if (errors.HasValue) r.Errors = errors.Value;
    }

}

/// <summary>
/// Mutable result tracker for a single validation target.
/// Stored in ConcurrentDictionary (one instance per target). Thread safety relies on
/// phases running sequentially — each phase writes to different properties, so no
/// concurrent writes to the same field occur.
/// </summary>
class TargetResult
{
    // Generation
    public string Gen { get; set; } = "unknown";
    public int GenSeconds { get; set; }
    public string? GenVerbose { get; set; }
    public string? GenOutput { get; set; }
    public int Lines { get; set; }

    // Swift wrapper compilation
    public string SwiftCompile { get; set; } = "unknown";
    public string? SwiftVerbose { get; set; }
    public string? SwiftOutput { get; set; }

    // C# compilation
    public string Compile { get; set; } = "unknown";
    public string? DepCompile { get; set; } = "none";
    public int Errors { get; set; }
    public string? CompileOutput { get; set; }
}
