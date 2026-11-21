// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Withdraws every member whose Swift call plan contradicts the callee's predicted lowering, before
/// the module is written.
/// </summary>
/// <remarks>
/// <para>
/// The planner compares the managed signature an import declares with the physical signature the
/// Swift callee takes. Most of its refusals say only that it cannot tell (no facts, a layout it cannot
/// predict, a carrier it does not model): the import may well be right, and the guards keep it off the
/// engines that need a bridge. Three say it can: <see cref="SwiftPlanRefusalKind.PrefixMismatch"/>,
/// <see cref="SwiftPlanRefusalKind.ResultMismatch"/> and <see cref="SwiftPlanRefusalKind.SpecialRegisterMismatch"/>
/// mean the arguments or the result travel in places the callee does not use. Such an import is wrong on
/// every engine, not only the ones a guard refuses, so it must not ship callable at all.
/// </para>
/// <para>
/// The member that declares it is withdrawn through emission containment, the same channel an emitter
/// fault or an ABI-validation withdrawal takes: the attempt is abandoned, the member is denied, and the
/// module is emitted again without it, so the skip report carries a row for it on every render path
/// (single render, the SDK's two-pass generation and the verify-recover loop alike). A Swift-convention
/// callback, and the place its address is taken, belong to the member whose import hands the closure to
/// Swift: that member is withdrawn with it. A contradiction no member can be withdrawn for (no owner, an
/// owner not droppable alone, a call through a pointer, or a member already denied whose transition came
/// back) fails the module closed instead.
/// </para>
/// <para>
/// An import whose member the ABI floor already emitted as a throwing tombstone is left alone, and so
/// are the callbacks only that import would have passed: the declarations stay for source
/// compatibility, but the body that called the import is gone, so nothing reaches native code through
/// either on any engine.
/// </para>
/// </remarks>
internal static class SwiftPlanContradictions
{
    /// <summary>True for the refusals that say the import is wrong, not merely unproven.</summary>
    public static bool IsContradiction(SwiftPlanRefusalKind kind) =>
        kind is SwiftPlanRefusalKind.PrefixMismatch
            or SwiftPlanRefusalKind.ResultMismatch
            or SwiftPlanRefusalKind.SpecialRegisterMismatch;

