// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

using BindingsGeneration;

namespace BindingsGeneration.Diagnostics;

/// <summary>
/// Severity of one compiler diagnostic, in the vocabulary both swiftc and Roslyn share.
/// </summary>
/// <remarks>
/// The attribution machinery is language-neutral on purpose — the same pipeline serves the Swift
/// wrapper compile today and the in-process C# probe later — so the severity vocabulary is the
/// intersection of what the two compilers emit, not a Swift-only enum.
/// </remarks>
public enum DiagnosticSeverity
{
    /// <summary>An error: the diagnostic that stopped the compile.</summary>
    Error,

    /// <summary>A warning: does not fail the compile, but carries a position and message.</summary>
    Warning,

    /// <summary>A note: follow-on detail attached to the primary above it. Never attributed on its own.</summary>
    Note,

    /// <summary>A remark: informational compiler output.</summary>
    Remark,
}

/// <summary>
/// One structured compiler diagnostic: where it points, how severe it is, and what it says.
/// </summary>
/// <remarks>
/// <para>
/// Positions are 1-based, matching every compiler's own numbering. <see cref="Column"/>'s encoding is
/// <em>plane-dependent</em>, because the two compilers this type carries number columns differently: a
/// swiftc (Swift-wrapper) diagnostic reports a <em>UTF-8 byte</em> column, which a caller resolves
/// against the interval map via <see cref="FileIntervalMap.TryResolveUtf8Column"/>; a Roslyn/SARIF
/// (emitted-C#) diagnostic reports a <em>UTF-16 character</em> column, resolved directly via
/// <c>FileIntervalMap.TryResolve</c>. The producer fixes which encoding applies, so it is stated here
/// rather than left implicit.
/// </para>
/// <para>
/// A diagnostic with no usable position — a linker error, a toolchain crash — carries a null
/// <see cref="File"/> and zero line/column; <see cref="HasPosition"/> is the single predicate that
/// distinguishes the two, so no consumer has to re-derive "is this locatable" from the raw fields.
/// </para>
/// </remarks>
public readonly record struct CompilerDiagnostic
{
    /// <summary>Source file the diagnostic points at, or null for a positionless diagnostic.</summary>
    public string? File { get; init; }

    /// <summary>1-based line, or 0 when there is no position.</summary>
    public int Line { get; init; }

    /// <summary>
    /// 1-based column, or 0 when there is no position. Encoding is plane-dependent — a UTF-8 byte
    /// column for swiftc (Swift) diagnostics, a UTF-16 character column for Roslyn/SARIF (C#) ones;
    /// see the type remarks.
    /// </summary>
    public int Column { get; init; }

    /// <summary>Severity of the diagnostic.</summary>
    public DiagnosticSeverity Severity { get; init; }

    /// <summary>The message text, with the <c>file:line:col: severity:</c> prefix stripped.</summary>
    public string Message { get; init; }

    /// <summary>True when the diagnostic carries a resolvable source position.</summary>
    public bool HasPosition => !string.IsNullOrEmpty(File) && Line > 0;

    /// <summary>Builds a positionless diagnostic (linker error, toolchain failure).</summary>
    public static CompilerDiagnostic Global(DiagnosticSeverity severity, string message) =>
        new() { File = null, Line = 0, Column = 0, Severity = severity, Message = message ?? string.Empty };

    /// <inheritdoc />
    public override string ToString() =>
        HasPosition ? $"{File}:{Line}:{Column}: {Severity}: {Message}" : $"{Severity}: {Message}";
}

/// <summary>
/// A primary diagnostic and the notes that ride along with it as evidence.
/// </summary>
/// <remarks>
/// Cascade hygiene starts here: only the <see cref="Primary"/> is ever attributed to a recovery
/// unit. The <see cref="Notes"/> are the compiler's own explanation of the primary — "candidate
/// here", "required by this conformance" — kept because they are exactly the context a human (or a
/// later bisection fallback) needs, but never counted as independent culprits. A note attributing
/// somewhere else would double-count a single failure and inflate the denylist.
/// </remarks>
public sealed record DiagnosticGroup
{
    /// <summary>The error/warning that heads the group.</summary>
    public required CompilerDiagnostic Primary { get; init; }

    /// <summary>Notes attached to <see cref="Primary"/>, in emitted order. Evidence only.</summary>
    public ImmutableArray<CompilerDiagnostic> Notes { get; init; } = ImmutableArray<CompilerDiagnostic>.Empty;

    /// <summary>True when the primary is an error (the group represents a compile-stopping failure).</summary>
    public bool IsError => Primary.Severity == DiagnosticSeverity.Error;

    /// <inheritdoc />
    public override string ToString() =>
        Notes.IsDefaultOrEmpty ? Primary.ToString() : $"{Primary} (+{Notes.Length} note(s))";
}
