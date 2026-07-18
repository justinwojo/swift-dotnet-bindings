// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Wraps one unit of emission dispatch so an unhandled exception inside it poisons that unit instead
/// of failing the whole module.
/// </summary>
/// <remarks>
/// <para>
/// A seam always abandons the attempt on the first fault; it never records the fault and carries on
/// with the next sibling. Continuing would mean emitting into writers whose text has been rolled back
/// but whose side effects have not — module-shared Swift helper registrations, dedup sets, wrapper
/// symbol registries and declaration stamps have no undo. The next sibling to throw would then be
/// blamed for damage the first one did, and an innocent declaration would be tombstoned permanently
/// on the clean re-run. Faults are collected across attempts instead, one per attempt, which costs a
/// few seconds of re-emission per fault and buys correct attribution.
/// </para>
/// <para>
/// Nested seams are why <see cref="EmissionAttemptAbandoned"/> is rethrown untouched: a fault inside a
/// nested type surfaces at that type's own seam, and the enclosing seam must not re-blame it on the
/// parent.
/// </para>
/// <para>
/// The abandonment signal travels through a great deal of emitter code, some of which catches broadly
/// (<c>catch { }</c> around optional probes, validators that answer "unsupported" for anything that
/// throws). Containment therefore does not depend on the signal surviving: the fault is recorded on the
/// attempt <em>before</em> the signal is thrown, every seam re-raises on entry once the attempt is
/// marked, and the attempt loop re-checks the flag even when emission returns normally. A swallowed
/// signal costs the rest of one discarded pass; it cannot ship a tainted attempt, and re-raising at the
/// next seam keeps a fault caused by already-tainted state from being blamed on an innocent sibling.
/// </para>
/// </remarks>
internal static class EmissionSeam
{
    /// <summary>
    /// Runs <paramref name="emit"/> under containment for <paramref name="subject"/>.
    /// </summary>
    /// <param name="subject">The declaration being emitted.</param>
    /// <param name="scope">How much has to be withdrawn if this throws.</param>
    /// <param name="escalation">
    /// The wider unit to deny if <paramref name="subject"/> throws again after already being denied.
    /// </param>
    /// <param name="emit">The dispatch to contain.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="emit"/> ran to completion, <see langword="false"/> if
    /// the declaration was denied. Callers that keep "this one emitted" bookkeeping outside the lambda
    /// — a dedup-name reservation, an emitted-member counter, a <c>RecordMemberEmitted</c> row — MUST
    /// gate it on this, because a denial returns normally rather than unwinding. Claiming a C# name for
    /// a declaration that emitted nothing pushes an innocent sibling that projects to the same name out
    /// as a duplicate, so one faulted member silently costs two.
    /// </returns>
    public static bool Guard(BaseDecl subject, RecoveryScope scope, BaseDecl? escalation, Action emit)
    {
        ArgumentNullException.ThrowIfNull(subject);

        DeclId capturedSubject = DeclIdFactory.ForDecl(subject)
            ?? throw new ArgumentException(
                $"No stable identity exists for a {subject.GetType().Name}, so a fault in it could not be denied on a retry.",
                nameof(subject));
        DeclId? escalationId = DeclIdFactory.ForDecl(escalation);

        if (EmissionAttempt.Current is { Abandoned: true })
        {
            // A previous seam's signal was swallowed by an intervening broad catch. Everything from
            // here on runs against state the abandoned unit already touched, so emitting further is
            // both wasted and actively misleading.
            throw new EmissionAttemptAbandoned();
        }

        // Gate 0, applied where the containment unit is actually defined. The emitter reaches its
        // seams through several independent validation paths — the member pipeline, the gate
        // evaluator, the type-skip conditions, the standalone member validator — and no one of them
        // covers every seam. Checking denial at each of those instead would leave whichever path
        // was missed dispatching a declaration that is already known to throw: it faults a second
        // time, finds itself already denied, and escalates, withdrawing a whole type over a fault
        // that only ever warranted withdrawing one member. Here it cannot be missed.
        if (EmissionAttempt.TryGetFault(capturedSubject, out var denial))
        {
            RecordDenial(subject, denial.Details);
            return false;
        }

        try
        {
            EmitterFaultInjector.MaybeThrow(capturedSubject);
            emit();
        }
        catch (EmissionAttemptAbandoned)
        {
            // An inner seam already owns this fault and recorded it against the right declaration.
            throw;
        }
        catch (Exception exception) when (!NonRecoverableFault.Test(exception))
        {
            var attempt = EmissionAttempt.Current;
            if (attempt is null)
            {
                // No attempt loop above us — unit tests and tools that drive emitters directly. There
                // is nothing to retry into, so the caller keeps the exception it would have seen.
                throw;
            }

            var fault = EmitterFaultRecord.From(capturedSubject, scope, exception, escalationId);
            if (!attempt.RecordFault(fault))
            {
                // Already denied and nothing wider to deny. Containment has nothing left to try.
                throw;
            }

            throw new EmissionAttemptAbandoned();
        }

        return true;
    }