    /// <summary>The refused coverage entries with at least one contradiction, in coverage order.</summary>
    public static ImmutableArray<SwiftPlanCoverageEntry> Find(SwiftCallPlanSet planSet)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        var inject = Injected.Value;
        return planSet.Coverage
            .Select(e => inject?.Invoke(e) is { } refusal
                ? e with { Status = SwiftPlanStatus.Refused, Refusals = e.Refusals.Add(refusal) }
                : e)
            .Where(e => e.Status == SwiftPlanStatus.Refused && e.Refusals.Any(r => IsContradiction(r.Kind)))
            .ToImmutableArray();
    }

    // A test seam, like EmitterFaultInjector: a contradiction the planner did not find, added to the
    // entries it matches. A test written against a real mismatched carrier stops testing the
    // withdrawal the moment that carrier is fixed; an injected one keeps proving it.
    private static readonly AsyncLocal<Func<SwiftPlanCoverageEntry, SwiftPlanRefusal?>?> Injected = new();

    /// <summary>Adds the refusal <paramref name="inject"/> returns to each entry it matches, until disposed.</summary>
    internal static IDisposable InjectForTesting(Func<SwiftPlanCoverageEntry, SwiftPlanRefusal?> inject)
    {
        ArgumentNullException.ThrowIfNull(inject);
        return new InjectionScope(inject);
    }

    private sealed class InjectionScope : IDisposable
    {
        private readonly Func<SwiftPlanCoverageEntry, SwiftPlanRefusal?>? _previous;
        private bool _disposed;

        public InjectionScope(Func<SwiftPlanCoverageEntry, SwiftPlanRefusal?> inject)
        {
            _previous = Injected.Value;
            Injected.Value = inject;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Injected.Value = _previous;
        }
    }

    /// <summary>One contradicted transition and the recovery unit that declares it.</summary>
    public sealed record Withdrawal(SwiftPlanCoverageEntry Entry, RecoveryUnitId Unit, string Reason);

    /// <summary>One contradicted transition no member can be withdrawn for, and why.</summary>
    public sealed record Unresolved(SwiftPlanCoverageEntry Entry, string Why);

    /// <summary>
    /// One emitted import, named as its <see cref="AbiCallPlan"/> names it: the entry point it binds
    /// (after any wrapper suffix or dispatch thunk), its P/Invoke name and its owning member.
    /// </summary>
    public sealed record ImportKey(string EntryPoint, string MethodName, ArtifactId Owner)
    {
        /// <summary>The key of <paramref name="plan"/>, or null when the plan records no owner.</summary>
        public static ImportKey? Of(AbiCallPlan plan) =>
            plan.Owner is { } owner ? new ImportKey(plan.EntryPoint, plan.MethodName, owner) : null;
    }

    /// <summary>
    /// Maps each contradicted transition to the member that declared its import, through the owner the
    /// import's ABI call plan recorded and the classification the verify-recover planes use. A
    /// callback, or the place its address is taken, maps through the imports
    /// <paramref name="callbackImports"/> records as passing its closure to Swift.
    /// </summary>
    public static (ImmutableArray<Withdrawal> Withdrawals, ImmutableArray<Unresolved> Unresolved) Resolve(
        IEnumerable<SwiftPlanCoverageEntry> contradictions,
        IReadOnlyList<AbiCallPlan> callPlans,
        IReadOnlySet<ImportKey>? orphaned = null,
        IReadOnlyDictionary<string, IReadOnlySet<ImportKey>>? callbackImports = null)
    {
        ArgumentNullException.ThrowIfNull(contradictions);
        ArgumentNullException.ThrowIfNull(callPlans);

        var withdrawals = ImmutableArray.CreateBuilder<Withdrawal>();
        var unresolved = ImmutableArray.CreateBuilder<Unresolved>();
        foreach (var entry in contradictions)
        {
            var reason = Describe(entry);
            List<AbiCallPlan> owners;
            switch (entry.Transition)
            {
                case SwiftTransitionKind.ForwardPInvoke:
                    owners = callPlans
                        .Where(p => p.EntryPoint == entry.EntryPoint && entry.Member.EndsWith("::" + p.MethodName, StringComparison.Ordinal))
                        .ToList();
                    if (owners.Count == 0)
                    {
                        unresolved.Add(new Unresolved(entry, "no ABI call plan records the import"));
                        continue;
                    }
                    break;

                case SwiftTransitionKind.ReverseCallback or SwiftTransitionKind.CallbackAddress:
                {
                    // The callback is the method itself; its address names the callback as its target.
                    var callback = MethodPart(entry.Transition == SwiftTransitionKind.ReverseCallback ? entry.Member : entry.Target);
                    if (callback is null || callbackImports is null || !callbackImports.TryGetValue(callback, out var imports) || imports.Count == 0)
                    {
                        unresolved.Add(new Unresolved(entry, "no import is recorded as passing the closure this callback is"));
                        continue;
                    }
                    owners = callPlans.Where(p => ImportKey.Of(p) is { } key && imports.Contains(key)).ToList();
                    if (owners.Count == 0)
                    {
                        unresolved.Add(new Unresolved(entry,
                            $"no ABI call plan records the import that passes the closure ({string.Join(", ", imports.Select(i => i.EntryPoint).Order(StringComparer.Ordinal))})"));
                        continue;
                    }
                    break;
                }

                default:
                    unresolved.Add(new Unresolved(entry, $"a {entry.Transition} transition is not an import a member can be withdrawn for"));
                    continue;
            }

            foreach (var plan in owners)
            {
                if (plan.Owner is not { } owner)
                {
                    unresolved.Add(new Unresolved(entry, "its ABI call plan records no owning member"));
                    continue;
                }

                if (orphaned?.Contains(new ImportKey(plan.EntryPoint, plan.MethodName, owner)) == true)
                    continue;

                // Only a member is withdrawn for its import. A coarser owner (a type's surface, a
                // conformance, a helper bundle, the module initializer) would take far more than the
                // contradicted call with it, so it fails the module instead.
                var (unit, droppable) = RecoveryUnitClassifier.ClassifyArtifact(owner);
                if (!droppable || unit.Scope is not (RecoveryScope.LeafApi or RecoveryScope.AccessorGroup))
                {
                    unresolved.Add(new Unresolved(entry, $"its owner {unit.Describe()} is not a member that can be withdrawn alone"));
                    continue;
                }

                if (!withdrawals.Any(w => w.Unit == unit))
                    withdrawals.Add(new Withdrawal(entry, unit, reason));
            }
        }

        return (withdrawals.ToImmutable(), unresolved.ToImmutable());
    }

    /// <summary>
    /// Withdraws the members of every contradicted transition of <paramref name="planSet"/> from the
    /// ambient emission attempt and abandons it, or fails the module closed. Returns normally only
    /// when no contradiction remains on an import something still calls (<paramref name="orphaned"/>
    /// names the ones nothing does).
    /// </summary>
    /// <exception cref="EmissionAttemptAbandoned">At least one member was withdrawn; the module is emitted again.</exception>
    /// <exception cref="SwiftPlanContradictionException">A contradiction no withdrawal can remove.</exception>
    public static void WithdrawOrFail(
        SwiftCallPlanSet planSet,
        IReadOnlyList<AbiCallPlan> callPlans,
        IReadOnlySet<ImportKey> orphaned,
        IReadOnlyDictionary<string, IReadOnlySet<ImportKey>> callbackImports,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(orphaned);
        ArgumentNullException.ThrowIfNull(callbackImports);
        ArgumentNullException.ThrowIfNull(logger);
        var contradictions = Find(planSet);
        if (contradictions.IsEmpty)
            return;

        var (withdrawals, unresolved) = Resolve(contradictions, callPlans, orphaned, callbackImports);
        if (!unresolved.IsEmpty)
            throw new SwiftPlanContradictionException(planSet.Module, unresolved);
        if (withdrawals.IsEmpty)
            return;

        var attempt = EmissionAttempt.Current;
        if (attempt is null)
            throw new SwiftPlanContradictionException(
                planSet.Module,
                withdrawals.Select(w => new Unresolved(w.Entry, "emission is not contained, so its member cannot be withdrawn")).ToImmutableArray());

        var repeated = ImmutableArray.CreateBuilder<Unresolved>();
        foreach (var withdrawal in withdrawals)
        {
            var fault = EmitterFaultRecord.ForRecoveryWithdrawal(
                withdrawal.Unit.Decl, withdrawal.Unit.Scope, withdrawal.Reason, origin: EmitterFaultOrigin.AbiRecoveryWithdrawal);
            if (!attempt.RecordFault(fault))
            {
                // Already denied, and its import is still here: the denial did not remove what it was
                // meant to, so withdrawing again would only loop.
                repeated.Add(new Unresolved(withdrawal.Entry, $"{withdrawal.Unit.Describe()} was already withdrawn and the import is still emitted"));
                continue;
            }

            logger.LogWarning(
                "{Code}: {Member} ({EntryPoint}) is withdrawn from {Module}: {Reason}",
                SwiftPlanContradictionException.Code, withdrawal.Entry.Member, withdrawal.Entry.EntryPoint, planSet.Module, withdrawal.Reason);
        }

        if (repeated.Count > 0)
            throw new SwiftPlanContradictionException(planSet.Module, repeated.ToImmutable());

        throw new EmissionAttemptAbandoned();
    }

    // `Type::method` names its method after the last separator.
    private static string? MethodPart(string? member) =>
        member is null ? null : member.LastIndexOf("::", StringComparison.Ordinal) is var i and >= 0 ? member[(i + 2)..] : member;

    /// <summary>The withdrawal reason a skip row carries: what the plan contradicts, per refusal.</summary>
    public static string Describe(SwiftPlanCoverageEntry entry) =>
        (entry.Transition is SwiftTransitionKind.ReverseCallback or SwiftTransitionKind.CallbackAddress
            ? "the Swift call plan of its closure callback contradicts the closure's lowering, so Swift would call the callback " +
              "with values where it does not read them ("
            : "its Swift call plan contradicts the callee's lowering, so the import would reach native code with values " +
              "where the callee does not take them (") +
        string.Join("; ", entry.Refusals
            .Where(r => IsContradiction(r.Kind))
            .GroupBy(r => (r.Kind, r.Reason))
            .Select(g => g.Count() > 1 ? $"{g.Key.Kind}: {g.Key.Reason}" : g.First().ToString())) + ")";
}

/// <summary>
/// A module declares a Swift-convention transition whose plan contradicts the callee's lowering and no
/// member can be withdrawn for it. Fatal: shipping it would hand native code values it does not read.
/// </summary>
internal sealed class SwiftPlanContradictionException : InvalidOperationException
{
    /// <summary>The diagnostic code of a contradicted plan, withdrawn or fatal.</summary>
    public const string Code = "SWIFTBIND135";

    public SwiftPlanContradictionException(string module, ImmutableArray<SwiftPlanContradictions.Unresolved> unresolved)
        : base(BuildMessage(module, unresolved))
    {
        Module = module;
        Unresolved = unresolved;
    }

    public string Module { get; }

    public ImmutableArray<SwiftPlanContradictions.Unresolved> Unresolved { get; }

    private static string BuildMessage(string module, ImmutableArray<SwiftPlanContradictions.Unresolved> unresolved) =>
        $"{Code}: {module} declares {unresolved.Length} Swift-convention transition(s) whose call plan contradicts the callee's lowering " +
        "and that no withdrawal can remove: " +
        string.Join("; ", unresolved.Select(u => $"{u.Entry.Member} ({u.Entry.EntryPoint}): {u.Why}; {SwiftPlanContradictions.Describe(u.Entry)}"));
}
