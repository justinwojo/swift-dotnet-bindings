// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using BindingsGeneration.Diagnostics;

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// The per-render inputs the wrapper compile needs that the emission pass — not the command — holds:
/// the output directory the render wrote into, and the four collision facts the wrapper compile threads
/// into swiftc. They are stable across renders (computed once before emission), so the driver hands the
/// same request to every round; the compile delegate closes over the resolution, architecture and
/// search-path state that lives in the command.
/// </summary>
public sealed record WrapperRecoveryCompileRequest(
    string OutputDirectory,
    HashSet<string>? InternalTypeNames,
    string? ModuleNameForCollision,
    HashSet<string>? NestedTypesInCollidingClass,
    DepModuleCollisionDetector.SlicedCollisionResult DepModuleCollisions);

/// <summary>
/// The production <see cref="IWrapperRecoveryDriver"/>: renders the module under a denylist through
/// Gate 0, compiles the promised wrapper slices in collecting mode, and attributes the union of
/// failures to recovery units — one round of the verify-recover loop, driven by
/// <see cref="WrapperRecoveryController"/>.
/// </summary>
/// <remarks>
/// <para>
/// The two verification planes are independently optional, so the same driver serves every generation
/// mode. A mode that compiles a consumer-facing Swift wrapper as part of generation wires the wrapper
/// plane and (when the emitted C# is verifiable) the C# plane, and convergence is the joint fixed-point
/// over both. A mode that emits a verifiable binding csproj but has no in-generation wrapper compile to
/// hang a loop on — the Apple system-framework/direct path, whose wrapper is built from the on-device
/// SDK slice after emission returns — wires the C# plane alone: each round renders, verifies the
/// emitted C#, and withdraws the attributed member, converging when the C# compiles clean. At least
/// one plane must be wired; a driver with neither would "converge" on round 0 having verified nothing.
/// </para>
/// <para>
/// Each round restores the pristine pre-loop baseline before it renders, so a later seeded render is a
/// pure function of the denylist and never inherits an earlier render's stamps. Three mutation channels
/// neither snapshot covers are rewound explicitly: the decl tree and emission context via their
/// snapshots; the specialization engine and marshalling context by rebuilding them (they mutate in
/// place, so restoring the reference would put the tainted instance back); and the type database's
/// emission facts via an outer journal the settled render transfers its pre-images into instead of
/// committing, so the loop can undo them before the next render yet the settled render's stamps remain
/// on the records for finalization.
/// </para>
/// <para>
/// Attribution runs against the exact bytes swiftc compiled. Emission publishes the pre-strip fragment
/// map; the post-processor strips blocks (shifting lines) before the compile, so the driver remaps each
/// wrapper file's intervals onto the post-strip bytes captured by the compile. A file the simulator
/// guard pass rewrote after the strip carries no provenance for its inserted lines, so it is treated as
/// unmapped and left to the symbol/anchor and linker fallbacks — which, like every unit resolution
/// here, are gated so a culprit that is not droppable alone can never be handed to the controller as a
/// leaf. A non-droppable resolution becomes no resolution, which the controller reads as an
/// unattributed error and fails the module closed.
/// </para>
/// </remarks>
public sealed class InEmissionDriver : IWrapperRecoveryDriver
{
    private readonly ModuleDecl _decl;
    private readonly ModuleEmissionContext _context;
    private readonly ITypeDatabase _typeDatabase;
    private readonly ILogger _logger;
    private readonly Func<StringEmitter> _newEmitter;
    private readonly Action _rebuildCollaborators;
    // Null when this generation mode has no in-generation wrapper compile to verify against; the loop
    // then runs the C# plane alone (see the class remarks).
    private readonly Func<WrapperRecoveryCompileRequest, WrapperCompileDiagnostics>? _compileWrapper;
    private readonly WrapperRecoveryCompileRequest _request;
    private readonly Action? _preRender;
    private readonly Func<IReadOnlySet<RecoveryUnitId>, CSharpVerificationResult>? _verifyCsharp;

    private readonly DeclEmissionStateSnapshot _declBaseline;
    private readonly ModuleEmissionStateSnapshot _contextBaseline;
    private readonly EmissionFactsJournal _outerJournal = new();

    // Which verifier first named each denied unit, so the next round's Gate-0 seed reproduces the
    // correct withdrawal wording (Swift wrapper vs C# compile) for its tombstone and report row. The
    // denylist is one monotonic set shared across both planes; only the wording differs per unit.
    // First-writer-wins: a unit the Swift compile named stays a Swift withdrawal even if a later C#
    // error also lands on it.
    private readonly Dictionary<RecoveryUnitId, EmitterFaultOrigin> _unitOrigin = new();

