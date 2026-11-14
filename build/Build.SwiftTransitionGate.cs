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
//
// The residual-guard check (build/SwiftTransitionGate/ResidualGuardCheck.cs) runs in the same step,
// over the same assemblies and the runtime: every forward Swift transition a runtime is known to
// mishandle (untyped self, an error or self argument ahead of ordinary ones, a lowered signature),
// whatever its plan says, must be reachable only through a member whose first statement refuses on
// that runtime, and the hazards the IL holds must be exactly those the generator's guard sidecars
// (*.swift-transition-guards.json, beside each module's C#) record. Also policy, not baseline.

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

    AbsolutePath CompileCheckOutputDir => CompileCheckOutputDirFor("ios");

    /// <summary>Where the compile check of output generated for <paramref name="platform"/> lands.</summary>
    AbsolutePath CompileCheckOutputDirFor(string platform) => BindingTestsDir / "CompileCheck" / "bin" / "Debug" / $"{BtFramework}-{platform}";

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
    (string Assembly, AbsolutePath LedgerDirectory, string[] HandWrittenTypes)[] LedgeredAssemblies => LedgeredAssembliesFor("ios");

    /// <summary>The ledgered assemblies of a compile check of output generated for <paramref name="platform"/>.</summary>
    (string Assembly, AbsolutePath LedgerDirectory, string[] HandWrittenTypes)[] LedgeredAssembliesFor(string platform) => new[]
    {
        ("CompileCheck.dll", BtOutputDir, Array.Empty<string>()),
        ("SwiftBindings.Apple.dll", AppleSupplementDir / "obj" / "Debug" / AppleSupplementFramework(platform) / "AppleTypes",
            new[] { "Swift.Foundation.Data", "Swift.Foundation.URL", "Swift.Foundation.URLRequest", "Swift.SwiftUI.Text" }),
    };

    /// <summary>
    /// The hand-written types of each ledgered assembly that still hold a known-unsafe direct
    /// transition, and so guard it by hand: the residual-guard check holds them to the same rules as
    /// generated code, and fails when one no longer holds any, so the list stays exact. None does
    /// today: Data, the last, builds its value through the supplement's C shim.
    /// </summary>
    static string[] HandWrittenResidualTypes(string assembly) => Array.Empty<string>();

    /// <summary>
    /// The call-plan sidecars a scanned assembly's bridge twins are checked against: those beside a
    /// ledgered assembly's C#, and for the runtime the plan products of its hand-written table, from
    /// which its twins (SwiftTransitions.g.cs) are written.
    /// </summary>
    TransitionPlanSource? DefaultPlanSource(string assembly) =>
        assembly == RuntimeAssemblyName
            ? new TransitionPlanSource(TransitionPlanSourceKind.CallPlanSidecars, SwiftBridgeProductsDir)
            : LedgeredAssemblies.Where(l => l.Assembly == assembly)
                .Select(l => new TransitionPlanSource(TransitionPlanSourceKind.CallPlanSidecars, l.LedgerDirectory))
                .SingleOrDefault();

    const string RuntimeAssemblyName = "Swift.Runtime.dll";

    /// <summary>
    /// The Apple supplement's framework for <paramref name="platform"/> the compile check's reference to
    /// it resolves to: of the frameworks its project targets for that platform, the newest the lane's
    /// framework can consume. A lane newer than the supplement (net11 against a net10-only supplement)
    /// consumes the older build, whose ledger is under that framework's obj directory.
    /// </summary>
    string AppleSupplementFramework(string platform)
    {
        var project = System.Xml.Linq.XDocument.Load(AppleSupplementDir / "Swift.Bindings.Apple.csproj");
        var lane = System.Version.Parse(BtFramework["net".Length..]);
        var suffix = "-" + platform;
        return project.Descendants("TargetFrameworks")
                   .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                   .Where(t => t.StartsWith("net", StringComparison.Ordinal) && t.EndsWith(suffix, StringComparison.Ordinal))
                   .Select(t => (Framework: t, Version: System.Version.Parse(t["net".Length..^suffix.Length])))
                   .Where(t => t.Version <= lane)
                   .OrderByDescending(t => t.Version)
                   .Select(t => t.Framework)
                   .FirstOrDefault()
               ?? throw new Exception($"Transition ledger cross-check: Swift.Bindings.Apple.csproj targets no {platform} framework a {BtFramework} compile check can consume.");
    }

    /// <summary>
    /// The compile-only step: scans the compile-check assembly and the Apple supplement built beside
    /// it, cross-checks each against the transition ledgers the generator wrote for it, and checks
    /// every bridge twin against its call plan (rule R3).
    /// Fail-closed and unconditional, with no baseline and no --permissive arm: a transition the
    /// ledger does not list, or lists without facts, is a producer that bypassed recording.
    /// The Mac Catalyst and tvOS legs run it too, each over a compile check of its own output
    /// (<paramref name="platform"/> <c>maccatalyst</c> or <c>tvos</c>); on every routed platform a control first
    /// proves the run is not vacuous: the bindings were judged against that platform's evidence, a
    /// guard sidecar exists for every module, and the compiled binding holds bridge twins.
    /// </summary>
    void RunTransitionLedgerCrossCheck(string platform = "ios")
    {
        Log.Information("=========================================");
        Log.Information(" Transition ledger cross-check ({Platform})", platform);
        Log.Information("=========================================");
        var output = BtOutputDir / ".swift-transition-gate";
        output.CreateDirectory();
        var failed = new List<string>();
        var compileCheckOutput = CompileCheckOutputDirFor(platform);
        var routed = SwiftBridgeRoutedPlatforms.Contains(platform, StringComparer.Ordinal);
        if (routed)
            failed.AddRange(CheckRoutedEvidenceControl(platform));
        foreach (var (name, ledgerDirectory, handWritten) in LedgeredAssembliesFor(platform))
        {
            var assembly = compileCheckOutput / name;
            if (!File.Exists(assembly))
                throw new Exception($"Transition ledger cross-check: {assembly} not found; the compile check did not produce it.");
            var report = SwiftTransitionScanner.Scan(assembly, new SwiftTransitionScanOptions
            {
                ReferenceDirectories = new[]
                {
                    compileCheckOutput.ToString(),
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

            if (!CheckResidualGuards(assembly, report, ledgerDirectory, HandWrittenResidualTypes(name), output))
                failed.Add($"{name} (residual guards)");

            // R3 is enforced here although the other rules are not yet: a twin is only ever emitted
            // from its plan, so the two must agree from the first twin on.
            var twins = report.Violations.Where(v => v.Rule == TransitionRule.R3).ToList();
            Log.Information("[{Assembly}] bridge twins: {Twins}, R3 violations: {Violations}",
                Path.GetFileNameWithoutExtension(name), report.Inventory.BridgeTwins, twins.Count);
            foreach (var violation in twins)
                Log.Error("  R3 {Member} -> {EntryPoint}: {Reason}", violation.Member, violation.EntryPoint, violation.Reason);
            if (twins.Count > 0)
                failed.Add($"{name} (R3)");
            if (routed && name == "CompileCheck.dll")
                failed.AddRange(CheckRoutedAssemblyControl(platform, assembly, report));

            // A transition the evidence made executable but the shim refused keeps its direct import,
            // so no twin exists for R3 to reject; only the generation's own sidecar records it.
            var routing = BridgeTwinPlans.Load(new TransitionPlanSource(TransitionPlanSourceKind.CallPlanSidecars, ledgerDirectory));
            foreach (var gap in routing.RoutingGaps)
                Log.Error("  routing {Gap}", gap);
            if (routing.RoutingGaps.Length > 0)
                failed.Add($"{name} (routing)");

            // A forward plan the evidence cannot judge executable stays a direct Swift-convention
            // import, and nothing above sees it: no twin exists and the coverage agrees with itself.
            foreach (var gap in routing.EvidenceGaps)
                Log.Error("  evidence {Gap}", gap);
            if (routing.EvidenceGaps.Length > 0)
            {
                Log.Error("  the lowering evidence is stale or contradicts these plans: rerun `swift-lowering-oracle` under each " +
                          "toolchain profile after a compile-only regeneration, or correct the plan");
                failed.Add($"{name} (evidence)");
            }
        }
        failed.AddRange(CheckRuntimeBridgeTwins(compileCheckOutput));
        if (failed.Count > 0)
            throw new Exception($"Transition ledger cross-check ({platform}) FAILED for {string.Join(", ", failed)} — see the lines above.");
    }

    /// <summary>
    /// The control that keeps the cross-check of a routed platform's output from passing vacuously,
    /// over the generation: the output was generated for <paramref name="platform"/>, and each
    /// module's call plans were judged by the qualification record over that platform's own slices
    /// (every slice the platform's lanes run, and none of another platform). The compiled side of the
    /// control is in <see cref="CheckRoutedAssemblyControl"/>. A module that guards nothing writes no
    /// guard sidecar, so none is required here: a hazard the IL holds without one fails the
    /// residual-guard check itself.
    /// </summary>
    IEnumerable<string> CheckRoutedEvidenceControl(string platform)
    {
        var stamp = BtOutputDir / TargetPlatformSidecarName;
        var generatedFor = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : null;
        if (generatedFor != platform)
        {
            Log.Error("  control: {Output} was generated for {Generated}, not {Platform}", BtOutputDir, generatedFor ?? "(unknown)", platform);
            yield return $"control (output generated for {generatedFor ?? "an unknown platform"})";
            yield break;
        }
        var laneSlices = RoutedLaneSlices(platform);
        foreach (var module in new[] { ModuleName, DepModuleName })
        {
            var plans = BtOutputDir / $"{module}.swift-call-plans.json";
            if (!File.Exists(plans))
            {
                Log.Error("  control: {Module} has no call-plan sidecar at {Path}", module, plans);
                yield return $"control ({module}: no call plans)";
                continue;
            }
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(plans));
            if (!document.RootElement.TryGetProperty("evidenceScope", out var scope) || scope.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                Log.Error("  control: {Module}'s call plans were judged against no evidence", module);
                yield return $"control ({module}: no evidence scope)";
                continue;
            }
            var basis = scope.GetProperty("basis").GetString();
            var artifact = scope.GetProperty("artifact").GetString();
            var slices = scope.GetProperty("slices").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
            var missing = laneSlices.Except(slices, StringComparer.Ordinal).ToArray();
            var foreign = slices.Where(s => !IsSliceOf(platform, s)).ToArray();
            if (basis != "qualification" || artifact != module || missing.Length > 0 || foreign.Length > 0)
            {
                Log.Error("  control: {Module}'s call plans were judged by {Basis} for {Artifact} over [{Slices}]; expected the qualification " +
                          "record for {Module} over the {Platform} slices (missing [{Missing}], foreign [{Foreign}])",
                    module, basis, artifact, string.Join(", ", slices), module, platform, string.Join(", ", missing), string.Join(", ", foreign));
                yield return $"control ({module}: evidence scope)";
            }
            else
                Log.Information("  control: {Module} judged by the qualification record over [{Slices}]", module, string.Join(", ", slices));
        }
    }

    /// <summary>
    /// The compiled side of the control: the scanned binding is the build for <paramref name="platform"/>
    /// (its target-platform attribute, not a stale build of another platform), the scan saw its Swift
    /// transitions, and routing reached it (it compiles bridge twins). Then the residual-guard check
    /// over it judged the platform's own transitions, whether or not any of them is still a hazard.
    /// </summary>
    static IEnumerable<string> CheckRoutedAssemblyControl(string platform, AbsolutePath assembly, AssemblyTransitionReport report)
    {
        var target = TargetPlatformOf(assembly);
        if (target is null || !target.StartsWith(platform, StringComparison.OrdinalIgnoreCase)
            || !char.IsDigit(target.ElementAtOrDefault(platform.Length)))
        {
            Log.Error("  control: {Assembly} targets {Target}, not {Platform}", assembly, target ?? "no platform", platform);
            yield return $"control ({Path.GetFileName(assembly)}: built for {target ?? "no platform"})";
        }
        if (report.Inventory.SwiftPInvokes == 0)
        {
            Log.Error("  control: the scan of {Assembly} saw no Swift-convention import", assembly);
            yield return $"control ({Path.GetFileName(assembly)}: no Swift transitions)";
        }
        if (report.Inventory.BridgeTwins == 0)
        {
            Log.Error("  control: the {Platform} binding compiles no bridge twin: routing did not reach it", platform);
            yield return $"control ({Path.GetFileName(assembly)}: no bridge twins)";
        }
        Log.Information("  control: {Assembly} targets {Target}: {Imports} Swift-convention import(s), {Twins} bridge twin(s)",
            Path.GetFileName(assembly), target, report.Inventory.SwiftPInvokes, report.Inventory.BridgeTwins);
    }

    /// <summary>The platform an assembly's <c>TargetPlatformAttribute</c> names (for example <c>maccatalyst15.0</c>), or null.</summary>
    static string? TargetPlatformOf(AbsolutePath assembly)
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(assembly));
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != System.Reflection.Metadata.HandleKind.MemberReference)
                continue;
            var constructor = metadata.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference)
                continue;
            var type = metadata.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)constructor.Parent);
            if (metadata.GetString(type.Namespace) != "System.Runtime.Versioning" || metadata.GetString(type.Name) != "TargetPlatformAttribute")
                continue;
            var blob = metadata.GetBlobReader(attribute.Value);
            return blob.ReadUInt16() == 1 ? blob.ReadSerializedString() : null;
        }
        return null;
    }

    /// <summary>The target triples the repo's own lanes build for <paramref name="platform"/>: each must be in its evidence scope.</summary>
    static string[] RoutedLaneSlices(string platform) => platform switch
    {
        "ios" => new[] { ApplePlatform.IOS.DeviceTarget!, ApplePlatform.IOS.SimulatorTarget },
        "maccatalyst" => new[] { ApplePlatform.MacCatalyst.SimulatorTarget, ApplePlatform.MacCatalystX64.SimulatorTarget },
        "tvos" => new[] { ApplePlatform.TvOS.DeviceTarget!, ApplePlatform.TvOS.SimulatorTarget },
        _ => throw new ArgumentException($"{platform} is not a routed platform", nameof(platform)),
    };

    /// <summary>Whether <paramref name="triple"/> is a slice of <paramref name="platform"/> (and not of, say, the platform it shares an SDK or an OS name with).</summary>
    static bool IsSliceOf(string platform, string triple) => platform switch
    {
        "ios" => System.Text.RegularExpressions.Regex.IsMatch(triple, @"^(arm64|x86_64)-apple-ios[0-9.]+(-simulator)?$"),
        "maccatalyst" => System.Text.RegularExpressions.Regex.IsMatch(triple, @"^(arm64|x86_64)-apple-ios[0-9.]+-macabi$"),
        "tvos" => System.Text.RegularExpressions.Regex.IsMatch(triple, @"^(arm64|x86_64)-apple-tvos[0-9.]+(-simulator)?$"),
        _ => false,
    };

    /// <summary>
    /// R3 over the runtime's own bridge twins: each SBSR_ twin in the compiled Swift.Runtime must be
    /// the C-convention, primitive-only import its plan product states, and there must be one for every
    /// transition the product routes through a bridge, so a runtime compiled from stale shims, or from
    /// the declarations alone, fails here rather than as a missing entry point in an app.
    /// </summary>
    IEnumerable<string> CheckRuntimeBridgeTwins(AbsolutePath compileCheckOutput)
    {
        var assembly = compileCheckOutput / RuntimeAssemblyName;
        if (!File.Exists(assembly))
            throw new Exception($"Transition ledger cross-check: {assembly} not found; the compile check did not produce it.");
        var source = new TransitionPlanSource(TransitionPlanSourceKind.CallPlanSidecars, SwiftBridgeProductsDir);
        var report = SwiftTransitionScanner.Scan(assembly, new SwiftTransitionScanOptions
        {
            ReferenceDirectories = new[]
            {
                compileCheckOutput.ToString(),
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            },
            PlanSource = source,
        });
        if (!report.InspectionErrors.IsEmpty)
            throw new Exception($"Transition ledger cross-check: {report.InspectionErrors.Length} inspection error(s) reading {assembly}: " +
                                string.Join("; ", report.InspectionErrors.Take(5).Select(e => $"{e.Member}: {e.Message}")));
        if (!CheckResidualGuards(assembly, report, ledgerDirectory: null, Array.Empty<string>(), BtOutputDir / ".swift-transition-gate"))
            yield return "Swift.Runtime.dll (residual guards)";
        var twins = report.Violations.Where(v => v.Rule == TransitionRule.R3).ToList();
        using var product = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SwiftBridgeProductsDir / "Swift.Runtime.swift-call-plans.json"));
        var routed = product.RootElement.GetProperty("coverage").EnumerateArray()
            .Count(e => e.TryGetProperty("bridged", out var bridged) && bridged.ValueKind == System.Text.Json.JsonValueKind.True);
        Log.Information("[Swift.Runtime] bridge twins: {Twins} (routed by its plan product: {Routed}), R3 violations: {Violations}",
            report.Inventory.BridgeTwins, routed, twins.Count);
        foreach (var violation in twins)
            Log.Error("  R3 {Member} -> {EntryPoint}: {Reason}", violation.Member, violation.EntryPoint, violation.Reason);
        if (twins.Count > 0)
            yield return "Swift.Runtime.dll (R3)";
        if (routed == 0 || report.Inventory.BridgeTwins != routed)
        {
            Log.Error("  the runtime compiles {Twins} bridge twin(s), but its plan product routes {Routed} transition(s) through a bridge: " +
                      "rerun `nuke swift-bridge-products` and rebuild", report.Inventory.BridgeTwins, routed);
            yield return "Swift.Runtime.dll (twins)";
        }
        var plans = BridgeTwinPlans.Load(source);
        foreach (var gap in plans.RoutingGaps)
        {
            Log.Error("  routing {Gap}", gap);
            yield return $"Swift.Runtime.dll (routing: {gap})";
        }
        foreach (var gap in plans.EvidenceGaps)
        {
            Log.Error("  evidence {Gap}", gap);
            yield return $"Swift.Runtime.dll (evidence: {gap})";
        }
    }

    /// <summary>
    /// The residual-guard check over one compiled assembly: every known-unsafe direct Swift transition
    /// it still holds, whatever its plan says (planned, refused or never planned), is reachable only
    /// through a member that refuses first on the runtimes that mishandle it. Against the generator's
    /// guard sidecars beside <paramref name="ledgerDirectory"/>, the hazards the IL holds are exactly
    /// the ones the generator recorded and guarded. Fail-closed, with no baseline and no
    /// --permissive arm: an unguarded hazard is a call that corrupts registers or crashes on a
    /// shipping runtime.
    /// </summary>
    static bool CheckResidualGuards(AbsolutePath assembly, AssemblyTransitionReport report, AbsolutePath? ledgerDirectory,
        IReadOnlyCollection<string> handWritten, AbsolutePath output)
    {
        var name = Path.GetFileNameWithoutExtension(assembly);
        var result = ResidualGuardCheck.Check(assembly, report);
        var problems = ledgerDirectory is null
            ? System.Collections.Immutable.ImmutableArray<string>.Empty
            : ResidualGuardCheck.CrossCheckSidecars(result, ledgerDirectory, handWritten);
        var summary = $"[{name}] " + ResidualGuardCheck.ToSummary(result) +
                      string.Concat(problems.Select(p => $"  sidecar {p}\n"));
        var path = output / $"swift-transition-residual-guards.{name}.txt";
        File.WriteAllText(path, summary);
        var passed = result.Passed && problems.IsEmpty;
        Log.Information("[{Assembly}] residual guards: {Verdict} ({Hazards} hazard(s), {Holders} holder(s), {Guards} guard call(s)); {Path}",
            name, passed ? "passed" : "FAILED", result.Hazards.Length, result.Holders.Length, result.GuardCalls, path);
        foreach (var violation in result.Violations)
            Log.Error("  {Rule} {Member}: {Reason}", violation.Rule, violation.Member, violation.Reason);
        foreach (var error in result.Errors)
            Log.Error("  inspection error {Member}: {Message}", error.Member, error.Message);
        foreach (var problem in problems)
            Log.Error("  sidecar {Problem}", problem);
        return passed;
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
