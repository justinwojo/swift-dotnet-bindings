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

    // Discovery order is load-bearing — the attempt loop reports the fault it just learned about, and
    // a diagnosis reads the sequence to tell a single defect surfacing repeatedly from independent
    // ones. Dictionary enumeration order is unspecified, so it is tracked explicitly rather than
    // inferred from Values.
    private readonly List<EmitterFaultRecord> _inDiscoveryOrder = new();

    /// <summary>True when nothing has been poisoned — the shape of every ordinary run.</summary>
    public bool IsEmpty => _byCanonical.Count == 0;

    /// <summary>Every fault recorded so far, in the order it was discovered.</summary>
    public IReadOnlyList<EmitterFaultRecord> Faults => _inDiscoveryOrder;

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

    /// <summary>True when <paramref name="id"/> must not be emitted.</summary>
    public bool IsPoisoned(in DeclId id) => _byCanonical.ContainsKey(id.Canonical);

    /// <summary>The fault that poisoned <paramref name="id"/>, if any.</summary>
    public bool TryGet(in DeclId id, out EmitterFaultRecord fault) =>
        _byCanonical.TryGetValue(id.Canonical, out fault);
}