    // Units withdrawn by the ingestion-quarantine closure, unioned into EVERY render's denylist so a
    // malformed-type dependent is tombstoned on every attempt regardless of what the compile loop
    // withdraws. Their origin is pre-seeded IngestionWithdrawal in _unitOrigin so the seed wording tells
    // the truth: they were removed because a malformed input node they depend on was quarantined at
    // ingestion, not because a compile rejected them. Empty on every healthy module.
    private readonly IReadOnlySet<RecoveryUnitId> _ingestionWithdrawals;

    // Render→compile probes the bounded bisection has spent across EVERY round of this module's loop. The
    // controller may consult the search once per unattributed round (up to the iteration cap), and each
    // search must not restart the budget — the mandate bounds probes to a single digit PER MODULE, not
    // per invocation. This accumulator caps the whole module's search cost: a round is handed only the
    // budget the earlier rounds left, and a round with none left declines without probing.
    private int _bisectionProbesUsed;

    /// <summary>
    /// The compilation result of the last render that compiled clean, or null until one does. The
    /// caller reads this after the controller converges to package the settled wrapper (stripped
    /// symbols, xcframework path) — the controller signature carries only the attribution, so the
    /// converged artifact travels through this side channel.
    /// </summary>
    public SwiftWrapperCompilationResult? LastConvergedOutcome { get; private set; }

    /// <summary>
    /// True when the loop converged because the module had no compilable wrapper surface at all (the
    /// no-wrapper-surface outcome), rather than because every promised slice compiled clean. The caller
    /// reads this to gate the usable-surface check (SWIFTBIND116) to exactly that degenerate path: a
    /// binding with a clean wrapper is usable by construction, so the check runs only when there is no
    /// wrapper to vouch for the surface.
    /// </summary>
    public bool NoWrapperSurfaceConverged { get; private set; }

    /// <summary>
    /// True only when the loop converged on a round in which the wired C# verifier actually ran and
    /// returned a <see cref="CSharpVerificationOutcome.Clean"/> verdict — the sole state that honestly
    /// proves the emitted C# compiled. It stays false when no C# verifier was wired, when convergence
    /// came from the no-wrapper-surface signal (the verifier never ran), and when a round-0 inconclusive
    /// verdict passed through to the post-generate publication gate (the verifier ran but reached no
    /// verdict). The publication ledger reads this — not the mere presence of a verifier delegate — so a
    /// C#-compile obligation is marked proven only when the compile was genuinely proven.
    /// </summary>
    public bool CSharpVerifiedClean { get; private set; }

    /// <summary>
    /// Captures the pristine pre-loop baseline. Must be constructed after all pre-emission setup and
    /// injectors have run and before the first render, so the baseline is the state a first render
    /// would have started from.
    /// </summary>
    /// <param name="compileWrapper">
    /// The Swift wrapper plane, or null for a generation mode with no in-generation wrapper compile.
    /// At least one of <paramref name="compileWrapper"/> and <paramref name="verifyCsharp"/> must be
    /// non-null.
    /// </param>
    public InEmissionDriver(
        ModuleDecl decl,
        ModuleEmissionContext context,
        ITypeDatabase typeDatabase,
        ILogger logger,
        Func<StringEmitter> newEmitter,
        Action rebuildCollaborators,
        Func<WrapperRecoveryCompileRequest, WrapperCompileDiagnostics>? compileWrapper,
        WrapperRecoveryCompileRequest request,
        Action? preRender = null,
        Func<IReadOnlySet<RecoveryUnitId>, CSharpVerificationResult>? verifyCsharp = null,
        IReadOnlySet<RecoveryUnitId>? ingestionWithdrawals = null)
    {
        _decl = decl ?? throw new ArgumentNullException(nameof(decl));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _typeDatabase = typeDatabase ?? throw new ArgumentNullException(nameof(typeDatabase));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _newEmitter = newEmitter ?? throw new ArgumentNullException(nameof(newEmitter));
        _rebuildCollaborators = rebuildCollaborators ?? throw new ArgumentNullException(nameof(rebuildCollaborators));
        // Neither plane wired would make every round "converge" without verifying anything — a loop that
        // silently certifies whatever it rendered. Refuse the construction rather than ship that.
        if (compileWrapper == null && verifyCsharp == null)
        {
            throw new ArgumentException(
                "The verify-recover driver needs at least one verification plane: pass a wrapper compile " +
                "delegate, a C# verifier, or both.", nameof(compileWrapper));
        }
        _compileWrapper = compileWrapper;
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _preRender = preRender;
        _verifyCsharp = verifyCsharp;
        _ingestionWithdrawals = ingestionWithdrawals ?? System.Collections.Immutable.ImmutableHashSet<RecoveryUnitId>.Empty;

        // Pre-seed the origin map so each render's Gate-0 seed spells an ingestion withdrawal as such,
        // not as a compile-driven one. First-writer-wins: if a compile plane later also names the unit,
        // it keeps the ingestion origin (RecordCulpritOrigins uses TryAdd).
        foreach (var unit in _ingestionWithdrawals)
            _unitOrigin[unit] = EmitterFaultOrigin.IngestionWithdrawal;

        _declBaseline = DeclEmissionStateSnapshot.Capture(decl);
        _contextBaseline = ModuleEmissionStateSnapshot.Capture(context);
    }

