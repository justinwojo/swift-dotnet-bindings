// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using BindingsGeneration;

namespace SwiftBindings.TransitionGate;

/// <summary>A managed assembly of a package: where it is shown as coming from, its bytes, and the directories its references resolve in.</summary>
public sealed record PackagedAssembly(string DisplayPath, byte[] Image, IReadOnlyList<string> ReferenceDirectories);

/// <summary>A native image of a package: the library it is (its bare name), where it is shown as coming from, and its bytes.</summary>
public sealed record PackagedNative(string Library, string DisplayPath, byte[] Image);

/// <summary>The verdict of <see cref="PackageTransitionEvidence.Check"/>.</summary>
public sealed record PackageTransitionEvidenceResult(
    ImmutableArray<string> Problems,
    ImmutableArray<TransitionEvidence> Evidence,
    ImmutableArray<AssemblyTransitionReport> Reports)
{
    public bool Passed => Problems.IsEmpty;
}

/// <summary>
/// The transition evidence of a package, checked from the package alone: each managed assembly must
/// embed valid transition manifests, its transitions must satisfy the transition gate's rules
/// against the plans those manifests carry (not against a build tree), and every native slice must
/// hold the manifest hash and export exactly the bridges the manifests route through it.
/// </summary>
/// <remarks>
/// Policy, not baseline: there is nothing to reseed. The rules, per assembly:
/// <list type="bullet">
/// <item>the evidence is <see cref="TransitionEvidenceState.PresentValid"/>;</item>
/// <item>no R2, R3, R4 or R5 violation, with R3 read against the manifests' plans;</item>
/// <item>every R1 hazard is guarded (<see cref="ResidualGuardCheck"/>), and the hazards are exactly
/// those the manifests' guard records list when any manifest carries one;</item>
/// <item>the plans have no routing or evidence gap, and the assembly imports as many bridge twins
/// as its manifests route;</item>
/// <item>with <c>requireRoutedBridges</c>, at least one manifest routes a bridge, so a package whose
/// routing silently fell away cannot pass as one that routes nothing.</item>
/// </list>
/// </remarks>
public static class PackageTransitionEvidence
{
    /// <summary>
    /// Checks <paramref name="assemblies"/> and <paramref name="natives"/>. <paramref name="scratch"/>
    /// is a directory the manifests' plans are written into for the plan-dependent rules; it is
    /// created if missing and left for the caller to remove.
    /// </summary>
    public static PackageTransitionEvidenceResult Check(
        IReadOnlyList<PackagedAssembly> assemblies, IReadOnlyList<PackagedNative> natives, bool requireRoutedBridges, string scratch)
    {
        var problems = new List<string>();
        var evidence = ImmutableArray.CreateBuilder<TransitionEvidence>();
        var reports = ImmutableArray.CreateBuilder<AssemblyTransitionReport>();
        if (assemblies.Count == 0)
            problems.Add("the package holds no assembly to check");

        for (var index = 0; index < assemblies.Count; index++)
        {
            var assembly = assemblies[index];
            var where = assembly.DisplayPath;
            var read = SwiftTransitionManifestReader.Read(assembly.Image, where, new SwiftTransitionScanOptions { ReferenceDirectories = assembly.ReferenceDirectories });
            evidence.Add(read);
            if (read.State != TransitionEvidenceState.PresentValid)
            {
                problems.Add($"{where}: transition evidence is {read.State}");
                problems.AddRange(read.Problems.Select(p => $"{where}: {p}"));
                // The rules still read whatever manifests do read, so a mismatch also shows what it breaks.
                if (read.Manifests.IsEmpty)
                    continue;
            }

            var directory = Path.Combine(scratch, $"{index}-{Path.GetFileNameWithoutExtension(where)}");
            var (ruleProblems, report) = RuleProblems(assembly, read, directory);
            reports.Add(report);
            problems.AddRange(ruleProblems);
            var routed = read.Manifests.SelectMany(m => m.Manifest.Bridges).Distinct(StringComparer.Ordinal).Count();
            if (requireRoutedBridges && routed == 0)
                problems.Add($"{where}: routes no transition through a bridge, but this package must (its bridges are its evidence that routing reached the package)");
        }

        // Every native slice against every assembly that can load it.
        foreach (var native in natives)
            foreach (var read in evidence.Where(e => e.State == TransitionEvidenceState.PresentValid))
                problems.AddRange(SwiftTransitionManifestReader.CheckNative(read, native.Library, native.Image, native.DisplayPath)
                    .Select(p => $"{p} (against {read.Path})"));
        if (requireRoutedBridges && natives.Count == 0)
            problems.Add("the package holds no native image, but its assemblies route through bridges");
        // Every library a manifest routes through must be among the images checked, or its hash and
        // exports went unchecked.
        foreach (var library in evidence.Where(e => e.State == TransitionEvidenceState.PresentValid)
                     .SelectMany(e => e.Routing.Select(m => m.Manifest.NativeLibrary!)).Distinct(StringComparer.Ordinal)
                     .Where(l => !natives.Any(n => n.Library == l)))
            problems.Add($"the manifests route bridges through {library}, but the package holds no image of it");

        return new PackageTransitionEvidenceResult(problems.Distinct(StringComparer.Ordinal).ToImmutableArray(), evidence.ToImmutable(), reports.ToImmutable());
    }

