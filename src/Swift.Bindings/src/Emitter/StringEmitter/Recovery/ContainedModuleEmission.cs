// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits a module under containment: a declaration the emitter throws on is denied and the module is
/// re-emitted without it, instead of one bad declaration taking the whole binding down.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism rests on emission being a pure function of the frozen type database, the declaration
/// tree and the denylist. Given that, re-running with one more denied declaration produces exactly
/// what a first run would have produced had that declaration never been supported — so a contained
/// fault is indistinguishable, in the output, from any other refusal. That is why the denial flows
/// through the ordinary skip channel rather than a private one.
/// </para>
/// <para>
/// Purity is not free, and this is where it is bought. Three layers accumulate state during a pass
/// and every one is rewound before a retry: the declaration tree (emission stamps, wrapper-strategy
/// flags, destructive signature edits), the emission context (dedup sets, emitted-once latches,
/// wrapper symbol registries), and the frozen database's emission facts. The conductor and its
/// handler factories are not rewound but rebuilt, by constructing a fresh emitter per attempt —
/// cheaper than auditing their internals, and it cannot miss a field.
/// </para>
/// <para>
/// Attempts are capped. A run that keeps discovering new faults is not converging on a shippable
/// module, and failing loudly beats shipping a binding that silently lost an unknown share of its
/// surface.
/// </para>
/// </remarks>
internal static class ContainedModuleEmission
{
    /// <summary>
    /// Runs emission to a settled attempt and returns the declarations that had to be denied — empty
    /// for every healthy module.
    /// </summary>
    /// <param name="decl">The module to emit. Mutated during emission and rewound between attempts.</param>
    /// <param name="emissionContext">
    /// The module's emission context. Reused across attempts rather than rebuilt, because its
    /// pre-emission state includes injector output (protocol-extension and foreign-extension wrapper
    /// lines, and the wrapper symbols those synthetic members already claimed) that is baked into the
    /// declaration tree and is not re-derived on a retry.
    /// </param>
    /// <param name="typeDatabase">The frozen database whose emission facts are journalled and rewound.</param>
    /// <param name="logger">Receives one warning per contained fault.</param>
    /// <param name="newEmitter">
    /// Builds the emitter for one attempt. Called once per attempt so the conductor and its handler
    /// factories — which hold per-module dedup state with no reset path — start clean.
    /// </param>
    /// <param name="prepareRetry">
    /// Rebuilds per-attempt collaborators that live outside the emission context (the specialization
    /// engine and marshalling context). Invoked only on retries, after the context is restored, since
    /// restoring would otherwise put the tainted instances back.
    /// </param>
    /// <param name="seed">
    /// Declarations to deny from the first attempt. Production passes none; tests use it to produce
    /// the reference render a contained run is required to match.
    /// </param>
    /// <param name="retainInto">
    /// When the verify-recover loop drives repeated renders, the outer journal the settled attempt's
    /// type-database pre-images are handed to instead of being committed. The settled render's stamps
    /// stay on the records (they are what the wrapper compile sees), but the loop can undo them by
    /// restoring this journal before its next render — so a later seeded render emits from the true
    /// pre-loop baseline rather than from a database still carrying an earlier render's stamps. Null on
    /// the ordinary single-render path, where the settled attempt commits (discards) its journal as before.
    /// </param>
    /// <exception cref="EmitterFaultLimitException">Faults were still being discovered at the cap.</exception>
    public static EmitterPoisonList Run(
        ModuleDecl decl,
        ModuleEmissionContext emissionContext,
        ITypeDatabase typeDatabase,
        ILogger logger,
        Func<StringEmitter> newEmitter,
        Action? prepareRetry = null,
        EmitterPoisonList? seed = null,
        EmissionFactsJournal? retainInto = null)
    {
        ArgumentNullException.ThrowIfNull(decl);
        ArgumentNullException.ThrowIfNull(emissionContext);
        ArgumentNullException.ThrowIfNull(typeDatabase);
        ArgumentNullException.ThrowIfNull(newEmitter);

        var poison = seed ?? new EmitterPoisonList();
        var declSnapshot = DeclEmissionStateSnapshot.Capture(decl);
        var contextSnapshot = ModuleEmissionStateSnapshot.Capture(emissionContext);

        for (var attemptNumber = 1; ; attemptNumber++)
        {
            using var attempt = EmissionAttempt.Begin(poison);

            if (attemptNumber > 1)
            {
                declSnapshot.Restore();
                contextSnapshot.Restore();
                prepareRetry?.Invoke();
            }

            // Stale references from a previous module — or from a discarded attempt at this one —
            // must not leak into the emitted csproj.
            AppleSupplementReferences.Reset();

            // Nothing records report rows before emission, so restarting the session per attempt
            // drops only the discarded attempt's own rows.
            ReportCollector.Reset();
            ReportCollector.Start(decl);

            var abandoned = false;
            try
            {
                newEmitter().EmitModule(decl, emissionContext);
            }
            catch (EmissionAttemptAbandoned)
            {
                abandoned = true;
            }
            catch
            {
                // Any other fault — including the fail-closed ABI gates (AbiContractViolationException,
                // AbiValidationInvariantException) EmitModule throws after the whole render settles —
                // leaves this attempt's type-database emission stamps applied, so they must be rewound
                // before the exception escapes or the next render emits from a dirty database. The two ABI
                // gates then diverge above this frame: the verify-recover loop CATCHES
                // AbiContractViolationException and re-renders with the culprit withdrawn, while
                // AbiValidationInvariantException is deliberately NOT caught — it is a generator invariant
                // failure that escapes the loop untouched (NonRecoverableFault). The rewind here applies to
                // both regardless, since a rethrow past a dirty database would corrupt whatever runs next.
                // Dispose only pops the ambient scope; it does not undo the journal, so rewind explicitly.
                attempt.Journal.RestoreInto(typeDatabase);
                throw;
            }

            // The flag, not just the signal. Abandonment travels through emitter code that catches
            // broadly in places, so a normal return does not by itself prove the attempt was clean;
            // the flag is set before the signal is ever thrown.
            if (!abandoned && !attempt.Abandoned)
            {
                if (retainInto != null)
                    attempt.Journal.TransferTo(retainInto);
                else
                    attempt.Journal.Commit();

                return poison;
            }

            attempt.Journal.RestoreInto(typeDatabase);

            if (attemptNumber >= EmissionAttempt.MaxEmissionAttempts)
                throw new EmitterFaultLimitException(decl.Name, attemptNumber, poison.Faults);

            var newest = poison.Faults[^1];
            logger.LogWarning(
                "SWIFTBIND110: emitter threw while lowering {Declaration}; denying it and re-emitting " +
                "{Module} (attempt {Next} of {Max}). {Details}",
                newest.Subject.Canonical, decl.Name, attemptNumber + 1,
                EmissionAttempt.MaxEmissionAttempts, newest.Details);
        }
    }
}