    /// <summary>
    /// Reports whether <paramref name="subject"/> is already denied, recording the skip row if it is.
    /// </summary>
    /// <remarks>
    /// For the call sites that reserve a dedup key, a collision slot or a signature <em>before</em>
    /// reaching the seam. <see cref="Guard"/>'s own Gate 0 fires too late for those: by the time it
    /// denies, the declaration has already claimed a C# name it will never emit under, and the sibling
    /// that projects to the same name is dropped as a duplicate. Call this at the top of the iteration
    /// and skip the declaration entirely when it answers <see langword="true"/> — the seam's Gate 0
    /// stays as the backstop for every site that has nothing to reserve.
    /// </remarks>
    /// <param name="subject">The declaration about to be emitted.</param>
    /// <param name="tombstoneWriter">
    /// Where to leave the <c>// Unsupported:</c> comment, for the call sites whose other skip paths
    /// leave one. Denying ahead of the validator gates means skipping the gate that would otherwise
    /// have written it, so the comment has to be emitted here or the member vanishes from the
    /// generated source with nothing to grep for. Omit it at sites that do not comment their skips.
    /// </param>
    public static bool TryDenyUpFront(BaseDecl subject, CSharpWriter? tombstoneWriter = null)
    {
        ArgumentNullException.ThrowIfNull(subject);

        DeclId? capturedSubject = DeclIdFactory.ForDecl(subject);
        if (capturedSubject is null)
        {
            // No stable identity, so nothing could have been denied against it. Guard raises on the
            // same condition; leaving that to it keeps one error path instead of two.
            return false;
        }

        if (!EmissionAttempt.TryGetFault(capturedSubject.Value, out var denial))
        {
            return false;
        }

        RecordDenial(subject, denial.Details);

        if (tombstoneWriter is not null)
        {
            BindingItemKind? kind = subject switch
            {
                MethodDecl => BindingItemKind.Method,
                PropertyDecl => BindingItemKind.Property,
                SubscriptDecl => BindingItemKind.Subscript,
                OperatorDecl => BindingItemKind.Operator,
                _ => null,
            };

            if (kind is not null)
            {
                UnsupportedCommentEmitter.EmitMemberSkipped(
                    tombstoneWriter,
                    subject.Name,
                    kind.Value,
                    SkipReason.EmitterFault,
                    denial.Details,
                    containingDecl: subject.ParentDecl);
            }
        }

        return true;
    }

    /// <summary>
    /// Records the skip row for a declaration refused because an earlier attempt threw on it. The
    /// tombstone comment in the generated source is emitted by whichever gate precedes this seam;
    /// several seams have no such gate, and this keeps those from vanishing without a record.
    /// </summary>
    private static void RecordDenial(BaseDecl subject, string details)
    {
        switch (subject)
        {
            case TypeDecl typeDecl:
                ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.EmitterFault, details);
                break;
            case MethodDecl methodDecl:
                ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.EmitterFault, details);
                break;
            case PropertyDecl propertyDecl:
                ReportCollector.RecordMemberSkipped(propertyDecl, SkipReason.EmitterFault, details);
                break;
            case SubscriptDecl subscriptDecl:
                ReportCollector.RecordMemberSkipped(subscriptDecl, SkipReason.EmitterFault, details);
                break;
            case OperatorDecl operatorDecl:
                ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.EmitterFault, details);
                break;
        }
    }
}
