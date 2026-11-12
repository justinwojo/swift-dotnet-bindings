// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BindingsGeneration;

/// <summary>
/// How binding generation judges each module's plans against swiftc lowering evidence: a function
/// from the module's name to its <see cref="SwiftLoweringJudgement"/>. Generation reads it from here
/// rather than from a parameter: it is set once per run by the command that was given
/// <c>--swift-lowering-evidence</c> (authorization by the module's own receipt) or
/// <c>--swift-lowering-qualification</c> (the SDK's qualification record), and with none set no plan
/// is executable, so nothing is bridged and the emission is what it would be without bridges.
/// </summary>
public static class SwiftBridgeEvidence
{
    private static readonly AsyncLocal<Func<string, SwiftLoweringJudgement>?> s_current = new();

    public static Func<string, SwiftLoweringJudgement>? Current => s_current.Value;

    /// <summary>Makes <paramref name="judge"/> current until the returned scope is disposed.</summary>
    public static IDisposable Use(Func<string, SwiftLoweringJudgement>? judge)
    {
        var prior = s_current.Value;
        s_current.Value = judge;
        return new Scope(prior);
    }

    private sealed class Scope(Func<string, SwiftLoweringJudgement>? prior) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            s_current.Value = prior;
        }
    }
}

/// <summary>
/// How one executable forward transition is emitted: through its bridge (the twin <see cref="Twin"/>
/// calls <see cref="Symbol"/>), or, when <see cref="Refusal"/> says why it cannot be, as the
/// Swift-convention import it was.
/// </summary>
/// <param name="Member">The import's IL member, as the ledger names it.</param>
/// <param name="EntryPoint">The Swift symbol the import calls.</param>
/// <param name="Symbol">The bridge the plan names.</param>
/// <param name="Twin">When bridged, the twin's IL member.</param>
/// <param name="Refusal">When not bridged, why (reported as <see cref="SwiftBridgeRouting.RefusedCode"/>).</param>
public sealed record SwiftBridgeRoute(string Member, string EntryPoint, string Symbol, string? Twin, string? Refusal)
{
    public bool IsBridged => Twin is not null;
}

/// <summary>One file of a module's emitted C#, by its name in the output directory.</summary>
public sealed record SwiftBridgeSource(string Name, string Source);

/// <summary>
/// One bridged import's rewrite: in <see cref="File"/>, the import's text <see cref="Original"/>
/// became <see cref="Replacement"/> (its shim and twin).
/// </summary>
/// <param name="File">The file that declares the import; empty when the routing was planned over one whole source.</param>
/// <param name="Member">The import's IL member.</param>
/// <param name="EntryPoint">The Swift symbol the import calls.</param>
/// <param name="Symbol">The bridge the twin calls.</param>
/// <param name="Original">The import's declaration as emitted.</param>
/// <param name="Replacement">The shim and twin written in its place.</param>
public sealed record SwiftBridgeRewrite(string File, string Member, string EntryPoint, string Symbol, string Original, string Replacement);

/// <summary>
/// Routes a module's executable forward plans through their bridges. Each Swift-convention import
/// whose plan is executable becomes two declarations: a shim with the import's own name and
/// signature, so no call site changes, and a C-convention twin import of the bridge, whose arguments
/// are the plan's twin arguments. The bridges themselves are emitted from the same plans into the
/// module's native assembly, so the twin and the bridge are two views of one plan.
/// </summary>
/// <remarks>
/// <para>
/// The route priority is unchanged: a member the emitter routed through a native thunk or a
/// <c>@_cdecl</c> wrapper has no Swift-convention import and so no plan here; only the direct lane is
/// rerouted, and only for a plan the evidence makes executable. Everything else, refused and
/// non-executable plans included, is emitted exactly as before.
/// </para>
/// <para>
/// An executable plan that cannot be bridged — the bridge emitter has no template for it on one of the
/// architectures, or its import's shape is one the shim does not reproduce — keeps its direct import
/// and is reported with the reason (<see cref="RefusedCode"/>). One architecture's refusal refuses
/// the member on every architecture: the managed code is shared, so there is no per-architecture
/// route.
/// </para>
/// <para>
/// The twin keeps the import's marshalling: a <c>SafeHandle</c>, a by-reference parameter and a
/// <c>bool</c> with its <c>MarshalAs</c> stay on the twin, whose source generator adds and releases
/// the reference, pins, and normalizes exactly as it did for the import. The shim only splits
/// aggregates into their pieces, passes the addresses of indirect arguments and results, and supplies
/// the self, indirect-result, result-buffer and error pointers the bridge moves into the Swift
/// registers.
/// </para>
/// </remarks>
public sealed class SwiftBridgeRouting
{
    /// <summary>The diagnostic of an executable plan whose transition kept its direct import.</summary>
    public const string RefusedCode = "SWIFTBIND128";

