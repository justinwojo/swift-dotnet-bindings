// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>Where a transition's facts came from.</summary>
public enum SwiftCallFactsSource
{
    /// <summary>The callee is declared: its facts come from the ABI JSON through the parsed model.</summary>
    Declared,

    /// <summary>The callee was synthesized into the wrapper: its producer described it.</summary>
    Synthesized,

    /// <summary>The callee is a Swift closure: its facts come from the closure's function type.</summary>
    ClosureType,
}

/// <summary>Which kind of managed↔Swift transition a ledger entry lists.</summary>
public enum SwiftTransitionKind
{
    /// <summary>A forward <c>CallConvSwift</c> P/Invoke.</summary>
    ForwardPInvoke,

    /// <summary>
    /// A P/Invoke of another convention whose signature carries <c>SwiftSelf</c>, <c>SwiftSelf&lt;T&gt;</c>
    /// or <c>SwiftIndirectResult</c>: a C-convention wrapper call that names a Swift register type.
    /// </summary>
    CdeclSpecialPInvoke,

    /// <summary>An <c>[UnmanagedCallersOnly]</c> method with the Swift convention: a reverse callback Swift calls as a closure.</summary>
    ReverseCallback,

    /// <summary>
    /// Where managed code takes a reverse callback's address (<c>&amp;Callback</c>) to hand Swift as a
    /// closure function: the start of its construction site.
    /// </summary>
    CallbackAddress,

    /// <summary>A forward <c>calli</c> through an <c>unmanaged[Swift]</c> function pointer: a call of a closure Swift returned.</summary>
    ForwardCalli,
}

/// <summary>
/// Whether a forward transition's managed signature carries only primitive carriers (design invariant
/// I1): integers, <c>nint</c>/<c>nuint</c>, <c>bool</c>, <c>char</c>, <c>float</c>, <c>double</c>,
/// pointers, function pointers, enums and the allow-listed one-word runtime wrappers. A primitive-only
/// transition is placed identically by every runtime and is not bridged.
/// </summary>
public enum SwiftTransitionCarriers
{
    Primitive,
    NonPrimitive,
}

/// <summary>
/// The forward call a reverse callback's closure is passed to, and how that callee takes it: the
/// convention decides whether the caller keeps or hands over its reference to the closure context.
/// </summary>
/// <param name="Symbol">The symbol of the forward callee the closure is an argument of.</param>
/// <param name="ParameterName">The closure parameter's name, as the producer knew it.</param>
/// <param name="ParameterIndex">Its position among the callee's formal parameters, when the callee's facts name it.</param>
public sealed record SwiftClosureOwner(
    string Symbol,
    string ParameterName,
    int? ParameterIndex = null,
    SwiftParameterConvention? Convention = null,
    SwiftClosureKind? Closure = null);

/// <summary>
/// What the producer of a Swift-convention reverse callback knew when it rendered it: the callback's
/// name, the facts of the closure Swift calls it as, and the forward callee its closure is passed to.
/// </summary>
public sealed record SwiftCallbackRecord
{
    /// <summary>The <c>[UnmanagedCallersOnly]</c> method's name.</summary>
    public required string CallbackName { get; init; }

    public required SwiftCallFacts Facts { get; init; }

    /// <summary>The forward callee the closure is an argument of, or null when the producer does not know it.</summary>
    public SwiftClosureOwner? Owner { get; init; }

    /// <summary>Why the owner's parameter is not described, when it is not.</summary>
    public string? OwnerReason { get; init; }

