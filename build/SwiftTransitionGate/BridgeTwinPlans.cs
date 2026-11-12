// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;

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

    private BridgeTwinPlans(Dictionary<string, TwinPlan> plans, ImmutableArray<string> problems, ImmutableArray<string> routingGaps, ImmutableArray<string> evidenceGaps)
    {
        _plans = plans;
        Problems = problems;
        RoutingGaps = routingGaps;
        EvidenceGaps = evidenceGaps;
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
        var gaps = new List<string>();
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
        return new BridgeTwinPlans(plans, problems.ToImmutableArray(), gaps.ToImmutableArray(), evidenceGaps.ToImmutableArray());
    }

    /// <summary>Plans given as (name, JSON text), for tests.</summary>
    public static BridgeTwinPlans FromSidecars(IEnumerable<(string Name, string Json)> sidecars)
    {
        var problems = new List<string>();
        var gaps = new List<string>();
        var evidenceGaps = new List<string>();
        var plans = new Dictionary<string, TwinPlan>(StringComparer.Ordinal);
        foreach (var (name, json) in sidecars)
            Read(name, json, plans, problems, gaps, evidenceGaps);
        return new BridgeTwinPlans(plans, problems.ToImmutableArray(), gaps.ToImmutableArray(), evidenceGaps.ToImmutableArray());
    }

    private static void Read(string name, string json, Dictionary<string, TwinPlan> plans, List<string> problems, List<string> gaps, List<string> evidenceGaps)
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
            if (root.TryGetProperty("coverage", out var coverage))
            {
                foreach (var entry in coverage.EnumerateArray())
                {
                    var executable = entry.TryGetProperty("executable", out var e) && e.ValueKind == JsonValueKind.True;
                    var bridged = entry.TryGetProperty("bridged", out var b) && b.ValueKind == JsonValueKind.True;
                    var member = entry.TryGetProperty("member", out var m) ? m.GetString() : null;
                    var target = entry.TryGetProperty("symbol", out var s) ? s.GetString() : null;
                    if (EvidenceGap(entry, executable, target, plans) is { } evidence)
                        evidenceGaps.Add($"{name}: {member} is planned through {target}, but its lowering evidence does not make it executable: {evidence}");
                    if (executable == bridged)
                        continue;
                    var refusal = entry.TryGetProperty("bridgeRefusal", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    gaps.Add(executable
                        ? $"{name}: {member} is executable through {target} but kept its direct import: {refusal ?? "nothing routed it"}"
                        : $"{name}: {member} is called through {target}, which the evidence does not prove executable");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            problems.Add($"{name}: the call plans do not read: {ex.Message}");
        }
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
