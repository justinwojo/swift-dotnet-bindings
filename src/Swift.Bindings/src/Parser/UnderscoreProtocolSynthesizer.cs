// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Synthesizes <see cref="ProtocolDecl"/> entries for underscore-prefixed public
/// protocols that swift-api-digester drops from its ABI JSON output.
///
/// <para>
/// swift-api-digester (<c>-dump-sdk -abi</c>) does not emit Protocol decl nodes
/// for underscore-prefixed protocols even when they are declared <c>public</c>
/// in the framework's swiftinterface. Conformance lists, mangled-name fragments,
/// and conformance records still reference these protocols, so types constrained
/// by them (e.g. <c>AppIntents.IntentParameter&lt;Value&gt;</c> where
/// <c>Value : _IntentValue</c>) reach the emitter without a TypeRecord for the
/// constraint, and <see cref="StringEmitter.PInvokeHelperEmitter"/> records
/// "protocol not projected in the type database" and tombstones every dependent
/// type with <c>IndeterminatePwtShape</c>.
/// </para>
///
/// <para>
/// For an allowlisted set of (module, protocol-name) pairs, this synthesizer
/// parses the protocol declaration directly from the swiftinterface and injects
/// a minimal <see cref="ProtocolDecl"/> with the same associated types, Self
/// requirement, inherited protocols, and mangled name a parsed-from-ABI decl
/// would carry. <see cref="ModuleProcessor.RegisterProtocolType"/> then produces
/// a TypeRecord with a valid <c>ProtocolDescriptorSymbol</c>, and
/// PInvokeHelperEmitter's PAT/Self-requirement branch (runtime descriptor
/// lookup) succeeds.
/// </para>
///
/// <para>
/// Scope: top-level nominal protocols only. The mangled-name rule
/// <c>$s{len}{Module}{len}{Name}P</c> does not generalize to nested or
/// extension-context protocols. The allowlist is intentionally narrow — the
/// digester's omission is module-specific, and we don't want to take ownership
/// of every underscored protocol in the SDK without an emitter use case.
/// </para>
/// </summary>
internal static class UnderscoreProtocolSynthesizer
{
    /// <summary>
    /// Per-module allowlist of underscored protocol names that require synthesis.
    /// Each entry must be a top-level <c>public protocol _Name</c> in the named
    /// module's swiftinterface.
    /// </summary>
    private static readonly Dictionary<string, string[]> s_allowlist = new(StringComparer.Ordinal)
    {
        ["AppIntents"] = new[]
        {
            "_IntentValue",
            "_ParameterSummarySwitchCase",
        },
    };

