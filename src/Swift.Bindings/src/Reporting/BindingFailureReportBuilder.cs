// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using BindingsGeneration.Diagnostics;

namespace BindingsGeneration;

/// <summary>The four input paths a failed generation was binding, in whatever combination was supplied.</summary>
public readonly record struct BindingFailureInputPaths(
    string? SwiftAbiPath, string? DylibPath, string? TbdPath, string? SwiftInterfacePath);

/// <summary>
/// Assembles a <see cref="BindingFailureReport"/> from a failure's evidence. Pure and total: it reads
/// the recovery result, the inputs, and the output directory and never throws — file reads for the
/// input fingerprint and artifact listing are caught, so a report is always producible even when the
/// inputs have vanished. It makes no recovery decision; it only projects evidence into the frozen schema.
/// </summary>
public static class BindingFailureReportBuilder
{
    private const int HashBufferSize = 81920;

    /// <summary>Builds the rich report for a verify-recover non-convergence (SWIFTBIND111).</summary>
    public static BindingFailureReport ForRecoveryNonConvergence(
        string module,
        BindingFailureInputPaths inputs,
        WrapperRecoveryResult recovery,
        IReadOnlySet<RecoveryUnitId> ingestionSeed,
        string outputDirectory)
    {
        var attribution = recovery.TerminalEvidence?.Attribution;
        var diagnostics = attribution is null ? new() : MapDiagnostics(attribution);
        return new BindingFailureReport
        {
            Module = module,
            GeneratorVersion = BindingArtifactManifestStore.GetGeneratorVersion(),
            Input = BuildInput(inputs),
            Outcome = new BindingFailureOutcome
            {
                Kind = BindingFailureOutcomeKind.RecoveryNonConvergence,
                ReasonCode = "SWIFTBIND111",
                Stage = StageFor(recovery.Cause, diagnostics),
                RecoveryRounds = recovery.Rounds,
                RecoveryCause = recovery.Cause,
            },
            Diagnostics = diagnostics,
            AttributedUnits = attribution is null ? new() : MapAttributedUnits(attribution),
            RecoveryDecision = BuildRecoveryDecision(recovery, ingestionSeed),
            ArtifactPaths = ArtifactPathsFor(outputDirectory),
        };
    }

    /// <summary>
    /// Builds the report for any other fatal exit — a structural fail-closed gate, an ABI-contract
    /// violation, or an unhandled exception. Carries the module, input identity, terminal outcome, and
    /// any diagnostics the caller could salvage (e.g. an exception message), but no recovery decision.
    /// </summary>
    public static BindingFailureReport ForFatalExit(
        string module,
        BindingFailureInputPaths inputs,
        BindingFailureOutcomeKind kind,
        string reasonCode,
        RecoveryStage stage,
        string outputDirectory,
        IReadOnlyList<FailureDiagnostic>? diagnostics = null)
    {
        return new BindingFailureReport
        {
            Module = module,
            GeneratorVersion = BindingArtifactManifestStore.GetGeneratorVersion(),
            Input = BuildInput(inputs),
            Outcome = new BindingFailureOutcome
            {
                Kind = kind,
                ReasonCode = reasonCode,
                Stage = stage,
                RecoveryRounds = 0,
                RecoveryCause = null,
            },
            Diagnostics = diagnostics is null ? new() : new List<FailureDiagnostic>(diagnostics),
            ArtifactPaths = ArtifactPathsFor(outputDirectory),
        };
    }

    /// <summary>
    /// Wraps a plain generator-originated message (e.g. an exception's <c>Message</c>) as a
    /// generator-plane error diagnostic, so a fatal exit with no compiler diagnostics still records what
    /// went wrong.
    /// </summary>
    public static FailureDiagnostic GeneratorDiagnostic(string message, string? code = null)
    {
        var normalized = NormalizeMessage(message);
        return new FailureDiagnostic
        {
            Plane = DiagnosticPlane.Generator,
            Code = code,
            Severity = DiagnosticSeverity.Error,
            Message = normalized,
            Span = null,
            Fingerprint = EmitterUtility.DeterministicHash8($"{DiagnosticPlane.Generator}|{normalized}|"),
        };
    }