    /// <summary>
    /// The transition gate's rules over one assembly, read against the plans and guards its own
    /// manifests carry (<paramref name="read"/>): R2–R5 (R3 against the manifests' plans), R1 through
    /// the residual-guard check, the guard-record cross-check when a manifest carries guard records,
    /// and the plans' routing and evidence gaps. <paramref name="directory"/> receives the manifests'
    /// records as sidecars; it is created if missing and left for the caller to remove.
    /// </summary>
    public static (ImmutableArray<string> Problems, AssemblyTransitionReport Report) RuleProblems(
        PackagedAssembly assembly, TransitionEvidence read, string directory)
    {
        var where = assembly.DisplayPath;
        var problems = new List<string>();
        Directory.CreateDirectory(directory);
        foreach (var manifest in read.Manifests)
        {
            var stem = manifest.Resource[..^SwiftTransitionManifestFormat.FileSuffix.Length];
            File.WriteAllText(Path.Combine(directory, stem + BridgeTwinPlans.SidecarSuffix), manifest.Manifest.Plans);
            if (manifest.Manifest.Guards is { } guards)
                File.WriteAllText(Path.Combine(directory, stem + ".swift-transition-guards.json"), guards);
        }
        var source = new TransitionPlanSource(TransitionPlanSourceKind.CallPlanSidecars, directory);
        var report = SwiftTransitionScanner.Scan(assembly.Image, where,
            new SwiftTransitionScanOptions { ReferenceDirectories = assembly.ReferenceDirectories, PlanSource = source });
        foreach (var error in report.InspectionErrors)
            problems.Add($"{where}: {error.Member} could not be inspected: {error.Message}");
        foreach (var violation in report.Violations.Where(v => v.Rule != TransitionRule.R1))
            problems.Add($"{where}: {violation.Rule} {violation.Member}{(violation.EntryPoint is { } e ? $" ({e})" : "")}: {violation.Reason}");

        var guarded = ResidualGuardCheck.Check(assembly.Image, report);
        foreach (var violation in guarded.Violations)
            problems.Add($"{where}: residual guard {violation.Rule} {violation.Member}: {violation.Reason}");
        foreach (var error in guarded.Errors)
            problems.Add($"{where}: residual guard inspection {error.Member}: {error.Message}");
        if (read.Manifests.Any(m => m.Manifest.Guards is not null))
            problems.AddRange(ResidualGuardCheck.CrossCheckSidecars(guarded, directory, Array.Empty<string>()).Select(p => $"{where}: guard record {p}"));

        var plans = BridgeTwinPlans.Load(source);
        problems.AddRange(plans.Problems.Select(p => $"{where}: plans {p}"));
        problems.AddRange(plans.RoutingGaps.Select(g => $"{where}: routing {g}"));
        problems.AddRange(plans.EvidenceGaps.Select(g => $"{where}: evidence {g}"));
        var routed = read.Manifests.SelectMany(m => m.Manifest.Bridges).Distinct(StringComparer.Ordinal).Count();
        if (report.Inventory.BridgeTwins != routed)
            problems.Add($"{where}: imports {report.Inventory.BridgeTwins} bridge twin(s), but its manifests route {routed} transition(s) through a bridge");
        return (problems.ToImmutableArray(), report);
    }
}