    /// <summary>
    /// Synthesizes ProtocolDecl entries for any allowlisted underscored protocol
    /// in <paramref name="moduleName"/> that is missing from <paramref name="moduleDecl"/>.
    /// </summary>
    /// <returns>
    /// Module-qualified names of synthesized protocols. Callers fold this set into the
    /// underscore-suppression set (and emission context) so the empty synthesized decl
    /// never surfaces as a public <c>I_IntentValue</c> C# interface — RegisterProtocolType
    /// still produces the TypeRecord we need for PWT resolution. These names must NOT be
    /// added to <c>ModuleDecl.InternalTypeNames</c>: the protocols are <c>public</c> in
    /// Swift (only digester-stripped), so generated wrappers may legally reference them and
    /// the Pattern-2 member-reach gate must not suppress members whose generic constraints
    /// name them. See the suppression-set merge in <c>Program.cs</c>.
    /// </returns>
    public static HashSet<string> Synthesize(
        string moduleName,
        string? swiftInterfacePath,
        ModuleDecl moduleDecl,
        Dictionary<NamedTypeSpec, TypeDecl> moduleTypes,
        ITypeDatabase typeDatabase,
        ILogger logger)
    {
        var synthesized = new HashSet<string>(StringComparer.Ordinal);

        if (!s_allowlist.TryGetValue(moduleName, out var names))
            return synthesized;

        if (string.IsNullOrWhiteSpace(swiftInterfacePath) || !File.Exists(swiftInterfacePath))
        {
            logger.LogDebug(
                "UnderscoreProtocolSynthesizer: '{Module}' is allowlisted but no swiftinterface available ('{Path}'); skipping.",
                moduleName, swiftInterfacePath ?? "<null>");
            return synthesized;
        }

        var existing = new HashSet<string>(moduleDecl.Protocols.Select(p => p.Name), StringComparer.Ordinal);
        var source = File.ReadAllText(swiftInterfacePath);

        foreach (var name in names)
        {
            if (existing.Contains(name))
                continue; // ABI JSON already exposes it (no synthesis needed).

            if (!TryExtractProtocolBlock(source, name, out var header, out var body))
            {
                logger.LogWarning(
                    "UnderscoreProtocolSynthesizer: declaration of '{Module}.{Name}' not found in swiftinterface; skipping.",
                    moduleName, name);
                continue;
            }

            var inherited = ParseInheritedProtocols(header);
            var associatedTypes = ParseAssociatedTypes(body);
            var hasSelfRequirement = DetectSelfRequirement(body);
            var mangledName = BuildTopLevelProtocolMangledName(moduleName, name);

            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}");
            var decl = new ProtocolDecl
            {
                Name = name,
                SwiftTypeName = swiftTypeName,
                MangledName = mangledName,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                AssociatedTypes = associatedTypes,
                HasSelfRequirement = hasSelfRequirement,
                InheritedProtocols = inherited,
                GenericSignature = null,
                IsClassBound = false,
                ParentDecl = moduleDecl,
                ModuleDecl = moduleDecl,
                // IsModuleInternal suppresses C# interface emission (the synthesized decl
                // has no members; we only need the TypeRecord for PWT resolution).
                // RegisterProtocolType does NOT check IsModuleInternal — only IsSpiProtected
                // gates registration, so the TypeRecord still lands in the type database.
                IsModuleInternal = true,
                IsSpiProtected = false,
            };

            moduleDecl.Protocols.Add(decl);
            moduleDecl.Types.Add(decl);
            moduleTypes.TryAdd(new NamedTypeSpec(swiftTypeName.ModuleQualifiedName), decl);
            synthesized.Add(swiftTypeName.ToString());

            logger.LogInformation(
                "UnderscoreProtocolSynthesizer: synthesized '{Qualified}' (mangled '{Mangled}', AssociatedTypes={ATs}, HasSelfRequirement={Self}).",
                swiftTypeName, mangledName, associatedTypes.Count, hasSelfRequirement);

            // swift-api-digester strips the protocol decl AND its conformance records
            // together, so the conformers we just unblocked still have no evidence that
            // they satisfy the constraint. Re-attach the stripped conformance records the
            // same pass synthesizes the protocol.
            IngestStrippedConformances(source, moduleName, swiftTypeName, moduleDecl, typeDatabase, logger);
        }

