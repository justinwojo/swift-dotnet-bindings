// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BindingsGeneration;

/// <summary>
/// Spells every namespace-rooted name in generated C# from the global namespace
/// (<c>System.X</c> → <c>global::System.X</c>).
/// </summary>
/// <remarks>
/// <para>
/// The generated code lives inside the types it binds, and C# looks a simple name up in the
/// enclosing types' members before it looks at namespaces. A bound Swift member that projects to
/// <c>System</c>, <c>Swift</c>, <c>Foundation</c> or the module's own name therefore captures every
/// <c>System.…</c> / <c>Swift.Runtime.…</c> / <c>Module.…</c> reference written in that type, and
/// the binding stops compiling. <c>global::</c> is the one spelling no declaration can capture.
/// </para>
/// <para>
/// This runs once over each finished C# file rather than at each emission site so that no path
/// can miss it, and it works on the syntax tree so string literals, comments and doc comments are
/// left alone. A chain head is qualified only when it is one of the namespace roots the generator
/// can reference and the file does not declare a type, local, parameter or type parameter of that
/// name: such a declaration is a deliberate reference, and a type colliding with a root stays as it
/// was (the module-root collision has its own rewrite, which this reproduces exactly).
/// </para>
/// </remarks>
internal static class CSharpGlobalQualifier
{
    private const string GlobalPrefix = "global::";

    /// <summary>Roots every binding can reference regardless of the frameworks it touches.</summary>
    private static readonly string[] s_fixedRoots = { "System", "Swift", "Microsoft", "ObjCRuntime" };