    // Unions the always-on ingestion withdrawals into a controller-supplied denylist. Returns the input
    // unchanged when there are none (the shape of every healthy module), so the common path allocates
    // nothing. Every render, C# verification, and bisection probe sees the ingestion units as denied.
    private IReadOnlySet<RecoveryUnitId> WithIngestionWithdrawals(IReadOnlySet<RecoveryUnitId> denylist)
    {
        if (_ingestionWithdrawals.Count == 0)
            return denylist;
        var unioned = new HashSet<RecoveryUnitId>(denylist);
        unioned.UnionWith(_ingestionWithdrawals);
        return unioned;
    }

    /// <inheritdoc />
    public AttributionResult? RenderCompileAttribute(IReadOnlySet<RecoveryUnitId> denylist)
    {
        ArgumentNullException.ThrowIfNull(denylist);
        denylist = WithIngestionWithdrawals(denylist);

        // Drop the previous render's wrapper source and thunk-assembly files before re-emitting.
        // Emission rewrites the single per-module wrapper .swift and per-arch .s wholesale WHEN it
        // emits them, but it does not prune them: a render that withdraws a module's last thunk-bearing
        // leaf never rewrites the .s, so the prior render's file would linger and the compiler would
        // re-collect the withdrawn symbol — defeating the withdrawal. Clearing first makes each
        // render's on-disk wrapper artifact set a pure function of that render.
        _preRender?.Invoke();

        // Reset the demangle reduction tally per render so the settled render's finalization reports
        // that render's misses, not the sum across every render the loop attempted.
        Demangling.ReductionDiagnostics.Reset();

        // Restore the pristine baseline before every render — including the first, where it is a
        // no-op — so the render is a pure function of the denylist. The three channels neither
        // snapshot covers are rewound in the order they must be: the context snapshot puts the
        // pre-emission engine/marshalling references back, the rebuild replaces them with fresh
        // instances (restoring alone would reinstate the tainted ones), and the outer journal undoes
        // the previous render's type-database stamps.
        _declBaseline.Restore();
        _contextBaseline.Restore();
        _rebuildCollaborators();
        _outerJournal.RestoreInto(_typeDatabase);

        var seed = WrapperDenylistSeed.Build(denylist, OriginOf);
        try
        {
            ContainedModuleEmission.Run(
                _decl, _context, _typeDatabase, _logger, _newEmitter,
                prepareRetry: _rebuildCollaborators, seed: seed, retainInto: _outerJournal);
        }
        catch (AbiContractViolationException abi)
        {
            // The ABI gate fired while this render settled — before any wrapper compile — so this round's
            // failure IS the ABI violation set, attributed through each violation's plan owner exactly as
            // the Swift and C# planes attribute a compile error: a droppable owner becomes a leaf culprit
            // the loop withdraws (its RecoveryStage reads AbiValidation via the seed wording), while a
            // null owner (a text-only backstop violation on a call no plan backs) or a non-droppable owner
            // becomes an unattributed error the controller reads as fail-closed. ContainedModuleEmission
            // has already rewound this render's type-database stamps, so the next round re-renders from the
            // pristine baseline. AbiValidationInvariantException is deliberately NOT caught — it is a
            // generator invariant failure that must escape the loop untouched (NonRecoverableFault).
            var abiAttribution = AttributeAbi(abi);
            RecordCulpritOrigins(abiAttribution, EmitterFaultOrigin.AbiRecoveryWithdrawal);
            return abiAttribution;
        }

        // No wrapper plane: the render is settled as far as Swift is concerned, so the C# verifier is the
        // whole round. There is no wrapper compilation result to hand the caller, so LastConvergedOutcome
        // stays null — this mode's wrapper (when it has one) is built after generation returns, from the
        // settled source, exactly as it is today without a loop.
        if (_compileWrapper == null)
            return VerifyCSharpPlane(denylist, wrapperResult: null);

        var diagnostics = _compileWrapper(_request);
        if (diagnostics.NoWrapperSurface)
        {
            // The module emitted no compilable wrapper surface at all — there is nothing to verify, so
            // this is convergence, not a failure to attribute. Whether a wrapper-less binding is
            // shippable is decided downstream from the emitted C# surface (the usable-surface gate),
            // which is the correct authority for "is there anything to ship" — not the wrapper compile.
            LastConvergedOutcome = diagnostics.Result;
            NoWrapperSurfaceConverged = true;
            return null;
        }
        if (!diagnostics.AllSlicesClean)
        {
            var steps = BuildProvenanceSteps(diagnostics);
            var swiftAttribution = new DiagnosticAttributor(steps).Attribute(diagnostics.Diagnostics);
            RecordCulpritOrigins(swiftAttribution, EmitterFaultOrigin.RecoveryWithdrawal);
            return swiftAttribution;
        }

        // The Swift wrapper is clean this round. With no C# verifier wired — the wave-1 Swift-only
        // loop and every legacy leg — that is convergence. Otherwise the emitted C# must ALSO compile
        // before the joint state is settled: a C# withdrawal removes the member's Swift wrapper too, so
        // the next round re-renders and re-verifies Swift first, and convergence requires both planes
        // clean in one round (the joint fixed-point).
        if (_verifyCsharp == null)
        {
            LastConvergedOutcome = diagnostics.Result;
            return null;
        }

        return VerifyCSharpPlane(denylist, diagnostics.Result);
    }