    /// <summary>
    /// The record a callback producer makes. The owner's facts are looked up by its symbol. Its
    /// closure parameter is the owner's only closure parameter; among several, the one whose name the
    /// producer wrote, when exactly one matches. Anything else leaves the owner's convention unknown.
    /// </summary>
    public static SwiftCallbackRecord For(
        string callbackName, ClosureTypeSpec closure, string parameterName, string ownerSymbol,
        ModuleEmissionContext context)
    {
        string? reason = null;
        SwiftClosureOwner owner = new(ownerSymbol, parameterName);
        var genericNames = new List<string>();
        string? module = null;
        if (context.TryResolveCallFacts(ownerSymbol, out var ownerFacts, out _))
        {
            module = ownerFacts.Target.Module;
            genericNames.AddRange(ownerFacts.Generics.Parameters);
            var closures = ownerFacts.Parameters.Where(p => p.Closure != SwiftClosureKind.NotClosure).ToList();
            var named = closures.Where(p => string.Equals(p.Name, parameterName, StringComparison.Ordinal)).ToList();
            var parameter = closures.Count == 1 ? closures[0] : named.Count == 1 ? named[0] : null;
            if (parameter is not null)
                owner = owner with { ParameterIndex = parameter.Index, Convention = parameter.Convention, Closure = parameter.Closure };
            else if (closures.Count == 0)
                reason = $"the facts of {ownerSymbol} list no closure parameter";
            else
                reason = $"{ownerSymbol} has {closures.Count} closure parameters and {named.Count} named '{parameterName}'";
        }
        else
        {
            reason = $"no facts are known for the owner {ownerSymbol}";
        }

        var facts = context.SwiftCallFacts.DescribeClosure(
            closure, ClosurePointerSource(ownerSymbol, parameterName), genericNames, module);
        return new SwiftCallbackRecord { CallbackName = callbackName, Facts = facts, Owner = owner, OwnerReason = reason };
    }

    /// <summary>The pointer source naming the closure passed as one parameter of a callee.</summary>
    public static string ClosurePointerSource(string ownerSymbol, string parameterName) =>
        $"closure argument '{parameterName}' of {ownerSymbol}";
}

/// <summary>
/// What the producer of a forward <c>calli</c> through an <c>unmanaged[Swift]</c> pointer knew: the
/// function-pointer type it cast to, and the facts of the closure it calls.
/// </summary>
public sealed record SwiftCalliRecord
{
    /// <summary>The function-pointer type the call goes through, as the producer wrote it.</summary>
    public required string FunctionPointerType { get; init; }

    public required SwiftCallFacts Facts { get; init; }

    /// <summary>The pointer source naming a closure a callee returned.</summary>
    public static string ClosurePointerSource(string closureSpelling) => $"closure of type {closureSpelling}";
}

/// <summary>
/// What a producer of a forward Swift-convention P/Invoke knew when it rendered the declaration: the
/// declaration's identity and the facts of the symbol it binds, or why there are none. Recorded as a
/// side effect of rendering, so it never changes emitted text.
/// </summary>
public sealed record SwiftTransitionRecord
{
    public required string MethodName { get; init; }

    public required string EntryPoint { get; init; }

    public required string Library { get; init; }

    /// <summary>The facts of the bound symbol, or null when the producer had none.</summary>
    public SwiftCallFacts? Facts { get; init; }

    /// <summary>Where <see cref="Facts"/> came from. Meaningless when there are none.</summary>
    public SwiftCallFactsSource Source { get; init; }

    /// <summary>Why there are no facts, when there are none.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The record a producer makes for one rendered declaration. Facts the producer supplied win;
    /// otherwise they are resolved from the context by the bound symbol: a synthesized function its
    /// producer registered, then a declared symbol.
    /// </summary>
    public static SwiftTransitionRecord For(
        string methodName, string entryPoint, string library, SwiftCallFacts? supplied, ModuleEmissionContext context)
    {
        if (supplied is not null)
        {
            return new SwiftTransitionRecord
            {
                MethodName = methodName,
                EntryPoint = entryPoint,
                Library = library,
                Facts = supplied,
                Source = supplied.Target.Kind == SwiftCallTargetKind.SyntheticWrapper
                    ? SwiftCallFactsSource.Synthesized
                    : SwiftCallFactsSource.Declared,
            };
        }

        if (context.TryResolveCallFacts(entryPoint, out var facts, out var source))
        {
            return new SwiftTransitionRecord
            {
                MethodName = methodName,
                EntryPoint = entryPoint,
                Library = library,
                Facts = facts,
                Source = source,
            };
        }

        return new SwiftTransitionRecord
        {
            MethodName = methodName,
            EntryPoint = entryPoint,
            Library = library,
            Reason = "no declaration names the symbol, and no producer registered a synthesized function for it",
        };
    }
}

