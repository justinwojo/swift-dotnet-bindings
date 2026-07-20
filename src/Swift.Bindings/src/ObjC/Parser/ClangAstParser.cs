// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Parses Clang AST JSON output into an ObjCModule model.
/// </summary>
public static class ClangAstParser
{
    /// <summary>
    /// The vocabulary of top-level Clang AST node kinds this parser recognizes — either parsed
    /// into the model (the eight handled <c>switch</c> cases) or knowingly skipped (forward decls,
    /// C/C++ scaffolding and builtins clang emits for system-header expansions). This is the
    /// in-code "golden" for the node-kind census (Finding 63): any top-level kind <em>not</em> in
    /// this set is surfaced via <c>SWIFTBIND029</c> so a future Clang schema change that introduces
    /// a new declaration kind can no longer silently drop bindable API. Update this set
    /// <em>deliberately</em> (together with the census golden test) when teaching the parser a new
    /// kind — the guard test <c>KnownTopLevelNodeKinds_CoversEveryHandledSwitchCase</c> fails if a
    /// parsed kind is missing here.
    /// </summary>
    internal static readonly IReadOnlySet<string> KnownTopLevelNodeKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        // Parsed into the model (must mirror the top-level switch in Parse):
        "ObjCInterfaceDecl", "ObjCProtocolDecl", "ObjCCategoryDecl",
        "EnumDecl", "RecordDecl", "FunctionDecl", "VarDecl", "TypedefDecl",