    // The C# plane of one round: verify the emitted C# for the render that just settled and turn the
    // verdict into this round's attribution. Shared by the joint (wrapper + C#) path, where it runs only
    // after the wrapper compiled clean, and the C#-only path, where it IS the round. <paramref
    // name="wrapperResult"/> is the converged wrapper artifact to publish through the side channel, or
    // null when no wrapper plane produced one.
    private AttributionResult? VerifyCSharpPlane(
        IReadOnlySet<RecoveryUnitId> denylist, SwiftWrapperCompilationResult? wrapperResult)
    {
        // A verifier throw is an infrastructure failure, not a C# verdict. The delegate extracts
        // metadata, probes native slices, re-emits the verification project, and runs an external
        // build — a command-runner timeout, a spawn failure, or an IO fault can throw from any of
        // those, and MsbuildSarifCSharpVerifier.Verify wraps its build only in a cleanup finally, so a
        // throw escapes rather than becoming a verdict. Fold it into an Inconclusive result so the same
        // policy below applies: a round-0 infra fault passes through to the post-generate publication
        // gate (SWIFTBIND114) instead of failing an otherwise healthy generation, while a fault after a
        // withdrawal still fails the module closed.
        CSharpVerificationResult csharp;
        try
        {
            csharp = _verifyCsharp!(denylist);
        }
        catch (Exception ex)
        {
            csharp = new CSharpVerificationResult(
                CSharpVerificationOutcome.Inconclusive,
                Array.Empty<CSharpCompileDiagnostic>(),
                $"C# verification could not run: {ex.GetType().Name}: {ex.Message}");
        }
        switch (csharp.Outcome)
        {
            case CSharpVerificationOutcome.Clean:
                LastConvergedOutcome = wrapperResult;
                CSharpVerifiedClean = true;
                return null;

            case CSharpVerificationOutcome.CompileErrors:
                var csharpAttribution = AttributeCSharp(csharp);
                RecordCulpritOrigins(csharpAttribution, EmitterFaultOrigin.CSharpRecoveryWithdrawal);
                return csharpAttribution;

            default:
                // Inconclusive: the verifier could not reach a verdict (a restore/infrastructure failure
                // or a verifier-internal error, never a genuine C# error — those are CompileErrors).
                // With nothing yet withdrawn this is a round-0 pass-through, identical to the
                // post-generate publication gate, which also lets an inconclusive verdict pass — so
                // converge and leave that gate the final say. But once the loop HAS withdrawn members,
                // an inconclusive verdict can no longer confirm the withdrawals were sound; shipping a
                // reduced binding on an unproven compile would be an over-withdrawal we cannot see, so
                // fail the module closed instead.
                if (denylist.Count == 0)
                {
                    _logger.LogWarning(
                        "SWIFTBIND114: C# verify-recover inconclusive ({Reason}); nothing has been " +
                        "withdrawn, so the loop passes through to the post-generate publication gate.",
                        csharp.InconclusiveReason);
                    LastConvergedOutcome = wrapperResult;
                    return null;
                }

                _logger.LogWarning(
                    "SWIFTBIND114: C# verify-recover inconclusive ({Reason}) after {Count} withdrawal(s); " +
                    "failing the module closed rather than shipping a reduced binding on an unproven C# compile.",
                    csharp.InconclusiveReason, denylist.Count);
                return InconclusiveFailClosed(csharp);
        }
    }