/// <summary>The ledger's verdict on one transition.</summary>
public enum SwiftTransitionLedgerStatus
{
    /// <summary>The producer recorded facts, and every fact is determined.</summary>
    Complete,

    /// <summary>The producer recorded facts, but some are unknown; the reasons are listed.</summary>
    Incomplete,

    /// <summary>No facts: either no producer recorded the declaration, or it recorded none. The reason is listed.</summary>
    Unplanned,
}

/// <summary>One emitted managed↔Swift transition and the facts its producer recorded.</summary>
/// <param name="Member">
/// The IL member that holds the transition: <c>Namespace.Outer+Inner::Method</c>, a generic type with
/// its arity (<c>Box`1</c>), a lambda as <c>Outer.&lt;lambda&gt;</c>. With the kind-specific part of
/// <see cref="Key"/> this is what an IL inventory joins on.
/// </param>
/// <param name="EntryPoint">An import's bound symbol; empty for every other kind.</param>
/// <param name="Library">An import's library; empty for every other kind.</param>
public sealed record SwiftTransitionLedgerEntry(
    string Member,
    string EntryPoint,
    string Library,
    SwiftTransitionLedgerStatus Status,
    string? FactsId,
    SwiftCallFactsSource? FactsSource,
    SwiftWrapperSynthesizer? Synthesizer,
    ImmutableArray<string> Reasons)
{
    public SwiftTransitionKind Transition { get; init; } = SwiftTransitionKind.ForwardPInvoke;

    /// <summary>For an import or a <c>calli</c>: whether its managed signature carries only primitives.</summary>
    public SwiftTransitionCarriers? Carriers { get; init; }

    /// <summary>For a callback address: the IL member of the callback whose address is taken.</summary>
    public string? Target { get; init; }

    /// <summary>For a reverse callback and its address: the forward callee its closure is passed to.</summary>
    public SwiftClosureOwner? Owner { get; init; }

    /// <summary>For a C-convention import: the Swift register types its signature names.</summary>
    public ImmutableArray<string>? Specials { get; init; }

    /// <summary>
    /// For a <c>calli</c> whose pointer type several closures of different Swift types share: the facts
    /// of each, by id. Which one the call is cannot be told from the C#, so no single facts id is cited.
    /// </summary>
    public ImmutableArray<string>? Candidates { get; init; }

    /// <summary>The managed signature the site passes, read from the emitted C#; not serialized.</summary>
    [JsonIgnore]
    public SwiftManagedSignature? Signature { get; init; }

    /// <summary>
    /// For a forward import the emitted C# reaches through a bridge: the bridge and its twin. The entry
    /// still describes the transition the import declared; the compiled IL holds the twin in its place.
    /// </summary>
    public SwiftTransitionBridge? Bridge { get; init; }

    /// <summary>
    /// The join key with an IL inventory site of the same kind: an import's member and entry point; a
    /// callback's member; a callback address's member and target; a <c>calli</c>'s member.
    /// </summary>
    public string Key => Transition switch
    {
        SwiftTransitionKind.ForwardPInvoke or SwiftTransitionKind.CdeclSpecialPInvoke => SwiftTransitionLedger.KeyOf(Member, EntryPoint),
        SwiftTransitionKind.CallbackAddress => SwiftTransitionLedger.KeyOf(Member, Target ?? ""),
        _ => Member,
    };
}