    private static readonly Regex s_directiveSymbols = new(
        @"^[ \t]*#[ \t]*(?:if|elif)\b(?<expr>[^\r\n]*)", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_identifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    /// <summary>
    /// The namespace roots generated C# can name: the runtime and BCL roots, every Apple framework
    /// namespace, and the given binding namespaces (the module's own and those of the modules it
    /// references).
    /// </summary>
    public static HashSet<string> BuildRoots(IEnumerable<string> bindingNamespaces)
    {
        var roots = new HashSet<string>(s_fixedRoots, StringComparer.Ordinal);
        foreach (var ns in AppleFrameworkRegistry.CSharpNamespaceRoots)
            roots.Add(ns);
        foreach (var ns in bindingNamespaces)
        {
            if (string.IsNullOrEmpty(ns)) continue;
            var dot = ns.IndexOf('.');
            roots.Add(dot < 0 ? ns : ns.Substring(0, dot));
        }
        return roots;
    }

    /// <summary>
    /// The type names declared in <paramref name="source"/>: every declared name (a root with the
    /// same name as a declared type is not qualified, because a bare reference to it may mean the
    /// type), plus a <c>Name`arity</c> key for each namespace-level type (a bare imported name that
    /// matches one means the module's own type, which namespace lookup finds before any import).
    /// </summary>
    public static HashSet<string> CollectDeclaredTypeNames(string source)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in ParseAllBranches(source))
        {
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)) continue;
                var (name, arity) = DeclaredName((MemberDeclarationSyntax)node);
                names.Add(name);
                if (node.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
                    names.Add(ArityKey(name, arity));
            }
        }
        return names;
    }

    private static (string Name, int Arity) DeclaredName(MemberDeclarationSyntax declaration) => declaration switch
    {
        TypeDeclarationSyntax t => (t.Identifier.ValueText, t.TypeParameterList?.Parameters.Count ?? 0),
        DelegateDeclarationSyntax d => (d.Identifier.ValueText, d.TypeParameterList?.Parameters.Count ?? 0),
        BaseTypeDeclarationSyntax b => (b.Identifier.ValueText, 0),
        _ => ("", -1),
    };

    private static string ArityKey(string name, int arity) => name + "`" + arity;

    /// <summary>
    /// Qualifies <paramref name="source"/> for a standalone file (no module-root collision rewrite).
    /// </summary>
    public static string Qualify(string source, IReadOnlySet<string> roots)
        => Qualify(source, roots, CollectDeclaredTypeNames(source), collisionNamespace: null,
            collisionNestedTypeNames: null, journal: null);

    /// <summary>
    /// Qualifies every namespace-rooted chain in <paramref name="source"/>.
    /// </summary>
    /// <param name="source">A complete C# file.</param>
    /// <param name="roots">The namespace roots to qualify.</param>
    /// <param name="declaredTypeNames">Type names declared by the module; a root among them is left
    /// alone.</param>
    /// <param name="collisionNamespace">The module namespace when the module declares a type of the
    /// same name. Its references go through <see cref="StringEmitter.QualifyNamespaceReferences(string, string, HashSet{string}, TextEditJournal?)"/>'s
    /// rewrite unchanged, so that case keeps its exact output.</param>
    /// <param name="collisionNestedTypeNames">The colliding type's nested type names.</param>
    /// <param name="journal">Records each insertion so fragment offsets can be carried over.</param>
    /// <param name="hierarchy">The whole module's types, for nested types a file's classes inherit
    /// from a base class declared in another file; <paramref name="source"/>'s own when null.</param>
    public static string Qualify(
        string source,
        IReadOnlySet<string> roots,
        IReadOnlySet<string> declaredTypeNames,
        string? collisionNamespace,
        HashSet<string>? collisionNestedTypeNames,
        TextEditJournal? journal,
        TypeHierarchy? hierarchy = null)
    {
        hierarchy ??= TypeHierarchy.Build(source);
        var insertions = new SortedDictionary<int, string>();

        if (collisionNamespace != null)
        {
            // The module-root collision rewrite, positions only: each of its edits replaces `Ns.`
            // by `global::Ns.`, which is an insertion at the edit's start.
            var collisionEdits = new TextEditJournal();
            StringEmitter.QualifyNamespaceReferences(
                source, collisionNamespace, collisionNestedTypeNames ?? new HashSet<string>(), collisionEdits);
            foreach (var edit in collisionEdits.Edits)
                insertions[edit.Start] = GlobalPrefix;
        }

        foreach (var tree in ParseAllBranches(source))
        {
            CollectRootInsertions(tree, roots, declaredTypeNames, collisionNamespace, insertions);
            CollectImportedNameInsertions(tree, declaredTypeNames, hierarchy, insertions);
        }

        if (insertions.Count == 0)
            return source;

        var builder = new StringBuilder(source.Length + insertions.Count * GlobalPrefix.Length);
        var last = 0;
        foreach (var (position, text) in insertions)
        {
            builder.Append(source, last, position - last);
            builder.Append(text);
            journal?.Record(position, 0, text.Length);
            last = position;
        }
        builder.Append(source, last, source.Length - last);
        return builder.ToString();
    }

    private static void CollectRootInsertions(
        SyntaxTree tree,
        IReadOnlySet<string> roots,
        IReadOnlySet<string> declaredTypeNames,
        string? collisionNamespace,
        SortedDictionary<int, string> insertions)
    {
        var root = tree.GetRoot();
        var scopeNames = new Dictionary<SyntaxNode, HashSet<string>>();
        foreach (var name in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var text = name.Identifier.ValueText;
            if (!roots.Contains(text) || text == collisionNamespace) continue;
            if (declaredTypeNames.Contains(text) || DeclaredInScope(name, text, scopeNames)) continue;

            var isChainHead = name.Parent switch
            {
                QualifiedNameSyntax q => q.Left == name,
                MemberAccessExpressionSyntax ma => ma.Expression == name
                    && ma.IsKind(SyntaxKind.SimpleMemberAccessExpression),
                _ => false,
            };
            if (!isChainHead) continue;
            if (name.Ancestors().Any(a => IsNamespaceName(a, name)))
                continue;

            insertions[name.SpanStart] = GlobalPrefix;
        }
    }

    /// <summary>
    /// Qualifies each bare type name the generated code used to reach through a <c>using</c>
    /// directive (<c>IntPtr</c> → <c>global::System.IntPtr</c>), and each bare call to the
    /// <see cref="object.ReferenceEquals"/> every type inherits (→ <c>object.ReferenceEquals</c>).
    /// A name is left alone where it means something of the module's own: a local, parameter or type
    /// parameter in scope, a type of the same name and arity nested in an enclosing type, or a
    /// namespace-level type of the module.
    /// </summary>
    private static void CollectImportedNameInsertions(
        SyntaxTree tree,
        IReadOnlySet<string> declaredTypeNames,
        TypeHierarchy hierarchy,
        SortedDictionary<int, string> insertions)
    {
        var imported = ImportedTypeNamespaces;
        var scopeNames = new Dictionary<SyntaxNode, HashSet<string>>();
        foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var text = name.Identifier.ValueText;
            var arity = name is GenericNameSyntax g ? g.TypeArgumentList.Arguments.Count : 0;
            if (!IsBareReference(name)) continue;

            if (text == nameof(ReferenceEquals) && arity == 0
                && name.Parent is InvocationExpressionSyntax inv && inv.Expression == name)
            {
                if (!DeclaredInScope(name, text, scopeNames))
                    insertions[name.SpanStart] = "object.";
                continue;
            }

            // A type cannot be invoked, so an invoked bare name is a method: the module's own.
            if (name.Parent is InvocationExpressionSyntax call && call.Expression == name) continue;

            var lookup = text;
            if (!imported.ContainsKey(lookup) && name.Parent is AttributeSyntax attribute && attribute.Name == name)
                lookup = text + "Attribute";
            if (!imported.TryGetValue(lookup, out var ns)) continue;
            if (declaredTypeNames.Contains(ArityKey(text, arity))
                || DeclaredInEnclosingType(name, text, arity, hierarchy)
                || DeclaredInScope(name, text, scopeNames))
                continue;

            insertions[name.SpanStart] = GlobalPrefix + ns + ".";
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/> stands alone, i.e. it is looked up in scope rather than as a
    /// member of something to its left, and is not a member name in an initializer or argument label.
    /// </summary>
    private static bool IsBareReference(SimpleNameSyntax name)
    {
        switch (name.Parent)
        {
            case QualifiedNameSyntax q when q.Right == name:
            case MemberAccessExpressionSyntax ma when ma.Name == name:
            case AliasQualifiedNameSyntax aq when aq.Name == name:
            case MemberBindingExpressionSyntax:
            case NameColonSyntax:
            case NameEqualsSyntax:
            case AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } assign when assign.Left == name:
                return false;
        }
        return !name.Ancestors().Any(a => a is UsingDirectiveSyntax
            || (a is BaseNamespaceDeclarationSyntax ns && ns.Name.Span.Contains(name.Span)));
    }

    /// <summary>
    /// Whether a type of this name and arity is nested in a type enclosing <paramref name="name"/>,
    /// or inherited from one of their base classes: C# member lookup finds an inherited nested type
    /// before any namespace, so a bare reference to it (inside a class whose Swift parent is another
    /// bound class with that nested type) means the module's own type.
    /// </summary>
    private static bool DeclaredInEnclosingType(
        SyntaxNode name, string text, int arity, TypeHierarchy hierarchy)
    {
        var key = ArityKey(text, arity);
        foreach (var type in name.Ancestors().OfType<TypeDeclarationSyntax>())
        {
            foreach (var member in type.Members)
            {
                if (member is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)) continue;
                var (declared, declaredArity) = DeclaredName(member);
                if (declared == text && declaredArity == arity)
                    return true;
            }
            if (hierarchy.InheritsNestedType(TypeHierarchy.BaseNames(type), key))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Each declared type's nested type names and base type names, across a whole module's output.
    /// The per-file pass needs it because a base class usually lives in another file than its
    /// subclass. Types are keyed by simple name, so a base's candidates are every declaration of
    /// that name and arity: that can only leave a name bare which the compile then rejects, never
    /// qualify one that means the module's own type.
    /// </summary>
    public sealed class TypeHierarchy
    {
        private sealed record Entry(HashSet<string> NestedKeys, List<string> BaseKeys);

        private readonly Dictionary<string, List<Entry>> _byKey = new(StringComparer.Ordinal);

        /// <summary>Indexes every type declared in the given sources.</summary>
        public static TypeHierarchy Build(params string[] sources)
        {
            var hierarchy = new TypeHierarchy();
            foreach (var source in sources)
            {
                foreach (var tree in ParseAllBranches(source))
                {
                    foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                    {
                        var nested = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var member in type.Members)
                        {
                            if (member is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)) continue;
                            var (declared, declaredArity) = DeclaredName(member);
                            nested.Add(ArityKey(declared, declaredArity));
                        }
                        var (typeName, typeArity) = DeclaredName(type);
                        var typeKey = ArityKey(typeName, typeArity);
                        if (!hierarchy._byKey.TryGetValue(typeKey, out var entries))
                            hierarchy._byKey[typeKey] = entries = new List<Entry>();
                        entries.Add(new Entry(nested, BaseNames(type)));
                    }
                }
            }
            return hierarchy;
        }

        internal static List<string> BaseNames(TypeDeclarationSyntax type)
        {
            var keys = new List<string>();
            if (type.BaseList is null) return keys;
            foreach (var baseType in type.BaseList.Types)
            {
                SimpleNameSyntax? simple = baseType.Type switch
                {
                    SimpleNameSyntax s => s,
                    QualifiedNameSyntax q => q.Right,
                    AliasQualifiedNameSyntax a => a.Name,
                    _ => null,
                };
                if (simple is null) continue;
                var arity = simple is GenericNameSyntax g ? g.TypeArgumentList.Arguments.Count : 0;
                keys.Add(ArityKey(simple.Identifier.ValueText, arity));
            }
            return keys;
        }

        /// <summary>Whether any type reachable through these base names declares the nested type.</summary>
        internal bool InheritsNestedType(IEnumerable<string> baseKeys, string nestedKey)
        {
            var visited = new HashSet<Entry>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<string>(baseKeys);
            while (pending.Count > 0)
            {
                if (!_byKey.TryGetValue(pending.Pop(), out var entries)) continue;
                foreach (var entry in entries)
                {
                    if (!visited.Add(entry)) continue;
                    if (entry.NestedKeys.Contains(nestedKey)) return true;
                    foreach (var baseKey in entry.BaseKeys)
                        pending.Push(baseKey);
                }
            }
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string>? s_importedTypeNamespaces;

    /// <summary>
    /// Simple name → namespace for every public top-level type of the namespaces generated code
    /// once imported with <c>using</c> directives (the embedded <c>imported-type-names.json</c>).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ImportedTypeNamespaces
        => s_importedTypeNamespaces ??= LoadImportedTypeNamespaces();

    private static IReadOnlyDictionary<string, string> LoadImportedTypeNamespaces()
    {
        const string resourceName = "Swift.Bindings.Data.imported-type-names.json";
        using var stream = typeof(CSharpGlobalQualifier).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        var file = Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd());
        if ((int?)file["schemaVersion"] != 1)
            throw new InvalidOperationException("imported-type-names.json has an unexpected schemaVersion.");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ns in (Newtonsoft.Json.Linq.JObject)file["namespaces"]!)
        {
            foreach (var name in ns.Value!)
            {
                var simple = (string)name!;
                if (!map.TryAdd(simple, ns.Key))
                    throw new InvalidOperationException(
                        $"imported-type-names.json lists '{simple}' under both '{map[simple]}' and '{ns.Key}'.");
            }
        }
        return map;
    }

    /// <summary>
    /// Whether a parameter, local or type parameter named <paramref name="text"/> is declared by a
    /// member or type enclosing <paramref name="name"/>. Judged per enclosing declaration, so the
    /// answer for a position does not depend on what else the file holds.
    /// </summary>
    private static bool DeclaredInScope(SyntaxNode name, string text, Dictionary<SyntaxNode, HashSet<string>> cache)
    {
        foreach (var ancestor in name.Ancestors())
        {
            if (ancestor is TypeDeclarationSyntax type)
            {
                if (type.TypeParameterList?.Parameters.Any(p => p.Identifier.ValueText == text) == true)
                    return true;
                continue;
            }
            if (ancestor is DelegateDeclarationSyntax)
                return false;
            if (ancestor is not MemberDeclarationSyntax member || ancestor is BaseNamespaceDeclarationSyntax)
                continue;
            if (!cache.TryGetValue(member, out var names))
            {
                names = CollectScopeNames(member);
                cache[member] = names;
            }
            if (names.Contains(text))
                return true;
        }
        return false;
    }

    private static HashSet<string> CollectScopeNames(SyntaxNode member)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in member.DescendantNodesAndSelf(n => n == member || n is not BaseTypeDeclarationSyntax))
        {
            switch (node)
            {
                case ParameterSyntax p: names.Add(p.Identifier.ValueText); break;
                case TypeParameterSyntax tp: names.Add(tp.Identifier.ValueText); break;
                case VariableDeclaratorSyntax v when v.Parent?.Parent is not FieldDeclarationSyntax
                    and not EventFieldDeclarationSyntax:
                    names.Add(v.Identifier.ValueText); break;
                case ForEachStatementSyntax fe: names.Add(fe.Identifier.ValueText); break;
                case SingleVariableDesignationSyntax sv: names.Add(sv.Identifier.ValueText); break;
                case CatchDeclarationSyntax c: names.Add(c.Identifier.ValueText); break;
                case LocalFunctionStatementSyntax lf: names.Add(lf.Identifier.ValueText); break;
            }
        }
        return names;
    }

    private static bool IsNamespaceName(SyntaxNode ancestor, SyntaxNode name) => ancestor switch
    {
        UsingDirectiveSyntax => true,
        BaseNamespaceDeclarationSyntax ns => ns.Name.Span.Contains(name.Span),
        _ => false,
    };

    /// <summary>
    /// Parses <paramref name="source"/> once with every conditional-compilation symbol it tests
    /// defined and once with none, so the code under both arms of each <c>#if</c> is visited.
    /// </summary>
    private static IEnumerable<SyntaxTree> ParseAllBranches(string source)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in s_directiveSymbols.Matches(source))
        {
            foreach (Match id in s_identifier.Matches(m.Groups["expr"].Value))
            {
                if (id.Value is not ("true" or "false" or "defined"))
                    symbols.Add(id.Value);
            }
        }

        var baseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        yield return CSharpSyntaxTree.ParseText(source, baseOptions.WithPreprocessorSymbols(symbols));
        if (symbols.Count > 0)
            yield return CSharpSyntaxTree.ParseText(source, baseOptions);
    }
}