    /// <summary>
    /// The candidate pool the bounded bisection searches: every whole-scope artifact the current render
    /// emitted that is a withdrawable leaf — droppable alone and leaf-recoverable — and not already denied.
    /// Enumerated from the same FragmentSet and through the same classifier the attribution planes use, so
    /// the search can only ever withdraw a unit an attributed round could have withdrawn. On the wave-2
    /// production path no populated recovery graph gives a leaf any dependents, so each candidate is its own
    /// singleton needs-closure group. Exposed to the test assembly so a driver-level test can pick a culprit
    /// guaranteed to be in the pool the search will actually probe.
    /// </summary>
    internal IReadOnlyList<ImmutableArray<RecoveryUnitId>> BuildBisectionCandidateGroups(
        IReadOnlySet<RecoveryUnitId> denylist)
    {
        var fragments = _context.FragmentSet;
        if (fragments == null)
            return Array.Empty<ImmutableArray<RecoveryUnitId>>();

        var candidateGroups = new List<ImmutableArray<RecoveryUnitId>>();
        var seen = new HashSet<RecoveryUnitId>();
        foreach (var artifact in fragments.EmittedArtifacts)
        {
            var (unit, droppable) = Classify(artifact);
            if (!droppable)
                continue;
            if (!WrapperRecoveryController.IsLeafRecoverable(unit.Scope))
                continue;
            if (denylist.Contains(unit))
                continue;
            if (seen.Add(unit))
                candidateGroups.Add(ImmutableArray.Create(unit));
        }

        return candidateGroups;
    }

    /// <inheritdoc />
    public BisectionOutcome AttemptBisection(IReadOnlySet<RecoveryUnitId> denylist)
    {
        ArgumentNullException.ThrowIfNull(denylist);
        denylist = WithIngestionWithdrawals(denylist);

        // The search's soundness rests on a probe that withdrew the whole surface reading as NOT clean —
        // otherwise an error in shared scaffolding disappears along with everything else and the search
        // falsely confirms an innocent leaf. That vacuity signal comes from the wrapper compile
        // (NoWrapperSurface); with no wrapper plane there is no equivalent, since an emptied module still
        // yields C# that compiles perfectly. Decline rather than search on a verdict we cannot trust: an
        // unattributed round then fails the module closed, which is the outcome this mode has today.
        if (_compileWrapper == null)
            return BisectionOutcome.Declined();

        var candidateGroups = BuildBisectionCandidateGroups(denylist);
        if (candidateGroups.Count == 0)
            return BisectionOutcome.Declined();

        // Per-MODULE probe budget: hand this round only what earlier rounds' searches left unspent, so the
        // total render→compile probes across the whole loop stays single-digit. A round with the budget
        // already exhausted declines before probing rather than restarting it at DefaultProbeBudget.
        var remainingBudget = BoundedBisectionSearch.DefaultProbeBudget - _bisectionProbesUsed;
        if (remainingBudget < 1)
            return BisectionOutcome.Declined();

        // Each probe is a full render→compile under the base denylist unioned with a candidate subset;
        // "clean" means the whole round converged (RenderCompileAttribute returns null — Swift wrapper,
        // C#, and ABI all clean) AND the convergence was not vacuous (see ProbeClean below: a probe that
        // emptied the wrapper surface reads as NOT clean, the one deliberate divergence from the raw
        // verdict the controller reads). Probing reuses the real
        // render path, whose convergence side-channels and origin map would otherwise leak an
        // intermediate probe's state, so snapshot them and restore in a finally: a probe that converges
        // must not leave a stale NoWrapperSurface/CSharpVerifiedClean flag or a probe-attributed origin
        // behind. The controller's next real render — with the isolated units denied — re-establishes
        // every side-channel from the pristine baseline, byte-identically to the sufficiency probe.
        var savedLastConverged = LastConvergedOutcome;
        var savedNoWrapperSurface = NoWrapperSurfaceConverged;
        var savedCSharpVerified = CSharpVerifiedClean;
        var savedOrigins = new Dictionary<RecoveryUnitId, EmitterFaultOrigin>(_unitOrigin);

        BisectionOutcome outcome;
        try
        {
            bool ProbeClean(IReadOnlyCollection<RecoveryUnitId> subset)
            {
                var probeDenylist = new HashSet<RecoveryUnitId>(denylist);
                probeDenylist.UnionWith(subset);
                // RenderCompileAttribute returns null on two DISTINCT terminals: a genuine clean joint
                // compile, and a vacuous no-wrapper-surface convergence (this subset withdrew every
                // compilable member, so there was nothing left to compile). Only the former is evidence
                // the subset contained the culprit; counting a vacuous convergence as clean would let the
                // search falsely confirm an innocent leaf — a false confirmation, not a decline. Clear the
                // flag before the probe and reject the probe if the render came back having set it, so an
                // emptied-surface probe reads as NOT clean. The search then fails its containment/
                // sufficiency gate and declines (fail-closed) rather than isolating on a compile that never
                // ran. The finally below restores the flag to its pre-search value.
                NoWrapperSurfaceConverged = false;
                var converged = RenderCompileAttribute(probeDenylist) is null;
                return converged && !NoWrapperSurfaceConverged;
            }

            outcome = BoundedBisectionSearch.Run(candidateGroups, ProbeClean, remainingBudget);
        }
        finally
        {
            LastConvergedOutcome = savedLastConverged;
            NoWrapperSurfaceConverged = savedNoWrapperSurface;
            CSharpVerifiedClean = savedCSharpVerified;
            _unitOrigin.Clear();
            foreach (var (unit, origin) in savedOrigins)
                _unitOrigin[unit] = origin;
        }

        // Charge this round's probes to the per-module accumulator (BisectionOutcome carries the count for
        // both an isolation and a decline), so a later round sees only the budget that remains.
        _bisectionProbesUsed += outcome.ProbesUsed;

        // Stamp the confirmed culprits as search-isolated (done after the side-channel restore, so only
        // the real isolated units carry this origin). The controller's next render seeds them through
        // OriginOf → the bounded-bisection withdrawal wording, and SkipCauseClassifier caps their skip
        // row at Medium confidence — distinct from an attributed withdrawal, as the design requires.
        if (outcome.DidIsolate)
        {
            foreach (var unit in outcome.Isolated)
                _unitOrigin[unit] = EmitterFaultOrigin.BisectionIsolatedWithdrawal;
        }

        return outcome;
    }