    /// <summary>The architectures every bridge is emitted for.</summary>
    public static readonly ImmutableArray<SwiftArchitecture> Architectures = [SwiftArchitecture.Arm64, SwiftArchitecture.X86_64];

    private readonly Dictionary<string, SwiftBridgeRewrite> _rewrites;

    private SwiftBridgeRouting(ImmutableArray<SwiftBridgeRoute> routes, ImmutableArray<SwiftCallPlan> bridged,
        Dictionary<string, SwiftBridgeRewrite> rewrites)
    {
        Routes = routes;
        Bridged = bridged;
        _rewrites = rewrites;
    }

    /// <summary>A routing that bridges nothing.</summary>
    public static SwiftBridgeRouting None { get; } = new([], [], new(StringComparer.Ordinal));

    /// <summary>
    /// Each bridged import's replacement, ordered by file, member and entry point: what
    /// <see cref="Rewrite"/> writes in place of the import, and what undoes it.
    /// </summary>
    public ImmutableArray<SwiftBridgeRewrite> Rewrites => _rewrites.Values
        .OrderBy(r => r.File, StringComparer.Ordinal)
        .ThenBy(r => r.Member, StringComparer.Ordinal)
        .ThenBy(r => r.EntryPoint, StringComparer.Ordinal)
        .ToImmutableArray();

    /// <summary>
    /// This routing with nothing bridged: every route it would have bridged keeps its import, for
    /// <paramref name="reason"/>; a route it refused keeps its own reason.
    /// </summary>
    public SwiftBridgeRouting Withdraw(string reason) => Routes.IsEmpty
        ? this
        : new SwiftBridgeRouting(
            Routes.Select(r => r.IsBridged ? r with { Twin = null, Refusal = reason } : r).ToImmutableArray(),
            [],
            new(StringComparer.Ordinal));

    /// <summary>One route per executable forward transition, ordered by member and entry point.</summary>
    public ImmutableArray<SwiftBridgeRoute> Routes { get; }

    /// <summary>The distinct plans the emitted C# calls through a bridge, by symbol.</summary>
    public ImmutableArray<SwiftCallPlan> Bridged { get; }

    public bool BridgesAny => !Bridged.IsEmpty;

    /// <summary>The suffix that names a twin after its shim and its bridge.</summary>
    public static string TwinName(string method, string symbol) => $"{method}__{symbol}";

    /// <summary>
    /// Decides the route of every executable forward transition of <paramref name="plans"/> over the
    /// module's whole emitted C#, <paramref name="source"/>.
    /// </summary>
    /// <param name="library">The library the bridges are linked into: the module's wrapper.</param>
    public static SwiftBridgeRouting Plan(SwiftCallPlanSet plans, string source, string library) =>
        Plan(plans, [new SwiftBridgeSource(string.Empty, source)], library);