    private static BindingFailureInput BuildInput(BindingFailureInputPaths inputs) => new()
    {
        SwiftAbiPath = inputs.SwiftAbiPath,
        DylibPath = inputs.DylibPath,
        TbdPath = inputs.TbdPath,
        SwiftInterfacePath = inputs.SwiftInterfacePath,
        Fingerprint = ComputeInputFingerprint(inputs),
    };

    // A SWIFTBIND111 manifests at whichever compile the terminal round failed at. An input-configuration
    // cause roots in the input facts, so it is reported at Parse. Otherwise the failing plane is read from
    // the terminal diagnostics: a joint verify-recover run that fails on emitted C# (its diagnostics plane
    // as CSharpCompiler) is CSharpCompile; a Swift-wrapper failure — the common case — is SwiftCompile.
    private static RecoveryStage StageFor(
        WrapperRecoveryFailureCause cause, List<FailureDiagnostic> diagnostics)
    {
        if (cause == WrapperRecoveryFailureCause.InputConfiguration)
            return RecoveryStage.Parse;
        return diagnostics.Any(d => d.Plane == DiagnosticPlane.CSharpCompiler)
            ? RecoveryStage.CSharpCompile
            : RecoveryStage.SwiftCompile;
    }

    private static List<FailureDiagnostic> MapDiagnostics(AttributionResult attribution)
    {
        var list = new List<FailureDiagnostic>(attribution.Diagnostics.Length);
        foreach (var ad in attribution.Diagnostics)
        {
            var primary = ad.Diagnostic.Primary;
            var plane = PlaneFor(ad);
            var normalized = NormalizeMessage(primary.Message);
            list.Add(new FailureDiagnostic
            {
                Plane = plane,
                Code = null,
                Severity = primary.Severity,
                Message = normalized,
                Span = MapSpan(primary),
                Fingerprint = EmitterUtility.DeterministicHash8(
                    $"{plane}|{normalized}|{primary.File}:{primary.Line}:{primary.Column}"),
            });
        }
        return list;
    }

    // Attributed units, keyed by unit and ordered by first appearance in the diagnostic stream, each
    // carrying the indices of the diagnostics that named it (aligned with MapDiagnostics' ordering).
    private static List<AttributedUnit> MapAttributedUnits(AttributionResult attribution)
    {
        var refsByUnit = new Dictionary<RecoveryUnitId, List<int>>();
        var provenanceByUnit = new Dictionary<RecoveryUnitId, ProvenanceSource>();
        var order = new List<RecoveryUnitId>();

        for (int i = 0; i < attribution.Diagnostics.Length; i++)
        {
            var ad = attribution.Diagnostics[i];
            if (ad.Kind != AttributionKind.Unit || ad.Unit is not { } unit)
                continue;

            if (!refsByUnit.TryGetValue(unit, out var refs))
            {
                refs = new List<int>();
                refsByUnit[unit] = refs;
                provenanceByUnit[unit] = ad.Source; // first-seen provenance for the unit
                order.Add(unit);
            }
            refs.Add(i);
        }

        var result = new List<AttributedUnit>(order.Count);
        foreach (var unit in order)
        {
            var provenance = provenanceByUnit[unit];
            result.Add(new AttributedUnit
            {
                UnitId = unit.Canonical,
                DeclId = unit.Decl.Canonical,
                DisplayName = unit.Describe(),
                Scope = unit.Scope,
                Provenance = provenance,
                Confidence = ConfidenceFor(provenance),
                DiagnosticRefs = refsByUnit[unit],
            });
        }
        return result;
    }

    private static RecoveryDecision BuildRecoveryDecision(
        WrapperRecoveryResult recovery, IReadOnlySet<RecoveryUnitId> ingestionSeed)
    {
        var proposed = recovery.TerminalEvidence?.ProposedWithdrawals ?? ImmutableArray<RecoveryUnitId>.Empty;
        return new RecoveryDecision
        {
            // A set has no order; sort so the serialized seed list is stable run-to-run.
            SeedIds = ingestionSeed.Select(u => u.Canonical).OrderBy(s => s, StringComparer.Ordinal).ToList(),
            ProposedWithdrawalIds = proposed.Select(u => u.Canonical).ToList(),
            ActualWithdrawalIds = recovery.Denylist.Select(u => u.Canonical).ToList(),
            SearchIsolatedIds = recovery.SearchIsolated.Select(u => u.Canonical).ToList(),
            BlockerUnitIds = recovery.Blocking.Select(u => u.Canonical).ToList(),
            EscalationUnitId = recovery.Blocking.IsDefaultOrEmpty ? null : recovery.Blocking[0].Canonical,
            AuthorizationOutcome = recovery.Cause == WrapperRecoveryFailureCause.RequiresGraphClosure
                ? CoarseWithdrawalOutcome.Unauthorized
                : CoarseWithdrawalOutcome.NotApplicable,
            ObstructionCode = recovery.Cause.ToString(),
        };
    }

