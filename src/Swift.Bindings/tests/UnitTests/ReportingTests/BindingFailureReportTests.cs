// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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

    [Fact]
    public async Task TryWrite_ConcurrentWritersToOneOutputDirectory_AllPersistTheirReport()
    {
        // Two generator invocations legitimately target one output directory at the same time (a
        // parallel build matrix regenerates the same RID-agnostic dependency project from two
        // cells). A fixed temp file name made the second writer's exclusive create fail; TryWrite
        // swallows IO errors, so the loser did not crash — it silently returned null and the
        // durable failure evidence this type exists to produce was never written at all.
        var outputDir = FreshOutputDir();
        var recovery = WrapperRecoveryController.Run(
            new ScriptedDriver(AttributedFailure(Coarse("sharedHelper", RecoveryScope.SharedHelperBundle))));
        var report = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, recovery, Seed(), outputDir);

        const int Writers = 8;
        const int Rounds = 12;
        using var gate = new Barrier(Writers);
        var dropped = new ConcurrentBag<int>();

        var tasks = Enumerable.Range(0, Writers).Select(_ => Task.Factory.StartNew(() =>
        {
            gate.SignalAndWait();
            for (var round = 0; round < Rounds; round++)
            {
                if (BindingFailureReporting.TryWrite(report, outputDir, NullLogger.Instance) == null)
                    dropped.Add(round);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(dropped.IsEmpty,
            $"{dropped.Count} of {Writers * Rounds} concurrent writes dropped the failure report.");

        var path = Path.Combine(outputDir, BindingFailureReporting.FileName);
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(outputDir, "*.tmp"));
        var doc = JObject.Parse(File.ReadAllText(path));
        Assert.Equal("MyModule", doc.Value<string>("Module"));
        Assert.NotEmpty((JArray)doc["Diagnostics"]!);
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

    // ---- fingerprint: the target platform is part of the input identity -------------------------

    [Fact]
    public void InputFingerprint_DistinguishesTargetPlatforms_AndIsStablePerPlatform()
    {
        var dir = FreshOutputDir();
        var abi = Path.Combine(dir, "a.abi.json");
        File.WriteAllText(abi, "{\"ABIRoot\":{}}");

        string Fingerprint(string? platform) => BindingFailureReportBuilder
            .ForFatalExit("M", new BindingFailureInputPaths(abi, null, null, null, platform),
                BindingFailureOutcomeKind.UnhandledException, "UNHANDLED_EXCEPTION",
                RecoveryStage.Emit, dir)
            .Input.Fingerprint;

        var ios = Fingerprint("iOS");
        var macos = Fingerprint("MacOS");
        Assert.Matches("^[0-9a-f]{64}$", ios);
        // The same inputs bound for two platforms are two distinct failures — never one digest.
        Assert.NotEqual(ios, macos);
        // Per-platform the fingerprint stays stable.
        Assert.Equal(ios, Fingerprint("iOS"));
        // A platformless invocation (the legacy shape) still fingerprints.
        Assert.Matches("^[0-9a-f]{64}$", Fingerprint(null));
        Assert.NotEqual(ios, Fingerprint(null));
    }

    // ---- fingerprint: file-name (not directory) identity is deliberate ------------------------

    [Fact]
    public void InputFingerprint_IsStableAcrossDirectories_ForSameNamesAndContent()
    {
        // Re-conversions land the same inputs in fresh temp directories; the fingerprint must
        // identify the inputs, not the directory they happened to land in.
        string FingerprintIn(string dir)
        {
            var abi = Path.Combine(dir, "a.abi.json");
            File.WriteAllText(abi, "{\"ABIRoot\":{}}");
            return BindingFailureReportBuilder
                .ForFatalExit("M", new BindingFailureInputPaths(abi, null, null, null, "iOS"),
                    BindingFailureOutcomeKind.UnhandledException, "UNHANDLED_EXCEPTION",
                    RecoveryStage.Emit, dir)
                .Input.Fingerprint;
        }

        Assert.Equal(FingerprintIn(FreshOutputDir()), FingerprintIn(FreshOutputDir()));
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

    // ---- diagnostic codes: parsed from message text when the tool embeds one -------------------

    // A single-diagnostic terminal attribution whose message and file are chosen by the test, so
    // code-extraction can be exercised against real compiler message shapes.
    private static AttributionResult AttributedFailureWithMessage(string file, string message)
    {
        // A coarse blocker, so the scripted single round is terminal (a leaf would be withdrawn
        // and the controller would ask for a second round).
        var unit = Coarse("codeCarrier", RecoveryScope.SharedHelperBundle);
        var group = ErrorGroupIn(file, message);
        var diagnostics = ImmutableArray.Create(new AttributedDiagnostic
        {
            Diagnostic = group,
            Kind = AttributionKind.Unit,
            Artifact = ArtifactId.Create(unit.Decl, ArtifactRole.SwiftWrapper),
            Unit = unit,
            Source = ProvenanceSource.SymbolAnchor,
        });
        return new AttributionResult
        {
            Diagnostics = diagnostics,
            Culprits = ImmutableArray.Create(unit),
            Fingerprint = DiagnosticFingerprint.Compute(new List<DiagnosticGroup> { group }),
        };
    }

    [Theory]
    // Roslyn diagnostics arrive re-prefixed with their id ("CS0234: …").
    [InlineData("Verify.cs", "CS0234: The type or namespace name 'Foundation' does not exist in the namespace 'Swift'", "CS0234")]
    // MSBuild/SARIF text can also embed the id mid-message after the severity word.
    [InlineData("Verify.cs", "Verify.cs(3,10): error CS0246: The type or namespace name 'FooProxy' could not be found", "CS0246")]
    // Generator diagnostics carry the SWIFTBIND namespace.
    [InlineData("Wrapper.swift", "SWIFTBIND114: an emitted Apple-supplement reference was not recorded", "SWIFTBIND114")]
    // swiftc emits no diagnostic codes — a codeless message must stay null, never a false positive.
    [InlineData("Wrapper.swift", "cannot convert value of type 'Bool' to expected argument type '() -> Bool'", null)]
    // Prose that merely contains capitals+digits (a type name, a version) must not be mistaken for a code.
    [InlineData("Wrapper.swift", "value of type 'P256' has no member 'signature' in iOS 15", null)]
    public void MappedDiagnostic_CarriesEmbeddedCode_WhenMessageEmbedsOne(
        string file, string message, string? expectedCode)
    {
        var recovery = WrapperRecoveryController.Run(
            new ScriptedDriver(AttributedFailureWithMessage(file, message)));
        Assert.False(recovery.Converged);

        var report = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, recovery, Seed(), FreshOutputDir());

        var diag = Assert.Single(report.Diagnostics);
        Assert.Equal(expectedCode, diag.Code);
    }

    // ---- notes: a group's cascade context rides along with its primary ------------------------

    [Fact]
    public void MappedDiagnostic_ProjectsGroupNotes_WithSpansAndNormalizedMessages()
    {
        var unit = Coarse("notesCarrier", RecoveryScope.SharedHelperBundle);
        var group = new DiagnosticGroup
        {
            Primary = new CompilerDiagnostic
            {
                File = "Wrapper.swift",
                Line = 7,
                Column = 3,
                Severity = DiagnosticSeverity.Error,
                Message = "cannot bind the helper",
            },
            Notes = ImmutableArray.Create(
                new CompilerDiagnostic
                {
                    File = "Wrapper.swift",
                    Line = 42,
                    Column = 5,
                    Severity = DiagnosticSeverity.Note,
                    Message = "declared   here\n  with cascade context", // whitespace-normalized on projection
                },
                CompilerDiagnostic.Global(DiagnosticSeverity.Note, "in expansion of macro")),
        };
        var diagnostics = ImmutableArray.Create(new AttributedDiagnostic
        {
            Diagnostic = group,
            Kind = AttributionKind.Unit,
            Artifact = ArtifactId.Create(unit.Decl, ArtifactRole.SwiftWrapper),
            Unit = unit,
            Source = ProvenanceSource.SymbolAnchor,
        });
        var attribution = new AttributionResult
        {
            Diagnostics = diagnostics,
            Culprits = ImmutableArray.Create(unit),
            Fingerprint = DiagnosticFingerprint.Compute(new List<DiagnosticGroup> { group }),
        };
        var recovery = WrapperRecoveryController.Run(new ScriptedDriver(attribution));
        Assert.False(recovery.Converged);

        var report = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, recovery, Seed(), FreshOutputDir());

        var diag = Assert.Single(report.Diagnostics);
        Assert.Equal(2, diag.Notes.Count);

        var positioned = diag.Notes[0];
        Assert.Equal("declared here with cascade context", positioned.Message);
        Assert.NotNull(positioned.Span);
        Assert.Equal("Wrapper.swift", positioned.Span!.File);
        Assert.Equal(42, positioned.Span.Line);
        Assert.Equal(5, positioned.Span.Column);

        var global = diag.Notes[1];
        Assert.Equal("in expansion of macro", global.Message);
        Assert.Null(global.Span); // a positionless note carries no span

        // A note-free group keeps an empty (never null) Notes list.
        var noteFree = WrapperRecoveryController.Run(
            new ScriptedDriver(AttributedFailureWithMessage("Wrapper.swift", "no notes here")));
        var noteFreeReport = BindingFailureReportBuilder.ForRecoveryNonConvergence(
            "MyModule", NoInputs, noteFree, Seed(), FreshOutputDir());
        Assert.Empty(Assert.Single(noteFreeReport.Diagnostics).Notes);
    }

    [Fact]
    public void GeneratorDiagnostic_ParsesLeadingCode_ButExplicitCodeWins()
    {
        // A generator message that leads with its own code gets it parsed out…
        var parsed = BindingFailureReportBuilder.GeneratorDiagnostic(
            "SWIFTBIND115: wrapper compilation failed and --compile-only forbids degrading");
        Assert.Equal("SWIFTBIND115", parsed.Code);

        // …an explicitly supplied code always wins over anything embedded in the text…
        var explicitCode = BindingFailureReportBuilder.GeneratorDiagnostic(
            "SWIFTBIND115: wrapper compilation failed", "SWIFTBIND999");
        Assert.Equal("SWIFTBIND999", explicitCode.Code);

        // …and a codeless message stays codeless.
        Assert.Null(BindingFailureReportBuilder.GeneratorDiagnostic("something exploded").Code);
    }

    // ---- fatal-exit emission: every nonzero-exit path leaves the structured artifact ------------

    // The contract: any nonzero generator exit with a known module and inputs writes
    // binding-failure-report.json — including the post-generation gates (strict-inputs, wrapper
    // compile, C# verification, project emission, mixed-ObjC surface), which exit AFTER a successful
    // generation already cleared the stale report.
    [Theory]
    [InlineData(BindingFailureOutcomeKind.StrictInputsDegraded, "SWIFTBIND027", RecoveryStage.Parse)]
    [InlineData(BindingFailureOutcomeKind.WrapperCompileFailure, "SWIFTBIND052", RecoveryStage.SwiftCompile)]
    [InlineData(BindingFailureOutcomeKind.CSharpVerificationFailure, "CSHARP_VERIFICATION_FAILURE", RecoveryStage.CSharpCompile)]
    [InlineData(BindingFailureOutcomeKind.ProjectEmissionFailure, "PROJECT_EMISSION_FAILURE", RecoveryStage.Emit)]
    [InlineData(BindingFailureOutcomeKind.MixedObjCSurfaceFailure, "MIXED_OBJC_SURFACE_FAILURE", RecoveryStage.Emit)]
    public void EmitFatalExitReport_WritesReport_ForPostGenerationOutcomeKinds(
        BindingFailureOutcomeKind kind, string reasonCode, RecoveryStage stage)
    {
        var dir = FreshOutputDir();

        BindingsGenerator.EmitFatalExitReport(
            "MyModule", kind, reasonCode, stage, "the gate's evidence line",
            new BindingFailureInputPaths(null, null, null, null, "iOS"), dir, NullLogger.Instance);

        var path = Path.Combine(dir, BindingFailureReporting.FileName);
        Assert.True(File.Exists(path));
        var doc = JObject.Parse(File.ReadAllText(path));
        Assert.Equal("MyModule", doc.Value<string>("Module"));
        Assert.Equal(kind.ToString(), doc["Outcome"]!.Value<string>("Kind"));
        Assert.Equal(reasonCode, doc["Outcome"]!.Value<string>("ReasonCode"));
        Assert.Equal(stage.ToString(), doc["Outcome"]!.Value<string>("Stage"));
        var diagnostics = (JArray)doc["Diagnostics"]!;
        Assert.NotEmpty(diagnostics);
        Assert.Contains("the gate's evidence line", diagnostics[0].Value<string>("Message"));
    }

    // Pre-generation exits share the same contract: once the run has resolved a module identity
    // and its inputs, a nonzero exit must leave the artifact even though generation never ran —
    // otherwise a stale report from a PREVIOUS failed run would misattribute this run's failure.
    [Theory]
    [InlineData(BindingFailureOutcomeKind.ObjCPipelineFailure, "OBJC_PIPELINE_FAILURE", RecoveryStage.Parse)]
    [InlineData(BindingFailureOutcomeKind.RequiredInputMissing, "REQUIRED_INPUT_MISSING", RecoveryStage.Parse)]
    [InlineData(BindingFailureOutcomeKind.InvalidConfiguration, "INVALID_CONFIGURATION", RecoveryStage.Parse)]
    public void EmitFatalExitReport_WritesReport_ForPreGenerationOutcomeKinds(
        BindingFailureOutcomeKind kind, string reasonCode, RecoveryStage stage)
        => EmitFatalExitReport_WritesReport_ForPostGenerationOutcomeKinds(kind, reasonCode, stage);

    [Fact]
    public void EmitFatalExitReport_UnknownModule_StillWritesReport()
    {
        var dir = FreshOutputDir();

        BindingsGenerator.EmitFatalExitReport(
            null, BindingFailureOutcomeKind.UnhandledException, "UNHANDLED_EXCEPTION",
            RecoveryStage.Emit, "boom", new BindingFailureInputPaths(null, null, null, null),
            dir, NullLogger.Instance);

        var doc = JObject.Parse(File.ReadAllText(Path.Combine(dir, BindingFailureReporting.FileName)));
        Assert.Equal("<unknown>", doc.Value<string>("Module"));
    }

    [Fact]
    public void EmitFatalExitReport_IsTotal_WhenOutputDirectoryIsUnwritable()
    {
        // The "directory" is an existing FILE, so the write cannot succeed; the reporter must swallow
        // the fault — a reporter error must never mask or re-label the original generation failure.
        var blocking = Path.Combine(FreshOutputDir(), "not-a-dir");
        File.WriteAllText(blocking, "x");

        var ex = Record.Exception(() => BindingsGenerator.EmitFatalExitReport(
            "MyModule", BindingFailureOutcomeKind.WrapperCompileFailure, "SWIFTBIND052",
            RecoveryStage.SwiftCompile, "evidence",
            new BindingFailureInputPaths(null, null, null, null), blocking, NullLogger.Instance));

        Assert.Null(ex);
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
