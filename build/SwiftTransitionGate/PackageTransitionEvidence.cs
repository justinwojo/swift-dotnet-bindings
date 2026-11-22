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

/// <summary>
/// A managed assembly of a package: where it is shown as coming from, its bytes, and the directories its
/// references resolve in. <paramref name="CopyTfm"/> is the package copy it belongs to (its
/// <c>lib/&lt;tfm&gt;/</c> folder) in a package of per-toolchain copies; null otherwise.
/// </summary>
public sealed record PackagedAssembly(string DisplayPath, byte[] Image, IReadOnlyList<string> ReferenceDirectories, string? CopyTfm = null);

/// <summary>
/// A native image of a package: the library it is (its bare name), where it is shown as coming from,
/// and its bytes. <paramref name="CopyTfm"/> is the copy whose <c>native/&lt;tfm&gt;/</c> holds it; null
/// for an image every assembly of the package can load.
/// </summary>
public sealed record PackagedNative(string Library, string DisplayPath, byte[] Image, string? CopyTfm = null)
{
    /// <summary>
    /// Whether an app that loads <paramref name="assembly"/> links this image: a copy's assembly links
    /// only its copy's natives, and a shared image serves only assemblies outside every copy.
    /// </summary>
    public bool Serves(PackagedAssembly assembly) =>
        CopyTfm is null ? assembly.CopyTfm is null : string.Equals(CopyTfm, assembly.CopyTfm, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The verdict of <see cref="PackageTransitionEvidence.Check"/>.</summary>
public sealed record PackageTransitionEvidenceResult(
    ImmutableArray<string> Problems,
    ImmutableArray<TransitionEvidence> Evidence,
    ImmutableArray<AssemblyTransitionReport> Reports)
{
    public bool Passed => Problems.IsEmpty;
}

/// <summary>
/// The transition gate's rules over one assembly (<see cref="PackageTransitionEvidence.RuleProblems"/>):
/// what fails, the scan they were read from, and the recorded bridge withdrawals that were accepted.
/// </summary>
public sealed record TransitionRuleOutcome(ImmutableArray<string> Problems, AssemblyTransitionReport Report, ImmutableArray<TransitionWithdrawal> Withdrawals);

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
/// <item>no R2, R3, R4 or R5 violation, with R3 read against the manifests' plans, except an R4
/// Swift-convention callback every member handing it to Swift guards
/// (<see cref="ResidualGuardCheck.AcceptedReverseCallbacks"/>);</item>
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
    /// <remarks>
    /// Strict by default: a routing gap fails whether or not a recorded withdrawal explains it. With
    /// <paramref name="acceptWithdrawals"/> the gaps a withdrawal explains are accepted on the terms
    /// <see cref="RuleProblems"/> states, and every withdrawal's library must be among
    /// <paramref name="natives"/>, so its images are shown to carry no bridge and no manifest hash.
    /// </remarks>
    public static PackageTransitionEvidenceResult Check(
        IReadOnlyList<PackagedAssembly> assemblies, IReadOnlyList<PackagedNative> natives, bool requireRoutedBridges, string scratch,
        bool acceptWithdrawals = false)
    {
        var withdrawn = new List<(PackagedAssembly Assembly, string Library)>();
        var checkedEvidence = new List<(PackagedAssembly Assembly, TransitionEvidence Read)>();
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
            checkedEvidence.Add((assembly, read));
            if (read.State != TransitionEvidenceState.PresentValid)
            {
                problems.Add($"{where}: transition evidence is {read.State}");
                problems.AddRange(read.Problems.Select(p => $"{where}: {p}"));
                // The rules still read whatever manifests do read, so a mismatch also shows what it breaks.
                if (read.Manifests.IsEmpty)
                    continue;
            }

            var directory = Path.Combine(scratch, $"{index}-{Path.GetFileNameWithoutExtension(where)}");
            var (ruleProblems, report, withdrawals) = RuleProblems(assembly, read, directory, acceptWithdrawals);
            reports.Add(report);
            problems.AddRange(ruleProblems);
            withdrawn.AddRange(withdrawals.Select(w => (assembly, w.Library)));
            var routed = read.Manifests.SelectMany(m => m.Manifest.Bridges).Distinct(StringComparer.Ordinal).Count();
            if (requireRoutedBridges && routed == 0)
                problems.Add($"{where}: routes no transition through a bridge, but this package must (its bridges are its evidence that routing reached the package)");
        }

        // Every native slice against every assembly that can load it: a copy's natives against that
        // copy's assemblies only, an unscoped image against all.
        foreach (var native in natives)
            foreach (var (assembly, read) in checkedEvidence.Where(e => e.Read.State == TransitionEvidenceState.PresentValid && native.Serves(e.Assembly)))
                problems.AddRange(SwiftTransitionManifestReader.CheckNative(read, native.Library, native.Image, native.DisplayPath)
                    .Select(p => $"{p} (against {read.Path})"));
        if (requireRoutedBridges && natives.Count == 0)
            problems.Add("the package holds no native image, but its assemblies route through bridges");
        // Every library a manifest routes through must be among the images its assembly loads, or its
        // hash and exports went unchecked.
        foreach (var (assembly, read) in checkedEvidence.Where(e => e.Read.State == TransitionEvidenceState.PresentValid))
            foreach (var library in read.Routing.Select(m => m.Manifest.NativeLibrary!).Distinct(StringComparer.Ordinal)
                         .Where(l => !natives.Any(n => n.Library == l && n.Serves(assembly))))
                problems.Add(assembly.CopyTfm is null
                    ? $"the manifests route bridges through {library}, but the package holds no image of it"
                    : $"the manifests route bridges through {library}, but the package holds no image of it in native/{assembly.CopyTfm}/ (against {read.Path})");
        foreach (var (assembly, library) in withdrawn.Distinct().Where(w => !natives.Any(n => n.Library == w.Library && n.Serves(w.Assembly))))
            problems.Add($"a module's bridges through {library} were withdrawn, but the package holds no image of {library} to show they are gone");

        return new PackageTransitionEvidenceResult(problems.Distinct(StringComparer.Ordinal).ToImmutableArray(), evidence.ToImmutable(), reports.ToImmutable());
    }

