// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;

using BindingsGeneration;
using BindingsGeneration.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BindingsGeneration.Tests;

/// <summary>
/// Covers the always-written failure-report projection: a nonconvergent run leaves a
/// <c>binding-failure-report.json</c> whose frozen fields are populated from the terminal round's
/// evidence; a converged run leaves none. These assert the projected evidence semantically (fields
/// present, IDs stable, the final round's attribution — not an earlier one), never a whole-JSON string
/// match, so the report can evolve additively without churning the tests.
/// </summary>
public class BindingFailureReportTests
{
    // ---- unit + attribution builders (mirror WrapperRecoveryControllerTests' fakes) ----------

    private static RecoveryUnitId Leaf(string symbol) =>
        RecoveryUnitId.Create(AttributionFixtures.DeclForSymbol(symbol), RecoveryScope.LeafApi);

    private static RecoveryUnitId Coarse(string symbol, RecoveryScope scope) =>
        RecoveryUnitId.Create(AttributionFixtures.DeclForSymbol(symbol), scope);

    private static DiagnosticGroup ErrorGroupIn(string file, string message) =>
        new()
        {
            Primary = new CompilerDiagnostic
            {
                File = file,
                Line = 7,
                Column = 3,
                Severity = DiagnosticSeverity.Error,
                Message = message,
            },
        };

    private static DiagnosticGroup ErrorGroup(string message) => ErrorGroupIn("Wrapper.swift", message);

    private static AttributionResult AttributedFailure(params RecoveryUnitId[] culprits) =>
        AttributedFailureWith(ProvenanceSource.SymbolAnchor, "Wrapper.swift", culprits);

    // A terminal attribution whose diagnostics carry a chosen provenance and source file, so a test can
    // exercise the confidence ladder (via provenance) and the failing plane/stage (via the file extension).
    private static AttributionResult AttributedFailureWith(
        ProvenanceSource source, string file, params RecoveryUnitId[] culprits)
    {
        var groups = culprits.Select(u => ErrorGroupIn(file, $"cannot bind {u.Canonical}")).ToList();
        var diagnostics = culprits
            .Select((u, i) => new AttributedDiagnostic
            {
                Diagnostic = groups[i],
                Kind = AttributionKind.Unit,
                Artifact = ArtifactId.Create(u.Decl, ArtifactRole.SwiftWrapper),
                Unit = u,
                Source = source,
            })
            .ToImmutableArray();

        return new AttributionResult
        {
            Diagnostics = diagnostics,
            Culprits = culprits.Distinct().ToImmutableArray(),
            Fingerprint = DiagnosticFingerprint.Compute(groups),
        };
    }

    private sealed class ScriptedDriver : IWrapperRecoveryDriver
    {
        private readonly Queue<AttributionResult?> _rounds;
        public ScriptedDriver(params AttributionResult?[] rounds) => _rounds = new(rounds);

        public AttributionResult? RenderCompileAttribute(IReadOnlySet<RecoveryUnitId> denylist) =>
            _rounds.Count == 0
                ? throw new InvalidOperationException("driver ran out of scripted rounds")
                : _rounds.Dequeue();
    }

    private sealed class PolicyDriver : IWrapperRecoveryDriver
    {
        private readonly Func<IReadOnlySet<RecoveryUnitId>, AttributionResult?> _policy;
        public PolicyDriver(Func<IReadOnlySet<RecoveryUnitId>, AttributionResult?> policy) => _policy = policy;

        public AttributionResult? RenderCompileAttribute(IReadOnlySet<RecoveryUnitId> denylist) => _policy(denylist);
    }

    private static readonly BindingFailureInputPaths NoInputs = new(null, null, null, null);

    private static IReadOnlySet<RecoveryUnitId> Seed(params RecoveryUnitId[] units) =>
        new HashSet<RecoveryUnitId>(units);

    private static string FreshOutputDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bfr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- (1) nonconvergent module → report with the frozen fields populated ------------------

