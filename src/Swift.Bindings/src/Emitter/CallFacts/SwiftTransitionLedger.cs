// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
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

/// <summary>One emitted forward Swift-convention P/Invoke and the facts its producer recorded.</summary>
/// <param name="Member">
/// The declaration as the IL names it: <c>Namespace.Outer+Inner::Method</c>, a generic type with its
/// arity (<c>Box`1</c>). With <paramref name="EntryPoint"/> this is the key an IL inventory joins on.
/// </param>
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
    /// <summary>The join key with an IL inventory site: the member and the entry point.</summary>
    public string Key => SwiftTransitionLedger.KeyOf(Member, EntryPoint);
}

/// <summary>
/// The transition coverage ledger of one module: one entry per forward <c>CallConvSwift</c> P/Invoke in
/// the module's emitted C#, with the facts its producer recorded, or why there are none.
/// </summary>
/// <remarks>
/// <para>
/// The emitted set is read from the rendered C# itself, not from what producers recorded, so a
/// producer that renders a Swift-convention declaration without recording it cannot drop out of the
/// ledger: its declaration is listed <see cref="SwiftTransitionLedgerStatus.Unplanned"/> with the reason.
/// A recorded declaration that did not reach the output (a member rolled back after it was
/// rendered) is not listed.
/// </para>
/// <para>
/// Entries carry the IL's own naming (<see cref="SwiftTransitionLedgerEntry.Member"/>), so a cross-check
/// against the transition gate's IL inventory joins on <c>(member, entryPoint)</c> and can demand a
/// bijection. Writing the ledger changes nothing that is emitted.
/// </para>
/// </remarks>
public sealed class SwiftTransitionLedger
{
    /// <summary>The ledger schema. Bump it on any change to the ledger's shape.</summary>
    public const int SchemaVersion = 1;

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

