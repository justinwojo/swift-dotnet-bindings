// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration;

/// <summary>One direct Swift-convention transition the emitted C# keeps, and the shapes it has.</summary>
/// <param name="Name">The constant its shapes are recorded under in the module's guard class.</param>
/// <param name="Kind">An import (<see cref="SwiftTransitionKind.ForwardPInvoke"/>) or a call through a function pointer (<see cref="SwiftTransitionKind.ForwardCalli"/>).</param>
/// <param name="Member">The IL member: the import, or the function holding the call.</param>
/// <param name="EntryPoint">An import's symbol; for a call, its pointer type and its ordinal in the function.</param>
/// <param name="Facts">The shapes some runtimes do not execute correctly.</param>
internal sealed record SwiftTransitionHazard(string Name, SwiftTransitionKind Kind, string Member, string EntryPoint, SwiftTransitionFacts Facts);

/// <summary>One guarded function.</summary>
/// <param name="Key">Its identity in the C#, the same in the whole module and in the file that declares it.</param>
/// <param name="Display">How the guard names it to a caller.</param>
/// <param name="Kind">Method, Constructor, Property, Indexer, Event or Operator.</param>
/// <param name="Name">Its member name.</param>
/// <param name="ContainingType">Its declaring type, namespace-qualified.</param>
/// <param name="Visible">Whether code outside the binding can call it: through its accessibility, an override or an interface.</param>
/// <param name="Direct">Whether its own body holds one of the transitions.</param>
/// <param name="Transitions">The constants of every transition it reaches.</param>
internal sealed record SwiftTransitionGuardSite(
    string Key, string Display, string Kind, string Name, string ContainingType, bool Visible, bool Direct, ImmutableArray<string> Transitions);

/// <summary>
/// Guards the members of a module that reach a direct Swift-convention transition of a shape some
/// runtimes do not execute correctly (<see cref="SwiftTransitionFacts"/>), so that on those runtimes
/// they refuse — <see cref="SwiftTransitionGuard.ThrowIfUnsupported"/>, as their first statement —
/// before any native work, and run unchanged everywhere else.
/// </summary>
/// <remarks>
/// <para>
/// The transitions are read from the module's final C#: a <c>[UnmanagedCallConv(CallConvSwift)]</c>
/// import, or a call through an <c>unmanaged[Swift]</c> function pointer, whose signature has a shape
/// (<see cref="FactsOf(SeparatedSyntaxList{ParameterSyntax}, TypeSyntax, HashSet{string}, bool)"/>).
/// An import a native bridge routes is no longer one, so the guards are planned after routing.
/// </para>
/// <para>
/// The members to guard come from the reverse call graph of the module, bound with a semantic model:
/// every function whose body holds a transition, and every member code outside the binding can call
/// whose calls reach one. A call through a virtual or interface member is a dispatch boundary — the
/// target guards itself, as any implementation it can dispatch to does — and so is a delegate's
/// invocation, a lambda being guarded through the function that creates it. A reference the model
/// cannot resolve is followed, unless its receiver is of a type outside the module. A transition
/// reached from a type's initializer, a finalizer or an implicit constructor cannot be guarded before
/// it runs; that is reported as an issue.
/// </para>
/// <para>
/// Each transition's shapes are a constant of the module's guard class (<see cref="ClassName"/>,
/// in <see cref="MasksFileName"/>); a guard names the constants of the transitions it covers. The
/// constants are a pure function of the C# (<see cref="RenderMasks"/>): a build that later routes a
/// transition through its bridge rewrites only that file, setting the constant to
/// <see cref="SwiftTransitionFacts.None"/>, and leaves every member body as generated.
/// </para>
/// </remarks>
internal sealed class SwiftTransitionGuards
{
    /// <summary>The warning for a member that refuses on runtimes that mishandle a transition it reaches.</summary>
    public const string MemberCode = "SWIFTBIND130";

    /// <summary>The summary of a module's guarded members.</summary>
    public const string SummaryCode = "SWIFTBIND131";

    /// <summary>The generated class that holds the module's transition constants.</summary>
    public const string ClassName = "__SwiftBindingsTransitionGuards";

    private const string GuardType = "global::Swift.Runtime.SwiftTransitionGuard";
    private const string FactsType = "global::Swift.Runtime.SwiftTransitionFacts";

    /// <summary>
    /// The file of the module's transition constants. It sits under the per-type file glob, so every
    /// project shape that compiles the module's files compiles it, and a regeneration that keeps no
    /// transition removes it with the other stale type files.
    /// </summary>
    public static string MasksFileName(string @namespace) => $"{@namespace}.Types.Module.SwiftTransitionGuards.cs";

    /// <summary>The sidecar that lists the module's guarded members and the transitions they reach.</summary>
    public static string SidecarFileName(string @namespace) => $"{@namespace}.swift-transition-guards.json";

    private static readonly Regex MaskReference = new(Regex.Escape(ClassName) + @"\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    private static readonly Regex MaskDeclaration = new(
        @"internal const global::Swift\.Runtime\.SwiftTransitionFacts ([A-Za-z_][A-Za-z0-9_]*) = \(global::Swift\.Runtime\.SwiftTransitionFacts\)0x([0-9A-Fa-f]+)UL;(?: // (.*))?",
        RegexOptions.Compiled);

    /// <summary>A guard call: its mask expression and the member it names.</summary>
    internal static readonly Regex GuardCall = new(
        @"global::Swift\.Runtime\.SwiftTransitionGuard\.(?:ThrowIfUnsupported|EnsureSupported)\(([^""]*?), ""((?:[^""\\]|\\.)*)""\)",
        RegexOptions.Compiled);

    private readonly Dictionary<string, SwiftTransitionGuardSite> _sites;
    private readonly string _namespace;
    private readonly Dictionary<string, int> _written = new(StringComparer.Ordinal);

    private SwiftTransitionGuards(string @namespace, ImmutableArray<SwiftTransitionHazard> hazards,
        Dictionary<string, SwiftTransitionGuardSite> sites, ImmutableArray<string> issues, int conservativeEdges)
    {
        _namespace = @namespace;
        Hazards = hazards;
        _sites = sites;
        Issues = issues;
        ConservativeEdges = conservativeEdges;
    }

    /// <summary>Every guarded transition: those some guard names.</summary>
    public ImmutableArray<SwiftTransitionHazard> Hazards { get; }

    /// <summary>Every guarded function, by key.</summary>
    public IReadOnlyCollection<SwiftTransitionGuardSite> Sites => _sites.Values;

    /// <summary>What could not be guarded before it runs, or could not be guarded at all.</summary>
    public ImmutableArray<string> Issues { get; }

    /// <summary>Call edges followed because the semantic model could not resolve them.</summary>
    public int ConservativeEdges { get; }

    public bool GuardsAny => _sites.Count > 0;

    // ---- shapes ----

    /// <summary>
    /// The shapes of a direct Swift-convention import's signature: an untyped <c>SwiftSelf</c>
    /// loads the self register, and a typed <c>SwiftSelf&lt;T&gt;</c> counts as loading it too. A
    /// small typed self is passed in ordinary registers instead, but which placement applies depends
    /// on <c>T</c>'s layout, which this pass cannot see, so it takes the stricter reading and the
    /// guard may refuse a call it could have allowed. A <c>SwiftSelf</c> or <c>SwiftError*</c> before an
    /// ordinary parameter is a special argument before the arguments; and any Swift register type,
    /// or a parameter or result that is not a primitive scalar or pointer, needs Swift lowering.
    /// </summary>
    internal static SwiftTransitionFacts FactsOf(SeparatedSyntaxList<ParameterSyntax> parameters, TypeSyntax returnType, HashSet<string> enums, bool lowered) =>
        FactsOf(parameters.Select(p => (p.Type, p.Modifiers)), returnType, enums, lowered, functionPointer: false);

    /// <summary>The shapes of a call through an <c>unmanaged[Swift]</c> function pointer of <paramref name="pointer"/>'s type.</summary>
    internal static SwiftTransitionFacts FactsOf(FunctionPointerTypeSyntax pointer, HashSet<string> enums)
    {
        var parameters = pointer.ParameterList.Parameters;
        if (parameters.Count == 0)
            return SwiftTransitionFacts.None;
        return FactsOf(parameters.Take(parameters.Count - 1).Select(p => ((TypeSyntax?)p.Type, p.Modifiers)), parameters[^1].Type, enums, lowered: false, functionPointer: true);
    }