    /// <summary>
    /// The transition gate's rules over one assembly, read against the plans and guards its own
    /// manifests carry (<paramref name="read"/>): R2–R5 (R3 against the manifests' plans), R1 and
    /// guarded R4 callbacks through the residual-guard check, the guard-record cross-check when a manifest carries guard records,
    /// and the plans' routing and evidence gaps. <paramref name="directory"/> receives the manifests'
    /// records as sidecars; it is created if missing and left for the caller to remove.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every routing gap fails unless <paramref name="acceptWithdrawals"/>. Then a gap a module's
    /// recorded bridge withdrawal explains (<see cref="BridgeTwinPlans.UnrecordedRoutingGaps"/>) is
    /// accepted only when the withdrawal holds up: it reads and agrees with its own coverage
    /// (<see cref="BridgeTwinPlans.WithdrawalProblems"/>, which also refuses a withdrawal whose
    /// relinked wrapper contradicted the receipt), the module's manifest routes nothing, and each
    /// transition it lists is a Swift-convention import of the compiled assembly with the member,
    /// entry point and shapes it records. Nothing else is relaxed: the residual-guard check still
    /// requires an early guard on each restored import, and the native images must still carry no
    /// bridge or hash of the withdrawn routing (<see cref="SwiftTransitionManifestReader.CheckNative"/>).
    /// </para>
    /// </remarks>
    public static TransitionRuleOutcome RuleProblems(
        PackagedAssembly assembly, TransitionEvidence read, string directory, bool acceptWithdrawals = false)
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

        var guarded = ResidualGuardCheck.Check(assembly.Image, report);
        // R1 is judged by the residual-guard check below, and so is an R4 Swift-convention callback
        // that every member handing it to Swift guards (ResidualGuardCheck.AcceptedReverseCallbacks).
        var acceptedCallbacks = ResidualGuardCheck.AcceptedReverseCallbacks(report, guarded);
        foreach (var violation in report.Violations.Where(v => v.Rule != TransitionRule.R1 && !ResidualGuardCheck.IsAcceptedReverseCallback(v, acceptedCallbacks)))
            problems.Add($"{where}: {violation.Rule} {violation.Member}{(violation.EntryPoint is { } e ? $" ({e})" : "")}: {violation.Reason}");

