// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// Severity of a single C# verification diagnostic, mirroring Roslyn's
/// <c>DiagnosticSeverity</c> and SARIF's <c>level</c> so the in-process probe and the
/// real-build SARIF leg map onto one scale.
/// </summary>
public enum CSharpDiagnosticSeverity
{
    Hidden,
    Info,
    Warning,
    Error,
}

/// <summary>
/// One structured C# compile diagnostic. This is the durable shape the downstream
/// attribution/recovery pass consumes: an id, a severity, and a 1-based file span, so a
/// diagnostic can be traced back to the emitted fragment that produced it without re-parsing
/// compiler text. Both verification legs (the in-process Roslyn probe and the real
/// <c>dotnet build</c> + SARIF leg) emit this identical shape.
/// </summary>
/// <param name="Id">Compiler/analyzer rule id (e.g. <c>CS0246</c>, <c>NU1101</c>).</param>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="FilePath">Absolute or output-relative path of the file the diagnostic anchors
/// to; null for project-level diagnostics (restore, missing reference) that carry no location.</param>
/// <param name="Line">1-based start line (0 when unlocated).</param>
/// <param name="Column">1-based start column (0 when unlocated).</param>
/// <param name="EndLine">1-based end line (0 when unlocated).</param>
/// <param name="EndColumn">1-based end column (0 when unlocated).</param>
/// <param name="Message">Human-readable message text.</param>
public sealed record CSharpCompileDiagnostic(
    string Id,
    CSharpDiagnosticSeverity Severity,
    string? FilePath,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Message)
{
    /// <summary>
    /// True for an error that means "the emitted C# does not compile" — the gate's actual
    /// question. Two id families qualify:
    /// <list type="bullet">
    /// <item><c>CS####</c> — the C# compiler itself.</item>
    /// <item><c>SYSLIB####</c> — the source generators the emitted code depends on, chiefly the
    /// <c>LibraryImport</c> generator. A P/Invoke parameter the generator cannot marshal (an
    /// unresolved-type placeholder is the shape that reaches it) is reported ONLY as
    /// <c>SYSLIB1051</c>: the generator refuses to expand the stub, and the build fails without
    /// the compiler ever raising a <c>CS</c> error of its own unless some call site happens to
    /// also break. Classifying that as infrastructure-or-unknown made the loop answer
    /// "inconclusive" — which, with nothing yet withdrawn, passes through and ships a binding
    /// that provably does not build. It is a statement about the emitted source, it anchors to
    /// the emitted member, and it is recoverable by withdrawing that member, so it belongs on
    /// this side of the split.</item>
    /// </list>
    /// </summary>
    public bool IsCompilerError =>
        Severity == CSharpDiagnosticSeverity.Error &&
        (Id.StartsWith("CS", StringComparison.Ordinal) ||
         Id.StartsWith("SYSLIB", StringComparison.Ordinal));

    /// <summary>
    /// True for a restore / build-infrastructure diagnostic (<c>NU####</c> NuGet restore,
    /// <c>MSB####</c> MSBuild). A build that fails only with these did not answer the
    /// "does the emitted C# compile" question — it never reached the compiler — so the gate
    /// treats it as inconclusive rather than a C# compile failure.
    /// </summary>
    public bool IsRestoreOrInfrastructure =>
        Severity == CSharpDiagnosticSeverity.Error &&
        (Id.StartsWith("NU", StringComparison.Ordinal) ||
         Id.StartsWith("MSB", StringComparison.Ordinal));

    /// <summary>Deterministic ordering key: file, line, column, id.</summary>
    public (string, int, int, string) OrderKey => (FilePath ?? string.Empty, Line, Column, Id);
}

/// <summary>
/// The verdict of a C# verification pass.
/// </summary>
public enum CSharpVerificationOutcome
{
    /// <summary>The emitted C# compiled with no errors.</summary>
    Clean,

    /// <summary>The emitted C# produced one or more source-level errors (<c>CS####</c> or <c>SYSLIB####</c>).</summary>
    CompileErrors,

    /// <summary>
    /// Verification could not answer the compile question — restore/build-infrastructure
    /// failure, a missing reference the verifier could not resolve, or a verifier-internal
    /// error. Never a statement that the C# is good; a statement that it was not checked.
    /// </summary>
    Inconclusive,
}

/// <summary>
/// The result of a C# verification pass. Carries the full structured diagnostic set (both
/// verification legs produce the same shape) plus a coarse outcome the gate acts on.
/// </summary>
public sealed record CSharpVerificationResult(
    CSharpVerificationOutcome Outcome,
    IReadOnlyList<CSharpCompileDiagnostic> Diagnostics,
    string? InconclusiveReason = null)
{
    /// <summary>The source-level errors (<c>CS####</c>/<c>SYSLIB####</c>) among the diagnostics, deterministically ordered.</summary>
    public IReadOnlyList<CSharpCompileDiagnostic> CompilerErrors =>
        Diagnostics.Where(d => d.IsCompilerError).OrderBy(d => d.OrderKey).ToList();

    /// <summary>
    /// Classify a diagnostic set into an outcome using the source-error-vs-NU/MSB split. A build
    /// that produced any CS/SYSLIB error is <see cref="CSharpVerificationOutcome.CompileErrors"/>; a build
    /// that failed with only restore/infrastructure errors is
    /// <see cref="CSharpVerificationOutcome.Inconclusive"/>; a build that produced no errors is
    /// <see cref="CSharpVerificationOutcome.Clean"/>.
    /// </summary>
    public static CSharpVerificationResult FromDiagnostics(
        IReadOnlyList<CSharpCompileDiagnostic> diagnostics, bool buildSucceeded)
    {
        var ordered = diagnostics.OrderBy(d => d.OrderKey).ToList();
        if (ordered.Any(d => d.IsCompilerError))
            return new CSharpVerificationResult(CSharpVerificationOutcome.CompileErrors, ordered);

        if (buildSucceeded)
            return new CSharpVerificationResult(CSharpVerificationOutcome.Clean, ordered);

        // Non-zero exit with no CS error: the compiler never rendered a verdict on the C#.
        var infra = ordered.Where(d => d.IsRestoreOrInfrastructure).ToList();
        var reason = infra.Count > 0
            ? $"build failed before the C# compile with {infra.Count} restore/infrastructure error(s) (first: {infra[0].Id})"
            : "build failed with no C# source-level error reported";
        return new CSharpVerificationResult(CSharpVerificationOutcome.Inconclusive, ordered, reason);
    }
}
