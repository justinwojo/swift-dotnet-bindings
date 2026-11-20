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

    /// <summary>A parameter of a typed requirement: its argument label, its internal name and its type.</summary>
    internal sealed record TypedParameter(string Label, string PrivateName, TypeSpec Type);

    /// <summary>
    /// A method requirement read with its types from the interface that declares it: its name, its
    /// requirement key (<see cref="MethodKey"/>), the declaration line, the attribute lines above it,
    /// its parameters, its return type (<see cref="TupleTypeSpec.Empty"/> for <c>Void</c>) and its
    /// isolation and effects.
    /// </summary>
    internal sealed record TypedRequirement(
        string Name,
        string Key,
        string Declaration,
        IReadOnlyList<string> Attributes,
        IReadOnlyList<TypedParameter> Parameters,
        TypeSpec ReturnType,
        bool IsNonisolated,
        bool IsMainActorIsolated,
        bool Throws);

    /// <summary>A protocol declared in an SDK interface, with its requirements that have no default.</summary>
    internal sealed record TypedParent(string QualifiedName, IReadOnlyList<TypedRequirement> Requirements);

    /// <summary>
    /// The requirements of the protocol <paramref name="qualifiedProtocolName"/> (<c>Module.P</c>)
    /// that no unconstrained extension in its module's interface defaults, read with their types from
    /// the SDK interface that declares it, for a protocol no dependency database describes. All or
    /// nothing: null when the protocol cannot be read, when it inherits a protocol that is not a
    /// marker (one unbound level only), declares an associated type, or when one requirement without
    /// a default is not an instance method whose parameter and return types read as concrete types
    /// (generic, async, static, <c>Self</c>, ownership-qualified and variadic declarations do not).
    /// </summary>
    internal static TypedParent? TypedParentOf(string qualifiedProtocolName)
    {
        var plain = Plain(qualifiedProtocolName);
        var dot = plain.IndexOf('.');
        if (dot <= 0 || dot == plain.Length - 1)
            return null;
        var module = plain[..dot];
        var path = plain[(dot + 1)..];
        if (LookupInInterfaces(module, path, new HashSet<string>(StringComparer.Ordinal)) is not
            { RequirementLines: { } lines, DefaultKeys: { } defaults } declaration
            || declaration.HasAssociatedTypes)
            return null;
        if (!declaration.Inherited.All(IsMarkerParent))
            return null;

        var requirements = new List<TypedRequirement>();
        foreach (var line in lines)
        {
            if (defaults.Contains(line.Key))
                continue;
            if (TypeRequirement(line) is not { } typed)
                return null;
            requirements.Add(typed);
        }
        return new TypedParent($"{module}.{path}", requirements);
    }

    // Inherited constraints that carry no requirements.
    private static bool IsMarkerParent(string inherited)
    {
        var plain = Plain(inherited);
        if (plain.StartsWith('~'))
            return true;
        var leaf = plain.StartsWith("Swift.", StringComparison.Ordinal) ? plain["Swift.".Length..] : plain;
        return leaf is "Sendable" or "SendableMetatype" or "AnyObject" or "Copyable" or "Escapable" or "BitwiseCopyable";
    }

    private static TypedRequirement? TypeRequirement(RequirementLine requirement)
    {
        if (requirement.Kind != RequirementKind.Method || requirement.IsStatic || requirement.Key.Contains("(*)", StringComparison.Ordinal))
            return null;
        var line = requirement.Line.Trim();
        if (FuncName.Match(line) is not { Success: true } func)
            return null;
        var name = func.Groups[1].Value;
        if (name.StartsWith('`') || IsOperator(name))
            return null;
        var modifiers = line[..func.Index];
        if (UnsupportedModifier.IsMatch(modifiers))
            return null;
        var afterName = line[(func.Index + func.Length)..].TrimStart();
        if (afterName.StartsWith('<'))
            return null;
        if (SwiftParameterClause.Effects(line) is not { } effects
            || AsyncEffect.IsMatch(effects) || Regex.IsMatch(effects, @"\b(?:rethrows|throws\s*\()"))
            return null;
        if (SwiftParameterClause.Find(line) is not { } clause)
            return null;
        var tail = line[(clause.Close + 1)..];
        if (Regex.IsMatch(tail, @"\bwhere\b"))
            return null;

        var parsed = ProtocolExtensionEmitter.ParseFunctionSignature(
            line, name, returnsSelf: false, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        if (parsed is not { } signature || signature.returnTypeName == "Self")
            return null;

        var clauseText = line[(clause.Open + 1)..clause.Close];
        var rawParameters = string.IsNullOrWhiteSpace(clauseText)
            ? new List<string>()
            : SwiftTypeListText.SplitTopLevelParameters(clauseText);
        if (rawParameters.Count != signature.parameters.Count)
            return null;
        var parameters = new List<TypedParameter>();
        for (var i = 0; i < rawParameters.Count; i++)
        {
            var raw = rawParameters[i];
            var colon = TopLevelColon(raw);
            if (colon < 0)
                return null;
            var typeText = raw[(colon + 1)..].Trim();
            if (OwnershipOrVariadic.IsMatch(typeText) || typeText.EndsWith("...", StringComparison.Ordinal))
                return null;
            var (label, type, swiftType) = signature.parameters[i];
            if (!IsConcrete(swiftType))
                return null;
            var names = raw[..colon].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var privateName = (names.Length > 1 ? names[1] : names.Length == 1 ? names[0] : "_").Trim('`');
            if (privateName == "_")
                privateName = $"arg{i}";
            parameters.Add(new TypedParameter(label.Trim('`'), privateName, type));
        }

        TypeSpec returnType = TupleTypeSpec.Empty;
        if (signature.returnTypeSpec is { } returnSpec)
        {
            if (!IsConcrete(signature.returnTypeName))
                return null;
            returnType = returnSpec;
        }

        var attributeText = string.Join(" ", requirement.Attributes) + " " + modifiers;
        return new TypedRequirement(
            name,
            requirement.Key,
            line,
            requirement.Attributes,
            parameters,
            returnType,
            IsNonisolated: Regex.IsMatch(attributeText, @"\bnonisolated\b"),
            IsMainActorIsolated: MainActorAttribute.IsMatch(attributeText),
            Throws: Regex.IsMatch(effects, @"\bthrows\b"));
    }

    private static readonly Regex UnsupportedModifier = new(@"\b(?:mutating|consuming|borrowing|__consuming|static|class)\b", RegexOptions.Compiled);
    private static readonly Regex OwnershipOrVariadic = new(@"^(?:@\w+(?:\([^)]*\))?\s+)*(?:inout|borrowing|consuming|__owned|__shared|sending|isolated)\b|@autoclosure\b", RegexOptions.Compiled);
    private static readonly Regex MainActorAttribute = new(@"@(?:_Concurrency\.)?MainActor\b", RegexOptions.Compiled);


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

        // The types of the method requirements under each key; null once one declaration under the key
        // shows no concrete types, which leaves its labels to decide.
        private readonly Dictionary<string, HashSet<string>?> _typedMethods = new(StringComparer.Ordinal);

        internal void Add(string key) => _keys.Add(key);

        /// <summary>
        /// Adds the types of a method requirement (<see cref="MethodTypesKey"/>) under its key, or records
        /// that its types are not known (null), so that its labels decide.
        /// </summary>
        internal void AddTypedMethod(string key, string? typedKey)
        {
            if (typedKey is null)
                _typedMethods[key] = null;
            else if (!_typedMethods.TryGetValue(key, out var typed))
                _typedMethods[key] = new HashSet<string>(StringComparer.Ordinal) { typedKey };
            else
                typed?.Add(typedKey);
        }

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
            foreach (var (key, typed) in other._typedMethods)
            {
                if (typed is null)
                    AddTypedMethod(key, null);
                else
                    foreach (var typedKey in typed)
                        AddTypedMethod(key, typedKey);
            }
        }

        /// <summary>
        /// Whether a method or initializer requirement is this member: by its key
        /// (<see cref="Declares"/>), then by its types (<see cref="MethodTypesKey"/>) where both the
        /// member's and every requirement's under that key are concrete, so an overload with a
        /// requirement's labels and other types is not taken for it. Where either side's types are
        /// generic, <c>Self</c>, associated or unreadable, the labels decide.
        /// </summary>
        internal bool DeclaresMethod(string memberKey, string? typedKey)
        {
            if (!Declares(memberKey))
                return false;
            if (typedKey is null)
                return true;
            const string async = " async";
            var held = (memberKey.EndsWith(async, StringComparison.Ordinal)
                    ? new[] { memberKey }
                    : new[] { memberKey, memberKey + async })
                .Where(_keys.Contains)
                .ToList();
            // A key matched only through a requirement whose labels did not read (`name(*)`).
            if (held.Count == 0)
                return true;
            return held.Any(key => !_typedMethods.TryGetValue(key, out var typed) || typed is null || typed.Contains(typedKey));
        }

        /// <summary>
        /// Whether a requirement is the extension member <paramref name="member"/>: a property by its
        /// key, a method by its key and, where both show concrete types, its types.
        /// </summary>
        internal bool DeclaresMember(ProtocolExtensionMethodDecl member)
            => member.IsProperty || IsOperator(member.MethodName)
                ? Declares(KeyOf(member))
                : DeclaresMethod(KeyOf(member),
                    MethodTypesKey(ForeignOverloadPairing.SignatureOf(member.RawSignature, "func", member.MethodName)));

        /// <summary>
        /// Whether an operator requirement is this operator: by its types where the requirement's
        /// declaration showed them, by its spelling alone otherwise.
        /// </summary>
        internal bool DeclaresOperator(string key, string? typedKey)
            => _typedOperators.TryGetValue(key, out var typed) ? typedKey is not null && typed.Contains(typedKey) : Declares(key);

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
    /// subscript's parameters seldom carry argument labels. Null when the signature's types do not
    /// read (a generic subscript among them): two such subscripts are not known to be one.
    /// </summary>
    internal static string? SubscriptKey(OverloadSignature? signature, bool isStatic)
        => TypesKey(signature) is { } types ? (isStatic ? "static " : string.Empty) + "subscript" + types : null;

    /// <summary>An operator's requirement key with its parameter and return types (see <see cref="SubscriptKey"/>).</summary>
    internal static string? OperatorKey(string name, OverloadSignature? signature, bool isStatic)
        => TypesKey(signature) is { } types ? (isStatic ? "static " : string.Empty) + name + types : null;

    private static string? TypesKey(OverloadSignature? signature)
    {
        static string Type(string? type) => SelfQualifier.Replace(ForeignOverloadPairing.Normalize(type) ?? "?", string.Empty);
        return signature?.Parameters is not { } parameters || signature.Return is null
            ? null
            : $"({string.Join(",", parameters.Select(Type))})->{Type(signature.Return)}";
    }

    private static readonly Regex SelfQualifier = new(@"\bSelf\.", RegexOptions.Compiled);

    /// <summary>
    /// The parameter and return types of a method or initializer (an initializer's return is not
    /// compared), or null when one of them is not a concrete type both sides spell alike: a generic
    /// parameter, <c>Self</c> or a type named through it, an associated type (a name without a module),
    /// or a type that does not read. Requirement and member are then told apart by their labels.
    /// </summary>
    internal static string? MethodTypesKey(OverloadSignature? signature)
    {
        if (signature?.Parameters is not { } parameters)
            return null;
        var types = signature.Return is null ? parameters : parameters.Append(signature.Return);
        if (!types.All(IsConcrete))
            return null;
        static string Type(string type) => ForeignOverloadPairing.Normalize(type) ?? "?";
        return $"({string.Join(",", parameters.Select(Type))})->{(signature.Return is null ? "init" : Type(signature.Return))}";
    }

    private static bool IsConcrete(string type)
    {
        TypeSpec? parsed;
        try
        {
            parsed = TypeSpecParser.Parse(Ownership.Replace(SwiftModuleSelectors.ToDotted(type), " ").Trim());
        }
        catch (Exception)
        {
            return false;
        }
        return parsed is not null && IsConcrete(parsed);
    }

    private static bool IsConcrete(TypeSpec type) => type switch
    {
        NamedTypeSpec named => (named.Name.Contains('.') ? !named.Name.StartsWith("Self.", StringComparison.Ordinal)
                : named.Name is "Any" or "AnyObject" or "Void")
            && !named.Name.Contains('τ')
            && named.GenericParameters.All(IsConcrete),
        TupleTypeSpec tuple => tuple.Elements.All(IsConcrete),
        ClosureTypeSpec closure => IsConcrete(closure.Arguments) && IsConcrete(closure.ReturnType),
        _ => false,
    };

    private static readonly Regex Ownership = new(@"(?:@\w+(?:\([^)]*\))?|\b(?:inout|__owned|__shared|borrowing|consuming|sending)\b)", RegexOptions.Compiled);

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
        // The module, then the protocol's path in it: a protocol nested in a type (`M.Outer.P`) is
        // another protocol than a top-level one of its leaf name (`M.P`).
        var dot = plain.IndexOf('.');
        var path = dot < 0 ? plain : plain[(dot + 1)..];
        var module = dot < 0 ? null : plain[..dot];
        if (LanguageConstraints.Contains(path) && (module is null or "Swift"))
            return true;
        if (!visited.Add(module is null ? plain : $"{module}.{path}"))
            return true;

        var candidates = module is not null
            ? new[] { module }
            : ImplicitModules.Concat(moduleDecl.Dependencies ?? new List<string>()).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var candidate in candidates)
        {
            if (Lookup(moduleDecl, candidate, path) is not { } declaration)
                continue;
            foreach (var key in declaration.Names)
                names.Add(key);
            foreach (var (key, typed) in declaration.TypedOperators ?? Array.Empty<(string, string)>())
                names.AddTypedOperator(key, typed);
            foreach (var (key, typed) in declaration.TypedMethods ?? Array.Empty<(string, string?)>())
                names.AddTypedMethod(key, typed);
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

        /// <summary>The method and initializer requirements: key, and types (<see cref="MethodTypesKey"/>) or null.</summary>
        public IReadOnlyList<(string Key, string? Typed)>? TypedMethods { get; init; }

        /// <summary>
        /// Every requirement the declaration reads from an interface, with its declaration line and
        /// the attribute lines above it; null for a protocol a dependency database describes or a
        /// <c>typealias</c> composition.
        /// </summary>
        public IReadOnlyList<RequirementLine>? RequirementLines { get; init; }

        /// <summary>The requirement keys an unconstrained extension of the protocol in the same interface defaults.</summary>
        public IReadOnlySet<string>? DefaultKeys { get; init; }

        /// <summary>Whether the declaration names an associated type.</summary>
        public bool HasAssociatedTypes { get; init; }
    }

    private enum RequirementKind { Method, Initializer, Property, Subscript }

    /// <summary>A requirement as the interface declares it: its kind, key, line and the attribute lines above it.</summary>
    private sealed record RequirementLine(RequirementKind Kind, string Key, string Line, IReadOnlyList<string> Attributes, bool IsStatic);

    /// <summary>
    /// The declaration of the protocol at <paramref name="path"/> in <paramref name="module"/>: its name,
    /// after the names of the types it is nested in (<c>Outer.P</c>), if any.
    /// </summary>
    private static Declaration? Lookup(ModuleDecl moduleDecl, string module, string path)
    {
        if (moduleDecl.DependencyProtocols.TryGetValue(module, out var protocols)
            && protocols.FirstOrDefault(p => string.Equals(
                p.SwiftTypeName.ModuleQualifiedName, $"{module}.{path}", StringComparison.Ordinal)) is { } bound)
        {
            var memberNames = bound.Methods.Where(m => !m.IsAccessor)
                .Select(ProtocolExtensionEmitter.RequirementKeyOf)
                .Concat(bound.Properties.Select(ProtocolExtensionEmitter.RequirementKeyOf))
                .Concat(bound.Subscripts.Select(ProtocolExtensionEmitter.RequirementKeyOf))
                .OfType<string>()
                .ToList();
            return new Declaration(module, memberNames, bound.InheritedProtocols.Select(i => i.Name).ToList())
            {
                TypedMethods = bound.Methods.Where(m => !m.IsAccessor)
                    .Select(m => (ProtocolExtensionEmitter.RequirementKeyOf(m)!, ProtocolExtensionEmitter.RequirementTypesKeyOf(m)))
                    .ToList(),
            };
        }
        return LookupInInterfaces(module, path, new HashSet<string>(StringComparer.Ordinal));
    }

    // The module's interface in each SDK of the run; a protocol a module re-exports from another
    // (`@_exported import M`) is followed there.
    private static Declaration? LookupInInterfaces(string module, string path, HashSet<string> visitedModules)
    {
        if (!visitedModules.Add(module) || SdkPublicModuleNames.Current is not { } sdk)
            return null;
        foreach (var root in sdk.SdkRoots)
        {
            if (InterfaceText(root, module) is not { } text)
                continue;
            if (Declarations.GetOrAdd($"{root}\n{module}\n{path}", _ => Parse(text, module, path)) is { } found)
                return found with { Module = module };
            foreach (Match exported in ExportedImport.Matches(text))
            {
                // A selective re-export (`@_exported import protocol M.P`) brings in that one name.
                if (exported.Groups["name"].Success && exported.Groups["name"].Value != path)
                    continue;
                if (LookupInInterfaces(exported.Groups["module"].Value, path, visitedModules) is { } reexported)
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

    // Every declaration of the protocol at `path` is read (an interface prints one per `#if` arm),
    // or the protocols a same-named `typealias` composes. A declaration nested in other types than
    // the path names is another protocol of the same leaf name.
    private static Declaration? Parse(string text, string module, string path)
    {
        var leafAt = path.LastIndexOf('.');
        var protocolName = path[(leafAt + 1)..];
        var enclosing = leafAt < 0 ? string.Empty : path[..leafAt];
        var names = new List<string>();
        var typedOperators = new List<(string, string)>();
        var typedMethods = new List<(string, string?)>();
        var inherited = new List<string>();
        var requirementLines = new List<RequirementLine>();
        var hasAssociatedTypes = false;
        var found = false;
        var searchFrom = 0;
        var headerPattern = new Regex($@"\bpublic\s+protocol\s+{Regex.Escape(protocolName)}\b[^{{]*\{{");
        while (searchFrom < text.Length && headerPattern.Match(text, searchFrom) is { Success: true } at
            && UnderscoreProtocolSynthesizer.TryExtractProtocolBlock(text[at.Index..], protocolName, out var header, out var body, out var end))
        {
            searchFrom = at.Index + end;
            if (!string.Equals(EnclosingTypes(text, at.Index, module), enclosing, StringComparison.Ordinal))
                continue;
            found = true;
            inherited.AddRange(InheritanceClause(header));
            // The attribute lines above a declaration belong to it.
            var attributes = new List<string>();
            foreach (var line in body.Split('\n'))
            {
                var isStatic = StaticMember.IsMatch(line);
                if (FuncName.Match(line) is { Success: true } func)
                {
                    var labels = ParameterLabels(line, func.Index + func.Length, out var clauseEnd);
                    var name = func.Groups[1].Value.Trim('`');
                    var key = MethodKey(name, labels, isStatic, labels is not null && AsyncEffect.IsMatch(line[clauseEnd..]));
                    names.Add(key);
                    requirementLines.Add(new RequirementLine(RequirementKind.Method, key, line, attributes, isStatic));
                    if (IsOperator(name) && OperatorKey(name, ForeignOverloadPairing.SignatureOf(line, "func", name), isStatic) is { } typed)
                        typedOperators.Add((key, typed));
                    else if (!IsOperator(name))
                        typedMethods.Add((key, labels is null ? null : MethodTypesKey(ForeignOverloadPairing.SignatureOf(line, "func", name))));
                }
                else if (VarName.Match(line) is { Success: true } variable)
                {
                    var key = PropertyKey(variable.Groups[1].Value.Trim('`'), isStatic);
                    names.Add(key);
                    requirementLines.Add(new RequirementLine(RequirementKind.Property, key, line, attributes, isStatic));
                }
                else if (SubscriptDeclaration.IsMatch(line))
                {
                    var isStaticSubscript = StaticSubscript.IsMatch(line);
                    var subscriptKey = SubscriptKey(ForeignOverloadPairing.SignatureOf(line, "subscript"), isStaticSubscript);
                    if (subscriptKey is not null)
                        names.Add(subscriptKey);
                    requirementLines.Add(new RequirementLine(RequirementKind.Subscript, subscriptKey ?? "subscript(*)", line, attributes, isStaticSubscript));
                }
                else if (Initializer.Match(line) is { Success: true } init)
                {
                    var labels = ParameterLabels(line, init.Index + init.Length - 1, out var clauseEnd);
                    var key = MethodKey("init", labels, isStatic: false,
                        labels is not null && AsyncEffect.IsMatch(line[clauseEnd..]));
                    names.Add(key);
                    requirementLines.Add(new RequirementLine(RequirementKind.Initializer, key, line, attributes, false));
                    typedMethods.Add((key, labels is null ? null : MethodTypesKey(ForeignOverloadPairing.SignatureOf(line, "init"))));
                }
                else
                {
                    var trimmed = line.Trim();
                    if (AssociatedType.IsMatch(trimmed))
                        hasAssociatedTypes = true;
                    if (trimmed.StartsWith('@'))
                        attributes.Add(trimmed);
                    else if (trimmed.Length > 0 && !trimmed.StartsWith("//", StringComparison.Ordinal))
                        attributes = new List<string>();
                    continue;
                }
                attributes = new List<string>();
            }
        }
        if (found)
            return new Declaration(string.Empty, names.Distinct(StringComparer.Ordinal).ToList(), inherited.Distinct(StringComparer.Ordinal).ToList())
            {
                TypedOperators = typedOperators,
                TypedMethods = typedMethods,
                RequirementLines = requirementLines,
                DefaultKeys = enclosing.Length == 0 ? ExtensionDefaultKeys(text, module, protocolName) : new HashSet<string>(StringComparer.Ordinal),
                HasAssociatedTypes = hasAssociatedTypes,
            };

        var alias = new Regex($@"\bpublic\s+typealias\s+{Regex.Escape(protocolName)}\s*=\s*([^\n]+)").Matches(text)
            .FirstOrDefault(m => string.Equals(EnclosingTypes(text, m.Index, module), enclosing, StringComparison.Ordinal));
        if (alias is null || !alias.Groups[1].Value.Contains('&'))
            return null;
        return new Declaration(string.Empty, Array.Empty<string>(),
            alias.Groups[1].Value.Split('&').Select(p => p.Trim()).Where(p => p.Length > 0).ToList());
    }

    /// <summary>
    /// The member keys the unconstrained extensions of the top-level protocol
    /// <paramref name="protocolName"/> in <paramref name="text"/> declare: the requirements those
    /// members default for every conformer. An extension with a <c>where</c> clause defaults them for
    /// some conformers only, so its members are not counted.
    /// </summary>
    private static HashSet<string> ExtensionDefaultKeys(string text, string module, string protocolName)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var header = new Regex($@"\bextension\s+(?:{Regex.Escape(module)}\.)?{Regex.Escape(protocolName)}(?![\w.])([^{{]*)\{{");
        foreach (Match extension in header.Matches(text))
        {
            if (Regex.IsMatch(extension.Groups[1].Value, @"\bwhere\b")
                || !string.IsNullOrEmpty(EnclosingTypes(text, extension.Index, module)))
                continue;
            var bodyStart = extension.Index + extension.Length;
            var bodyEnd = ClosingBrace(text, bodyStart);
            // Only the extension's own members, not those of a type or body nested in it.
            foreach (var line in DepthZeroLines(text, bodyStart, bodyEnd))
            {
                var isStatic = StaticMember.IsMatch(line);
                if (FuncName.Match(line) is { Success: true } func)
                {
                    var labels = ParameterLabels(line, func.Index + func.Length, out var clauseEnd);
                    keys.Add(MethodKey(func.Groups[1].Value.Trim('`'), labels, isStatic,
                        labels is not null && AsyncEffect.IsMatch(line[clauseEnd..])));
                }
                else if (VarName.Match(line) is { Success: true } variable)
                    keys.Add(PropertyKey(variable.Groups[1].Value.Trim('`'), isStatic));
            }
        }
        return keys;
    }

    // The lines of text[from..to] that begin outside every brace opened after `from`; braces in
    // comments and string literals do not count.
    private static IEnumerable<string> DepthZeroLines(string text, int from, int to)
    {
        var depth = 0;
        var lineStart = from;
        var startsAtZero = true;
        for (var i = from; i < to; i++)
        {
            if (text[i] == '/' && i + 1 < to && text[i + 1] == '/')
            {
                while (i + 1 < to && text[i + 1] != '\n')
                    i++;
                continue;
            }
            if (SwiftParameterClause.IsStringLiteralStart(text, i))
            {
                i = Math.Min(SwiftParameterClause.SkipStringLiteral(text, i), to) - 1;
                continue;
            }
            switch (text[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case '\n':
                    if (startsAtZero)
                        yield return text[lineStart..i];
                    lineStart = i + 1;
                    startsAtZero = depth == 0;
                    break;
            }
        }
        if (startsAtZero && lineStart < to)
            yield return text[lineStart..to];
    }

    // The index of the `}` that closes a body opening just before `from` (the text's length when it
    // does not close); braces in comments and string literals do not count.
    private static int ClosingBrace(string text, int from)
    {
        var depth = 1;
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }
            if (SwiftParameterClause.IsStringLiteralStart(text, i))
            {
                i = SwiftParameterClause.SkipStringLiteral(text, i) - 1;
                continue;
            }
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return i;
        }
        return text.Length;
    }

    /// <summary>
    /// The names of the types a declaration at <paramref name="index"/> of an interface of
    /// <paramref name="module"/> is nested in, outermost first and joined by dots (<c>Outer.Inner</c>),
    /// or "" at the top level: each brace a type or extension declaration opens names its type, and a
    /// declaration inside any other body is nested in no type (<c>?</c>).
    /// </summary>
    private static string EnclosingTypes(string text, int index, string module)
    {
        var scopes = new List<string?>();
        var segmentStart = 0;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '/' && i + 1 < index && text[i + 1] == '/')
            {
                while (i < index && text[i] != '\n')
                    i++;
                continue;
            }
            if (SwiftParameterClause.IsStringLiteralStart(text, i))
            {
                i = SwiftParameterClause.SkipStringLiteral(text, i) - 1;
                continue;
            }
            if (text[i] == '{')
            {
                var opened = TypeDeclaration.Match(text, segmentStart, i - segmentStart);
                scopes.Add(opened.Success ? opened.Groups[1].Value : null);
                segmentStart = i + 1;
            }
            else if (text[i] == '}')
            {
                if (scopes.Count > 0)
                    scopes.RemoveAt(scopes.Count - 1);
                segmentStart = i + 1;
            }
        }
        if (scopes.Any(scope => scope is null))
            return "?";
        return string.Join(".", scopes.Select(scope =>
            scope!.StartsWith(module + ".", StringComparison.Ordinal) ? scope[(module.Length + 1)..] : scope));
    }

    // The type a brace opens: the last type or extension declaration before it (right to left).
    private static readonly Regex TypeDeclaration = new(
        @"\b(?:struct|class|enum|actor|extension)\s+(?!(?:func|var|let|subscript|init|deinit|typealias)\b)([\p{L}_][\w.]*)",
        RegexOptions.Compiled | RegexOptions.RightToLeft);

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
            if (SwiftParameterClause.IsStringLiteralStart(line, i))
            {
                i = SwiftParameterClause.SkipStringLiteral(line, i) - 1;
                continue;
            }
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
            if (SwiftParameterClause.IsStringLiteralStart(clause, i))
            {
                i = SwiftParameterClause.SkipStringLiteral(clause, i) - 1;
                continue;
            }
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
            if (SwiftParameterClause.IsStringLiteralStart(parameter, i))
            {
                i = SwiftParameterClause.SkipStringLiteral(parameter, i) - 1;
                continue;
            }
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
    private static readonly Regex AssociatedType = new(@"^(?:@\S+\s+)*associatedtype\b", RegexOptions.Compiled);
    private static readonly Regex AsyncEffect = new(@"^\s*(?:re)?async\b", RegexOptions.Compiled);
    private static readonly Regex ExportedImport = new(@"(?m)^\s*@_exported\s+(?:(?:public|package|internal)\s+)?import\s+(?:(?:struct|class|enum|protocol|typealias|func|var|let)\s+(?<module>\w+)\.(?:\w+\.)*(?<name>\w+)|(?<module>\w+))", RegexOptions.Compiled);
}
