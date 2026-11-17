// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using BindingsGeneration;

namespace SwiftBindings.TransitionGate;

/// <summary>One scalar slot of a twin signature as its plan fixes it: register class, size and signedness.</summary>
public readonly record struct TwinSlot(string Class, int Size, bool Signed)
{
    /// <summary>
    /// The canonical native carriers (<see cref="SwiftTransitionScanner.CanonicalCarrier"/>) a compiled
    /// twin may spell this slot as. A pointer-sized integer is any eight-byte integer or pointer; below
    /// eight bytes signedness decides how the caller extends the value, so it must agree.
    /// </summary>
    public IReadOnlySet<string> Carriers => (Class, Size) switch
    {
        ("General", 8) => new HashSet<string> { "isize", "usize", "ptr", "i64", "u64" },
        ("General", 4) => new HashSet<string> { Signed ? "i32" : "u32" },
        ("General", 2) => Signed ? new HashSet<string> { "i16" } : new HashSet<string> { "u16", "char" },
        ("General", 1) => Signed ? new HashSet<string> { "i8" } : new HashSet<string> { "u8", "bool" },
        ("Float", 4) => new HashSet<string> { "f32" },
        ("Float", 8) => new HashSet<string> { "f64" },
        _ => new HashSet<string>(),
    };

    public override string ToString() => $"{Class}{Size * 8}{(Signed ? "s" : "")}";
}

/// <summary>A forward bridge's twin signature, as its plan states it.</summary>
/// <param name="Roles">Each parameter's role (<c>Ordinary</c>, <c>Self</c>, <c>Error</c>, <c>IndirectResult</c>, <c>ResultBuffer</c>), in order.</param>
/// <param name="Return">The scalar a scalar result comes back as; null when the twin returns nothing.</param>
public sealed record TwinPlan(string Symbol, string Kind, ImmutableArray<TwinSlot> Parameters, ImmutableArray<string> Roles, TwinSlot? Return, string Origin)
{
    /// <summary>
    /// The parameter name the generator gives a trailing pointer of <paramref name="role"/>, or null for an
    /// ordinary argument. The trailing pointers are all pointer-sized, so only their names tell a swapped
    /// pair apart; an ordinary argument never carries one of these names.
    /// </summary>
    public static string? TrailingName(string role) => role switch
    {
        "Self" => "__self",
        "Error" => "__error",
        "IndirectResult" => "__indirectResult",
        "ResultBuffer" => "__resultBuffer",
        _ => null,
    };

    /// <summary>Every name <see cref="TrailingName"/> gives.</summary>
    public static IReadOnlySet<string> TrailingNames { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "__self", "__error", "__indirectResult", "__resultBuffer" };

    public override string ToString() =>
        $"{(Return is { } r ? r.ToString() : "void")} ({string.Join(", ", Parameters)})";
}

/// <summary>One transition a recorded bridge withdrawal says went back to its direct import.</summary>
public sealed record TransitionWithdrawnTransition(string Member, string EntryPoint, string Symbol, ImmutableArray<string> Shapes, ImmutableArray<string> RefusesOn);

/// <summary>
/// A module's recorded bridge withdrawal (the plan sidecar's <c>bridgeWithdrawal</c>): the wrapper
/// compile decided a routing, could not complete it, and restored the module's direct imports.
/// <see cref="Origin"/> is the sidecar it was read from.
/// </summary>
public sealed record TransitionWithdrawal(
    string Origin, string Module, string Cause, string Reason, string Library, ImmutableArray<TransitionWithdrawnTransition> Transitions)
{
    /// <summary>The causes a withdrawal can be accepted under: the restored wrapper is the one the receipt describes.</summary>
    public static readonly IReadOnlySet<string> AcceptableCauses = new HashSet<string>(StringComparer.Ordinal) { "RelinkFailed", "RoutingDefect" };

    /// <summary>The cause no withdrawal is accepted under: the relinked wrapper contradicted the receipt.</summary>
    public const string ReceiptMismatch = "ReceiptMismatch";

    /// <summary>Each restored member and the engines it refuses on, for a diagnostic.</summary>
    public string Describe() => string.Join("; ", Transitions.Select(t =>
        $"{t.Member} ({(t.RefusesOn.IsEmpty ? "refuses on no engine" : "refuses on " + string.Join(", ", t.RefusesOn))})"));
}

/// <summary>
/// The bridge plans a scan's R3 compares twins against, read from the generator's call-plan
/// sidecars (<c>*.swift-call-plans.json</c>, one per module) in the plan source's directory.
/// </summary>
public sealed class BridgeTwinPlans
{
    public const string SidecarSuffix = ".swift-call-plans.json";

    /// <summary>The call-plan schema this reader understands.</summary>
    public const int PlanVersion = 1;

