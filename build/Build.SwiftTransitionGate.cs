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
//   R3  a forward bridge's twin import (SBSR_…) that is not the C-convention, primitive-only
//       signature its call plan states
//   R4  UnmanagedCallersOnly with CallConvSwift, or SwiftSelf<T> in a reverse signature
//   R5  forward calli through an unmanaged[Swift] pointer that is not primitive-only
// R3 reads the generator's call-plan sidecars: by default those beside each ledgered assembly's
// C#, or the directory --transition-gate-plan-source names.
//
// Standalone for now: the tree is red until the bridges land, so this target is not yet wired into
// --compile-only or pack. R3 alone already is: the compile-only ledger cross-check step fails on any
// R3 violation. It is policy, not baseline — no reseed and no --permissive arm.
//
// Default input: the BindingTests compile-check assembly and the Swift.Runtime and
// SwiftBindings.Apple assemblies built beside it (run `nuke binding-tests --compile-only` first).
// Pass --transition-gate-assembly to scan other assemblies (packed or shipped package DLLs); each
// assembly's own directory and the host runtime directory are searched for referenced assemblies,
// plus any --transition-gate-reference-dir.
//
// Writes swift-transition-report.json (verdict, counts, violations), swift-transition-inventory.json
// (every transition site with the rules it violates) and swift-transition-summary.txt.
//
// The ledger cross-check (build/SwiftTransitionGate/LedgerCrossCheck.cs) joins the generator's
// transition ledgers (one *.swift-transition-ledger.json per module, beside its C#) with the IL
// inventory of the compile-check assembly, and the Apple supplement's ledger with the IL of
// SwiftBindings.Apple: every Swift transition site in the IL must have exactly one ledger entry carrying
// facts, and the reverse, with the two agreeing on which forward transitions are primitive-only. It runs, fail-closed, as a step of `nuke binding-tests --compile-only`, and its
// verdict is reported by this target too (whose own verdict stays the rules above).

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

    [Parameter("Swift transition gate: a directory of *.swift-call-plans.json sidecars rule R3 reads " +
               "(default: the sidecars beside each ledgered assembly's C#; required for any other assembly with bridge twins)")]
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
                if (!Directory.Exists(full))
                    throw new Exception($"Swift transition gate: plan source directory {full} not found.");
                planSource = new TransitionPlanSource(TransitionPlanSourceKind.CallPlanSidecars, full);
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
                    PlanSource = planSource ?? (defaultInput ? DefaultPlanSource(Path.GetFileName(assembly)) : null),
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

            // The default inputs are the ledgered assemblies, in order, then the hand-written runtime.
            bool? crossCheckPassed = null;
            if (defaultInput)
            {
                crossCheckPassed = true;
                foreach (var (name, ledgerDirectory, handWritten) in LedgeredAssemblies)
                {
                    var report = reports.Single(r => Path.GetFileName(r.Path) == name);
                    var result = LedgerCrossCheck.CheckDirectory(report, ledgerDirectory, handWritten);
                    WriteLedgerCrossCheck(result, output, Path.GetFileNameWithoutExtension(name));
                    crossCheckPassed &= result.Passed;
                }
            }

            if (!reports.All(r => r.Passed))
            {
                var totals = SwiftTransitionReport.CountByRule(reports.SelectMany(r => r.Violations));
                var errors = reports.Sum(r => r.InspectionErrors.Length);
                throw new Exception(
                    "Swift transition gate: " + string.Join(", ", totals.Select(kv => $"{kv.Key}={kv.Value}")) +
                    $", inspection errors={errors}" +
                    (crossCheckPassed is null ? "" : $", ledger cross-check {(crossCheckPassed.Value ? "passed" : "FAILED")}") +
                    ". Each listed transition is one the .NET 11 interpreter can mis-place or crash on — see the lines above.");
            }
            if (crossCheckPassed == false)
                throw new Exception("Swift transition gate: the transition ledger cross-check failed — see the lines above.");
            Log.Information("Swift transition gate: PASSED");
        });

    /// <summary>
    /// The generated C# whose ledgers describe each scanned assembly: the BindingTests modules compile
    /// into the compile-check assembly, and the Apple supplement's emitted types into its own.
    /// The Apple supplement also carries hand-written types (under its Sources/) that no generator
    /// ledgers; their transitions are listed apart, and the cross-check fails if one of them no longer
    /// has any, so the list stays exact.
    /// </summary>
    (string Assembly, AbsolutePath LedgerDirectory, string[] HandWrittenTypes)[] LedgeredAssemblies => new[]
    {
        ("CompileCheck.dll", BtOutputDir, Array.Empty<string>()),
        ("SwiftBindings.Apple.dll", AppleSupplementDir / "obj" / "Debug" / AppleSupplementIosFramework() / "AppleTypes",
            new[] { "Swift.Foundation.Data", "Swift.Foundation.URL", "Swift.Foundation.URLRequest", "Swift.SwiftUI.Text" }),
    };

    /// <summary>The call-plan sidecars beside a ledgered assembly's C#, which its bridge twins are checked against.</summary>
    TransitionPlanSource? DefaultPlanSource(string assembly) =>
        LedgeredAssemblies.Where(l => l.Assembly == assembly)
            .Select(l => new TransitionPlanSource(TransitionPlanSourceKind.CallPlanSidecars, l.LedgerDirectory))
            .SingleOrDefault();

    /// <summary>
    /// The Apple supplement's iOS framework the compile check's reference to it resolves to: of the iOS
    /// frameworks its project targets, the newest the lane's framework can consume. A lane newer than
    /// the supplement (net11 against a net10-only supplement) consumes the older build, whose
    /// ledger is under that framework's obj directory.
    /// </summary>
    string AppleSupplementIosFramework()
    {
        var project = System.Xml.Linq.XDocument.Load(AppleSupplementDir / "Swift.Bindings.Apple.csproj");
        var lane = System.Version.Parse(BtFramework["net".Length..]);
        return project.Descendants("TargetFrameworks")
                   .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                   .Where(t => t.StartsWith("net", StringComparison.Ordinal) && t.EndsWith("-ios", StringComparison.Ordinal))
                   .Select(t => (Framework: t, Version: System.Version.Parse(t["net".Length..^"-ios".Length])))
                   .Where(t => t.Version <= lane)
                   .OrderByDescending(t => t.Version)
                   .Select(t => t.Framework)
                   .FirstOrDefault()
               ?? throw new Exception($"Transition ledger cross-check: Swift.Bindings.Apple.csproj targets no iOS framework a {BtFramework} compile check can consume.");
    }

    /// <summary>
    /// The compile-only step: scans the compile-check assembly and the Apple supplement built beside
    /// it, cross-checks each against the transition ledgers the generator wrote for it, and checks
    /// every bridge twin against its call plan (rule R3).
    /// Fail-closed and unconditional, with no baseline and no --permissive arm: a transition the
    /// ledger does not list, or lists without facts, is a producer that bypassed recording.
    /// </summary>
    void RunTransitionLedgerCrossCheck()
    {
        Log.Information("=========================================");
        Log.Information(" Transition ledger cross-check");
        Log.Information("=========================================");
        var output = BtOutputDir / ".swift-transition-gate";
        output.CreateDirectory();
        var failed = new List<string>();
        foreach (var (name, ledgerDirectory, handWritten) in LedgeredAssemblies)
        {
            var assembly = CompileCheckOutputDir / name;
            if (!File.Exists(assembly))
                throw new Exception($"Transition ledger cross-check: {assembly} not found; the compile check did not produce it.");
            var report = SwiftTransitionScanner.Scan(assembly, new SwiftTransitionScanOptions
            {
                ReferenceDirectories = new[]
                {
                    CompileCheckOutputDir.ToString(),
                    System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                },
                PlanSource = new TransitionPlanSource(TransitionPlanSourceKind.CallPlanSidecars, ledgerDirectory),
            });
            if (!report.InspectionErrors.IsEmpty)
                throw new Exception($"Transition ledger cross-check: {report.InspectionErrors.Length} inspection error(s) reading {assembly}: " +
                                    string.Join("; ", report.InspectionErrors.Take(5).Select(e => $"{e.Member}: {e.Message}")));
            var result = LedgerCrossCheck.CheckDirectory(report, ledgerDirectory, handWritten);
            WriteLedgerCrossCheck(result, output, Path.GetFileNameWithoutExtension(name));
            if (!result.Passed)
                failed.Add(name);

            // R3 is enforced here although the other rules are not yet: a twin is only ever emitted
            // from its plan, so the two must agree from the first twin on.
            var twins = report.Violations.Where(v => v.Rule == TransitionRule.R3).ToList();
            Log.Information("[{Assembly}] bridge twins: {Twins}, R3 violations: {Violations}",
                Path.GetFileNameWithoutExtension(name), report.Inventory.BridgeTwins, twins.Count);
            foreach (var violation in twins)
                Log.Error("  R3 {Member} -> {EntryPoint}: {Reason}", violation.Member, violation.EntryPoint, violation.Reason);
            if (twins.Count > 0)
                failed.Add($"{name} (R3)");

            // A transition the evidence made executable but the shim refused keeps its direct import,
            // so no twin exists for R3 to reject; only the generation's own sidecar records it.
            var routing = BridgeTwinPlans.Load(new TransitionPlanSource(TransitionPlanSourceKind.CallPlanSidecars, ledgerDirectory));
            foreach (var gap in routing.RoutingGaps)
                Log.Error("  routing {Gap}", gap);
            if (routing.RoutingGaps.Length > 0)
                failed.Add($"{name} (routing)");
        }
        if (failed.Count > 0)
            throw new Exception($"Transition ledger cross-check FAILED for {string.Join(", ", failed)} — see the lines above.");
    }

    static void WriteLedgerCrossCheck(LedgerCrossCheckResult result, AbsolutePath output, string assembly)
    {
        var summary = $"[{assembly}] " + LedgerCrossCheck.ToSummary(result);
        var path = output / $"swift-transition-ledger-crosscheck.{assembly}.txt";
        File.WriteAllText(path, summary);
        foreach (var line in summary.Split('\n'))
            if (line.Length > 0)
                Log.Information("{Line}", line.TrimEnd('\r'));
        Log.Information("Ledger cross-check: {Path}", path);
    }
}
