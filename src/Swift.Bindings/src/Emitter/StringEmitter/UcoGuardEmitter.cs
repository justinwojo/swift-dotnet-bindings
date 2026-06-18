// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Single source of truth for the <c>try</c>/<c>catch</c> envelope that guards the managed
/// body of every emitted <c>[UnmanagedCallersOnly]</c> (UCO) callback.
///
/// A managed exception that unwinds across the native (Swift) call boundary is undefined
/// behaviour — the process aborts with a corrupted, undiagnosable stack. A bare
/// <c>catch { }</c> is equally wrong: it hides the fault and hands Swift a fabricated result.
/// The contract therefore depends on what error channel the callback has:
/// <list type="bullet">
/// <item><see cref="UcoFaultPolicy.FailFast"/> — non-throwing callbacks with no error channel
/// (closure trampolines, protocol-proxy receivers, KVO change handlers). A controlled
/// <see cref="System.Environment.FailFast(string, System.Exception)"/> via
/// <c>SwiftClosureMarshaller.FailFastUnhandledClosureException</c> is the only safe outcome:
/// loud, attributable, and never re-entering native code.</item>
/// <item><see cref="UcoFaultPolicy.StreamFault"/> — AsyncStream element/complete callbacks
/// that own a channel. A marshal failure faults the channel (the consumer observes the
/// exception) instead of aborting the process. Wired by Defect I (AsyncStream bridge).</item>
/// <item><see cref="UcoFaultPolicy.ResumeBoxError"/> — async-closure Start thunks that own a
/// Swift continuation box. Any escape resumes the box with an error (or, for a non-throwing
/// closure with no Swift error channel, a loud FailFast) via a caller-supplied catch body so
/// Swift's task never hangs and the box is consumed exactly once. Wired by Finding 37; the
/// owning emitter supplies the resume statements (which continuation box, which resume helper)
/// the same way <see cref="UcoFaultPolicy.StreamFault"/> supplies its fault statements, while
/// this envelope still owns the try/catch structure (Finding 38 — one structural envelope).</item>
/// </list>
///
/// The trailing <c>throw;</c> in the FailFast catch is required: C# end-point reachability
/// (CS0161) does not honor <c>[DoesNotReturn]</c>, so a value-returning callback whose catch
/// ends in the FailFast call still trips CS0161. <c>throw;</c> is a type-agnostic terminator
/// and is safe on the void path too (FailFast never returns, so it is never reached).
///
/// <para>The <c>member</c> parameter of <see cref="EmitClose"/> selects a member-named refinement
/// of the <see cref="UcoFaultPolicy.FailFast"/> close for async protocol-requirement receivers
/// (<see cref="EmitCloseAsyncWitnessFailFast"/> is a thin convenience wrapper over it, so there is
/// one close-emitter, not two). The async witness is satisfied through the synchronously-blocked
/// reverse-dispatch slot (the async witness ABI hits the Mono reverse-async assertion, upstream
/// Issue 1), which exposes no Swift error channel — even for an <c>async throws</c> requirement —
/// so any escaping exception is still process-terminating. The refinement only makes the FailFast
/// loud and attributable: a cancellation-specific arm (<see cref="System.OperationCanceledException"/>,
/// the routine C# control-flow exception a token wired into the conformance would raise) and a
/// general arm, both naming the protocol member (Finding 36). The real async/error witness that
/// would carry the error back is Session 13.</para>
/// </summary>
public static class UcoGuardEmitter
{
    /// <summary>The fault policy applied when an exception escapes a guarded UCO body.</summary>
    public enum UcoFaultPolicy
    {
        /// <summary>Controlled <see cref="System.Environment.FailFast(string, System.Exception)"/>.</summary>
        FailFast,

        /// <summary>Fault the owning AsyncStream channel (Defect I).</summary>
        StreamFault,

        /// <summary>Resume the owning Swift continuation box with an error (Finding 37).</summary>
        ResumeBoxError,
    }

    /// <summary>
    /// Opens the <c>try</c> block guarding a UCO body and increases the writer indent. Pair with
    /// <see cref="EmitClose"/>.
    /// </summary>
    public static void EmitOpen(CSharpWriter writer)
    {
        writer.WriteLine("try");
        writer.WriteLine("{");
        writer.Indent++;
    }