/// <summary>A bridged import's bridge symbol and the IL member of the twin that calls it.</summary>
public sealed record SwiftTransitionBridge(string Symbol, string Twin);

/// <summary>
/// The transition coverage ledger of one module: one entry per managed↔Swift transition in the
/// module's emitted C#, with the facts its producer recorded, or why there are none. A transition is a
/// forward <c>CallConvSwift</c> P/Invoke, an import of another convention that names a Swift register
/// type, a Swift-convention <c>[UnmanagedCallersOnly]</c> callback, a place a callback's address is
/// taken to construct a closure, or a <c>calli</c> through an <c>unmanaged[Swift]</c> pointer.
/// </summary>
/// <remarks>
/// <para>
/// The emitted set is read from the rendered C# itself, not from what producers recorded, so a
/// producer that renders a transition without recording it cannot drop out of the ledger: it is
/// listed <see cref="SwiftTransitionLedgerStatus.Unplanned"/> with the reason. A recorded transition
/// that did not reach the output (a member rolled back after it was rendered) is not listed.
/// </para>
/// <para>
/// Entries carry the IL's own naming (<see cref="SwiftTransitionLedgerEntry.Member"/>), so a cross-check
/// against the transition gate's IL inventory joins on <see cref="SwiftTransitionLedgerEntry.Key"/> per
/// kind and can demand a bijection. Writing the ledger changes nothing that is emitted.
/// </para>
/// </remarks>
public sealed class SwiftTransitionLedger
{
    /// <summary>The ledger schema. Bump it on any change to the ledger's shape.</summary>
    public const int SchemaVersion = 2;

    public required string Module { get; init; }

    public required ImmutableArray<SwiftTransitionLedgerEntry> Entries { get; init; }

    /// <summary>The facts the entries cite, one per id.</summary>
    public required ImmutableArray<SwiftCallFacts> Facts { get; init; }