    [Fact]
    public void NonconvergentModule_ProducesReportWithFrozenFieldsPopulated()
    {
        var coarse = Coarse("sharedHelper", RecoveryScope.SharedHelperBundle);
        var recovery = WrapperRecoveryController.Run(new ScriptedDriver(AttributedFailure(coarse)));
        Assert.False(recovery.Converged); // precondition: this is a terminal failure

        var seed = Seed(Leaf("seedB"), Leaf("seedA"));
        var report = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, recovery, seed, FreshOutputDir());

        // Identity + schema.
        Assert.Equal(BindingFailureReport.CurrentSchemaVersion, report.SchemaVersion);
        Assert.Equal("MyModule", report.Module);
        // With no input files present the fingerprint still hashes the generator version → a real digest.
        Assert.Matches("^[0-9a-f]{64}$", report.Input.Fingerprint);

        // Terminal outcome.
        Assert.Equal(BindingFailureOutcomeKind.RecoveryNonConvergence, report.Outcome.Kind);
        Assert.Equal("SWIFTBIND111", report.Outcome.ReasonCode);
        Assert.Equal(WrapperRecoveryFailureCause.RequiresGraphClosure, report.Outcome.RecoveryCause);
        Assert.Equal(recovery.Rounds, report.Outcome.RecoveryRounds);
        Assert.True(report.Outcome.RecoveryRounds >= 1);

        // Diagnostics: first-class evidence, planed and fingerprinted.
        Assert.NotEmpty(report.Diagnostics);
        var diag = report.Diagnostics[0];
        Assert.Equal(DiagnosticPlane.SwiftCompiler, diag.Plane); // Wrapper.swift → Swift plane
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.False(string.IsNullOrWhiteSpace(diag.Message));
        Assert.Matches("^[0-9a-fA-F]{8}$", diag.Fingerprint);

        // Attributed units carry the stable ids and reference back into Diagnostics by index.
        var unit = Assert.Single(report.AttributedUnits);
        Assert.Equal(coarse.Canonical, unit.UnitId);
        Assert.Equal(coarse.Decl.Canonical, unit.DeclId);
        Assert.Equal(RecoveryScope.SharedHelperBundle, unit.Scope);
        Assert.NotEmpty(unit.DiagnosticRefs);
        Assert.All(unit.DiagnosticRefs, i => Assert.InRange(i, 0, report.Diagnostics.Count - 1));

        // Recovery decision: the obstruction, blocker, escalation boundary, and authorization verdict.
        var decision = report.RecoveryDecision;
        Assert.NotNull(decision);
        Assert.Equal("RequiresGraphClosure", decision!.ObstructionCode);
        Assert.Contains(coarse.Canonical, decision.BlockerUnitIds);
        Assert.Equal(coarse.Canonical, decision.EscalationUnitId);
        Assert.Equal(CoarseWithdrawalOutcome.Unauthorized, decision.AuthorizationOutcome);
        Assert.Contains(coarse.Canonical, decision.ProposedWithdrawalIds);
        Assert.Empty(decision.ActualWithdrawalIds); // a coarse blocker is never withdrawn (fail-closed)

        // Seed ids are order-stable (a set has no order) — ascending ordinal.
        Assert.Equal(
            decision.SeedIds.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            decision.SeedIds);
        Assert.Contains(Leaf("seedA").Canonical, decision.SeedIds);
        Assert.Contains(Leaf("seedB").Canonical, decision.SeedIds);