    /// <summary>
    /// Emits the catch-only FailFast block (no preceding close brace) for callers that manage
    /// their own <c>try</c> close. <paramref name="exceptionVar"/> and
    /// <paramref name="fullyQualified"/> exist solely to reproduce the two historical byte-for-byte
    /// shapes (the unqualified closure-bridge catch using <c>__ex</c>, and the fully-qualified
    /// receiver catch using <c>__uco_ex</c>) so consolidating those sites changes no emitted output.
    /// </summary>
    public static void EmitFailFastCatch(CSharpWriter writer, string exceptionVar = "__uco_ex",
        bool fullyQualified = true)
    {
        var marshaller = fullyQualified
            ? "global::Swift.Runtime.SwiftClosureMarshaller"
            : "SwiftClosureMarshaller";
        writer.WriteLine($"catch (global::System.Exception {exceptionVar})");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"{marshaller}.FailFastUnhandledClosureException({exceptionVar});");
        writer.WriteLine("throw;");
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Closes the <c>try</c> opened by <see cref="EmitOpen"/> (decreases indent, emits the close
    /// brace) and appends the catch for <paramref name="policy"/>.
    /// </summary>
    /// <param name="streamFaultBody">
    /// For <see cref="UcoFaultPolicy.StreamFault"/>, the statement(s) emitted inside the catch (e.g.
    /// <c>stream.FaultChannel(__uco_ex); return 0;</c>). The fault target and return shape are
    /// callback-specific (element vs completion), so the owning emitter supplies them while this
    /// envelope still owns the catch structure. Each line is written at the catch-body indent.
    /// Required for <see cref="UcoFaultPolicy.StreamFault"/>; ignored otherwise.
    /// </param>
    /// <param name="resumeErrorBody">
    /// For <see cref="UcoFaultPolicy.ResumeBoxError"/>, the statement(s) emitted inside the catch
    /// (e.g. resume the owning continuation box with the error, or — for a non-throwing closure with
    /// no Swift error channel — a loud FailFast). Which box and which resume helper are
    /// callback-specific, so the owning emitter supplies them while this envelope still owns the
    /// catch structure. Each line is written at the catch-body indent. Required for
    /// <see cref="UcoFaultPolicy.ResumeBoxError"/>; ignored otherwise.
    /// </param>
    /// <param name="member">
    /// When non-null with <see cref="UcoFaultPolicy.FailFast"/>, selects the async-witness member-named
    /// FailFast refinement (see <see cref="EmitCloseAsyncWitnessFailFast"/>) instead of the plain
    /// anonymous FailFast. Ignored for the other policies.
    /// </param>
    public static void EmitClose(CSharpWriter writer, UcoFaultPolicy policy,
        string exceptionVar = "__uco_ex", bool fullyQualified = true,
        string[]? streamFaultBody = null,
        string[]? resumeErrorBody = null,
        string? member = null)
    {
        writer.Indent--;
        writer.WriteLine("}");
        switch (policy)
        {
            case UcoFaultPolicy.FailFast:
                if (member is not null)
                {
                    EmitAsyncWitnessFailFastCatch(writer, member, exceptionVar, fullyQualified);
                }
                else
                {
                    EmitFailFastCatch(writer, exceptionVar, fullyQualified);
                }
                break;
            case UcoFaultPolicy.StreamFault:
                // AsyncStream element/completion callbacks own a channel: a managed exception that
                // would otherwise unwind across the Swift boundary faults the channel so the consumer
                // observes it. The fault statements are caller-supplied (which stream variable, what
                // to return) but the try/catch envelope stays single-sourced here. Wired by Defect I.
                EmitCallerSuppliedCatch(writer, exceptionVar, streamFaultBody,
                    nameof(streamFaultBody), "StreamFault");
                break;
            case UcoFaultPolicy.ResumeBoxError:
                // Async-closure Start thunks own a Swift continuation box: an escape resumes the box
                // with the error (throwing closure) or FailFasts loudly (non-throwing closure, no
                // Swift error channel) so the Swift task never hangs and the box is consumed exactly
                // once. The resume statements are caller-supplied (which box, which helper) but the
                // try/catch envelope stays single-sourced here. Wired by Finding 37.
                EmitCallerSuppliedCatch(writer, exceptionVar, resumeErrorBody,
                    nameof(resumeErrorBody), "ResumeBoxError");
                break;
        }
    }

    /// <summary>
    /// Emits a <c>catch (Exception)</c> whose body is the caller-supplied statements, after asserting
    /// at least one statement was supplied. Shared by the channel-owning policies
    /// (<see cref="UcoFaultPolicy.StreamFault"/>, <see cref="UcoFaultPolicy.ResumeBoxError"/>) that
    /// route the fault to a resource they own rather than FailFasting the process.
    /// </summary>
    private static void EmitCallerSuppliedCatch(CSharpWriter writer, string exceptionVar,
        string[]? body, string paramName, string policyName)
    {
        if (body is null || body.Length == 0)
        {
            throw new global::System.ArgumentException(
                $"{policyName} policy requires a non-empty {paramName} (the catch statements).",
                paramName);
        }
        writer.WriteLine($"catch (global::System.Exception {exceptionVar})");
        writer.WriteLine("{");
        writer.Indent++;
        foreach (var line in body)
        {
            writer.WriteLine(line);
        }
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Closes the <c>try</c> opened by <see cref="EmitOpen"/> for an <b>async</b> protocol-requirement
    /// receiver with a member-named refinement of the <see cref="UcoFaultPolicy.FailFast"/> close
    /// (Finding 36). A thin convenience wrapper over <see cref="EmitClose"/> with <c>member</c> set, so
    /// there is one close-emitter, not two. The async witness blocks the C# <c>Task</c> on the
    /// synchronously-blocked reverse-dispatch slot (upstream Issue 1) and has no Swift error channel,
    /// so any escaping exception is process-terminating exactly as for the plain FailFast policy — this
    /// only names the member and splits out the cancellation case so the fault is attributable rather
    /// than anonymous.
    /// </summary>
    /// <param name="member">
    /// A human-readable protocol-member descriptor (e.g. <c>Protocol.method</c>) embedded verbatim in
    /// the FailFast diagnostics. Callers build it from the protocol + method name.
    /// </param>
    public static void EmitCloseAsyncWitnessFailFast(CSharpWriter writer, string member,
        string exceptionVar = "__uco_ex", bool fullyQualified = true)
        => EmitClose(writer, UcoFaultPolicy.FailFast, exceptionVar, fullyQualified, member: member);

    /// <summary>
    /// Emits the two member-named FailFast catch arms (no preceding try-close brace; <see cref="EmitClose"/>
    /// already emitted it). Most-derived first, so the <see cref="System.OperationCanceledException"/>
    /// arm is reachable:
    /// <list type="number">
    /// <item><see cref="System.OperationCanceledException"/> → <c>FailFastAsyncWitnessCancellation</c>
    /// (cancellation is routine C# async control flow; a token wired into the conformance would raise
    /// it on normal cancellation, so it earns a dedicated message).</item>
    /// <item><see cref="System.Exception"/> → <c>FailFastAsyncWitnessException</c> (any other escape,
    /// including a deliberate throw from an <c>async throws</c> conformance).</item>
    /// </list>
    /// Both arms end in <c>throw;</c> for the same CS0161 reason documented on the type.
    /// </summary>
    private static void EmitAsyncWitnessFailFastCatch(CSharpWriter writer, string member,
        string exceptionVar, bool fullyQualified)
    {
        var marshaller = fullyQualified
            ? "global::Swift.Runtime.SwiftClosureMarshaller"
            : "SwiftClosureMarshaller";
        // The member descriptor is a C# string literal in the emitted diagnostic; escape any quote or
        // backslash so an unusual Swift identifier can't break out of the literal.
        var memberLiteral = member.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var oceVar = exceptionVar + "_oce";

        // Most-derived first: OperationCanceledException before Exception, else CS0160 (a later catch
        // can never be reached because an earlier catch clause catches it).
        writer.WriteLine($"catch (global::System.OperationCanceledException {oceVar})");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"{marshaller}.FailFastAsyncWitnessCancellation({oceVar}, \"{memberLiteral}\");");
        writer.WriteLine("throw;");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine($"catch (global::System.Exception {exceptionVar})");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"{marshaller}.FailFastAsyncWitnessException({exceptionVar}, \"{memberLiteral}\");");
        writer.WriteLine("throw;");
        writer.Indent--;
        writer.WriteLine("}");
    }
}