    /// <summary>
    /// Anything that makes the ledger less than the whole emitted set or less than consistent: a
    /// Swift-convention declaration in an inactive preprocessor region (not listed), a declaration
    /// two producers recorded with different facts, a synthesized symbol registered twice differently.
    /// </summary>
    public ImmutableArray<string> Issues { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// How many functions each synthesizer wrote into the wrapper and described, whether or not a
    /// Swift-convention declaration binds them (most are reached through a C-convention wrapper).
    /// </summary>
    public ImmutableSortedDictionary<string, int> Registered { get; init; } =
        ImmutableSortedDictionary<string, int>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>The value types the emitted C# declares, which the entries' signatures name; not serialized.</summary>
    public SwiftManagedTypeIndex Types { get; init; } = SwiftManagedTypeIndex.Empty;

    /// <summary>The join key of a member and an entry point.</summary>
    public static string KeyOf(string member, string entryPoint) => string.Concat(member, " ", entryPoint);

    /// <summary>This ledger with each bridged forward import's bridge and twin recorded on its entry.</summary>
    public SwiftTransitionLedger WithBridges(IEnumerable<SwiftBridgeRoute> routes)
    {
        var bridged = routes.Where(r => r.IsBridged)
            .ToDictionary(r => KeyOf(r.Member, r.EntryPoint), r => new SwiftTransitionBridge(r.Symbol, r.Twin!), StringComparer.Ordinal);
        if (bridged.Count == 0)
            return this;
        return new SwiftTransitionLedger
        {
            Module = Module,
            Entries = Entries.Select(e => e.Transition == SwiftTransitionKind.ForwardPInvoke && bridged.TryGetValue(e.Key, out var bridge) ? e with { Bridge = bridge } : e)
                .ToImmutableArray(),
            Facts = Facts,
            Issues = Issues,
            Registered = Registered,
            Types = Types,
        };
    }

    public int Count(SwiftTransitionLedgerStatus status) => Entries.Count(e => e.Status == status);

    /// <summary>
    /// Builds the ledger from a module's rendered C# and the records its producers made while
    /// rendering it.
    /// </summary>
    public static SwiftTransitionLedger Build(
        string module,
        string csharpSource,
        IEnumerable<SwiftTransitionRecord> records,
        IEnumerable<string>? additionalIssues = null,
        IEnumerable<SyntheticSwiftFunction>? registered = null,
        IEnumerable<SwiftCallbackRecord>? callbacks = null,
        IEnumerable<SwiftCalliRecord>? callis = null) =>
        Build(module, new[] { csharpSource }, records, additionalIssues, registered, callbacks, callis);

    /// <summary>The ledger of C# split across several files, each a complete compilation unit.</summary>
    public static SwiftTransitionLedger Build(
        string module,
        IEnumerable<string> csharpSources,
        IEnumerable<SwiftTransitionRecord> records,
        IEnumerable<string>? additionalIssues = null,
        IEnumerable<SyntheticSwiftFunction>? registered = null,
        IEnumerable<SwiftCallbackRecord>? callbacks = null,
        IEnumerable<SwiftCalliRecord>? callis = null)
    {
        var issues = new List<string>(additionalIssues ?? Array.Empty<string>());
        var types = new SwiftManagedTypeIndex();
        var sites = csharpSources.SelectMany(source => SwiftTransitionSyntax.Scan(source, issues, types)).ToList();

        var importRecords = records
            .GroupBy(r => (r.MethodName, r.EntryPoint, r.Library))
            .ToDictionary(g => g.Key, g => g.ToList());
        var callbackRecords = (callbacks ?? Array.Empty<SwiftCallbackRecord>())
            .GroupBy(r => r.CallbackName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var calliRecords = (callis ?? Array.Empty<SwiftCalliRecord>())
            .GroupBy(r => SwiftTransitionSyntax.NormalizeFunctionPointerType(r.FunctionPointerType), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var entries = ImmutableArray.CreateBuilder<SwiftTransitionLedgerEntry>(sites.Count);
        var cited = new SortedDictionary<string, SwiftCallFacts>(StringComparer.Ordinal);
        foreach (var site in sites)
        {
            var entry = site.Kind switch
            {
                SwiftTransitionKind.ForwardPInvoke or SwiftTransitionKind.CdeclSpecialPInvoke => ImportEntry(site, importRecords, cited, issues),
                SwiftTransitionKind.ReverseCallback or SwiftTransitionKind.CallbackAddress => CallbackEntry(site, callbackRecords, cited, issues),
                _ => CalliEntry(site, calliRecords, cited),
            };
            entries.Add(entry with
            {
                Transition = site.Kind,
                Carriers = site.Carriers,
                Target = site.Target,
                Specials = site.Kind == SwiftTransitionKind.CdeclSpecialPInvoke ? site.Specials : null,
                Signature = site.Signature,
            });
        }

        return new SwiftTransitionLedger
        {
            Module = module,
            Types = types,
            Entries = entries
                .OrderBy(e => e.Transition)
                .ThenBy(e => e.Member, StringComparer.Ordinal)
                .ThenBy(e => e.EntryPoint, StringComparer.Ordinal)
                .ThenBy(e => e.Library, StringComparer.Ordinal)
                .ThenBy(e => e.Target, StringComparer.Ordinal)
                .ToImmutableArray(),
            Facts = cited.Values.ToImmutableArray(),
            Issues = issues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(),
            Registered = (registered ?? Array.Empty<SyntheticSwiftFunction>())
                .GroupBy(f => f.Synthesizer.ToString())
                .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        };
    }

    private static SwiftTransitionLedgerEntry Unplanned(SwiftTransitionSite site, IEnumerable<string> reasons) => new(
        site.Member, site.EntryPoint, site.Library, SwiftTransitionLedgerStatus.Unplanned, null, null, null,
        reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray());

    private static SwiftTransitionLedgerEntry Cite(
        SwiftTransitionSite site, SwiftCallFacts facts, SwiftCallFactsSource source,
        SortedDictionary<string, SwiftCallFacts> cited, IEnumerable<string>? extraReasons = null)
    {
        cited.TryAdd(facts.Id, facts);
        var reasons = facts.Unknowns.Concat(extraReasons ?? Array.Empty<string>()).ToImmutableArray();
        return new SwiftTransitionLedgerEntry(
            site.Member, site.EntryPoint, site.Library,
            reasons.IsEmpty ? SwiftTransitionLedgerStatus.Complete : SwiftTransitionLedgerStatus.Incomplete,
            facts.Id, source, facts.Target.Synthesizer, reasons);
    }

    private static SwiftTransitionLedgerEntry ImportEntry(
        SwiftTransitionSite site, Dictionary<(string, string, string), List<SwiftTransitionRecord>> records,
        SortedDictionary<string, SwiftCallFacts> cited, List<string> issues)
    {
        if (!records.TryGetValue((site.Method, site.EntryPoint, site.Library), out var matches))
            return Unplanned(site, ["no producer recorded this declaration"]);
        var withFacts = matches.Where(m => m.Facts is not null).ToList();
        if (withFacts.Count == 0)
            return Unplanned(site, matches.Select(m => m.Reason ?? "the producer recorded no facts"));

        var distinctFacts = withFacts.Select(m => m.Facts!).Distinct().ToList();
        string[]? conflict = null;
        if (distinctFacts.Count > 1)
        {
            issues.Add($"{site.Member} ({site.EntryPoint}): producers recorded {distinctFacts.Count} different facts; the first by id is cited");
            // Which of them is the call's is not determined, so the entry is not complete.
            conflict = [$"producers recorded {distinctFacts.Count} different facts"];
        }
        var record = withFacts.OrderBy(m => m.Facts!.Id, StringComparer.Ordinal).ThenBy(m => (int)m.Source).First();
        return Cite(site, record.Facts!, record.Source, cited, conflict);
    }

    private static SwiftTransitionLedgerEntry CallbackEntry(
        SwiftTransitionSite site, Dictionary<string, List<SwiftCallbackRecord>> records,
        SortedDictionary<string, SwiftCallFacts> cited, List<string> issues)
    {
        if (site.CallbackName is null || !records.TryGetValue(site.CallbackName, out var matches))
            return Unplanned(site, ["no producer recorded this callback"]);
        var distinct = matches.Distinct().ToList();
        var reasons = new List<string>();
        if (distinct.Count > 1 && site.Kind == SwiftTransitionKind.ReverseCallback)
        {
            issues.Add($"{site.Member}: producers recorded {distinct.Count} different descriptions of this callback; the first by id is cited");
            reasons.Add($"producers recorded {distinct.Count} different descriptions of this callback");
        }
        var record = distinct.OrderBy(r => r.Facts.Id, StringComparer.Ordinal).First();
        if (record.OwnerReason is { } reason)
            reasons.Add("closure owner: " + reason);
        return Cite(site, record.Facts, SwiftCallFactsSource.ClosureType, cited, reasons) with { Owner = record.Owner };
    }

    private static SwiftTransitionLedgerEntry CalliEntry(
        SwiftTransitionSite site, Dictionary<string, List<SwiftCalliRecord>> records, SortedDictionary<string, SwiftCallFacts> cited)
    {
        var key = SwiftTransitionSyntax.NormalizeFunctionPointerType(site.FunctionPointerType ?? "");
        if (!records.TryGetValue(key, out var matches))
            return Unplanned(site, ["no producer recorded a call through this pointer type"]);
        var distinct = matches.Select(m => m.Facts).Distinct().OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        if (distinct.Count == 1)
            return Cite(site, distinct[0], SwiftCallFactsSource.ClosureType, cited);

        // A pointer type is what the call site shows; closures of different Swift types can share one.
        // Then which closure this call is cannot be told from the C#: every candidate's facts are kept,
        // and none is cited as the call's own.
        foreach (var facts in distinct)
            cited.TryAdd(facts.Id, facts);
        return new SwiftTransitionLedgerEntry(
            site.Member, site.EntryPoint, site.Library, SwiftTransitionLedgerStatus.Incomplete, null, SwiftCallFactsSource.ClosureType, null,
            [$"{distinct.Count} closures of different Swift types are called through this pointer type"])
        {
            Candidates = distinct.Select(f => f.Id).ToImmutableArray(),
        };
    }

    /// <summary>
    /// The keys of <paramref name="sites"/> (member and entry point, as an IL inventory names them)
    /// that have no forward Swift-convention P/Invoke entry, and the entries no site matches. Both
    /// empty is a bijection. Multiplicity counts: a key the IL has twice needs two entries.
    /// </summary>
    public (ImmutableArray<string> MissingFromLedger, ImmutableArray<string> MissingFromSites) Compare(
        IEnumerable<(string Member, string EntryPoint)> sites)
    {
        var remaining = Entries.Where(e => e.Transition == SwiftTransitionKind.ForwardPInvoke)
            .GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var (member, entryPoint) in sites)
        {
            var key = KeyOf(member, entryPoint);
            if (remaining.TryGetValue(key, out var n) && n > 0)
                remaining[key] = n - 1;
            else
                missing.Add(key);
        }
        var extra = remaining.Where(kv => kv.Value > 0)
            .SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value))
            .Order(StringComparer.Ordinal);
        return (missing.Order(StringComparer.Ordinal).ToImmutableArray(), extra.ToImmutableArray());
    }

