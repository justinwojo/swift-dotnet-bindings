// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    AbsolutePath StdlibShadowTrapFile => BindingTestsDir / "StdlibShadowTrap" / "StdlibShadowTrap.swift";

    /// <summary>
    /// Compile-only gate: every standard-library name the emitted Swift relies on must be spelled
    /// module-qualified, because a member of the bound library (a property, a static func, a
    /// protocol-extension typealias) outranks a bare stdlib name inside the extension bodies the
    /// wrapper writes. Each emitted Swift source set is typechecked together with
    /// <c>BindingTests/StdlibShadowTrap/StdlibShadowTrap.swift</c> in its own module, where the
    /// trap's unavailable same-module declarations capture any unqualified use. Typecheck only —
    /// nothing built here is linked, and the trap never reaches the library or any binding output.
    ///
    /// A canary with two bare uses runs first and must FAIL, so a trap file that stopped capturing
    /// (a signature drifting away from the standard library's, say) reds the gate instead of
    /// passing it vacuously.
    /// </summary>
    void RunStdlibShadowTrapGate()
    {
        Log.Information("--- Stdlib-shadow trap: emitted Swift must qualify standard-library names ---");
        if (!File.Exists(StdlibShadowTrapFile))
            throw new Exception($"Stdlib-shadow trap file missing: {StdlibShadowTrapFile}");

        var platform = ResolvedPlatform;
        var sliceId = platform.SimulatorSliceId;
        var sdkPath = XcRun.GetSdkPath(platform.SimulatorSdkName);
        var mainSlice = BtXcframeworkDir / sliceId;
        var depSlice = BtDepXcframeworkDir / sliceId;
        var workDir = BtOutputDir / ".stdlib-shadow-trap";
        if (Directory.Exists(workDir))
            workDir.DeleteDirectory();
        workDir.CreateDirectory();

        try
        {
            // Positive control: the trap must capture.
            var canary = workDir / "Canary.swift";
            File.WriteAllText(canary,
                "func stdlibShadowTrapCanaryType() -> Swift.Int { MemoryLayout<Swift.Int>.size }\n" +
                "func stdlibShadowTrapCanaryFunc() -> Swift.Int { max(1, 2) }\n");
            var (canaryOk, canaryLog) = TypecheckWithTrap("canary", new[] { (string)canary }, "StdlibShadowTrapCanary",
                platform.SimulatorTarget, sdkPath, Array.Empty<string>());
            var captured = canaryLog.Split('\n').Count(l => l.Contains("error:") && l.Contains("is unavailable") && !l.TrimStart().StartsWith("|"));
            if (canaryOk || captured < 2)
                throw new Exception("Stdlib-shadow trap canary did not fail on its two bare uses — the trap file no longer " +
                                    "captures unqualified standard-library names, so this gate would pass vacuously.");

            var failures = new List<string>();

            // Main wrapper, scrubbed by the generator's own post-processor exactly as the wrapper build does.
            var mainSwift = Directory.GetFiles(BtOutputDir, "*.swift").Where(f => !f.EndsWith(".SwiftUIBridge.swift")).ToList();
            if (mainSwift.Count > 0)
            {
                var cleaned = workDir / "main";
                cleaned.CreateDirectory();
                RunWrapperPostProcess(mainSwift, cleaned, LoadInternalTypeNames(BtOutputDir / "wrapper-context.json"), ModuleName, "stdlib-trap-main");
                var search = new List<string> { mainSlice + "/" };
                if (Directory.Exists(depSlice)) search.Add(depSlice + "/");
                CheckSet("main wrapper", Directory.GetFiles(cleaned, "*.swift"), WrapperModule, search);
            }
            else
                throw new Exception("Stdlib-shadow trap: no emitted wrapper Swift found under the BindingTests output.");

            // Dependency wrapper (preserved sources + its own internalTypeNames).
            var depSwiftDir = BtOutputDir / "dep-swift";
            var depSwift = Directory.Exists(depSwiftDir) ? Directory.GetFiles(depSwiftDir, "*.swift").ToList() : new List<string>();
            if (depSwift.Count > 0)
            {
                var cleaned = workDir / "dep";
                cleaned.CreateDirectory();
                RunWrapperPostProcess(depSwift, cleaned, LoadInternalTypeNames(depSwiftDir / "wrapper-context.json"), DepModuleName, "stdlib-trap-dep");
                CheckSet("dependency wrapper", Directory.GetFiles(cleaned, "*.swift"), $"{DepModuleName}SwiftBindings",
                    new List<string> { depSlice + "/" });
            }

            // SwiftUI bridge — the generated file alone; the hand-written test helpers are not emitted output.
            var bridge = BtOutputDir / $"{ModuleName}.SwiftUIBridge.swift";
            if (File.Exists(bridge))
            {
                var search = new List<string> { mainSlice + "/" };
                if (Directory.Exists(depSlice)) search.Add(depSlice + "/");
                CheckSet("SwiftUI bridge", new[] { (string)bridge }, BridgeModule, search);
            }

            if (failures.Count > 0)
                throw new Exception("Stdlib-shadow trap: emitted Swift uses unqualified standard-library names (a bound " +
                                    "module's member of the same name would capture them). Spell them Swift.X / " +
                                    "_Concurrency.X at the emission site:\n" + string.Join("\n", failures));
            Log.Information("Stdlib-shadow trap passed.");

            void CheckSet(string label, IEnumerable<string> files, string module, List<string> searchPaths)
            {
                var (ok, log) = TypecheckWithTrap(label, files, module, platform.SimulatorTarget, sdkPath, searchPaths);
                if (ok) { Log.Information("  {Label}: clean", label); return; }
                var errors = log.Split('\n').Where(l => l.Contains("error:") && !l.TrimStart().StartsWith("|")).Distinct().ToList();
                failures.Add($"  [{label}] {errors.Count} error(s):");
                failures.AddRange(errors.Take(40).Select(e => "    " + e.Trim()));
            }
        }
        finally
        {
            CleanupWrapperBuild(workDir);
        }
    }

    (bool ok, string log) TypecheckWithTrap(string label, IEnumerable<string> files, string module, string target, string sdkPath,
        IEnumerable<string> frameworkSearchPaths)
    {
        var settings = new SwiftCompilerSettings()
            .SetTarget(target)
            .SetSdk(sdkPath)
            .SetModuleName(module)
            .SetStrictConcurrency("minimal")
            .AddExtraArgument("-typecheck")
            .AddSourceFiles(files.Append((string)StdlibShadowTrapFile));
        foreach (var p in frameworkSearchPaths)
            settings.AddFrameworkSearchPath(p);
        // Quiet: the canary is expected to fail, and a failing set is replayed by the caller.
        var process = ProcessTasks.StartProcess(XcRun.FindTool("swiftc"), settings.BuildArguments(), logOutput: false);
        process.WaitForExit();
        var log = string.Join("\n", process.Output.Select(o => o.Text));
        return (process.ExitCode == 0, log);
    }
}