    private static DiagnosticPlane PlaneFor(AttributedDiagnostic ad)
    {
        var file = ad.Diagnostic.Primary.File;
        if (!string.IsNullOrEmpty(file))
        {
            if (file.EndsWith(".swift", StringComparison.OrdinalIgnoreCase))
                return DiagnosticPlane.SwiftCompiler;
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return DiagnosticPlane.CSharpCompiler;
        }
        // A global classification (missing module, toolchain fault) with no source file is generator-plane.
        return ad.Kind == AttributionKind.Classification ? DiagnosticPlane.Generator : DiagnosticPlane.Unknown;
    }

    // Confidence tracks the provenance priority order (IntervalMap 1 > SymbolAnchor 2 > OriginAnchor 3 >
    // LinkerSymbol 4): the two most precise anchors — the per-render interval map and the enclosing
    // @_cdecl/@_silgen_name symbol — are High; the weaker origin-comment and name-matched linker anchors
    // are Medium; an unresolved source is Low. Ranking a lower-priority source above a higher one would
    // mislabel the common IntervalMap-attributed unit that production attribution resolves first.
    private static AttributionConfidence ConfidenceFor(ProvenanceSource source) => source switch
    {
        ProvenanceSource.IntervalMap or ProvenanceSource.SymbolAnchor => AttributionConfidence.High,
        ProvenanceSource.OriginAnchor or ProvenanceSource.LinkerSymbol => AttributionConfidence.Medium,
        _ => AttributionConfidence.Low,
    };

    private static SourceSpan? MapSpan(CompilerDiagnostic diagnostic) =>
        string.IsNullOrEmpty(diagnostic.File) && diagnostic.Line == 0
            ? null
            : new SourceSpan { File = diagnostic.File, Line = diagnostic.Line, Column = diagnostic.Column };

    private static string NormalizeMessage(string? message) =>
        string.IsNullOrEmpty(message) ? string.Empty : Regex.Replace(message.Trim(), @"\s+", " ");

    private static List<string> ArtifactPathsFor(string outputDirectory)
    {
        var paths = new List<string> { outputDirectory };
        try
        {
            foreach (var name in new[]
                     {
                         BindingArtifactManifestStore.ReportFileName,
                         BindingArtifactManifestStore.ManifestFileName,
                     })
            {
                var candidate = Path.Combine(outputDirectory, name);
                if (File.Exists(candidate))
                    paths.Add(candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best-effort listing; the output directory itself is always recorded.
        }
        return paths;
    }

    // Streams each present input through one SHA-256 so even a large dylib is hashed without loading it
    // whole. Bounded to the failure path, where the cost is irrelevant. Returns "unavailable" on IO error
    // rather than throwing — a fingerprint that cannot be computed must not sink the whole report.
    private static string ComputeInputFingerprint(BindingFailureInputPaths inputs)
    {
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            void Feed(string label, string? path)
            {
                if (string.IsNullOrEmpty(path))
                    return;
                // Fold the requested input's identity in even when the file is absent, so two failures
                // that named different (missing) inputs never collide on the same digest; the content is
                // streamed only when the file is actually present.
                hasher.AppendData(Encoding.UTF8.GetBytes($"{label}:{Path.GetFileName(path)}\n"));
                if (!File.Exists(path))
                    return;
                using var stream = File.OpenRead(path);
                var buffer = new byte[HashBufferSize];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hasher.AppendData(buffer, 0, read);
            }

            Feed("abi", inputs.SwiftAbiPath);
            Feed("dylib", inputs.DylibPath);
            Feed("tbd", inputs.TbdPath);
            Feed("interface", inputs.SwiftInterfacePath);
            hasher.AppendData(Encoding.UTF8.GetBytes(
                $"gen:{BindingArtifactManifestStore.GetGeneratorVersion() ?? string.Empty}"));

            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "unavailable";
        }
    }
}