    // ---- the sidecar ----

    /// <summary>The ledger's file name for a module namespace.</summary>
    public static string FileName(string @namespace) => $"{@namespace}.swift-transition-ledger.json";

    public SwiftTransitionLedgerDocument ToDocument() => new()
    {
        Schema = SchemaVersion,
        FactsVersion = SwiftCallFacts.CurrentVersion,
        Module = Module,
        Totals = new SwiftTransitionLedgerTotals
        {
            Transitions = Entries.Length,
            Complete = Count(SwiftTransitionLedgerStatus.Complete),
            Incomplete = Count(SwiftTransitionLedgerStatus.Incomplete),
            Unplanned = Count(SwiftTransitionLedgerStatus.Unplanned),
            BySource = Entries.Where(e => e.FactsSource is not null)
                .GroupBy(e => e.FactsSource!.Value.ToString())
                .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            BySynthesizer = Entries.Where(e => e.Synthesizer is not null)
                .GroupBy(e => e.Synthesizer!.Value.ToString())
                .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            ByTargetKind = Entries.Where(e => e.FactsId is not null)
                .Join(Facts, e => e.FactsId, f => f.Id, (e, f) => f.Target.Kind.ToString())
                .GroupBy(k => k)
                .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            RegisteredBySynthesizer = Registered,
            ByTransition = Entries.GroupBy(e => e.Transition.ToString())
                .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            ByCarriers = Entries.Where(e => e.Carriers is not null)
                .GroupBy(e => $"{e.Transition}/{e.Carriers}")
                .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        },
        Issues = Issues,
        Transitions = Entries,
        Facts = Facts,
    };

