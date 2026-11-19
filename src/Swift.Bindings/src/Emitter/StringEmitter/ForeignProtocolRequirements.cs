// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BindingsGeneration;

/// <summary>
/// The requirements of a protocol another module declares, read from where that module declares it:
/// the module database of a bound dependency (<see cref="ModuleDecl.DependencyProtocols"/>), else the
/// module's textual interface in the run's SDK (the standard library's under <c>usr/lib/swift</c>, a
/// framework's under its <c>Modules</c> directory). Inherited protocols are followed to their own
/// declarations, and a <c>typealias P = A &amp; B</c> composition stands for its parts.
///
/// The answer is null when any protocol on the way cannot be read (no dependency database names it,
/// and no SDK interface reachable from this run declares it): an extension member of such a protocol
/// may supply the default of a requirement nobody here can see.
/// </summary>
internal static class ForeignProtocolRequirements
{
    /// <summary>
    /// Constraints the language itself defines, which no interface declares and which carry no
    /// requirements: <c>AnyObject</c> and the suppressible <c>Copyable</c>/<c>Escapable</c>.
    /// </summary>
    private static readonly HashSet<string> LanguageConstraints = new(StringComparer.Ordinal)
    {
        "AnyObject", "Copyable", "Escapable",
    };

    /// <summary>The modules an unqualified protocol name is looked up in, before the module's own dependencies.</summary>
    private static readonly string[] ImplicitModules = { "Swift", "_Concurrency", "Foundation" };

    private static readonly ConcurrentDictionary<string, string?> InterfaceTexts = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Declaration?> Declarations = new(StringComparer.Ordinal);

    /// <summary>
    /// The requirements of <paramref name="protocolName"/> and every protocol it inherits, or null
    /// when one of them cannot be read.
    /// </summary>
    internal static Requirements? Of(ModuleDecl moduleDecl, string protocolName)
    {
        var names = new Requirements();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return Collect(moduleDecl, protocolName, names, visited) ? names : null;
    }

    /// <summary>
    /// Requirement keys: a property's name, an operator's spelling, and a method's or initializer's
    /// name with its argument labels (<c>hash(into:)</c>, <c>init(from:)</c>), so an overload with other
    /// labels is not taken for a requirement; <c>static </c> before a static member and <c> async</c>
    /// after an async method. A method whose labels the interface line does not show whole is kept as
    /// <c>name(*)</c> and matches any labels. A subscript is keyed by its parameter types
    /// (<see cref="SubscriptKey"/>), since its parameters carry no argument labels.
    /// </summary>
    internal sealed class Requirements
    {
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _typedOperators = new(StringComparer.Ordinal);

        internal void Add(string key) => _keys.Add(key);

        /// <summary>Adds an operator requirement's typed key (<see cref="OperatorKey"/>) under its spelling key.</summary>
        internal void AddTypedOperator(string key, string typedKey)
        {
            if (!_typedOperators.TryGetValue(key, out var typed))
                _typedOperators[key] = typed = new HashSet<string>(StringComparer.Ordinal);
            typed.Add(typedKey);
        }

        internal void UnionWith(Requirements other)
        {
            _keys.UnionWith(other._keys);
            foreach (var (key, typed) in other._typedOperators)
                foreach (var typedKey in typed)
                    AddTypedOperator(key, typedKey);
        }

        /// <summary>
        /// Whether an operator requirement is this operator: by its types where the requirement's
        /// declaration showed them, by its spelling alone otherwise.
        /// </summary>
        internal bool DeclaresOperator(string key, string typedKey)
            => _typedOperators.TryGetValue(key, out var typed) ? typed.Contains(typedKey) : Declares(key);