        foreach (var violation in guarded.Violations)
            problems.Add($"{where}: residual guard {violation.Rule} {violation.Member}: {violation.Reason}");
        foreach (var error in guarded.Errors)
            problems.Add($"{where}: residual guard inspection {error.Member}: {error.Message}");
        if (read.Manifests.Any(m => m.Manifest.Guards is not null))
            problems.AddRange(ResidualGuardCheck.CrossCheckSidecars(guarded, directory, Array.Empty<string>()).Select(p => $"{where}: guard record {p}"));

        var plans = BridgeTwinPlans.Load(source);
        problems.AddRange(plans.Problems.Select(p => $"{where}: plans {p}"));
        var withdrawals = ImmutableArray<TransitionWithdrawal>.Empty;
        if (!acceptWithdrawals)
            problems.AddRange(plans.RoutingGaps.Select(g => $"{where}: routing {g}"));
        else
        {
            problems.AddRange(plans.UnrecordedRoutingGaps.Select(g => $"{where}: routing {g}"));
            problems.AddRange(plans.WithdrawalProblems.Select(p => $"{where}: withdrawal {p}"));
            var restoration = plans.Withdrawals.SelectMany(w => RestorationProblems(w, read, report)).ToList();
            problems.AddRange(restoration.Select(p => $"{where}: withdrawal {p}"));
            if (plans.WithdrawalProblems.IsEmpty && restoration.Count == 0)
                withdrawals = plans.Withdrawals;
        }
        problems.AddRange(plans.EvidenceGaps.Select(g => $"{where}: evidence {g}"));
        var routed = read.Manifests.SelectMany(m => m.Manifest.Bridges).Distinct(StringComparer.Ordinal).Count();
        if (report.Inventory.BridgeTwins != routed)
            problems.Add($"{where}: imports {report.Inventory.BridgeTwins} bridge twin(s), but its manifests route {routed} transition(s) through a bridge");
        return new TransitionRuleOutcome(problems.ToImmutableArray(), report, withdrawals);
    }

    /// <summary>
    /// Where the compiled assembly does not show what <paramref name="withdrawal"/> says was restored:
    /// the module's manifest still routes a bridge, or a listed transition is not a Swift-convention
    /// import of the assembly from the recorded library, with the recorded member, entry point and shapes.
    /// </summary>
    private static IEnumerable<string> RestorationProblems(TransitionWithdrawal withdrawal, TransitionEvidence read, AssemblyTransitionReport report)
    {
        var manifest = read.Manifests.FirstOrDefault(m =>
            m.Resource[..^SwiftTransitionManifestFormat.FileSuffix.Length] + BridgeTwinPlans.SidecarSuffix == withdrawal.Origin);
        if (manifest is null)
            yield return $"{withdrawal.Origin}: no manifest of the assembly carries the plans that record the withdrawal";
        else if (manifest.Manifest.NativeSymbol is not null || !manifest.Manifest.Bridges.IsEmpty)
            yield return $"{manifest.Resource} records that {withdrawal.Module}'s bridges were withdrawn but still routes "
                         + $"[{string.Join(", ", manifest.Manifest.Bridges)}] through {manifest.Manifest.NativeLibrary} (a partial restoration)";
        foreach (var transition in withdrawal.Transitions)
        {
            var sites = report.Sites.Where(s => s.Kind == TransitionSiteKind.PInvoke && s.Member == transition.Member && s.EntryPoint == transition.EntryPoint).ToList();
            if (sites.Count == 0)
            {
                yield return $"{transition.Member} ({transition.EntryPoint}) is listed as restored to its direct import, but the assembly declares no such import";
                continue;
            }
            foreach (var site in sites)
            {
                if (site.Convention != TransitionConvention.Swift)
                    yield return $"{transition.Member} ({transition.EntryPoint}) is listed as restored to its Swift-convention import, but the assembly imports it with the {site.Convention} convention";
                else if (SwiftTransitionEngines.ShapesOf(transition.Shapes) is { } recorded && recorded != site.Shapes)
                    yield return $"{transition.Member} ({transition.EntryPoint}) is recorded with the shapes [{string.Join(", ", transition.Shapes)}], "
                                 + $"but the compiled import has [{string.Join(", ", SwiftTransitionEngines.ShapeNames(site.Shapes))}]";
            }
        }
    }
}