    // The origin each denied unit was first attributed under, defaulting to the Swift-wrapper wording
    // for any unit not yet recorded (the wave-1 Swift-only path never records, so it always defaults).
    private EmitterFaultOrigin OriginOf(RecoveryUnitId unit) =>
        _unitOrigin.TryGetValue(unit, out var origin) ? origin : EmitterFaultOrigin.RecoveryWithdrawal;

    // Records the verifier that named each fresh culprit, first-writer-wins so a unit keeps the plane
    // that first withdrew it. Marking every culprit (not just the not-yet-denied ones) is harmless: an
    // already-recorded unit's TryAdd no-ops, and the origin it keeps is the one that first named it.
    private void RecordCulpritOrigins(AttributionResult attribution, EmitterFaultOrigin origin)
    {
        foreach (var unit in attribution.Culprits)
            _unitOrigin.TryAdd(unit, origin);
    }

    // Attributes a failed C# compile through the C#-plane interval map — the emitted syntax tiling, so
    // a diagnostic's line/column lands on the exact member fragment whose owner carries the same
    // recovery unit the Swift loop withdraws. A diagnostic the map cannot resolve (positionless, an
    // unmapped file, or shared scaffolding rather than a member) falls through to no resolution, which
    // the controller reads as an unattributed error and fails the module closed — the sound default
    // until a coarse-scope authorization (a later session) can widen safely.
    private AttributionResult AttributeCSharp(CSharpVerificationResult csharp)
    {
        var groups = csharp.CompilerErrors.Select(ToDiagnosticGroup).ToList();
        var steps = BuildCSharpProvenanceSteps();
        return new DiagnosticAttributor(steps).Attribute(groups);
    }