    /// <summary>
    /// The evidence a generation records on a planned transition when it was given no lowering
    /// evidence at all (a lane that does not bridge). Equal to the generator's own reason.
    /// </summary>
    public const string NoEvidenceReason = "no swiftc lowering evidence was joined";

    private readonly Dictionary<string, TwinPlan> _plans;

    private BridgeTwinPlans(Dictionary<string, TwinPlan> plans, ImmutableArray<string> problems, Gaps gaps, ImmutableArray<string> evidenceGaps)
    {
        _plans = plans;
        Problems = problems;
        RoutingGaps = gaps.All.ToImmutableArray();
        UnrecordedRoutingGaps = gaps.Unrecorded.ToImmutableArray();
        Withdrawals = gaps.Withdrawals.ToImmutableArray();
        WithdrawalProblems = gaps.WithdrawalProblems.ToImmutableArray();
        EvidenceGaps = evidenceGaps;
    }

    /// <summary>What the coverage of the sidecars read says about routing: every gap, and how each is explained.</summary>
    private sealed class Gaps
    {
        public List<string> All { get; } = new();
        public List<string> Unrecorded { get; } = new();
        public List<TransitionWithdrawal> Withdrawals { get; } = new();
        public List<string> WithdrawalProblems { get; } = new();
    }

    /// <summary>Why the source does not read; any one makes every twin unverifiable.</summary>
    public ImmutableArray<string> Problems { get; }

    /// <summary>
    /// Transitions whose generation disagrees with its own evidence: one the evidence made executable
    /// that kept its direct import (the shim refused it, or nothing routed it), or one called through
    /// a bridge the evidence did not prove. A refused executable transition leaves no twin for R3 to
    /// reject, so only the sidecar's coverage shows it.
    /// </summary>
    public ImmutableArray<string> RoutingGaps { get; }

    /// <summary>
    /// The routing gaps no recorded withdrawal explains: every gap of <see cref="RoutingGaps"/> but
    /// those a module's <see cref="Withdrawals"/> lists, with the bridge it lists and the reason it
    /// records. A transition called through a bridge the evidence did not prove is never explained.
    /// </summary>
    public ImmutableArray<string> UnrecordedRoutingGaps { get; }

    /// <summary>The recorded bridge withdrawals, one per module that has one.</summary>
    public ImmutableArray<TransitionWithdrawal> Withdrawals { get; }

    /// <summary>
    /// Why a recorded withdrawal does not hold up against its own sidecar: it does not read, lists a
    /// transition its coverage does not show restored, leaves one still called through a bridge (a
    /// partial restoration), records shapes or engines that disagree, or has a cause no withdrawal is
    /// accepted under. Each makes the withdrawal explain nothing.
    /// </summary>
    public ImmutableArray<string> WithdrawalProblems { get; }

    /// <summary>
    /// Forward transitions a generation that was given lowering evidence planned but could not make
    /// executable: the evidence names no definition of the target (it predates the code that
    /// declares it), or it contradicts the plan. Either leaves a planned Swift-convention call on its
    /// direct import with nothing else reporting it, so the evidence must be re-recorded or the plan
    /// corrected. A generation given no evidence, and a closure or reverse plan (which swiftc never
    /// defines), are not gaps.
    /// </summary>
    public ImmutableArray<string> EvidenceGaps { get; }

    public int Count => _plans.Count;

    public TwinPlan? Find(string symbol) => _plans.TryGetValue(symbol, out var plan) ? plan : null;