    private static SwiftTransitionFacts FactsOf(
        IEnumerable<(TypeSyntax? Type, SyntaxTokenList Modifiers)> parameters, TypeSyntax returnType, HashSet<string> enums, bool lowered, bool functionPointer)
    {
        bool self = false, specialBefore = false, needsLowering = false, special = false;
        foreach (var (type, modifiers) in parameters)
        {
            if (type is null)
            {
                needsLowering = true;
                continue;
            }
            switch (RegisterRole(type, modifiers))
            {
                case "SwiftSelf":
                    self = special = needsLowering = true;
                    break;
                case "SwiftError":
                    special = needsLowering = true;
                    break;
                case "SwiftIndirectResult":
                    needsLowering = true;
                    break;
                default:
                    specialBefore |= special;
                    needsLowering |= !SwiftTransitionSyntax.IsPrimitive(type, modifiers, enums, lowered);
                    break;
            }
        }
        if (RegisterRole(returnType, default) is not null || !SwiftTransitionSyntax.IsPrimitive(returnType, default, enums, lowered))
            needsLowering = true;

        var facts = SwiftTransitionFacts.None;
        if (self)
            facts |= functionPointer ? SwiftTransitionFacts.FunctionPointerSelfRegister : SwiftTransitionFacts.SelfRegister;
        if (specialBefore)
            facts |= functionPointer ? SwiftTransitionFacts.FunctionPointerSpecialBeforeArguments : SwiftTransitionFacts.SpecialBeforeArguments;
        if (needsLowering)
            facts |= functionPointer ? SwiftTransitionFacts.FunctionPointerLoweredSignature : SwiftTransitionFacts.LoweredSignature;
        return facts;
    }