    // Attributes an ABI-contract failure through each violation's plan owner. There is no interval map to
    // consult — a typed violation already carries the declaring artifact the plan recorded — so this
    // resolves owners directly rather than through the provenance ladder, then applies the SAME droppable
    // gate the Swift/C# planes apply: a violation whose owner is droppable-alone becomes a leaf culprit;
    // one with no owner (a text-only backstop violation on a call no plan backs) or a non-droppable owner
    // becomes an unattributed error the controller reads as fail-closed. Culprits are deduplicated by
    // unit, first-seen order, matching the batched one-increment-per-unit contract.
    // Internal (not private) so the ABI-plane loop integration can be pinned directly: this is the exact
    // transform whose output the WrapperRecoveryController consumes, and a test drives the real controller
    // with it rather than re-deriving the mapping.
    internal static AttributionResult AttributeAbi(AbiContractViolationException abi)
    {
        var decisions = ImmutableArray.CreateBuilder<AttributedDiagnostic>();
        var culprits = ImmutableArray.CreateBuilder<RecoveryUnitId>();
        var seenCulprits = new HashSet<RecoveryUnitId>();

        foreach (var attributed in abi.Attributed)
        {
            var group = new DiagnosticGroup
            {
                Primary = CompilerDiagnostic.Global(
                    DiagnosticSeverity.Error, attributed.Violation.Describe()),
            };

            if (attributed.Owner is { } owner)
            {
                var (unit, droppable) = Classify(owner);
                if (droppable)
                {
                    decisions.Add(new AttributedDiagnostic
                    {
                        Diagnostic = group,
                        Kind = AttributionKind.Unit,
                        Artifact = owner,
                        Unit = unit,
                        Source = ProvenanceSource.None,
                    });
                    if (seenCulprits.Add(unit))
                        culprits.Add(unit);
                    continue;
                }
            }

            // No owner, or an owner not droppable alone: an unattributed error → the controller fails the
            // module closed, the sound default (ABI violations have always been terminal).
            decisions.Add(new AttributedDiagnostic
            {
                Diagnostic = group,
                Kind = AttributionKind.Unattributed,
                Source = ProvenanceSource.None,
            });
        }

        // Fingerprint the ABI plane through the SAME FNV-1a hash the Swift and C# planes use
        // (DiagnosticFingerprint.Compute over the error groups: paths elided, whitespace collapsed,
        // sorted multiset), under an "abi:" plane discriminator so an ABI failure can never share a
        // fingerprint with a Swift/C# failure of the same normalized text. Position-independent by
        // construction: Describe() carries no line/column, only rule + member + symbol + explanation,
        // so the hash is stable across renders of the same failure.
        var groups = decisions.Select(d => d.Diagnostic).ToList();
        var fingerprint = "abi:" + DiagnosticFingerprint.Compute(groups);

        return new AttributionResult
        {
            Diagnostics = decisions.ToImmutable(),
            Culprits = culprits.ToImmutable(),
            Fingerprint = fingerprint,
        };
    }

    private static DiagnosticGroup ToDiagnosticGroup(CSharpCompileDiagnostic diagnostic) => new()
    {
        Primary = new CompilerDiagnostic
        {
            File = diagnostic.FilePath,
            Line = diagnostic.Line,
            // Roslyn/SARIF report UTF-16 character columns, which the C#-plane interval map resolves
            // directly (unlike swiftc's UTF-8 byte columns) — see CSharpIntervalMapProvenanceStep.
            Column = diagnostic.Column,
            Severity = DiagnosticSeverity.Error,
            Message = string.IsNullOrEmpty(diagnostic.Id)
                ? diagnostic.Message
                : $"{diagnostic.Id}: {diagnostic.Message}",
        },
    };

    private IReadOnlyList<IProvenanceStep> BuildCSharpProvenanceSteps()
    {
        var steps = new List<IProvenanceStep>();

        // The current render's published map. On the loop path no post-publish C# rewrite intervenes,
        // so its intervals describe the exact on-disk bytes MSBuild compiled. Gated so a hit whose
        // artifact is not droppable alone becomes no resolution → fail closed.
        var fragments = _context.FragmentSet;
        if (fragments != null)
            steps.Add(new DroppableGate(new CSharpIntervalMapProvenanceStep(fragments)));

        return steps;
    }

    // Synthesizes a global input-configuration failure the controller reads as a fail-closed cause,
    // used when the C# verifier is inconclusive AFTER at least one withdrawal — the loop cannot prove
    // its reduction sound, so it must not converge.
    private static AttributionResult InconclusiveFailClosed(CSharpVerificationResult csharp)
    {
        var reason = string.IsNullOrEmpty(csharp.InconclusiveReason)
            ? "C# verification could not reach a verdict"
            : csharp.InconclusiveReason;

        var group = new DiagnosticGroup
        {
            Primary = CompilerDiagnostic.Global(
                DiagnosticSeverity.Error,
                $"C# verification inconclusive after withdrawals: {reason}"),
        };

        var decision = new AttributedDiagnostic
        {
            Diagnostic = group,
            Kind = AttributionKind.Classification,
            Owner = CauseOwner.InputConfiguration,
            ClassificationDetail = reason,
            Source = ProvenanceSource.None,
        };

        return new AttributionResult
        {
            Diagnostics = ImmutableArray.Create(decision),
            Culprits = ImmutableArray<RecoveryUnitId>.Empty,
            Fingerprint = "csharp-inconclusive",
        };
    }

