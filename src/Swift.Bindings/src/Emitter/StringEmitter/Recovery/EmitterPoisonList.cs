// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The set of declarations a module's emission must refuse, accumulated across attempts.
/// </summary>
/// <remarks>
/// This is the denylist half of poison-and-regenerate. Emission is a pure function of the frozen type
/// database, the declaration tree, and this list, so re-running it with one more entry is a complete,
/// principled do-over rather than a patch applied to half-written output. The list only ever grows
/// within a module, which is what makes the attempt loop terminate.
/// </remarks>
internal sealed class EmitterPoisonList
{
    private readonly Dictionary<string, EmitterFaultRecord> _byCanonical = new(StringComparer.Ordinal);

    // The unit-keyed index for coarse sub-declaration scopes (a shared-helper bundle, a reverse
    // conformance, a conformance edge). Their withdrawal must NOT collapse the whole enclosing
    // declaration, so they are keyed on the full RecoveryUnitId.Canonical (decl + scope + discriminator)
    // rather than the bare DeclId — poisoning a bundle's synthetic key here leaves every other member of
    // the type untouched, which the bare-DeclId index (read by the whole-type skip gate) could not do.
    private readonly Dictionary<string, EmitterFaultRecord> _byUnit = new(StringComparer.Ordinal);

    // Discovery order is load-bearing — the attempt loop reports the fault it just learned about, and
    // a diagnosis reads the sequence to tell a single defect surfacing repeatedly from independent
    // ones. Dictionary enumeration order is unspecified, so it is tracked explicitly rather than
    // inferred from Values.
    private readonly List<EmitterFaultRecord> _inDiscoveryOrder = new();

    /// <summary>True when nothing has been poisoned — the shape of every ordinary run.</summary>
    public bool IsEmpty => _byCanonical.Count == 0 && _byUnit.Count == 0;

    /// <summary>Every fault recorded so far, in the order it was discovered.</summary>
    public IReadOnlyList<EmitterFaultRecord> Faults => _inDiscoveryOrder;

    /// <summary>
    /// Whether a unit of <paramref name="scope"/> is keyed by its full <see cref="RecoveryUnitId"/>
    /// rather than by its bare <see cref="DeclId"/>. The whole-declaration scopes — a leaf, an accessor
    /// group (already normalized to its property), and a whole type — keep the existing bare-DeclId
    /// index so their poisoning stays byte-identical to before this index existed. Every coarser
    /// sub-declaration scope routes to the unit index so it cannot collapse its enclosing declaration.
    /// </summary>
    private static bool RoutesToUnitIndex(RecoveryScope scope) =>
        scope is not (RecoveryScope.LeafApi or RecoveryScope.AccessorGroup or RecoveryScope.TypeSurface);

    /// <summary>
    /// Records a fault. If this subject was already poisoned, the fault escalates to the subject's
    /// next rung instead: denying it plainly did not contain the defect, so widening is the only
    /// change that can make the next attempt different from the last one.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the fault cannot be contained at all — the subject was already poisoned and
    /// has no wider rung to escalate to, so the module must fail.
    /// </returns>
    public bool Record(EmitterFaultRecord fault)
    {
        var key = fault.Subject.Canonical;
        if (!_byCanonical.ContainsKey(key))
        {
            _byCanonical.Add(key, fault);
            _inDiscoveryOrder.Add(fault);
            return true;
        }

        if (fault.Escalation is not { } wider)
        {
            return false;
        }

        var escalated = fault with
        {
            Subject = wider,
            Scope = RecoveryScope.TypeSurface,
            Escalation = null,
        };

        if (!_byCanonical.TryAdd(wider.Canonical, escalated))
        {
            return false;
        }

        _inDiscoveryOrder.Add(escalated);
        return true;
    }

    /// <summary>
    /// Records a fault for a specific recovery <paramref name="unit"/>, routing it by scope: a
    /// whole-declaration scope (leaf, accessor group, whole type) goes to the bare-DeclId index exactly
    /// as <see cref="Record(EmitterFaultRecord)"/> would, keeping existing behavior byte-identical; a
    /// coarse sub-declaration scope goes to the unit-keyed index so it cannot collapse its enclosing
    /// declaration. A coarse unit has no wider rung in the poison list — the controller decides its
    /// escalation against the recovery graph — so re-recording the same coarse unit returns
    /// <c>false</c> (no progress) rather than escalating.
    /// </summary>
    public bool Record(RecoveryUnitId unit, EmitterFaultRecord fault)
    {
        if (!RoutesToUnitIndex(unit.Scope))
            return Record(fault);

        var key = unit.Canonical;
        if (!_byUnit.TryAdd(key, fault))
            return false;

        _inDiscoveryOrder.Add(fault);
        return true;
    }

    /// <summary>True when <paramref name="id"/> must not be emitted.</summary>
    public bool IsPoisoned(in DeclId id) => _byCanonical.ContainsKey(id.Canonical);

    /// <summary>The fault that poisoned <paramref name="id"/>, if any.</summary>
    public bool TryGet(in DeclId id, out EmitterFaultRecord fault) =>
        _byCanonical.TryGetValue(id.Canonical, out fault);

    /// <summary>
    /// True when the coarse sub-declaration <paramref name="unit"/> must not be emitted. Queries the
    /// unit-keyed index only — a coarse surface is suppressed by its own unit identity, never by its
    /// enclosing declaration's bare-DeclId poisoning (if the whole declaration were poisoned, the
    /// coarse emitter would not be reached at all).
    /// </summary>
    public bool IsPoisoned(in RecoveryUnitId unit) => _byUnit.ContainsKey(unit.Canonical);

    /// <summary>The fault that poisoned the coarse <paramref name="unit"/>, if any.</summary>
    public bool TryGet(in RecoveryUnitId unit, out EmitterFaultRecord fault) =>
        _byUnit.TryGetValue(unit.Canonical, out fault);
}
