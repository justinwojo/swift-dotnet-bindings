// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>
/// Assembles a <see cref="RecoveryGraph"/> and refuses to produce one that violates the model's
/// invariants.
/// </summary>
/// <remarks>
/// <para>
/// Two rules do the real work. Every artifact is attached to exactly one unit — attaching the same
/// artifact twice throws, so "which unit owns this" can never have two answers. And every unit names
/// an escalation parent that is either coarser or, for a nested type, its enclosing type, with module
/// units alone having none, so an escalation walk climbs a finite chain and provably ends at the
/// module.
/// </para>
/// <para>
/// Ownership is stated by the caller rather than inferred from the artifact's declaration, because
/// the two differ exactly where it matters most: a frozen struct's stored field is generated for the
/// property declaration but belongs to the <em>type's</em> representation, and guessing the property
/// would let the bytes be withdrawn with the accessors.
/// </para>
/// </remarks>
public sealed class RecoveryGraphBuilder
{
    private sealed class PendingUnit
    {
        public required RecoveryUnitId Id { get; init; }
        public RecoveryUnitId? EscalationParent { get; set; }
        public AbiFootprint Footprint { get; set; }
        public bool ContributesToParentLayout { get; set; }
        public List<ArtifactId> Artifacts { get; } = new();
        public List<RecoveryUnitId> Requires { get; } = new();
    }

    private readonly Dictionary<RecoveryUnitId, PendingUnit> _units = new();
    private readonly Dictionary<ArtifactId, RecoveryUnitId> _artifactOwners = new();

    /// <summary>
    /// Declares the module unit — the escalation terminus every chain ends at.
    /// </summary>
    public RecoveryUnitId DeclareModule(DeclId moduleDecl)
    {
        var id = RecoveryUnitId.Create(moduleDecl, RecoveryScope.Module);
        GetOrAdd(id, parent: null, allowNullParent: true);
        return id;
    }

    /// <summary>
    /// Declares a unit at <paramref name="scope"/> owned by <paramref name="owner"/>, escalating to
    /// <paramref name="escalationParent"/>. Idempotent: re-declaring an existing unit with the same
    /// parent is a no-op, with a different parent throws.
    /// </summary>
    /// <remarks>
    /// Two scopes need a key this overload cannot supply — a shared helper is identified by its bundle
    /// and a conformance edge by its protocol — so they have dedicated entry points and are rejected
    /// here rather than silently collapsing every bundle in a module onto one id.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The parent cannot be escalated to from the unit, the scope needs a dedicated declarer, or the
    /// declaration contradicts an earlier one.
    /// </exception>
    public RecoveryUnitId DeclareUnit(
        DeclId owner,
        RecoveryScope scope,
        RecoveryUnitId escalationParent,
        bool contributesToParentLayout = false)
    {
        if (scope == RecoveryScope.Module)
            throw new ArgumentException(
                "The module scope is the escalation terminus; declare it with DeclareModule.", nameof(scope));
        if (scope == RecoveryScope.SharedHelperBundle)
            throw new ArgumentException(
                "A shared-helper bundle is identified by its bundle key; declare it with DeclareSharedHelper.",
                nameof(scope));
        if (scope == RecoveryScope.ConformanceEdge)
            throw new ArgumentException(
                "A conformance edge is identified by its protocol; declare it with DeclareConformanceEdge.",
                nameof(scope));

        // An accessor-level DeclId names one half of a group the model treats as indivisible, so the
        // id is normalized through the same factory a caller would use to look it up — otherwise a
        // getter and a setter declare two units that the factory then fails to find either of.
        var id = scope == RecoveryScope.AccessorGroup
            ? RecoveryUnitId.ForAccessorGroup(owner)
            : RecoveryUnitId.Create(owner, scope);

        return Declare(id, scope, escalationParent, contributesToParentLayout);
    }

    /// <summary>
    /// Declares the unit owning one shared-helper bundle, identified by <paramref name="bundleKey"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The key is blank, or the declaration is contradictory.</exception>
    public RecoveryUnitId DeclareSharedHelper(
        DeclId moduleDecl,
        string bundleKey,
        RecoveryUnitId escalationParent,
        bool contributesToParentLayout = false) =>
        Declare(
            RecoveryUnitId.ForSharedHelper(moduleDecl, bundleKey),
            RecoveryScope.SharedHelperBundle,
            escalationParent,
            contributesToParentLayout);