    private IReadOnlyList<IProvenanceStep> BuildProvenanceSteps(WrapperCompileDiagnostics diagnostics)
    {
        var steps = new List<IProvenanceStep>();

        var remapped = BuildRemappedFragmentSet(diagnostics.FileProvenance);
        if (remapped != null)
            steps.Add(new DroppableGate(new IntervalMapProvenanceStep(remapped)));

        var blockIndex = BuildBlockIndex(diagnostics.FileProvenance);
        if (blockIndex != null)
            steps.Add(new DroppableGate(new SymbolAnchorProvenanceStep(blockIndex, SymbolLookup, UnitLookup)));

        steps.Add(new DroppableGate(new LinkerSymbolProvenanceStep(SymbolLookup, UnitLookup)));
        return steps;
    }

    // Rebuilds the wrapper fragment map over the exact post-strip bytes swiftc saw. The published set
    // is the pre-strip render map; each wrapper file's intervals are remapped onto its post-strip
    // content using the line-origin vector captured before the staging tree was dropped. A file the
    // guard pass rewrote, or one whose remap does not tile exactly, is left out — an approximate map is
    // worse than none, because the interval step cannot tell an exact hit from a shifted one.
    private ModuleFragmentSet? BuildRemappedFragmentSet(IReadOnlyList<WrapperFileProvenance> provenance)
    {
        var published = _context.FragmentSet;
        if (published == null || provenance.Count == 0)
            return null;

        var remapped = new ModuleFragmentSet { ModuleName = published.ModuleName };
        var mappedAny = false;

        foreach (var file in provenance)
        {
            if (file.GuardRewrote || file.CleanedLineSources == null)
                continue;
            if (!published.Files.TryGetValue(file.FileName, out var preStripMap))
                continue;

            var intervals = WrapperStripRemap.Remap(
                preStripMap.Intervals, file.PreStripContent, file.PostStripContent, file.CleanedLineSources);
            if (intervals == null)
                continue;

            remapped.Add(file.FileName, file.PostStripContent, intervals);
            if (remapped.Files.ContainsKey(file.FileName))
                mappedAny = true;
        }

        return mappedAny ? remapped : null;
    }

    // Builds the block index from the compiled wrapper text for the symbol/anchor fallback. Only files
    // whose compiled bytes are the post-strip content are usable: a guard-rewritten file's inserted
    // lines shift every position after them, so resolving a diagnostic against its post-strip text
    // would name the wrong block. Such files are skipped, leaving their positioned diagnostics to fail
    // closed rather than mis-attribute.
    private static WrapperBlockIndex? BuildBlockIndex(IReadOnlyList<WrapperFileProvenance> provenance)
    {
        var wrapperFile = provenance.FirstOrDefault(f => !f.GuardRewrote);
        return wrapperFile == null ? null : WrapperBlockIndex.Build(wrapperFile.PostStripContent);
    }

    private ArtifactId? SymbolLookup(string symbol) =>
        _context.TryGetWrapperSymbolOwner(symbol, out var artifact) ? artifact : null;

    // Resolves an artifact to the same recovery unit its fragment owner would carry, so a symbol/anchor
    // hit names the unit the interval map would have. Wave-1 has no populated recovery graph, so the
    // unit is derived from the artifact's own classification rather than a dependency lookup — the same
    // mapping FragmentOwners uses when it stamps the owner during emission.
    private static RecoveryUnitId? UnitLookup(ArtifactId artifact) => Classify(artifact).Unit;

    // The artifact→(unit, droppable) resolution lives in RecoveryUnitClassifier so the strip-to-withdrawal
    // classifier reads the identical mapping and the two sides can never disagree on a symbol's unit.
    private static (RecoveryUnitId Unit, bool Droppable) Classify(ArtifactId artifact) =>
        RecoveryUnitClassifier.ClassifyArtifact(artifact);

    /// <summary>
    /// Wraps a provenance step so a hit whose artifact is not droppable alone resolves to nothing. The
    /// controller withdraws by scope, and an unmodelled artifact classifies as a nominal leaf while not
    /// being droppable; handing it over would tombstone something whose removal shifts a retained
    /// sibling's layout. Suppressing the hit turns it into an unattributed error, which fails the
    /// module closed — the sound outcome when the only recovery available is a leaf withdrawal that is
    /// not actually safe.
    /// </summary>
    private sealed class DroppableGate : IProvenanceStep
    {
        private readonly IProvenanceStep _inner;

        public DroppableGate(IProvenanceStep inner) => _inner = inner;

        public bool TryResolve(CompilerDiagnostic diagnostic, out ProvenanceHit hit)
        {
            if (!_inner.TryResolve(diagnostic, out hit))
                return false;

            if (!Classify(hit.Artifact).Droppable)
            {
                hit = default;
                return false;
            }

            return true;
        }
    }
}