    /// <summary>The join key of a member and an entry point.</summary>
    public static string KeyOf(string member, string entryPoint) => string.Concat(member, " ", entryPoint);

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
        IEnumerable<SyntheticSwiftFunction>? registered = null)
    {
        var issues = new List<string>(additionalIssues ?? Array.Empty<string>());
        var declarations = FindSwiftDeclarations(csharpSource, issues);

        var byKey = records
            .GroupBy(r => (r.MethodName, r.EntryPoint, r.Library))
            .ToDictionary(g => g.Key, g => g.ToList());

        var entries = ImmutableArray.CreateBuilder<SwiftTransitionLedgerEntry>(declarations.Count);
        var cited = new SortedDictionary<string, SwiftCallFacts>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            if (!byKey.TryGetValue((declaration.Method, declaration.EntryPoint, declaration.Library), out var matches))
            {
                entries.Add(new SwiftTransitionLedgerEntry(
                    declaration.Member, declaration.EntryPoint, declaration.Library,
                    SwiftTransitionLedgerStatus.Unplanned, null, null, null,
                    ImmutableArray.Create("no producer recorded this declaration")));
                continue;
            }

            var withFacts = matches.Where(m => m.Facts is not null).ToList();
            if (withFacts.Count == 0)
            {
                entries.Add(new SwiftTransitionLedgerEntry(
                    declaration.Member, declaration.EntryPoint, declaration.Library,
                    SwiftTransitionLedgerStatus.Unplanned, null, null, null,
                    matches.Select(m => m.Reason ?? "the producer recorded no facts").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray()));
                continue;
            }

            var distinctFacts = withFacts.Select(m => m.Facts!).Distinct().ToList();
            if (distinctFacts.Count > 1)
                issues.Add($"{declaration.Member} ({declaration.EntryPoint}): producers recorded {distinctFacts.Count} different facts; the first by id is cited");
            var record = withFacts.OrderBy(m => m.Facts!.Id, StringComparer.Ordinal).ThenBy(m => (int)m.Source).First();
            var facts = record.Facts!;
            cited.TryAdd(facts.Id, facts);
            entries.Add(new SwiftTransitionLedgerEntry(
                declaration.Member, declaration.EntryPoint, declaration.Library,
                facts.IsComplete ? SwiftTransitionLedgerStatus.Complete : SwiftTransitionLedgerStatus.Incomplete,
                facts.Id, record.Source, facts.Target.Synthesizer,
                facts.IsComplete ? ImmutableArray<string>.Empty : facts.Unknowns));
        }

        return new SwiftTransitionLedger
        {
            Module = module,
            Entries = entries
                .OrderBy(e => e.Member, StringComparer.Ordinal)
                .ThenBy(e => e.EntryPoint, StringComparer.Ordinal)
                .ThenBy(e => e.Library, StringComparer.Ordinal)
                .ToImmutableArray(),
            Facts = cited.Values.ToImmutableArray(),
            Issues = issues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(),
            Registered = (registered ?? Array.Empty<SyntheticSwiftFunction>())
                .GroupBy(f => f.Synthesizer.ToString())
                .ToImmutableSortedDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// The keys of <paramref name="sites"/> (member and entry point, as an IL inventory names them)
    /// that have no ledger entry, and the ledger entries no site matches. Both empty is a bijection.
    /// Multiplicity counts: a key the IL has twice needs two entries.
    /// </summary>
    public (ImmutableArray<string> MissingFromLedger, ImmutableArray<string> MissingFromSites) Compare(
        IEnumerable<(string Member, string EntryPoint)> sites)
    {
        var remaining = Entries.GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
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

    // ---- reading the emitted C# ----

    internal sealed record SwiftDeclaration(string Member, string Method, string EntryPoint, string Library);

    /// <summary>
    /// Every method in <paramref name="source"/> that is a native import (<c>[LibraryImport]</c> or
    /// <c>[DllImport]</c>) declared <c>CallConvSwift</c> through <c>[UnmanagedCallConv]</c>, named the
    /// way the IL names it.
    /// </summary>
    internal static List<SwiftDeclaration> FindSwiftDeclarations(string source, List<string> issues)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var root = tree.GetRoot();
        var found = new List<SwiftDeclaration>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var attributes = method.AttributeLists.SelectMany(l => l.Attributes).ToList();
            var import = attributes.FirstOrDefault(a => IsAttribute(a, "LibraryImport") || IsAttribute(a, "DllImport"));
            if (import is null)
                continue;
            var callConv = attributes.FirstOrDefault(a => IsAttribute(a, "UnmanagedCallConv"));
            if (callConv is null || !callConv.DescendantNodes().OfType<TypeOfExpressionSyntax>().Any(t => RightmostName(t.Type) == "CallConvSwift"))
                continue;

            var arguments = import.ArgumentList?.Arguments ?? default;
            var library = arguments.FirstOrDefault(a => a.NameEquals is null && a.NameColon is null)?.Expression is LiteralExpressionSyntax lib
                ? lib.Token.ValueText
                : null;
            var entryArgument = arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "EntryPoint");
            var entryPoint = entryArgument?.Expression is LiteralExpressionSyntax ep ? ep.Token.ValueText : method.Identifier.ValueText;
            if (library is null)
            {
                issues.Add($"{MemberOf(method)}: a Swift-convention import whose library is not a literal is not listed");
                continue;
            }
            found.Add(new SwiftDeclaration(MemberOf(method), method.Identifier.ValueText, entryPoint, library));
        }

        // A declaration in an inactive region is not in the syntax tree; say so rather than miss it.
        foreach (var trivia in root.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia)))
        {
            var text = trivia.ToString();
            if (text.Contains("CallConvSwift", StringComparison.Ordinal))
                issues.Add($"a Swift-convention declaration sits in an inactive preprocessor region at line {trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1} and is not listed");
        }

        return found;
    }

    private static bool IsAttribute(AttributeSyntax attribute, string name)
    {
        var simple = RightmostName(attribute.Name);
        return simple == name || simple == name + "Attribute";
    }

    private static string RightmostName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => type.ToString(),
    };

    private static string RightmostName(NameSyntax name) => RightmostName((TypeSyntax)name);

    /// <summary><c>Namespace.Outer+Inner::Method</c>, generic types with their arity, as the IL spells it.</summary>
    private static string MemberOf(MethodDeclarationSyntax method)
    {
        var types = new List<string>();
        var namespaces = new List<string>();
        for (SyntaxNode? node = method.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case TypeDeclarationSyntax type:
                    var arity = type.TypeParameterList?.Parameters.Count ?? 0;
                    types.Add(arity > 0 ? $"{type.Identifier.ValueText}`{arity}" : type.Identifier.ValueText);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    namespaces.Add(NamespaceOf(ns.Name));
                    break;
            }
        }
        types.Reverse();
        namespaces.Reverse();
        var typePath = string.Join("+", types);
        var qualified = namespaces.Count > 0 ? string.Join(".", namespaces) + "." + typePath : typePath;
        return $"{qualified}::{method.Identifier.ValueText}";
    }

    /// <summary>A namespace name as metadata spells it: identifiers without their <c>@</c> escapes.</summary>
    private static string NamespaceOf(NameSyntax name) => name switch
    {
        QualifiedNameSyntax q => NamespaceOf(q.Left) + "." + q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => name.ToString(),
    };

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
        },
        Issues = Issues,
        Transitions = Entries,
        Facts = Facts,
    };

    public string ToJson() =>
        JsonSerializer.Serialize(ToDocument(), SwiftTransitionLedgerJsonContext.Default.SwiftTransitionLedgerDocument) + "\n";

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
                    "Swift transition ledger for {Module}: {Unplanned} of {Total} Swift-convention P/Invokes have no facts; {Issues} issue(s)",
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
