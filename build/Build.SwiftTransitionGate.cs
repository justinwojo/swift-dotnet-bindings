// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.SwiftTransitionGate.cs — `nuke swift-transition-gate`: every managed↔Swift transition in a
// compiled binding assembly is one every .NET runtime gets right.
//
// The .NET 11 CoreCLR interpreter mishandles the Swift calling convention: it clobbers the thread
// pointer when it loads the self register, shifts ordinary arguments after a SwiftSelf or
// SwiftError, writes a reverse callback's error into its first argument, and on x86_64 applies no
// Swift lowering at all. It runs all Debug code on Apple mobile and runtime-created generics even in
// Release. What every runtime gets right is a primitive-only signature. The rule engine
// (build/SwiftTransitionGate/) reads the compiled IL and metadata and reports every transition
// outside that safe set, by rule:
//   R1  forward CallConvSwift P/Invoke that is not primitive-only
//   R2  SwiftSelf / SwiftSelf<T> / SwiftIndirectResult in a non-Swift signature
//   R4  UnmanagedCallersOnly with CallConvSwift, or SwiftSelf<T> in a reverse signature
//   R5  forward calli through an unmanaged[Swift] pointer that is not primitive-only
// The plan-dependent rules (R3 forward twins, R7 reverse coverage) arrive with the bridge plans;
// the engine already takes an optional plan source (--transition-gate-plan-source) for them.
//
// Standalone for now: the tree is red until the bridges land, so this is not yet wired into
// --compile-only or pack. It is policy, not baseline — no reseed and no --permissive arm.
//
// Default input: the BindingTests compile-check assembly and the Swift.Runtime and
// SwiftBindings.Apple assemblies built beside it (run `nuke binding-tests --compile-only` first).
// Pass --transition-gate-assembly to scan other assemblies (packed or shipped package DLLs); each
// assembly's own directory and the host runtime directory are searched for referenced assemblies,
// plus any --transition-gate-reference-dir.
//
// Writes swift-transition-report.json (verdict, counts, violations), swift-transition-inventory.json
// (every transition site with the rules it violates) and swift-transition-summary.txt.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.TransitionGate;

partial class Build
{
    [Parameter("Swift transition gate: compiled assemblies to scan (default: the BindingTests compile-check assembly and the Swift.Runtime and SwiftBindings.Apple assemblies beside it)")]
    readonly string[] TransitionGateAssembly = Array.Empty<string>();

    [Parameter("Swift transition gate: extra directories searched for assemblies the scanned ones reference")]
    readonly string[] TransitionGateReferenceDir = Array.Empty<string>();

    [Parameter("Swift transition gate: the build-tree binding-report.json holding the bridge plans " +
               "(optional; the plan-dependent rules read it)")]
    readonly string? TransitionGatePlanSource;

    [Parameter("Swift transition gate: directory the report, site inventory and summary are written to")]
    readonly string? TransitionGateOutput;

    AbsolutePath CompileCheckOutputDir => BindingTestsDir / "CompileCheck" / "bin" / "Debug" / $"{BtFramework}-ios";

    // The binding under test, then the runtime and Apple supplement it was compiled against.
    string[] DefaultTransitionGateAssemblies => new[] { "CompileCheck.dll", "Swift.Runtime.dll", "SwiftBindings.Apple.dll" }
        .Select(name => (CompileCheckOutputDir / name).ToString()).ToArray();

    Target SwiftTransitionGate => _ => _
        .Description("Scan compiled binding assemblies for managed↔Swift transitions the .NET 11 interpreter " +
                     "mishandles (rules R1, R2, R4, R5); writes a JSON report, a site inventory and a summary.")
        // Ordering only: keep standalone sinks totally ordered for Nuke --strict.
        .After(Net11PackageConsumerTests)
        .Executes(() =>
        {
            var defaultInput = TransitionGateAssembly.Length == 0;
            var assemblies = defaultInput
                ? DefaultTransitionGateAssemblies
                : TransitionGateAssembly.Select(p => Path.GetFullPath(p)).ToArray();
            foreach (var assembly in assemblies)
                if (!File.Exists(assembly))
                    throw new Exception(defaultInput
                        ? $"Swift transition gate: {assembly} not found. Run `nuke binding-tests --compile-only` first."
                        : $"Swift transition gate: {assembly} not found.");

            TransitionPlanSource? planSource = null;
            if (TransitionGatePlanSource is { Length: > 0 } planPath)
            {
                var full = Path.GetFullPath(planPath);
                if (!File.Exists(full))
                    throw new Exception($"Swift transition gate: plan source {full} not found.");
                planSource = new TransitionPlanSource(TransitionPlanSourceKind.BuildReport, full);
            }

            var runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var reports = new List<AssemblyTransitionReport>();
            foreach (var assembly in assemblies)
            {
                var options = new SwiftTransitionScanOptions
                {
                    ReferenceDirectories = new[] { Path.GetDirectoryName(assembly)! }
                        .Concat(TransitionGateReferenceDir.Select(d => Path.GetFullPath(d)))
                        .Append(runtimeDirectory)
                        .ToArray(),
                    PlanSource = planSource,
                };
                reports.Add(SwiftTransitionScanner.Scan(assembly, options));
            }

            var output = TransitionGateOutput is { Length: > 0 } dir
                ? (AbsolutePath)Path.GetFullPath(dir)
                : BtOutputDir / ".swift-transition-gate";
            output.CreateDirectory();
            var jsonPath = output / "swift-transition-report.json";
            var inventoryPath = output / "swift-transition-inventory.json";
            var summaryPath = output / "swift-transition-summary.txt";
            File.WriteAllText(jsonPath, SwiftTransitionReport.ToJson(reports));
            File.WriteAllText(inventoryPath, SwiftTransitionReport.ToInventoryJson(reports));
            var summary = SwiftTransitionReport.ToSummary(reports);
            File.WriteAllText(summaryPath, summary);

            Log.Information("=========================================");
            Log.Information(" Swift transition gate");
            Log.Information("=========================================");
            foreach (var line in summary.Split('\n'))
                Log.Information("{Line}", line.TrimEnd('\r'));
            Log.Information("Report: {Json}", jsonPath);
            Log.Information("Site inventory: {Inventory}", inventoryPath);
            Log.Information("Summary: {Summary}", summaryPath);

            if (!reports.All(r => r.Passed))
            {
                var totals = SwiftTransitionReport.CountByRule(reports.SelectMany(r => r.Violations));
                var errors = reports.Sum(r => r.InspectionErrors.Length);
                throw new Exception(
                    "Swift transition gate: " + string.Join(", ", totals.Select(kv => $"{kv.Key}={kv.Value}")) +
                    $", inspection errors={errors}. Each listed transition is one the .NET 11 interpreter can " +
                    "mis-place or crash on — see the lines above.");
            }
            Log.Information("Swift transition gate: PASSED");
        });
}