        /// <summary>
        /// Whether a requirement has this member's key (<see cref="MethodKey"/>, <see cref="PropertyKey"/>).
        /// A sync method also supplies an async requirement; an async one supplies only an async requirement.
        /// </summary>
        internal bool Declares(string memberKey)
        {
            const string async = " async";
            if (memberKey.EndsWith(async, StringComparison.Ordinal))
                return Has(memberKey[..^async.Length], async);
            return Has(memberKey, string.Empty) || Has(memberKey, async);
        }

        private bool Has(string key, string suffix)
        {
            if (_keys.Contains(key + suffix))
                return true;
            var paren = key.IndexOf('(');
            return paren > 0 && _keys.Contains(key[..paren] + "(*)" + suffix);
        }
    }

    /// <summary>A method's requirement key: its name and labels (an operator's spelling alone) and its effects.</summary>
    internal static string MethodKey(string name, IEnumerable<string>? labels, bool isStatic, bool isAsync)
        => (isStatic ? "static " : string.Empty)
            + (IsOperator(name) ? name : $"{name}({(labels is null ? "*" : string.Concat(labels.Select(l => l + ":")))})")
            + (isAsync ? " async" : string.Empty);

    /// <summary>A property's requirement key.</summary>
    internal static string PropertyKey(string name, bool isStatic) => (isStatic ? "static " : string.Empty) + name;

    /// <summary>
    /// A subscript's requirement key: its parameter and return types
    /// (<see cref="ForeignOverloadPairing.Normalize"/>, a <c>Self.</c> qualifier dropped), since a
    /// subscript's parameters seldom carry argument labels. A signature that does not read gives a key
    /// no requirement has.
    /// </summary>
    internal static string SubscriptKey(OverloadSignature? signature, bool isStatic)
        => (isStatic ? "static " : string.Empty) + "subscript" + TypesKey(signature);

    /// <summary>An operator's requirement key with its parameter and return types (see <see cref="SubscriptKey"/>).</summary>
    internal static string OperatorKey(string name, OverloadSignature? signature, bool isStatic)
        => (isStatic ? "static " : string.Empty) + name + TypesKey(signature);

    private static string TypesKey(OverloadSignature? signature)
    {
        static string Type(string? type) => SelfQualifier.Replace(ForeignOverloadPairing.Normalize(type) ?? "?", string.Empty);
        return signature?.Parameters is not { } parameters
            ? "(?)"
            : $"({string.Join(",", parameters.Select(Type))})->{Type(signature.Return)}";
    }

    private static readonly Regex SelfQualifier = new(@"\bSelf\.", RegexOptions.Compiled);

    /// <summary>
    /// The requirement key of an extension member the interface reader extracted, from its printed
    /// name (<c>name(label:)</c>) and signature.
    /// </summary>
    internal static string KeyOf(ProtocolExtensionMethodDecl member)
    {
        if (member.IsProperty)
            return PropertyKey(member.MethodName, member.IsStatic);
        var key = IsOperator(member.MethodName) ? member.MethodName : member.PrintedName;
        return (member.IsStatic ? "static " : string.Empty) + key
            + (ProtocolExtensionEmitter.IsAsyncSignature(member.RawSignature) ? " async" : string.Empty);
    }

    private static bool IsOperator(string name) => name.Length > 0 && !(char.IsLetter(name[0]) || name[0] is '_' or '`');

