// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BindingsGeneration;

/// <summary>
/// A member whose <c>SB0001</c> marker the final route no longer warrants: every Swift call it makes
/// goes through a native bridge, and none is a direct Swift-convention transition besides a type
/// metadata accessor.
/// </summary>
/// <param name="Key">The member's identity in the C# (<see cref="SwiftTransitionGuards.KeyOf"/>).</param>
/// <param name="Display">How the member is named in the note that replaces the marker.</param>
/// <param name="Kind">Method, Constructor, Property, Indexer or Operator.</param>
/// <param name="Name">Its member name.</param>
/// <param name="ContainingType">Its declaring type, namespace-qualified.</param>
/// <param name="Bridges">The bridge symbols it reaches.</param>
internal sealed record SwiftRouteMarkerSettlement(
    string Key, string Display, string Kind, string Name, string ContainingType, ImmutableArray<string> Bridges);

/// <summary>
/// Settles the <c>SB0001</c> marker of a member that bridge routing moved off its direct
/// Swift-convention import.
/// </summary>
/// <remarks>
/// <para>
/// Emission writes <c>[Obsolete(…, DiagnosticId = "SB0001")]</c> on a member that has neither a
/// <c>@_cdecl</c> wrapper nor a native thunk, because it then calls Swift directly with a P/Invoke
/// whose convention the runtime may not honour. Routing runs later and rewrites such an import into a
/// C-convention call of a native bridge (<c>SBSR_…</c>), proved against swiftc's own lowering, so the
/// sentence the marker carries is no longer true of the member. The marker is settled from the final
/// C#: a member is settled when its calls reach a bridge and reach no direct Swift-convention import,
/// function-pointer call or callback, read from the same reverse call graph the transition guards are
/// planned on. A member that still reaches one keeps its marker.
/// </para>
/// <para>
/// A type metadata accessor (<c>$s…Ma</c>) is the one Swift-convention import that does not count. A
/// member of a generic type reaches its type's accessor to build the metadata it passes, as every
/// unmarked member and the type's own initializer do; the accessor's signature is the runtime's
/// fixed one (the request and the type's key arguments in, the metadata response out), not the
/// member's transition the marker is about.
/// </para>
/// <para>
/// Settling removes the <c>SB0001</c> sentence. A marker that said nothing else goes, with the
/// <c>[EditorBrowsable(Never)]</c> emission wrote for it; a library deprecation stays as a plain
/// <c>[Obsolete]</c>, and a missing symbol or unusable return stays under <c>SB0002</c>. A one-line
/// note naming the bridges takes the marker's place, so a reader of the binding sees why the member
/// is unmarked and a journal can undo the edit by text.
/// </para>
/// </remarks>
internal static class SwiftRouteMarkers
{
    /// <summary>
    /// True when <paramref name="entryPoint"/> is a Swift type metadata accessor, which settlement does
    /// not count as the member's direct transition.
    /// </summary>
    public static bool IsMetadataAccessorSymbol(string entryPoint) =>
        entryPoint.StartsWith("$s", StringComparison.Ordinal) && entryPoint.EndsWith("Ma", StringComparison.Ordinal);

    /// <summary>The start of the note that replaces a settled marker.</summary>
    public const string NotePrefix = "// Routed through its native bridge";

    private const string TroubleshootingUrl = "https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting";

    // The sentence WrapperValidation writes for SB0001, with its optional cause clause, and the
    // separator that joins it to the next issue or ends the message.
    private static readonly Regex Sentence = new(
        @"No @_cdecl wrapper or native thunk available(?: \((?:[^()]|\([^()]*\))*\))?\. P/Invoke calling convention may not match Swift ABI(?:\. |\.$)",
        RegexOptions.Compiled);

    /// <summary>Whether <paramref name="source"/> may hold a marker routing can settle: an SB0001 marker and a bridge import.</summary>
    public static bool MayApply(string source) =>
        source.Contains(WrapperValidation.DirectCallConvSwiftDiagnosticId, StringComparison.Ordinal)
        && source.Contains(SwiftCallPlan.ForwardPrefix, StringComparison.Ordinal);