        // Seen at translation-unit scope and deliberately ignored (no bindable surface, or
        // implementation-only): C/ObjC scaffolding…
        "EmptyDecl", "StaticAssertDecl", "FileScopeAsmDecl", "IndirectFieldDecl",
        "ObjCImplementationDecl", "ObjCCategoryImplDecl", "ImportDecl",
        "PragmaCommentDecl", "PragmaDetectMismatchDecl",
        // …and C++ constructs that arrive via included system headers (we bind ObjC/C, not C++):
        "LinkageSpecDecl", "NamespaceDecl", "UsingDecl", "UsingDirectiveDecl",
        "UsingShadowDecl", "TypeAliasDecl", "TypeAliasTemplateDecl",
        "CXXRecordDecl", "ClassTemplateDecl", "ClassTemplateSpecializationDecl",
        "FunctionTemplateDecl", "VarTemplateDecl", "BuiltinTemplateDecl",
        "FriendDecl", "AccessSpecDecl", "NamespaceAliasDecl",
    };

    /// <summary>
    /// Per-parse cache of header file bytes, keyed by absolute path. Availability recovery
    /// (Finding 22, recovery option a2) reads the consumer header at each <c>AvailabilityAttr</c>'s
    /// source byte offset; a member-dense framework can carry hundreds of attributes in one header,
    /// so the bytes are read once per file and reused. Cleared in <see cref="Parse"/>'s finally so it
    /// never leaks across modules. <c>[ThreadStatic]</c> mirrors the single-threaded-per-parse
    /// assumption the existing <see cref="ObjCTypeRefParser.SetAdditionalGenericContainers"/> state
    /// already relies on.
    /// </summary>
    [ThreadStatic]
    private static Dictionary<string, byte[]>? _sourceByteCache;

    /// <summary>
    /// Parses a Clang AST JSON string into an ObjCModule.
    /// </summary>
    /// <param name="logger">
    /// Optional logger for the node-kind census (Finding 63). When supplied, the parser logs the
    /// full top-level node-kind census at debug and raises <c>SWIFTBIND029</c> for any kind outside
    /// <see cref="KnownTopLevelNodeKinds"/>. Null in tests that don't assert on it.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The clang AST is empty or malformed — zero top-level nodes from a non-empty header set. This
    /// is a systemic parse failure (<c>SWIFTBIND029</c>), surfaced as a hard error rather than a
    /// silently-empty binding.
    /// </exception>
    public static ObjCModule Parse(string json, string moduleName, string frameworkHeadersPath, ILogger? logger = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var classes = new List<ObjCClassDecl>();
        var protocols = new List<ObjCProtocolDecl>();
        var enums = new List<ObjCEnumDecl>();
        var structs = new List<ObjCStructDecl>();
        var functions = new List<ObjCFunctionDecl>();
        var constants = new List<ObjCConstantDecl>();
        var typedefs = new List<ObjCTypedefDecl>();
        var systemTypedefs = new List<ObjCTypedefDecl>();
        var categories = new List<ObjCCategoryDecl>();
        // Apple SDK class/protocol name → owning .NET namespace (empty when none derivable).
        var appleSdkTypeNamespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        // Apple SDK ENUM name → owning .NET namespace. Usings-only — never feeds resolvability.
        var appleSdkEnumNamespaces = new Dictionary<string, string>(StringComparer.Ordinal);

        // Normalize headers path for comparison
        frameworkHeadersPath = frameworkHeadersPath.TrimEnd('/');

        // Systemic-failure hard error (Finding 63): zero top-level AST nodes from a non-empty
        // header set. ObjCPipeline only calls Parse after locating a real umbrella header, so an
        // absent/empty `inner` means clang ran on a real header and produced nothing — a malformed
        // dump or a header that failed to compile, NOT "this framework has no declarations" (a real
        // umbrella always expands at least system builtins). Failing open here would silently emit
        // an empty binding; fail loud instead.
        if (!root.TryGetProperty("inner", out var inner)
            || inner.ValueKind != JsonValueKind.Array
            || inner.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                $"SWIFTBIND029: Clang produced zero top-level AST nodes for module '{moduleName}'. " +
                "This is a systemic parse failure — the clang AST dump is empty or malformed (e.g. " +
                "the umbrella header failed to compile, or the AST flags/output schema changed), not " +
                "an empty framework. Refusing to emit an empty binding silently.");
        }

        // Pre-scan: collect ObjC class names that declare lightweight generic type parameters.
        // These are used by ObjCTypeRefParser to distinguish generic containers (MOSResults<ObjectType>)
        // from protocol-qualified types (NSObject<NSCopying>) when both use simple identifier args.
        var astGenericContainers = ScanGenericContainerNames(inner);
        ObjCTypeRefParser.SetAdditionalGenericContainers(
            astGenericContainers.Count > 0 ? astGenericContainers : null);
        try
        {

        // Track the "current file" — clang omits loc.file when it's the same as
        // the previous declaration, so we must carry it forward.
        string? currentFile = null;

        // Track the last anonymous RecordDecl (struct with fields but no name)
        // to promote when a typedef follows it.
        List<ObjCStructField>? lastAnonymousStructFields = null;
        bool lastAnonymousHasUnsafeLayout = false;
        string? lastAnonymousUnsafeReason = null;

        // Node-kind census (Finding 63): tally every top-level kind so silent skips become loud.
        // Reported after the pass; out-of-vocabulary kinds raise SWIFTBIND029.
        var nodeKindCensus = new Dictionary<string, int>(StringComparer.Ordinal);

        // Pass 1: Parse all top-level declarations
        foreach (var node in inner.EnumerateArray())
        {
            if (!node.TryGetProperty("kind", out var kindProp))
                continue;

            var kind = kindProp.GetString();
            if (kind == null)
                continue;

            nodeKindCensus[kind] = nodeKindCensus.TryGetValue(kind, out var kindCount) ? kindCount + 1 : 1;

            // Update current file tracking and filter by framework headers path.
            // IsPublicDeclaration always updates currentFile tracking (side-effect),
            // even when returning false, so file tracking stays accurate.
            var isFrameworkLocal = IsPublicDeclaration(node, frameworkHeadersPath, ref currentFile, out var nodeResolvedFile);


            // Non-framework-local declarations: parse TypedefDecl for typedef resolution,
            // and collect class/protocol names from Apple SDK headers for ApiDefinition
            // type resolvability (these types are available via .NET iOS framework bindings).
            if (!isFrameworkLocal)
            {
                if (kind == "TypedefDecl")
                {
                    // Fall through to switch below
                }
                else if ((kind is "ObjCInterfaceDecl" or "ObjCProtocolDecl") && IsAppleSdkPath(nodeResolvedFile))
                {
                    var name = GetName(node);
                    if (name != null)
                    {
                        // Provenance: the resolved header path names the owning <Framework>.framework,
                        // the authoritative source of the .NET namespace. A real namespace overwrites a
                        // prior empty seed; an empty (no .framework segment, e.g. /usr/include) only
                        // seeds when absent so it never clobbers a real one.
                        if (AppleFrameworkRegistry.TryResolveFrameworkNamespaceFromHeaderPath(nodeResolvedFile, out var ns))
                            appleSdkTypeNamespaces[name] = ns;
                        else
                            appleSdkTypeNamespaces.TryAdd(name, "");
                    }
                    continue;
                }
                else if (kind == "EnumDecl" && IsAppleSdkPath(nodeResolvedFile))
                {
                    // Usings-only channel: Apple SDK enums (e.g. MTLPixelFormat from Metal) are
                    // referenced from struct fields / free functions but must NOT enter
                    // appleSdkTypeNamespaces — those keys drive ApiDefinition resolvability.
                    var name = GetName(node);
                    if (name != null
                        && AppleFrameworkRegistry.TryResolveFrameworkNamespaceFromHeaderPath(nodeResolvedFile, out var ns))
                        appleSdkEnumNamespaces[name] = ns;
                    continue;
                }
                else
                {
                    continue;
                }
            }

            switch (kind)
            {
                case "ObjCInterfaceDecl":
                    if (!IsForwardDeclaration(node))
                    {
                        var classDecl = ParseClassDecl(node, nodeResolvedFile);
                        if (classDecl != null)
                            classes.Add(classDecl);
                    }
                    break;

                case "ObjCProtocolDecl":
                    if (!IsForwardDeclaration(node))
                    {
                        var protocolDecl = ParseProtocolDecl(node, currentFile, nodeResolvedFile);
                        if (protocolDecl != null)
                            protocols.Add(protocolDecl);
                    }
                    break;

                case "ObjCCategoryDecl":
                    var category = ParseCategoryDecl(node, nodeResolvedFile);
                    if (category != null)
                        categories.Add(category);
                    break;

                case "EnumDecl":
                    var enumDecl = ParseEnumDecl(node, nodeResolvedFile);
                    if (enumDecl != null)
                        enums.Add(enumDecl);
                    break;

                case "RecordDecl":
                    var structDecl = ParseStructDecl(node);
                    if (structDecl != null)
                        structs.Add(structDecl);
                    else
                    {
                        // Anonymous struct — remember its fields and layout info for potential typedef promotion
                        var (anonFields, hasUnsafe, unsafeReason) = ParseStructFieldsWithLayout(node);
                        lastAnonymousStructFields = anonFields.Count > 0 || hasUnsafe ? anonFields : null;
                        lastAnonymousHasUnsafeLayout = hasUnsafe;
                        lastAnonymousUnsafeReason = unsafeReason;
                    }
                    break;

                case "FunctionDecl":
                    var funcDecl = ParseFunctionDecl(node, nodeResolvedFile);
                    if (funcDecl != null)
                        functions.Add(funcDecl);
                    break;

                case "VarDecl":
                    var constDecl = ParseConstantDecl(node, nodeResolvedFile);
                    if (constDecl != null)
                        constants.Add(constDecl);
                    break;

                case "TypedefDecl":
                    // Only framework-local typedefs can consume anonymous struct fields.
                    // A system-header typedef must NOT steal pending fields from a
                    // framework-local anonymous RecordDecl that precedes it.
                    var (typedefDecl, promotedStruct) = ParseTypedefDecl(node,
                        isFrameworkLocal ? lastAnonymousStructFields : null,
                        isFrameworkLocal ? lastAnonymousHasUnsafeLayout : false,
                        isFrameworkLocal ? lastAnonymousUnsafeReason : null);
                    if (isFrameworkLocal)
                    {
                        lastAnonymousStructFields = null; // consumed by framework-local typedef
                        lastAnonymousHasUnsafeLayout = false;
                        lastAnonymousUnsafeReason = null;
                    }
                    if (typedefDecl != null)
                    {
                        if (isFrameworkLocal)
                            typedefs.Add(typedefDecl);
                        else
                            systemTypedefs.Add(typedefDecl);
                    }
                    if (promotedStruct != null && isFrameworkLocal)
                        structs.Add(promotedStruct);
                    break;
            }
        }

        // Census report (Finding 63): full census at debug, SWIFTBIND029 for unrecognized kinds.
        ReportNodeKindCensus(nodeKindCensus, moduleName, logger);

        // Pass 2: Merge categories onto their owning classes.
        // Merge onto ALL matching duplicates so Pass 3 dedup doesn't discard category members.
        // Also merge category-adopted protocols onto the class's ProtocolNames.
        foreach (var cat in categories)
        {
            var taggedMethods = cat.Methods.Select(m => m with { IsFromCategory = true, CategoryName = cat.CategoryName }).ToList();
            var taggedProperties = cat.Properties.Select(p => p with { IsFromCategory = true, CategoryName = cat.CategoryName }).ToList();
            for (int i = 0; i < classes.Count; i++)
            {
                if (classes[i].Name == cat.ClassName)
                {
                    var mergedProtocols = classes[i].ProtocolNames;
                    if (cat.ProtocolNames.Count > 0)
                    {
                        var allProtos = new HashSet<string>(classes[i].ProtocolNames);
                        foreach (var p in cat.ProtocolNames) allProtos.Add(p);
                        mergedProtocols = allProtos.ToList();
                    }
                    classes[i] = classes[i] with
                    {
                        Methods = [.. classes[i].Methods, .. taggedMethods],
                        Properties = [.. classes[i].Properties, .. taggedProperties],
                        ProtocolNames = mergedProtocols
                    };
                }
            }
            // If class not found (forward-declared in another framework), skip category
        }

        // Pass 3: Deduplicate declarations by name.
        // The same type can appear in multiple headers (public + internal, or multiple umbrella includes).
        // Enums/structs: keep richest (most cases/fields) since empty forward-like decls precede full defs.
        // Classes/protocols: merge metadata (superclass, protocols, generic params)
        //   from all duplicates onto the richest (most methods+properties).
        // Functions/constants/typedefs: keep first (no richness variation).
        // Availability is merged from every duplicate (not just the kept one) for every decl kind that
        // carries it — a sparser duplicate (e.g. a forward enum decl) can hold the availability macro
        // while the richer definition does not, and dropping it would lose a real [SupportedOSPlatform].
        // ObjCStructDecl has no Availability field, so structs use the plain richest-wins dedup.
        enums = DeduplicateByRichestMergingAvailability(
            enums, e => e.Name, e => e.Cases.Count, e => e.Availability, (e, av) => e with { Availability = av });
        structs = DeduplicateByRichest(structs, s => s.Name, s => s.Fields.Count);
        classes = MergeClasses(classes);
        protocols = MergeProtocols(protocols);
        // Functions/constants have no member-richness axis, but a real header shape DOES split
        // availability across duplicates: a bare forward declaration followed by a redeclaration that
        // carries the availability macro (e.g. `void F(void);` then `void F(void) API_AVAILABLE(...)`).
        // Keep the first decl's identity but MERGE availability from every duplicate so a later-decl
        // annotation is not dropped — same fidelity the class/protocol merge path already provides.
        functions = DeduplicateByFirstMergingAvailability(
            functions, f => f.Name, f => f.Availability, (f, av) => f with { Availability = av });
        constants = DeduplicateByFirstMergingAvailability(
            constants, c => c.Name, c => c.Availability, (c, av) => c with { Availability = av });
        typedefs = DeduplicateByFirst(typedefs, t => t.Name); // no Availability field — nothing to merge

        // Pass 3.5: Drop deprecated-subclass legacy-name aliases. Must run AFTER MergeClasses so
        // SuperclassName is populated from the definition node (not a stray forward-decl instance).
        // Apple's MTR_DEPRECATED rename pattern: the canonical class (MTROTAFoo) is preserved, and a
        // fully-deprecated subclass with the legacy spelling (MTROtaFoo) re-declares the parent's
        // properties verbatim purely so existing source keeps compiling. Emitting both produces
        // duplicate partial interfaces and bgen .g.cs collisions on case-insensitive macOS
        // filesystems. The alias's name differs from its superclass's name only by letter casing —
        // that is the most reliable signal for this rename pattern. (Availability itself IS now
        // recovered from header source per Finding 22 a2 — see RecoverAvailability — but the clang
        // JSON AvailabilityAttr node still carries only {id, kind, range}, and a pure "is-deprecated"
        // match would be far less precise than the name-casing match used here.)
        var droppedAliasNames = new HashSet<string>(StringComparer.Ordinal);
        var aliasToCanonical = new Dictionary<string, string>(StringComparer.Ordinal);
        classes = DropDeprecatedSubclassAliases(classes, droppedAliasNames, aliasToCanonical);

        // Rewrite any remaining class whose SuperclassName points at a dropped alias to point at
        // the alias's superclass (the canonical class). Defensive — Apple's rename pattern places
        // the alias as the leaf, but a future framework may break that assumption.
        if (aliasToCanonical.Count > 0)
        {
            classes = classes
                .Select(c => c.SuperclassName != null && aliasToCanonical.TryGetValue(c.SuperclassName, out var canonical)
                    ? c with { SuperclassName = canonical }
                    : c)
                .ToList();
        }

        // Pass 4: Deduplicate categories by (ClassName, CategoryName).
        // Same category can appear through umbrella + public header.
        // Also drop categories whose owning class was a deprecated-subclass alias that we just
        // removed — emitting them would reference a non-existent base type. We only filter against
        // the exact alias drop set (not all-classes-not-in-module), because the downstream pipeline
        // pass ObjCPipeline.FilterToForeignCategories preserves legitimately-foreign categories on
        // Apple SDK types (e.g. NSNull+MOSValue).
        var dedupedCategories = MergeCategories(categories)
            .Where(c => !droppedAliasNames.Contains(c.ClassName))
            .ToList();

        return new ObjCModule
        {
            ModuleName = moduleName,
            FrameworkPath = frameworkHeadersPath,
            Classes = classes,
            Protocols = protocols,
            Enums = enums,
            Structs = structs,
            Functions = functions,
            Constants = constants,
            Typedefs = typedefs,
            // System typedefs first, framework-local second — BuildResolvedTypedefMap uses
            // last-write-wins dict assignment, so framework-local definitions take precedence
            // when a system header defines the same alias name.
            ResolutionTypedefs = [.. systemTypedefs, .. typedefs],
            Categories = dedupedCategories,
            AppleSdkTypeNamespaces = appleSdkTypeNamespaces.Count > 0 ? appleSdkTypeNamespaces : null,
            AppleSdkEnumNamespaces = appleSdkEnumNamespaces.Count > 0 ? appleSdkEnumNamespaces : null,
        };

        } // try
        finally
        {
            ObjCTypeRefParser.SetAdditionalGenericContainers(null);
            _sourceByteCache = null;
        }
    }

    /// <summary>
    /// Reports the top-level node-kind census (Finding 63). Logs the full kind→count tally at debug,
    /// then raises <c>SWIFTBIND029</c> (warning) for any kind outside
    /// <see cref="KnownTopLevelNodeKinds"/> — those are silently skipped by the parser's switch, so
    /// surfacing them prevents a future Clang schema change from dropping bindable API unnoticed.
    /// No-op when no logger is supplied.
    /// </summary>
    internal static void ReportNodeKindCensus(
        IReadOnlyDictionary<string, int> census, string moduleName, ILogger? logger)
    {
        if (logger == null || census.Count == 0)
            return;

        logger.LogDebug(
            "Clang AST node-kind census for '{Module}': {Census}",
            moduleName,
            string.Join(", ", census.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}")));

        var novel = census
            .Where(kv => !KnownTopLevelNodeKinds.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();
        if (novel.Count > 0)
        {
            logger.LogWarning(
                "SWIFTBIND029: Clang AST for module '{Module}' contains {Count} top-level node " +
                "kind(s) this parser does not recognize and silently skips: {Kinds}. If any carry " +
                "bindable API, the binding is incomplete. After confirming whether they need " +
                "handling, update ClangAstParser.KnownTopLevelNodeKinds (and its census golden test).",
                moduleName, novel.Count,
                string.Join(", ", novel.Select(kv => $"{kv.Key}({kv.Value})")));
        }
    }

    // ──────────────────────────────────────────────
    // Top-level declaration parsers
    // ──────────────────────────────────────────────

    private static ObjCClassDecl? ParseClassDecl(JsonElement element, string? declFile = null)
    {
        var name = GetName(element);
        if (name == null) return null;

        var superclass = GetOptionalString(element, "super");
        // Extract superclass name: could be nested under "name"
        if (superclass == null && element.TryGetProperty("super", out var superProp) &&
            superProp.ValueKind == JsonValueKind.Object)
        {
            superclass = GetOptionalString(superProp, "name");
        }

        var protocols = new List<string>();
        if (element.TryGetProperty("protocols", out var protocolsArr))
        {
            foreach (var p in protocolsArr.EnumerateArray())
            {
                var pName = GetOptionalString(p, "name");
                if (pName != null)
                    protocols.Add(pName);
            }
        }

        // Extract ObjC lightweight generic type parameters (e.g., MOSObjectType in MOSResults<MOSObjectType>)
        var genericTypeParamNames = new List<string>();
        if (element.TryGetProperty("inner", out var innerForParams))
        {
            foreach (var child in innerForParams.EnumerateArray())
            {
                if (GetOptionalString(child, "kind") == "ObjCTypeParamDecl")
                {
                    var paramName = GetName(child);
                    if (paramName != null)
                        genericTypeParamNames.Add(paramName);
                }
            }
        }

        var methods = new List<ObjCMethodDecl>();
        var properties = new List<ObjCPropertyDecl>();

        ParseContainerChildren(element, methods, properties, isProtocol: false, currentFile: declFile);

        var swiftName = ExtractSwiftName(element);
        var (docComment, _) = ExtractDocComment(element);

        // An interface marked __attribute__((objc_runtime_name("X"))) has its runtime class
        // symbol under X, not its declared name. Clang's JSON AST emits an ObjCRuntimeNameAttr
        // child node but omits the string argument, so we can only record the attribute's
        // presence; the native-symbol guard uses this to avoid false-dropping the class.
        var hasCustomRuntimeName = HasDirectChildOfKind(element, "ObjCRuntimeNameAttr");

        return new ObjCClassDecl
        {
            Name = name,
            SuperclassName = superclass,
            ProtocolNames = protocols,
            GenericTypeParamNames = genericTypeParamNames,
            Methods = methods,
            Properties = properties,
            SwiftName = swiftName,
            DocComment = docComment,
            HasCustomRuntimeName = hasCustomRuntimeName,
            Availability = RecoverAvailability(element, declFile)
        };
    }

    /// <summary>
    /// Returns true if <paramref name="element"/> has a direct <c>inner</c> child whose
    /// <c>kind</c> equals <paramref name="kind"/>. Used to detect decl-level attribute nodes
    /// (e.g. <c>ObjCRuntimeNameAttr</c>) that clang places alongside member declarations.
    /// </summary>
    private static bool HasDirectChildOfKind(JsonElement element, string kind)
    {
        if (!element.TryGetProperty("inner", out var inner) || inner.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var child in inner.EnumerateArray())
        {
            if (GetOptionalString(child, "kind") == kind)
                return true;
        }
        return false;
    }

    private static ObjCProtocolDecl? ParseProtocolDecl(JsonElement element, string? currentFile = null, string? declFile = null)
    {
        var name = GetName(element);
        if (name == null) return null;

        var inherited = new List<string>();
        if (element.TryGetProperty("protocols", out var protocolsArr))
        {
            foreach (var p in protocolsArr.EnumerateArray())
            {
                var pName = GetOptionalString(p, "name");
                if (pName != null)
                    inherited.Add(pName);
            }
        }

        var methods = new List<ObjCMethodDecl>();
        var properties = new List<ObjCPropertyDecl>();

        ParseContainerChildren(element, methods, properties, isProtocol: true, currentFile: currentFile ?? declFile);

        var (docComment, _) = ExtractDocComment(element);

        return new ObjCProtocolDecl
        {
            Name = name,
            InheritedProtocolNames = inherited,
            Methods = methods,
            Properties = properties,
            DocComment = docComment,
            Availability = RecoverAvailability(element, declFile ?? currentFile)
        };
    }

    private static ObjCCategoryDecl? ParseCategoryDecl(JsonElement element, string? declFile = null)
    {
        // In clang AST, the owning class is in "interface.name", not "name".
        // "name" is the category name (e.g., "NSCoderMethods" in NSObject(NSCoderMethods)).
        string? className = null;
        if (element.TryGetProperty("interface", out var iface) &&
            iface.ValueKind == JsonValueKind.Object)
        {
            className = GetOptionalString(iface, "name");
        }
        if (className == null) return null;

        // Category name: null from AST means unnamed category (class extension) → normalize to ""
        var categoryName = GetName(element) ?? "";

        // Extract protocols adopted by this category
        var protocols = new List<string>();
        if (element.TryGetProperty("protocols", out var protocolsArr))
        {
            foreach (var p in protocolsArr.EnumerateArray())
            {
                var pName = GetOptionalString(p, "name");
                if (pName != null)
                    protocols.Add(pName);
            }
        }

        var methods = new List<ObjCMethodDecl>();
        var properties = new List<ObjCPropertyDecl>();

        ParseContainerChildren(element, methods, properties, isProtocol: false, currentFile: declFile);

        return new ObjCCategoryDecl
        {
            CategoryName = categoryName,
            ClassName = className,
            ProtocolNames = protocols,
            Methods = methods,
            Properties = properties,
            Availability = RecoverAvailability(element, declFile)
        };
    }

    private static ObjCEnumDecl? ParseEnumDecl(JsonElement element, string? declFile = null)
    {
        var name = GetName(element);
        if (name == null) return null;

        var isOptions = false;
        var cases = new List<ObjCEnumCaseDecl>();
        ObjCTypeRef? underlyingType = null;

        // Check for fixed underlying type
        if (element.TryGetProperty("fixedUnderlyingType", out var fixedType))
        {
            var qualType = GetOptionalString(fixedType, "qualType");
            if (qualType != null)
                underlyingType = ObjCTypeRefParser.Parse(qualType);
        }

        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                var childKind = GetOptionalString(child, "kind");
                switch (childKind)
                {
                    case "EnumConstantDecl":
                        var caseName = GetName(child);
                        if (caseName != null)
                        {
                            long? value = null;
                            // Try to extract value from inner ConstantExpr or IntegerLiteral
                            if (child.TryGetProperty("inner", out var caseInner))
                            {
                                value = TryExtractEnumValue(caseInner);
                            }
                            cases.Add(new ObjCEnumCaseDecl
                            {
                                Name = caseName,
                                Value = value,
                                // Per-case availability: an enumerator can carry its own
                                // API_AVAILABLE/API_DEPRECATED/API_UNAVAILABLE distinct from the
                                // enum type's (recovered the same way — source byte offset at the
                                // EnumConstantDecl's range.begin; degrades to empty when absent).
                                Availability = RecoverAvailability(child, declFile),
                            });
                        }
                        break;

                    case "FlagEnumAttr":
                        isOptions = true;
                        break;
                }
            }
        }

        var swiftName = ExtractSwiftName(element);
        var (docComment, _) = ExtractDocComment(element);

        return new ObjCEnumDecl
        {
            Name = name,
            IsOptions = isOptions,
            UnderlyingType = underlyingType,
            Cases = cases,
            SwiftName = swiftName,
            DocComment = docComment,
            Availability = RecoverAvailability(element, declFile)
        };
    }

    private static ObjCStructDecl? ParseStructDecl(JsonElement element)
    {
        var name = GetName(element);
        if (name == null) return null;

        var (fields, hasUnsafeLayout, unsafeReason) = ParseStructFieldsWithLayout(element);
        return new ObjCStructDecl { Name = name, Fields = fields, HasUnsafeLayout = hasUnsafeLayout, UnsafeLayoutReason = unsafeReason };
    }

    private static List<ObjCStructField> ParseStructFields(JsonElement element)
    {
        var (fields, _, _) = ParseStructFieldsWithLayout(element);
        return fields;
    }

    private static (List<ObjCStructField> fields, bool hasUnsafeLayout, string? unsafeReason) ParseStructFieldsWithLayout(JsonElement element)
    {
        var fields = new List<ObjCStructField>();
        var unsafeReasons = new List<string>();

        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                var kind = GetOptionalString(child, "kind");

                if (kind == "FieldDecl")
                {
                    // Detect bitfield: clang AST emits "isBitfield": true on FieldDecl
                    if (child.TryGetProperty("isBitfield", out var isBitfield) && isBitfield.GetBoolean())
                    {
                        unsafeReasons.Add("contains bitfield");
                        continue;
                    }

                    var fieldName = GetName(child);
                    var fieldType = GetQualType(child);
                    if (fieldName != null && fieldType != null)
                    {
                        fields.Add(new ObjCStructField
                        {
                            Name = fieldName,
                            Type = ObjCTypeRefParser.Parse(fieldType)
                        });
                    }
                }
                else if (kind == "RecordDecl")
                {
                    // Anonymous union/struct inside the struct
                    var memberName = GetName(child);
                    if (memberName == null)
                        unsafeReasons.Add("contains anonymous union/struct");
                }
            }
        }

        var hasUnsafe = unsafeReasons.Count > 0;
        var reason = hasUnsafe ? string.Join(", ", unsafeReasons.Distinct()) : null;
        return (fields, hasUnsafe, reason);
    }

    private static ObjCFunctionDecl? ParseFunctionDecl(JsonElement element, string? declFile = null)
    {
        var name = GetName(element);
        if (name == null) return null;

        // Only functions with external linkage produce a callable symbol in the binary. A
        // `static` (internal-linkage) function is file-local, and a `static inline`/`NS_INLINE`
        // — or any non-`extern` `inline` — definition is inlined at every call site and emits no
        // standalone symbol. Generating a P/Invoke for either yields an undefined symbol at link
        // (the static registrar's force-reference then makes the whole app fail to link), so there
        // is nothing to bind. Skip them at parse time; a binary-backed filter later in the pipeline
        // is the authoritative backstop for symbols that look external but are never actually exported.
        var storageClass = element.TryGetProperty("storageClass", out var storageClassProp)
            ? storageClassProp.GetString()
            : null;
        var isInline = element.TryGetProperty("inline", out var inlineProp) && inlineProp.GetBoolean();
        if (storageClass == "static" || (isInline && storageClass != "extern"))
            return null;

        var returnType = GetQualType(element);
        if (returnType == null) return null;

        // Detect variadic functions: clang AST emits "variadic": true on FunctionDecl
        var isVariadic = false;
        if (element.TryGetProperty("variadic", out var variadicProp) && variadicProp.GetBoolean())
        {
            isVariadic = true;
        }

        var parameters = new List<ObjCParameterDecl>();

        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                var childKind = GetOptionalString(child, "kind");
                switch (childKind)
                {
                    case "ParmVarDecl":
                        var param = ParseParameter(child);
                        if (param != null)
                            parameters.Add(param);
                        break;
                }
            }
        }

        // Parse the return type from the function type signature
        var funcReturnType = ParseFunctionReturnType(returnType);

        return new ObjCFunctionDecl
        {
            Name = name,
            ReturnType = ObjCTypeRefParser.Parse(funcReturnType),
            Parameters = parameters,
            IsVariadic = isVariadic,
            Availability = RecoverAvailability(element, declFile)
        };
    }

    private static ObjCConstantDecl? ParseConstantDecl(JsonElement element, string? declFile = null)
    {
        var name = GetName(element);
        if (name == null) return null;

        var qualType = GetQualType(element);
        if (qualType == null) return null;

        var isExtern = false;
        if (element.TryGetProperty("storageClass", out var sc))
        {
            isExtern = sc.GetString() == "extern";
        }

        return new ObjCConstantDecl
        {
            Name = name,
            Type = ObjCTypeRefParser.Parse(qualType),
            IsExtern = isExtern,
            Availability = RecoverAvailability(element, declFile)
        };
    }

    private static (ObjCTypedefDecl?, ObjCStructDecl?) ParseTypedefDecl(JsonElement element, List<ObjCStructField>? precedingAnonymousFields = null, bool precedingHasUnsafeLayout = false, string? precedingUnsafeReason = null)
    {
        var name = GetName(element);
        if (name == null) return (null, null);

        // Get the underlying type from inner or type
        string? underlyingQualType = null;
        ObjCStructDecl? promotedStruct = null;

        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                var childKind = GetOptionalString(child, "kind");

                // Check for anonymous struct (RecordDecl with fields) inside typedef's inner
                if (childKind == "RecordDecl")
                {
                    var (fields, hasUnsafe, unsafeReason) = ParseStructFieldsWithLayout(child);
                    if (fields.Count > 0 || hasUnsafe)
                        promotedStruct = new ObjCStructDecl { Name = name, Fields = fields, HasUnsafeLayout = hasUnsafe, UnsafeLayoutReason = unsafeReason };
                }

                if (childKind is "BuiltinType" or "RecordType" or "ElaboratedType"
                    or "ObjCObjectPointerType" or "TypedefType" or "PointerType"
                    or "BlockPointerType" or "EnumType")
                {
                    underlyingQualType = GetOptionalString(child, "qualType")
                        ?? GetQualType(child);
                    break;
                }
            }
        }

        // Promote anonymous struct from preceding sibling RecordDecl
        // (clang emits anonymous struct as top-level sibling, then typedef referencing it)
        if (promotedStruct == null && (precedingAnonymousFields is { Count: > 0 } || precedingHasUnsafeLayout))
        {
            var qualType = GetQualType(element);
            if (qualType != null && qualType.StartsWith("struct ", StringComparison.Ordinal))
                promotedStruct = new ObjCStructDecl { Name = name, Fields = precedingAnonymousFields ?? [], HasUnsafeLayout = precedingHasUnsafeLayout, UnsafeLayoutReason = precedingUnsafeReason };
        }

        // Fall back to the type property
        underlyingQualType ??= GetQualType(element);
        if (underlyingQualType == null) return (null, promotedStruct);

        var typedefDecl = new ObjCTypedefDecl
        {
            Name = name,
            UnderlyingType = ObjCTypeRefParser.Parse(underlyingQualType),
            // NS_TYPED_ENUM / NS_TYPED_EXTENSIBLE_ENUM lower to clang's swift_wrapper attribute, which
            // -ast-dump=json emits as a SwiftNewTypeAttr child. Its presence is what distinguishes a
            // Swift-newtype typedef (bridges to an _ObjectiveCBridgeable value-type struct) from a plain
            // alias; the string argument is not needed (and JSON omits it, as for SwiftNameAttr).
            IsSwiftNewType = HasDirectChildOfKind(element, "SwiftNewTypeAttr")
        };
        return (typedefDecl, promotedStruct);
    }

    // ──────────────────────────────────────────────
    // Container children parsing (class/protocol/category)
    // ──────────────────────────────────────────────

    private static void ParseContainerChildren(
        JsonElement element,
        List<ObjCMethodDecl> methods,
        List<ObjCPropertyDecl> properties,
        bool isProtocol,
        string? currentFile = null)
    {
        if (!element.TryGetProperty("inner", out var inner))
            return;

        // For protocols, build a set of source lines that fall in @optional sections
        // by reading the header file. Clang JSON marks properties with control:"optional"
        // but does NOT mark methods — we need source-level section parsing.
        // Pass currentFile as fallback: clang omits loc.file when it's inherited from the
        // previous declaration's file context, which happens when headers are included from
        // an umbrella header.
        HashSet<int>? optionalLines = null;
        if (isProtocol)
            optionalLines = BuildOptionalLineSet(element, currentFile);

        foreach (var child in inner.EnumerateArray())
        {
            var childKind = GetOptionalString(child, "kind");
            switch (childKind)
            {
                case "ObjCMethodDecl":
                    // Skip implicit accessor methods generated for properties
                    if (child.TryGetProperty("isImplicit", out var implProp) && implProp.GetBoolean())
                        break;
                    var method = ParseMethodDecl(child, IsInOptionalSection(child, optionalLines), currentFile);
                    if (method != null)
                        methods.Add(method);
                    break;

                case "ObjCPropertyDecl":
                    var prop = ParsePropertyDecl(child, optionalLines, currentFile);
                    if (prop != null)
                        properties.Add(prop);
                    break;
            }
        }
    }

    /// <summary>
    /// Builds a set of source line numbers that fall within @optional sections
    /// of a protocol, by reading the header file and finding @optional/@required markers.
    /// Returns null if the source file can't be read.
    /// <param name="currentFile">Fallback file path from the parser's file tracking.
    /// Clang omits loc.file when the file is inherited from the previous declaration,
    /// which happens when protocols are defined in headers included from an umbrella header.</param>
    /// </summary>
    private static HashSet<int>? BuildOptionalLineSet(JsonElement protocolElement, string? currentFile = null)
    {
        // Resolve the source file from the protocol's loc, with fallback to currentFile
        var filePath = ResolveLocFile(protocolElement) ?? currentFile;
        if (filePath == null || !File.Exists(filePath))
            return null;

        // Get the protocol's line range from the AST
        int startLine = GetLocLine(protocolElement);
        int endLine = GetRangeEndLine(protocolElement);
        if (startLine <= 0) return null;
        if (endLine <= 0) endLine = int.MaxValue;

        string[] lines;
        try { lines = File.ReadAllLines(filePath); }
        catch { return null; }

        var optionalLines = new HashSet<int>();
        var inOptional = false;

        for (int i = startLine - 1; i < Math.Min(lines.Length, endLine); i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == "@optional")
                inOptional = true;
            else if (trimmed == "@required")
                inOptional = false;

            if (inOptional)
                optionalLines.Add(i + 1); // 1-based line numbers
        }

        return optionalLines.Count > 0 ? optionalLines : null;
    }

    private static bool IsInOptionalSection(JsonElement child, HashSet<int>? optionalLines)
    {
        if (optionalLines == null) return false;
        int line = GetLocLine(child);
        return line > 0 && optionalLines.Contains(line);
    }

    // ──────────────────────────────────────────────
    // Availability recovery (Finding 22, recovery option a2)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Recovers platform-availability records for a declaration by reading its
    /// <c>AvailabilityAttr</c> child nodes from the consumer header source.
    /// <para/>
    /// Clang's <c>-ast-dump=json</c> serializes <c>AvailabilityAttr</c> as only
    /// <c>{id, kind, range}</c> — the platform / introduced / deprecated / message fields are NOT
    /// emitted. We instead read the raw annotation text at the attribute's source byte offset and
    /// parse it (see <see cref="ObjCAvailabilityParser"/>). For macro forms (<c>API_AVAILABLE</c>,
    /// <c>NS_AVAILABLE_IOS</c>, …) the <c>range.begin.expansionLoc</c> points at the macro use-site
    /// in the consumer header (the <c>spellingLoc</c> instead points uselessly into the SDK's
    /// <c>AvailabilityInternal.h</c>); for a bare <c>__attribute__((availability(...)))</c> the
    /// <c>range.begin</c> offset points directly at the <c>availability</c> keyword and the file is
    /// omitted (inherited from the enclosing declaration → <paramref name="declFile"/>).
    /// </summary>
    private static List<ObjCAvailability> RecoverAvailability(JsonElement element, string? declFile)
    {
        var result = new List<ObjCAvailability>();
        if (!element.TryGetProperty("inner", out var inner) || inner.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var child in inner.EnumerateArray())
        {
            if (GetOptionalString(child, "kind") != "AvailabilityAttr")
                continue;
            var recovered = RecoverOneAvailability(child, declFile);
            if (recovered != null)
                result.AddRange(recovered);
        }
        return result;
    }

    private static IReadOnlyList<ObjCAvailability>? RecoverOneAvailability(JsonElement attr, string? declFile)
    {
        if (!attr.TryGetProperty("range", out var range) || !range.TryGetProperty("begin", out var begin))
            return null;

        if (!TryGetSourceOffset(begin, out var offset, out var file))
            return null;

        file ??= declFile;
        if (file == null)
            return null;

        var bytes = ReadFileBytesCached(file);
        if (bytes == null || offset < 0 || offset >= bytes.Length)
            return null;

        var invocation = ReadInvocationAt(bytes, offset);
        if (invocation == null)
            return null;

        var (token, args) = SplitInvocation(invocation);
        if (token == null)
            return null;

        return ObjCAvailabilityParser.ParseInvocation(token, args);
    }

    /// <summary>
    /// Resolves the source byte offset and (best-effort) file for an <c>AvailabilityAttr</c>'s
    /// <c>range.begin</c> location. Prefers <c>expansionLoc</c> (the macro use-site) over the bare
    /// location; returns null-file when clang omits it (the caller falls back to the decl's file).
    /// </summary>
    private static bool TryGetSourceOffset(JsonElement loc, out int offset, out string? file)
    {
        offset = 0;
        file = null;

        // Macro forms carry the consumer-header use-site under expansionLoc.
        if (loc.TryGetProperty("expansionLoc", out var exp) && TryReadOffset(exp, out offset))
        {
            file = TryGetLocFile(exp, "file", out var f) ? f : null;
            return true;
        }

        // Bare __attribute__ / direct location.
        if (TryReadOffset(loc, out offset))
        {
            file = TryGetLocFile(loc, "file", out var f) ? f : null;
            return true;
        }

        return false;
    }

    private static bool TryReadOffset(JsonElement loc, out int offset)
    {
        offset = 0;
        if (loc.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number)
        {
            offset = o.GetInt32();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reads (and caches) the raw bytes of a header file. Clang source offsets are byte offsets, so
    /// the slice must be taken over bytes, not over a decoded string (headers can contain multi-byte
    /// UTF-8 in comments before the attribute). Returns null when the file can't be read.
    /// </summary>
    private static byte[]? ReadFileBytesCached(string file)
    {
        _sourceByteCache ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (_sourceByteCache.TryGetValue(file, out var cached))
            return cached;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); }
        catch { return null; }

        _sourceByteCache[file] = bytes;
        return bytes;
    }

    /// <summary>
    /// Extracts the availability annotation text beginning at <paramref name="offset"/>: the leading
    /// identifier token followed by its balanced parenthesized argument list (e.g.
    /// <c>API_AVAILABLE(ios(13.0), macos(10.15))</c> or <c>availability(ios, introduced=13.0)</c>).
    /// Returns null when no identifier or no balanced parenthesis group is found.
    /// </summary>
    private static string? ReadInvocationAt(byte[] bytes, int offset)
    {
        int i = offset;

        // Skip leading whitespace.
        while (i < bytes.Length && IsAsciiWhitespace(bytes[i]))
            i++;

        int tokenStart = i;
        // Leading identifier: [A-Za-z_][A-Za-z0-9_]* — the first byte must NOT be a digit (a macro/
        // attribute name never starts with one, and clang anchors the offset at that name), so a bad
        // offset landing mid-number degrades to no-availability instead of misparsing a numeric token.
        if (i >= bytes.Length || !IsIdentStartByte(bytes[i]))
            return null; // not an identifier start at this offset
        while (i < bytes.Length && IsIdentByte(bytes[i]))
            i++;
        if (i == tokenStart)
            return null; // no identifier at this offset

        // Skip whitespace between token and '('.
        int j = i;
        while (j < bytes.Length && IsAsciiWhitespace(bytes[j]))
            j++;
        if (j >= bytes.Length || bytes[j] != (byte)'(')
            return null; // no argument list — not an annotation we can parse

        // Balance parentheses, respecting char/string literals.
        int depth = 0;
        bool inString = false;
        byte quote = (byte)'"';
        int end = -1;
        for (int k = j; k < bytes.Length; k++)
        {
            byte c = bytes[k];
            if (inString)
            {
                if (c == (byte)'\\') { k++; continue; }
                if (c == quote) inString = false;
                continue;
            }
            if (c == (byte)'"' || c == (byte)'\'')
            {
                inString = true;
                quote = c;
            }
            else if (c == (byte)'(')
            {
                depth++;
            }
            else if (c == (byte)')')
            {
                depth--;
                if (depth == 0)
                {
                    end = k;
                    break;
                }
            }
        }
        if (end < 0)
            return null; // unbalanced — give up rather than guess

        return System.Text.Encoding.UTF8.GetString(bytes, tokenStart, end - tokenStart + 1);
    }

    /// <summary>
    /// Splits an invocation string <c>TOKEN(args)</c> into its leading token and the raw argument
    /// text inside the outermost parentheses.
    /// </summary>
    private static (string? token, string args) SplitInvocation(string invocation)
    {
        var open = invocation.IndexOf('(');
        if (open <= 0)
            return (null, "");
        var token = invocation[..open].Trim();
        var close = invocation.LastIndexOf(')');
        var args = close > open ? invocation[(open + 1)..close] : invocation[(open + 1)..];
        return (token.Length == 0 ? null : token, args);
    }

    private static bool IsIdentByte(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z') ||
        (b >= (byte)'a' && b <= (byte)'z') ||
        (b >= (byte)'0' && b <= (byte)'9') ||
        b == (byte)'_';

    private static bool IsIdentStartByte(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z') ||
        (b >= (byte)'a' && b <= (byte)'z') ||
        b == (byte)'_';

    private static bool IsAsciiWhitespace(byte b) =>
        b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n' || b == (byte)'\f' || b == (byte)'\v';

    private static string? ResolveLocFile(JsonElement element)
    {
        if (!element.TryGetProperty("loc", out var loc))
            return null;
        if (TryGetLocFile(loc, "file", out var f)) return f;
        if (loc.TryGetProperty("expansionLoc", out var exp) && TryGetLocFile(exp, "file", out f)) return f;
        if (loc.TryGetProperty("spellingLoc", out var sp) && TryGetLocFile(sp, "file", out f)) return f;
        return null;
    }

    private static int GetLocLine(JsonElement element)
    {
        if (element.TryGetProperty("loc", out var loc))
        {
            if (loc.TryGetProperty("line", out var lineProp) && lineProp.ValueKind == JsonValueKind.Number)
                return lineProp.GetInt32();
            if (loc.TryGetProperty("expansionLoc", out var exp) &&
                exp.TryGetProperty("line", out lineProp) && lineProp.ValueKind == JsonValueKind.Number)
                return lineProp.GetInt32();
            if (loc.TryGetProperty("spellingLoc", out var sp) &&
                sp.TryGetProperty("line", out lineProp) && lineProp.ValueKind == JsonValueKind.Number)
                return lineProp.GetInt32();
        }
        return 0;
    }

    private static int GetRangeEndLine(JsonElement element)
    {
        if (element.TryGetProperty("range", out var range) &&
            range.TryGetProperty("end", out var end) &&
            end.TryGetProperty("line", out var lineProp) && lineProp.ValueKind == JsonValueKind.Number)
            return lineProp.GetInt32();
        return 0;
    }

    private static ObjCMethodDecl? ParseMethodDecl(JsonElement element, bool isOptional, string? declFile = null)
    {
        var name = GetName(element);
        if (name == null) return null;

        var isInstance = true;
        if (element.TryGetProperty("instance", out var instanceProp))
        {
            isInstance = instanceProp.GetBoolean();
        }

        // Detect variadic methods: clang AST emits "variadic": true on ObjCMethodDecl
        var isVariadic = false;
        if (element.TryGetProperty("variadic", out var variadicProp) && variadicProp.GetBoolean())
        {
            isVariadic = true;
        }

        var returnQualType = GetReturnType(element) ?? "void";

        var parameters = new List<ObjCParameterDecl>();
        var isDesignatedInitializer = false;

        if (element.TryGetProperty("inner", out var inner))
        {
            foreach (var child in inner.EnumerateArray())
            {
                var childKind = GetOptionalString(child, "kind");
                switch (childKind)
                {
                    case "ParmVarDecl":
                        var param = ParseParameter(child);
                        if (param != null)
                            parameters.Add(param);
                        break;

                    case "ObjCDesignatedInitializerAttr":
                        isDesignatedInitializer = true;
                        break;
                }
            }
        }

        var swiftName = ExtractSwiftName(element);
        var isRefined = HasSwiftPrivateAttr(element);
        var (docComment, docParams) = ExtractDocComment(element);

        return new ObjCMethodDecl
        {
            Selector = name,
            ReturnType = ObjCTypeRefParser.Parse(returnQualType),
            Parameters = parameters,
            IsInstanceMethod = isInstance,
            IsOptional = isOptional,
            IsVariadic = isVariadic,
            IsDesignatedInitializer = isDesignatedInitializer,
            SwiftName = swiftName,
            IsRefinedForSwift = isRefined,
            DocComment = docComment,
            DocParams = docParams,
            Availability = RecoverAvailability(element, declFile)
        };
    }

    private static ObjCPropertyDecl? ParsePropertyDecl(JsonElement element, HashSet<int>? optionalLines, string? declFile = null)
    {
        var name = GetName(element);
        if (name == null) return null;

        var qualType = GetQualType(element) ?? "id";

        var isReadonly = false;
        if (element.TryGetProperty("readonly", out var roProp))
        {
            isReadonly = roProp.GetBoolean();
        }

        var isClass = false;
        if (element.TryGetProperty("class", out var classProp))
        {
            isClass = classProp.GetBoolean();
        }

        string? getter = null;
        if (element.TryGetProperty("getter", out var getterProp))
        {
            getter = getterProp.ValueKind == JsonValueKind.Object
                ? GetOptionalString(getterProp, "name")
                : getterProp.GetString();
        }

        string? setter = null;
        if (element.TryGetProperty("setter", out var setterProp))
        {
            setter = setterProp.ValueKind == JsonValueKind.Object
                ? GetOptionalString(setterProp, "name")
                : setterProp.GetString();
        }

        // Extract ObjC property memory management attribute (copy, assign, weak, strong, retain, unsafe_unretained)
        var memorySemantic = ObjCMemorySemantic.None;
        if (element.TryGetProperty("copy", out var copyProp) && copyProp.GetBoolean())
            memorySemantic = ObjCMemorySemantic.Copy;
        else if (element.TryGetProperty("weak", out var weakProp) && weakProp.GetBoolean())
            memorySemantic = ObjCMemorySemantic.Weak;
        else if (element.TryGetProperty("strong", out var strongProp) && strongProp.GetBoolean())
            memorySemantic = ObjCMemorySemantic.Strong;
        else if (element.TryGetProperty("retain", out var retainProp) && retainProp.GetBoolean())
            memorySemantic = ObjCMemorySemantic.Retain;
        else if (element.TryGetProperty("assign", out var assignProp) && assignProp.GetBoolean())
            memorySemantic = ObjCMemorySemantic.Assign;
        else if (element.TryGetProperty("unsafe_unretained", out var unsafeProp) && unsafeProp.GetBoolean())
            memorySemantic = ObjCMemorySemantic.UnsafeUnretained;

        // Properties have control:"optional" in clang JSON;
        // also check source-level section for consistency
        var isOptional = false;
        var control = GetOptionalString(element, "control");
        if (control == "optional")
            isOptional = true;
        else if (control == null)
            isOptional = IsInOptionalSection(element, optionalLines);

        var swiftName = ExtractSwiftName(element);
        var isRefined = HasSwiftPrivateAttr(element);
        var (docComment, _) = ExtractDocComment(element);

        return new ObjCPropertyDecl
        {
            Name = name,
            Type = ObjCTypeRefParser.Parse(qualType),
            IsReadonly = isReadonly,
            IsClass = isClass,
            IsOptional = isOptional,
            GetterSelector = getter,
            SetterSelector = setter,
            MemorySemantic = memorySemantic,
            SwiftName = swiftName,
            IsRefinedForSwift = isRefined,
            DocComment = docComment,
            Availability = RecoverAvailability(element, declFile)
        };
    }

    private static ObjCParameterDecl? ParseParameter(JsonElement element)
    {
        var name = GetName(element) ?? "";
        var qualType = GetQualType(element);
        if (qualType == null) return null;

        return new ObjCParameterDecl
        {
            Name = name,
            Type = ObjCTypeRefParser.Parse(qualType)
        };
    }

    /// <summary>
    /// Extracts doc comments from a FullComment node in a declaration's inner nodes.
    /// Clang includes FullComment > ParagraphComment > TextComment for description text,
    /// and ParamCommandComment > ParagraphComment > TextComment for @param docs.
    /// </summary>
    private static (string? summary, List<ObjCDocParam> docParams) ExtractDocComment(JsonElement element)
    {
        if (!element.TryGetProperty("inner", out var inner))
            return (null, []);

        foreach (var child in inner.EnumerateArray())
        {
            if (GetOptionalString(child, "kind") != "FullComment")
                continue;
            if (!child.TryGetProperty("inner", out var commentInner))
                continue;

            var summaryParts = new List<string>();
            var docParams = new List<ObjCDocParam>();

            foreach (var commentChild in commentInner.EnumerateArray())
            {
                var kind = GetOptionalString(commentChild, "kind");
                switch (kind)
                {
                    case "ParagraphComment":
                        var text = ExtractParagraphText(commentChild);
                        if (!string.IsNullOrWhiteSpace(text))
                            summaryParts.Add(text.Trim());
                        break;

                    case "BlockCommandComment":
                        // @return / @brief etc — treat as summary text
                        if (commentChild.TryGetProperty("inner", out var blockInner))
                        {
                            foreach (var blockChild in blockInner.EnumerateArray())
                            {
                                if (GetOptionalString(blockChild, "kind") == "ParagraphComment")
                                {
                                    var blockText = ExtractParagraphText(blockChild);
                                    if (!string.IsNullOrWhiteSpace(blockText))
                                        summaryParts.Add(blockText.Trim());
                                }
                            }
                        }
                        break;

                    case "ParamCommandComment":
                        var paramName = GetOptionalString(commentChild, "param");
                        if (paramName != null && commentChild.TryGetProperty("inner", out var paramInner))
                        {
                            foreach (var paramChild in paramInner.EnumerateArray())
                            {
                                if (GetOptionalString(paramChild, "kind") == "ParagraphComment")
                                {
                                    var paramText = ExtractParagraphText(paramChild);
                                    if (!string.IsNullOrWhiteSpace(paramText))
                                        docParams.Add(new ObjCDocParam { Name = paramName, Description = paramText.Trim() });
                                }
                            }
                        }
                        break;
                }
            }

            var summary = summaryParts.Count > 0 ? string.Join(" ", summaryParts) : null;
            return (summary, docParams);
        }

        return (null, []);
    }

    private static string ExtractParagraphText(JsonElement paragraph)
    {
        if (!paragraph.TryGetProperty("inner", out var inner))
            return "";

        var parts = new List<string>();
        foreach (var child in inner.EnumerateArray())
        {
            if (GetOptionalString(child, "kind") == "TextComment")
            {
                var text = GetOptionalString(child, "text");
                if (text != null)
                    parts.Add(text);
            }
        }
        return string.Join("", parts);
    }

    /// <summary>
    /// Extracts the NS_SWIFT_NAME value from a declaration's inner nodes.
    /// <para>
    /// LIMITATION: on real input this returns null even for a decl that carries NS_SWIFT_NAME. Clang's
    /// <c>-ast-dump=json</c> emits a <c>SwiftNameAttr</c> child node but OMITS its string argument (it
    /// carries only <c>id</c>/<c>kind</c>/<c>range</c>) — the same JSON-vs-text gap already documented
    /// for <c>ObjCRuntimeNameAttr</c> above. The text <c>-ast-dump</c> does print the name, but this
    /// generator consumes the JSON dump, so <c>child["name"]</c> is absent and this is effectively a
    /// presence check. JSON parsing also cannot model automatic ObjC-prefix stripping. The authoritative
    /// rawObjCName → Swift-import-name mapping is recovered instead from the Swift ABI (see
    /// <c>SwiftABIParser.ObjCImportedTypeNames</c> / <c>ObjCBridgeRecordRekeyer</c>).
    /// </para>
    /// </summary>
    private static string? ExtractSwiftName(JsonElement element)
    {
        if (!element.TryGetProperty("inner", out var inner))
            return null;

        foreach (var child in inner.EnumerateArray())
        {
            if (GetOptionalString(child, "kind") == "SwiftNameAttr")
                return GetOptionalString(child, "name");
        }
        return null;
    }

    /// <summary>
    /// Checks if a declaration has the NS_REFINED_FOR_SWIFT attribute.
    /// Clang represents this as a SwiftPrivateAttr inner node.
    /// </summary>
    private static bool HasSwiftPrivateAttr(JsonElement element)
    {
        if (!element.TryGetProperty("inner", out var inner))
            return false;

        foreach (var child in inner.EnumerateArray())
        {
            if (GetOptionalString(child, "kind") == "SwiftPrivateAttr")
                return true;
        }
        return false;
    }

    // ──────────────────────────────────────────────
    // Location filtering
    // ──────────────────────────────────────────────

    /// <summary>
    /// Determines if a declaration is from the framework's public headers.
    /// Also updates currentFile tracking, since clang omits loc.file when the
    /// file hasn't changed from the previous declaration.
    /// </summary>
    internal static bool IsPublicDeclaration(JsonElement decl, string frameworkHeadersPath, ref string? currentFile)
        => IsPublicDeclaration(decl, frameworkHeadersPath, ref currentFile, out _);

    internal static bool IsPublicDeclaration(JsonElement decl, string frameworkHeadersPath, ref string? currentFile, out string? resolvedFilePath)
    {
        resolvedFilePath = null;
        if (!decl.TryGetProperty("loc", out var loc))
            return false;

        // Extract any file path from the loc fields and update tracking
        string? resolvedFile = null;

        // 1. loc.file (direct source location — updates current file)
        if (TryGetLocFile(loc, "file", out var f))
        {
            currentFile = f;
            resolvedFile = f;
        }

        // 2. loc.expansionLoc.file (macro-expanded declarations)
        if (resolvedFile == null && loc.TryGetProperty("expansionLoc", out var expLoc))
        {
            if (TryGetLocFile(expLoc, "file", out f))
            {
                currentFile = f;
                resolvedFile = f;
            }
        }

        // 3. loc.spellingLoc.file (spelling location for macro args)
        if (resolvedFile == null && loc.TryGetProperty("spellingLoc", out var spLoc))
        {
            if (TryGetLocFile(spLoc, "file", out f))
            {
                currentFile = f;
                resolvedFile = f;
            }
        }

        // 4. loc.includedFrom.file (the file that #imported this header)
        // includedFrom identifies the INCLUDING file, not the declaration's source.
        // We use it as a heuristic: if BOTH the includer AND the current file chain
        // point to framework headers, the declaration is from a sub-header (e.g.,
        // CBCentralManager.h included by CoreBluetooth.h). We additionally require
        // currentFile to be framework-local (or null) to avoid false positives when
        // a framework header #imports an SDK header — the SDK declarations get
        // includedFrom pointing to the framework header but currentFile points to
        // the SDK header (set by the first declaration in that file via step 1).
        bool hasIncludedFrom = loc.TryGetProperty("includedFrom", out var inclFrom);
        if (resolvedFile == null && hasIncludedFrom)
        {
            if (TryGetLocFile(inclFrom, "file", out f) && IsUnderPath(f, frameworkHeadersPath)
                && (currentFile == null || IsUnderPath(currentFile, frameworkHeadersPath)))
            {
                resolvedFile = f;
            }
            // If includedFrom points outside our framework, resolvedFile stays null
            // and we do NOT fall through to currentFile inheritance below.
        }

        // 5. If no file field at all and no includedFrom, inherit from previous declaration.
        // (Clang omits loc.file when consecutive declarations are in the same file.)
        if (resolvedFile == null && !hasIncludedFrom)
            resolvedFile = currentFile;

        resolvedFilePath = resolvedFile ?? currentFile;

        if (resolvedFile != null && IsUnderPath(resolvedFile, frameworkHeadersPath))
            return true;

        return false;
    }

    private static bool TryGetLocFile(JsonElement parent, string key, out string value)
    {
        value = "";
        if (parent.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? "";
            return !string.IsNullOrEmpty(value);
        }
        return false;
    }

    private static bool IsUnderPath(string filePath, string basePath)
    {
        return filePath.StartsWith(basePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks whether a header path is from an Apple SDK (Xcode SDKs, system includes).
    /// Types declared in Apple SDK headers are available in .NET iOS via framework bindings.
    /// </summary>
    internal static bool IsAppleSdkPath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        // The platform's Developer-tools frameworks (XCTest, Testing, XCUIAutomation) live at
        // <Platform>.platform/Developer/Library/Frameworks, and the platform's clang builtins at
        // <Platform>.platform/Developer/usr. These are test/tooling frameworks that are NOT part
        // of the bindable Apple SDK — Microsoft.iOS binds none of them — so they must not be
        // treated as resolvable SDK types. Otherwise a binding whose surface references one (e.g.
        // a Swift test framework whose base class is XCTestCase) would emit
        // [BaseType(typeof(XCTestCase))] and fail to compile with CS0246. The real SDK lives under
        // <Platform>.platform/Developer/SDKs/<Platform>.sdk, which the /SDKs/ check below matches.
        if (filePath.Contains("/Developer/Library/Frameworks/", StringComparison.Ordinal)
            || filePath.Contains("/Developer/usr/", StringComparison.Ordinal))
            return false;
        return filePath.Contains("/SDKs/", StringComparison.Ordinal)
            || filePath.Contains("/usr/include/", StringComparison.Ordinal)
            || filePath.Contains("/Platforms/", StringComparison.Ordinal);
    }

    internal static bool IsForwardDeclaration(JsonElement element)
    {
        // Forward declarations have no inner array, or empty inner, and no super
        if (element.TryGetProperty("inner", out var inner) && inner.GetArrayLength() > 0)
            return false;

        // If it has a superclass, it's a real definition even without inner
        if (element.TryGetProperty("super", out _))
            return false;

        return true;
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Quick pre-scan of the AST to find ObjC class names that declare lightweight generic
    /// type parameters (ObjCTypeParamDecl). These names supplement the static
    /// KnownGenericContainers set so the type ref parser can distinguish custom generic
    /// containers (MOSResults&lt;ObjectType&gt;) from protocol-qualified types (NSObject&lt;NSCopying&gt;).
    /// </summary>
    private static HashSet<string> ScanGenericContainerNames(JsonElement inner)
    {
        var result = new HashSet<string>();
        foreach (var node in inner.EnumerateArray())
        {
            if (GetOptionalString(node, "kind") != "ObjCInterfaceDecl")
                continue;
            if (!node.TryGetProperty("inner", out var children))
                continue;
            foreach (var child in children.EnumerateArray())
            {
                if (GetOptionalString(child, "kind") == "ObjCTypeParamDecl")
                {
                    var className = GetName(node);
                    if (className != null)
                        result.Add(className);
                    break; // One type param is enough to know it's generic
                }
            }
        }
        return result;
    }

    private static string? GetName(JsonElement element)
    {
        return GetOptionalString(element, "name");
    }

    private static string? GetOptionalString(JsonElement element, string key)
    {
        if (element.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static string? GetQualType(JsonElement element)
    {
        if (element.TryGetProperty("type", out var typeProp) &&
            typeProp.TryGetProperty("qualType", out var qt))
        {
            return qt.GetString();
        }
        return null;
    }

    private static string? GetReturnType(JsonElement element)
    {
        if (element.TryGetProperty("returnType", out var rt) &&
            rt.TryGetProperty("qualType", out var qt))
        {
            return qt.GetString();
        }
        return null;
    }

    private static string ParseFunctionReturnType(string funcTypeStr)
    {
        // Function types in clang AST look like "void (int, float)"
        // Extract the return type (everything before the first '(')
        var parenIdx = funcTypeStr.IndexOf('(');
        if (parenIdx > 0)
            return funcTypeStr[..parenIdx].Trim();
        return funcTypeStr;
    }

    private static List<T> DeduplicateByRichest<T>(
        List<T> items, Func<T, string> nameSelector, Func<T, int> richnessSelector)
    {
        if (items.Count <= 1) return items;
        return items.GroupBy(nameSelector)
            .Select(g => g.OrderByDescending(richnessSelector).First())
            .ToList();
    }

    /// <summary>
    /// Like <see cref="DeduplicateByRichest{T}"/>, but also merges availability from every same-named
    /// duplicate onto the kept (richest) instance — the same fidelity the class/protocol/category and
    /// function/constant merge paths provide. A sparser duplicate (e.g. a forward enum declaration) can
    /// carry the availability macro while the richer definition (more cases) does not, so keeping the
    /// richest must not drop that annotation. Returns the richest unchanged when no duplicate adds
    /// anything.
    /// </summary>
    private static List<T> DeduplicateByRichestMergingAvailability<T>(
        List<T> items,
        Func<T, string> nameSelector,
        Func<T, int> richnessSelector,
        Func<T, List<ObjCAvailability>> availabilityGetter,
        Func<T, List<ObjCAvailability>, T> withAvailability)
    {
        if (items.Count <= 1) return items;
        return items.GroupBy(nameSelector).Select(g =>
        {
            var richest = g.OrderByDescending(richnessSelector).First();
            if (g.Count() == 1) return richest;

            var merged = new List<ObjCAvailability>(availabilityGetter(richest));
            var changed = false;
            foreach (var dup in g)
            {
                if (ReferenceEquals(dup, richest)) continue;
                var before = merged.Count;
                MergeAvailabilityInto(merged, availabilityGetter(dup));
                if (merged.Count != before) changed = true;
            }
            return changed ? withAvailability(richest, merged) : richest;
        }).ToList();
    }

    /// <summary>
    /// Drops "deprecated-subclass alias" classes — Apple's rename pattern where the legacy spelling
    /// becomes a fully-deprecated subclass of the canonical class (e.g. Matter's
    /// <c>MTROtaSoftware…</c> subclassing <c>MTROTASoftware…</c>, or
    /// <c>MTRTimeSynchronizationClusterSetUtcTimeParams</c> subclassing the all-caps UTC variant).
    /// Signal: subclass and superclass share the same name except for letter casing. Records each
    /// dropped name into <paramref name="droppedAliasNames"/> so the category filter can also drop
    /// categories that target the alias.
    /// </summary>
    private static List<ObjCClassDecl> DropDeprecatedSubclassAliases(List<ObjCClassDecl> classes, HashSet<string> droppedAliasNames, Dictionary<string, string> aliasToCanonical)
    {
        if (classes.Count == 0) return classes;
        var classNames = new HashSet<string>(classes.Select(c => c.Name));
        var kept = new List<ObjCClassDecl>(classes.Count);
        foreach (var c in classes)
        {
            if (IsDeprecatedSubclassAlias(c, classNames))
            {
                droppedAliasNames.Add(c.Name);
                if (c.SuperclassName != null)
                    aliasToCanonical[c.Name] = c.SuperclassName;
            }
            else
                kept.Add(c);
        }
        return kept;
    }

    private static bool IsDeprecatedSubclassAlias(ObjCClassDecl cls, HashSet<string> moduleClassNames)
    {
        if (cls.SuperclassName == null) return false;
        if (!moduleClassNames.Contains(cls.SuperclassName)) return false;
        if (string.Equals(cls.Name, cls.SuperclassName, StringComparison.Ordinal)) return false;
        return string.Equals(cls.Name, cls.SuperclassName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deduplicates classes by name, merging metadata from all duplicates onto the richest instance.
    /// Metadata includes: SuperclassName, ProtocolNames, GenericTypeParamNames.
    /// NOTE: Methods/properties are NOT merged across duplicates — only the richest instance's
    /// members are kept. In practice, duplicate declarations of the same class come from the same
    /// header definition (re-included via umbrella headers), so they have identical members.
    /// Disjoint members only arise from categories, which are handled in Pass 2 before dedup.
    /// </summary>
    private static List<ObjCClassDecl> MergeClasses(List<ObjCClassDecl> classes)
    {
        if (classes.Count <= 1) return classes;
        return classes.GroupBy(c => c.Name)
            .Select(g =>
            {
                var richest = g.OrderByDescending(c => c.Methods.Count + c.Properties.Count).First();
                if (g.Count() == 1) return richest;

                // Merge metadata from all duplicates
                string? superclass = richest.SuperclassName;
                var allProtocols = new HashSet<string>(richest.ProtocolNames);
                var allGenericParams = new HashSet<string>(richest.GenericTypeParamNames);
                var allAvailability = new List<ObjCAvailability>(richest.Availability);
                // objc_runtime_name can appear on any duplicate declaration (it is not tied to the
                // richest-by-member-count one). OR it across all duplicates so the native-symbol
                // guard never false-drops a runtime-renamed class just because the attribute landed
                // on a sparser decl.
                var hasCustomRuntimeName = g.Any(c => c.HasCustomRuntimeName);

                foreach (var dup in g)
                {
                    if (ReferenceEquals(dup, richest)) continue;
                    superclass ??= dup.SuperclassName;
                    foreach (var p in dup.ProtocolNames) allProtocols.Add(p);
                    foreach (var gp in dup.GenericTypeParamNames) allGenericParams.Add(gp);
                    MergeAvailabilityInto(allAvailability, dup.Availability);
                }

                return richest with
                {
                    SuperclassName = superclass,
                    ProtocolNames = allProtocols.ToList(),
                    GenericTypeParamNames = allGenericParams.ToList(),
                    HasCustomRuntimeName = hasCustomRuntimeName,
                    Availability = allAvailability
                };
            })
            .ToList();
    }

    /// <summary>
    /// Deduplicates protocols by name, merging metadata from all duplicates onto the richest instance.
    /// Metadata includes: InheritedProtocolNames.
    /// NOTE: Methods/properties are NOT merged — same rationale as MergeClasses.
    /// </summary>
    private static List<ObjCProtocolDecl> MergeProtocols(List<ObjCProtocolDecl> protocols)
    {
        if (protocols.Count <= 1) return protocols;
        return protocols.GroupBy(p => p.Name)
            .Select(g =>
            {
                var richest = g.OrderByDescending(p => p.Methods.Count + p.Properties.Count).First();
                if (g.Count() == 1) return richest;

                var allInherited = new HashSet<string>(richest.InheritedProtocolNames);
                var allAvailability = new List<ObjCAvailability>(richest.Availability);

                foreach (var dup in g)
                {
                    if (ReferenceEquals(dup, richest)) continue;
                    foreach (var ip in dup.InheritedProtocolNames) allInherited.Add(ip);
                    MergeAvailabilityInto(allAvailability, dup.Availability);
                }

                return richest with
                {
                    InheritedProtocolNames = allInherited.ToList(),
                    Availability = allAvailability
                };
            })
            .ToList();
    }

    /// <summary>
    /// Deduplicates categories by (ClassName, CategoryName), merging members from all duplicates
    /// onto the richest instance (most methods+properties). Same pattern as MergeClasses.
    /// </summary>
    private static List<ObjCCategoryDecl> MergeCategories(List<ObjCCategoryDecl> categories)
    {
        if (categories.Count <= 1) return categories;
        return categories.GroupBy(c => (c.ClassName, c.CategoryName))
            .Select(g =>
            {
                var richest = g.OrderByDescending(c => c.Methods.Count + c.Properties.Count).First();
                if (g.Count() == 1) return richest;

                var allProtocols = new HashSet<string>(richest.ProtocolNames);
                var allMethodSelectors = new HashSet<string>(richest.Methods.Select(m => m.Selector));
                var allMethods = new List<ObjCMethodDecl>(richest.Methods);
                var allPropertyNames = new HashSet<string>(richest.Properties.Select(p => p.Name));
                var allProperties = new List<ObjCPropertyDecl>(richest.Properties);
                var allAvailability = new List<ObjCAvailability>(richest.Availability);

                foreach (var dup in g)
                {
                    if (ReferenceEquals(dup, richest)) continue;
                    foreach (var p in dup.ProtocolNames) allProtocols.Add(p);
                    foreach (var m in dup.Methods)
                    {
                        if (allMethodSelectors.Add(m.Selector))
                            allMethods.Add(m);
                    }
                    foreach (var p in dup.Properties)
                    {
                        if (allPropertyNames.Add(p.Name))
                            allProperties.Add(p);
                    }
                    MergeAvailabilityInto(allAvailability, dup.Availability);
                }

                return richest with
                {
                    ProtocolNames = allProtocols.ToList(),
                    Methods = allMethods,
                    Properties = allProperties,
                    Availability = allAvailability
                };
            })
            .ToList();
    }

    /// <summary>
    /// Appends availability records from <paramref name="source"/> into <paramref name="target"/>,
    /// skipping records that duplicate one already present (same platform + introduced + deprecated).
    /// Used when merging duplicate declarations so an availability annotation that landed on a
    /// sparser duplicate is preserved on the merged decl.
    /// </summary>
    private static void MergeAvailabilityInto(List<ObjCAvailability> target, List<ObjCAvailability> source)
    {
        foreach (var a in source)
        {
            if (!target.Any(existing =>
                existing.Platform == a.Platform
                && existing.IntroducedVersion == a.IntroducedVersion
                && existing.DeprecatedVersion == a.DeprecatedVersion
                && existing.ObsoletedVersion == a.ObsoletedVersion
                && existing.IsUnavailable == a.IsUnavailable))
            {
                target.Add(a);
            }
        }
    }

    private static List<T> DeduplicateByFirst<T>(
        List<T> items, Func<T, string> nameSelector)
    {
        if (items.Count <= 1) return items;
        return items.GroupBy(nameSelector).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// Like <see cref="DeduplicateByFirst{T}"/>, but preserves availability that landed on a duplicate
    /// other than the first: keeps the first decl's shape/identity and merges availability from every
    /// same-named duplicate into it (via <see cref="MergeAvailabilityInto"/>). Handles the
    /// forward-declare-then-redeclare-with-availability header shape where the FIRST decl is bare and a
    /// LATER one carries the macro. Returns the first decl unchanged when no duplicate adds anything.
    /// </summary>
    private static List<T> DeduplicateByFirstMergingAvailability<T>(
        List<T> items,
        Func<T, string> nameSelector,
        Func<T, List<ObjCAvailability>> availabilityGetter,
        Func<T, List<ObjCAvailability>, T> withAvailability)
    {
        if (items.Count <= 1) return items;
        return items.GroupBy(nameSelector).Select(g =>
        {
            var first = g.First();
            var merged = new List<ObjCAvailability>(availabilityGetter(first));
            var changed = false;
            foreach (var dup in g)
            {
                if (ReferenceEquals(dup, first)) continue;
                var before = merged.Count;
                MergeAvailabilityInto(merged, availabilityGetter(dup));
                if (merged.Count != before) changed = true;
            }
            return changed ? withAvailability(first, merged) : first;
        }).ToList();
    }

    private static long? TryExtractEnumValue(JsonElement innerArray)
    {
        foreach (var child in innerArray.EnumerateArray())
        {
            var kind = GetOptionalString(child, "kind");

            // ConstantExpr wraps the value — always has the evaluated result
            if (kind == "ConstantExpr")
            {
                if (child.TryGetProperty("value", out var valProp))
                {
                    var valStr = valProp.GetString();
                    if (valStr != null && TryParseIntegerValue(valStr, out var val))
                        return val;
                }
                // Recurse into ConstantExpr's inner
                if (child.TryGetProperty("inner", out var ceInner))
                    return TryExtractEnumValue(ceInner);
            }

            // IntegerLiteral is the leaf node containing the actual value
            if (kind == "IntegerLiteral")
            {
                if (child.TryGetProperty("value", out var valProp))
                {
                    var valStr = valProp.GetString();
                    if (valStr != null && TryParseIntegerValue(valStr, out var val))
                        return val;
                }
            }

            // ImplicitCastExpr / ExplicitCastExpr / ParenExpr — transparent wrappers,
            // recurse into their inner children to find the actual value node
            if (kind is "ImplicitCastExpr" or "ExplicitCastExpr" or "ParenExpr"
                or "CStyleCastExpr")
            {
                if (child.TryGetProperty("inner", out var wrapperInner))
                {
                    var result = TryExtractEnumValue(wrapperInner);
                    if (result.HasValue)
                        return result;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Parses an integer value string that may be decimal, hex (0x/0X prefix),
    /// octal (0 prefix), or negative.
    /// </summary>
    private static bool TryParseIntegerValue(string value, out long result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value))
            return false;

        // Handle negative values
        var isNegative = false;
        var toParse = value;
        if (toParse.StartsWith('-'))
        {
            isNegative = true;
            toParse = toParse[1..];
        }

        bool parsed;
        if (toParse.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            // Hex literal — parse as ulong first to handle high-bit values
            // (e.g., 0xFFFFFFFF80000000) that exceed long.MaxValue, then
            // use unchecked cast to preserve the bit pattern in a long.
            parsed = ulong.TryParse(toParse[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var ulongResult);
            if (parsed)
                result = unchecked((long)ulongResult);
        }
        else
        {
            // Decimal (or octal — clang typically evaluates these to decimal in the value field)
            parsed = long.TryParse(toParse, out result);
        }

        if (parsed && isNegative)
            result = -result;

        return parsed;
    }
}
