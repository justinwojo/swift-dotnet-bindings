// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using Xunit;

using BindingsGeneration.Diagnostics;

namespace BindingsGeneration.Tests;

/// <summary>
/// The fingerprint is a position-independent digest of a compile's error set, and the no-progress
/// detector reads a history of them to decide when to escalate. These pin both.
/// </summary>
public class DiagnosticFingerprintTests
{
    private static DiagnosticGroup Error(string file, int line, int col, string message) => new()
    {
        Primary = new CompilerDiagnostic
        {
            File = file,
            Line = line,
            Column = col,
            Severity = DiagnosticSeverity.Error,
            Message = message,
        },
    };

    private static DiagnosticGroup Warning(string message) => new()
    {
        Primary = new CompilerDiagnostic { Severity = DiagnosticSeverity.Warning, Message = message },
    };

    /// <summary>
    /// The defining property: the same error messages at different lines/columns/files produce the
    /// same fingerprint, because a re-render moves a failure without changing it.
    /// </summary>
    [Fact]
    public void Compute_SameMessagesAtDifferentPositions_ProduceTheSameFingerprint()
    {
        var round1 = new[]
        {
            Error("A.swift", 8, 34, "cannot find 'MissingGadgetType' in scope"),
            Error("A.swift", 8, 25, "generic parameter 'T' could not be inferred"),
        };
        var round2 = new[]
        {
            Error("A.swift", 42, 10, "generic parameter 'T' could not be inferred"),
            Error("A.swift", 40, 3, "cannot find 'MissingGadgetType' in scope"),
        };

        Assert.Equal(
            DiagnosticFingerprint.Compute(round1),
            DiagnosticFingerprint.Compute(round2));
    }

    /// <summary>
    /// Multiplicity is part of the fingerprint: two members failing with the same generic message
    /// versus one differ, so withdrawing one of a same-message pair reads as progress, not a repeat.
    /// </summary>
    [Fact]
    public void Compute_SameMessageDifferentMultiplicity_ProducesDifferentFingerprints()
    {
        var bothBroken = new[]
        {
            Error("A.swift", 7, 1, "generic parameter 'T' could not be inferred"),
            Error("A.swift", 19, 1, "generic parameter 'T' could not be inferred"),
        };
        var oneBroken = new[]
        {
            Error("A.swift", 19, 1, "generic parameter 'T' could not be inferred"),
        };

        Assert.NotEqual(
            DiagnosticFingerprint.Compute(bothBroken),
            DiagnosticFingerprint.Compute(oneBroken));
    }

    [Fact]
    public void Compute_DifferentErrorSets_ProduceDifferentFingerprints()
    {
        var a = new[] { Error("A.swift", 1, 1, "cannot find 'Alpha' in scope") };
        var b = new[] { Error("A.swift", 1, 1, "cannot find 'Beta' in scope") };

        Assert.NotEqual(DiagnosticFingerprint.Compute(a), DiagnosticFingerprint.Compute(b));
    }

    /// <summary>Absolute paths inside a message are normalized, so a different temp dir is not "progress".</summary>
    [Fact]
    public void Compute_MessagesDifferingOnlyByAbsolutePath_AreTheSame()
    {
        var a = new[] { Error(null, 0, 0, "ld: cannot open /tmp/build-abc/libX.a") };
        var b = new[] { Error(null, 0, 0, "ld: cannot open /tmp/build-xyz/libX.a") };

        Assert.Equal(DiagnosticFingerprint.Compute(a), DiagnosticFingerprint.Compute(b));
    }

    /// <summary>Only errors participate — a new warning is not a changed failure.</summary>
    [Fact]
    public void Compute_IgnoresWarnings()
    {
        var justError = new[] { Error("A.swift", 1, 1, "cannot find 'Alpha' in scope") };
        var errorPlusWarning = new[]
        {
            Error("A.swift", 1, 1, "cannot find 'Alpha' in scope"),
            Warning("unused variable 'x'"),
        };

        Assert.Equal(
            DiagnosticFingerprint.Compute(justError),
            DiagnosticFingerprint.Compute(errorPlusWarning));
    }

    [Fact]
    public void Compute_EmptyAndNull_AreEqualAndStable()
    {
        Assert.Equal(
            DiagnosticFingerprint.Compute(System.Array.Empty<DiagnosticGroup>()),
            DiagnosticFingerprint.Compute(null));
    }

    // ── no-progress detector ────────────────────────────────────────────────────────────────

    [Fact]
    public void IsRepeatedFingerprint_TrueOnlyWhenTheLastTwoMatch()
    {
        Assert.False(NoProgressDetector.IsRepeatedFingerprint(new[] { "a" }));
        Assert.False(NoProgressDetector.IsRepeatedFingerprint(new[] { "a", "b" }));
        Assert.True(NoProgressDetector.IsRepeatedFingerprint(new[] { "a", "b", "b" }));
        // An earlier repeat that is not the most recent pair does not count.
        Assert.False(NoProgressDetector.IsRepeatedFingerprint(new[] { "b", "b", "c" }));
    }

    [Fact]
    public void AttributedNothing_TrueWhenErrorsExistButNoCulprit()
    {
        var withErrorNoCulprit = new AttributionResult
        {
            Diagnostics = ImmutableArray.Create(new AttributedDiagnostic
            {
                Diagnostic = Error("A.swift", 1, 1, "unrecognized"),
                Kind = AttributionKind.Unattributed,
            }),
            Culprits = ImmutableArray<RecoveryUnitId>.Empty,
            Fingerprint = "x",
        };

        Assert.True(NoProgressDetector.AttributedNothing(withErrorNoCulprit));
    }

    [Fact]
    public void AttributedNothing_FalseWhenAtLeastOneCulprit()
    {
        var unit = AttributionFixtures.UnitForSymbol("SBW_x");
        var withCulprit = new AttributionResult
        {
            Diagnostics = ImmutableArray.Create(new AttributedDiagnostic
            {
                Diagnostic = Error("A.swift", 1, 1, "boom"),
                Kind = AttributionKind.Unit,
                Unit = unit,
            }),
            Culprits = ImmutableArray.Create(unit),
            Fingerprint = "x",
        };

        Assert.False(NoProgressDetector.AttributedNothing(withCulprit));
    }

    [Fact]
    public void ShouldEscalate_FiresOnEitherSignal()
    {
        var progressing = new AttributionResult
        {
            Diagnostics = ImmutableArray<AttributedDiagnostic>.Empty,
            Culprits = ImmutableArray.Create(AttributionFixtures.UnitForSymbol("SBW_x")),
            Fingerprint = "b",
        };

        // Repeated fingerprint, even though this round attributed a culprit.
        Assert.True(NoProgressDetector.ShouldEscalate(new[] { "a", "b", "b" }, progressing));

        // Fresh fingerprint but nothing attributed.
        var stuck = new AttributionResult
        {
            Diagnostics = ImmutableArray.Create(new AttributedDiagnostic
            {
                Diagnostic = Error("A.swift", 1, 1, "boom"),
                Kind = AttributionKind.Unattributed,
            }),
            Culprits = ImmutableArray<RecoveryUnitId>.Empty,
            Fingerprint = "c",
        };
        Assert.True(NoProgressDetector.ShouldEscalate(new[] { "a", "b", "c" }, stuck));

        // Distinct recent fingerprints and a culprit: keep going.
        Assert.False(NoProgressDetector.ShouldEscalate(new[] { "a", "b" }, progressing));
    }
}