    private static bool Collect(ModuleDecl moduleDecl, string protocolName, Requirements names, HashSet<string> visited)
    {
        var plain = Plain(protocolName);
        if (plain.Length == 0 || plain[0] == '~')
            return true;
        var dot = plain.LastIndexOf('.');
        var shortName = dot < 0 ? plain : plain[(dot + 1)..];
        var module = dot < 0 ? null : plain[..plain.IndexOf('.')];
        if (LanguageConstraints.Contains(shortName) && (module is null or "Swift"))
            return true;
        if (!visited.Add(module is null ? plain : $"{module}.{shortName}"))
            return true;

        var candidates = module is not null
            ? new[] { module }
            : ImplicitModules.Concat(moduleDecl.Dependencies ?? new List<string>()).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var candidate in candidates)
        {
            if (Lookup(moduleDecl, candidate, shortName) is not { } declaration)
                continue;
            foreach (var key in declaration.Names)
                names.Add(key);
            foreach (var (key, typed) in declaration.TypedOperators ?? Array.Empty<(string, string)>())
                names.AddTypedOperator(key, typed);
            foreach (var inherited in declaration.Inherited)
                if (!Collect(moduleDecl, Qualify(inherited, declaration.Module), names, visited))
                    return false;
            return true;
        }
        return false;
    }

    private sealed record Declaration(string Module, IReadOnlyList<string> Names, IReadOnlyList<string> Inherited)
    {
        /// <summary>The operator requirements whose declarations show their types: spelling key, typed key.</summary>
        public IReadOnlyList<(string Key, string Typed)>? TypedOperators { get; init; }
    }

    private static Declaration? Lookup(ModuleDecl moduleDecl, string module, string protocolName)
    {
        if (moduleDecl.DependencyProtocols.TryGetValue(module, out var protocols)
            && protocols.FirstOrDefault(p => p.Name == protocolName) is { } bound)
        {
            var memberNames = bound.Methods.Where(m => !m.IsAccessor)
                .Select(ProtocolExtensionEmitter.RequirementKeyOf)
                .Concat(bound.Properties.Select(ProtocolExtensionEmitter.RequirementKeyOf))
                .Concat(bound.Subscripts.Select(s => SubscriptKey(
                    ForeignOverloadPairing.ListedSignature(null, s.IndexParameters.Select(a => a.SwiftTypeSpec?.ToString()),
                        s.ReturnTypeSpec?.ToString()), s.IsStatic)))
                .OfType<string>()
                .ToList();
            return new Declaration(module, memberNames, bound.InheritedProtocols.Select(i => i.Name).ToList());
        }
        return LookupInInterfaces(module, protocolName, new HashSet<string>(StringComparer.Ordinal));
    }

    // The module's interface in each SDK of the run; a protocol a module re-exports from another
    // (`@_exported import M`) is followed there.
    private static Declaration? LookupInInterfaces(string module, string protocolName, HashSet<string> visitedModules)
    {
        if (!visitedModules.Add(module) || SdkPublicModuleNames.Current is not { } sdk)
            return null;
        foreach (var root in sdk.SdkRoots)
        {
            if (InterfaceText(root, module) is not { } text)
                continue;
            if (Declarations.GetOrAdd($"{root}\n{module}\n{protocolName}", _ => Parse(text, protocolName)) is { } found)
                return found with { Module = module };
            foreach (Match exported in ExportedImport.Matches(text))
            {
                // A selective re-export (`@_exported import protocol M.P`) brings in that one name.
                if (exported.Groups["name"].Success && exported.Groups["name"].Value != protocolName)
                    continue;
                if (LookupInInterfaces(exported.Groups["module"].Value, protocolName, visitedModules) is { } reexported)
                    return reexported;
            }
        }
        return null;
    }

    private static string? InterfaceText(string sdkRoot, string module)
        => InterfaceTexts.GetOrAdd($"{sdkRoot}\n{module}", _ =>
        {
            var path = SwiftModuleInterface(sdkRoot, module) ?? SdkPublicModuleNames.FindModuleInterface(sdkRoot, module);
            if (path is null)
                return null;
            try
            {
                return SwiftModuleSelectors.ReadDotted(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });

    // The toolchain's own modules (the standard library, _Concurrency, …) live under usr/lib/swift.
    private static string? SwiftModuleInterface(string sdkRoot, string module)
    {
        var moduleDir = Path.Combine(sdkRoot, "usr", "lib", "swift", $"{module}.swiftmodule");
        if (!Directory.Exists(moduleDir))
            return null;
        return Directory.EnumerateFiles(moduleDir, "*.swiftinterface")
            .Where(p => !p.EndsWith(".private.swiftinterface", StringComparison.Ordinal)
                && !p.EndsWith(".package.swiftinterface", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }

    // Every top-level declaration of the protocol is read (an interface prints one per `#if` arm),
    // or the protocols a same-named `typealias` composes.
    private static Declaration? Parse(string text, string protocolName)
    {
        var names = new List<string>();
        var typedOperators = new List<(string, string)>();
        var inherited = new List<string>();
        var found = false;
        var searchFrom = 0;
        while (UnderscoreProtocolSynthesizer.TryExtractProtocolBlock(text[searchFrom..], protocolName, out var header, out var body, out var end))
        {
            found = true;
            searchFrom += end;
            inherited.AddRange(InheritanceClause(header));
            foreach (var line in body.Split('\n'))
            {
                var isStatic = StaticMember.IsMatch(line);
                if (FuncName.Match(line) is { Success: true } func)
                {
                    var labels = ParameterLabels(line, func.Index + func.Length, out var clauseEnd);
                    var name = func.Groups[1].Value.Trim('`');
                    var key = MethodKey(name, labels, isStatic, labels is not null && AsyncEffect.IsMatch(line[clauseEnd..]));
                    names.Add(key);
                    if (IsOperator(name) && ForeignOverloadPairing.SignatureOf(line, "func", name) is { Parameters: not null } signature)
                        typedOperators.Add((key, OperatorKey(name, signature, isStatic)));
                }
                else if (VarName.Match(line) is { Success: true } variable)
                    names.Add(PropertyKey(variable.Groups[1].Value.Trim('`'), isStatic));
                else if (SubscriptDeclaration.IsMatch(line))
                {
                    names.Add(SubscriptKey(ForeignOverloadPairing.SignatureOf(line, "subscript"), StaticSubscript.IsMatch(line)));
                }
                else if (Initializer.Match(line) is { Success: true } init)
                {
                    var labels = ParameterLabels(line, init.Index + init.Length - 1, out var clauseEnd);
                    names.Add(MethodKey("init", labels, isStatic: false,
                        labels is not null && AsyncEffect.IsMatch(line[clauseEnd..])));
                }
            }
        }
        if (found)
            return new Declaration(string.Empty, names.Distinct(StringComparer.Ordinal).ToList(), inherited.Distinct(StringComparer.Ordinal).ToList())
            {
                TypedOperators = typedOperators,
            };

        var alias = new Regex($@"\bpublic\s+typealias\s+{Regex.Escape(protocolName)}\s*=\s*([^\n]+)").Match(text);
        if (!alias.Success || !alias.Groups[1].Value.Contains('&'))
            return null;
        return new Declaration(string.Empty, Array.Empty<string>(),
            alias.Groups[1].Value.Split('&').Select(p => p.Trim()).Where(p => p.Length > 0).ToList());
    }

    /// <summary>
    /// The argument labels of the parameter clause starting at <paramref name="from"/> (after the
    /// name, past any generic parameter clause), or null when the clause does not close on the line.
    /// </summary>
    private static List<string>? ParameterLabels(string line, int from, out int clauseEnd)
    {
        clauseEnd = from;
        var i = from;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
            i++;
        if (i < line.Length && line[i] == '<' && SkipBalanced(line, ref i, '<', '>') is false)
            return null;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
            i++;
        if (i >= line.Length || line[i] != '(')
            return null;
        var open = i;
        if (!SkipBalanced(line, ref i, '(', ')'))
            return null;
        clauseEnd = i;
        var clause = line[(open + 1)..(i - 1)];
        var labels = new List<string>();
        foreach (var parameter in SplitTopLevel(clause))
        {
            var colon = TopLevelColon(parameter);
            if (colon < 0)
                return null;
            var names = parameter[..colon].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (names.Length == 0)
                return null;
            labels.Add(names[0].Trim('`'));
        }
        return labels;
    }

    // Moves past the bracketed run opening at `i`; false when it does not close on the line.
    private static bool SkipBalanced(string line, ref int i, char open, char close)
    {
        var depth = 0;
        for (; i < line.Length; i++)
        {
            var c = line[i];
            if (c == open)
                depth++;
            else if (c == close && !(close == '>' && i > 0 && line[i - 1] == '-') && --depth == 0)
            {
                i++;
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> SplitTopLevel(string clause)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < clause.Length; i++)
        {
            switch (clause[i])
            {
                case '(' or '[' or '<' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case '>' when i == 0 || clause[i - 1] != '-':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return clause[start..i];
                    start = i + 1;
                    break;
            }
        }
        if (clause[start..].Trim().Length > 0)
            yield return clause[start..];
    }

    private static int TopLevelColon(string parameter)
    {
        var depth = 0;
        for (var i = 0; i < parameter.Length; i++)
        {
            switch (parameter[i])
            {
                case '(' or '[' or '<':
                    depth++;
                    break;
                case ')' or ']' or '>':
                    depth--;
                    break;
                case ':' when depth == 0:
                    return i;
            }
        }
        return -1;
    }

    private static IEnumerable<string> InheritanceClause(string header)
    {
        var colon = header.IndexOf(':');
        if (colon < 0)
            yield break;
        var clause = header[(colon + 1)..].TrimEnd('{', ' ', '\n', '\t');
        var where = Regex.Match(clause, @"\bwhere\b");
        if (where.Success)
            clause = clause[..where.Index];
        foreach (var part in clause.Split(','))
        {
            var name = part.Trim();
            if (name.Length > 0)
                yield return name;
        }
    }

    // A name printed without a module inside another module's interface is that module's.
    private static string Qualify(string name, string module)
    {
        var plain = Plain(name);
        return plain.Contains('.') || plain.StartsWith('~') || LanguageConstraints.Contains(plain) ? plain : $"{module}.{plain}";
    }

    // Module selectors as dots, generic arguments and an `any` dropped.
    private static string Plain(string name)
    {
        var plain = SwiftModuleSelectors.ToDotted(name).Trim();
        if (plain.StartsWith("any ", StringComparison.Ordinal))
            plain = plain[4..].Trim();
        var angle = plain.IndexOf('<');
        return angle < 0 ? plain : plain[..angle].Trim();
    }

    // An identifier, or an operator (`static func < (…)`), whose spelling can hold `<`.
    private static readonly Regex FuncName = new(@"\bfunc\s+(`[^`]+`|[\p{L}_]\w*|[/=\-+!*%<>&|^~?.]+)", RegexOptions.Compiled);
    private static readonly Regex VarName = new(@"\bvar\s+(`[^`]+`|\w+)\s*:", RegexOptions.Compiled);
    private static readonly Regex Initializer = new(@"(?:^|\s)init[?!]?\s*[(<]", RegexOptions.Compiled);
    private static readonly Regex StaticMember = new(@"\b(?:static|class)\s+(?:[\w@]+\s+)*(?:func|var)\b", RegexOptions.Compiled);
    private static readonly Regex SubscriptDeclaration = new(@"(?:^|\s)subscript\s*[(<]", RegexOptions.Compiled);
    private static readonly Regex StaticSubscript = new(@"\b(?:static|class)\s+(?:[\w@]+\s+)*subscript\b", RegexOptions.Compiled);
    private static readonly Regex AsyncEffect = new(@"^\s*(?:re)?async\b", RegexOptions.Compiled);
    private static readonly Regex ExportedImport = new(@"(?m)^\s*@_exported\s+(?:(?:public|package|internal)\s+)?import\s+(?:(?:struct|class|enum|protocol|typealias|func|var|let)\s+(?<module>\w+)\.(?:\w+\.)*(?<name>\w+)|(?<module>\w+))", RegexOptions.Compiled);
}