    /// <summary>
    /// Which Swift register type <paramref name="type"/> is, if any. The error register is passed as
    /// <c>SwiftError*</c>, or as <c>ref</c>/<c>out</c> <c>SwiftError</c>, which a <c>LibraryImport</c>
    /// stub pins to that pointer.
    /// </summary>
    private static string? RegisterRole(TypeSyntax type, SyntaxTokenList modifiers)
    {
        if (type is PointerTypeSyntax pointer)
            return SwiftTransitionSyntax.SimpleName(pointer.ElementType) == "SwiftError" ? "SwiftError" : null;
        var simple = SwiftTransitionSyntax.SimpleName(type);
        if (simple == "SwiftError" && modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.InKeyword)))
            return "SwiftError";
        return simple is "SwiftSelf" or "SwiftIndirectResult" ? simple : null;
    }

    /// <summary>The constant a transition's shapes are recorded under: readable, and unique by its hash.</summary>
    internal static string MaskName(string member, string entryPoint)
    {
        var separator = member.IndexOf("::", StringComparison.Ordinal);
        var type = separator < 0 ? member : member[..separator];
        var method = separator < 0 ? "" : member[(separator + 2)..];
        type = type[(type.LastIndexOf('.') + 1)..];
        var readable = new StringBuilder();
        foreach (var c in $"{type}__{method}")
            readable.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        if (readable.Length == 0 || char.IsAsciiDigit(readable[0]))
            readable.Insert(0, '_');
        return $"{readable}_{Fnv1a($"{member}|{entryPoint}"):x8}";
    }

    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }

    // ---- discovery ----

    /// <summary>The names of the enums <paramref name="roots"/> declare, which are primitive carriers.</summary>
    internal static HashSet<string> EnumsOf(IEnumerable<SyntaxNode> roots) =>
        roots.SelectMany(r => r.DescendantNodes().OfType<EnumDeclarationSyntax>()).Select(e => e.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every direct Swift-convention transition of <paramref name="root"/> with a shape, and the node
    /// that holds it: the import's declaration, or the call.
    /// </summary>
    internal static List<(SwiftTransitionHazard Hazard, SyntaxNode Node)> Discover(SyntaxNode root, HashSet<string> enums)
    {
        var found = new List<(SwiftTransitionHazard, SyntaxNode)>();
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                {
                    var attributes = method.AttributeLists.SelectMany(l => l.Attributes).ToList();
                    var import = attributes.FirstOrDefault(a => SwiftTransitionSyntax.IsAttribute(a, "LibraryImport") || SwiftTransitionSyntax.IsAttribute(a, "DllImport"));
                    if (import is null && attributes.FirstOrDefault(a => SwiftTransitionSyntax.IsAttribute(a, "UnmanagedCallersOnly")) is { } uco
                        && SwiftTransitionSyntax.NamesCallConvSwift(uco))
                    {
                        // A Swift-convention callback: Swift calls it with the closure context in the self
                        // register, so every member that hands Swift its address reaches that shape.
                        var callback = SwiftTransitionSyntax.MemberOf(method);
                        found.Add((new SwiftTransitionHazard(MaskName(callback, ""), SwiftTransitionKind.ReverseCallback, callback, "", SwiftTransitionFacts.ReverseCallback), method));
                        break;
                    }
                    var callConv = attributes.FirstOrDefault(a => SwiftTransitionSyntax.IsAttribute(a, "UnmanagedCallConv"));
                    if (import is null || callConv is null || !SwiftTransitionSyntax.NamesCallConvSwift(callConv))
                        break;
                    var facts = FactsOf(method.ParameterList.Parameters, method.ReturnType, enums, lowered: SwiftTransitionSyntax.IsAttribute(import, "LibraryImport"));
                    if (facts == SwiftTransitionFacts.None)
                        break;
                    var entryArgument = import.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "EntryPoint");
                    var entryPoint = entryArgument?.Expression is LiteralExpressionSyntax literal ? literal.Token.ValueText : method.Identifier.ValueText;
                    var member = SwiftTransitionSyntax.MemberOf(method);
                    found.Add((new SwiftTransitionHazard(MaskName(member, entryPoint), SwiftTransitionKind.ForwardPInvoke, member, entryPoint, facts), method));
                    break;
                }
                case InvocationExpressionSyntax invocation when SwiftTransitionSyntax.SwiftFunctionPointerCalled(invocation) is { } pointer:
                {
                    var facts = FactsOf(pointer, enums);
                    if (facts == SwiftTransitionFacts.None)
                        break;
                    var member = SwiftTransitionSyntax.ContainingMember(invocation);
                    var spelling = SwiftTransitionSyntax.NormalizeFunctionPointerType(pointer.ToString());
                    var ordinal = ordinals[member + "|" + spelling] = ordinals.GetValueOrDefault(member + "|" + spelling) + 1;
                    var entryPoint = $"{spelling}#{ordinal}";
                    found.Add((new SwiftTransitionHazard(MaskName(member, entryPoint), SwiftTransitionKind.ForwardCalli, member, entryPoint, facts), invocation));
                    break;
                }
            }
        }
        return found;
    }

    // ---- planning ----

    /// <summary>A module that keeps no direct transition with a shape.</summary>
    public static SwiftTransitionGuards None(string @namespace) => new(@namespace, [], new(StringComparer.Ordinal), [], 0);

    /// <summary>
    /// Plans the guards of a module's final C#, <paramref name="sources"/> (after bridge routing), each
    /// a complete compilation unit.
    /// </summary>
    public static SwiftTransitionGuards Plan(string @namespace, IReadOnlyList<string> sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s, parseOptions)).ToList();
        var roots = trees.Select(t => t.GetRoot()).ToList();
        var enums = EnumsOf(roots);
        var discovered = roots.SelectMany(r => Discover(r, enums)).ToList();
        if (discovered.Count == 0)
            return None(@namespace);

        var compilation = CSharpCompilation.Create(
            "SwiftTransitionGuards", trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var graph = new CallGraph(compilation, trees);
        var issues = new List<string>();

        // The functions whose bodies hold a transition.
        var reached = new Dictionary<SyntaxNode, SortedSet<string>>();
        var direct = new HashSet<SyntaxNode>();
        var guarded = new List<SwiftTransitionHazard>();
        foreach (var (hazard, node) in discovered)
        {
            var holders = new List<SyntaxNode>();
            if (hazard.Kind == SwiftTransitionKind.ForwardCalli)
            {
                Attribute(node, holders, issues, $"the call through {hazard.EntryPoint} in {hazard.Member}");
            }
            else if (hazard.Kind == SwiftTransitionKind.ReverseCallback)
            {
                foreach (var read in graph.CallbackAddressReads((MethodDeclarationSyntax)node))
                    Attribute(read, holders, issues, $"the address of {hazard.Member}");
            }
            else
            {
                var import = graph.SymbolOf((MethodDeclarationSyntax)node);
                foreach (var reference in graph.ReferencesByName(((MethodDeclarationSyntax)node).Identifier.ValueText))
                {
                    if (graph.Resolves(reference, import, out _) != Edge.Call)
                        continue;
                    Attribute(reference, holders, issues, $"a call of {hazard.Member} ({hazard.EntryPoint})");
                }
            }
            if (holders.Count == 0)
                continue;
            guarded.Add(hazard);
            foreach (var holder in holders)
            {
                direct.Add(holder);
                if (!reached.TryGetValue(holder, out var set))
                    reached[holder] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(hazard.Name);
            }
        }

        // Every caller that reaches them, until nothing more is reached.
        var queue = new Queue<SyntaxNode>(reached.Keys);
        while (queue.Count > 0)
        {
            var callee = queue.Dequeue();
            var masks = reached[callee];
            foreach (var caller in graph.CallersOf(callee, issues))
            {
                if (!reached.TryGetValue(caller, out var set))
                    reached[caller] = set = new SortedSet<string>(StringComparer.Ordinal);
                var before = set.Count;
                set.UnionWith(masks);
                if (set.Count != before)
                    queue.Enqueue(caller);
            }
        }

        var sites = new Dictionary<string, SwiftTransitionGuardSite>(StringComparer.Ordinal);
        foreach (var (unit, masks) in reached)
        {
            var symbol = graph.FunctionSymbol(unit);
            var visible = symbol is not null && CallGraph.IsCallableFromOutside(symbol);
            if (!direct.Contains(unit) && !visible)
                continue;
            if (unit is DestructorDeclarationSyntax || symbol?.MethodKind == MethodKind.StaticConstructor)
            {
                issues.Add($"{Describe(unit).Display} reaches {string.Join(", ", masks)}, and a {(unit is DestructorDeclarationSyntax ? "finalizer" : "type initializer")} cannot refuse before it runs");
                continue;
            }
            var site = Describe(unit) with { Visible = visible, Direct = direct.Contains(unit), Transitions = masks.ToImmutableArray() };
            if (!sites.TryAdd(site.Key, site))
                issues.Add($"two guarded functions share the identity {site.Key}");
        }

        return new SwiftTransitionGuards(@namespace,
            guarded.DistinctBy(h => h.Name).OrderBy(h => h.Name, StringComparer.Ordinal).ToImmutableArray(),
            sites, issues.Distinct(StringComparer.Ordinal).ToImmutableArray(), graph.ConservativeEdges);
    }

    /// <summary>Records the function that holds <paramref name="node"/>, or why none can be guarded.</summary>
    private static void Attribute(SyntaxNode node, List<SyntaxNode> holders, List<string> issues, string what)
    {
        switch (CallGraph.FunctionOf(node, out var place))
        {
            case { } function:
                holders.Add(function);
                break;
            case null when place is not null:
                issues.Add($"{what} sits in {place}, which runs before any guard");
                break;
        }
    }

    // ---- rewriting ----

    /// <summary>
    /// Inserts each guard into <paramref name="source"/> — the whole module or one of its files —
    /// recording each insertion into <paramref name="journal"/>. With <paramref name="written"/>, the
    /// guards are counted toward <see cref="VerifyWritten"/>.
    /// </summary>
    public string Rewrite(string source, TextEditJournal? journal = null, bool written = false)
    {
        if (_sites.Count == 0)
            return source;
        var root = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var edits = new List<(int Start, int Length, string Text)>();
        foreach (var unit in root.DescendantNodes().Where(IsFunction))
        {
            var key = KeyOf(unit);
            if (!_sites.TryGetValue(key, out var site))
                continue;
            if (written)
                _written[key] = _written.GetValueOrDefault(key) + 1;
            edits.AddRange(Insert(unit, source, GuardExpression(site), site.Display, newline));
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

    /// <summary>Throws unless every guard was written exactly once across the files counted by <see cref="Rewrite"/>.</summary>
    public void VerifyWritten()
    {
        var missing = _sites.Keys.Where(k => !_written.ContainsKey(k)).ToList();
        var repeated = _written.Where(kv => kv.Value != 1).Select(kv => kv.Key).ToList();
        if (missing.Count > 0 || repeated.Count > 0)
            throw new InvalidOperationException(
                $"The transition guards of {_namespace} were not each written once: " +
                $"{missing.Count} never ({string.Join(", ", missing.Take(5))}), {repeated.Count} more than once ({string.Join(", ", repeated.Take(5))}).");
    }

    private string GuardExpression(SwiftTransitionGuardSite site) =>
        string.Join(" | ", site.Transitions.Select(t => $"global::{_namespace}.{ClassName}.{t}"));

    private static IEnumerable<(int Start, int Length, string Text)> Insert(SyntaxNode unit, string source, string masks, string display, string newline)
    {
        var literal = SymbolDisplay.FormatLiteral(display, quote: true);
        var statement = $"{GuardType}.ThrowIfUnsupported({masks}, {literal});";
        var indent = IndentOf(source, unit.SpanStart);
        switch (unit)
        {
            case ConstructorDeclarationSyntax { Initializer.ArgumentList.Arguments: { Count: > 0 } arguments } ctor
                when arguments[0].RefKindKeyword.IsKind(SyntaxKind.None) && arguments[0].Expression is not AnonymousFunctionExpressionSyntax:
            {
                // The initializer's arguments, and the constructor it calls, run before the body.
                var first = arguments[0].Expression;
                yield return (first.SpanStart, first.Span.Length,
                    $"{GuardType}.EnsureSupported({masks}, {literal}) ? ({first}) : default!");
                yield break;
            }
            case BaseMethodDeclarationSyntax { Body: { } body }:
                yield return (body.OpenBraceToken.Span.End, 0, $"{newline}{indent}    {statement}");
                yield break;
            case BaseMethodDeclarationSyntax { ExpressionBody: { } arrow } method:
                yield return Replace(arrow, method.SemicolonToken, statement, Returns(method), indent, newline);
                yield break;
            case AccessorDeclarationSyntax { Body: { } body }:
                yield return (body.OpenBraceToken.Span.End, 0, $" {statement}");
                yield break;
            case AccessorDeclarationSyntax { ExpressionBody: { } arrow } accessor:
                yield return Replace(arrow, accessor.SemicolonToken, statement, accessor.Keyword.IsKind(SyntaxKind.GetKeyword), indent, newline);
                yield break;
            case BasePropertyDeclarationSyntax property when ArrowOf(property) is { } arrow:
            {
                var semicolon = property is PropertyDeclarationSyntax p ? p.SemicolonToken : ((IndexerDeclarationSyntax)property).SemicolonToken;
                var body = Body(arrow.Expression, statement, returns: true);
                yield return (arrow.SpanStart, semicolon.Span.End - arrow.SpanStart, $"{{ get {{ {body} }} }}");
                yield break;
            }
        }
    }

    private static (int, int, string) Replace(ArrowExpressionClauseSyntax arrow, SyntaxToken semicolon, string statement, bool returns, string indent, string newline) =>
        (arrow.SpanStart, semicolon.Span.End - arrow.SpanStart, $"{{ {Body(arrow.Expression, statement, returns)} }}");

    private static string Body(ExpressionSyntax expression, string statement, bool returns) => expression switch
    {
        ThrowExpressionSyntax thrown => $"{statement} throw {thrown.Expression};",
        _ when returns => $"{statement} return {expression};",
        _ => $"{statement} {expression};",
    };

    /// <summary>Whether an expression-bodied method's expression is its result.</summary>
    private static bool Returns(BaseMethodDeclarationSyntax method)
    {
        if (method is ConstructorDeclarationSyntax or DestructorDeclarationSyntax)
            return false;
        var type = method switch
        {
            MethodDeclarationSyntax m => m.ReturnType,
            OperatorDeclarationSyntax o => o.ReturnType,
            ConversionOperatorDeclarationSyntax c => c.Type,
            _ => null,
        };
        if (type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
            return false;
        // An async method returning a non-generic task has no result.
        if (method.Modifiers.Any(SyntaxKind.AsyncKeyword) && type is not null && SwiftTransitionSyntax.SimpleName(type) is "Task" or "ValueTask" && type is not GenericNameSyntax
            && !(type is QualifiedNameSyntax { Right: GenericNameSyntax }) && !(type is AliasQualifiedNameSyntax { Name: GenericNameSyntax }))
            return false;
        return true;
    }

    private static ArrowExpressionClauseSyntax? ArrowOf(BasePropertyDeclarationSyntax property) => property switch
    {
        PropertyDeclarationSyntax p => p.ExpressionBody,
        IndexerDeclarationSyntax i => i.ExpressionBody,
        _ => null,
    };

    private static string IndentOf(string source, int position)
    {
        var lineStart = source.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        var end = lineStart;
        while (end < position && source[end] is ' ' or '\t')
            end++;
        return source[lineStart..end];
    }

    // ---- identity ----

    /// <summary>
    /// A function's identity from its syntax alone: its type path, its IL method name and its
    /// parameters, so the whole module and the file that declares the function agree on it.
    /// </summary>
    internal static string KeyOf(SyntaxNode unit)
    {
        var type = SwiftTransitionSyntax.TypePathOf(unit);
        return unit switch
        {
            AccessorDeclarationSyntax accessor when accessor.Parent?.Parent is BasePropertyDeclarationSyntax owner =>
                $"{type}::{SwiftTransitionSyntax.AccessorMethodName(accessor, owner)}{ExplicitOf(owner)}({ParametersOf(owner)})",
            BasePropertyDeclarationSyntax property =>
                $"{type}::get_{(property is IndexerDeclarationSyntax ? "Item" : ((PropertyDeclarationSyntax)property).Identifier.ValueText)}{ExplicitOf(property)}({ParametersOf(property)})",
            MethodDeclarationSyntax method =>
                $"{type}::{ExplicitOf(method)}{method.Identifier.ValueText}`{method.TypeParameterList?.Parameters.Count ?? 0}({Normalize(method.ParameterList)})",
            ConversionOperatorDeclarationSyntax conversion =>
                $"{type}::{SwiftTransitionSyntax.MethodNameOf(conversion)}({Normalize(conversion.ParameterList)})->{Normalize(conversion.Type)}",
            BaseMethodDeclarationSyntax method =>
                $"{type}::{SwiftTransitionSyntax.MethodNameOf(method)}({Normalize(method.ParameterList)})",
            _ => $"{type}::?{unit.Kind()}",
        };
    }

    private static string ExplicitOf(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax { ExplicitInterfaceSpecifier: { } spec } => Normalize(spec.Name) + ".",
        BasePropertyDeclarationSyntax { ExplicitInterfaceSpecifier: { } spec } => "@" + Normalize(spec.Name),
        _ => "",
    };

    private static string ParametersOf(BasePropertyDeclarationSyntax owner) =>
        owner is IndexerDeclarationSyntax indexer ? Normalize(indexer.ParameterList) : "";

    private static string Normalize(SyntaxNode node) => Regex.Replace(node.ToString(), @"\s+", "");

    /// <summary>How a guard names a function, and its kind, name and declaring type for the report.</summary>
    internal static SwiftTransitionGuardSite Describe(SyntaxNode unit)
    {
        var typeNames = unit.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse()
            .Select(t => t.Identifier.ValueText + ((t as TypeDeclarationSyntax)?.TypeParameterList is { } tp ? Short(tp) : ""))
            .ToList();
        var typeDisplay = string.Join(".", typeNames);
        var namespaces = unit.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()).ToList();
        var containing = string.Join(".", namespaces.Append(string.Join(".", unit.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText))));
        var (kind, name, display) = unit switch
        {
            AccessorDeclarationSyntax accessor when accessor.Parent?.Parent is BasePropertyDeclarationSyntax owner =>
                (owner is IndexerDeclarationSyntax ? "Indexer" : owner is EventDeclarationSyntax ? "Event" : "Property",
                 NameOf(owner), $"{typeDisplay}{OwnerDisplay(owner)}.{accessor.Keyword.ValueText}"),
            BasePropertyDeclarationSyntax property =>
                (property is IndexerDeclarationSyntax ? "Indexer" : "Property", NameOf(property), $"{typeDisplay}{OwnerDisplay(property)}.get"),
            MethodDeclarationSyntax method =>
                ("Method", method.Identifier.ValueText,
                 $"{typeDisplay}.{(method.ExplicitInterfaceSpecifier is { } spec ? Short(spec.Name) + "." : "")}{method.Identifier.ValueText}{(method.TypeParameterList is { } tp ? Short(tp) : "")}{Short(method.ParameterList)}"),
            ConstructorDeclarationSyntax ctor =>
                ("Constructor", ".ctor", $"{typeDisplay}{Short(ctor.ParameterList)}"),
            OperatorDeclarationSyntax op =>
                ("Operator", SwiftTransitionSyntax.MethodNameOf(op) ?? "operator", $"{typeDisplay}.operator {op.OperatorToken.ValueText}{Short(op.ParameterList)}"),
            ConversionOperatorDeclarationSyntax conversion =>
                ("Operator", SwiftTransitionSyntax.MethodNameOf(conversion) ?? "operator",
                 $"{typeDisplay}.{conversion.ImplicitOrExplicitKeyword.ValueText} operator {Short(conversion.Type)}{Short(conversion.ParameterList)}"),
            DestructorDeclarationSyntax => ("Finalizer", "Finalize", $"{typeDisplay}.~{typeNames.LastOrDefault()}()"),
            _ => ("Member", "?", typeDisplay),
        };
        return new SwiftTransitionGuardSite(KeyOf(unit), display, kind, name, containing, false, false, []);
    }

    private static string NameOf(BasePropertyDeclarationSyntax owner) => owner switch
    {
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        EventDeclarationSyntax e => e.Identifier.ValueText,
        _ => "Item",
    };

    private static string OwnerDisplay(BasePropertyDeclarationSyntax owner) => owner switch
    {
        IndexerDeclarationSyntax indexer => $"[{string.Join(", ", indexer.ParameterList.Parameters.Select(p => Short(p.Type!)))}]",
        PropertyDeclarationSyntax { ExplicitInterfaceSpecifier: { } spec } p => $".{Short(spec.Name)}.{p.Identifier.ValueText}",
        PropertyDeclarationSyntax p => "." + p.Identifier.ValueText,
        EventDeclarationSyntax e => "." + e.Identifier.ValueText,
        _ => "",
    };

    /// <summary>A spelling without <c>global::</c>, namespace qualifiers or redundant whitespace.</summary>
    private static string Short(SyntaxNode node)
    {
        var text = node switch
        {
            ParameterListSyntax list => "(" + string.Join(", ", list.Parameters.Select(p =>
                string.Join(" ", p.Modifiers.Select(m => m.ValueText).Append(p.Type?.ToString() ?? "")))) + ")",
            _ => node.ToString(),
        };
        text = Regex.Replace(text, @"\s+", " ").Replace("global::", "", StringComparison.Ordinal);
        return Regex.Replace(text, @"(?<![\w])(?:[A-Za-z_]\w*\.)+(?=[A-Za-z_])", "");
    }

    // ---- residuals ----

    /// <summary>The plans' coverage entries, by the ledger key of their transition.</summary>
    internal static Dictionary<string, SwiftPlanCoverageEntry> CoverageOf(SwiftCallPlanSet plans)
    {
        var coverage = new Dictionary<string, SwiftPlanCoverageEntry>(StringComparer.Ordinal);
        foreach (var entry in plans.Coverage.Where(e => e.Transition == SwiftTransitionKind.ForwardPInvoke))
            coverage.TryAdd(SwiftTransitionLedger.KeyOf(entry.Member, entry.EntryPoint), entry);
        return coverage;
    }

    /// <summary>Why no native bridge carries a transition the C# keeps.</summary>
    internal static string ResidualReason(string kind, string member, string entryPoint, IReadOnlyDictionary<string, SwiftPlanCoverageEntry> coverage)
    {
        if (kind == nameof(SwiftTransitionKind.ForwardCalli))
            return "no native bridge carries a call through a function pointer";
        if (kind == nameof(SwiftTransitionKind.ReverseCallback))
            return "no native bridge carries a Swift-convention callback: reverse stubs have no template yet";
        if (!coverage.TryGetValue(SwiftTransitionLedger.KeyOf(member, entryPoint), out var entry))
            return "the transition has no bridge plan";
        return entry.Status switch
        {
            SwiftPlanStatus.Refused => "its bridge plan was refused: " + string.Join("; ", entry.Refusals.Select(r => r.Architecture is { } arch ? $"{r.Reason} ({arch})" : r.Reason).Distinct(StringComparer.Ordinal)),
            SwiftPlanStatus.NotBridged => "it needs no bridge: " + (entry.Reason ?? "no reason recorded"),
            _ when !entry.Executable => "its bridge plan is not executable: " + (entry.Evidence ?? SwiftCallPlanSet.NoEvidenceReason),
            _ => "its executable bridge could not be routed: " + (entry.BridgeRefusal ?? "no reason recorded"),
        };
    }

    /// <summary>
    /// The sidecar of the guards as generation writes them over <paramref name="plans"/>: a transition
    /// with a plan no evidence has judged is pending, since a wrapper compile with evidence may route it.
    /// </summary>
    public SwiftTransitionGuardDocument Document(string module, SwiftCallPlanSet plans)
    {
        var coverage = CoverageOf(plans);
        return new SwiftTransitionGuardDocument
        {
            Schema = SwiftTransitionGuardDocument.SchemaVersion,
            Module = module,
            Namespace = _namespace,
            Transitions = Hazards.Select(h =>
            {
                var kind = h.Kind.ToString();
                var pending = h.Kind == SwiftTransitionKind.ForwardPInvoke && plans.EvidenceScope is null
                    && coverage.TryGetValue(SwiftTransitionLedger.KeyOf(h.Member, h.EntryPoint), out var entry)
                    && entry.Status == SwiftPlanStatus.Planned && !entry.Executable;
                return new SwiftTransitionGuardTransition(h.Name, kind, h.Member, h.EntryPoint, (ulong)h.Facts, h.Facts.ToString(),
                    ResidualReason(kind, h.Member, h.EntryPoint, coverage), pending);
            }).ToImmutableArray(),
            Sites = _sites.Values.OrderBy(s => s.ContainingType, StringComparer.Ordinal).ThenBy(s => s.Key, StringComparer.Ordinal).ToImmutableArray(),
        };
    }

    /// <summary>The guard class constants of the planned guards.</summary>
    public IReadOnlyList<Mask> Masks() => Hazards.Select(h => new Mask(h.Name, h.Facts, $"{h.Member} {h.EntryPoint}")).ToList();

    /// <summary>
    /// Writes the guard class and the sidecar of a module generation just wrote, or removes them when
    /// nothing is guarded, and logs the guarded members. Returns the guard class's text, null when
    /// none was written.
    /// </summary>
    public string? Write(string directory, string module, SwiftCallPlanSet plans, ILogger logger)
    {
        var masksPath = Path.Combine(directory, MasksFileName(_namespace));
        var sidecarPath = Path.Combine(directory, SidecarFileName(_namespace));
        if (!GuardsAny)
        {
            if (File.Exists(masksPath))
                File.Delete(masksPath);
            if (File.Exists(sidecarPath))
                File.Delete(sidecarPath);
            return null;
        }
        var masks = RenderMasks(_namespace, Masks());
        File.WriteAllText(masksPath, masks);
        var document = Document(module, plans);
        File.WriteAllText(sidecarPath, document.ToJson());
        document.Log(logger);
        return masks;
    }

    // ---- the constants ----

    /// <summary>One constant of the guard class: its transition's shapes and what it names.</summary>
    internal sealed record Mask(string Name, SwiftTransitionFacts Facts, string Comment);

    /// <summary>
    /// The guard class of a module whose C# is <paramref name="sources"/>, as a pure function of that C#:
    /// a constant for every transition a guard names, holding the transition's shapes while the C#
    /// declares it directly and <see cref="SwiftTransitionFacts.None"/> once <paramref name="routed"/>
    /// routes it through a bridge. Null when no guard names one. A transition the C# reaches that no
    /// guard names, and a constant naming one the C# neither declares nor routes, are issues.
    /// </summary>
    internal static IReadOnlyList<Mask> MasksOf(
        IReadOnlyCollection<string> sources, IEnumerable<(string Member, string EntryPoint)> routed, List<string> issues)
    {
        // C# that declares no Swift-convention transition and names no constant needs no guard class.
        if (!sources.Any(s => s.Contains("CallConvSwift", StringComparison.Ordinal) || s.Contains("unmanaged[Swift]", StringComparison.Ordinal)
                || s.Contains(ClassName, StringComparison.Ordinal)))
            return [];
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var roots = sources.Select(s => CSharpSyntaxTree.ParseText(s, parseOptions).GetRoot()).ToList();
        var enums = EnumsOf(roots);
        var declared = roots.SelectMany(r => Discover(r, enums)).Select(d => d.Hazard).DistinctBy(h => h.Name).ToDictionary(h => h.Name, StringComparer.Ordinal);
        var routedByName = routed.Select(r => (Name: MaskName(r.Member, r.EntryPoint), r.Member, r.EntryPoint))
            .DistinctBy(r => r.Name).ToDictionary(r => r.Name, StringComparer.Ordinal);
        var referenced = sources.SelectMany(s => MaskReference.Matches(s).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);

        // Which imports code calls: an import nothing calls needs no guard.
        var called = new HashSet<string>(StringComparer.Ordinal);
        var importNames = declared.Values.Where(h => h.Kind == SwiftTransitionKind.ForwardPInvoke)
            .Select(h => h.Member[(h.Member.LastIndexOf("::", StringComparison.Ordinal) + 2)..]).ToHashSet(StringComparer.Ordinal);
        foreach (var root in roots)
            foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
                if (importNames.Contains(name.Identifier.ValueText))
                    called.Add(name.Identifier.ValueText);

        // Which callbacks code hands on: the address taken in a function, or a field it initializes read.
        var handedOn = new HashSet<string>(StringComparer.Ordinal);
        var callbackNames = declared.Values.Where(h => h.Kind == SwiftTransitionKind.ReverseCallback)
            .Select(h => h.Member[(h.Member.LastIndexOf("::", StringComparison.Ordinal) + 2)..]).ToHashSet(StringComparer.Ordinal);
        if (callbackNames.Count > 0)
        {
            var identifiers = roots.SelectMany(r => r.DescendantNodes().OfType<IdentifierNameSyntax>()).ToList();
            foreach (var address in roots.SelectMany(r => r.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()).Where(p => p.IsKind(SyntaxKind.AddressOfExpression)))
            {
                var name = address.Operand switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                    _ => null,
                };
                if (name is null || !callbackNames.Contains(name))
                    continue;
                if (address.FirstAncestorOrSelf<VariableDeclaratorSyntax>() is { Initializer: { } initializer } declarator
                    && initializer.Span.Contains(address.Span) && declarator.Parent?.Parent is FieldDeclarationSyntax)
                {
                    if (identifiers.Any(i => i.Identifier.ValueText == declarator.Identifier.ValueText))
                        handedOn.Add(name);
                }
                else
                    handedOn.Add(name);
            }
        }

        foreach (var hazard in declared.Values)
        {
            var simpleName = hazard.Member[(hazard.Member.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
            var calledByCode = hazard.Kind switch
            {
                SwiftTransitionKind.ForwardCalli => true,
                SwiftTransitionKind.ReverseCallback => handedOn.Contains(simpleName),
                _ => called.Contains(simpleName),
            };
            if (calledByCode && !referenced.Contains(hazard.Name))
                issues.Add($"{hazard.Member} ({hazard.EntryPoint}) is a direct Swift-convention transition of shape {hazard.Facts} that no guard names");
        }

        var masks = new List<Mask>();
        foreach (var name in referenced.Order(StringComparer.Ordinal))
        {
            if (declared.TryGetValue(name, out var hazard))
                masks.Add(new Mask(name, hazard.Facts, $"{hazard.Member} {hazard.EntryPoint}"));
            else if (routedByName.TryGetValue(name, out var route))
                masks.Add(new Mask(name, SwiftTransitionFacts.None, $"{route.Member} {route.EntryPoint} (routed through its bridge)"));
            else
                issues.Add($"a guard names {name}, a transition the C# neither declares nor routes through a bridge");
        }
        return masks;
    }

    /// <summary>The guard class file holding <paramref name="masks"/>, or null when there are none.</summary>
    internal static string? RenderMasks(string @namespace, IReadOnlyCollection<Mask> masks)
    {
        if (masks.Count == 0)
            return null;
        var text = new StringBuilder();
        text.Append("// <auto-generated>\n");
        text.Append("//   The shapes of the direct Swift-convention transitions this binding keeps, one constant per\n");
        text.Append("//   transition. A member that reaches one refuses, before any native work, on a runtime that\n");
        text.Append("//   does not execute that shape correctly. A transition routed through a native bridge is None.\n");
        text.Append("// </auto-generated>\n");
        text.Append($"namespace {@namespace}\n{{\n");
        text.Append("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        text.Append($"    internal static class {ClassName}\n    {{\n");
        foreach (var mask in masks.OrderBy(m => m.Name, StringComparer.Ordinal))
            text.Append($"        internal const {FactsType} {mask.Name} = ({FactsType})0x{(ulong)mask.Facts:X}UL; // {mask.Comment.Replace('\n', ' ').Replace('\r', ' ')}\n");
        text.Append("    }\n}\n");
        return text.ToString();
    }

    /// <summary>The constants a guard class file declares.</summary>
    internal static IReadOnlyList<Mask> ParseMasks(string? text) =>
        text is null
            ? []
            : MaskDeclaration.Matches(text).Select(m => new Mask(
                m.Groups[1].Value,
                (SwiftTransitionFacts)ulong.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                m.Groups[3].Success ? m.Groups[3].Value : "")).ToList();

    /// <summary>Every constant of either set, holding the shapes of both: a state at least as restrictive as each.</summary>
    internal static IReadOnlyList<Mask> Union(IReadOnlyList<Mask> first, IReadOnlyList<Mask> second)
    {
        var merged = first.ToDictionary(m => m.Name, StringComparer.Ordinal);
        foreach (var mask in second)
            merged[mask.Name] = merged.TryGetValue(mask.Name, out var existing)
                ? existing with { Facts = existing.Facts | mask.Facts, Comment = mask.Facts != SwiftTransitionFacts.None ? mask.Comment : existing.Comment }
                : mask;
        return merged.Values.ToList();
    }

    /// <summary>A guard of the final C#: the member it names and the shapes it refuses on, from the constants.</summary>
    internal static IEnumerable<(string Member, SwiftTransitionFacts Facts, ImmutableArray<string> Transitions)> GuardsOf(
        IEnumerable<string> sources, IReadOnlyList<Mask> masks)
    {
        var byName = masks.ToDictionary(m => m.Name, m => m.Facts, StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (Match call in GuardCall.Matches(source))
            {
                var names = MaskReference.Matches(call.Groups[1].Value).Select(m => m.Groups[1].Value).ToImmutableArray();
                var facts = names.Aggregate(SwiftTransitionFacts.None, (f, n) => f | byName.GetValueOrDefault(n));
                yield return (Regex.Unescape(call.Groups[2].Value), facts, names);
            }
        }
    }

    // ---- references ----

    /// <summary>
    /// The framework the generator runs on and the runtime the binding compiles against, from the
    /// process's trusted assemblies. Apple and dependency assemblies are left out on purpose: the
    /// guards must not depend on which workloads a machine has, and a member of a type they declare
    /// is not one of the module's (<see cref="CallGraph.Resolves"/>).
    /// </summary>
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var coreLibrary = trusted.FirstOrDefault(p => Path.GetFileName(p).Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase));
        var framework = coreLibrary is null ? null : Path.GetDirectoryName(coreLibrary);
        var paths = trusted
            .Where(p => (framework is not null && string.Equals(Path.GetDirectoryName(p), framework, StringComparison.Ordinal))
                || Path.GetFileName(p).Equals("Swift.Runtime.dll", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (var path in paths)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var reader = new System.Reflection.PortableExecutable.PEReader(stream);
                if (!reader.HasMetadata)
                    continue;
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                continue;
            }
            references.Add(MetadataReference.CreateFromFile(path));
        }
        if (!references.Any(r => Path.GetFileName(r.Display) == "Swift.Runtime.dll"))
            throw new InvalidOperationException("Swift.Runtime.dll is not among the generator's trusted assemblies, so the transition guards cannot bind the module.");
        return references.ToImmutable();
    });

    /// <summary>What a reference to a function is.</summary>
    internal enum Edge
    {
        None,
        Call,
        Dispatch,
    }

    /// <summary>The module's reverse call graph, bound lazily with one semantic model per tree.</summary>
    private sealed class CallGraph
    {
        private readonly CSharpCompilation _compilation;
        private readonly Dictionary<SyntaxTree, SemanticModel> _models = new();
        private readonly Dictionary<string, List<SimpleNameSyntax>> _names = new(StringComparer.Ordinal);
        private readonly List<ConstructorInitializerSyntax> _initializers = new();
        private readonly List<ImplicitObjectCreationExpressionSyntax> _implicitCreations = new();
        private readonly List<SyntaxNode> _elementAccesses = new();
        private readonly List<SyntaxNode> _operators = new();
        private readonly List<CastExpressionSyntax> _casts = new();
        private readonly List<TypeDeclarationSyntax> _types = new();
        private readonly Dictionary<IMethodSymbol, SyntaxNode> _functions = new(SymbolEqualityComparer.Default);
        private Dictionary<INamedTypeSymbol, List<TypeDeclarationSyntax>>? _derived;

        public int ConservativeEdges { get; private set; }

        public CallGraph(CSharpCompilation compilation, IReadOnlyList<SyntaxTree> trees)
        {
            _compilation = compilation;
            foreach (var tree in trees)
            {
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    switch (node)
                    {
                        case IdentifierNameSyntax or GenericNameSyntax:
                        {
                            var name = (SimpleNameSyntax)node;
                            if (!_names.TryGetValue(name.Identifier.ValueText, out var list))
                                _names[name.Identifier.ValueText] = list = new List<SimpleNameSyntax>();
                            list.Add(name);
                            break;
                        }
                        case ConstructorInitializerSyntax initializer:
                            _initializers.Add(initializer);
                            break;
                        case ImplicitObjectCreationExpressionSyntax creation:
                            _implicitCreations.Add(creation);
                            break;
                        case ElementAccessExpressionSyntax or ElementBindingExpressionSyntax:
                            _elementAccesses.Add(node);
                            break;
                        case BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax or AssignmentExpressionSyntax:
                            _operators.Add(node);
                            break;
                        case CastExpressionSyntax cast:
                            _casts.Add(cast);
                            break;
                        case TypeDeclarationSyntax type:
                            _types.Add(type);
                            break;
                    }
                }
            }
        }

        private SemanticModel ModelOf(SyntaxNode node)
        {
            if (!_models.TryGetValue(node.SyntaxTree, out var model))
                _models[node.SyntaxTree] = model = _compilation.GetSemanticModel(node.SyntaxTree);
            return model;
        }

        public IReadOnlyList<SimpleNameSyntax> ReferencesByName(string name) =>
            _names.TryGetValue(name, out var list) ? list : [];

        public IMethodSymbol? SymbolOf(MethodDeclarationSyntax method) => Normalize(ModelOf(method).GetDeclaredSymbol(method) as IMethodSymbol);

        /// <summary>
        /// Where code reads the address of <paramref name="callback"/> to hand it on: each
        /// <c>&amp;callback</c> in a function, and, when the address initializes a field (the closure
        /// emitters' <c>s_&lt;callback&gt;</c>), each read of that field instead, since the field's
        /// initializer only stores it. A name the model cannot bind is taken as the callback.
        /// </summary>
        public List<SyntaxNode> CallbackAddressReads(MethodDeclarationSyntax callback)
        {
            var reads = new List<SyntaxNode>();
            var symbol = SymbolOf(callback);
            foreach (var reference in ReferencesByName(callback.Identifier.ValueText))
            {
                SyntaxNode operand = reference.Parent is MemberAccessExpressionSyntax access && access.Name == reference ? access : reference;
                if (operand.Parent is not PrefixUnaryExpressionSyntax address || !address.IsKind(SyntaxKind.AddressOfExpression))
                    continue;
                var bound = ModelOf(reference).GetSymbolInfo(reference);
                var target = Normalize((bound.Symbol ?? bound.CandidateSymbols.FirstOrDefault()) as IMethodSymbol);
                if (target is not null && symbol is not null && !SymbolEqualityComparer.Default.Equals(target, symbol))
                    continue;
                if (address.FirstAncestorOrSelf<VariableDeclaratorSyntax>() is { Initializer: { } initializer } declarator
                    && initializer.Span.Contains(address.Span)
                    && declarator.Parent?.Parent is FieldDeclarationSyntax
                    && ModelOf(declarator).GetDeclaredSymbol(declarator) is IFieldSymbol field)
                {
                    foreach (var use in ReferencesByName(field.Name))
                    {
                        var used = ModelOf(use).GetSymbolInfo(use).Symbol;
                        if (used is null || SymbolEqualityComparer.Default.Equals(used.OriginalDefinition, field.OriginalDefinition))
                            reads.Add(use);
                    }
                    continue;
                }
                reads.Add(address);
            }
            return reads;
        }

        /// <summary>The method a function declaration compiles to.</summary>
        public IMethodSymbol? FunctionSymbol(SyntaxNode function)
        {
            var model = ModelOf(function);
            var symbol = function switch
            {
                BasePropertyDeclarationSyntax property => (model.GetDeclaredSymbol(property) as IPropertySymbol)?.GetMethod,
                _ => model.GetDeclaredSymbol(function) as IMethodSymbol,
            };
            symbol = Normalize(symbol);
            if (symbol is not null)
                _functions.TryAdd(symbol, function);
            return symbol;
        }

        private static IMethodSymbol? Normalize(IMethodSymbol? method)
        {
            if (method is null)
                return null;
            method = method.ReducedFrom ?? method;
            method = method.OriginalDefinition;
            return method.PartialDefinitionPart ?? method;
        }

        /// <summary>Whether code outside the binding can invoke <paramref name="method"/>.</summary>
        public static bool IsCallableFromOutside(IMethodSymbol method)
        {
            if (method.MethodKind is MethodKind.StaticConstructor or MethodKind.Destructor)
                return false;
            if (!method.ExplicitInterfaceImplementations.IsEmpty || method.IsOverride)
                return true;
            var type = method.ContainingType;
            foreach (var @interface in type.AllInterfaces)
                foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>())
                    if (SymbolEqualityComparer.Default.Equals(Normalize(type.FindImplementationForInterfaceMember(member) as IMethodSymbol), method))
                        return true;
            if (!Accessible(method))
                return false;
            for (var containing = type; containing is not null; containing = containing.ContainingType)
                if (!Accessible(containing))
                    return false;
            return true;

            static bool Accessible(ISymbol symbol) =>
                symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;
        }

        /// <summary>Whether a call that binds to <paramref name="method"/> may run another implementation.</summary>
        private static bool Dispatches(IMethodSymbol method, SyntaxNode reference)
        {
            if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor || method.IsStatic && !method.IsAbstract && !method.IsVirtual)
                return false;
            var type = method.ContainingType;
            if (type.TypeKind == TypeKind.Interface)
                return true;
            if (!(method.IsAbstract || method.IsVirtual || method.IsOverride) || method.IsSealed || type.IsSealed || type.IsValueType)
                return false;
            // base.M() calls exactly the base implementation.
            return reference.Parent is not MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax };
        }

        /// <summary>How <paramref name="reference"/> refers to <paramref name="target"/>.</summary>
        public Edge Resolves(SyntaxNode reference, IMethodSymbol? target, out bool conservative)
        {
            conservative = false;
            if (target is null)
                return Edge.None;
            if (IsInNameof(reference))
                return Edge.None;
            var model = ModelOf(reference);
            var info = model.GetSymbolInfo(reference);
            if (info.Symbol is { } symbol)
                return Matches(symbol, target, reference) ? EdgeOf(target, reference) : Edge.None;
            if (!info.CandidateSymbols.IsEmpty)
            {
                // Overload resolution failed (an argument did not bind): a candidate that cannot take
                // as many arguments as the call passes is not one it may run.
                var count = ArgumentCount(reference);
                return info.CandidateSymbols.Any(c => Matches(c, target, reference) && (count is not { } n || c is not IMethodSymbol m || Takes(m, n)))
                    ? EdgeOf(target, reference)
                    : Edge.None;
            }

            switch (reference)
            {
                // An operator, conversion or indexer that binds to no method is a built-in one — a field
                // store, arithmetic, a pointer or array element — or one whose operands did not all bind.
                // A user-defined one is declared by the type of an operand (the indexed value's type or
                // a base of it), and an operand that did not bind is of a type outside the module, so
                // only an operand bound to the target's type can still pick it.
                case AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression):
                    return Edge.None;
                case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    return Edge.None;
                case AssignmentExpressionSyntax assignment:
                    return Unbound(model, target, ref conservative, assignment.Left, assignment.Right);
                case BinaryExpressionSyntax binary:
                    return Unbound(model, target, ref conservative, binary.Left, binary.Right);
                case PrefixUnaryExpressionSyntax prefix:
                    return Unbound(model, target, ref conservative, prefix.Operand);
                case PostfixUnaryExpressionSyntax postfix:
                    return Unbound(model, target, ref conservative, postfix.Operand);
                case CastExpressionSyntax cast:
                    return Unbound(model, target, ref conservative, cast.Type, cast.Expression);
                case ElementAccessExpressionSyntax element:
                    return Unbound(model, target, ref conservative, element.Expression);
                case ElementBindingExpressionSyntax binding when binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is { } conditional:
                    return Unbound(model, target, ref conservative, conditional.Expression);
                // A constructor call names its type: one outside the module (an error type here, since
                // only the module's own sources are compiled) runs none of the module's constructors.
                case ConstructorInitializerSyntax initializer:
                {
                    var containing = initializer.Parent is ConstructorDeclarationSyntax constructor
                        ? (model.GetDeclaredSymbol(constructor) as IMethodSymbol)?.ContainingType
                        : null;
                    var type = initializer.IsKind(SyntaxKind.ThisConstructorInitializer) ? containing : containing?.BaseType;
                    return Constructs(type, target, reference, ref conservative);
                }
                case BaseObjectCreationExpressionSyntax creation:
                    return Constructs(model.GetTypeInfo(creation).Type, target, reference, ref conservative);
            }

            // Unresolved: a member of a type outside the module is not one of the module's.
            if (reference.Parent is MemberAccessExpressionSyntax access && access.Name == reference)
            {
                var receiver = model.GetTypeInfo(access.Expression).Type;
                if (receiver is null or IErrorTypeSymbol)
                    return Edge.None;
            }
            // A call whose arguments did not bind may run any method of its name that can take as many.
            if (ArgumentCount(reference) is { } arguments && !Takes(target, arguments))
                return Edge.None;
            conservative = true;
            return Edge.Call;
        }

        /// <summary>How many arguments the call that <paramref name="reference"/> names passes, when it names a call.</summary>
        private static int? ArgumentCount(SyntaxNode reference)
        {
            switch (reference)
            {
                case ConstructorInitializerSyntax initializer:
                    return initializer.ArgumentList.Arguments.Count;
                case BaseObjectCreationExpressionSyntax creation:
                    return creation.ArgumentList?.Arguments.Count ?? 0;
            }
            SyntaxNode callee = reference;
            while (callee.Parent is MemberAccessExpressionSyntax member && member.Name == callee
                   || callee.Parent is MemberBindingExpressionSyntax binding && binding.Name == callee)
                callee = callee.Parent;
            if (callee.Parent is InvocationExpressionSyntax invocation && invocation.Expression == callee)
                return invocation.ArgumentList.Arguments.Count;
            // new T(...): the name is the created type's.
            if (reference.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>() is { } created && created.Type.Span.Contains(reference.Span))
                return created.ArgumentList?.Arguments.Count ?? 0;
            return null;
        }

        /// <summary>Whether <paramref name="method"/> can take <paramref name="arguments"/> arguments (an extension method's receiver aside).</summary>
        private static bool Takes(IMethodSymbol method, int arguments)
        {
            if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.ReducedExtension or MethodKind.Constructor))
                return true;
            var parameters = method.Parameters;
            var receiver = method.IsExtensionMethod && method.MethodKind != MethodKind.ReducedExtension ? 1 : 0;
            var required = parameters.Count(p => !p.IsOptional && !p.IsParams) - receiver;
            var most = parameters.Any(p => p.IsParams) ? int.MaxValue : parameters.Length - receiver;
            // Called as a static method, an extension takes its receiver as an argument.
            return arguments >= required && arguments <= most || receiver == 1 && arguments >= required + 1 && arguments <= most + (most == int.MaxValue ? 0 : 1);
        }

        /// <summary>
        /// An unresolved constructor call of <paramref name="type"/>: it may run <paramref name="target"/>
        /// only when that is a constructor of the type that can take the call's arguments.
        /// </summary>
        private static Edge Constructs(ITypeSymbol? type, IMethodSymbol target, SyntaxNode reference, ref bool conservative)
        {
            if (type is IErrorTypeSymbol)
                return Edge.None;
            if (type is not null && !SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, target.ContainingType.OriginalDefinition))
                return Edge.None;
            if (ArgumentCount(reference) is { } arguments && !Takes(target, arguments))
                return Edge.None;
            conservative = true;
            return Edge.Call;
        }

        private static Edge Unbound(SemanticModel model, IMethodSymbol target, ref bool conservative, params SyntaxNode[] operands)
        {
            foreach (var operand in operands)
            {
                for (var type = model.GetTypeInfo(operand).Type; type is not null and not IErrorTypeSymbol; type = type.BaseType)
                {
                    var declaring = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                        ? nullable.TypeArguments[0]
                        : type;
                    if (SymbolEqualityComparer.Default.Equals(declaring.OriginalDefinition, target.ContainingType.OriginalDefinition))
                    {
                        conservative = true;
                        return Edge.Call;
                    }
                }
            }
            return Edge.None;
        }

        /// <summary>
        /// A reference that calls <paramref name="target"/> runs it here; one that only names it — a
        /// method group made into a delegate, or a function pointer taken with <c>&amp;</c> — runs it
        /// wherever that delegate or pointer is invoked, which is a boundary like a virtual dispatch.
        /// </summary>
        private static Edge EdgeOf(IMethodSymbol target, SyntaxNode reference)
        {
            if (target.MethodKind is MethodKind.Ordinary or MethodKind.ReducedExtension && !Invoked(reference))
                return Edge.Dispatch;
            return Dispatches(target, reference) ? Edge.Dispatch : Edge.Call;

            static bool Invoked(SyntaxNode reference)
            {
                SyntaxNode callee = reference;
                while (callee.Parent is MemberAccessExpressionSyntax member && member.Name == callee
                       || callee.Parent is MemberBindingExpressionSyntax binding && binding.Name == callee)
                    callee = callee.Parent;
                // a?.M(): the binding .M is the invocation's expression.
                return callee.Parent is InvocationExpressionSyntax invocation && invocation.Expression == callee;
            }
        }

        private static bool IsInNameof(SyntaxNode node) =>
            node.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });

        private static bool Matches(ISymbol symbol, IMethodSymbol target, SyntaxNode reference) => symbol switch
        {
            IMethodSymbol method => SymbolEqualityComparer.Default.Equals(Normalize(method), target),
            IPropertySymbol property when target.AssociatedSymbol is IPropertySymbol owner
                && SymbolEqualityComparer.Default.Equals(property.OriginalDefinition, owner.OriginalDefinition) =>
                Uses(reference, target.MethodKind == MethodKind.PropertySet),
            IEventSymbol @event when target.AssociatedSymbol is IEventSymbol owner =>
                SymbolEqualityComparer.Default.Equals(@event.OriginalDefinition, owner.OriginalDefinition),
            _ => false,
        };

        /// <summary>Whether a property reference reads (<paramref name="set"/> false) or writes it.</summary>
        private static bool Uses(SyntaxNode reference, bool set)
        {
            SyntaxNode access = reference;
            while (access.Parent is MemberAccessExpressionSyntax member && member.Name == access)
                access = member;
            while (access.Parent is ConditionalAccessExpressionSyntax conditional && conditional.WhenNotNull == access)
                access = conditional;
            var writes = false;
            var reads = true;
            switch (access.Parent)
            {
                case AssignmentExpressionSyntax assignment when assignment.Left == access:
                    writes = true;
                    reads = !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression);
                    break;
                case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.PreIncrementExpression) || unary.IsKind(SyntaxKind.PreDecrementExpression):
                case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    writes = true;
                    break;
                case ArgumentSyntax argument when !argument.RefKindKeyword.IsKind(SyntaxKind.None):
                    writes = true;
                    break;
            }
            return set ? writes : reads;
        }

        /// <summary>The functions whose calls reach <paramref name="callee"/> without a dispatch.</summary>
        public IEnumerable<SyntaxNode> CallersOf(SyntaxNode callee, List<string> issues)
        {
            var target = FunctionSymbol(callee);
            if (target is null)
            {
                issues.Add($"{Describe(callee).Display} could not be bound, so its callers are not known");
                yield break;
            }

            IEnumerable<SyntaxNode> references = target.MethodKind switch
            {
                MethodKind.Constructor => ConstructorReferences(target),
                MethodKind.PropertyGet or MethodKind.PropertySet when target.AssociatedSymbol is IPropertySymbol { IsIndexer: true } => _elementAccesses,
                MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove => ReferencesByName(target.AssociatedSymbol!.Name),
                MethodKind.UserDefinedOperator => _operators,
                MethodKind.Conversion => _casts,
                MethodKind.Ordinary or MethodKind.ReducedExtension or MethodKind.DelegateInvoke => ReferencesByName(target.Name),
                _ => [],
            };

            foreach (var reference in references)
            {
                if (Resolves(reference, target, out var conservative) != Edge.Call)
                    continue;
                if (conservative)
                    ConservativeEdges++;
                if (FunctionOf(reference, out var place) is { } caller)
                    yield return caller;
                else if (place is not null)
                    issues.Add($"{Describe(callee).Display} is called from {place}, which runs before any guard");
            }

            // A constructor that calls no other runs its base type's parameterless one implicitly.
            if (target.MethodKind == MethodKind.Constructor && !target.IsStatic && target.Parameters.All(p => p.IsOptional || p.IsParams))
            {
                foreach (var derived in DerivedFrom(target.ContainingType))
                {
                    var constructors = derived.Members.OfType<ConstructorDeclarationSyntax>().Where(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword)).ToList();
                    if (constructors.Count == 0)
                        issues.Add($"{derived.Identifier.ValueText}'s implicit constructor runs {Describe(callee).Display}, and an implicit constructor holds no guard");
                    foreach (var constructor in constructors.Where(c => c.Initializer is null))
                        yield return constructor;
                }
            }
        }

        private IEnumerable<SyntaxNode> ConstructorReferences(IMethodSymbol constructor)
        {
            foreach (var name in ReferencesByName(constructor.ContainingType.Name))
            {
                if (name.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>() is { } creation && creation.Type.Span.Contains(name.Span))
                    yield return creation;
            }
            foreach (var initializer in _initializers)
                yield return initializer;
            foreach (var creation in _implicitCreations)
                yield return creation;
        }

        private IEnumerable<TypeDeclarationSyntax> DerivedFrom(INamedTypeSymbol type)
        {
            if (_derived is null)
            {
                _derived = new Dictionary<INamedTypeSymbol, List<TypeDeclarationSyntax>>(SymbolEqualityComparer.Default);
                foreach (var declaration in _types)
                {
                    if (declaration is not ClassDeclarationSyntax || ModelOf(declaration).GetDeclaredSymbol(declaration) is not INamedTypeSymbol { BaseType: { } baseType })
                        continue;
                    var key = baseType.OriginalDefinition;
                    if (!_derived.TryGetValue(key, out var list))
                        _derived[key] = list = new List<TypeDeclarationSyntax>();
                    list.Add(declaration);
                }
            }
            return _derived.TryGetValue(type.OriginalDefinition, out var derived) ? derived : [];
        }

        /// <summary>
        /// The function whose body holds <paramref name="node"/> — a lambda or local function belongs
        /// to the function that declares it — or null with <paramref name="place"/> naming where it
        /// runs when that is not a function body (a field, property or type initializer), or null for
        /// a reference that runs nothing (an attribute or a parameter's default value).
        /// </summary>
        public static SyntaxNode? FunctionOf(SyntaxNode node, out string? place)
        {
            place = null;
            for (var current = node.Parent; current is not null; current = current.Parent)
            {
                switch (current)
                {
                    case AttributeSyntax or ParameterSyntax { Parent: ParameterListSyntax { Parent: BaseMethodDeclarationSyntax or IndexerDeclarationSyntax or BracketedParameterListSyntax } }:
                        return null;
                    case AccessorDeclarationSyntax accessor:
                        return accessor;
                    case ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax or IndexerDeclarationSyntax } arrow:
                        return arrow.Parent;
                    case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property }:
                        place = $"the initializer of {property.Identifier.ValueText}";
                        return null;
                    case VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax field } declaration:
                        place = $"the initializer of {string.Join(", ", declaration.Variables.Select(v => v.Identifier.ValueText))}";
                        return null;
                    case BaseMethodDeclarationSyntax method:
                        return method;
                    case BaseTypeDeclarationSyntax type:
                        place = $"the declaration of {type.Identifier.ValueText}";
                        return null;
                }
            }
            return null;
        }
    }

    /// <summary>Whether <paramref name="node"/> is a function a guard opens: a method, constructor, operator, accessor, or expression-bodied property or indexer.</summary>
    internal static bool IsFunction(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.Body is not null || method.ExpressionBody is not null,
        BaseMethodDeclarationSyntax method => method.Body is not null || method.ExpressionBody is not null,
        AccessorDeclarationSyntax accessor => accessor.Body is not null || accessor.ExpressionBody is not null,
        PropertyDeclarationSyntax property => property.ExpressionBody is not null,
        IndexerDeclarationSyntax indexer => indexer.ExpressionBody is not null,
        _ => false,
    };
}