        return synthesized;
    }

    /// <summary>
    /// Folds the underscore-suppression set into the Pattern-2 internal-type-name set
    /// (consumed by the wrapper post-processor and <c>MemberValidationPipeline</c>'s
    /// member-reach gate), <b>excluding</b> the synthesized public-underscore protocols.
    ///
    /// <para>
    /// Synthesized protocols (e.g. <c>AppIntents._IntentValue</c>) are <c>public</c> in
    /// Swift — swift-api-digester only strips them from the ABI JSON — so a generated
    /// <c>@_cdecl</c> wrapper may legally name them. They belong in the underscore-suppression
    /// set (so the empty synthesized decl never surfaces as a public C# interface) but must
    /// NOT enter the internal-type set, or the member-reach gate would suppress every member
    /// whose generic constraint mentions them. A genuinely module-internal underscore type
    /// (not synthesized) still flows through and suppresses correctly. This decoupling is the
    /// fix for the init-suppression cascade that otherwise drops the bulk of the surface.
    /// </para>
    /// </summary>
    /// <returns>
    /// The merged set. A new set is allocated only when <paramref name="internalTypeNames"/>
    /// is null <i>and</i> at least one genuine (non-synthesized) name is added; otherwise the
    /// input set is returned mutated in place so callers can re-sync <c>decl.InternalTypeNames</c>.
    /// When every suppressed name is a synthesized protocol there is nothing to add, so the
    /// original input is returned untouched (including null) — a module whose only underscore
    /// names are the synthesized protocols keeps its prior <c>InternalTypeNames</c> identity.
    /// </returns>
    public static HashSet<string>? MergeSuppressedIntoInternalTypeNames(
        HashSet<string>? internalTypeNames,
        IReadOnlyCollection<string> underscoreSuppressedNames,
        IReadOnlySet<string> synthesizedUnderscoreNames)
    {
        if (underscoreSuppressedNames.Count == 0)
            return internalTypeNames;

        var merged = internalTypeNames;
        foreach (var name in underscoreSuppressedNames)
        {
            if (synthesizedUnderscoreNames.Contains(name))
                continue;
            merged ??= new HashSet<string>();
            merged.Add(name);
        }
        return merged;
    }

    /// <summary>
    /// Re-attaches the protocol-conformance records that swift-api-digester strips from the
    /// ABI JSON alongside an underscored protocol decl, so that bound-generic constraint
    /// checks (<see cref="BoundGenericsHandler"/>'s <c>HasConformance</c>) and conformer
    /// indexing (<see cref="ConcreteSpecializationEngine"/>) can see the conformance.
    ///
    /// <para>
    /// Only <b>unconditional</b> extensions are ingested. Conditional conformances
    /// (<c>extension X : _P where ...</c>, e.g. <c>Array : _IntentValue where Element : _IntentValue</c>)
    /// can't be attached unconditionally — the constraint check must keep failing for element
    /// types that don't themselves conform — so they are skipped. Unconditional conformers are
    /// split by where the satisfaction fact must live:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Local</b> nominal conformers (a <see cref="TypeDecl"/> in
    ///   <paramref name="moduleDecl"/>), <i>reference- OR value-typed</i>: the conformance is
    ///   appended to the local decl's <c>Conformances</c> list. With the relaxed
    ///   <c>GenericTypeEmitter.GetWhereClause</c> seed (descriptor-path-safe PATs drop
    ///   <c>ISwiftObject</c>), a closed <c>IntentParameter&lt;FrozenStruct&gt;</c> now type-checks,
    ///   so frozen value-typed conformers are no longer excluded. <c>HasConformance</c> then
    ///   sees the fact, and it persists across modules via <c>TypeRecord.ProtocolConformances</c>.</item>
    ///   <item><b>Foreign</b> conformers (<c>Swift.Int</c>, <c>Foundation.Date</c>, …) have no
    ///   local <see cref="TypeDecl"/> to carry a conformance. Their (concrete, protocol) fact is
    ///   registered on <paramref name="typeDatabase"/> via
    ///   <see cref="ITypeDatabase.RegisterStrippedConformance"/>;
    ///   <c>BoundGenericsHandler.SatisfiesConstraint</c> consults it in the
    ///   <c>typeArgumentDecl == null</c> branch. (This table is in-memory and scoped to the
    ///   current generator run — sufficient because the decision to emit the closed binding is
    ///   made during this run; a different module that later loads this module's database and
    ///   re-validates the same closed generic would fail closed, which is safe.)</item>
    /// </list>
    ///
    /// <para>
    /// Local conformances are attached with an <b>empty</b> descriptor symbol. The synthesized
    /// protocol is module-internal and PAT/Self-shaped, so every runtime-conformance emission
    /// path (the conformance dictionary, the single-PAT <c>typeof(object)</c> entry, and
    /// NativeAOT factory registration) skips empty-descriptor / PAT / Self-requirement entries
    /// by <i>protocol</i> flag — independent of whether the conformer is a class or a frozen
    /// struct — so the record exists purely as a type-database fact for constraint satisfaction
    /// and never surfaces as generated runtime code or a bogus <c>: I_IntentValue</c> interface.
    /// </para>
    /// </summary>
    private static void IngestStrippedConformances(
        string source,
        string moduleName,
        SwiftTypeName protocolName,
        ModuleDecl moduleDecl,
        ITypeDatabase typeDatabase,
        ILogger logger)
    {
        // Lookup of local nominal types by module-qualified name (e.g. "AppIntents.IntentFile").
        // Nested types live under their parent's Types list, so flatten recursively — a nested
        // conformer (extension Outer.Inner : _P) must be reachable just like a top-level one.
        var localTypes = new Dictionary<string, TypeDecl>(StringComparer.Ordinal);
        IndexLocalNominals(moduleDecl.Types, localTypes);

        foreach (var (conformingTypeName, isConditional) in EnumerateExtensionConformers(source, protocolName, moduleName))
        {
            if (isConditional)
                continue;

            // swiftinterface extension headers may write the extended type either fully
            // qualified (AppIntents.IntentFile) or bare (IntentFile), and a nested conformer
            // appears dotted-but-unqualified (IntentParameter.DateKind). Try the name as-is and
            // module-prefixed so all three forms resolve against the module-qualified index.
            if (!TryResolveLocalConformer(conformingTypeName, moduleName, localTypes, out var decl))
            {
                // Foreign / stdlib conformer (Swift.Int, Foundation.Date, …): no local TypeDecl
                // to carry the conformance, so record the (concrete, protocol) fact on the type
                // database. SatisfiesConstraint's typeArgumentDecl == null branch consults it.
                // Names in the swiftinterface extension header are already module-qualified.
                var foreignType = SwiftTypeName.FromModuleQualifiedName(conformingTypeName);
                typeDatabase.RegisterStrippedConformance(foreignType, protocolName);
                logger.LogInformation(
                    "UnderscoreProtocolSynthesizer: registered foreign stripped conformance '{Type} : {Protocol}'.",
                    foreignType, protocolName);
                continue;
            }

            // Local conformer (reference- OR value-typed). Value types are no longer excluded:
            // the relaxed GenericTypeEmitter seed lets a closed IntentParameter<FrozenStruct>
            // compile, so satisfying the constraint now enables a usable binding.
            var conformanceList = GetConformanceList(decl);
            if (conformanceList == null)
                continue;
            if (conformanceList.Any(c => c.Protocol == protocolName))
                continue; // already attached (idempotent across allowlist entries).

            conformanceList.Add(new TypeConformance(decl.SwiftTypeName, protocolName, ProtocolConformanceDescriptor: string.Empty));
            logger.LogInformation(
                "UnderscoreProtocolSynthesizer: attached stripped conformance '{Type} : {Protocol}'.",
                decl.SwiftTypeName, protocolName);
        }
    }

    private static readonly Regex s_extensionHeaderRegex = new(
        @"(?m)^\s*(?:@[\w.]+(?:\s*\([^)]*\))?\s+)*extension\s+([\w.]+)\s*:\s*([^{]+?)\s*\{",
        RegexOptions.Compiled);

    /// <summary>
    /// Enumerates <c>extension &lt;Type&gt; : &lt;conformance-list&gt; [where …] { … }</c>
    /// headers in the swiftinterface whose conformance list names
    /// <paramref name="protocolName"/>, yielding the extended type's name and whether the
    /// extension carries a <c>where</c> clause (a conditional conformance). The match against
    /// the protocol accepts both the module-qualified form (<c>AppIntents._IntentValue</c>)
    /// and the bare form (<c>_IntentValue</c>), and only at top-level (angle-bracket depth
    /// zero) so an inner generic argument can never be mistaken for the conformance.
    /// </summary>
    private static IEnumerable<(string conformingType, bool isConditional)> EnumerateExtensionConformers(
        string source, SwiftTypeName protocolName, string moduleName)
    {
        var qualifiedTarget = protocolName.ModuleQualifiedName;
        var bareTarget = protocolName.Name;

        foreach (Match m in s_extensionHeaderRegex.Matches(source))
        {
            var conformingType = m.Groups[1].Value.Trim();
            var clause = m.Groups[2].Value;

            var whereIdx = clause.IndexOf(" where ", StringComparison.Ordinal);
            var isConditional = whereIdx >= 0;
            var conformanceListText = isConditional ? clause[..whereIdx] : clause;

            var names = SwiftTypeListText.SplitTopLevelCommas(conformanceListText);
            var conformsToTarget = names.Any(n =>
            {
                var t = n.Trim();
                return t == qualifiedTarget || t == bareTarget;
            });

            if (conformsToTarget)
                yield return (conformingType, isConditional);
        }
    }

    /// <summary>
    /// Recursively indexes every local nominal type (and nested type) by its module-qualified
    /// name, so conformer resolution sees nested types as well as top-level ones.
    /// </summary>
    private static void IndexLocalNominals(IEnumerable<TypeDecl> types, Dictionary<string, TypeDecl> index)
    {
        foreach (var t in types)
        {
            if (t is StructDecl or ClassDecl or EnumDecl)
                index[t.SwiftTypeName.ModuleQualifiedName] = t;
            IndexLocalNominals(t.Types, index);
        }
    }

    /// <summary>
    /// Resolves an extension's extended-type name against the module-qualified index, trying the
    /// name verbatim (already fully qualified) and module-prefixed (bare or nested-but-unqualified).
    /// </summary>
    private static bool TryResolveLocalConformer(
        string conformingTypeName, string moduleName, Dictionary<string, TypeDecl> localTypes, out TypeDecl decl)
    {
        if (localTypes.TryGetValue(conformingTypeName, out decl!))
            return true;
        return localTypes.TryGetValue($"{moduleName}.{conformingTypeName}", out decl!);
    }

    private static List<TypeConformance>? GetConformanceList(TypeDecl decl) => decl switch
    {
        StructDecl s => s.Conformances,
        ClassDecl c => c.Conformances,
        EnumDecl e => e.Conformances,
        _ => null,
    };

    /// <summary>
    /// Computes the Swift-mangled protocol-type symbol for a top-level protocol
    /// at <c>{module}.{name}</c>. The form is <c>$s{moduleLen}{module}{nameLen}{name}P</c>
    /// where length is the byte length (ASCII names use char length). The
    /// underscore prefix on <paramref name="protocolName"/> counts toward the
    /// length (e.g. <c>_IntentValue</c> is 12 bytes).
    /// </summary>
    private static string BuildTopLevelProtocolMangledName(string moduleName, string protocolName)
    {
        var moduleBytes = System.Text.Encoding.UTF8.GetByteCount(moduleName);
        var nameBytes = System.Text.Encoding.UTF8.GetByteCount(protocolName);
        return $"$s{moduleBytes}{moduleName}{nameBytes}{protocolName}P";
    }

    /// <summary>
    /// Locates a <c>public protocol _Name [ : Bases ] { ... }</c> declaration in
    /// the swiftinterface, returning its header text (the segment from the
    /// <c>public</c> keyword through the opening brace) and the body text
    /// (everything between the matched braces). Same-line attributes
    /// (<c>@_alwaysEmitConformanceMetadata public protocol …</c>) and
    /// preceding-line attribute stacks are both accepted. Returns false if the
    /// declaration is not present or its braces are unbalanced.
    /// </summary>
    private static bool TryExtractProtocolBlock(string source, string protocolName, out string header, out string body)
    {
        header = string.Empty;
        body = string.Empty;

        // \bpublic\s+protocol\s+Name matches both
        //   public protocol Name {                                (no attributes)
        //   @attr public protocol Name {                          (same-line attribute)
        //   @attrA\n@attrB\npublic protocol Name {                (multi-line attribute stack)
        // because the word-boundary anchor lets `public` start at column ≥ 1.
        var pattern = new Regex(
            $@"\bpublic\s+protocol\s+{Regex.Escape(protocolName)}\b[^{{]*\{{",
            RegexOptions.Compiled);

        var match = pattern.Match(source);
        if (!match.Success)
            return false;

        header = match.Value;
        var bodyStart = match.Index + match.Length;
        var depth = 1;
        var i = bodyStart;
        // Skip braces inside line comments, block comments, and string literals.
        // Swift protocol bodies rarely contain implementations, but attribute
        // strings like @available(*, deprecated, message: "use Foo {}") and
        // doc-comment fragments can carry braces that would unbalance a naive
        // counter.
        while (i < source.Length && depth > 0)
        {
            var c = source[i];
            // Line comment: skip to end of line.
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            // Block comment: skip to */ (no nesting support — Swift allows
            // nesting but swiftinterface output never produces them).
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i = Math.Min(source.Length, i + 2);
                continue;
            }
            // String literal: skip to closing quote, honoring \" escapes.
            if (c == '"')
            {
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < source.Length) i++;
                    i++;
                }
                if (i < source.Length) i++; // consume closing quote
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}') depth--;
            if (depth > 0) i++;
        }

        if (depth != 0)
            return false;

        body = source.Substring(bodyStart, i - bodyStart);
        return true;
    }

    /// <summary>
    /// Parses the inherited-protocols clause from a protocol header
    /// (<c>public protocol Foo : A, B.C, D.E.F</c>). Returns an empty list when
    /// the declaration has no inheritance clause.
    /// </summary>
    private static List<NamedTypeSpec> ParseInheritedProtocols(string header)
    {
        var result = new List<NamedTypeSpec>();
        var colonIdx = header.IndexOf(':');
        if (colonIdx < 0)
            return result;

        var braceIdx = header.IndexOf('{', colonIdx);
        var bound = braceIdx >= 0 ? braceIdx : header.Length;
        var inheritanceClause = header.Substring(colonIdx + 1, bound - colonIdx - 1);

        foreach (var raw in inheritanceClause.Split(','))
        {
            var name = raw.Trim();
            if (name.Length == 0)
                continue;
            result.Add(new NamedTypeSpec(name));
        }
        return result;
    }

    private static readonly Regex s_associatedTypeRegex = new(
        @"(?m)^\s*associatedtype\s+(\w+)(?:\s*:\s*([^=\n{]+?))?(?:\s*=\s*([^\n{]+?))?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses <c>associatedtype Name [: Bound] [= Default]</c> lines from a
    /// protocol body. <c>Bound</c> is preserved as a constraint string but not
    /// projected into a TypeSpec — protocol-shape consumers only need the count
    /// and the default for descriptor-keyed resolution.
    /// </summary>
    private static List<AssociatedTypeDecl> ParseAssociatedTypes(string body)
    {
        var result = new List<AssociatedTypeDecl>();
        foreach (Match m in s_associatedTypeRegex.Matches(body))
        {
            var name = m.Groups[1].Value;
            var bound = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
            var atDecl = new AssociatedTypeDecl { Name = name };
            if (!string.IsNullOrEmpty(bound))
                atDecl.Constraints.Add(bound);
            result.Add(atDecl);
        }
        return result;
    }

    /// <summary>
    /// Detects a Self requirement by scanning the protocol body for
    /// <c>Self.</c> or <c>Self ==</c>. Mirrors the conservative test used by
    /// <see cref="SwiftABIParser.CreateProtocolDecl"/> on a parsed GenericSig.
    /// </summary>
    private static bool DetectSelfRequirement(string body)
    {
        return body.Contains("Self.", StringComparison.Ordinal)
            || body.Contains("Self ==", StringComparison.Ordinal);
    }
}