    public static BridgeTwinPlans Load(TransitionPlanSource source)
    {
        var problems = new List<string>();
        var gaps = new Gaps();
        var evidenceGaps = new List<string>();
        var plans = new Dictionary<string, TwinPlan>(StringComparer.Ordinal);
        if (source.Kind != TransitionPlanSourceKind.CallPlanSidecars)
        {
            problems.Add($"plan source kind {source.Kind} is not one R3 reads");
        }
        else if (!Directory.Exists(source.Location))
        {
            problems.Add($"plan source directory {source.Location} does not exist");
        }
        else
        {
            foreach (var file in Directory.GetFiles(source.Location, "*" + SidecarSuffix, SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
                Read(Path.GetFileName(file), File.ReadAllText(file), plans, problems, gaps, evidenceGaps);
        }
        return new BridgeTwinPlans(plans, problems.ToImmutableArray(), gaps, evidenceGaps.ToImmutableArray());
    }

    /// <summary>Plans given as (name, JSON text), for tests.</summary>
    public static BridgeTwinPlans FromSidecars(IEnumerable<(string Name, string Json)> sidecars)
    {
        var problems = new List<string>();
        var gaps = new Gaps();
        var evidenceGaps = new List<string>();
        var plans = new Dictionary<string, TwinPlan>(StringComparer.Ordinal);
        foreach (var (name, json) in sidecars)
            Read(name, json, plans, problems, gaps, evidenceGaps);
        return new BridgeTwinPlans(plans, problems.ToImmutableArray(), gaps, evidenceGaps.ToImmutableArray());
    }

    private static void Read(string name, string json, Dictionary<string, TwinPlan> plans, List<string> problems, Gaps gaps, List<string> evidenceGaps)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var version = root.TryGetProperty("planVersion", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
            if (version != PlanVersion)
            {
                problems.Add($"{name}: plan schema {version}, but R3 reads schema {PlanVersion}");
                return;
            }
            foreach (var plan in root.GetProperty("plans").EnumerateArray())
            {
                var symbol = plan.GetProperty("symbol").GetString() ?? "";
                var body = plan.GetProperty("body");
                var managed = body.GetProperty("managed");
                var arguments = managed.GetProperty("arguments").EnumerateArray().ToList();
                var parameters = arguments.Select(Slot).ToImmutableArray();
                var roles = arguments.Select(a => a.GetProperty("role").GetString() ?? "").ToImmutableArray();
                var result = managed.GetProperty("result");
                TwinSlot? returned = result.GetProperty("kind").GetString() == "Scalar" ? Slot(result) : null;
                var read = new TwinPlan(symbol, body.GetProperty("kind").GetString() ?? "", parameters, roles, returned, name);
                if (plans.TryGetValue(symbol, out var existing))
                {
                    // A plan's symbol is its body's hash: two modules may hold the same plan, never two bodies.
                    if (existing.Kind != read.Kind || existing.Return != read.Return || !existing.Parameters.SequenceEqual(read.Parameters) || !existing.Roles.SequenceEqual(read.Roles))
                        problems.Add($"{name}: plan {symbol} differs from the one {existing.Origin} holds");
                    continue;
                }
                plans.Add(symbol, read);
            }
            var withdrawal = root.TryGetProperty("bridgeWithdrawal", out var w) && w.ValueKind != JsonValueKind.Null
                ? ReadWithdrawal(name, root.TryGetProperty("module", out var mod) && mod.ValueKind == JsonValueKind.String ? mod.GetString() ?? "" : "", w, gaps.WithdrawalProblems)
                : null;
            // A transition listed twice is already a problem of the record; the first listing explains its gap.
            var listed = withdrawal?.Transitions.GroupBy(t => $"{t.Member}|{t.EntryPoint}", StringComparer.Ordinal)
                             .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
                         ?? new Dictionary<string, TransitionWithdrawnTransition>(StringComparer.Ordinal);
            var restored = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("coverage", out var coverage))
            {
                foreach (var entry in coverage.EnumerateArray())
                {
                    var executable = entry.TryGetProperty("executable", out var e) && e.ValueKind == JsonValueKind.True;
                    var bridged = entry.TryGetProperty("bridged", out var b) && b.ValueKind == JsonValueKind.True;
                    var member = entry.TryGetProperty("member", out var m) ? m.GetString() : null;
                    var entryPoint = entry.TryGetProperty("entryPoint", out var ep) && ep.ValueKind == JsonValueKind.String ? ep.GetString() : null;
                    var target = entry.TryGetProperty("symbol", out var s) ? s.GetString() : null;
                    if (EvidenceGap(entry, executable, target, plans) is { } evidence)
                        evidenceGaps.Add($"{name}: {member} is planned through {target}, but its lowering evidence does not make it executable: {evidence}");
                    if (withdrawal is not null && bridged)
                        gaps.WithdrawalProblems.Add($"{name}: {member} is still called through its bridge {target}, but the module records that its bridges were withdrawn (a partial restoration)");
                    if (executable == bridged)
                        continue;
                    var refusal = entry.TryGetProperty("bridgeRefusal", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    var gap = executable
                        ? $"{name}: {member} is executable through {target} but kept its direct import: {refusal ?? "nothing routed it"}"
                        : $"{name}: {member} is called through {target}, which the evidence does not prove executable";
                    gaps.All.Add(gap);
                    // A gap is explained only by a withdrawal that lists it, with the bridge it was
                    // planned through and the reason the withdrawal recorded on it.
                    var key = $"{member}|{entryPoint}";
                    if (executable && withdrawal is not null && listed.TryGetValue(key, out var listing)
                        && listing.Symbol == target && refusal == withdrawal.Reason && restored.Add(key))
                        continue;
                    gaps.Unrecorded.Add(gap);
                }
            }
            if (withdrawal is not null)
            {
                foreach (var listing in withdrawal.Transitions.Where(t => !restored.Contains($"{t.Member}|{t.EntryPoint}")))
                    gaps.WithdrawalProblems.Add($"{name}: the bridge withdrawal lists {listing.Member} ({listing.EntryPoint}) through {listing.Symbol}, "
                                                + "but its coverage does not show that transition executable and restored to its direct import for the withdrawal's reason");
                gaps.Withdrawals.Add(withdrawal);
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            problems.Add($"{name}: the call plans do not read: {ex.Message}");
        }
    }

    /// <summary>
    /// The withdrawal record of a sidecar, or null when it does not read; what is wrong with it,
    /// including a cause no withdrawal is accepted under and shapes or engines that disagree with the
    /// engine table, goes to <paramref name="problems"/>.
    /// </summary>
    private static TransitionWithdrawal? ReadWithdrawal(string name, string module, JsonElement record, List<string> problems)
    {
        string? Text(JsonElement e, string property) =>
            e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text ? text : null;
        ImmutableArray<string>? Names(JsonElement e, string property) =>
            e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
                ? v.EnumerateArray().Select(x => x.GetString()!).ToImmutableArray()
                : null;

        if (record.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{name}: the bridge withdrawal is not an object");
            return null;
        }
        var cause = Text(record, "cause");
        var reason = Text(record, "reason");
        var library = Text(record, "library");
        if (cause is null || reason is null || library is null || !record.TryGetProperty("transitions", out var listed) || listed.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{name}: the bridge withdrawal does not record its cause, reason, library and transitions");
            return null;
        }
        var transitions = ImmutableArray.CreateBuilder<TransitionWithdrawnTransition>();
        foreach (var entry in listed.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Text(entry, "member") is not { } member || Text(entry, "entryPoint") is not { } entryPoint
                || Text(entry, "symbol") is not { } symbol || Names(entry, "shapes") is not { } shapes || Names(entry, "refusesOn") is not { } refusesOn)
            {
                problems.Add($"{name}: a transition of the bridge withdrawal does not record its member, entry point, bridge, shapes and engines");
                return null;
            }
            transitions.Add(new TransitionWithdrawnTransition(member, entryPoint, symbol, shapes, refusesOn));
        }
        var withdrawal = new TransitionWithdrawal(name, module, cause, reason, library, transitions.ToImmutable());

        if (cause == TransitionWithdrawal.ReceiptMismatch)
            problems.Add($"{name}: the module's bridges were withdrawn because the relinked wrapper contradicts its lowering receipt ({reason}); "
                         + "the receipt then no longer shows the ABI the restored imports were judged against, so the withdrawal cannot be shown safe");
        else if (!TransitionWithdrawal.AcceptableCauses.Contains(cause))
            problems.Add($"{name}: the bridge withdrawal names the cause {cause}, which this check does not know");
        if (withdrawal.Transitions.IsEmpty)
            problems.Add($"{name}: the bridge withdrawal lists no transition");
        foreach (var duplicate in withdrawal.Transitions.GroupBy(t => $"{t.Member}|{t.EntryPoint}", StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"{name}: the bridge withdrawal lists {duplicate.First().Member} ({duplicate.First().EntryPoint}) {duplicate.Count()} times");
        foreach (var transition in withdrawal.Transitions)
        {
            if (SwiftTransitionEngines.ShapesOf(transition.Shapes) is not { } bits)
                problems.Add($"{name}: the bridge withdrawal records shapes of {transition.Member} that are not shape names ({string.Join(", ", transition.Shapes)})");
            else if (!SwiftTransitionEngines.Refusing(bits).SequenceEqual(transition.RefusesOn, StringComparer.Ordinal))
                problems.Add($"{name}: the bridge withdrawal says {transition.Member} refuses on [{string.Join(", ", transition.RefusesOn)}], "
                             + $"but its shapes ({string.Join(", ", transition.Shapes)}) are refused on [{string.Join(", ", SwiftTransitionEngines.Refusing(bits))}]");
        }
        return withdrawal;
    }

    /// <summary>
    /// The evidence of a planned forward transition that was judged against evidence and not made
    /// executable, or null. A plan this sidecar does not hold counts as forward: the generation
    /// could not judge it either.
    /// </summary>
    private static string? EvidenceGap(JsonElement entry, bool executable, string? symbol, Dictionary<string, TwinPlan> plans)
    {
        if (executable || !entry.TryGetProperty("status", out var status) || status.GetString() != "Planned")
            return null;
        var evidence = entry.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.String ? ev.GetString() : null;
        if (evidence is null or NoEvidenceReason)
            return null;
        if (symbol is not null && plans.TryGetValue(symbol, out var plan) && plan.Kind != "Forward")
            return null;
        return evidence;
    }

    private static TwinSlot Slot(JsonElement element) => new(
        element.GetProperty("class").GetString() ?? "",
        element.GetProperty("size").GetInt32(),
        element.GetProperty("signed").GetBoolean());
}
