// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Why a declaration is being refused: a live emitter exception, or a deliberate withdrawal by the
/// wrapper verify-recover loop. It selects the wording of <see cref="EmitterFaultRecord.Details"/> so a
/// recovery withdrawal never masquerades as a thrown exception in a tombstone comment or report row.
/// </summary>
internal enum EmitterFaultOrigin
{
    /// <summary>The emitter threw while lowering the declaration. The default.</summary>
    EmitterException,

    /// <summary>
    /// The declaration was withdrawn to make the Swift wrapper compile — no exception occurred. The
    /// exception-shaped fields are not meaningful for this origin.
    /// </summary>
    RecoveryWithdrawal,
}

/// <summary>
/// One declaration being refused emission — either the emitter threw on it, or the wrapper
/// verify-recover loop withdrew it (see <see cref="Origin"/>). For a thrown fault it also carries
/// enough of the exception to tell two distinct defects apart in a report without dragging a whole
/// stack trace into generated output.
/// </summary>
/// <remarks>
/// The fingerprint deliberately excludes file paths and line numbers. It has to be stable across
/// machines and build configurations, because it lands in a report row that regeneration-consistency
/// assertions compare; the innermost frame's declaring type and method name identify the defect just
/// as well and do not move when an unrelated edit shifts line numbers.
/// </remarks>
internal readonly record struct EmitterFaultRecord
{
    /// <summary>The declaration whose emission threw.</summary>
    public required DeclId Subject { get; init; }

    /// <summary>
    /// Whether this fault is a live emitter exception or a deliberate recovery withdrawal. Defaults to
    /// <see cref="EmitterFaultOrigin.EmitterException"/> so records built by <see cref="From"/> keep the
    /// exception wording; a synthetic verify-recover seed sets
    /// <see cref="EmitterFaultOrigin.RecoveryWithdrawal"/>.
    /// </summary>
    public EmitterFaultOrigin Origin { get; init; }

    /// <summary>How much of the surface has to be withdrawn to contain this fault.</summary>
    public required RecoveryScope Scope { get; init; }

    /// <summary>Runtime type name of the thrown exception.</summary>
    public required string ExceptionType { get; init; }

    /// <summary>Stable identifier for the throw site — see the remarks on this type.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The exception's message, for the report row's details column.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// The unit to poison instead if this same subject throws again on a later attempt — the
    /// containing type for a member, absent for a type (whose next rung is the module, i.e. failure).
    /// Denying a member that then faults a second time means the fault was never really the member's,
    /// so widening is the only move that can converge.
    /// </summary>
    public DeclId? Escalation { get; init; }

    /// <summary>Builds a record from a live exception caught at an emission seam.</summary>
    public static EmitterFaultRecord From(
        DeclId subject,
        RecoveryScope scope,
        Exception exception,
        DeclId? escalation = null) =>
        new()
        {
            Subject = subject,
            Scope = scope,
            ExceptionType = exception.GetType().Name,
            Fingerprint = BuildFingerprint(exception),
            Message = exception.Message,
            Escalation = escalation,
        };

    /// <summary>
    /// Builds a record for a declaration withdrawn by the wrapper verify-recover loop rather than one
    /// the emitter threw on. No exception is involved, so the exception-shaped fields are left empty and
    /// <see cref="Details"/> reads as a withdrawal, not a throw.
    /// </summary>
    public static EmitterFaultRecord ForRecoveryWithdrawal(
        DeclId subject,
        RecoveryScope scope,
        string reason,
        DeclId? escalation = null) =>
        new()
        {
            Subject = subject,
            Scope = scope,
            Origin = EmitterFaultOrigin.RecoveryWithdrawal,
            ExceptionType = string.Empty,
            Fingerprint = string.Empty,
            Message = reason,
            Escalation = escalation,
        };

    /// <summary>
    /// How a recovery-withdrawal details string begins. This prefix is the one signal of
    /// <see cref="EmitterFaultOrigin.RecoveryWithdrawal"/> that survives into a skip row — the row
    /// itself carries only reason and details — so <see cref="SkipCauseClassifier"/> keys stage
    /// refinement on it rather than duplicating the wording.
    /// </summary>
    internal const string WithdrawalDetailsPrefix = "Withdrawn by wrapper verify-recover: ";

    /// <summary>
    /// The details string recorded on the skip row. Reads as a sentence a triager can act on. For an
    /// emitter exception it keeps the fingerprint so two rows can be compared without re-running the
    /// generator; for a recovery withdrawal it says plainly that the unit was withdrawn to make the
    /// wrapper compile — never claiming an exception that did not occur.
    /// </summary>
    public string Details =>
        Origin == EmitterFaultOrigin.RecoveryWithdrawal
            ? $"{WithdrawalDetailsPrefix}{Message}"
            : $"Emitter threw {ExceptionType} at {Fingerprint}: {Message}";

    private static string BuildFingerprint(Exception exception)
    {
        // Unwrap to the exception that actually threw — a wrapper's own stack starts at the rethrow
        // site, which would collapse every distinct inner defect onto one fingerprint.
        var innermost = exception;
        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }

        var frames = new StackTrace(innermost, fNeedFileInfo: false).GetFrames();
        var builder = new StringBuilder();

        foreach (var frame in frames)
        {
            // DiagnosticMethodInfo rather than StackFrame.GetMethod(): the latter is reflection over
            // metadata the trimmer is free to drop, and the generator publishes trimmed.
            var method = DiagnosticMethodInfo.Create(frame);
            if (method is null)
            {
                continue;
            }

            var declaringType = method.DeclaringTypeName;
            var lastDot = declaringType?.LastIndexOf('.') ?? -1;

            builder.Append(lastDot >= 0 ? declaringType![(lastDot + 1)..] : declaringType ?? "<global>")
                   .Append('.')
                   .Append(method.Name);
            break;
        }

        return builder.Length == 0 ? "<no-frame>" : builder.ToString();
    }
}
