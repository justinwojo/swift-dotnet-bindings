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
    /// Module-qualified names of synthesized protocols. Callers should fold this
    /// set into the underscore-suppression set so the wrapper post-processor and
    /// MemberValidationPipeline treat them as internal — RegisterProtocolType
    /// produces the TypeRecord we need, but we do not want a public
    /// <c>I_IntentValue</c> C# interface emitted from the empty decl.
    /// </returns>
    public static HashSet<string> Synthesize(
        string moduleName,
        string? swiftInterfacePath,
        ModuleDecl moduleDecl,
        Dictionary<NamedTypeSpec, TypeDecl> moduleTypes,
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
        }

        return synthesized;
    }

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
