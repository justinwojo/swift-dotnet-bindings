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
    AbsolutePath GeneratorDll => RootDirectory / "src" / "Swift.Bindings" / "src" / "bin" / "Debug" / DotNetTfm / "Swift.Bindings.dll";

    AbsolutePath GetRuntimeDll(string platform)
    {
        var tfm = ApplePlatform.FromName(platform).GetTfm();
        return RootDirectory / "src" / "Swift.Runtime" / "src" / "bin" / "Debug" / tfm / "Swift.Runtime.dll";
    }

    // ============================================================
    // Validate target — library validation compile gate
    // ============================================================

    Target Validate => _ => _
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

            // --- Phase 1: Prerequisites ---
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
                // Check .libraries/ exists
                if (!Directory.Exists(LibrariesDir))
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

            // --- Phase 2: Build Generator ---
            if (!Quick)
                BuildGeneratorIfChanged(outputBase);

            // --- Determine which targets have declared dependencies ---
            var hasDeps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lib in manifest.Libraries)
                foreach (var prod in lib.Products)
                    if (prod.Dependencies is { Count: > 0 })
                        hasDeps.Add(prod.Framework);

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

            // --- Phase 3: Generate, Compile Wrappers, and Compile C# ---
            Log.Information("--- Binding Pipeline ---");

            var semaphore = new SemaphoreSlim(maxJobs);

            // === Phase 3a: Generate All Bindings (parallel) ===
            Log.Debug("Phase 3a: Generating {Count} targets with {Jobs} parallel workers...",
                totalTargets, maxJobs);
            var phase3aStart = DateTime.UtcNow;

            await Task.WhenAll(sortedTargets.Select(async target =>
            {
                await semaphore.WaitAsync();
                try { await Task.Run(() => GenerateTarget(target, outputBase, results)); }
                finally { semaphore.Release(); }
            }));

            Log.Debug("Phase 3a completed in {Seconds}s",
                (int)(DateTime.UtcNow - phase3aStart).TotalSeconds);

            // Display Phase 3a results in manifest order
            foreach (var target in displayTargets)
            {
                if (results.TryGetValue(target.Name, out var r) && r.GenOutput != null)
                    Log.Information("{Output}", r.GenOutput);
            }

            // === Phase 3b: Compile Swift Wrappers (parallel) ===
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

            // Display Phase 3b results and compute swift counters
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

            // === Phase 3c: C# Compile Gate ===
            Log.Information("");
            Log.Information("--- Compile Gate ---");

            // Split into non-dep and dep targets
            var nonDepTargets = sortedTargets.Where(t => !hasDeps.Contains(t.Name.Split('@')[0])).ToList();
            var depTargets = sortedTargets.Where(t => hasDeps.Contains(t.Name.Split('@')[0])).ToList();

            // Phase 3c-standalone: Compile non-dep targets in parallel
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

            // === Phase 3c-dependency: Cascading Dependency Gate ===
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

                        WriteDependencyCsproj(depCsproj, platform.GetTfm(), platform.MinOsVersion,
                            runtimeDll, depFwBase, csBasename, refElements);

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

            // === Phase 4: Baseline & Regression Detection ===
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

            // Load previous baseline for comparison
            var prevBaseline = ValidationBaseline.Load(BaselinePath);

            // Update baseline only on full unfiltered runs
            if (isFullRun)
            {
                var newBaseline = new ValidationBaseline
                {
                    GitSha = GetGitShortSha(),
                    Gate = new() { Libraries = currentResults }
                };
                newBaseline.Save(BaselinePath);
            }
            else
            {
                Log.Debug("Filtered run — baseline not updated");
            }

            // === Phase 5: Regression Detection ===
            if (prevBaseline.Gate.Libraries.Count > 0)
            {
                Log.Information("--- Regression Check ---");
                var (regressions, improvements, drift) = prevBaseline.Compare(currentResults, isFullRun);

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
            if (overallFailed <= 0 && compileNoOutput == 0)
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

            if (isFullRun)
                Log.Debug("  Baseline: {Path} (updated)", BaselinePath);
            else
                Log.Debug("  Baseline: {Path} (not updated — filtered/tier run)", BaselinePath);

            Log.Information("");

            // Exit with failure if any compile failures
            if (compileFailed > 0 || compileNoOutput > 0 || depFailed > 0)
                Assert.Fail($"Validation failed: {compileFailed + depFailed} compile failures, {compileNoOutput} no output");
        });

    // ============================================================
    // Phase 3a: Generate Bindings
    // ============================================================

    void GenerateTarget(ValidationTarget target, AbsolutePath outputBase,
        ConcurrentDictionary<string, TargetResult> results)
    {
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
    // Phase 3b: Compile Swift Wrappers
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

                // Capture Swift error lines when verbose + fail
                if (Verbose && swiftStatus == "fail")
                {
                    var swiftErrors = process.Output
                        .Where(o => Regex.IsMatch(o.Text, @"\.swift:\d+:\d+: error:"))
                        .Take(5)
                        .Select(o => o.Text);
                    result.SwiftVerbose = string.Join("\n", swiftErrors);
                }
            }
            catch (Exception ex)
            {
                result.SwiftCompile = "fail";
                Log.Debug("  {Name}: wrapper compilation exception: {Message}", target.Name, ex.Message);
            }

            // Format swift wrapper result
            if (result.SwiftVerbose != null)
                result.SwiftOutput = $"    {result.SwiftVerbose}\n";

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
    // Phase 3c: Compile C# (standalone, non-dependency)
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

        var files = new List<string>();
        foreach (var dir in new[] { generatorSrc.ToString(), runtimeSrc.ToString() })
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
        AbsolutePath runtimeDll, string assemblyName, string csFilename, string refElements)
    {
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

        // Check for compiled wrapper binary.
        // The framework name is {Library}SwiftBindings.framework/{Library}SwiftBindings,
        // so match any path containing "SwiftBindings.framework" with a non-plist file
        // whose name ends with "SwiftBindings" (matching bash: -path "*SwiftBindings.framework/*SwiftBindings").
        var wrapperBinary = Directory.EnumerateFiles(outdir, "*", SearchOption.AllDirectories)
            .Where(f => f.Contains("SwiftBindings.framework") &&
                        !f.EndsWith(".plist") &&
                        Path.GetFileName(f).EndsWith("SwiftBindings"))
            .FirstOrDefault();

        return wrapperBinary != null ? "ok" : "fail";
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
        var emitterFile = RootDirectory / "src" / "Swift.Bindings" / "src" / "Emitter" / "BindingProjectEmitter.cs";
        if (!File.Exists(emitterFile)) return "unknown";

        var match = Regex.Match(File.ReadAllText(emitterFile),
            @"DefaultSwiftRuntimeVersion\s*=\s*""([^""]*)""");
        return match.Success ? match.Groups[1].Value : "unknown";
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
    // Phase 3a: Generation
    public string Gen { get; set; } = "unknown";
    public int GenSeconds { get; set; }
    public string? GenVerbose { get; set; }
    public string? GenOutput { get; set; }
    public int Lines { get; set; }

    // Phase 3b: Swift wrapper compilation
    public string SwiftCompile { get; set; } = "unknown";
    public string? SwiftVerbose { get; set; }
    public string? SwiftOutput { get; set; }

    // Phase 3c: C# compilation
    public string Compile { get; set; } = "unknown";
    public string? DepCompile { get; set; } = "none";
    public int Errors { get; set; }
    public string? CompileOutput { get; set; }
}