    /// <summary>
    /// Declares the unit owning <paramref name="conformerDecl"/>'s conformance to
    /// <paramref name="protocolDecl"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The declaration is contradictory.</exception>
    public RecoveryUnitId DeclareConformanceEdge(
        DeclId conformerDecl,
        DeclId protocolDecl,
        RecoveryUnitId escalationParent,
        bool contributesToParentLayout = false) =>
        Declare(
            RecoveryUnitId.ForConformanceEdge(conformerDecl, protocolDecl),
            RecoveryScope.ConformanceEdge,
            escalationParent,
            contributesToParentLayout);

    private RecoveryUnitId Declare(
        RecoveryUnitId id,
        RecoveryScope scope,
        RecoveryUnitId escalationParent,
        bool contributesToParentLayout)
    {
        if (!RecoveryScopeLattice.CanEscalateTo(scope, escalationParent.Scope))
            throw new ArgumentException(
                $"A '{scope}' unit cannot escalate to '{escalationParent.Scope}': the parent must be coarser.",
                nameof(escalationParent));

        // A same-scope parent is only legal when it genuinely encloses the child; that is what makes
        // the depth half of the escalation measure decrease, and without it two peer types could name
        // each other and the walk would never terminate.
        if (escalationParent.Scope == scope && !escalationParent.Decl.Encloses(id.Decl))
            throw new ArgumentException(
                $"Unit '{id.Canonical}' cannot escalate to same-scope parent '{escalationParent.Canonical}': "
                + "a same-scope parent must be the enclosing declaration.",
                nameof(escalationParent));

        var pending = GetOrAdd(id, escalationParent, allowNullParent: false);
        // Monotone: a caller may state a contribution the artifact-kind table cannot see (a reverse
        // slot position, which only VtableLayout knows), but may never argue one away.
        pending.ContributesToParentLayout |= contributesToParentLayout;
        return id;
    }

    /// <summary>
    /// Attaches a generated artifact to <paramref name="unit"/>, folding the artifact's recovery rule
    /// into the unit's footprint.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The unit was not declared, the artifact is already owned by another unit, or the artifact
    /// kind's rule places it at a different scope than the unit's.
    /// </exception>
    public RecoveryGraphBuilder AddArtifact(RecoveryUnitId unit, ArtifactId artifact, RecoveryArtifactKind kind)
    {
        if (!_units.TryGetValue(unit, out var pending))
            throw new ArgumentException($"Unit '{unit.Canonical}' has not been declared.", nameof(unit));

        if (_artifactOwners.TryGetValue(artifact, out var existingOwner))
        {
            // Re-attaching to the same unit is a harmless repeat; two owners is the invariant break.
            if (existingOwner != unit)
                throw new ArgumentException(
                    $"Artifact '{artifact.Canonical}' already belongs to unit '{existingOwner.Canonical}'; "
                    + "an artifact belongs to exactly one recovery unit.",
                    nameof(artifact));
            return this;
        }

        var rule = RecoveryUnitClassifier.Classify(kind);
        if (rule.Scope != unit.Scope)
            throw new ArgumentException(
                $"Artifact kind '{kind}' classifies at scope '{rule.Scope}' but was attached to a '{unit.Scope}' unit.",
                nameof(kind));

        _artifactOwners[artifact] = unit;
        pending.Artifacts.Add(artifact);
        pending.Footprint |= rule.Footprint;
        pending.ContributesToParentLayout |= rule.ContributesToParentLayout;
        return this;
    }

    /// <summary>
    /// States that withdrawing <paramref name="unit"/> alone would change a layout its escalation
    /// parent still exposes, for a reason no artifact kind can see on its own.
    /// </summary>
    /// <remarks>
    /// The artifact-kind table supplies a conservative default, but layout contribution is contextual:
    /// an ordinary method contributes nothing, while the same kind of method occupying a reverse
    /// -dispatch position does — and only <c>VtableLayout</c> knows which. Marking is one-way; there
    /// is deliberately no way to clear the flag, so a stored-field cell cannot be talked out of its
    /// contribution by a later caller.
    /// </remarks>
    /// <exception cref="ArgumentException">The unit was not declared.</exception>
    public RecoveryGraphBuilder MarkContributesToParentLayout(RecoveryUnitId unit)
    {
        if (!_units.TryGetValue(unit, out var pending))
            throw new ArgumentException($"Unit '{unit.Canonical}' has not been declared.", nameof(unit));

        pending.ContributesToParentLayout = true;
        return this;
    }