        // The output directory is always an artifact path.
        Assert.NotEmpty(report.ArtifactPaths);
    }

    // ---- (2) the attribution reported is the FINAL round's, not an earlier one ----------------

    [Fact]
    public void FinalRoundAttribution_IsReported_NotTheWithdrawnEarlierRound()
    {
        var early = Leaf("recoverableLeaf");                       // round 1: withdrawn
        var terminal = Coarse("blockingHelper", RecoveryScope.SharedHelperBundle); // round 2: blocks

        var recovery = WrapperRecoveryController.Run(new PolicyDriver(denylist =>
            denylist.Contains(early) ? AttributedFailure(terminal) : AttributedFailure(early)));

        Assert.False(recovery.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.RequiresGraphClosure, recovery.Cause);
        Assert.True(recovery.Rounds >= 2);

        var report = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, recovery, Seed(), FreshOutputDir());

        // The reported attribution is the terminal round's blocker, not the leaf settled in round 1.
        var unit = Assert.Single(report.AttributedUnits);
        Assert.Equal(terminal.Canonical, unit.UnitId);
        Assert.DoesNotContain(report.AttributedUnits, u => u.UnitId == early.Canonical);

        // The terminal round's proposed withdrawal is the blocker; the leaf shows as an *actual*
        // (settled) withdrawal — the two carriers are distinct and must not be conflated.
        Assert.Contains(terminal.Canonical, report.RecoveryDecision!.ProposedWithdrawalIds);
        Assert.DoesNotContain(early.Canonical, report.RecoveryDecision.ProposedWithdrawalIds);
        Assert.Contains(early.Canonical, report.RecoveryDecision.ActualWithdrawalIds);
        Assert.Equal(terminal.Canonical, report.RecoveryDecision.EscalationUnitId);
    }

    // ---- (3) the report is sourced from the recovery result, not ambient ReportCollector state

    [Fact]
    public void ReportContent_IsIndependentOfReportCollectorResetOrdering()
    {
        var coarse = Coarse("sharedHelper", RecoveryScope.SharedHelperBundle);
        var recovery = WrapperRecoveryController.Run(new ScriptedDriver(AttributedFailure(coarse)));

        // Built before any reset.
        var before = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, recovery, Seed(), FreshOutputDir());

        // Discard the ambient per-run collector, then build again from the same recovery result. The
        // report projects the recovery evidence directly, so a reset between failure and emission — the
        // exact ordering Program.cs guards by emitting first — cannot erase the attribution.
        ReportCollector.Reset();
        var after = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, recovery, Seed(), FreshOutputDir());

        Assert.NotEmpty(after.Diagnostics);
        Assert.NotEmpty(after.AttributedUnits);
        Assert.Equal(
            before.Diagnostics.Select(d => d.Fingerprint),
            after.Diagnostics.Select(d => d.Fingerprint));
        Assert.Equal(
            before.AttributedUnits.Select(u => u.UnitId),
            after.AttributedUnits.Select(u => u.UnitId));
    }

    // ---- (4) a succeeding module carries no terminal evidence and writes no report ------------

    [Fact]
    public void ConvergedRun_CarriesNoTerminalEvidence_AndGuardedEmissionWritesNoReport()
    {
        var recovery = WrapperRecoveryController.Run(new ScriptedDriver((AttributionResult?)null));

        Assert.True(recovery.Converged);
        Assert.Null(recovery.TerminalEvidence); // the converged factory sets no evidence — nothing to project

        var outputDir = FreshOutputDir();

        // Mirror the Program.cs guard: the failure report is emitted only on the non-converged branch.
        if (!recovery.Converged)
        {
            var report = BindingFailureReportBuilder.ForRecoveryNonConvergence(
                "MyModule", NoInputs, recovery, Seed(), outputDir);
            BindingFailureReporting.Emit(report, outputDir, NullLogger.Instance);
        }

        Assert.False(File.Exists(Path.Combine(outputDir, BindingFailureReporting.FileName)));

        // And even if a report were built from a converged result, it would carry no evidence.
        var fromConverged = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, recovery, Seed(), outputDir);
        Assert.Empty(fromConverged.Diagnostics);
        Assert.Empty(fromConverged.AttributedUnits);
    }

    // ---- writer: Emit produces a readable document with enums serialized by name -------------

    [Fact]
    public void Emit_WritesReadableReport_WithEnumsAsNames()
    {
        var coarse = Coarse("sharedHelper", RecoveryScope.SharedHelperBundle);
        var recovery = WrapperRecoveryController.Run(new ScriptedDriver(AttributedFailure(coarse)));
        var outputDir = FreshOutputDir();
        var report = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, recovery, Seed(), outputDir);

        BindingFailureReporting.Emit(report, outputDir, NullLogger.Instance);

        var path = Path.Combine(outputDir, BindingFailureReporting.FileName);
        Assert.True(File.Exists(path));

        var doc = JObject.Parse(File.ReadAllText(path));
        Assert.Equal("MyModule", doc.Value<string>("Module"));
        // The contract is that enums round-trip as their NAME, not an ordinal.
        Assert.Equal("RecoveryNonConvergence", doc["Outcome"]!.Value<string>("Kind"));
        Assert.Equal("SWIFTBIND111", doc["Outcome"]!.Value<string>("ReasonCode"));
        Assert.NotEmpty((JArray)doc["Diagnostics"]!);
        Assert.NotEmpty((JArray)doc["AttributedUnits"]!);
    }

    // ---- input fingerprint: stable for identical inputs, sensitive to content --------------

    [Fact]
    public void InputFingerprint_IsStable_AndContentSensitive()
    {
        var dir = FreshOutputDir();
        var abi = Path.Combine(dir, "a.abi.json");
        var dylib = Path.Combine(dir, "a.dylib");
        File.WriteAllText(abi, "{\"ABIRoot\":{}}");
        File.WriteAllBytes(dylib, new byte[] { 1, 2, 3, 4 });
        var inputs = new BindingFailureInputPaths(abi, dylib, null, null);

        string Fingerprint() => BindingFailureReportBuilder
            .ForFatalExit("M", inputs, BindingFailureOutcomeKind.UnhandledException,
                "UNHANDLED_EXCEPTION", RecoveryStage.Emit, dir)
            .Input.Fingerprint;

        var first = Fingerprint();
        var second = Fingerprint();
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.Equal(first, second); // identical inputs → identical fingerprint

        File.WriteAllBytes(dylib, new byte[] { 9, 9, 9, 9 }); // mutate content, same path
        Assert.NotEqual(first, Fingerprint());
    }

    // ---- ForFatalExit: minimal report, no recovery decision ---------------------------------

    [Fact]
    public void ForFatalExit_BuildsMinimalReport_WithNoRecoveryDecision()
    {
        var report = BindingFailureReportBuilder.ForFatalExit(
            "M", NoInputs, BindingFailureOutcomeKind.UnhandledException,
            "UNHANDLED_EXCEPTION", RecoveryStage.Emit, FreshOutputDir(),
            new[] { BindingFailureReportBuilder.GeneratorDiagnostic("something exploded") });

        Assert.Equal(BindingFailureOutcomeKind.UnhandledException, report.Outcome.Kind);
        Assert.Equal("UNHANDLED_EXCEPTION", report.Outcome.ReasonCode);
        Assert.Equal(RecoveryStage.Emit, report.Outcome.Stage);
        Assert.Equal(0, report.Outcome.RecoveryRounds);
        Assert.Null(report.Outcome.RecoveryCause);
        Assert.Null(report.RecoveryDecision); // a fatal exit never entered the verify-recover loop

        var diag = Assert.Single(report.Diagnostics);
        Assert.Equal(DiagnosticPlane.Generator, diag.Plane);
        Assert.Equal("something exploded", diag.Message);
    }

    // ---- confidence tracks provenance priority (IntervalMap 1 > SymbolAnchor 2 > OriginAnchor 3 > Linker 4)

    [Theory]
    [InlineData(ProvenanceSource.IntervalMap, AttributionConfidence.High)]   // priority 1 — most precise
    [InlineData(ProvenanceSource.SymbolAnchor, AttributionConfidence.High)]  // priority 2 — the @_cdecl block
    [InlineData(ProvenanceSource.OriginAnchor, AttributionConfidence.Medium)] // priority 3 — comment anchor
    [InlineData(ProvenanceSource.LinkerSymbol, AttributionConfidence.Medium)] // priority 4 — name-matched
    public void AttributedUnitConfidence_TracksProvenancePriority(
        ProvenanceSource source, AttributionConfidence expected)
    {
        var coarse = Coarse("sharedHelper", RecoveryScope.SharedHelperBundle);
        var recovery = WrapperRecoveryController.Run(
            new ScriptedDriver(AttributedFailureWith(source, "Wrapper.swift", coarse)));
        Assert.False(recovery.Converged);

        var report = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, recovery, Seed(), FreshOutputDir());

        var unit = Assert.Single(report.AttributedUnits);
        Assert.Equal(source, unit.Provenance);
        // The high-priority interval map must not be reported below the lower-priority linker anchor.
        Assert.Equal(expected, unit.Confidence);
    }

    // ---- outcome stage follows the plane the terminal round actually failed at --------------------

    [Fact]
    public void RecoveryStage_IsCSharpCompile_WhenTerminalDiagnosticsArePlanedCSharp()
    {
        var coarse = Coarse("sharedHelper", RecoveryScope.SharedHelperBundle);
        var recovery = WrapperRecoveryController.Run(
            new ScriptedDriver(AttributedFailureWith(ProvenanceSource.SymbolAnchor, "Verify.cs", coarse)));

        var report = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, recovery, Seed(), FreshOutputDir());

        Assert.Equal(DiagnosticPlane.CSharpCompiler, report.Diagnostics[0].Plane);
        // A C#-plane fixed-point failure reports CSharpCompile, not the SwiftCompile default.
        Assert.Equal(RecoveryStage.CSharpCompile, report.Outcome.Stage);
    }

    [Fact]
    public void RecoveryStage_IsSwiftCompile_WhenTerminalDiagnosticsArePlanedSwift()
    {
        var coarse = Coarse("sharedHelper", RecoveryScope.SharedHelperBundle);
        var recovery = WrapperRecoveryController.Run(new ScriptedDriver(AttributedFailure(coarse)));

        var report = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, recovery, Seed(), FreshOutputDir());

        Assert.Equal(DiagnosticPlane.SwiftCompiler, report.Diagnostics[0].Plane);
        Assert.Equal(RecoveryStage.SwiftCompile, report.Outcome.Stage);
    }

    // ---- fingerprint: distinct named-but-missing inputs must not collide -------------------------

    [Fact]
    public void InputFingerprint_DistinguishesDifferentMissingInputs()
    {
        var dir = FreshOutputDir();

        string Fingerprint(string abiPath) => BindingFailureReportBuilder
            .ForFatalExit("M", new BindingFailureInputPaths(abiPath, null, null, null),
                BindingFailureOutcomeKind.DependencyInputFailure, "SWIFTBIND072", RecoveryStage.Parse, dir)
            .Input.Fingerprint;

        var alpha = Fingerprint(Path.Combine(dir, "Alpha.abi.json")); // supplied but never created
        var beta = Fingerprint(Path.Combine(dir, "Beta.abi.json"));

        Assert.Matches("^[0-9a-f]{64}$", alpha);
        Assert.Matches("^[0-9a-f]{64}$", beta);
        Assert.NotEqual(alpha, beta); // two failures naming different missing inputs must not share a digest
    }

    // ---- a success clears a stale failure report; absence is a safe no-op -----------------------

    [Fact]
    public void RemoveStaleReport_DeletesPriorReport_AndIsSafeWhenAbsent()
    {
        var dir = FreshOutputDir();
        var path = Path.Combine(dir, BindingFailureReporting.FileName);

        // Absent → no-op, never throws.
        BindingFailureReporting.RemoveStaleReport(dir, NullLogger.Instance);
        Assert.False(File.Exists(path));

        // Present (a prior failed run) → removed, so a fresh success leaves no false failure evidence.
        File.WriteAllText(path, "{\"stale\":true}");
        Assert.True(File.Exists(path));
        BindingFailureReporting.RemoveStaleReport(dir, NullLogger.Instance);
        Assert.False(File.Exists(path));
    }
}
