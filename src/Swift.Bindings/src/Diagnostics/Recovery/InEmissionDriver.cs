// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
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
    private readonly Func<WrapperRecoveryCompileRequest, WrapperCompileDiagnostics> _compileWrapper;
    private readonly WrapperRecoveryCompileRequest _request;
    private readonly Action? _preRender;

    private readonly DeclEmissionStateSnapshot _declBaseline;
    private readonly ModuleEmissionStateSnapshot _contextBaseline;
    private readonly EmissionFactsJournal _outerJournal = new();

    /// <summary>
    /// The compilation result of the last render that compiled clean, or null until one does. The
    /// caller reads this after the controller converges to package the settled wrapper (stripped
    /// symbols, xcframework path) — the controller signature carries only the attribution, so the
    /// converged artifact travels through this side channel.
    /// </summary>
    public SwiftWrapperCompilationResult? LastConvergedOutcome { get; private set; }

    /// <summary>
    /// Captures the pristine pre-loop baseline. Must be constructed after all pre-emission setup and
    /// injectors have run and before the first render, so the baseline is the state a first render
    /// would have started from.
    /// </summary>
    public InEmissionDriver(
        ModuleDecl decl,
        ModuleEmissionContext context,
        ITypeDatabase typeDatabase,
        ILogger logger,
        Func<StringEmitter> newEmitter,
        Action rebuildCollaborators,
        Func<WrapperRecoveryCompileRequest, WrapperCompileDiagnostics> compileWrapper,
        WrapperRecoveryCompileRequest request,
        Action? preRender = null)
    {
        _decl = decl ?? throw new ArgumentNullException(nameof(decl));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _typeDatabase = typeDatabase ?? throw new ArgumentNullException(nameof(typeDatabase));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _newEmitter = newEmitter ?? throw new ArgumentNullException(nameof(newEmitter));
        _rebuildCollaborators = rebuildCollaborators ?? throw new ArgumentNullException(nameof(rebuildCollaborators));
        _compileWrapper = compileWrapper ?? throw new ArgumentNullException(nameof(compileWrapper));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _preRender = preRender;

        _declBaseline = DeclEmissionStateSnapshot.Capture(decl);
        _contextBaseline = ModuleEmissionStateSnapshot.Capture(context);
    }

    /// <inheritdoc />
    public AttributionResult? RenderCompileAttribute(IReadOnlySet<RecoveryUnitId> denylist)
    {
        ArgumentNullException.ThrowIfNull(denylist);

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

        var seed = WrapperDenylistSeed.Build(denylist);
        ContainedModuleEmission.Run(
            _decl, _context, _typeDatabase, _logger, _newEmitter,
            prepareRetry: _rebuildCollaborators, seed: seed, retainInto: _outerJournal);

        var diagnostics = _compileWrapper(_request);
        if (diagnostics.AllSlicesClean)
        {
            LastConvergedOutcome = diagnostics.Result;
            return null;
        }

        var steps = BuildProvenanceSteps(diagnostics);
        return new DiagnosticAttributor(steps).Attribute(diagnostics.Diagnostics);
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

    private static (RecoveryUnitId Unit, bool Droppable) Classify(ArtifactId artifact)
    {
        var kind = RecoveryUnitClassifier.FromArtifact(artifact.Role, artifact.Decl.Kind);
        var classification = RecoveryUnitClassifier.Classify(kind);
        var unit = classification.Scope switch
        {
            RecoveryScope.AccessorGroup => RecoveryUnitId.ForAccessorGroup(artifact.Decl),
            // These two scopes need a qualifier the artifact does not carry; FragmentOwners falls back
            // to the declaration's own leaf surface for them, and this must match so the units agree.
            RecoveryScope.ConformanceEdge or RecoveryScope.SharedHelperBundle =>
                RecoveryUnitId.Create(artifact.Decl, RecoveryScope.LeafApi),
            _ => RecoveryUnitId.Create(artifact.Decl, classification.Scope),
        };
        return (unit, classification.DroppableAlone);
    }

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