    /// <summary>
    /// Records that <paramref name="dependent"/> cannot remain without <paramref name="dependency"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A unit is undeclared, or a unit requires itself.</exception>
    public RecoveryGraphBuilder AddRequires(RecoveryUnitId dependent, RecoveryUnitId dependency)
    {
        if (!_units.TryGetValue(dependent, out var pending))
            throw new ArgumentException($"Unit '{dependent.Canonical}' has not been declared.", nameof(dependent));
        if (!_units.ContainsKey(dependency))
            throw new ArgumentException($"Unit '{dependency.Canonical}' has not been declared.", nameof(dependency));
        if (dependent == dependency)
            throw new ArgumentException("A unit cannot require itself.", nameof(dependency));

        if (!pending.Requires.Contains(dependency))
            pending.Requires.Add(dependency);
        return this;
    }

    /// <summary>
    /// Validates the invariants and produces the graph.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No module unit exists, a declared unit is missing its escalation parent, or an escalation
    /// chain fails to reach a module unit.
    /// </exception>
    public RecoveryGraph Build()
    {
        if (!_units.Keys.Any(id => id.Scope == RecoveryScope.Module))
            throw new InvalidOperationException(
                "A recovery graph needs at least one module unit — it is the escalation terminus.");

        foreach (var pending in _units.Values)
        {
            if (pending.Id.Scope == RecoveryScope.Module)
            {
                if (pending.EscalationParent is not null)
                    throw new InvalidOperationException(
                        $"Module unit '{pending.Id.Canonical}' must not have an escalation parent.");
                continue;
            }

            if (pending.EscalationParent is not { } parent)
                throw new InvalidOperationException(
                    $"Unit '{pending.Id.Canonical}' has no escalation parent; only module units may.");
            if (!_units.ContainsKey(parent))
                throw new InvalidOperationException(
                    $"Unit '{pending.Id.Canonical}' escalates to undeclared unit '{parent.Canonical}'.");
        }

        // Every step either coarsens the scope or, at equal scope, moves to an enclosing declaration —
        // both bounded — so this walk is finite; the explicit check is what turns "should terminate"
        // into "does terminate".
        foreach (var pending in _units.Values)
            WalkToTerminus(pending.Id);

        var units = _units.ToDictionary(
            kv => kv.Key,
            kv => new RecoveryUnit
            {
                Id = kv.Value.Id,
                EscalationParent = kv.Value.EscalationParent,
                Footprint = kv.Value.Footprint,
                ContributesToParentLayout = kv.Value.ContributesToParentLayout,
                Requires = kv.Value.Requires.ToImmutableArray(),
                Artifacts = kv.Value.Artifacts.ToImmutableArray(),
            });

        var provides = new Dictionary<RecoveryUnitId, List<RecoveryUnitId>>();
        foreach (var unit in units.Values)
        {
            foreach (var dependency in unit.Requires)
            {
                if (!provides.TryGetValue(dependency, out var dependents))
                    provides[dependency] = dependents = new List<RecoveryUnitId>();
                dependents.Add(unit.Id);
            }
        }

        return new RecoveryGraph(
            units,
            provides.ToDictionary(kv => kv.Key, kv => kv.Value.ToImmutableArray()));
    }

    private void WalkToTerminus(RecoveryUnitId start)
    {
        var seen = new HashSet<RecoveryUnitId> { start };
        var current = start;
        while (current.Scope != RecoveryScope.Module)
        {
            var parent = _units[current].EscalationParent!.Value;
            if (!seen.Add(parent))
                throw new InvalidOperationException(
                    $"Escalation chain from '{start.Canonical}' cycles at '{parent.Canonical}'.");
            current = parent;
        }
    }

    private PendingUnit GetOrAdd(RecoveryUnitId id, RecoveryUnitId? parent, bool allowNullParent)
    {
        if (_units.TryGetValue(id, out var existing))
        {
            if (!allowNullParent && existing.EscalationParent is { } declared && declared != parent)
                throw new ArgumentException(
                    $"Unit '{id.Canonical}' was already declared with escalation parent "
                    + $"'{declared.Canonical}'; re-declaring it with '{parent?.Canonical ?? "<none>"}' is ambiguous.",
                    nameof(parent));
            existing.EscalationParent ??= parent;
            return existing;
        }

        var pending = new PendingUnit { Id = id, EscalationParent = parent };
        _units[id] = pending;
        return pending;
    }
}