    public string ToJson() =>
        JsonSerializer.Serialize(ToDocument(), SwiftTransitionLedgerJsonContext.Default.SwiftTransitionLedgerDocument) + "\n";

    /// <summary>
    /// The ledger sidecar <paramref name="json"/> with its forward imports' bridges recorded as
    /// <paramref name="routes"/> route them: a bridged route's entry names its bridge and twin, and every
    /// other forward entry names none. Everything else in the sidecar is kept as written.
    /// </summary>
    /// <exception cref="InvalidDataException">The sidecar is not a ledger of this schema.</exception>
    public static string WithBridges(string json, IEnumerable<SwiftBridgeRoute> routes)
    {
        var bridged = routes.Where(r => r.IsBridged)
            .ToDictionary(r => KeyOf(r.Member, r.EntryPoint), r => r, StringComparer.Ordinal);
        if (JsonNode.Parse(json) is not JsonObject root || root["schema"]?.GetValue<int>() != SchemaVersion
            || root["transitions"] is not JsonArray transitions)
            throw new InvalidDataException($"the sidecar is not a transition ledger of schema {SchemaVersion}");
        foreach (var entry in transitions.OfType<JsonObject>())
        {
            if (entry["transition"]?.GetValue<string>() != nameof(SwiftTransitionKind.ForwardPInvoke))
                continue;
            entry.Remove("bridge");
            var key = KeyOf(entry["member"]?.GetValue<string>() ?? "", entry["entryPoint"]?.GetValue<string>() ?? "");
            if (!bridged.TryGetValue(key, out var route))
                continue;
            // Where the serializer writes it: after the entry's other fields, before its key.
            var bridge = new JsonObject { ["symbol"] = route.Symbol, ["twin"] = route.Twin };
            var at = entry.IndexOf("key");
            if (at < 0)
                entry["bridge"] = bridge;
            else
                entry.Insert(at, "bridge", bridge);
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>
    /// Writes the ledger next to the module's C#. A diagnostic artifact: a failure to write it is
    /// logged and never fails generation; the cross-check that consumes it fails closed on a missing file.
    /// </summary>
    public void Write(string outputDirectory, string @namespace, ILogger logger)
    {
        try
        {
            File.WriteAllText(Path.Combine(outputDirectory, FileName(@namespace)), ToJson());
            var unplanned = Count(SwiftTransitionLedgerStatus.Unplanned);
            if (unplanned > 0 || Issues.Length > 0)
                logger.LogInformation(
                    "Swift transition ledger for {Module}: {Unplanned} of {Total} transitions have no facts; {Issues} issue(s)",
                    Module, unplanned, Entries.Length, Issues.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            logger.LogWarning("Could not write the Swift transition ledger for {Module}: {Message}", Module, ex.Message);
        }
    }
}

/// <summary>The serialized form of <see cref="SwiftTransitionLedger"/>.</summary>
public sealed record SwiftTransitionLedgerDocument
{
    public required int Schema { get; init; }
    public required int FactsVersion { get; init; }
    public required string Module { get; init; }
    public required SwiftTransitionLedgerTotals Totals { get; init; }
    public required ImmutableArray<string> Issues { get; init; }
    public required ImmutableArray<SwiftTransitionLedgerEntry> Transitions { get; init; }
    public required ImmutableArray<SwiftCallFacts> Facts { get; init; }
}

/// <summary>The ledger's counts.</summary>
public sealed record SwiftTransitionLedgerTotals
{
    public required int Transitions { get; init; }
    public required int Complete { get; init; }
    public required int Incomplete { get; init; }
    public required int Unplanned { get; init; }
    public required ImmutableSortedDictionary<string, int> BySource { get; init; }
    public required ImmutableSortedDictionary<string, int> BySynthesizer { get; init; }
    public required ImmutableSortedDictionary<string, int> ByTargetKind { get; init; }
    public required ImmutableSortedDictionary<string, int> RegisteredBySynthesizer { get; init; }

    /// <summary>Entries per transition kind.</summary>
    public required ImmutableSortedDictionary<string, int> ByTransition { get; init; }

    /// <summary>Entries per transition kind and carrier class (<c>ForwardPInvoke/Primitive</c>), for the kinds that have one.</summary>
    public required ImmutableSortedDictionary<string, int> ByCarriers { get; init; }
}

/// <summary>Source-generated, trim-safe serialization of the ledger.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SwiftTransitionLedgerDocument))]
internal partial class SwiftTransitionLedgerJsonContext : JsonSerializerContext
{
}