    /// <summary>The SB0001 marker of <paramref name="member"/>, or null when it carries none.</summary>
    internal static (AttributeListSyntax List, AttributeSyntax Attribute, string Message)? MarkerOf(SyntaxNode member)
    {
        var lists = member switch
        {
            BaseMethodDeclarationSyntax method => method.AttributeLists,
            BasePropertyDeclarationSyntax property => property.AttributeLists,
            _ => default,
        };
        foreach (var list in lists)
        {
            if (list.Attributes.Count != 1 || list.Target is not null)
                continue;
            var attribute = list.Attributes[0];
            if (!SwiftTransitionSyntax.IsAttribute(attribute, "Obsolete") || attribute.ArgumentList is not { } arguments)
                continue;
            var id = arguments.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "DiagnosticId");
            if (id?.Expression is not LiteralExpressionSyntax { Token.ValueText: WrapperValidation.DirectCallConvSwiftDiagnosticId })
                continue;
            if (arguments.Arguments.FirstOrDefault(a => a.NameEquals is null && a.NameColon is null)?.Expression is not LiteralExpressionSyntax message
                || !message.IsKind(SyntaxKind.StringLiteralExpression))
                continue;
            return (list, attribute, message.Token.ValueText);
        }
        return null;
    }

    /// <summary>
    /// What is left of a marker's message once its SB0001 sentence is removed: empty when nothing is,
    /// null when the message does not hold the sentence.
    /// </summary>
    internal static string? Strip(string message)
    {
        var match = Sentence.Match(message);
        if (!match.Success)
            return null;
        var rest = (message[..match.Index] + message[(match.Index + match.Length)..]).TrimEnd();
        return rest.Length == 0 || rest.EndsWith('.') ? rest : rest + ".";
    }

    /// <summary>
    /// The edit that settles <paramref name="member"/>'s marker in <paramref name="source"/>: the span of
    /// its marker line (and the <c>[EditorBrowsable(Never)]</c> line right after it), and what replaces
    /// it. Null when the member carries no settleable marker.
    /// </summary>
    internal static (int Start, int Length, string Text)? Edit(SyntaxNode member, string source, SwiftRouteMarkerSettlement settlement, string newline)
    {
        if (MarkerOf(member) is not var (list, attribute, message) || Strip(message) is not { } rest)
            return null;
        var lists = member is BaseMethodDeclarationSyntax method ? method.AttributeLists : ((BasePropertyDeclarationSyntax)member).AttributeLists;
        var start = LineStart(source, list.SpanStart);
        var indent = source[start..list.SpanStart];
        if (indent.Any(c => c is not (' ' or '\t')))
            return null; // the marker shares its line with other text: not emission's own layout
        var end = LineEnd(source, list.Span.End);
        var index = lists.IndexOf(list);
        if (index + 1 < lists.Count && IsEditorBrowsableNever(lists[index + 1])
            && LineStart(source, lists[index + 1].SpanStart) == end)
            end = LineEnd(source, lists[index + 1].Span.End);

        var name = attribute.Name.ToString();
        var text = $"{indent}{NotePrefix} {string.Join(", ", settlement.Bridges)}: {settlement.Display} makes no direct Swift-convention call besides metadata accessors.{newline}";
        if (rest.Length > 0)
        {
            var literal = SymbolDisplay.FormatLiteral(rest, quote: true);
            text += rest.Contains("P/Invoke entry point not exported", StringComparison.Ordinal)
                    || rest.Contains("Return type has no usable surface", StringComparison.Ordinal)
                ? $"{indent}[{name}({literal}, DiagnosticId = \"SB0002\", UrlFormat = \"{TroubleshootingUrl}\")]{newline}"
                : $"{indent}[{name}({literal})]{newline}";
        }
        return (start, end - start, text);
    }

    /// <summary>The members of <paramref name="root"/> that carry an SB0001 marker.</summary>
    internal static IEnumerable<SyntaxNode> Marked(SyntaxNode root) =>
        root.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax && MarkerOf(n) is not null);

    /// <summary>The functions whose calls are the member's own: its body, or each accessor's.</summary>
    internal static IEnumerable<SyntaxNode> FunctionsOf(SyntaxNode member) => member switch
    {
        BasePropertyDeclarationSyntax { AccessorList: { } accessors } => accessors.Accessors.Where(SwiftTransitionGuards.IsFunction),
        _ when SwiftTransitionGuards.IsFunction(member) => [member],
        _ => [],
    };

    /// <summary>
    /// The settled members a file's text records, by the note each carries, and the members that still
    /// carry an SB0001 marker, each as its declaring type path (without namespace) and member name.
    /// </summary>
    internal static (HashSet<(string Type, string Name)> Settled, HashSet<(string Type, string Name)> Marked) MembersOf(string source)
    {
        var settled = new HashSet<(string, string)>();
        var marked = new HashSet<(string, string)>();
        // A file with no note settles nothing but may still hold a marker under a name another file settles.
        if (!source.Contains(NotePrefix, StringComparison.Ordinal)
            && !source.Contains(WrapperValidation.DirectCallConvSwiftDiagnosticId, StringComparison.Ordinal))
            return (settled, marked);
        var root = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
        foreach (var member in root.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax))
        {
            var site = SwiftTransitionGuards.Describe(member);
            var type = string.Join(".", member.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText));
            var key = (type, site.Kind == "Constructor" ? "Init" : site.Name);
            if (MarkerOf(member) is not null)
                marked.Add(key);
            else if (member.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) && t.ToString().StartsWith(NotePrefix, StringComparison.Ordinal)))
                settled.Add(key);
        }
        return (settled, marked);
    }

    private static bool IsEditorBrowsableNever(AttributeListSyntax list) =>
        list.Attributes.Count == 1
        && SwiftTransitionSyntax.IsAttribute(list.Attributes[0], "EditorBrowsable")
        && list.Attributes[0].ToString().Contains("EditorBrowsableState.Never", StringComparison.Ordinal);

    private static int LineStart(string source, int position) => source.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;

    private static int LineEnd(string source, int position)
    {
        var newline = source.IndexOf('\n', position);
        return newline < 0 ? source.Length : newline + 1;
    }
}