    /// <summary>
    /// Decides the route of every executable forward transition of <paramref name="plans"/> over the
    /// module's emitted C# split across <paramref name="files"/>, each a complete compilation unit. An
    /// import is found in whichever file declares it, and two imports of one member and entry point
    /// refuse the route whether or not they share a file.
    /// </summary>
    /// <param name="library">The library the bridges are linked into: the module's wrapper.</param>
    public static SwiftBridgeRouting Plan(SwiftCallPlanSet plans, IReadOnlyList<SwiftBridgeSource> files, string library)
    {
        var executable = plans.Coverage
            .Where(e => e.Transition == SwiftTransitionKind.ForwardPInvoke && e.Status == SwiftPlanStatus.Planned && e.Executable && e.Symbol is not null)
            .ToList();
        if (executable.Count == 0)
            return None;

        var byKey = new Dictionary<string, (SwiftBridgeSource File, MethodDeclarationSyntax Method)>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var (method, member, entryPoint) in ForwardImports(Parse(file.Source)))
            {
                var key = SwiftTransitionLedger.KeyOf(member, entryPoint);
                if (!byKey.TryAdd(key, (file, method)))
                    duplicates.Add(key);
            }
        }
        var plansBySymbol = plans.Plans.ToDictionary(p => p.Symbol, StringComparer.Ordinal);

        var routes = new List<SwiftBridgeRoute>();
        var rewrites = new Dictionary<string, SwiftBridgeRewrite>(StringComparer.Ordinal);
        var bridged = new SortedDictionary<string, SwiftCallPlan>(StringComparer.Ordinal);
        foreach (var entry in executable)
        {
            var key = SwiftTransitionLedger.KeyOf(entry.Member, entry.EntryPoint);
            string? refusal = null;
            string? twin = null;
            if (!plansBySymbol.TryGetValue(entry.Symbol!, out var plan))
                refusal = $"the plan {entry.Symbol} is not in the plan set";
            else if (plan.Body.Kind != SwiftPlanKind.Forward)
                refusal = $"a {plan.Body.Kind} plan is not a symbol's forward call";
            else if (Architectures.Select(a => SwiftBridgeEmitter.Refusal(plan, a) is { } why ? $"{a}: {why}" : null).FirstOrDefault(r => r is not null) is { } noTemplate)
                refusal = $"the bridge emitter has no template for it ({noTemplate})";
            else if (duplicates.Contains(key))
                refusal = "two imports share its member and entry point";
            else if (!byKey.TryGetValue(key, out var found))
                refusal = "its import is not in the emitted C#";
            else
            {
                var (file, method) = found;
                var twinName = TwinName(method.Identifier.ValueText, plan.Symbol);
                var type = method.Parent as TypeDeclarationSyntax;
                var newline = file.Source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                if (type is null)
                    refusal = "its import is not declared in a type";
                else if (type.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == twinName))
                    refusal = $"its type already declares {twinName}";
                else if (SwiftTwinShim.Build(method, plan, library, twinName, IndentOf(file.Source, method.SpanStart), newline, out var replacement) is { } shimRefusal)
                    refusal = shimRefusal;
                else
                {
                    twin = $"{entry.Member[..(entry.Member.LastIndexOf("::", StringComparison.Ordinal) + 2)]}{twinName}";
                    rewrites[key] = new SwiftBridgeRewrite(file.Name, entry.Member, entry.EntryPoint, plan.Symbol, method.ToString(), replacement!);
                    bridged.TryAdd(plan.Symbol, plan);
                }
            }
            routes.Add(new SwiftBridgeRoute(entry.Member, entry.EntryPoint, entry.Symbol!, twin, refusal));
        }

        return new SwiftBridgeRouting(
            routes.OrderBy(r => r.Member, StringComparer.Ordinal).ThenBy(r => r.EntryPoint, StringComparer.Ordinal).ToImmutableArray(),
            bridged.Values.ToImmutableArray(),
            rewrites);
    }

    /// <summary>
    /// Rewrites every bridged import in <paramref name="source"/> — the whole module or one of its
    /// files — into its shim and twin, recording each replacement into <paramref name="journal"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">An import differs from the one the routing was planned over.</exception>
    public string Rewrite(string source, TextEditJournal? journal = null)
    {
        if (_rewrites.Count == 0)
            return source;
        var edits = new List<(int Start, int Length, string Text)>();
        foreach (var (method, member, entryPoint) in ForwardImports(Parse(source)))
        {
            if (!_rewrites.TryGetValue(SwiftTransitionLedger.KeyOf(member, entryPoint), out var rewrite))
                continue;
            var original = method.ToString();
            if (!string.Equals(original, rewrite.Original, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The import {member} ({entryPoint}) differs from the one its bridge route was planned over; a pass between planning and writing changed it.");
            edits.Add((method.SpanStart, method.Span.Length, rewrite.Replacement));
        }
        if (edits.Count == 0)
            return source;

        var text = new StringBuilder(source.Length + edits.Sum(e => e.Text.Length));
        var position = 0;
        foreach (var (start, length, replacement) in edits.OrderBy(e => e.Start))
        {
            text.Append(source, position, start - position);
            text.Append(replacement);
            journal?.Record(start, length, replacement.Length);
            position = start + length;
        }
        text.Append(source, position, source.Length - position);
        return text.ToString();
    }

    /// <summary>The plan set with each executable forward transition's route recorded on its coverage entry.</summary>
    public SwiftCallPlanSet Annotate(SwiftCallPlanSet plans)
    {
        if (Routes.IsEmpty)
            return plans;
        var byKey = Routes.ToDictionary(r => SwiftTransitionLedger.KeyOf(r.Member, r.EntryPoint), StringComparer.Ordinal);
        return new SwiftCallPlanSet
        {
            Module = plans.Module,
            EvidenceScope = plans.EvidenceScope,
            Coverage = plans.Coverage.Select(e =>
                    e.Transition == SwiftTransitionKind.ForwardPInvoke && byKey.TryGetValue(SwiftTransitionLedger.KeyOf(e.Member, e.EntryPoint), out var route)
                        ? e with { Bridged = route.IsBridged, Twin = route.Twin, BridgeRefusal = route.Refusal }
                        : e)
                .ToImmutableArray(),
            Plans = plans.Plans,
        };
    }

    /// <summary>The bridges for <paramref name="architecture"/>, or null when nothing is bridged.</summary>
    /// <exception cref="InvalidOperationException">The emitter refused a plan the routing bridged.</exception>
    public SwiftBridgeAssembly? Assembly(SwiftArchitecture architecture)
    {
        if (!BridgesAny)
            return null;
        var assembly = SwiftBridgeEmitter.Emit(Bridged, architecture);
        if (!assembly.Refused.IsEmpty || assembly.Bridges.Length != Bridged.Length)
            throw new InvalidOperationException(
                $"The bridge emitter refused plans the routing bridged on {architecture}: " +
                string.Join("; ", assembly.Refused.Select(r => $"{r.Symbol}: {r.Reason}")));
        return assembly;
    }

    /// <summary>The bridge assembly's file name: beside the thunks', picked up by the same per-architecture glob.</summary>
    public static string AssemblyFileName(string @namespace, SwiftArchitecture architecture) =>
        $"{@namespace}.Bridges.{(architecture == SwiftArchitecture.Arm64 ? "arm64" : "x86_64")}.s";

    /// <summary>A directory glob that matches every bridge assembly file and no thunk file.</summary>
    public const string AssemblyFilePattern = "*.Bridges.*.s";

    private static CompilationUnitSyntax Parse(string source) =>
        (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();

    /// <summary>Every Swift-convention import of <paramref name="root"/>, with its IL member and entry point.</summary>
    private static IEnumerable<(MethodDeclarationSyntax Method, string Member, string EntryPoint)> ForwardImports(SyntaxNode root)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var attributes = method.AttributeLists.SelectMany(l => l.Attributes).ToList();
            var import = attributes.FirstOrDefault(a => SwiftTransitionSyntax.IsAttribute(a, "LibraryImport") || SwiftTransitionSyntax.IsAttribute(a, "DllImport"));
            var callConv = attributes.FirstOrDefault(a => SwiftTransitionSyntax.IsAttribute(a, "UnmanagedCallConv"));
            if (import is null || callConv is null || !SwiftTransitionSyntax.NamesCallConvSwift(callConv))
                continue;
            var entryArgument = import.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "EntryPoint");
            var entryPoint = entryArgument?.Expression is LiteralExpressionSyntax literal ? literal.Token.ValueText : method.Identifier.ValueText;
            yield return (method, SwiftTransitionSyntax.MemberOf(method), entryPoint);
        }
    }

    private static string IndentOf(string source, int position)
    {
        var lineStart = source.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        var end = lineStart;
        while (end < position && source[end] is ' ' or '\t')
            end++;
        return source[lineStart..end];
    }
}
