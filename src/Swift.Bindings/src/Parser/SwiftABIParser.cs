// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using BindingsGeneration.Demangling;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

[assembly: InternalsVisibleTo("Swift.Bindings.Unit.Tests")]

namespace BindingsGeneration
{
    /// <summary>
    /// Typed node-level reconciliation of a module parse (Finding 14a). Every ABI declaration node
    /// the parser visits lands in exactly one bucket, so the invariant
    /// <c>Parsed == Emitted + SkippedWithReason + DroppedWithError</c> holds. This is the parser
    /// analog of the emission skip-attribution work: it turns the previously invisible
    /// <see cref="SwiftABIParser.HandleNode"/> swallow channel (a bug under a TypeDecl silently
    /// deletes the whole type) into a durable count surfaced on the artifact manifest, so a
    /// regression that drops declarations shows up as a number instead of greener logs.
    /// </summary>
    /// <param name="Parsed">Total declaration nodes the parser attempted (the sum of the others).</param>
    /// <param name="Emitted">Nodes that produced a declaration.</param>
    /// <param name="SkippedWithReason">Nodes deliberately not bound (e.g. imports, an unsupported
    /// declaration kind, or a recognized-but-unbound kind such as <c>AssociatedType</c>/<c>OperatorDecl</c>)
    /// — a handler returned null without throwing.</param>
    /// <param name="DroppedWithError">Nodes lost without being bound — the silent-failure channel this
    /// finding exists to expose. Two sources land here: an unrecognized node kind off the dispatch
    /// allowlist (SWIFTBIND034, which also records an AbiJson degradation), and a caught exception in
    /// <c>HandleNode</c> (an unimplemented or faulted binder). Every increment records a structured
    /// <see cref="IngestionLedgerEntry"/> through <c>RecordDropLedger</c>, so no drop is silent. A
    /// bindable type declaration MISSING its load-bearing mangled name is a distinct case — it is NOT
    /// dropped but QUARANTINED (kept in the tree, withheld from the type database, tombstoned at
    /// emission with its proven dependent closure), so it counts under <see cref="Emitted"/> with a
    /// quarantine ledger entry, not here.</param>
    /// <param name="UnknownNodeKinds">Finding 45 (ingestion contract): per-kind census of ABI node
    /// kinds that fell through the dispatch allowlist — declarations the digester emitted that the
    /// parser does not recognize at all (as opposed to recognized-but-unbound kinds such as
    /// <c>AssociatedType</c>/<c>OperatorDecl</c>, which are deliberate skips and never counted here).
    /// A non-empty census means the digester shape drifted past what the parser knows; it is the
    /// allowlist breach surfaced as a number-by-kind instead of a generic dropped-with-error tally.
    /// Null when no unknown kind was seen (the steady state).</param>
    public sealed record ParseReconciliation(
        int Parsed,
        int Emitted,
        int SkippedWithReason,
        int DroppedWithError,
        IReadOnlyDictionary<string, int>? UnknownNodeKinds = null)
    {
        /// <summary>True when the buckets sum to the total — always expected to hold.</summary>
        public bool IsBalanced => Parsed == Emitted + SkippedWithReason + DroppedWithError;
    }

    /// <summary>
    /// Represents the result of parsing a module.
    /// </summary>
    /// <param name="ModuleDecl">The module declaration.</param>
    /// <param name="TypeDecls">The type declarations.</param>
    /// <param name="Reconciliation">Node-level parse reconciliation counts (Finding 14a).</param>
    public sealed record ModuleParsingResult(
        ModuleDecl ModuleDecl,
        Dictionary<NamedTypeSpec, TypeDecl> TypeDecls,
        ParseReconciliation Reconciliation);

    /// <summary>
    /// Represents the root node of the ABI.
    /// </summary>
    public record ABIRootNode
    {
        public required RootNode ABIRoot { get; set; }
    }

    /// <summary>
    /// Represents the root node of a module.
    /// </summary>
    public record RootNode
    {
        public required string Kind { get; set; }
        public required string Name { get; set; }
        public required string PrintedName { get; set; }

        /// <summary>
        /// Finding 45 (ingestion contract): the swift-api-digester schema version stamped on every
        /// ABI JSON (currently 8 — including the project's own shipped supplement artifacts). Read
        /// nowhere historically; <see cref="SwiftABIParser.ParseModule"/> now gates it loudly so a
        /// digester output-shape change is observable (and fail-closed under <c>--strict-inputs</c>)
        /// instead of silently mis-parsed. Nullable so absence is distinguishable from a real value.
        /// </summary>
        public int? json_format_version { get; set; }

        public required IEnumerable<Node> Children { get; set; } = Enumerable.Empty<Node>();
    }

    /// <summary>
    /// Represents a node.
    /// </summary>
    public record Node
    {
        public required string Kind { get; set; }
        public required string DeclKind { get; set; }
        public required string Name { get; set; }
        public required string MangledName { get; set; }
        public required string PrintedName { get; set; }
        public required string ModuleName { get; set; }
        public required string[] DeclAttributes { get; set; }
        public required bool? @static { get; set; }
        public required bool? IsInternal { get; set; }
        public required string? GenericSig { get; set; }
        public required string? sugared_genericSig { get; set; }
        public required bool? throwing { get; set; }
        public required string? AccessorKind { get; set; }
        public required string? EnumRawTypeName { get; set; }
        public required string? paramValueOwnership { get; set; }
        public required bool? hasDefaultArg { get; set; }
        public bool? overriding { get; set; }
        // Reference-ownership qualifier on a stored property (Swift's ReferenceOwnership enum):
        // absent/0 = strong, 1 = weak, 2 = unowned, 3 = unowned(unsafe). Both ABI producers the
        // repo consumes — swift-frontend -emit-abi-descriptor-path and swift-api-digester
        // -dump-sdk — emit this key with identical spelling and values, alongside a
        // "ReferenceOwnership" entry in declAttributes.
        public int? ownership { get; set; }
        public bool? @implicit { get; set; }
        public bool? isFromExtension { get; set; }
        public string? funcSelfKind { get; set; }
        public string? usr { get; set; }
        // swift-api-digester sets this on a node whose declaration is NOT owned by the module
        // being dumped — a re-export stub the digester materializes only because the module
        // references or extends the foreign declaration. Load-bearing for ABI-completeness
        // judgements: a foreign stub is a pointer to another module's record, not this module's
        // own record, so an ABI field that is absent on it says nothing about digester drift.
        public bool? isExternal { get; set; }
        public string? superclassUsr { get; set; }
        public string[]? superclassNames { get; set; }
        public bool? inheritsConvenienceInitializers { get; set; }
        public bool? hasMissingDesignatedInitializers { get; set; }
        public bool? protocolReq { get; set; }
        public string[]? typeAttributes { get; set; }
        public string[]? spi_group_names { get; set; }
        // Accessor-level introduced version fields from swift-api-digester.
        // Properties whose setters are restricted to a newer platform version emit
        // these on the `set` accessor child (e.g., WorkoutKit.PowerThresholdAlert.metric
        // setter is iOS 17.4 while the property itself is iOS 17.0). Reading them lets
        // the Swift wrapper generator emit matching @available attributes so the cdecl
        // setter doesn't reference an API requiring a newer OS than its annotation.
        public string? intro_iOS { get; set; }
        public string? intro_Macosx { get; set; }
        public string? intro_tvOS { get; set; }
        public string? intro_watchOS { get; set; }
        public string? intro_visionOS { get; set; }
        public string? intro_macCatalyst { get; set; }
        public required IEnumerable<Node> Children { get; set; } = Enumerable.Empty<Node>();
        public required IEnumerable<Node> Conformances { get; set; } = Enumerable.Empty<Node>();
        public required IEnumerable<Node> Accessors { get; set; } = Enumerable.Empty<Node>();
    }

    /// <summary>
    /// Represents a parser for Swift ABI.
    /// </summary>
    public sealed class SwiftABIParser : ISwiftParser
    {
        const string kNominal = "TypeNominal";
        const string kFunc = "TypeFunc";
        const string kTuple = "Tuple";
        const string kGenericTypeParam = "GenericTypeParam";

        // Swift operator characters per Swift Language Reference §Lexical Structure.
        // Operators are built from: / = - + ! * % < > & | ^ ~ ? .
        private static readonly HashSet<char> _operatorChars = new()
        {
            '/', '=', '-', '+', '!', '*', '%', '<', '>', '&', '|', '^', '~', '?', '.'
        };

        /// <summary>
        /// The ABI file path.
        /// </summary>
        private readonly string _filePath;

        /// <summary>
        /// The type database.
        /// </summary>
        private readonly ITypeDatabase _typeDatabase;

        /// <summary>
        /// The demangled TBD.
        /// </summary>
        private readonly DemanglingResults _demangledTbd;

        /// <summary>
        /// Resolves cross-module facts (nominal ownership / foreign-type shape, metadata-accessor
        /// symbols, protocol-conformance descriptors). Defaults to
        /// <see cref="LegacyCrossModuleFactResolver"/> — the order-sensitive combination of
        /// <see cref="_demangledTbd"/> + <see cref="_typeDatabase"/> the parser used before the seam
        /// existed — so a parse constructed via the public constructor is byte-identical to the
        /// pre-seam generator. A graph-wide index-backed resolver can be injected via the internal
        /// constructor to make those facts order-independent.
        /// </summary>
        private readonly ICrossModuleFactResolver _resolver;


        /// <summary>
        /// Logger instance.
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// The module root node.
        /// </summary>
        private readonly ABIRootNode _moduleRoot;

        /// <summary>
        /// Types declared in the module.
        /// </summary>
        private readonly Dictionary<NamedTypeSpec, TypeDecl> _moduleTypes = new();

        /// <summary>
        /// Finding 14a: node-level parse reconciliation counters, accumulated at the single
        /// <see cref="HandleNode"/> chokepoint and packaged into <see cref="ParseReconciliation"/>
        /// by <see cref="ParseModule"/>. Counts the whole declaration tree (HandleTypeDecl recurses
        /// through CollectDeclarations → HandleNode for nested types and members).
        /// </summary>
        private int _nodesSeen;
        private int _nodesEmitted;
        private int _nodesSkippedWithReason;
        private int _nodesDroppedWithError;

        /// <summary>
        /// Finding 45 (ingestion contract): per-kind tally of ABI node kinds that fell through the
        /// <see cref="HandleNode"/> dispatch allowlist — i.e. kinds the parser has never seen. Built
        /// only by the <c>default</c> arm, so recognized-but-unbound kinds (<c>Import</c>,
        /// <c>AssociatedType</c>, <c>OperatorDecl</c>) never appear here; a non-empty census is an
        /// allowlist breach that means the digester emitted a declaration shape we don't model.
        /// </summary>
        private readonly Dictionary<string, int> _unknownNodeKinds = new(StringComparer.Ordinal);

        /// <summary>
        /// Finding 45: the swift-api-digester <c>json_format_version</c> this parser is calibrated
        /// against. A mismatch (or absence) is surfaced loudly and recorded as an
        /// <see cref="InputResolutionCategory.AbiJson"/> degradation so <c>--strict-inputs</c> can
        /// fail the generation rather than silently mis-parse a drifted output shape.
        /// </summary>
        /// <remarks>
        /// Finding 58: the literal lives in <see cref="SupportedToolchain.ExpectedAbiFormatVersion"/>
        /// (the single owner of the tested toolchain envelope); this is a forwarding constant so the
        /// digester format version and the supported-matrix can never disagree.
        /// </remarks>
        internal const int ExpectedAbiFormatVersion = SupportedToolchain.ExpectedAbiFormatVersion;

        /// <summary>
        /// Finding 58 (ABI-JSON node-kind golden, amendment C): the committed vocabulary of node
        /// <c>Kind</c> strings the <see cref="HandleNode"/> dispatch switch actually models — every
        /// recognized case, whether it is bound (<c>TypeDecl</c>/<c>Function</c>/<c>Constructor</c>/
        /// <c>Var</c>/<c>Subscript</c>) or recognized-and-skipped (<c>Import</c>/<c>AssociatedType</c>/
        /// <c>OperatorDecl</c>). Anything outside this set falls to the switch's <c>default</c> arm and is
        /// censused + warned as <c>SWIFTBIND034</c> (the runtime arm — this set does not duplicate it).
        /// This set is the compile-time guard: <c>AbiIngestionContractTests</c> asserts every member is
        /// recognized (never lands in the unknown census), so teaching the parser a new kind is a
        /// deliberate, test-pinned update — mirroring the clang path's
        /// <c>ClangAstParser.KnownTopLevelNodeKinds</c>. Keep in lockstep with the
        /// <c>switch (node.Kind)</c> in <see cref="HandleNode"/>.
        /// </summary>
        internal static readonly IReadOnlySet<string> KnownAbiNodeKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            // Bound into the model:
            "TypeDecl", "Function", "Constructor", "Var", "Subscript",
            // Recognized and deliberately skipped (bound elsewhere / not a bindable member):
            "Import", "AssociatedType", "OperatorDecl",
        };

        /// <summary>
        /// TypeWitness mappings from conformance entries.
        /// Populated during HandleConformance, assigned to ModuleDecl at end of ParseModule.
        /// </summary>
        private readonly ConformanceGraph _conformanceGraph = new();

        /// <summary>
        /// Per-method capture for synthetic generic parameters introduced to lower
        /// parameter-position opaque types (<c>some P</c>). Non-null only while
        /// <see cref="CreateMethodDecl"/> is iterating a method's parameter children.
        /// <see cref="CreateTypeSpec"/> appends to this list when it encounters an
        /// opaque parameter; <see cref="CreateMethodDecl"/> then merges the captured
        /// entries into the resulting <see cref="MethodDecl.GenericParameters"/>.
        /// Saved/restored around each method so nested parses don't interfere.
        /// </summary>
        private List<GenericArgumentDecl>? _opaqueParamCapture;

        /// <summary>
        /// The Swift demangler.
        /// </summary>
        private readonly Swift5Demangler demangler = new();

        /// <summary>
        /// Maps a raw Objective-C declaration name (the identifier in a Clang USR, e.g.
        /// <c>MGreeter</c> from <c>c:objc(cs)MGreeter</c>) to the Swift-import name the ABI uses
        /// for it (the last component of the reference node's <c>printedName</c>, e.g. <c>Greeter</c>
        /// from <c>M.Greeter</c>). Populated as <see cref="CreateTypeSpec"/> visits every type
        /// reference — the ABI carries both halves on each ObjC-imported nominal: the Swift-facing
        /// name in <c>printedName</c> and the raw ObjC identity in <c>usr</c>. This is the only
        /// reliable source of an ObjC type's Swift-import name: Clang's <c>-ast-dump=json</c> omits
        /// the string argument of <c>SwiftNameAttr</c>, and the mapping must account for automatic
        /// prefix stripping as well as explicit <c>NS_SWIFT_NAME</c>, which only the Swift compiler
        /// resolves. Consumed by the mixed ObjC+Swift bridge to re-key ObjC type-resolution records
        /// (synthesized under the raw ObjC name) to the name a Swift member actually references.
        /// </summary>
        private readonly Dictionary<string, string> _objcImportedTypeNames = new(StringComparer.Ordinal);

        /// <summary>
        /// Raw Objective-C name → Swift-import name for every ObjC-imported type the Swift ABI
        /// references. See <see cref="_objcImportedTypeNames"/>.
        /// </summary>
        public IReadOnlyDictionary<string, string> ObjCImportedTypeNames => _objcImportedTypeNames;

        /// <summary>
        /// Determines if a declaration node represents a module-internal declaration
        /// that is ABI-visible but not accessible from external Swift code.
        /// Detection layers:
        /// 1. node.IsInternal == true (explicit ABI JSON flag)
        /// 2. "UsableFromInline" in declAttributes (always means internal — @usableFromInline is only
        ///    used on internal declarations, regardless of whether AccessControl is also present)
        /// 3. "Inlinable" WITHOUT "AccessControl" (means @inlinable internal with implicit access)
        /// 4. "SPIAccessControl" in declAttributes (@_spi types — only visible to SPI consumers,
        ///    not part of the public API surface)
        /// 5. Supplementary swiftinterface data for @inlinable internal WITH AccessControl
        ///    (handled separately via SwiftInterfaceFacts.InternalMemberKeys)
        /// </summary>
        private bool IsNodeModuleInternal(Node node)
        {
            if (node.IsInternal == true)
                return true;

            if (node.DeclAttributes is null)
                return false;

            bool hasUsableFromInline = Array.IndexOf(node.DeclAttributes, "UsableFromInline") != -1;

            // @usableFromInline is exclusively used on internal declarations.
            // It means "this internal member has ABI stability requirements for inlining."
            if (hasUsableFromInline)
                return true;

            bool hasInlinable = Array.IndexOf(node.DeclAttributes, "Inlinable") != -1;
            bool hasAccessControl = Array.IndexOf(node.DeclAttributes, "AccessControl") != -1;

            // @inlinable without an AccessControl attribute is an AMBIGUOUS signal, not a
            // reliable internal marker. Some toolchains record an explicit `public` keyword as
            // an AccessControl declAttribute (so "@inlinable & no AccessControl" implied the
            // default `internal`), but others emit ONLY [Inlinable] for an `@inlinable public`
            // member — making this heuristic mis-flag public inlinable members (e.g. a
            // re-exported `@inlinable public init` with all-default SIMD parameters) as internal,
            // which then drops their @_cdecl wrapper. When a public swiftinterface is available,
            // IsInternalFromSwiftInterface + negative-space detection (IsInternalFromPublicMemberNames)
            // resolve the access level authoritatively, so the guess must NOT pre-empt them.
            // Only fall back to the guess when no swiftinterface is present to consult.
            if (hasInlinable && !hasAccessControl && _facts.PublicMemberNames.Count == 0)
                return true;

            // @_spi types are only visible to SPI consumers (e.g., other modules in the same SPI group).
            // They are not part of the public API and should not appear in generated bindings.
            // Check both declAttributes and spi_group_names (different Swift compiler versions
            // use one or the other).
            if (Array.IndexOf(node.DeclAttributes, "SPIAccessControl") != -1)
                return true;
            if (node.spi_group_names is not null && node.spi_group_names.Length > 0)
                return true;

            return false;
        }

        /// <summary>
        /// Returns true if the node has @_spi protection.
        /// Checks both "SPIAccessControl" in declAttributes and the presence of spi_group_names.
        /// Some Swift compiler versions emit one or the other depending on how @_spi is applied
        /// (e.g., on the member directly vs. inherited from an @_spi extension).
        /// </summary>
        private static bool IsNodeSpiProtected(Node node)
        {
            if (node.DeclAttributes is not null &&
                Array.IndexOf(node.DeclAttributes, "SPIAccessControl") != -1)
                return true;

            return node.spi_group_names is not null && node.spi_group_names.Length > 0;
        }

        /// <summary>
        /// Sets actor isolation flags on a type declaration based on swiftinterface data.
        /// </summary>
        private void ApplyActorIsolation(TypeDecl typeDecl)
        {
            var qualifiedPath = BuildTypeQualifiedPath(typeDecl);

            if (_facts.MainActorTypes.Contains(qualifiedPath))
                typeDecl.IsMainActorIsolated = true;

            if (_facts.CustomActorTypes.Contains(qualifiedPath))
                typeDecl.IsCustomActor = true;

            if (_facts.CustomActorIsolatorMap.TryGetValue(qualifiedPath, out var isolatorName))
            {
                typeDecl.IsCustomActorIsolated = true;
                typeDecl.CustomActorIsolatorName = isolatorName;
            }
        }

        /// <summary>
        /// Sets actor isolation flags on a method declaration based on swiftinterface data.
        /// Uses qualified type path + PrintedName (e.g., "Outer.Inner.foo(_:bar:)")
        /// to distinguish overloads and avoid nested-type name collisions.
        /// </summary>
        private void ApplyMemberActorIsolation(MethodDecl methodDecl, TypeDecl parentTypeDecl, string printedName)
        {
            var qualifiedPath = BuildTypeQualifiedPath(parentTypeDecl);
            var key = $"{qualifiedPath}.{printedName}";
            var shortKey = $"{parentTypeDecl.Name}.{printedName}";

            if (_facts.ActorIsolatedMembers.Contains(key))
                methodDecl.IsActorIsolated = true;
            else if (shortKey != key && _facts.ActorIsolatedMembers.Contains(shortKey))
                methodDecl.IsActorIsolated = true;

            // Set @MainActor-specific flag (subset of IsActorIsolated)
            if (_facts.MainActorIsolatedMembers.Contains(key) ||
                (shortKey != key && _facts.MainActorIsolatedMembers.Contains(shortKey)))
                methodDecl.IsMainActorIsolated = true;

            if (_facts.NonisolatedMembers.Contains(key))
                methodDecl.IsNonisolated = true;
        }

        /// <summary>
        /// Sets actor isolation flags on a property declaration based on swiftinterface data.
        /// Uses qualified type path to avoid nested-type name collisions.
        /// </summary>
        private void ApplyPropertyActorIsolation(PropertyDecl propertyDecl, TypeDecl parentTypeDecl)
        {
            var qualifiedPath = BuildTypeQualifiedPath(parentTypeDecl);
            var key = $"{qualifiedPath}.{propertyDecl.Name}";
            var shortKey = $"{parentTypeDecl.Name}.{propertyDecl.Name}";

            if (_facts.ActorIsolatedMembers.Contains(key))
                propertyDecl.IsActorIsolated = true;
            else if (shortKey != key && _facts.ActorIsolatedMembers.Contains(shortKey))
                propertyDecl.IsActorIsolated = true;

            // Set @MainActor-specific flag (subset of IsActorIsolated)
            if (_facts.MainActorIsolatedMembers.Contains(key) ||
                (shortKey != key && _facts.MainActorIsolatedMembers.Contains(shortKey)))
                propertyDecl.IsMainActorIsolated = true;

            if (_facts.NonisolatedMembers.Contains(key))
                propertyDecl.IsNonisolated = true;
        }

        /// <summary>
        /// Sets <see cref="TypeDecl.ObjCRuntimeName"/> when the swiftinterface declared an
        /// explicit <c>@objc(CustomName)</c> rename for this type. Left null otherwise (the ObjC
        /// runtime name then equals the Swift name). Sourced from
        /// <see cref="SwiftInterfaceFacts.ObjCRuntimeNames"/>, keyed on the same qualified type
        /// path as the actor-isolation facts. Consumed by the swift-types.json ownership manifest
        /// so mixed-framework dedup can match the ObjC <c>@interface</c> the Swift type owns.
        /// </summary>
        private void ApplyObjCRuntimeName(TypeDecl typeDecl)
        {
            var qualifiedPath = BuildTypeQualifiedPath(typeDecl);
            if (_facts.ObjCRuntimeNames.TryGetValue(qualifiedPath, out var objcName))
                typeDecl.ObjCRuntimeName = objcName;
        }

        /// <summary>
        /// Sets availability annotations on a type declaration from swiftinterface data.
        /// </summary>
        private void ApplyAvailability(TypeDecl typeDecl)
        {
            var qualifiedPath = BuildTypeQualifiedPath(typeDecl);
            if (_facts.AvailabilityAnnotations.TryGetValue(qualifiedPath, out var annotations))
                typeDecl.AvailabilityAnnotations = annotations;
        }

        /// <summary>
        /// Sets availability annotations on a member declaration from swiftinterface data.
        /// When <paramref name="signatureNode"/> is provided, the lookup first tries the
        /// disamb-suffixed key <c>"{TypePath}.{printedName}|sig"</c> before falling back
        /// to the bare key. This is how Family-F-1 / Family-F-4 are resolved without
        /// requiring a separate ambiguous-key set: producers store overloads under their
        /// disamb keys whenever 2+ overloads share a printedName, and the bare key is
        /// LEFT EMPTY so an unmatched bare-key lookup safely returns nothing rather
        /// than misapplying another overload's annotations.
        /// </summary>
        private void ApplyMemberAvailability(BaseDecl decl, TypeDecl parentTypeDecl, string printedName, Node? signatureNode = null)
        {
            var bareKey = $"{BuildTypeQualifiedPath(parentTypeDecl)}.{printedName}";
            if (signatureNode != null)
            {
                var sig = ComputeAbiParamSignature(signatureNode);
                if (!string.IsNullOrEmpty(sig))
                {
                    var disambKey = MemberSignatureNormalizer.ComposeKey(bareKey, sig);
                    if (_facts.AvailabilityAnnotations.TryGetValue(disambKey, out var disambAnnotations))
                    {
                        decl.AvailabilityAnnotations = disambAnnotations;
                        return;
                    }
                }
            }
            if (_facts.AvailabilityAnnotations.TryGetValue(bareKey, out var annotations))
                decl.AvailabilityAnnotations = annotations;
        }

        /// <summary>
        /// Computes the same parameter-type signature the swiftinterface parser does,
        /// but reading from an ABI JSON function/init/subscript node instead of source
        /// text. <paramref name="node"/>'s <c>Children</c> contain the parameters
        /// (skipping index 0 — that's the return type for funcs/inits; subscripts also
        /// place the return type at index 0). Each child's <c>printedName</c> is
        /// normalized through <see cref="MemberSignatureNormalizer.NormalizeParamType"/>
        /// so it matches the producer-side normalization. Returns an empty string when
        /// the node has no parameter children.
        /// <para/>
        /// Exposed to the test assembly via <c>InternalsVisibleTo</c> (same precedent as
        /// <see cref="MergeAccessorAvailability"/>) so the ABI consumer side of the
        /// availability disamb signature can be asserted byte-equal to
        /// <see cref="SwiftSyntaxInterfaceFactsProducer"/>'s output without staging a
        /// full ABI-JSON fixture. The index-0 return-type skip and the per-child
        /// <c>printedName</c> normalization are the consumer-specific behavior (Finding 46).
        /// </summary>
        internal static string ComputeAbiParamSignature(Node node)
        {
            if (node.Children == null) return string.Empty;
            var raw = new List<string>();
            int i = 0;
            foreach (var child in node.Children)
            {
                // Index 0 of a Function/Constructor/Subscript Children list is the
                // return type; the parser-side signature only lists parameters.
                if (i++ == 0) continue;
                raw.Add(child.PrintedName ?? string.Empty);
            }
            return MemberSignatureNormalizer.BuildSignature(raw);
        }

        /// <summary>
        /// Sets the best-effort source position on a type declaration from swiftinterface
        /// data. Tries the qualified type path against the qualified-path-keyed maps first
        /// (<c>@MainActor</c>, <c>@available</c>). Convention-c is keyed by short protocol
        /// name, so the short-name fallback is restricted to <see cref="ProtocolDecl"/> and
        /// looks up <em>only</em> <see cref="SwiftInterfaceFacts.ConventionCProtocolPositions"/>
        /// — never the qualified-path maps. That prevents a nested <c>Outer.Foo</c> from
        /// latching onto a top-level <c>Foo</c> entry via short-name collision. Leaves
        /// <see cref="BaseDecl.Position"/> null when no map has a hit — best-effort, no
        /// fabricated positions.
        /// </summary>
        private void ApplyPosition(TypeDecl typeDecl)
        {
            var qualifiedPath = BuildTypeQualifiedPath(typeDecl);
            if (_facts.MainActorTypePositions.TryGetValue(qualifiedPath, out var pos) ||
                _facts.AvailabilityAnnotationPositions.TryGetValue(qualifiedPath, out pos))
            {
                typeDecl.Position = pos;
                return;
            }
            if (typeDecl is ProtocolDecl &&
                _facts.ConventionCProtocolPositions.TryGetValue(typeDecl.Name, out pos))
            {
                typeDecl.Position = pos;
            }
        }

        /// <summary>
        /// Sets the best-effort source position on a member declaration from swiftinterface
        /// data. Uses the same <c>TypePath.printedName</c> key as
        /// <see cref="ApplyMemberAvailability"/>.
        /// </summary>
        private void ApplyMemberPosition(BaseDecl decl, TypeDecl parentTypeDecl, string printedName)
        {
            var key = $"{BuildTypeQualifiedPath(parentTypeDecl)}.{printedName}";
            var pos = _facts.TryGetPosition(key);
            if (pos is { } p)
                decl.Position = p;
        }

        /// <summary>
        /// Reads accessor-level introduced-version fields from an ABI JSON accessor node
        /// and returns them as AvailabilityAnnotation entries. Returns null when the accessor
        /// has no tighter version than its parent property.
        /// </summary>
        private static List<AvailabilityAnnotation>? ExtractAccessorAvailability(Node accessorNode)
        {
            List<AvailabilityAnnotation>? result = null;
            void Add(string? platform, string? version)
            {
                if (string.IsNullOrEmpty(platform) || string.IsNullOrEmpty(version))
                    return;
                result ??= new List<AvailabilityAnnotation>();
                result.Add(new AvailabilityAnnotation(platform, version, null, null, false, false, null, null));
            }
            Add("iOS", accessorNode.intro_iOS);
            Add("macOS", accessorNode.intro_Macosx);
            Add("tvOS", accessorNode.intro_tvOS);
            Add("watchOS", accessorNode.intro_watchOS);
            Add("visionOS", accessorNode.intro_visionOS);
            Add("macCatalyst", accessorNode.intro_macCatalyst);
            return result;
        }

        /// <summary>
        /// Merges property-level availability with accessor-specific availability from the
        /// ABI JSON. For each platform the accessor tightens, the accessor's introduced version
        /// replaces the property-level introduced version; other platforms keep the property's
        /// record untouched. Returns null if neither source has any entries. Exposed to the test
        /// assembly via <c>InternalsVisibleTo</c> so setter-availability merging can be
        /// unit-tested directly without staging a full ABI-JSON fixture.
        ///
        /// <para>An accessor node carries ONLY <c>intro_*</c> fields — the ABI descriptor has no
        /// per-accessor deprecation, obsoletion, message or unavailability — so an accessor record
        /// overrides the introduced version alone and inherits the rest of the property's record
        /// for that platform. Replacing the whole record would silently drop a property's
        /// deprecation/obsoletion off the setter, so a property deprecated at iOS 18 whose setter
        /// arrived at iOS 17 would emit a setter with no <c>[ObsoletedOSPlatform]</c> and no
        /// deprecation message while the getter kept both.</para>
        /// </summary>
        internal static List<AvailabilityAnnotation>? MergeAccessorAvailability(
            IReadOnlyList<AvailabilityAnnotation>? propertyAvailability,
            List<AvailabilityAnnotation>? accessorAvailability)
        {
            if ((propertyAvailability == null || propertyAvailability.Count == 0) &&
                (accessorAvailability == null || accessorAvailability.Count == 0))
                return null;

            var merged = new Dictionary<string, AvailabilityAnnotation>(StringComparer.Ordinal);
            var passthroughs = new List<AvailabilityAnnotation>();
            if (propertyAvailability != null)
            {
                foreach (var ann in propertyAvailability)
                {
                    if (ann.Platform != null && ann.IntroducedVersion != null)
                        merged[ann.Platform] = ann;
                    else
                        passthroughs.Add(ann);
                }
            }
            if (accessorAvailability != null)
            {
                foreach (var ann in accessorAvailability)
                {
                    if (ann.Platform == null || ann.IntroducedVersion == null)
                        continue;
                    // Override the introduced version only; everything else on that platform is a
                    // property-level fact the accessor node cannot restate.
                    merged[ann.Platform] = merged.TryGetValue(ann.Platform, out var propertyAnn)
                        ? propertyAnn with { IntroducedVersion = ann.IntroducedVersion }
                        : ann;
                }
            }

            var result = new List<AvailabilityAnnotation>(merged.Values);
            result.AddRange(passthroughs);
            return result;
        }

        /// <summary>
        /// Applies default parameter value expressions from swiftinterface data to a method's arguments.
        /// Must be called AFTER all ArgumentDecl instances have been added to CSSignature.
        /// </summary>
        private void ApplyMemberDefaultValues(MethodDecl methodDecl, TypeDecl parentTypeDecl, string printedName)
        {
            var key = $"{BuildTypeQualifiedPath(parentTypeDecl)}.{printedName}";
            if (!_facts.DefaultParameterValues.TryGetValue(key, out var defaultValues))
                return;
            // Apply to arguments (skip i=0, the return type)
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var argIdx = i - 1;
                if (argIdx < defaultValues.Count && methodDecl.CSSignature[i].HasDefaultArg)
                    methodDecl.CSSignature[i].SwiftDefaultExpression = defaultValues[argIdx];
            }
        }

        /// <summary>
        /// Applies default parameter values for free functions (module-level, not inside a type).
        /// Uses the bare printedName as key (matching the swiftinterface parser's output for top-level funcs).
        /// </summary>
        private void ApplyFreeFunctionDefaultValues(MethodDecl methodDecl, string printedName)
        {
            if (!_facts.DefaultParameterValues.TryGetValue(printedName, out var defaultValues))
                return;
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var argIdx = i - 1;
                if (argIdx < defaultValues.Count && methodDecl.CSSignature[i].HasDefaultArg)
                    methodDecl.CSSignature[i].SwiftDefaultExpression = defaultValues[argIdx];
            }
        }

        /// <summary>
        /// Applies @autoclosure flags from swiftinterface data to closure parameters.
        /// Sets the "autoclosure" attribute on ClosureTypeSpec parameters so that wrapper
        /// emitters can add "()" when forwarding autoclosure arguments.
        /// </summary>
        private void ApplyMemberAutoclosureFlags(MethodDecl methodDecl, TypeDecl parentTypeDecl, string printedName)
        {
            var key = $"{BuildTypeQualifiedPath(parentTypeDecl)}.{printedName}";
            if (!_facts.AutoclosureParameters.TryGetValue(key, out var flags))
                return;
            ApplyAutoclosureFlagsToSignature(methodDecl, flags);
        }

        private void ApplyFreeFunctionAutoclosureFlags(MethodDecl methodDecl, string printedName)
        {
            if (!_facts.AutoclosureParameters.TryGetValue(printedName, out var flags))
                return;
            ApplyAutoclosureFlagsToSignature(methodDecl, flags);
        }

        private static void ApplyAutoclosureFlagsToSignature(MethodDecl methodDecl, List<bool> flags)
        {
            // CSSignature[0] is the return type, [1..] are parameters
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var argIdx = i - 1;
                if (argIdx < flags.Count && flags[argIdx] &&
                    methodDecl.CSSignature[i].SwiftTypeSpec is ClosureTypeSpec closureSpec)
                {
                    closureSpec.Attributes.Add(new TypeSpecAttribute("autoclosure"));
                }
            }
        }

        /// <summary>
        /// Applies <c>_const</c> parameter flags from swiftinterface data to method parameters.
        /// The runtime @_cdecl wrapper passes runtime values; Swift rejects const-literal
        /// parameter calls with "expect a compile-time constant literal", so downstream
        /// emitters consult <see cref="ArgumentDecl.IsConstLiteral"/> to skip wrapper
        /// emission for the affected member. The annotation lives in the swiftinterface
        /// only — ABI JSON strips it.
        /// </summary>
        private void ApplyMemberConstLiteralFlags(MethodDecl methodDecl, TypeDecl parentTypeDecl, string printedName)
        {
            var key = $"{BuildTypeQualifiedPath(parentTypeDecl)}.{printedName}";
            if (!_facts.ConstLiteralParameters.TryGetValue(key, out var flags))
                return;
            ApplyConstLiteralFlagsToSignature(methodDecl, flags);
        }

        private void ApplyFreeFunctionConstLiteralFlags(MethodDecl methodDecl, string printedName)
        {
            if (!_facts.ConstLiteralParameters.TryGetValue(printedName, out var flags))
                return;
            ApplyConstLiteralFlagsToSignature(methodDecl, flags);
        }

        private static void ApplyConstLiteralFlagsToSignature(MethodDecl methodDecl, List<bool> flags)
        {
            // CSSignature[0] is the return type, [1..] are parameters.
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var argIdx = i - 1;
                if (argIdx < flags.Count && flags[argIdx])
                    methodDecl.CSSignature[i].IsConstLiteral = true;
            }
        }

        /// <summary>
        /// Applies per-parameter closure type-level attributes (<c>@MainActor</c>,
        /// <c>@Sendable</c>) from swiftinterface data onto closure parameters of a protocol
        /// requirement. swift-api-digester strips these attributes from the ABI JSON (the
        /// closure's <c>printedName</c> collapses to <c>() -&gt; ()</c>), so the swiftinterface
        /// is the only source. They matter only for protocol-requirement signature matching:
        /// the synthesized <c>extension EveryProtocol: SomeProtocol</c> conformance must
        /// reproduce the requirement's exact closure type, or the conformance fails to compile.
        /// Restricted to protocol parents — non-protocol method wrappers define their own
        /// <c>@_cdecl</c> signatures and don't need (and could be perturbed by) these attributes.
        /// </summary>
        private void ApplyMemberClosureAttributeFlags(MethodDecl methodDecl, TypeDecl parentTypeDecl, string printedName)
        {
            var key = $"{BuildTypeQualifiedPath(parentTypeDecl)}.{printedName}";
            if (!_facts.ClosureParameterAttributes.TryGetValue(key, out var perParam))
                return;
            ApplyClosureAttributeFlagsToSignature(methodDecl, perParam);
        }

        private static void ApplyClosureAttributeFlagsToSignature(MethodDecl methodDecl, List<List<string>> perParam)
        {
            // CSSignature[0] is the return type, [1..] are parameters.
            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var argIdx = i - 1;
                if (argIdx < perParam.Count &&
                    methodDecl.CSSignature[i].SwiftTypeSpec is ClosureTypeSpec closureSpec)
                {
                    foreach (var attrName in perParam[argIdx])
                    {
                        if (!closureSpec.Attributes.Exists(a => a.Name == attrName))
                            closureSpec.Attributes.Add(new TypeSpecAttribute(attrName));
                    }
                }
            }
        }

        /// <summary>
        /// Checks if a member is unconditionally unavailable from swiftinterface availability annotations.
        /// Only returns true for truly unconditional `@available(*, unavailable)` annotations. A
        /// per-platform form like `@available(watchOS, unavailable)` parses into an annotation with
        /// <c>Platform != null</c> and must NOT suppress members when the binding target is iOS, etc.
        /// </summary>
        private bool IsUnavailableFromSwiftInterface(TypeDecl parentTypeDecl, string printedName)
        {
            var key = $"{BuildTypeQualifiedPath(parentTypeDecl)}.{printedName}";
            return _facts.AvailabilityAnnotations.TryGetValue(key, out var annotations)
                && annotations.Any(a => a.IsUnconditionallyUnavailable && a.Platform == null);
        }

        /// <summary>
        /// Checks if a type is unconditionally unavailable from swiftinterface availability annotations.
        /// Only returns true for truly unconditional `@available(*, unavailable)` annotations (see
        /// <see cref="IsUnavailableFromSwiftInterface"/> for rationale).
        /// </summary>
        private bool IsTypeUnavailableFromSwiftInterface(TypeDecl typeDecl)
        {
            var key = BuildTypeQualifiedPath(typeDecl);
            return _facts.AvailabilityAnnotations.TryGetValue(key, out var annotations)
                && annotations.Any(a => a.IsUnconditionallyUnavailable && a.Platform == null);
        }

        /// <summary>
        /// Checks if a type is internal based on the public type names set from swiftinterface.
        /// Returns true if the set is available, non-empty, and the type is NOT in it.
        /// </summary>
        private bool IsInternalFromPublicTypeNames(TypeDecl typeDecl)
        {
            if (_facts.PublicTypeNames.Count == 0)
                return false;

            var qualifiedPath = BuildTypeQualifiedPath(typeDecl);
            return !_facts.PublicTypeNames.Contains(qualifiedPath);
        }

        /// <summary>
        /// Checks if a member is marked as internal in the supplementary swiftinterface data.
        /// This catches @inlinable internal members with AccessControl in declAttributes,
        /// which are indistinguishable from @inlinable public in ABI JSON alone.
        ///
        /// Overload disambiguation: swiftinterface keys use only "TypeName.printedName" (no
        /// parameter types), so a type with both internal and public overloads sharing a
        /// printed name lands the same key in both sets. Example: StoreKit's
        /// `Product.PurchaseOption.custom(key:value:)` has one `@usableFromInline internal`
        /// `BackingValue` overload plus four public overloads; the key appears in both sets.
        ///
        /// Disambiguate via the ABI node's own <c>Inlinable</c> DeclAttribute. When the key
        /// appears in both internal and public swiftinterface sets, a node reaching here is an
        /// <c>@inlinable internal</c> / <c>@usableFromInline internal</c> overload colliding with
        /// a public overload of the same printed name. A node WITHOUT <c>Inlinable</c> cannot be
        /// the inlinable-internal one, so it must be the plain-<c>public</c> overload and is safely
        /// marked public. A node WITH <c>Inlinable</c> could be either the inlinable-internal
        /// overload or an <c>@inlinable public</c> overload — stay conservative and keep it internal
        /// (this is the only path where an <c>@inlinable public</c> member is still suppressed; it
        /// requires a same-named internal overload to land the key in <c>InternalMemberKeys</c>).
        /// Note: when a public swiftinterface is present this set drives access resolution —
        /// <see cref="IsNodeModuleInternal"/>'s "@inlinable without AccessControl" guess is gated
        /// OFF in that case (it is a fallback for the no-swiftinterface path only).
        /// </summary>
        private bool IsInternalFromSwiftInterface(string parentTypeName, string printedName, Node? node)
        {
            if (_facts.InternalMemberKeys.Count == 0)
                return false;

            var key = $"{parentTypeName}.{printedName}";
            if (!_facts.InternalMemberKeys.Contains(key))
                return false;

            // Both internal and public swiftinterface sets contain this key. If the ABI node
            // itself lacks the Inlinable attribute, it cannot be the @inlinable internal /
            // @usableFromInline internal overload, so it must be a plain-public overload — defer
            // to public.
            if (_facts.PublicMemberNames.Contains(key) && node != null)
            {
                bool nodeHasInlinable = node.DeclAttributes != null &&
                    Array.IndexOf(node.DeclAttributes, "Inlinable") != -1;
                if (!nodeHasInlinable)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Checks if a member is internal by negative-space detection: if the public
        /// swiftinterface has a set of public member names, any ABI member NOT in that
        /// set is internal. For type members, the key is "TypeName.printedName".
        /// For module-level functions, the key is the bare printedName.
        /// </summary>
        private bool IsInternalFromPublicMemberNames(BaseDecl parentDecl, string printedName, bool isCurrentModuleMember = false)
        {
            if (_facts.PublicMemberNames.Count == 0)
                return false;

            if (parentDecl is TypeDecl typeDecl)
            {
                // Skip types that are themselves internal — their members are already
                // suppressed along with the type, so classifying them is moot. The one
                // exception is a member DEFINED IN THIS MODULE on an internal-flagged
                // receiver: a foreign receiver type (e.g. Foundation.Date) is flagged
                // internal here only because it is absent from this module's public type
                // set, yet its current-module extension members ARE emitted via the
                // cross-module trampoline. Those must still be negative-space classified
                // against the public member set, or an internal extension member leaks
                // into a client-compiled wrapper that cannot resolve it. A genuinely
                // public extension member is keyed in PublicMemberNames ("Date.foo()"), so
                // running the check here does not over-suppress it.
                if (typeDecl.IsModuleInternal && !isCurrentModuleMember)
                    return false;

                var key = $"{typeDecl.Name}.{printedName}";
                return !_facts.PublicMemberNames.Contains(key);
            }

            // Module-level (free functions/variables): bare printedName
            return !_facts.PublicMemberNames.Contains(printedName);
        }

        /// <summary>
        /// Aggregate of every supplementary fact extracted from the public swiftinterface.
        /// Replaces 21 individually-threaded nullable side-channel maps with a single
        /// drift-loud hand-off. Always non-null; use <see cref="SwiftInterfaceFacts.Empty"/>
        /// when no swiftinterface is available.
        /// </summary>
        private readonly SwiftInterfaceFacts _facts;

        /// <summary>
        /// Optional doc comments from symbol graph, keyed by USR. Sourced separately from
        /// the swiftinterface, so it is not part of <see cref="_facts"/>.
        /// </summary>
        private readonly Dictionary<string, DocComment>? _docComments;

        public SwiftABIParser(
            string filePath,
            ITypeDatabase typeDatabase,
            DemanglingResults demangledTbd,
            ILogger logger,
            SwiftInterfaceFacts facts,
            Dictionary<string, DocComment>? docComments = null)
            : this(filePath, typeDatabase, demangledTbd, logger, facts,
                   new LegacyCrossModuleFactResolver(typeDatabase, demangledTbd), docComments)
        {
        }

        /// <summary>
        /// Internal constructor that injects the <see cref="ICrossModuleFactResolver"/>. The public
        /// constructor delegates here with a <see cref="LegacyCrossModuleFactResolver"/> (the
        /// order-sensitive baseline); the two-phase orchestration injects a graph-wide index-backed
        /// resolver so cross-module facts resolve against the whole graph instead of whatever
        /// happened to be loaded first. Injecting the resolver is an internal migration detail — no
        /// CLI surface exposes it.
        /// </summary>
        internal SwiftABIParser(
            string filePath,
            ITypeDatabase typeDatabase,
            DemanglingResults demangledTbd,
            ILogger logger,
            SwiftInterfaceFacts facts,
            ICrossModuleFactResolver crossModuleResolver,
            // No default: an optional trailing param here would make the internal 7-arg ctor and the
            // public 6-arg ctor (whose trailing param is a nullable Dictionary) both applicable to a
            // same-assembly `new SwiftABIParser(..., facts, null)` call — a CS0121 ambiguity. Requiring
            // docComments explicitly keeps a 6-arg call bound solely to the public ctor.
            Dictionary<string, DocComment>? docComments)
        {
            _filePath = filePath;
            _typeDatabase = typeDatabase;
            _demangledTbd = demangledTbd;
            _logger = logger;
            _facts = facts;
            _resolver = crossModuleResolver;
            _docComments = docComments;

            string jsonContent = File.ReadAllText(_filePath);
            _moduleRoot = JsonConvert.DeserializeObject<ABIRootNode>(jsonContent) ?? throw new InvalidOperationException("Invalid ABI structure.");
        }

        /// <summary>
        /// True when the parsed ABI carries zero top-level declaration children — an empty
        /// shim module (e.g. a re-export-only or namespace-only dependency). Distinct from a
        /// malformed ABI, which fails to deserialize in the constructor and throws before this
        /// can ever be read. An empty shim contributes no types and its <see cref="GetModuleName"/>
        /// would throw on the empty child set, so callers skip it rather than hard-failing.
        /// </summary>
        public bool HasNoDeclChildren => !_moduleRoot.ABIRoot.Children.Any();

        /// <summary>
        /// Cache for <see cref="CurrentModuleName"/>. The module name is fixed for the
        /// lifetime of the parser instance (one abi.json = one module), so resolve it once.
        /// </summary>
        private string? _cachedModuleName;

        /// <summary>
        /// The name of the module this parser is parsing. Used to distinguish members
        /// defined in THIS module (including extension members on a foreign receiver) from
        /// members owned by a foreign re-exported type.
        /// </summary>
        private string CurrentModuleName => _cachedModuleName ??= GetModuleName();

        /// <summary>
        /// Gets the module name.
        /// </summary>
        /// <returns>The module name.</returns>
        public string GetModuleName()
        {
            // Pick the first child whose ModuleName is a real Swift module.
            // swift-api-digester may emit compiler-internal TypeAlias children
            // (__NSConstantString, __builtin_va_list) with moduleName="__ObjC"
            // at the front of the children list when a framework @_exports
            // itself (e.g. ActivityKit). The old logic grabbed Children[0]
            // unconditionally and produced "__ObjC" as the module name.
            var moduleName = _moduleRoot.ABIRoot.Children
                .Select(c => c.ModuleName)
                .FirstOrDefault(n => !string.IsNullOrEmpty(n) && n != "__ObjC")
                ?? _moduleRoot.ABIRoot.Children.FirstOrDefault()?.ModuleName
                ?? string.Empty;

            if (string.IsNullOrEmpty(moduleName) || moduleName == "NO_MODULE")
            {
                throw new InvalidOperationException(
                    $"ABI JSON has invalid module name '{moduleName}'. " +
                    "The Swift library must be compiled with BUILD_LIBRARY_FOR_DISTRIBUTION=YES " +
                    "(swiftc -enable-library-evolution) to produce valid ABI metadata.");
            }

            return moduleName;
        }

        /// <summary>
        /// Processes the module ABI. Processes all declarations and builds the ModuleDecl.
        /// </summary>
        /// <returns>The module ABI processing result.</returns>
        public ModuleParsingResult ParseModule()
        {
            var dependencies = new List<string>();
            var moduleName = GetModuleName();
            GateAbiFormatVersion(moduleName);
            var moduleDecl = new ModuleDecl
            {
                Name = ExtractUniqueName(moduleName),
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Dependencies = dependencies,
                Protocols = new List<ProtocolDecl>(),
                ParentDecl = null,
                ModuleDecl = null
            };

            var decls = CollectDeclarations(_moduleRoot.ABIRoot.Children, moduleDecl, moduleDecl);

            dependencies.Remove(moduleName);

            moduleDecl.Properties = decls.OfType<PropertyDecl>().ToList();
            moduleDecl.Methods = decls.OfType<MethodDecl>().ToList();
            moduleDecl.Types = decls.OfType<TypeDecl>().ToList();
            moduleDecl.Dependencies = dependencies;
            moduleDecl.Protocols = decls.OfType<ProtocolDecl>().ToList();

            foreach (var type in moduleDecl.Types)
            {
                _moduleTypes.TryAdd(new NamedTypeSpec(type.SwiftTypeName.ModuleQualifiedName), type);
            }

            moduleDecl.ConformanceGraph = _conformanceGraph;

            var reconciliation = new ParseReconciliation(
                Parsed: _nodesSeen,
                Emitted: _nodesEmitted,
                SkippedWithReason: _nodesSkippedWithReason,
                DroppedWithError: _nodesDroppedWithError,
                UnknownNodeKinds: _unknownNodeKinds.Count == 0
                    ? null
                    : new Dictionary<string, int>(_unknownNodeKinds, StringComparer.Ordinal));

            return new ModuleParsingResult(moduleDecl, _moduleTypes, reconciliation);
        }

        /// <summary>
        /// Finding 45 (ingestion contract): gate the ABI JSON <c>json_format_version</c> loudly. The
        /// digester stamps it on every artifact (currently <see cref="ExpectedAbiFormatVersion"/>);
        /// historically nothing read it, so a digester output-shape change would have been absorbed
        /// silently as mis-parsed declarations. A present-and-matching version is recorded as an
        /// informational input-resolution decision; absence or a mismatch is both warned
        /// (SWIFTBIND033) and recorded as an <see cref="InputResolutionCategory.AbiJson"/> degradation
        /// so <c>--strict-inputs</c> escalates it to a hard failure instead of binding against an
        /// input shape the parser was never calibrated for.
        /// </summary>
        private void GateAbiFormatVersion(string moduleName)
        {
            var version = _moduleRoot.ABIRoot.json_format_version;
            if (version is null)
            {
                _logger.LogWarning(
                    "SWIFTBIND033: ABI JSON for module '{Module}' carries no json_format_version; the "
                    + "ingestion contract (expected v{Expected}) cannot be verified.",
                    moduleName, ExpectedAbiFormatVersion);
                InputResolutionReport.RecordDegradation(
                    InputResolutionCategory.AbiJson,
                    $"module '{moduleName}': ABI JSON has no json_format_version (expected {ExpectedAbiFormatVersion})");
            }
            else if (version.Value != ExpectedAbiFormatVersion)
            {
                _logger.LogWarning(
                    "SWIFTBIND033: ABI JSON for module '{Module}' declares json_format_version {Actual}, "
                    + "but this generator is calibrated against v{Expected}; ingestion may mis-parse.",
                    moduleName, version.Value, ExpectedAbiFormatVersion);
                InputResolutionReport.RecordDegradation(
                    InputResolutionCategory.AbiJson,
                    $"module '{moduleName}': ABI JSON json_format_version {version.Value} != expected {ExpectedAbiFormatVersion}");
            }
            else
            {
                InputResolutionReport.RecordInfo(
                    InputResolutionCategory.AbiJson,
                    $"module '{moduleName}': ABI JSON json_format_version {version.Value}");
            }
        }

        /// <summary>
        /// Collects declarations from a list of nodes.
        /// </summary>
        /// <param name="nodes">The list of nodes to collect declarations from.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The list of collected declarations.</returns>
        private List<BaseDecl> CollectDeclarations(IEnumerable<Node> nodes, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var declarations = new List<BaseDecl>();
            foreach (var node in nodes)
            {
                var nodeDeclaration = HandleNode(node, parentDecl, moduleDecl);
                if (nodeDeclaration is not null)
                    declarations.Add(nodeDeclaration);
            }
            return declarations;
        }

        /// <summary>
        /// The stable ingestion identity of one ABI node: module + kind + the USR (or, absent a USR,
        /// the mangled name, or a sentinel). Two malformed nodes never collapse onto one identity, and
        /// the identity survives even when the very field that was lost is the mangled name.
        /// </summary>
        private static IngestionInputIdentity IdentityOf(Node node)
        {
            var symbol = !string.IsNullOrEmpty(node.usr)
                ? node.usr!
                : !string.IsNullOrEmpty(node.MangledName)
                    ? node.MangledName
                    : IngestionInputIdentity.AbsentSymbol;
            var kind = !string.IsNullOrEmpty(node.DeclKind) ? node.DeclKind : node.Kind;
            return new IngestionInputIdentity(node.ModuleName ?? string.Empty, kind, symbol);
        }

        /// <summary>The coarse ingestion identity of a node's declaring parent, for ledger context.</summary>
        private static IngestionInputIdentity? ParentIdentityOf(BaseDecl? parentDecl)
        {
            switch (parentDecl)
            {
                case null:
                    return null;
                case ModuleDecl m:
                    return new IngestionInputIdentity(m.Name, "Module", m.Name);
                case TypeDecl t:
                    {
                        var qualified = t.SwiftTypeName.ModuleQualifiedName;
                        var firstDot = qualified.IndexOf('.');
                        var module = firstDot < 0 ? qualified : qualified.Substring(0, firstDot);
                        return new IngestionInputIdentity(module, "Type", qualified);
                    }
                default:
                    return new IngestionInputIdentity(
                        parentDecl.ModuleDecl?.Name ?? string.Empty, "Decl", parentDecl.Name);
            }
        }

        /// <summary>
        /// Records one structured ledger entry for a node dropped through the legacy fail-open channel
        /// (unknown kind / unhandled shape / parse fault / absent field): the drop is reported and
        /// generation continues, which <c>--strict-inputs</c> escalates to fatal. Every
        /// <c>DroppedWithError</c> census increment routes through here, so no parser loss is silent.
        /// </summary>
        private void RecordDropLedger(Node node, BaseDecl? parentDecl, IngestionCause cause, string evidence) =>
            InputResolutionReport.RecordLedgerEntry(new IngestionLedgerEntry(
                Input: IdentityOf(node),
                Parent: ParentIdentityOf(parentDecl),
                Plane: IngestionPlane.Ingest,
                Cause: cause,
                Referenced: null,
                Disposition: IngestionDisposition.ReportOnly,
                ClosureEvidence: evidence,
                Status: IngestionStatus.Dropped));

        /// <summary>
        /// Handles an ABI node and returns the corresponding declaration.
        /// </summary>
        /// <param name="node">The node representing a declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The declaration.</returns>
        private BaseDecl? HandleNode(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            BaseDecl? result = null;
            bool droppedWithError = false;
            try
            {
                switch (node.Kind)
                {
                    case "TypeDecl":
                        result = HandleTypeDecl(node, parentDecl, moduleDecl);
                        break;
                    case "Function":
                    case "Constructor":
                        if (IsOperator(node.Name))
                            result = CreateOperatorDecl(node, parentDecl, moduleDecl);
                        else
                            result = CreateMethodDecl(node, parentDecl, moduleDecl);
                        break;
                    case "Var":
                        // Check if this is an enum element (enum case)
                        if (node.DeclKind == "EnumElement")
                            result = CreateEnumCaseDecl(node, parentDecl, moduleDecl);
                        else
                            result = CreatePropertyDecl(node, parentDecl, moduleDecl);
                        break;
                    case "Subscript":
                        result = CreateSubscriptDecl(node, parentDecl, moduleDecl);
                        break;
                    case "Import":
                        break;
                    case "AssociatedType":
                        // Finding 45: associated types are consumed structurally as truth in
                        // CreateProtocolDecl (which reads the protocol node's AssociatedType children
                        // directly). Recognizing the kind here turns the member-walk's previously
                        // error-dropped visit into a deliberate skip — it is bound elsewhere, not lost
                        // — so it no longer pollutes the dropped-with-error channel as an allowlist miss.
                        break;
                    case "OperatorDecl":
                        // Finding 45: a standalone operator *declaration* (fixity/precedence) is not a
                        // bindable member; the operator's backing function arrives separately as a
                        // Function node routed through CreateOperatorDecl. Recognized-and-skipped, not
                        // an unknown kind.
                        break;
                    default:
                        // Finding 45 (ingestion contract): the dispatch allowlist above is the set of
                        // ABI node kinds the parser models. Anything else is a digester shape the
                        // parser has never seen — count it by kind (the census), warn loudly, and
                        // record an input degradation so --strict-inputs fails the generation rather
                        // than silently dropping a declaration the digester newly emits.
                        _unknownNodeKinds[node.Kind] = _unknownNodeKinds.GetValueOrDefault(node.Kind) + 1;
                        droppedWithError = true;
                        _logger.LogWarning(
                            "SWIFTBIND034: unrecognized ABI node kind '{Kind}' (name '{Name}', mangled "
                            + "'{Mangled}') is not in the ingestion allowlist; the declaration is dropped. "
                            + "If the digester now emits this kind, the parser must learn it.",
                            node.Kind, node.Name, node.MangledName);
                        InputResolutionReport.RecordDegradation(
                            InputResolutionCategory.AbiJson,
                            $"unrecognized ABI node kind '{node.Kind}' (e.g. '{node.Name}') dropped");
                        RecordDropLedger(
                            node, parentDecl, IngestionCause.UnrecognizedNodeKind,
                            $"ABI node kind '{node.Kind}' is not in the parser's dispatch allowlist; the "
                            + "declaration was dropped and generation continued with a smaller surface.");
                        break;
                }
            }
            catch (NotImplementedException e)
            {
                droppedWithError = true;
                _logger.LogWarning($"Not implemented '{node.Name}' ({node.MangledName}): {e.Message}");
                RecordDropLedger(
                    node, parentDecl, IngestionCause.UnhandledDeclaration,
                    $"the parser has no binder implemented for this declaration shape ({e.Message}); it "
                    + "was dropped and generation continued.");
            }
            catch (Exception e)
            {
                droppedWithError = true;
                _logger.LogWarning($"Error while processing node '{node.Name} ({node.MangledName})': {e.Message}");
                RecordDropLedger(
                    node, parentDecl, IngestionCause.ParseFault,
                    $"an unclassified fault escaped the per-declaration binder ({e.Message}); it was "
                    + "dropped and generation continued.");
            }

            // Finding 14a: classify this node's outcome into exactly one reconciliation bucket. A
            // caught exception is the silent-failure channel (dropped-with-error); a null result with
            // no exception is a deliberate handler skip; a non-null result was emitted.
            _nodesSeen++;
            if (droppedWithError)
                _nodesDroppedWithError++;
            else if (result is not null)
                _nodesEmitted++;
            else
                _nodesSkippedWithReason++;

            return result;
        }

        /// <summary>
        /// Handles a type declaration node and returns the corresponding declaration.
        /// </summary>
        /// <param name="node">The node representing a type declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The type declaration.</returns>
        private TypeDecl? HandleTypeDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            // Foreign-receiver handling. A node whose ModuleName differs from the module
            // being parsed falls into one of three buckets, decided in this order:
            //
            //   1. Has current-module extension children (a child node carries
            //      ModuleName == moduleDecl.Name) AND the receiver is a Class or Struct
            //      → this is `extension ForeignModule.ForeignType { ... }`. Keep it and
            //      route through CrossModuleExtensionEmitter — regardless of whether the
            //      source module is third-party or Apple (e.g., RealityKit → RealityFoundation.AccessibilityComponent.RotorType).
            //      The children-first ordering is load-bearing: prior to it, an Apple
            //      framework registered under `concreteClassFallback` (e.g. RealityFoundation)
            //      would short-circuit through the system-re-export path below and end up
            //      mis-qualified as `RealityKit.AccessibilityComponent`.
            //
            //   2. No current-module extension children AND the source module is in the
            //      system-re-export keep-list (Swift stdlib, ObjC runtime modules, and
            //      common Apple feature frameworks via apple-frameworks.json's autoBridge /
            //      optionalFallback / unsupported sets) → keep with a moduleName override
            //      so canonical names like `Foundation.URL` flow through normally.
            //
            //   3. Otherwise → pure third-party re-export. Drop the node.
            //      Example: a module that re-exports a type from another module with no extension
            //      children of its own — it would mis-claim ownership of the type if kept.
            // Set below for a foreign-module node that carries extension members contributed by the
            // module currently being parsed. Hoisted out of the foreign-node block because the
            // malformed-record gate further down needs it: a foreign node must keep being WALKED
            // when it hosts current-module extension members, since that walk is what attaches them.
            bool hasCurrentModuleExtensionChildren = false;
            if (!string.IsNullOrEmpty(node.ModuleName) &&
                !string.IsNullOrEmpty(moduleDecl.Name) &&
                node.ModuleName != moduleDecl.Name)
            {
                // Runtime-provided Swift stdlib generics (Optional / Array / Dictionary / Set /
                // Result / ClosedRange) are supplied by Swift.Runtime as Swift.SwiftOptional<T>,
                // Swift.SwiftArray<T>, etc. A third-party module that merely extends or re-exports
                // one of them must NEVER materialize a local C# type for it: the emitter would
                // render a colliding `public [static] partial class SwiftOptional<TWrapped>` that
                // shadows the runtime type and breaks every unqualified `SwiftOptional<...>`
                // reference in the generated assembly. The constraint-bearing extension that
                // surfaces these nodes (e.g. `extension Swift.Optional : CustomStringConvertible
                // where Wrapped == <unrepresentable>`) carries no members we can bind anyway, so
                // drop the node outright rather than letting it reach any type-emission handler.
                if (node.ModuleName == "Swift" && TypeDatabaseExtensions.IsKnownGenericType(node.Name))
                {
                    _logger.LogInformation($"Skipping foreign Swift stdlib generic '{node.Name}' (canonical module: Swift) — runtime-provided; a local type would shadow Swift.Swift{node.Name}<...>.");
                    return null;
                }

                hasCurrentModuleExtensionChildren = node.Children?.Any(child =>
                    !string.IsNullOrEmpty(child.ModuleName) && child.ModuleName == moduleDecl.Name) ?? false;
                // Cross-module extension support: route both Class and Struct
                // receivers through CrossModuleExtensionEmitter. Class receivers use direct
                // CallConvSwift dispatch against the foreign module's Swift symbol (SwiftSelf
                // routes via x20 with a class pointer). Struct receivers add @_cdecl trampolines
                // in the current module's wrapper library that pin self as
                // `UnsafeMutableRawPointer` + load `.pointee`, since Swift's CallConvSwift
                // splits struct-by-value self across registers and .NET cannot synthesize that.
                // Enum receivers and non-frozen-struct receivers are deferred — both add
                // significant runtime/wrapper complexity (existential boxes, ARC field walking)
                // and were not part of the SDK 0.11.0 surface restoration scope.
                bool isClassReceiver = node.DeclKind == "Class";
                bool isStructReceiver = node.DeclKind == "Struct";

                if (hasCurrentModuleExtensionChildren && (isClassReceiver || isStructReceiver))
                {
                    // Non-frozen foreign struct receivers are not yet supported: the cross-module
                    // struct trampoline path reads `self` via `assumingMemoryBound(to: T.self).pointee`
                    // which is only ABI-safe for frozen value structs. Without this gate the foreign
                    // type still gets registered in the current module's database (with a synthesized
                    // metadata accessor) even though the emitter would later skip every member —
                    // polluting the database with an entry that has no usable members. Look up the
                    // foreign type in the dependency type database we already have; if it's a struct
                    // that is NOT a frozen value, skip cleanly here. Unknown types (no record yet)
                    // are tolerated because the emitter has its own fallback-receiver guard.
                    if (isStructReceiver)
                    {
                        var probeName = GetSwiftTypeName(parentDecl, node.Name, node.ModuleName);
                        if (_resolver.TryGetForeignTypeShape(probeName, out var foreignShape) &&
                            foreignShape.Kind == TypeRecordKind.Struct &&
                            (!foreignShape.Flags.HasFlag(TypeRecordFlags.Frozen) ||
                             foreignShape.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement)))
                        {
                            _logger.LogInformation($"Skipping cross-module extension on non-frozen or non-trivial (RequiresMemoryManagement) foreign struct '{node.Name}' (canonical module: {node.ModuleName}) — only frozen value structs without managed payload are supported by the cross-module struct trampoline path.");
                            return null;
                        }
                    }
                    _logger.LogInformation($"Foreign {(isStructReceiver ? "struct" : "class")} '{node.Name}' carries extension members from '{moduleDecl.Name}' — routing to cross-module extension emitter.");
                }
                else if (!_resolver.IsSystemReexportAllowedModule(node.ModuleName))
                {
                    _logger.LogInformation($"Skipping re-exported type '{node.Name}' (canonical module: {node.ModuleName}, current module: {moduleDecl.Name}).");
                    return null;
                }
                // else: foreign type from a system / common-Apple module with no extension
                // children of its own — fall through to the moduleNameOverride path below.
            }

            // When a system-module type appears in another module's ABI (e.g., Swift.KeyPath
            // extended by a third-party module), use the type's actual module for name qualification.
            string? moduleNameOverride = null;
            if (!string.IsNullOrEmpty(node.ModuleName) && parentDecl is ModuleDecl md2 && node.ModuleName != md2.Name)
            {
                moduleNameOverride = node.ModuleName;
            }

            var typeName = GetSwiftTypeName(parentDecl, node.Name, moduleNameOverride);
            var typeNameSpec = new NamedTypeSpec(typeName.ModuleQualifiedName);
            // For cross-module foreign types (moduleNameOverride != null), tolerate
            // `_typeDatabase.IsTypeProcessed` being true — that just means the dependency
            // module already registered the type. We still need to walk this node so the
            // extension members declared in the current module get attached. Only the
            // SECOND occurrence within this same parser pass is a true duplicate; that's
            // detected via `_moduleTypes.ContainsKey`.
            bool alreadyInModulePass = _moduleTypes.ContainsKey(typeNameSpec);
            // Finding 10: the narrow registration predicate, NOT the resolvable predicate. A
            // supplement-owned same-module type must answer "no" here, or the throw below fires
            // spuriously when an Apple-framework binding re-declares a type the supplement owns.
            bool processedByDependency = _resolver.IsTypeRegistered(typeName);
            if (alreadyInModulePass || (processedByDependency && moduleNameOverride == null))
            {
                if (moduleNameOverride != null)
                {
                    _logger.LogDebug($"Skipping duplicate cross-module type '{typeName}' (already processed in this pass).");
                    return null;
                }
                throw new InvalidOperationException($"Type '{node.Name}' already processed.");
            }

            // A bindable type declaration (Struct/Enum/Class/Protocol) REQUIRES a mangled name — it is
            // the load-bearing ABI field every downstream binder keys off. This node has cleared the
            // foreign-reexport and duplicate gates above, so an absent mangled name here is a type the
            // digester declared that we cannot bind: a malformed type record. Rather than dropping it
            // silently, QUARANTINE it — build the decl (below) so it carries a stable identity, mark it
            // so ModuleProcessor withholds it from the type database, and record a structured ledger
            // entry. The proven-closure walk then withdraws it plus every retained declaration that
            // depends on it, or fails the module before emission if that closure cannot be proven.
            bool isBindableTypeKind = node.DeclKind is "Struct" or "Enum" or "Class" or "Protocol";
            bool quarantineMalformedType = false;
            if (isBindableTypeKind && string.IsNullOrEmpty(node.MangledName))
            {
                // An ObjC-rooted declaration — an imported/`@objc` ObjC class or a C-typedef
                // struct re-exported through a Swift module — legitimately carries no Swift
                // mangled name: its ABI identity is the Clang USR (`c:objc(...)` / `c:@T@...`)
                // plus an `ObjC` decl attribute, the *expected* shape for foreign interop, not
                // digester drift. The missing mangled name here is therefore not a record loss:
                // such a type, when referenced, resolves through the Apple-supplement /
                // out-of-module path, and the digester re-export node itself is never bound.
                // Skip it cleanly (lands in SkippedWithReason) — the pre-b297b66f semantics —
                // rather than quarantining. A Swift-defined `@objc` class keeps its `$s...` mangled
                // name and never reaches this branch, so the exemption stays scoped to
                // mangled-name-less ObjC identities.
                if (IsObjCRootedIdentity(node))
                {
                    _logger.LogDebug($"Skipping ObjC-rooted declaration '{node.Name}' with no Swift mangled name (resolved via supplement / out-of-module when referenced).");
                    return null;
                }

                // The same reasoning generalizes past `@objc` classes and C typedefs to EVERY
                // Clang-rooted declaration the digester re-exports: a C aggregate (struct / union /
                // enum) surfaced only because this module retroactively conforms or extends it is
                // not a Swift declaration, so it has no Swift mangled name to lose. The record this
                // node points at is owned by the Clang importer, not by the digester dump we are
                // reading, and it is resolved through the Apple-supplement / out-of-module path when
                // referenced. Quarantining it would withdraw every declaration that merely STORES
                // the aggregate — for a library that does nothing but extend a system C type, that
                // is the whole binding.
                if (IsForeignClangReexportStub(node))
                {
                    // A stub that hosts current-module extension members must still be WALKED so
                    // those members attach (the cross-module extension routing above depends on it);
                    // only the malformed-record verdict is withdrawn here, not the walk.
                    if (!hasCurrentModuleExtensionChildren)
                    {
                        _logger.LogInformation(
                            "Skipping Clang-rooted re-export stub '{Name}' (module '{Module}', USR '{Usr}') with no "
                            + "Swift mangled name — an external C/ObjC declaration this module only references or "
                            + "extends; resolved via supplement / out-of-module when referenced.",
                            node.Name, node.ModuleName, node.usr);
                        return null;
                    }

                    _logger.LogInformation(
                        "Keeping Clang-rooted re-export stub '{Name}' (module '{Module}', USR '{Usr}') with no Swift "
                        + "mangled name — it hosts extension members declared by '{CurrentModule}', which are attached "
                        + "by walking this node.",
                        node.Name, node.ModuleName, node.usr, moduleDecl.Name);
                }
                else
                {
                    quarantineMalformedType = true;
                }
            }

            TypeDecl? decl;

            // Parse generic parameters if present (except for protocols which handle them differently)
            List<GenericArgumentDecl> genericParameters = new();
            if (node.GenericSig is not null && node.DeclKind != "Protocol")
            {
                genericParameters = GenericSignatureParser.ParseGenericSignature(node.GenericSig, node.sugared_genericSig);
            }

            switch (node.DeclKind)
            {
                case "Struct":
                    decl = CreateStructDecl(node, parentDecl, moduleDecl, genericParameters, moduleNameOverride);
                    break;

                case "Enum":
                    decl = CreateEnumDecl(node, parentDecl, moduleDecl, genericParameters, moduleNameOverride);
                    break;

                case "Class":
                    decl = CreateClassDecl(node, parentDecl, moduleDecl, genericParameters, moduleNameOverride);
                    break;

                case "Protocol":
                    decl = CreateProtocolDecl(node, parentDecl, moduleDecl, moduleNameOverride);
                    break;

                default:
                    _logger.LogWarning($"Unsupported declaration type '{node.DeclKind} {node.Name}' encountered.");
                    return null;
            }

            if (decl is not null)
            {
                if (quarantineMalformedType)
                {
                    // Mark the built decl so ModuleProcessor withholds it from the type database, and
                    // record the structured ledger entry. This is a proposed quarantine: the terminal
                    // fate is decided by the proven-closure walk at emission — if that closure cannot be
                    // proven complete the module fails fatally, so any binding that actually ships with
                    // this entry is one where the quarantine was proven, making Quarantined accurate.
                    decl.IsIngestionQuarantined = true;
                    InputResolutionReport.RecordLedgerEntry(new IngestionLedgerEntry(
                        Input: IdentityOf(node),
                        Parent: ParentIdentityOf(parentDecl),
                        Plane: IngestionPlane.Ingest,
                        Cause: IngestionCause.MalformedTypeRecord,
                        Referenced: null,
                        Disposition: IngestionDisposition.QuarantineType,
                        ClosureEvidence:
                            "bindable type record missing its load-bearing Swift mangled name; "
                            + "quarantined pending the proven-closure withdrawal walk at emission.",
                        Status: IngestionStatus.Quarantined));
                    _logger.LogWarning(
                        "SWIFTBIND046: bindable type '{Name}' (module '{Module}') has no Swift mangled "
                        + "name; quarantined at ingestion. It and every retained declaration that depends "
                        + "on it are withdrawn from the binding; if that withdrawal closure cannot be "
                        + "proven complete, the module fails before emission.",
                        node.Name, node.ModuleName);
                }

                // Register immediately so duplicate cross-module re-exports are caught
                _moduleTypes.TryAdd(new NamedTypeSpec(decl.SwiftTypeName.ModuleQualifiedName), decl);

                var childDecls = CollectDeclarations(node.Children ?? Array.Empty<Node>(), decl, moduleDecl);

                // Protocol-extension defaults (isFromExtension=true, protocolReq=false) are NOT
                // part of the protocol's abstract contract — they're @_alwaysEmitIntoClient or
                // similar Swift extension bodies that are inlined at call sites. The ABI
                // digester flattens them into the protocol node's children (e.g., AppIntents'
                // @_marker protocol BooksEnum has 10 such children, all properties from
                // `extension BooksEnum { @_alwaysEmitIntoClient public var ... }`). Emitting
                // them as abstract C# interface requirements causes CS0535 cascades on every
                // conforming umbrella type (EnumSchema etc.) that doesn't redeclare them.
                // Filter at the population site so every downstream consumer (interface
                // emission, EveryProtocol, ProtocolProxy, ConformanceValidator, vtables,
                // composition wrappers) sees only the real protocol contract. Real extension
                // defaults that ARE genuine requirements (rare) still flow through because the
                // filter requires BOTH isFromExtension AND !IsProtocolRequirement.
                if (decl is ProtocolDecl)
                {
                    decl.Properties.AddRange(childDecls.OfType<PropertyDecl>()
                        .Where(p => !(p.IsFromExtension && !p.IsProtocolRequirement)));
                    decl.Methods.AddRange(childDecls.OfType<MethodDecl>()
                        .Where(m => !(m.IsExtensionMethod && !m.IsProtocolRequirement)));
                }
                else
                {
                    decl.Properties.AddRange(childDecls.OfType<PropertyDecl>());
                    decl.Methods.AddRange(childDecls.OfType<MethodDecl>());
                }
                decl.Types.AddRange(childDecls.OfType<TypeDecl>());
                decl.Operators.AddRange(childDecls.OfType<OperatorDecl>());
                decl.Subscripts.AddRange(childDecls.OfType<SubscriptDecl>());
                decl.GenericParameters = genericParameters;

                // TypeAlias children (incl. those introduced by `extension`) carry the
                // resolved nominal type as their first child's printedName. Conformer-
                // extension typealiases like `extension Album: MusicLibraryRequestable {
                // typealias LibrarySortProperties = LibraryAlbumSortProperties }` are the
                // signal Route C's bag walker needs to map a parent's associated-type
                // name to a per-conformer protocol/struct/class. CollectDeclarations
                // doesn't surface TypeAlias kinds (they have no MethodDecl/PropertyDecl
                // equivalent), so we walk the raw children directly here.
                foreach (var child in node.Children ?? Array.Empty<Node>())
                {
                    if (child.Kind != "TypeAlias") continue;
                    if (string.IsNullOrEmpty(child.Name)) continue;
                    var targetNode = child.Children?.FirstOrDefault();
                    if (targetNode is null) continue;
                    var targetName = targetNode.PrintedName;
                    if (string.IsNullOrEmpty(targetName)) continue;
                    decl.Typealiases[child.Name] = targetName;
                }

                // NOTE: We intentionally do NOT dedup PropertyDecls collected from
                // constrained extensions here. The ABI JSON emits one Var node per
                // specialization (e.g., StoreKit's three `extension VerificationResult
                // where SignedType == ...` blocks each contribute their own copy of
                // `jwsRepresentation`), and each PropertyDecl carries its own
                // specialization-specific accessor mangled symbol. Picking one and
                // dropping the rest would silently miscompile the C# binding because
                // a closed generic instantiation `Wrapper<Beta>.Property` would dispatch
                // to the surviving Alpha specialization's symbol — undefined behavior.
                // Instead, the multi-specialization conflict is detected at emission
                // time in `MemberEmissionValidator.CanEmitProperty`, which skips ALL
                // conflicting copies with a clear skip reason. Regression coverage
                // lives in BindingTests/.../Generics/ConstrainedExtensionDedup.swift.

                // Collect enum cases if this is an EnumDecl
                if (decl is EnumDecl enumDecl)
                {
                    enumDecl.Cases.AddRange(childDecls.OfType<EnumCaseDecl>());
                }

                // Detect missing protocol requirements: count ABI JSON Function/Constructor/Var
                // children that are actual protocol requirements (protocolReq=true) and compare
                // against successfully parsed members that are requirements. Extension defaults
                // (protocolReq=false or absent) don't need proxy stubs — Swift provides their
                // default implementation automatically. Var requirements are counted alongside
                // Function/Constructor: a Var requirement that fails to parse is just as fatal
                // to EveryProtocol conformance as a missing method (the emitter can't synthesize
                // a stub for an unknown property type).
                if (decl is ProtocolDecl protocolDecl2)
                {
                    int expectedReqChildren = (node.Children ?? Array.Empty<Node>())
                        .Count(c => (c.Kind == "Function" || c.Kind == "Constructor" || c.Kind == "Var") && c.protocolReq == true);
                    int parsedReqMembers = decl.Methods.Count(m => m.IsProtocolRequirement)
                                         + decl.Properties.Count(p => p.IsProtocolRequirement);
                    if (parsedReqMembers < expectedReqChildren)
                    {
                        protocolDecl2.HasMissingRequirements = true;
                        _logger.LogDebug("Protocol {Name}: {Missing} required member(s) failed ABI parsing ({Parsed}/{Expected})",
                            decl.Name, expectedReqChildren - parsedReqMembers, parsedReqMembers, expectedReqChildren);
                    }

                    // Hidden-requirement gate: only fire when a __-prefixed requirement
                    // declared in the swiftinterface protocol body is ALSO absent from the
                    // ABI JSON children for this protocol. If the requirement is in the ABI
                    // (e.g. newer Swift toolchains that retain __-names), the generator can
                    // witness it normally and the EveryProtocol conformance is fine.
                    // Comparing names — not just protocol identity — keeps us from
                    // suppressing valid proxies whenever the swiftinterface happens to
                    // declare a __-name.
                    if (_facts.HiddenRequirementProtocols.TryGetValue(decl.Name, out var swiftinterfaceUnderscored) &&
                        swiftinterfaceUnderscored.Count > 0)
                    {
                        // Collect ALL protocol-requirement member names present in the ABI JSON
                        // (not just __-prefixed ones). The hidden-requirement candidate set now
                        // includes requirements whose NAME is ordinary but whose signature
                        // references a __-prefixed SPI type (e.g. RealityCoordinateSpace._resolve);
                        // those are keyed by their real name. Broadening the ABI set is safe for
                        // the original __-name case: a __-prefixed candidate can only match a
                        // __-prefixed ABI name, so ordinary ABI names never mask it.
                        var abiRequirementNames = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var child in node.Children ?? Array.Empty<Node>())
                        {
                            if (child.protocolReq != true)
                                continue;
                            if (child.Kind != "Function" && child.Kind != "Var" && child.Kind != "Constructor" && child.Kind != "Subscript")
                                continue;
                            abiRequirementNames.Add(ExtractUniqueName(child.Name));
                        }
                        foreach (var name in swiftinterfaceUnderscored)
                        {
                            if (!abiRequirementNames.Contains(name))
                            {
                                protocolDecl2.HasUnsatisfiedHiddenRequirements = true;
                                _logger.LogDebug("Protocol {Name}: swiftinterface declares __-prefixed requirement '{Member}' that is missing from ABI JSON; skipping EveryProtocol conformance.",
                                    decl.Name, name);
                                break;
                            }
                        }
                    }

                    // TBD-method-descriptor gate (Mac Catalyst Apple-bug pattern): the
                    // macCatalyst swiftinterface can declare a protocol requirement whose
                    // method-descriptor symbol (`{mangledName}Tq`) is absent from the
                    // framework's TBD on this slice — Apple's
                    // LiveCommunicationKit.ConversationManagerDelegate.didActivate /
                    // didDeactivate ships in the macabi swiftinterface but the macOS
                    // dylib's TBD does not export the Tq descriptor. The EveryProtocol
                    // extension would synthesize a witness table referencing the
                    // missing descriptor, producing an undefined-symbol link error.
                    // Skip the conformance so the wrapper links; existential dispatch
                    // through the protocol's vtable remains unaffected because that
                    // path uses ConversationManagerDelegateProxy, not EveryProtocol.
                    //
                    // The gate is scoped to native-Swift protocols. `@objc` protocols
                    // and `@objc optional` members dispatch through the ObjC selector table rather
                    // than a Swift witness table — they never emit a `Tq` descriptor,
                    // so a missing one means nothing for them. Treating them as missing
                    // would suppress proxy classes for every `@objc` protocol on every
                    // slice, eliminating a working surface.
                    //
                    // Scope: methods only. Protocol property and subscript requirements
                    // also have `Tq` descriptors in Swift, but no observed Apple SDK has
                    // missing-property-descriptor cases against the validation corpus.
                    // If one ever surfaces, it will fail loudly through the now-always-on
                    // swiftc stderr propagation in Build.Validation rather than silently,
                    // and the cross-check can be extended to walk Properties/Subscripts.
                    bool isObjCProtocol = node.DeclAttributes is not null &&
                        Array.IndexOf(node.DeclAttributes, "ObjC") != -1;
                    if (!isObjCProtocol)
                    {
                        foreach (var method in protocolDecl2.Methods)
                        {
                            if (!method.IsProtocolRequirement)
                                continue;
                            if (method.IsObjCOptional)
                                continue;
                            if (string.IsNullOrEmpty(method.MangledName))
                                continue;
                            if (!ManglingProbes.HasMethodDescriptor(_demangledTbd.AllSymbols, method.MangledName))
                            {
                                protocolDecl2.HasMissingTbdMethodDescriptors = true;
                                _logger.LogDebug("Protocol {Name}: required method '{Method}' has no Tq method descriptor in TBD ({Mangled}Tq missing); skipping EveryProtocol conformance.",
                                    decl.Name, method.Name, method.MangledName);
                                break;
                            }
                        }
                    }
                }

                foreach (var type in decl.Types)
                {
                    _moduleTypes.TryAdd(new NamedTypeSpec(type.SwiftTypeName.ModuleQualifiedName), type);
                }
            }

            return decl;
        }

        private TypeConformance HandleConformance(Node node, SwiftTypeName typeName, string? implementingTypeMangledName = null)
        {
            // Demangle the conformance's protocol mangled name. The demangler does not
            // yet recognize every standard library short substitution (notably the
            // `Sc*` family used by `_Concurrency` types — `$sSci` for AsyncSequence,
            // `$sScI` for AsyncIteratorProtocol, etc.), and a crash here would propagate
            // up through the conformance list select in CreateStructDecl/CreateClassDecl
            // and kill the entire enclosing TypeDecl via HandleNode's catch-all. Wrap
            // the call so a missing substitution merely degrades the conformance to a
            // best-effort identity built from the ABI JSON's printedName.
            SwiftTypeName protocolName;
            try
            {
                var reduction = demangler.Run(node.MangledName) as TypeSpecReduction
                    ?? throw new InvalidOperationException($"Invalid demangling result for '{node.MangledName}'.");
                var protocolTypeSpec = reduction.TypeSpec as NamedTypeSpec
                    ?? throw new InvalidOperationException($"TypeSpec '{reduction.TypeSpec}' is not a NamedTypeSpec");
                protocolName = SwiftTypeName.FromTypeSpec(protocolTypeSpec);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Failed to demangle conformance '{node.Name}' ({node.MangledName}) on '{typeName}': {ex.Message}. " +
                    $"Falling back to printedName-derived protocol identity.");
                protocolName = BuildFallbackProtocolName(node);
            }
            string protocolConformanceDescriptor = string.Empty;

            if (!_resolver.TryGetProtocolConformanceDescriptor(typeName, protocolName, out protocolConformanceDescriptor))
            {
                // @_originallyDefinedIn umbrella re-exports: the type decl is attributed to its
                // CURRENT module via its USR (e.g. RealityFoundation.AnchorEntity), but the TBD's
                // conformance-descriptor symbol is mangled with the type's ORIGINAL module
                // (e.g. RealityKit.AnchorEntity). The protocol identity already comes from the
                // mangled name, so it matches; only the implementing type's module diverges.
                // Retry the lookup with the implementing type's original (mangled) module so these
                // conformances resolve a real descriptor instead of silently emitting an empty one.
                if (ManglingProbes.TryGetModuleFromMangledName(implementingTypeMangledName, out var abiModule) &&
                    !string.Equals(abiModule, typeName.Module, StringComparison.Ordinal))
                {
                    var abiParts = typeName.ModuleQualifiedName.Split('.', StringSplitOptions.RemoveEmptyEntries);
                    // Guard against a degenerate module-qualified name (empty or all-separators):
                    // with no segment to rewrite there is no original-module identity to retry, so
                    // leave the descriptor empty and fall through to the graceful no-descriptor path.
                    if (abiParts.Length > 0)
                    {
                        abiParts[0] = abiModule;
                        var abiTypeName = SwiftTypeName.FromModuleQualifiedName(string.Join('.', abiParts));
                        _resolver.TryGetProtocolConformanceDescriptor(abiTypeName, protocolName, out protocolConformanceDescriptor);
                    }
                }

                if (string.IsNullOrEmpty(protocolConformanceDescriptor))
                {
                    // Some types conform to protocols inherently, i.e., they are not explicitly declared.
                    // These conformances are specified in the ABI.json but the descriptors are not present in the TBD.
                    _logger.LogWarning($"Protocol conformance descriptor not found for '{typeName}' and protocol '{protocolName}'.");
                }
            }

            var conformance = new TypeConformance(typeName, protocolName, protocolConformanceDescriptor);

            // Extract TypeWitness entries from conformance children.
            // These map associated types to concrete types for this conformance.
            foreach (var child in node.Children)
            {
                if (child.Kind != "TypeWitness") continue;
                if (!child.Children.Any()) continue;
                try
                {
                    var resolvedType = CreateTypeSpec(child.Children.First());
                    _conformanceGraph.AddWitness(
                        typeName.ModuleQualifiedName,
                        protocolName.ModuleQualifiedName,
                        child.Name,  // e.g., "Element"
                        resolvedType);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse TypeWitness {typeName}.{child.Name}: {ex.Message}");
                }
            }

            return conformance;
        }

        /// <summary>
        /// Materializes a type's <see cref="TypeConformance"/> list from <c>node.Conformances</c>
        /// and drops entries that are SPI-only — declared in <c>*.private.swiftinterface</c>
        /// behind a <c>@_spi(...) extension</c> and therefore invisible to a plain
        /// (non-<c>@_spi</c>) <c>import</c> at wrapper compile time. ABI JSON ships every
        /// conformance regardless of access, so without this filter the generator would emit
        /// wrapper code (<c>==</c> operator wrappers, <c>JSONEncoder().encode(value)</c>
        /// stubs, etc.) that fails to typecheck. The SPI fact is extracted from the sibling
        /// <c>*.private.swiftinterface</c> and threaded in via
        /// <see cref="SwiftInterfaceFacts.SpiOnlyConformances"/>.
        /// </summary>
        private List<TypeConformance> BuildFilteredConformances(Node node, SwiftTypeName swiftTypeName)
        {
            var conformances = new List<TypeConformance>();
            var spiSet = _facts.SpiOnlyConformances;
            var qualified = swiftTypeName.ModuleQualifiedName;
            foreach (var c in node.Conformances)
            {
                var conformance = HandleConformance(c, swiftTypeName, node.MangledName);
                if (spiSet.Count > 0 &&
                    spiSet.Contains($"{qualified}::{conformance.Protocol.Name}"))
                {
                    _logger.LogDebug(
                        "Filtered SPI-only conformance '{Type}: {Protocol}' (declared via @_spi extension in private.swiftinterface).",
                        qualified, conformance.Protocol.Name);
                    continue;
                }
                conformances.Add(conformance);
            }
            return conformances;
        }

        /// <summary>
        /// Creates a struct declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the struct declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <param name="genericParameters">The generic parameters for this type.</param>
        /// <returns>The struct declaration.</returns>
        private StructDecl CreateStructDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl, List<GenericArgumentDecl> genericParameters, string? moduleNameOverride = null)
        {
            var swiftTypeName = GetSwiftTypeName(parentDecl, node.Name, moduleNameOverride);
            var hasFrozenAttribute = node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "Frozen") != -1;

            var decl = new StructDecl
            {
                Name = ExtractUniqueName(node.Name),
                SwiftTypeName = swiftTypeName,
                MangledName = node.MangledName,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                GenericParameters = genericParameters,
                Conformances = BuildFilteredConformances(node, swiftTypeName),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsFrozen = hasFrozenAttribute,
                // The descriptor carries the bare presence of `@_alignment(N)`, never N itself.
                HasCustomAlignment = node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "Alignment") != -1,
                MetadataAccessor = ResolveMetadataAccessor(node, swiftTypeName, moduleDecl),
                IsModuleInternal = IsNodeModuleInternal(node),
                IsSpiProtected = IsNodeSpiProtected(node)
            };
            if (!decl.IsModuleInternal)
                decl.IsModuleInternal = IsInternalFromPublicTypeNames(decl);
            if (!decl.IsModuleInternal && IsTypeUnavailableFromSwiftInterface(decl))
                decl.IsModuleInternal = true;
            ApplyActorIsolation(decl);
            ApplyAvailability(decl);
            ApplyObjCRuntimeName(decl);
            ApplyPosition(decl);
            PopulateDocumentation(decl, node);
            return decl;
        }

        // Resolves the metadata accessor symbol for a struct/enum decl. Prefers the demangled
        // TBD entry; falls back to the canonical Swift mangling ({mangledName}Ma) when the
        // node belongs to the module being parsed (umbrella re-exports where
        // RealityFoundation parses RealityKit-mangled types as its own) or when the foreign
        // type is already registered in the dependency type database (third-party-to-third-party
        // cross-module struct extension, e.g. SwiftBindingsTestLib's `extension DependencyPoint`).
        // In the cross-module case the symbol resolves at runtime via the dependency framework's
        // dylib; the upstream parser gate above this function (the non-frozen/non-trivial probe
        // on `extension` nodes) prevents unsupported foreign struct shapes from being routed
        // here. Truly unknown cross-module types still throw so a missing dependency surfaces
        // loudly instead of producing a broken accessor.
        private string ResolveMetadataAccessor(Node node, SwiftTypeName swiftTypeName, ModuleDecl moduleDecl)
        {
            if (_resolver.TryGetMetadataAccessor(swiftTypeName, out var symbol))
                return symbol;
            if (string.IsNullOrEmpty(node.ModuleName) || node.ModuleName == moduleDecl.Name)
                return $"{node.MangledName}Ma";
            // Finding 10: registration predicate — "is the foreign type already registered in a
            // loaded dependency database" — not "resolvable via the supplement". A supplement-owned
            // foreign type must not be claimed here, so its metadata accessor comes from the TBD.
            if (_resolver.IsTypeRegistered(swiftTypeName))
                return $"{node.MangledName}Ma";
            return _resolver.GetMetadataAccessor(swiftTypeName);
        }

        /// <summary>
        /// Creates an enum declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the enum declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <param name="genericParameters">The generic parameters for this type.</param>
        /// <returns>The enum declaration.</returns>
        private EnumDecl CreateEnumDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl, List<GenericArgumentDecl> genericParameters, string? moduleNameOverride = null)
        {
            var swiftTypeName = GetSwiftTypeName(parentDecl, node.Name, moduleNameOverride);
            var hasFrozenAttribute = node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "Frozen") != -1;

            var decl = new EnumDecl
            {
                Name = ExtractUniqueName(node.Name),
                SwiftTypeName = swiftTypeName,
                MangledName = node.MangledName,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Cases = new List<EnumCaseDecl>(),
                GenericParameters = genericParameters,
                Conformances = BuildFilteredConformances(node, swiftTypeName),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsFrozen = hasFrozenAttribute,
                HasCustomAlignment = node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "Alignment") != -1,
                MetadataAccessor = ResolveMetadataAccessor(node, swiftTypeName, moduleDecl),
                RawValueTypeName = node.EnumRawTypeName,
                IsModuleInternal = IsNodeModuleInternal(node),
                IsSpiProtected = IsNodeSpiProtected(node)
            };
            if (!decl.IsModuleInternal)
                decl.IsModuleInternal = IsInternalFromPublicTypeNames(decl);
            if (!decl.IsModuleInternal && IsTypeUnavailableFromSwiftInterface(decl))
                decl.IsModuleInternal = true;
            ApplyActorIsolation(decl);
            ApplyAvailability(decl);
            ApplyObjCRuntimeName(decl);
            ApplyPosition(decl);
            PopulateDocumentation(decl, node);
            return decl;
        }

        /// <summary>
        /// Creates an enum case declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the enum case.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The enum case declaration.</returns>
        private EnumCaseDecl CreateEnumCaseDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            // Strip Swift backtick escaping (e.g., `subscript` → subscript).
            var caseName = node.Name;
            if (caseName.Length >= 2 && caseName[0] == '`' && caseName[caseName.Length - 1] == '`')
                caseName = caseName.Substring(1, caseName.Length - 2);

            var enumCaseDecl = new EnumCaseDecl
            {
                Name = caseName,
                MangledName = node.MangledName,
                AssociatedValues = new List<TypeSpec>(),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsSpiProtected = IsNodeSpiProtected(node),
            };
            PopulateDocumentation(enumCaseDecl, node);

            // Parse associated values from the type signature if present
            // The type signature for enum cases looks like:
            // - Simple case: (EnumType.Type) -> EnumType
            // - Case with associated values: (EnumType.Type) -> (AssocValue1, AssocValue2) -> EnumType
            var children = node.Children.ToList();
            if (children.Count > 0 && children[0].Kind == kFunc)
            {
                var funcChildren = children[0].Children.ToList();
                // For associated values, there will be a tuple in the function type
                // The structure is: Function -> [ReturnType, Metatype] for simple
                // or Function -> [Function -> [ReturnType, TupleOfAssocValues], Metatype] for associated values
                if (funcChildren.Count >= 2)
                {
                    var returnPart = funcChildren[0];
                    // Check if returnPart is another function (indicating associated values)
                    if (returnPart.Kind == kFunc)
                    {
                        var innerFuncChildren = returnPart.Children.ToList();
                        if (innerFuncChildren.Count >= 2)
                        {
                            // The second child should be the associated values
                            var assocValuesNode = innerFuncChildren[1];
                            if (assocValuesNode.Kind == kTuple || assocValuesNode.Name == kTuple)
                            {
                                // Parse the full tuple printedName to preserve associated value labels.
                                // e.g., "(radius: Swift.Double)" → TypeSpec with TypeLabel = "radius"
                                // TypeSpecParser.Parse() throws on malformed input, so wrap in try/catch
                                // with fallback to the old child-by-child approach.
                                bool parsedFromTuplePrintedName = false;
                                try
                                {
                                    var tuplePrintedName = assocValuesNode.PrintedName;
                                    var parsedTuple = TypeSpecParser.Parse(tuplePrintedName);
                                    List<TypeSpec> parsedElements;
                                    if (parsedTuple is TupleTypeSpec tupleSpec)
                                    {
                                        // Detect the `case foo(label: (a:, b:, ...))` shape:
                                        // TypeSpecParser unwraps the outer one-element tuple and
                                        // moves the "label" onto the inner tuple. Without this
                                        // detection the inner tuple is flattened to bare params
                                        // and the @_cdecl wrapper emits `foo(a:, b:)` instead of
                                        // the required `foo(label: (a:, b:))`.
                                        if (!string.IsNullOrEmpty(tupleSpec.TypeLabel))
                                        {
                                            enumCaseDecl.OuterTupleLabel = tupleSpec.TypeLabel;
                                        }
                                        parsedElements = tupleSpec.Elements;
                                    }
                                    else if (parsedTuple != null)
                                    {
                                        // Single-element tuple unwrapped by TypeSpecParser
                                        parsedElements = new List<TypeSpec> { parsedTuple };
                                    }
                                    else
                                    {
                                        parsedElements = new List<TypeSpec>();
                                    }

                                    if (parsedElements.Count > 0)
                                    {
                                        // For elements whose corresponding ABI child is a TypeNameAlias,
                                        // resolve the alias to its underlying nominal type via CreateTypeSpec
                                        // (which unwraps the alias) — the textual TypeSpecParser only sees
                                        // the printed alias name (e.g. "simd.float4x4") and cannot expand
                                        // it to the real type ("simd.simd_float4x4"). Preserve the
                                        // textually-parsed label since CreateTypeSpec doesn't see it.
                                        var assocChildren = assocValuesNode.Children.ToList();
                                        for (int i = 0; i < parsedElements.Count && i < assocChildren.Count; i++)
                                        {
                                            if (assocChildren[i].Kind == "TypeNameAlias")
                                            {
                                                var resolved = CreateTypeSpec(assocChildren[i]);
                                                if (resolved != null)
                                                {
                                                    resolved.TypeLabel = parsedElements[i].TypeLabel;
                                                    parsedElements[i] = resolved;
                                                }
                                            }
                                            else
                                            {
                                                // The printedName parse carries the type's shape but not
                                                // its ABI USR; thread it from the matching child node so a
                                                // clang-imported associated value (e.g. a bridged NSError
                                                // that Swift surfaces as a struct) can be recognized at
                                                // type resolution and its case skipped rather than
                                                // reconstructed from a raw payload it has no initializer for.
                                                ThreadNominalUsrs(parsedElements[i], assocChildren[i]);
                                            }
                                        }
                                        foreach (var element in parsedElements)
                                            enumCaseDecl.AssociatedValues.Add(element);
                                        parsedFromTuplePrintedName = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // TypeSpecParser throws on parse errors — fall through to child iteration
                                    _logger.LogDebug($"Failed to parse tuple printedName '{assocValuesNode.PrintedName}' for enum case '{enumCaseDecl.Name}': {ex.Message}");
                                }

                                if (!parsedFromTuplePrintedName)
                                {
                                    // Fallback: parse individual children (no labels). Reached only
                                    // when the primary printedName parse above throws — in practice
                                    // rare, since TypeSpecParser handles closures-in-tuples (e.g. the
                                    // `case labeled(label: String, handler: ((Int32,String)->Bool)?)`
                                    // fixture). It must still never DROP a non-nominal element: a
                                    // skipped closure/function tuple element undersizes the enum the
                                    // same way the single-payload case did (Alamofire SIGSEGV root
                                    // cause below), so route non-nominal children through CreateTypeSpec
                                    // and, on failure, record a placeholder rather than discarding it.
                                    foreach (var tupleElement in assocValuesNode.Children)
                                    {
                                        if (tupleElement.Kind == kNominal)
                                        {
                                            try
                                            {
                                                var typeSpec = TypeSpecParser.Parse(tupleElement.PrintedName);
                                                if (typeSpec != null)
                                                {
                                                    ThreadNominalUsrs(typeSpec, tupleElement);
                                                    enumCaseDecl.AssociatedValues.Add(typeSpec);
                                                }
                                            }
                                            catch (TypeSpecParseException ex)
                                            {
                                                // Never DROP a tuple element on parse failure: a missing element
                                                // undersizes the enum payload (Alamofire SIGSEGV root cause). Record
                                                // a placeholder, mirroring the non-nominal fallback below. EOF-strict
                                                // Parse makes a malformed printedName throw here rather than
                                                // prefix-accept; the throw must not escape and drop the whole enum.
                                                _logger.LogDebug($"Failed to parse nominal tuple associated value '{tupleElement.PrintedName}' for enum case '{enumCaseDecl.Name}': {ex.Message}");
                                                enumCaseDecl.AssociatedValues.Add(new NamedTypeSpec(tupleElement.PrintedName));
                                            }
                                        }
                                        else
                                        {
                                            try
                                            {
                                                enumCaseDecl.AssociatedValues.Add(CreateTypeSpec(tupleElement));
                                            }
                                            catch (Exception ex)
                                            {
                                                _logger.LogDebug($"Failed to create TypeSpec for tuple associated value of enum case '{enumCaseDecl.Name}' (kind={tupleElement.Kind}): {ex.Message}");
                                                enumCaseDecl.AssociatedValues.Add(new NamedTypeSpec(tupleElement.PrintedName));
                                            }
                                        }
                                    }
                                }
                            }
                            else if (assocValuesNode.Kind == kNominal)
                            {
                                // Single associated value
                                try
                                {
                                    var typeSpec = TypeSpecParser.Parse(assocValuesNode.PrintedName);
                                    if (typeSpec != null)
                                    {
                                        // Thread the ABI USR onto the textually-parsed spec so a
                                        // clang-imported payload (e.g. a bridged NSError struct) is
                                        // recognized at type resolution and its case skipped.
                                        ThreadNominalUsrs(typeSpec, assocValuesNode);
                                        enumCaseDecl.AssociatedValues.Add(typeSpec);
                                    }
                                }
                                catch (TypeSpecParseException ex)
                                {
                                    // Never DROP the payload on parse failure: a missing associated value
                                    // undersizes the enum (Alamofire SIGSEGV root cause). Record a placeholder
                                    // rather than letting an EOF-strict throw escape and drop the whole enum.
                                    _logger.LogDebug($"Failed to parse nominal associated value '{assocValuesNode.PrintedName}' for enum case '{enumCaseDecl.Name}': {ex.Message}");
                                    enumCaseDecl.AssociatedValues.Add(new NamedTypeSpec(assocValuesNode.PrintedName));
                                }
                            }
                            else
                            {
                                // Single associated value that is neither a tuple nor a plain
                                // nominal — most commonly a function/closure payload such as
                                // `case custom((String, Int) -> String)`, which swift-api-digester
                                // encodes as a TypeFunc node in the payload-application position
                                // (it can also be a TypeNameAlias). Earlier code matched only
                                // kTuple/kNominal here, so a closure-payload case recorded ZERO
                                // associated values; the enum was then misclassified as a simple
                                // (Int32-backed) enum, the parameter was marshalled as a 4-byte
                                // tag, and the Swift wrapper loaded the real (multi-word,
                                // closure-carrying) enum out of that undersized buffer — an
                                // out-of-bounds read whose garbage non-trivial payload crashed on
                                // ARC release (Alamofire URLEncoding.ArrayEncoding SIGSEGV). Route
                                // the node through CreateTypeSpec so the payload type is recorded
                                // and the enum is correctly treated as having associated values.
                                try
                                {
                                    var typeSpec = CreateTypeSpec(assocValuesNode);
                                    if (typeSpec != null)
                                    {
                                        enumCaseDecl.AssociatedValues.Add(typeSpec);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // Even if we cannot model the payload type, record that this
                                    // case HAS an associated value so the enum is never lowered to
                                    // a simple Int32 enum. A placeholder spec is enough for the
                                    // HasAssociatedValueCases classification; downstream emission
                                    // skips members it cannot marshal rather than mis-sizing them.
                                    _logger.LogDebug($"Failed to create TypeSpec for associated value of enum case '{enumCaseDecl.Name}' (kind={assocValuesNode.Kind}): {ex.Message}");
                                    enumCaseDecl.AssociatedValues.Add(new NamedTypeSpec(assocValuesNode.PrintedName ?? assocValuesNode.Kind));
                                }
                            }

                            // Disambiguate `case foo((A, B))` — ONE unlabeled tuple-typed
                            // associated value — from `case foo(A, B)` — N separate values.
                            // Both flatten to the same AssociatedValues list and the same ABI
                            // Tuple node; only the enum-case function type's printedName tells
                            // them apart by an extra paren around the parameter clause
                            // ("((A, B)) -> Enum" vs "(A, B) -> Enum"). Record it so the wrapper
                            // re-wraps the args into a single tuple. The labeled counterpart is
                            // handled by OuterTupleLabel above.
                            if (enumCaseDecl.AssociatedValues.Count > 1 &&
                                string.IsNullOrEmpty(enumCaseDecl.OuterTupleLabel) &&
                                IsSingleTupleAssociatedValueParam(returnPart.PrintedName))
                            {
                                enumCaseDecl.IsSingleTuplePayload = true;
                            }
                        }
                    }
                }
            }

            // Apply parameter labels from swiftinterface if available
            if (enumCaseDecl.AssociatedValues.Count > 0 && parentDecl is TypeDecl parentType)
            {
                // Build fully-qualified type path matching the parser's dot-joined key format
                // e.g., "OrderContainer.Status.caseName" for nested enum Status inside OrderContainer
                var typePath = BuildTypeQualifiedPath(parentType);
                var key = $"{typePath}.{enumCaseDecl.Name}";
                if (_facts.EnumCaseLabels.TryGetValue(key, out var labels))
                {
                    for (int i = 0; i < Math.Min(labels.Count, enumCaseDecl.AssociatedValues.Count); i++)
                    {
                        // Only apply swiftinterface label when ABI didn't already provide one
                        if (labels[i] != null && string.IsNullOrEmpty(enumCaseDecl.AssociatedValues[i].TypeLabel))
                        {
                            enumCaseDecl.AssociatedValues[i].TypeLabel = labels[i];
                        }
                    }
                }
            }

            // Apply string raw values from swiftinterface if available
            if (parentDecl is TypeDecl rawValueParent)
            {
                var typePath = BuildTypeQualifiedPath(rawValueParent);
                var rawKey = $"{typePath}.{enumCaseDecl.Name}";
                if (_facts.EnumCaseRawValues.TryGetValue(rawKey, out var rawValue))
                {
                    enumCaseDecl.RawValue = rawValue;
                }
            }

            // Apply per-case @available annotations from swiftinterface so the @_cdecl wrapper
            // for newly-introduced cases (e.g., StoreKit ExternalPurchase.NoticeResult.continuedWithExternalPurchaseToken
            // — iOS 17.4) compiles against older deployment targets. The swiftinterface parser
            // keys enum cases by their bare name (matching the ABI JSON Var node printedName).
            if (parentDecl is TypeDecl enumParentType)
            {
                ApplyMemberAvailability(enumCaseDecl, enumParentType, enumCaseDecl.Name);
                ApplyMemberPosition(enumCaseDecl, enumParentType, enumCaseDecl.Name);
            }

            return enumCaseDecl;
        }

        /// <summary>
        /// Whether an enum-case function type's printedName has a single tuple-typed
        /// parameter (a single UNLABELED tuple associated value), distinguished from
        /// N separate associated values by an extra paren around the parameter clause:
        /// <c>((Int32, BoxedCounter)) -&gt; Enum</c> (single tuple) vs
        /// <c>(Int32, String) -&gt; Enum</c> (two values). Returns true only when the
        /// leading balanced-paren parameter clause wraps exactly one further balanced-paren
        /// group spanning its entire interior.
        /// </summary>
        internal static bool IsSingleTupleAssociatedValueParam(string? funcPrintedName)
        {
            if (string.IsNullOrEmpty(funcPrintedName) || funcPrintedName[0] != '(')
                return false;

            // Find the leading balanced-paren parameter clause.
            int depth = 0;
            int clauseEnd = -1;
            for (int i = 0; i < funcPrintedName.Length; i++)
            {
                char c = funcPrintedName[i];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        clauseEnd = i;
                        break;
                    }
                }
            }
            if (clauseEnd < 1)
                return false;

            // Strip the parameter-clause parens; a single tuple payload leaves an interior
            // that is itself ONE balanced-paren group spanning the whole remainder.
            var inner = funcPrintedName.Substring(1, clauseEnd - 1).Trim();
            if (inner.Length < 2 || inner[0] != '(' || inner[inner.Length - 1] != ')')
                return false;

            depth = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                        return i == inner.Length - 1;
                }
            }
            return false;
        }

        /// <summary>
        /// Builds a fully-qualified type path by walking up the parent chain.
        /// Returns a dot-joined string, e.g., "OrderContainer.Status" for Status nested
        /// inside OrderContainer.
        /// </summary>
        private static string BuildTypeQualifiedPath(TypeDecl typeDecl)
        {
            var parts = new List<string>();
            BaseDecl? current = typeDecl;
            while (current is TypeDecl td)
            {
                parts.Add(td.Name);
                current = td.ParentDecl;
            }
            parts.Reverse();
            return string.Join(".", parts);
        }

        /// <summary>
        /// Builds the same dot-joined path as <see cref="BuildTypeQualifiedPath"/>, but with each
        /// component spelled the way Swift source spells it.
        /// </summary>
        /// <remarks>
        /// <para>A <see cref="TypeDecl"/> stores its <c>Name</c> after <c>ExtractUniqueName</c>, which
        /// prefixes a C#-keyword name with an underscore — Swift's <c>struct event</c> is held as
        /// <c>_event</c> — and type declarations record no original-name provenance to recover from.
        /// Interface facts are keyed by Swift identifiers, so a lookup built from the sanitized chain
        /// silently misses every keyword-named type.</para>
        /// <para>Undoing the prefix is deterministic in the direction that matters: an underscore
        /// followed by a C# keyword is exactly and only what that sanitizer emits. A Swift type
        /// genuinely named <c>_class</c> inverts to <c>class</c> and finds no fact — the lookup is
        /// then simply silent, the same silence as a library shipped without a .swiftinterface, and
        /// never a false positive.</para>
        /// </remarks>
        private static string BuildSwiftTypeQualifiedPath(TypeDecl typeDecl)
        {
            var parts = new List<string>();
            BaseDecl? current = typeDecl;
            while (current is TypeDecl td)
            {
                parts.Add(UnsanitizeKeywordName(td.Name));
                current = td.ParentDecl;
            }
            parts.Reverse();
            return string.Join(".", parts);
        }

        /// <summary>
        /// Inverse of the C#-keyword escaping <c>ExtractUniqueName</c> applies: strips a leading
        /// underscore when what follows is a C# keyword, leaving every other name untouched.
        /// </summary>
        private static string UnsanitizeKeywordName(string name)
        {
            return name.Length > 1
                && name[0] == '_'
                && SyntaxFacts.GetKeywordKind(name.Substring(1)) != SyntaxKind.None
                ? name.Substring(1)
                : name;
        }

        /// <summary>
        /// Creates a class declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the class declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <param name="genericParameters">The generic parameters for this type.</param>
        /// <returns>The class declaration.</returns>
        private ClassDecl CreateClassDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl, List<GenericArgumentDecl> genericParameters, string? moduleNameOverride = null)
        {
            var swiftTypeName = GetSwiftTypeName(parentDecl, node.Name, moduleNameOverride);

            // Detect actors by checking for conformance to the Swift Actor protocol.
            // Use the stable mangled name ($sScA) to avoid false positives from user-defined protocols named "Actor".
            var isActor = node.Conformances.Any(c => c.MangledName == "$sScA");

            var decl = new ClassDecl
            {
                Name = ExtractUniqueName(node.Name),
                SwiftTypeName = swiftTypeName,
                MangledName = node.MangledName,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                GenericParameters = genericParameters,
                Conformances = BuildFilteredConformances(node, swiftTypeName),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsActor = isActor,
                IsFinal = node.DeclAttributes?.Contains("Final") == true,
                IsModuleInternal = IsNodeModuleInternal(node),
                IsSpiProtected = IsNodeSpiProtected(node),
                SuperclassUsr = node.superclassUsr,
                SuperclassNames = node.superclassNames?.ToList() ?? new List<string>(),
                InheritsConvenienceInitializers = node.inheritsConvenienceInitializers ?? false,
                HasMissingDesignatedInitializers = node.hasMissingDesignatedInitializers ?? false,
            };
            if (!decl.IsModuleInternal)
                decl.IsModuleInternal = IsInternalFromPublicTypeNames(decl);
            if (!decl.IsModuleInternal && IsTypeUnavailableFromSwiftInterface(decl))
                decl.IsModuleInternal = true;
            ApplyActorIsolation(decl);
            ApplyAvailability(decl);
            ApplyObjCRuntimeName(decl);
            ApplyPosition(decl);
            PopulateDocumentation(decl, node);
            return decl;
        }

        /// <summary>
        /// Creates a protocol declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the protocol declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The protocol declaration.</returns>
        private ProtocolDecl CreateProtocolDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl, string? moduleNameOverride = null)
        {
            // Parse associated types from children
            var associatedTypes = new List<AssociatedTypeDecl>();
            foreach (var child in node.Children)
            {
                if (child.DeclKind == "AssociatedType")
                {
                    associatedTypes.Add(new AssociatedTypeDecl
                    {
                        Name = child.Name
                    });
                }
            }

            // Parse inherited protocols from conformances.
            // Protocol conformance entries use Kind == "Conformance" (not "TypeNominal"),
            // so we must accept both kinds. Without this, InheritedProtocols would always
            // be empty for protocols, breaking InheritsCodable, IsClassBoundProtocol,
            // InheritsCaseIterable, and InheritsProtocolWithAssociatedTypes checks.
            // Marker protocols (Sendable, Escapable, Copyable, SendableMetatype) are
            // filtered out — they have no C# representation and would generate ISendable etc.
            var inheritedProtocols = new List<NamedTypeSpec>();
            foreach (var conformance in node.Conformances)
            {
                if (conformance.Kind == kNominal || conformance.Kind == "Conformance")
                {
                    // ObjC protocol conformance entries (e.g., NSCoding) have no Swift
                    // mangled name — they carry a USR like `c:objc(pl)NSCoding`. Record
                    // these via the printedName-derived fallback so IsClassBoundProtocol
                    // can detect NSObject-rooted protocols (NSCoding, NSCopying, etc.) and
                    // route them to the correct carrier: NSObjectProtocol/NSCoding through
                    // the EveryObjCProtocol helper; NSSecureCoding / NSCopying / NSMutableCopying
                    // still suppress EveryProtocol conformance for delegates that require them.
                    if (string.IsNullOrEmpty(conformance.MangledName))
                    {
                        if (!string.IsNullOrEmpty(conformance.usr) &&
                            conformance.usr.StartsWith("c:objc(pl)", StringComparison.Ordinal))
                        {
                            var fallbackName = BuildFallbackProtocolName(conformance);
                            inheritedProtocols.Add(new NamedTypeSpec(fallbackName.ModuleQualifiedName));
                        }
                        continue;
                    }

                    // Mirror the HandleConformance guard: an unsupported demangler
                    // substitution (e.g. `$sSci` for `_Concurrency.AsyncSequence`) would
                    // otherwise throw and HandleNode's catch-all would silently drop the
                    // entire enclosing ProtocolDecl. Fall back to the printedName-derived
                    // identity so the inherited protocol is still recorded.
                    NamedTypeSpec? namedTypeSpec = null;
                    try
                    {
                        var reduction = demangler.Run(conformance.MangledName);
                        if (reduction is TypeSpecReduction typeSpecReduction &&
                            typeSpecReduction.TypeSpec is NamedTypeSpec demangledSpec)
                        {
                            namedTypeSpec = demangledSpec;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            $"Failed to demangle inherited protocol '{conformance.Name}' ({conformance.MangledName}) on protocol '{node.Name}': {ex.Message}. " +
                            $"Falling back to printedName-derived protocol identity.");
                        var fallbackName = BuildFallbackProtocolName(conformance);
                        namedTypeSpec = new NamedTypeSpec(fallbackName.ModuleQualifiedName);
                    }

                    if (namedTypeSpec != null)
                    {
                        // @_originallyDefinedIn: the conformance MangledName encodes the symbol's
                        // ORIGINAL module (e.g. RealityKit), so the demangled spec reads
                        // `RealityKit.HasTransform` even though the protocol is now vended from the
                        // current module (RealityFoundation). The USR records the real defining
                        // module. Prefer it so the emitted base-interface reference resolves to a
                        // type that exists — otherwise an unbound `RealityKit.IHasTransform` leaks
                        // into the inheritance list and Roslyn reports CS0246.
                        if (TryGetModuleFromSwiftUsr(conformance.usr, out var usrModule) &&
                            !string.IsNullOrEmpty(namedTypeSpec.Module) &&
                            usrModule != namedTypeSpec.Module)
                        {
                            var corrected = new NamedTypeSpec($"{usrModule}.{namedTypeSpec.NameWithoutModule}");
                            corrected.GenericParameters.AddRange(namedTypeSpec.GenericParameters);
                            namedTypeSpec = corrected;
                        }

                        // Skip compiler-internal marker protocols that have no C# binding
                        var simpleName = namedTypeSpec.NameWithoutModule;
                        if (simpleName is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype")
                            continue;
                        inheritedProtocols.Add(namedTypeSpec);
                    }
                }
            }

            // Check for a Self requirement in the generic signature.
            //
            // Every Swift protocol has an implicit Self receiver, and swift-api-digester
            // prints every protocol-inheritance requirement as `<Self : OtherProtocol>`
            // in the generic signature. A bare `Contains("Self")` therefore matches any
            // protocol that inherits from another protocol (e.g., `MusicItem : Sendable`
            // emits `<Self : Swift.Sendable>`), even when the protocol has no semantic
            // Self requirement. That incorrectly gates the interface into the generic
            // `IFoo<TSelf> where TSelf : IFoo<TSelf>` F-bound form, which downstream
            // constraint emission then breaks at use sites (CS0305 / CS0311).
            //
            // A real Self requirement manifests either as:
            //   * an associated-type reference through Self: `Self.X` (property-style access)
            //   * a same-type constraint anchored on Self: `Self == X` or `Self.X == Y`
            //
            // Simple protocol inheritance `<Self : Foo>` does NOT produce a `Self.` or
            // `Self ==` substring, so the tightened pattern distinguishes the two cases.
            //
            // Method-level Self usage (`func foo() -> Self`) is still detected via the
            // separate HasMethodSelfTypeParams check that walks method signatures for
            // τ_0_0 references, so protocols whose only Self usage is in method
            // parameters/returns remain covered.
            // Finding 19: query the parsed signature instead of substring/regex scans of the raw text.
            var parsedSig = GenericSignatureParser.ParseSignature(node.GenericSig);

            // A real Self requirement manifests as an associated-type path through Self
            // (`Self.X`, a non-direct subject rooted at Self) or a same-type pin on Self
            // (`Self == X`). Simple protocol inheritance (`Self : Foo`) does NOT count. In
            // unsugared api-digester output Self renders as τ_0_0 and this never fires — method-level
            // Self usage is covered separately by the HasMethodSelfTypeParams signature walk — so the
            // check is faithful to the legacy "Self." / "Self ==" substring test for the sugared form.
            bool hasSelfRequirement = parsedSig.Requirements.Any(r =>
                string.Equals(r.SubjectRoot, "Self", StringComparison.Ordinal) &&
                (!r.IsDirect || r.Kind == GenericRequirementKind.SameType));

            // Check if class-bound (requires AnyObject). AnyObject may appear in conformances OR as a
            // DIRECT Self conformance in the generic signature (e.g. "<τ_0_0 : AnyObject>" for
            // a protocol declared ": AnyObject"). A constraint on an associated type
            // ("τ_0_0.Element : AnyObject") must NOT count — hence the IsDirect filter.
            //
            // The subject root arrives in BOTH dialects and they mean the same thing: a module
            // compiled from source prints the desugared `τ_0_0`, while `swift-api-digester
            // -dump-sdk` — the ABI source for an Apple-direct binding — prints the sugared `Self`
            // and never emits a `τ` at all. Matching only the desugared root graded every sugared
            // `: AnyObject` protocol opaque, which is an ABI mismatch rather than a cosmetic one:
            // the proxy then writes its witness table into the 5-word opaque container while Swift
            // reads a 2-word class existential, so the callee dispatches through a zero witness
            // table. Accepting both roots mirrors the superclass-constraint block below, which has
            // always taken either spelling.
            bool isClassBound = inheritedProtocols.Any(p =>
                p.Name == "AnyObject" ||
                p.Name == "Swift.AnyObject") ||
                parsedSig.Requirements.Any(r =>
                    r.IsDirect && r.Kind == GenericRequirementKind.Conformance &&
                    (string.Equals(r.SubjectRoot, "τ_0_0", StringComparison.Ordinal) ||
                     string.Equals(r.SubjectRoot, "Self", StringComparison.Ordinal)) &&
                    r.TargetSimpleName == "AnyObject");

            // A superclass constraint (`protocol P : SomeClass`) is also class-bound: its
            // existential carries the compact `[classRef][witnessTables]` layout rather than
            // the opaque value-existential layout, so it must marshal through a class-bound
            // container. swift-api-digester encodes the superclass constraint ONLY in the
            // generic signature as a direct-Self requirement (`Self : SomeClass` sugared /
            // `τ_0_0 : SomeClass` unsugared); unlike inherited *protocols*, the superclass is
            // NOT listed among the protocol's conformances. So a direct-Self constraint whose
            // target is neither AnyObject, a marker, nor one of the protocol's own conformances
            // is a superclass constraint => class-bound. (Protocol-inheritance class-boundness,
            // e.g. `HasCollision : HasTransform : Entity`, is resolved transitively downstream
            // by ModuleProcessor.ProtocolIsClassBoundTransitive walking inherited protocols.)
            if (!isClassBound)
            {
                var conformanceSimpleNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var conformance in node.Conformances)
                {
                    if (!string.IsNullOrEmpty(conformance.Name))
                        conformanceSimpleNames.Add(conformance.Name.Split('.')[^1]);
                }

                foreach (var r in parsedSig.Requirements)
                {
                    if (!r.IsDirect || r.Kind != GenericRequirementKind.Conformance)
                        continue;
                    if (!string.Equals(r.SubjectRoot, "τ_0_0", StringComparison.Ordinal) &&
                        !string.Equals(r.SubjectRoot, "Self", StringComparison.Ordinal))
                        continue;
                    var simpleTarget = r.TargetSimpleName;
                    if (simpleTarget is "AnyObject" or "Sendable" or "Escapable"
                        or "Copyable" or "SendableMetatype" or "Any")
                        continue;
                    if (conformanceSimpleNames.Contains(simpleTarget))
                        continue; // inherited protocol, not a superclass
                    isClassBound = true; // superclass constraint
                    break;
                }
            }

            var decl = new ProtocolDecl
            {
                Name = ExtractUniqueName(node.Name),
                SwiftTypeName = GetSwiftTypeName(parentDecl, node.Name, moduleNameOverride),
                MangledName = node.MangledName,
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                AssociatedTypes = associatedTypes,
                HasSelfRequirement = hasSelfRequirement,
                InheritedProtocols = inheritedProtocols,
                GenericSignature = node.GenericSig,
                IsClassBound = isClassBound,
                // An @objc protocol's existential is a single ObjC object pointer (no Swift
                // witness-table word, no `…Mp` descriptor) even when it is also class-bound,
                // so it must not marshal through the 16-byte ClassExistentialContainer1 carrier.
                IsObjC = node.DeclAttributes is not null &&
                    Array.IndexOf(node.DeclAttributes, "ObjC") != -1,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsModuleInternal = IsNodeModuleInternal(node),
                IsSpiProtected = IsNodeSpiProtected(node)
            };
            if (!decl.IsModuleInternal)
                decl.IsModuleInternal = IsInternalFromPublicTypeNames(decl);
            if (!decl.IsModuleInternal && IsTypeUnavailableFromSwiftInterface(decl))
                decl.IsModuleInternal = true;
            ApplyActorIsolation(decl);
            ApplyAvailability(decl);
            ApplyObjCRuntimeName(decl);
            ApplyPosition(decl);
            PopulateDocumentation(decl, node);

            // Mark protocols whose methods have @convention(c)/@convention(block) closure parameters
            if (_facts.ConventionCProtocols.Contains(decl.Name))
                decl.HasConventionCClosureParameters = true;

            return decl;
        }

        /// <summary>
        /// Creates a method declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the method declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The method declaration.</returns>
        private MethodDecl CreateMethodDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            // Extract parameter names from the signature
            var paramNames = ExtractParameterNames(node.PrintedName);
            string mangledName = node.Kind == "Constructor" ? PatchMangledName(node.MangledName) : node.MangledName;

            IReduction? reduction = null;
            try
            {
                reduction = demangler.Run(mangledName);
            }
            catch (Exception e)
            {
                _logger.LogWarning($"Demangling failed for '{node.Name}' ({mangledName}): {e.Message}");
            }
            FunctionReduction? functionReduction = reduction as FunctionReduction;

            // Detect failable initializer: init? returns Optional<Self>
            // The first child of a Constructor node is the return type.
            // For init?, it will have name == "Optional".
            bool isFailable = node.Kind == "Constructor" &&
                node.Children.Any() &&
                node.Children.First().Name == "Optional";

            var (methodCSharpName, methodOriginalSwiftName) = ExtractUniqueNameWithOriginal(node.Name);
            var methodDecl = new MethodDecl
            {
                Name = methodCSharpName,
                OriginalSwiftName = methodOriginalSwiftName,
                // Constructors for structs are named with a trailing 'C' instead of 'c'
                // because a constructor wrapper is missing in the library.
                MangledName = mangledName,
                MethodType = node.@static ?? false ? MethodType.Static : MethodType.Instance,
                IsConstructor = node.Kind == "Constructor",
                IsFailable = isFailable,
                CSSignature = new List<ArgumentDecl>(),
                GenericParameters = GenericSignatureParser.ParseGenericSignature(node.GenericSig, node.sugared_genericSig),
                RawGenericSig = node.GenericSig,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                Throws = node.throwing ?? false,
                // Primary source: demangler's FunctionReduction. Fallback: walk the raw demangled
                // node tree for an AsyncAnnotation marker when no FunctionReduction is produced
                // (Constructor/accessor symbols, which the reducer intentionally does not reduce).
                // Finding 17: this replaces DetectAsyncFromMangledName's "Ya" substring scan with a
                // grammar-grounded tree walk that cannot false-positive on an incidental "Ya".
                IsAsync = functionReduction?.Function?.IsAsync
                    ?? demangler.HasAsyncMarker(mangledName),
                IsSynthesizedAccessor = false,
                IsMutating = node.funcSelfKind == "Mutating",
                IsConsuming = node.funcSelfKind == "Consuming",
                IsBorrowing = node.funcSelfKind == "Borrowing",
                IsFinal = node.DeclAttributes?.Contains("Final") == true,
                IsOverride = node.overriding == true || node.DeclAttributes?.Contains("Override") == true,
                IsImplicit = node.@implicit == true,
                IsModuleInternal = IsNodeModuleInternal(node) ||
                    IsInternalFromSwiftInterface(parentDecl.Name, node.PrintedName, node),
                IsSpiProtected = IsNodeSpiProtected(node),
                IsObjCOptional = node.DeclAttributes?.Contains("Optional") == true,
                IsExtensionMethod = node.isFromExtension == true,
                IsProtocolRequirement = node.protocolReq == true,
            };

            // Suppress underscore-prefixed methods without explicit AccessControl.
            // Swift convention: _-prefixed members are internal. The ABI JSON includes them
            // for binary compatibility but they're not callable from external code.
            // Only suppress if no AccessControl attribute (explicitly public _-prefixed APIs
            // like _NIOFileSystem get AccessControl and should be preserved).
            if (!methodDecl.IsModuleInternal && node.Name.StartsWith("_") &&
                (node.DeclAttributes is null || Array.IndexOf(node.DeclAttributes, "AccessControl") == -1))
            {
                methodDecl.IsModuleInternal = true;
            }

            // Suppress unconditionally unavailable methods
            if (!methodDecl.IsModuleInternal && parentDecl is TypeDecl parentTypeForUnavail &&
                IsUnavailableFromSwiftInterface(parentTypeForUnavail, node.PrintedName))
            {
                methodDecl.IsModuleInternal = true;
            }

            // Negative-space detection: if the member is NOT in the public swiftinterface,
            // it's internal. Skip implicit NON-CONSTRUCTOR members (synthesized accessors, etc.)
            // which are public but don't appear in the swiftinterface.
            // Constructors are NOT skipped even if implicit: implicit inits on types with
            // @_hasMissingDesignatedInitializers are internal and MUST be caught.
            // Public implicit inits DO appear in the public swiftinterface (memberwise inits, etc.).
            if (!methodDecl.IsModuleInternal &&
                (node.@implicit != true || methodDecl.IsConstructor))
            {
                bool isCurrentModuleMember = !string.IsNullOrEmpty(node.ModuleName) &&
                    node.ModuleName == CurrentModuleName;
                if (IsInternalFromPublicMemberNames(parentDecl, node.PrintedName, isCurrentModuleMember))
                    methodDecl.IsModuleInternal = true;
            }

            // Suppress implicit inherited constructors that are not callable from external code.
            // Swift's initialization safety rules: when a class defines its own designated inits
            // (inheritsConvenienceInitializers=false) and all designated inits are visible
            // (hasMissingDesignatedInitializers=false), implicit inherited constructors from
            // the superclass are NOT available. Emitting wrappers for them causes compilation errors
            // like "missing argument for parameter 'name'" because the implicit init doesn't exist.
            if (!methodDecl.IsModuleInternal && methodDecl.IsImplicit && methodDecl.IsOverride &&
                methodDecl.IsConstructor && parentDecl is ClassDecl classParentForImplicit &&
                !classParentForImplicit.InheritsConvenienceInitializers &&
                !classParentForImplicit.HasMissingDesignatedInitializers)
            {
                methodDecl.IsModuleInternal = true;
            }

            // Look up typed throws error type from swiftinterface data
            if (methodDecl.Throws)
            {
                // Try type-scoped key first (e.g., "TypedThrowingParser.parse(_:)")
                var throwsScopedKey = $"{parentDecl.Name}.{node.PrintedName}";
                if (!_facts.TypedThrowsErrors.TryGetValue(throwsScopedKey, out var errorTypeName))
                {
                    // Try module-level key (free functions, e.g., "parseNumber(_:)")
                    _facts.TypedThrowsErrors.TryGetValue(node.PrintedName, out errorTypeName);
                }

                if (errorTypeName != null)
                {
                    // EOF-strict Parse throws on a malformed/over-captured error-type string (the
                    // typed-throws extractor is not depth-aware). Leave ThrownErrorType null on failure
                    // so the method still emits — just without the typed-error refinement — rather than
                    // dropping the entire declaration via HandleNode's catch.
                    try
                    {
                        methodDecl.ThrownErrorType = TypeSpecParser.Parse(errorTypeName);
                    }
                    catch (TypeSpecParseException ex)
                    {
                        _logger.LogDebug($"Failed to parse typed-throws error type '{errorTypeName}' for '{node.PrintedName}': {ex.Message}");
                    }
                }
            }

            // Apply member-level actor isolation from swiftinterface data
            if (parentDecl is TypeDecl parentType)
            {
                ApplyMemberActorIsolation(methodDecl, parentType, node.PrintedName);
                ApplyMemberAvailability(methodDecl, parentType, node.PrintedName, node);
                ApplyMemberPosition(methodDecl, parentType, node.PrintedName);
            }
            else if (parentDecl is ModuleDecl)
            {
                // Free functions: check if the function itself is actor-isolated.
                // The actorIsolatedMembers set uses bare printedName for free functions.
                if (_facts.ActorIsolatedMembers.Contains(node.PrintedName))
                    methodDecl.IsActorIsolated = true;
                if (_facts.MainActorIsolatedMembers.Contains(node.PrintedName))
                    methodDecl.IsMainActorIsolated = true;
                // Free function availability: keyed by bare printedName, with optional
                // disamb suffix when overload disambiguation is needed.
                var freeSig = ComputeAbiParamSignature(node);
                if (!string.IsNullOrEmpty(freeSig) &&
                    _facts.AvailabilityAnnotations.TryGetValue(
                        MemberSignatureNormalizer.ComposeKey(node.PrintedName, freeSig),
                        out var disambFree))
                {
                    methodDecl.AvailabilityAnnotations = disambFree;
                }
                else if (_facts.AvailabilityAnnotations.TryGetValue(node.PrintedName, out var freeFuncAnnotations))
                {
                    methodDecl.AvailabilityAnnotations = freeFuncAnnotations;
                }
                // Free function position uses the same bare-printedName key the parser
                // emitted under FreeFunctionLine.
                if (_facts.AvailabilityAnnotationPositions.TryGetValue(node.PrintedName, out var freeFuncPos))
                    methodDecl.Position = freeFuncPos;
            }

            // Actor-isolated instance methods are effectively async from outside the actor —
            // Swift requires `await` at the call site even for sync declarations. Route them
            // through the async @_cdecl wrapper pipeline so the Swift wrapper emits
            // `Task { await self.method() }` and the C# side surfaces a Task<T>-returning API.
            // Scope: instance methods (not @MainActor — exposed as sync per Xamarin.iOS precedent;
            // not nonisolated — opts out of isolation).
            //
            // Constructors on @<CustomActor>-isolated types follow the same pattern: Swift 6 has
            // no synchronous entry into a custom global actor's isolation domain, so the binding
            // surfaces them as `static Task<T> CreateAsync(...)` factories. The Swift wrapper
            // becomes `Task { let result = try await Type.init(...) }` — Swift inserts the actor
            // hop implicitly at the await. Constructors on Swift `actor` types stay sync because
            // their default inits are nonisolated from outside.
            if (!methodDecl.IsAsync &&
                !methodDecl.IsNonisolated &&
                !methodDecl.IsMainActorIsolated)
            {
                bool parentIsActor = parentDecl is ClassDecl { IsActor: true };
                bool parentIsCustomActorIsolated = parentDecl is TypeDecl { IsCustomActorIsolated: true };

                if (!methodDecl.IsConstructor &&
                    methodDecl.MethodType == MethodType.Instance &&
                    (parentIsActor || methodDecl.IsActorIsolated))
                {
                    methodDecl.IsAsync = true;
                }
                else if (methodDecl.IsConstructor && parentIsCustomActorIsolated)
                {
                    methodDecl.IsAsync = true;
                }
            }

            PopulateDocumentation(methodDecl, node);

            // Look up internal parameter names from swiftinterface data
            List<string>? internalParamNames = null;
            // Try type-scoped key first (e.g., "Dog.speak(_:_:)")
            var paramScopedKey = $"{parentDecl.Name}.{node.PrintedName}";
            if (!_facts.ParameterNames.TryGetValue(paramScopedKey, out internalParamNames))
            {
                // Try module-level key (free functions, e.g., "sumTwo(_:_:)")
                _facts.ParameterNames.TryGetValue(node.PrintedName, out internalParamNames);
            }

            // Install a fresh opaque-parameter capture for this method. CreateTypeSpec
            // will append synthetic GenericArgumentDecl entries here for any parameter
            // whose ABI node is GenericTypeParam with a "some ..." printedName. After
            // the loop finishes we merge them into methodDecl.GenericParameters so the
            // existing generic-method emission path handles them.
            var prevOpaqueCapture = _opaqueParamCapture;
            _opaqueParamCapture = new List<GenericArgumentDecl>();
            try
            {
                for (int i = 0; i < node.Children.Count(); i++)
                {
                    var typeSpec = CreateTypeSpec(node.Children.ElementAt(i));

                    var childNode = node.Children.ElementAt(i);

                    // Populate PrivateName from swiftinterface data.
                    // i=0 is the return type in paramNames (no corresponding internal name).
                    // i>=1 are actual parameters; internalParamNames index is (i-1).
                    var privateName = string.Empty;
                    if (internalParamNames != null && i >= 1 && (i - 1) < internalParamNames.Count)
                    {
                        privateName = internalParamNames[i - 1];
                    }

                    var ownership = ParseParameterOwnership(childNode.paramValueOwnership);

                    methodDecl.CSSignature.Add(new ArgumentDecl
                    {
                        SwiftTypeSpec = typeSpec,
                        Name = paramNames[i].Name,
                        OriginalSwiftName = paramNames[i].OriginalSwiftName,
                        PrivateName = privateName,
                        IsInOut = ownership == ParameterOwnership.InOut,
                        Ownership = ownership,
                        IsGeneric = childNode.Name == "GenericTypeParam",
                        HasDefaultArg = childNode.hasDefaultArg == true,
                        ParentDecl = methodDecl,
                        ModuleDecl = moduleDecl
                    });
                }

                if (_opaqueParamCapture.Count > 0)
                {
                    methodDecl.GenericParameters.AddRange(_opaqueParamCapture);
                }
            }
            finally
            {
                _opaqueParamCapture = prevOpaqueCapture;
            }

            // Detect variadic parameters per-overload. A Swift variadic parameter (T...)
            // lowers to Array<T> and shares its ABI with a plain [T] parameter; the two
            // differ only by the mangled-name "d" variadic marker. We combine three signals,
            // in order of cost and reliability:
            //
            //   1. printedName "...": swift-api-digester renders SOME variadics with a
            //      trailing "..." that CreateTypeSpec lowers to Swift.Array<E> with
            //      E.IsVariadic. This is a sound positive (no non-variadic param carries
            //      "...") but INCOMPLETE — e.g. result-builder buildBlock overloads render
            //      their variadic parameter as a plain "[T]" with no "...".
            //   2. demangled "d" marker: the per-overload mangled name carries the
            //      authoritative variadic marker. This catches the overloads tier 1 misses
            //      and distinguishes two overloads that share a printedName but differ only
            //      in variadic-ness, e.g. PageBuilder:
            //          buildBlock(_ components: [Page])      // not variadic
            //          buildBlock(_ components: [Page]...)   // variadic
            //      Both print as "[Page]"; only the "d" marker tells them apart. @_cdecl
            //      wrappers can't call variadic methods, so getting this per-overload flag
            //      right is what keeps us from emitting an invalid "[Page] as variadic" cast.
            //   3. name-keyed swiftinterface fact: a guarded last-resort fallback (see below).
            methodDecl.HasVariadicParameter =
                methodDecl.CSSignature.Skip(1).Any(p => IsVariadicArraySpec(p.SwiftTypeSpec));
            if (!methodDecl.HasVariadicParameter &&
                functionReduction?.Function?.ParameterList is TupleTypeSpec paramTuple)
            {
                methodDecl.HasVariadicParameter = HasVariadicElement(paramTuple);
            }
            if (!methodDecl.HasVariadicParameter && functionReduction is null)
            {
                // Tier 2b: the reducer produces no FunctionReduction for some symbols —
                // notably constructors/allocators, which have no reducer rule — so tier 2
                // can't reach the parameter list. The demangled NODE tree is still built,
                // and the authoritative "d" variadic marker lives in it, so consult it
                // directly. Like tier 2 this is per-overload-exact: it flags a variadic
                // init(x: T...) while leaving a plain init(x: [T]) sibling — which shares
                // the same Array ABI shape and printedName — untouched, so unlike the
                // name-keyed tier-3 fallback it never over-skips a plain-array overload.
                methodDecl.HasVariadicParameter = demangler.HasVariadicParameterMarker(mangledName);
            }
            if (!methodDecl.HasVariadicParameter &&
                !methodDecl.CSSignature.Skip(1).Any(p => IsArraySpec(p.SwiftTypeSpec)))
            {
                // Last-resort fallback: only when no array-typed parameter exists to inspect,
                // so neither precise signal above can apply (e.g. an unforeseen ABI lowering
                // that doesn't produce an Array<E>). The !IsArraySpec guard is what makes this
                // safe: the name-keyed fact cannot tell a variadic overload from a plain-[T]
                // sibling that shares its name, so we only consult it once tiers 1-2 have
                // declined AND there is no array parameter they could have inspected.
                // Use BuildTypeQualifiedPath for nested types (e.g. "DisposeBag.DisposableBuilder").
                var variadicScopedKey = parentDecl is TypeDecl varParentType
                    ? $"{BuildTypeQualifiedPath(varParentType)}.{node.PrintedName}"
                    : node.PrintedName;
                if (_facts.VariadicMembers.Contains(variadicScopedKey) ||
                    _facts.VariadicMembers.Contains(node.PrintedName))
                {
                    methodDecl.HasVariadicParameter = true;
                }
            }

            // Apply default parameter value expressions from swiftinterface data.
            // Must happen after the argument-construction loop since it mutates CSSignature entries.
            if (parentDecl is TypeDecl parentTypeForDefaults)
            {
                ApplyMemberDefaultValues(methodDecl, parentTypeForDefaults, node.PrintedName);
                ApplyMemberAutoclosureFlags(methodDecl, parentTypeForDefaults, node.PrintedName);
                ApplyMemberConstLiteralFlags(methodDecl, parentTypeForDefaults, node.PrintedName);
                if (parentTypeForDefaults is ProtocolDecl)
                    ApplyMemberClosureAttributeFlags(methodDecl, parentTypeForDefaults, node.PrintedName);
            }
            else
            {
                ApplyFreeFunctionDefaultValues(methodDecl, node.PrintedName);
                ApplyFreeFunctionAutoclosureFlags(methodDecl, node.PrintedName);
                ApplyFreeFunctionConstLiteralFlags(methodDecl, node.PrintedName);
            }

            return methodDecl;
        }

        /// <summary>
        /// Whether a type spec is the lowered form of a Swift variadic parameter:
        /// Array&lt;E&gt; (or Swift.Array&lt;E&gt;) whose element E carries IsVariadic=true.
        /// </summary>
        internal static bool IsVariadicArraySpec(TypeSpec spec)
        {
            return spec is NamedTypeSpec named &&
                (named.Name == "Swift.Array" || named.Name == "Array") &&
                named.GenericParameters.Count > 0 &&
                named.GenericParameters[0].IsVariadic;
        }

        /// <summary>
        /// Whether a type spec is an array (Array&lt;E&gt; / Swift.Array&lt;E&gt;), variadic or not.
        /// Used to decide whether a method has a parameter the per-overload variadic
        /// signal can inspect.
        /// </summary>
        private static bool IsArraySpec(TypeSpec spec)
        {
            return spec is NamedTypeSpec named &&
                (named.Name == "Swift.Array" || named.Name == "Array");
        }

        /// <summary>
        /// Maps the ABI JSON <c>paramValueOwnership</c> string (Swift's <c>ParamSpecifier</c>) to
        /// <see cref="ParameterOwnership"/>. The string values were confirmed empirically against
        /// <c>swift-frontend -emit-abi-descriptor-path</c>: <c>consuming</c> → <c>"Owned"</c>,
        /// <c>borrowing</c> → <c>"Shared"</c>, <c>inout</c> → <c>"InOut"</c>; a plain parameter omits
        /// the field (null). Unknown or absent values fall back to <see cref="ParameterOwnership.Default"/>.
        /// </summary>
        private static ParameterOwnership ParseParameterOwnership(string? paramValueOwnership) =>
            paramValueOwnership switch
            {
                "InOut" => ParameterOwnership.InOut,
                "Shared" => ParameterOwnership.Shared,
                "Owned" => ParameterOwnership.Owned,
                _ => ParameterOwnership.Default,
            };

        /// <summary>
        /// Checks whether any element in a demangled parameter list is a variadic parameter.
        /// Variadic params (T...) are demangled as Array&lt;T&gt; where the inner T has IsVariadic=true.
        /// </summary>
        internal static bool HasVariadicElement(TupleTypeSpec paramTuple)
        {
            foreach (var element in paramTuple.Elements)
            {
                if (IsVariadicArraySpec(element))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Creates an operator declaration from a node.
        /// </summary>
        /// <param name="node">The node representing the operator declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The operator declaration, or null if the underlying method cannot be created.</returns>
        private OperatorDecl? CreateOperatorDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var methodDecl = CreateMethodDecl(node, parentDecl, moduleDecl);
            if (methodDecl == null) return null;

            // CSSignature[0] is return type, remaining are parameters
            // For operators, Swift static func operators have the operands as parameters
            var paramCount = methodDecl.CSSignature.Count - 1;
            var isUnary = paramCount == 1;

            // Detect prefix vs postfix for unary operators
            // Swift prefix operators have 'prefix' in DeclAttributes
            bool isPrefix = true; // Default to prefix for unary operators
            if (node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "postfix") != -1)
            {
                isPrefix = false;
            }

            var operatorDecl = new OperatorDecl
            {
                Name = node.Name,
                OperatorSymbol = node.Name,
                Kind = isUnary ? OperatorKind.Unary : OperatorKind.Binary,
                IsPrefix = isPrefix,
                UnderlyingMethod = methodDecl,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                AvailabilityAnnotations = methodDecl.AvailabilityAnnotations,
                Position = methodDecl.Position,
            };
            PopulateDocumentation(operatorDecl, node);
            return operatorDecl;
        }

        /// <param name="swiftFieldName">The property's Swift-source identifier (backtick-stripped, before
        /// property-wrapper sanitization). Distinct from <paramref name="fieldName"/>, which is already
        /// C#-shaped; interface facts are keyed by what the .swiftinterface spells, not what C# emits.</param>
        private List<AccessorDecl> HandleAccessors(IEnumerable<Node> accessors, string fieldName, BaseDecl parentDecl, ModuleDecl moduleDecl, string swiftFieldName)
        {
            var result = new List<AccessorDecl>();

            // Sanitize property wrapper projected value names ($volume -> projectedVolume)
            // The $ prefix is valid in Swift but not in C# identifiers
            var sanitizedFieldName = NameProvider.SanitizePropertyWrapperName(fieldName);

            foreach (var accessor in accessors)
            {
                switch (accessor.AccessorKind)
                {
                    case "get":
                        result.Add(CreateGetAccessor(accessor, sanitizedFieldName, parentDecl, moduleDecl, swiftFieldName));
                        break;
                    case "set":
                        result.Add(CreateSetAccessor(accessor, sanitizedFieldName, parentDecl, moduleDecl));
                        break;
                    case "_modify":
                        // Optimization accessor, not needed for correctness
                        break;
                    default:
                        _logger.LogWarning($"Unsupported accessor kind '{accessor.AccessorKind}' encountered.");
                        break;
                }
            }

            return result;
        }

        private GetAccessorDecl CreateGetAccessor(Node accessor, string fieldName, BaseDecl parentDecl, ModuleDecl moduleDecl, string swiftFieldName)
        {
            // Accessor async-ness has no direct representation in the ABI JSON (accessor nodes carry
            // `throwing` but no async flag) and an async accessor's mangled name carries no `Ya` marker,
            // so it must be inferred. Two independent oracles answer it, and either saying "async" wins:
            //
            //  1. The TBD symbol table: an async accessor exports a sibling `{getter}Tu` symbol, or for a
            //     class property dispatched through a thunk, `{getter}TjTu`. ManglingProbes.IsAsyncAccessor
            //     owns both variants. This oracle goes silent whenever the TBD symbol set is incomplete —
            //     a stub library shipped without one, or a .tbd shape the parser reads as empty — and its
            //     silence is indistinguishable from "synchronous".
            //  2. The .swiftinterface: the source text literally spells `get async`, harvested into
            //     AsyncAccessorMembers keyed by the type-qualified path as Swift source spells it —
            //     BuildSwiftTypeQualifiedPath's shape, not the C#-sanitized one. This oracle goes
            //     silent when no .swiftinterface is available, or when the accessor is declared
            //     somewhere the walker's key shape can't render.
            //
            // Reading only one leaves an async getter emitted as a synchronous one: at best the @_cdecl
            // property wrapper fails to compile, at worst a `get async throws` accessor lands on a direct
            // CallConvSwift P/Invoke with a `ref SwiftError` out-param pointed at an async entry point,
            // which compiles and then mismatches the ABI on the first read. A disagreement between the two
            // is itself worth surfacing: it is the signal that names a broken TBD or a stale walker key.
            var tbdSaysAsync = ManglingProbes.IsAsyncAccessor(_demangledTbd.AllSymbols, accessor.MangledName);
            // A type can declare `static var value` and `var value` side by side, and each
            // exports its own getter. The interface fact prefixes the type-level one so the
            // two namespaces stay apart — without it, marking the static getter async would
            // drag its synchronous instance namesake onto the async path as well.
            var isStaticAccessor = accessor.@static ?? false;
            var asyncFactKey = parentDecl is TypeDecl asyncParentType
                ? $"{BuildSwiftTypeQualifiedPath(asyncParentType)}.{swiftFieldName}"
                : swiftFieldName;
            if (isStaticAccessor)
            {
                asyncFactKey = $"static {asyncFactKey}";
            }
            var interfaceSaysAsync = _facts.AsyncAccessorMembers.Contains(asyncFactKey);
            if (tbdSaysAsync != interfaceSaysAsync)
            {
                _logger.LogDebug(
                    "Async-accessor oracles disagree for '{Key}' ({Mangled}): TBD says {Tbd}, .swiftinterface says {Interface}. Treating the getter as async.",
                    asyncFactKey, accessor.MangledName, tbdSaysAsync, interfaceSaysAsync);
            }
            var isAsync = tbdSaysAsync || interfaceSaysAsync;

            // Build generic parameters for the accessor method.
            // If the accessor has its own GenericSig, parse it. Otherwise, if the parent type is generic,
            // copy the type's generic parameters so the accessor method has the correct generic context.
            var genericParameters = new List<GenericArgumentDecl>();
            if (!string.IsNullOrEmpty(accessor.GenericSig))
            {
                genericParameters = GenericSignatureParser.ParseGenericSignature(accessor.GenericSig, accessor.sugared_genericSig);
            }
            else if (parentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
            {
                genericParameters = new List<GenericArgumentDecl>(typeDecl.GenericParameters);
            }

            var returnTypeSpec = CreateTypeSpec(accessor.Children.ElementAt(0));

            var methodDecl = new MethodDecl
            {
                Name = $"{fieldName}_Get",
                MangledName = accessor.MangledName,
                MethodType = isStaticAccessor ? MethodType.Static : MethodType.Instance,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = returnTypeSpec,
                        Name = string.Empty,
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = TypeSpecHelpers.IsGenericTypeParameter(returnTypeSpec),
                        ParentDecl = parentDecl,
                        ModuleDecl = moduleDecl
                    }
                },
                GenericParameters = genericParameters,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                Throws = accessor.throwing ?? false,
                IsAsync = isAsync,
                // Accessors mark mutation via the "Mutating" DeclAttribute (lazy-var getters, mutating get).
                // Plain methods use funcSelfKind, but accessor nodes don't carry that — use either signal.
                IsMutating = accessor.DeclAttributes?.Contains("Mutating") == true
                    || accessor.funcSelfKind == "Mutating",
                IsConsuming = accessor.funcSelfKind == "Consuming",
                IsBorrowing = accessor.funcSelfKind == "Borrowing",
                IsSynthesizedAccessor = true,
                IsFinal = accessor.DeclAttributes?.Contains("Final") == true,
            };

            // Apply member-level actor isolation to accessor methods
            if (parentDecl is TypeDecl getParentType)
                ApplyMemberActorIsolation(methodDecl, getParentType, fieldName);

            return new GetAccessorDecl { Method = methodDecl };
        }

        private SetAccessorDecl CreateSetAccessor(Node accessor, string fieldName, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            // Build generic parameters for the accessor method (same logic as CreateGetAccessor).
            var genericParameters = new List<GenericArgumentDecl>();
            if (!string.IsNullOrEmpty(accessor.GenericSig))
            {
                genericParameters = GenericSignatureParser.ParseGenericSignature(accessor.GenericSig, accessor.sugared_genericSig);
            }
            else if (parentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
            {
                genericParameters = new List<GenericArgumentDecl>(typeDecl.GenericParameters);
            }

            // The setter has two children:
            // - Index 0: Void (return type)
            // - Index 1: The parameter type (value to set)
            var valueTypeSpec = CreateTypeSpec(accessor.Children.ElementAt(1));

            var methodDecl = new MethodDecl
            {
                Name = $"{fieldName}_Set",
                MangledName = accessor.MangledName,
                MethodType = accessor.@static ?? false ? MethodType.Static : MethodType.Instance,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    // Return type (void for setters - empty tuple)
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = TupleTypeSpec.Empty,
                        Name = string.Empty,
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = false,
                        ParentDecl = parentDecl,
                        ModuleDecl = moduleDecl
                    },
                    // Parameter (value) - at index 1, after the void return type
                    new ArgumentDecl
                    {
                        SwiftTypeSpec = valueTypeSpec,
                        Name = "value",
                        PrivateName = string.Empty,
                        IsInOut = false,
                        IsGeneric = TypeSpecHelpers.IsGenericTypeParameter(valueTypeSpec),
                        ParentDecl = parentDecl,
                        ModuleDecl = moduleDecl
                    }
                },
                GenericParameters = genericParameters,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                Throws = false,
                IsAsync = false,
                IsSynthesizedAccessor = true,
                IsFinal = accessor.DeclAttributes?.Contains("Final") == true,
            };

            // Apply member-level actor isolation to accessor methods
            if (parentDecl is TypeDecl setParentType)
                ApplyMemberActorIsolation(methodDecl, setParentType, fieldName);

            return new SetAccessorDecl { Method = methodDecl };
        }

        private PropertyDecl CreatePropertyDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var typeSpec = CreateTypeSpec(node.Children.ElementAt(0));

            // Strip Swift backtick escaping (e.g., `subscript` → subscript).
            // Methods already do this via ExtractUniqueNameWithOriginal.
            var rawName = node.Name;
            if (rawName.Length >= 2 && rawName[0] == '`' && rawName[rawName.Length - 1] == '`')
                rawName = rawName.Substring(1, rawName.Length - 2);

            // Sanitize property wrapper projected value names ($volume -> projectedVolume)
            var sanitizedName = NameProvider.SanitizePropertyWrapperName(rawName);

            // When sanitization renamed a projected value, preserve the raw Swift identifier as
            // OriginalSwiftName so GetSwiftName() recovers `$volume` for Swift emission — the same
            // provenance methods carry via ExtractUniqueNameWithOriginal. Without it, any Swift
            // read of the member (e.g. a synthesized CSM extension getter) emits the C#-safe
            // `__self.projectedVolume`, which swiftc rejects. Set ONLY when the name actually
            // changed, honoring OriginalSwiftName's "null unless the parser modified Name" contract.
            var projectedOriginalSwiftName = sanitizedName != rawName ? rawName : null;

            var decl = new PropertyDecl
            {
                SwiftTypeSpec = typeSpec,
                Name = sanitizedName,
                OriginalSwiftName = projectedOriginalSwiftName,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsStatic = node.@static ?? false,
                HasStorage = node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "HasStorage") != -1,
                IsOverride = node.overriding == true || node.DeclAttributes?.Contains("Override") == true,
                IsFinal = node.DeclAttributes?.Contains("Final") == true,
                IsSpiProtected = IsNodeSpiProtected(node),
                IsModuleInternal = IsNodeModuleInternal(node),
                IsObjCOptional = node.DeclAttributes?.Contains("Optional") == true,
                IsObjCDynamic = node.DeclAttributes is not null
                    && Array.IndexOf(node.DeclAttributes, "ObjC") != -1
                    && Array.IndexOf(node.DeclAttributes, "Dynamic") != -1,
                IsProtocolRequirement = node.protocolReq == true,
                IsFromExtension = node.isFromExtension == true,
                ReferenceOwnership = ParseReferenceOwnership(node),
                // rawName (backtick-stripped, pre-sanitization) is the identifier the .swiftinterface
                // spells, so it is what the async-accessor fact is keyed by.
                Accessors = HandleAccessors(node.Accessors, sanitizedName, parentDecl, moduleDecl, rawName)
            };
            // Propagate extension flag to accessor MethodDecls. Extension methods use static
            // dispatch — accessor P/Invokes must not get Tj dispatch thunk suffix.
            if (node.isFromExtension == true)
            {
                foreach (var accessor in decl.Accessors)
                    accessor.Method.IsExtensionMethod = true;
            }

            // Propagate the storage's reference ownership to the accessor MethodDecls. Marshalling
            // arms are handed the accessor, not the property, so a setter that writes weak/unowned
            // storage can only learn that its value is handed to a non-retaining sink from here.
            if (decl.ReferenceOwnership != SwiftReferenceOwnership.Strong)
            {
                foreach (var accessor in decl.Accessors)
                    accessor.Method.SinkReferenceOwnership = decl.ReferenceOwnership;
            }

            // Suppress underscore-prefixed properties without explicit AccessControl.
            if (!decl.IsModuleInternal && rawName.StartsWith("_") &&
                (node.DeclAttributes is null || Array.IndexOf(node.DeclAttributes, "AccessControl") == -1))
            {
                decl.IsModuleInternal = true;
            }

            // Cross-reference with swiftinterface: if property doesn't appear in the public
            // interface, it's internal even if not explicitly flagged in the ABI JSON.
            // Uses unqualified Name (consistent with method path at CreateMethodDecl and with
            // swiftinterface key format — see IsInternalFromSwiftInterface doc comment).
            if (!decl.IsModuleInternal && parentDecl is TypeDecl propParentForInternal)
            {
                decl.IsModuleInternal = IsInternalFromSwiftInterface(propParentForInternal.Name, sanitizedName, node);
            }
            // Negative-space detection: property not in public swiftinterface is internal.
            if (!decl.IsModuleInternal)
            {
                bool isCurrentModuleMember = !string.IsNullOrEmpty(node.ModuleName) &&
                    node.ModuleName == CurrentModuleName;
                decl.IsModuleInternal = IsInternalFromPublicMemberNames(parentDecl, sanitizedName, isCurrentModuleMember);
            }
            if (parentDecl is TypeDecl propParentType)
            {
                ApplyPropertyActorIsolation(decl, propParentType);
                ApplyMemberAvailability(decl, propParentType, sanitizedName);
                ApplyMemberPosition(decl, propParentType, sanitizedName);
            }
            // Propagate the property's availability to its accessor MethodDecls so the
            // private *_Get/*_Set backing methods emit [SupportedOSPlatform] attributes
            // matching the public wrapper. Without this, backing methods that reference
            // newer-SDK return/value types trigger CA1416 inside wider class-level surfaces.
            // Defensive copy per accessor: downstream emitters (PropertyHandler async-property
            // path) mutate accessor.Method.AvailabilityAnnotations via AddRange, which would
            // otherwise duplicate entries back into the shared parent list.
            if (decl.AvailabilityAnnotations is { Count: > 0 } propertyAvailability)
            {
                foreach (var accessor in decl.Accessors)
                    accessor.Method.AvailabilityAnnotations = new List<AvailabilityAnnotation>(propertyAvailability);
            }

            // If the ABI JSON marks the setter with tighter introduced versions
            // (e.g. WorkoutKit.PowerThresholdAlert.metric getter is iOS 17.0 but setter is
            // iOS 17.4), attach a setter-specific availability list so the Swift @_cdecl
            // setter wrapper emits the stricter @available. The list starts from the
            // property-level availability and overrides per-platform where the setter
            // accessor declares a newer intro.
            var setterAccessorNode = node.Accessors.FirstOrDefault(a => a.AccessorKind == "set");
            if (setterAccessorNode != null)
            {
                var setterSpecific = ExtractAccessorAvailability(setterAccessorNode);
                if (setterSpecific is { Count: > 0 })
                {
                    var mergedSetter = MergeAccessorAvailability(
                        decl.AvailabilityAnnotations, setterSpecific);
                    decl.SetterAvailabilityAnnotations = mergedSetter;
                    // Overwrite the set accessor method's availability with the merged
                    // (tighter) list so downstream emitters that read accessor.Method
                    // .AvailabilityAnnotations directly (subscript/async setter paths,
                    // method wrapper emission) see the setter-specific restrictions
                    // instead of the looser property-level copy.
                    if (mergedSetter != null)
                    {
                        foreach (var accessor in decl.Accessors.OfType<SetAccessorDecl>())
                        {
                            accessor.Method.AvailabilityAnnotations =
                                new List<AvailabilityAnnotation>(mergedSetter);
                        }
                    }
                }
            }
            PopulateDocumentation(decl, node);
            return decl;
        }

        /// <summary>
        /// Reads a Var node's reference-ownership qualifier (strong / weak / unowned /
        /// unowned(unsafe)) from the ABI JSON.
        /// </summary>
        /// <remarks>
        /// Both producers agree on the encoding: a non-strong property carries the raw integer in
        /// <c>ownership</c> and lists <c>ReferenceOwnership</c> in <c>declAttributes</c>; a strong
        /// property emits neither key. Verified against swift-frontend
        /// <c>-emit-abi-descriptor-path</c> (the BindingTests fixture producer) and
        /// <c>swift-api-digester -dump-sdk</c> (the Apple-framework producer) on the same module,
        /// and against a real framework dump where <c>DataScannerViewController.delegate</c>
        /// (declared <c>weak</c>) reads 1.
        ///
        /// An unrecognized non-zero value is treated as <see cref="SwiftReferenceOwnership.Unowned"/>
        /// rather than <see cref="SwiftReferenceOwnership.Strong"/>: zero is the only strong
        /// encoding, so every value the enum could grow is some non-retaining flavor, and
        /// mis-reading one as strong would silently drop the rooting a non-retaining sink needs.
        /// </remarks>
        private static SwiftReferenceOwnership ParseReferenceOwnership(Node node)
        {
            // The integer is authoritative when present. The attribute alone (no integer) still
            // says the storage does not retain, so fall back to the checked-unowned reading
            // rather than to strong.
            if (node.ownership is null)
            {
                var hasOwnershipAttribute = node.DeclAttributes is not null &&
                    Array.IndexOf(node.DeclAttributes, "ReferenceOwnership") != -1;
                return hasOwnershipAttribute ? SwiftReferenceOwnership.Unowned : SwiftReferenceOwnership.Strong;
            }

            return node.ownership switch
            {
                0 => SwiftReferenceOwnership.Strong,
                1 => SwiftReferenceOwnership.Weak,
                2 => SwiftReferenceOwnership.Unowned,
                3 => SwiftReferenceOwnership.Unmanaged,
                _ => SwiftReferenceOwnership.Unowned,
            };
        }

        /// <summary>
        /// Creates a subscript declaration from a node.
        /// Subscripts have children where:
        /// - Child[0] is the return type
        /// - Child[1..n] are the index parameters
        /// </summary>
        /// <param name="node">The node representing the subscript declaration.</param>
        /// <param name="parentDecl">The parent declaration.</param>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <returns>The subscript declaration.</returns>
        private SubscriptDecl CreateSubscriptDecl(Node node, BaseDecl parentDecl, ModuleDecl moduleDecl)
        {
            var children = node.Children.ToList();
            if (children.Count < 2)
            {
                throw new InvalidOperationException($"Subscript '{node.Name}' has insufficient children (expected at least 2).");
            }

            // First child is the return type
            var returnTypeSpec = CreateTypeSpec(children[0]);

            // Remaining children are index parameters
            var indexParameters = new List<ArgumentDecl>();
            var paramInfo = ExtractSubscriptParameterNamesWithUnlabeled(node.PrintedName);

            for (int i = 1; i < children.Count; i++)
            {
                var idx = i - 1;
                var paramName = idx < paramInfo.Count ? paramInfo[idx].Name : $"index{idx}";
                var originalSwiftName = idx < paramInfo.Count ? paramInfo[idx].OriginalSwiftName : null;
                var isUnlabeled = idx < paramInfo.Count && paramInfo[idx].IsUnlabeled;
                indexParameters.Add(new ArgumentDecl
                {
                    SwiftTypeSpec = CreateTypeSpec(children[i]),
                    Name = paramName,
                    OriginalSwiftName = originalSwiftName,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    IsUnlabeledSubscriptIndex = isUnlabeled,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                });
            }

            // The .swiftinterface half of the accessor async oracle (see CreateGetAccessor for
            // the two-oracle rationale). The walker keys a subscript by the same
            // `subscript(label:…)` spelling the ABI printedName uses — a parameter contributes its
            // external label only when it has one, `_` otherwise — so the ABI spelling is the
            // lookup key as-is; the type path is the Swift-spelled one, and a type-level subscript
            // carries the `static ` prefix exactly like a type-level property. Two subscripts on
            // one type that erase to the same spelling share a key, so an async getter on either
            // marks both: that over-marking only refuses the sibling indexer, whereas under-marking
            // emits a synchronous indexer over an async entry point.
            var subscriptAsyncFactKey = parentDecl is TypeDecl asyncSubscriptParentType
                ? $"{BuildSwiftTypeQualifiedPath(asyncSubscriptParentType)}.{node.PrintedName}"
                : node.PrintedName;
            if (node.@static ?? false)
            {
                subscriptAsyncFactKey = $"static {subscriptAsyncFactKey}";
            }
            var interfaceSaysSubscriptAsync = _facts.AsyncAccessorMembers.Contains(subscriptAsyncFactKey);

            var decl = new SubscriptDecl
            {
                Name = "subscript",
                MangledName = node.MangledName,
                ReturnTypeSpec = returnTypeSpec,
                IndexParameters = indexParameters,
                IsStatic = node.@static ?? false,
                Accessors = HandleSubscriptAccessors(node.Accessors, indexParameters, returnTypeSpec, parentDecl, moduleDecl, subscriptAsyncFactKey, interfaceSaysSubscriptAsync),
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                IsModuleInternal = IsNodeModuleInternal(node),
                IsSpiProtected = IsNodeSpiProtected(node)
            };
            // Classify visibility from the ABI JSON attributes, exactly as methods and
            // properties do. An ABI-visible internal subscript is always @usableFromInline
            // or @inlinable (a plain `internal subscript` never reaches the ABI), so
            // IsNodeModuleInternal captures the full suppressible surface here. The
            // swiftinterface negative-space gate that CreatePropertyDecl also runs is
            // deliberately NOT mirrored: the public swiftinterface keys subscripts by
            // argument label (`subscript(i:)`) while the ABI printedName is frequently
            // label-erased (`subscript(_:)`), so a name-mismatch would over-suppress a
            // genuinely public subscript.
            if (parentDecl is TypeDecl subscriptParentType)
            {
                ApplyMemberAvailability(decl, subscriptParentType, node.PrintedName, node);
                ApplyMemberPosition(decl, subscriptParentType, node.PrintedName);
            }
            // Propagate subscript availability to accessor MethodDecls (same rationale as
            // CreatePropertyDecl — backing accessors referencing newer-SDK types need matching
            // attributes to satisfy CA1416 inside wider class-level surfaces). Defensive copy
            // per accessor so downstream mutation cannot feed back into the parent decl.
            if (decl.AvailabilityAnnotations is { Count: > 0 } subscriptAvailability)
            {
                foreach (var accessor in decl.Accessors)
                    accessor.Method.AvailabilityAnnotations = new List<AvailabilityAnnotation>(subscriptAvailability);
            }

            // Apply parameter labels from swiftinterface if available.
            // ABI JSON may not encode all label variations for subscripts (e.g., "subscript(_:)"
            // when the actual declaration is "subscript(bitAt:)"). Cross-reference labels from
            // the swiftinterface to fix the parameter names.
            if (indexParameters.Count > 0 && parentDecl is TypeDecl subscriptLabelParentType)
            {
                var typePath = BuildTypeQualifiedPath(subscriptLabelParentType);

                // Try matching by ABI printed name first (may be correct for some subscripts)
                var abiKey = $"{typePath}.{node.PrintedName}";
                if (!_facts.SubscriptLabels.TryGetValue(abiKey, out var labels))
                {
                    // ABI key didn't match — search for a subscript with matching parameter count.
                    // For ambiguous cases (multiple subscripts with the same param count),
                    // we can't definitively match, so we only apply when there's exactly one match.
                    var prefix = $"{typePath}.subscript(";
                    var candidates = _facts.SubscriptLabels
                        .Where(kv => kv.Key.StartsWith(prefix) && kv.Value.Count == indexParameters.Count)
                        .ToList();

                    if (candidates.Count == 1)
                    {
                        labels = candidates[0].Value;
                    }
                }

                if (labels != null)
                {
                    for (int i = 0; i < Math.Min(labels.Count, indexParameters.Count); i++)
                    {
                        var label = labels[i];
                        if (label == "_")
                        {
                            // No argument label — force the "indexN" name pattern so
                            // FixSubscriptCallArg strips the label from bracket syntax.
                            // The ABI JSON may have a param name (e.g., "key" from subscript(key:))
                            // that looks like a label but isn't — subscripts with single-name params
                            // have no argument label in Swift.
                            if (!indexParameters[i].Name.StartsWith("index"))
                                indexParameters[i].Name = $"index{i}";
                            indexParameters[i].IsUnlabeledSubscriptIndex = true;
                        }
                        else
                        {
                            // Apply C#-keyword safety so the Name field stays a valid C# identifier,
                            // while OriginalSwiftName preserves the raw Swift label for emission paths
                            // that backtick-escape via NameProvider.ParserNameToSwift.
                            var (csLabel, swiftLabel) = ExtractUniqueNameWithOriginal(label);
                            indexParameters[i].Name = csLabel;
                            indexParameters[i].OriginalSwiftName = swiftLabel;
                            indexParameters[i].IsUnlabeledSubscriptIndex = false;
                        }
                    }
                }
            }

            // Propagate extension flag to subscript accessor MethodDecls.
            if (node.isFromExtension == true)
            {
                foreach (var accessor in decl.Accessors)
                    accessor.Method.IsExtensionMethod = true;
            }

            PopulateDocumentation(decl, node);
            return decl;
        }

        /// <summary>
        /// Extracts parameter names from a subscript's printed name, paired with a flag
        /// indicating whether each position was originally unlabeled (<c>_</c>) in Swift and
        /// the original Swift label (if the name was C#-keyword-safed).
        /// Examples: "subscript(_:)" -> [("index0", null, true)], "subscript(row:column:)" -> [("row", null, false), ("column", null, false)],
        /// "subscript(default:)" -> [("_default", "default", false)].
        /// The IsUnlabeled flag is the ground truth for label suppression — pattern-matching
        /// the synthetic <c>index{i}</c> name would mis-classify real user labels that happen
        /// to spell <c>index0</c>, <c>index1</c>, etc. The OriginalSwiftName is required so
        /// Swift emission can recover the real label (e.g. <c>default</c>) and backtick-escape
        /// it, instead of leaking the C#-safe form (<c>_default</c>) into Swift signatures.
        /// </summary>
        private List<(string Name, string? OriginalSwiftName, bool IsUnlabeled)> ExtractSubscriptParameterNamesWithUnlabeled(string printedName)
        {
            var result = new List<(string Name, string? OriginalSwiftName, bool IsUnlabeled)>();
            var start = printedName.IndexOf('(');
            var end = printedName.LastIndexOf(')');

            if (start < 0 || end < 0 || start >= end)
                return result;

            var paramPart = printedName.Substring(start + 1, end - start - 1);
            var paramNames = paramPart.Split(':').Where(s => !string.IsNullOrEmpty(s)).ToList();

            for (int i = 0; i < paramNames.Count; i++)
            {
                var name = paramNames[i].Trim();
                // If the parameter name is just "_", generate a unique name
                if (name == "_")
                {
                    result.Add(($"index{i}", null, true));
                }
                else
                {
                    var (csName, swiftName) = ExtractUniqueNameWithOriginal(name);
                    result.Add((csName, swiftName, false));
                }
            }

            return result;
        }

        /// <summary>
        /// Handles accessors for a subscript declaration.
        /// Similar to HandleAccessors but subscript accessors have index parameters.
        /// </summary>
        private List<AccessorDecl> HandleSubscriptAccessors(
            IEnumerable<Node> accessors,
            IReadOnlyList<ArgumentDecl> indexParameters,
            TypeSpec returnTypeSpec,
            BaseDecl parentDecl,
            ModuleDecl moduleDecl,
            string asyncFactKey,
            bool interfaceSaysAsync)
        {
            var result = new List<AccessorDecl>();

            foreach (var accessor in accessors)
            {
                switch (accessor.AccessorKind)
                {
                    case "get":
                        result.Add(CreateSubscriptGetAccessor(accessor, indexParameters, returnTypeSpec, parentDecl, moduleDecl,
                            IsSubscriptAccessorAsync(accessor, asyncFactKey, interfaceSaysAsync)));
                        break;
                    case "set":
                        result.Add(CreateSubscriptSetAccessor(accessor, indexParameters, returnTypeSpec, parentDecl, moduleDecl,
                            IsSubscriptAccessorAsync(accessor, asyncFactKey, interfaceSaysAsync)));
                        break;
                    case "_modify":
                    case "_read":
                        // Coroutine accessors - skip these for now
                        break;
                    default:
                        _logger.LogWarning($"Unsupported subscript accessor kind '{accessor.AccessorKind}' encountered.");
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// The two accessor async oracles, ORed, for a subscript accessor: the TBD's sibling
        /// <c>{accessor}Tu</c> / <c>{accessor}TjTu</c> symbol and the .swiftinterface fact keyed by
        /// the subscript's spelling. Either alone suffices, because each goes silent on its own —
        /// the TBD when the symbol set is incomplete, the interface when none was supplied — and
        /// silence is indistinguishable from "synchronous". The effect specifier is get-only in
        /// Swift, and an effectful getter excludes a setter, so on a setter the interface half is
        /// always false and the TBD probe answers alone; it is still routed through here so both
        /// accessors read one decision. A disagreement is logged: it names a broken TBD or a stale
        /// walker key.
        /// </summary>
        private bool IsSubscriptAccessorAsync(Node accessor, string asyncFactKey, bool interfaceSaysAsync)
        {
            var tbdSaysAsync = ManglingProbes.IsAsyncAccessor(_demangledTbd.AllSymbols, accessor.MangledName);
            if (tbdSaysAsync != interfaceSaysAsync)
            {
                _logger.LogDebug(
                    "Async-accessor oracles disagree for '{Key}' ({Mangled}): TBD says {Tbd}, .swiftinterface says {Interface}. Treating the accessor as async.",
                    asyncFactKey, accessor.MangledName, tbdSaysAsync, interfaceSaysAsync);
            }
            return tbdSaysAsync || interfaceSaysAsync;
        }

        /// <summary>
        /// Creates a getter accessor for a subscript.
        /// </summary>
        private GetAccessorDecl CreateSubscriptGetAccessor(
            Node accessor,
            IReadOnlyList<ArgumentDecl> indexParameters,
            TypeSpec returnTypeSpec,
            BaseDecl parentDecl,
            ModuleDecl moduleDecl,
            bool isAsync)
        {
            // Build signature: [0] = return type, [1..n] = index parameters
            var signature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = returnTypeSpec,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            };
            signature.AddRange(indexParameters);

            // Mirror CreateGetAccessor: subscript accessors on a generic parent type
            // must inherit the parent's generic parameters so HandleGenericMetadata /
            // HandleProtocolConformance emit the metadata + PWT params in the Metadata
            // phase. Without this the C# P/Invoke decl drops them, and the call site
            // appends metadata in the wrong phase (after Self), producing both a
            // decl/call arity mismatch and an ABI-mismatched call when it does line up.
            var genericParameters = new List<GenericArgumentDecl>();
            if (!string.IsNullOrEmpty(accessor.GenericSig))
            {
                genericParameters = GenericSignatureParser.ParseGenericSignature(accessor.GenericSig, accessor.sugared_genericSig);
            }
            else if (parentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
            {
                genericParameters = new List<GenericArgumentDecl>(typeDecl.GenericParameters);
            }

            var methodDecl = new MethodDecl
            {
                Name = "subscript_Get",
                MangledName = accessor.MangledName,
                MethodType = accessor.@static ?? false ? MethodType.Static : MethodType.Instance,
                IsConstructor = false,
                CSSignature = signature,
                GenericParameters = genericParameters,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                // A subscript accessor can be effectful (`get async`, `get throws`) exactly like a
                // property accessor. `throwing` is in the ABI JSON; async-ness is not, and the
                // accessor's mangled name carries no marker for it either, so it comes from the same
                // two oracles a property getter reads (IsSubscriptAccessorAsync). Leaving these false
                // emits an ordinary indexer whose thunk calls the async entry point synchronously —
                // an ABI mismatch that compiles.
                Throws = accessor.throwing ?? false,
                IsAsync = isAsync,
                IsSynthesizedAccessor = true,
                IsAccessor = true,
                IsFinal = accessor.DeclAttributes?.Contains("Final") == true,
            };

            return new GetAccessorDecl { Method = methodDecl };
        }

        /// <summary>
        /// Creates a setter accessor for a subscript.
        /// </summary>
        private SetAccessorDecl CreateSubscriptSetAccessor(
            Node accessor,
            IReadOnlyList<ArgumentDecl> indexParameters,
            TypeSpec returnTypeSpec,
            BaseDecl parentDecl,
            ModuleDecl moduleDecl,
            bool isAsync)
        {
            // Build signature: [0] = void (return), [1] = newValue, [2..n] = index parameters
            var signature = new List<ArgumentDecl>
            {
                // Return type (void for setters)
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                },
                // The new value parameter
                new ArgumentDecl
                {
                    SwiftTypeSpec = returnTypeSpec,
                    Name = "newValue",
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            };
            signature.AddRange(indexParameters);

            // Mirror CreateSetAccessor: see CreateSubscriptGetAccessor for rationale.
            var genericParameters = new List<GenericArgumentDecl>();
            if (!string.IsNullOrEmpty(accessor.GenericSig))
            {
                genericParameters = GenericSignatureParser.ParseGenericSignature(accessor.GenericSig, accessor.sugared_genericSig);
            }
            else if (parentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
            {
                genericParameters = new List<GenericArgumentDecl>(typeDecl.GenericParameters);
            }

            var methodDecl = new MethodDecl
            {
                Name = "subscript_Set",
                MangledName = accessor.MangledName,
                MethodType = accessor.@static ?? false ? MethodType.Static : MethodType.Instance,
                IsConstructor = false,
                CSSignature = signature,
                GenericParameters = genericParameters,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl,
                // Same sources as the getter: `throwing` from the ABI JSON, async-ness from the two
                // accessor oracles (IsSubscriptAccessorAsync).
                Throws = accessor.throwing ?? false,
                IsAsync = isAsync,
                IsSynthesizedAccessor = true,
                IsAccessor = true,
                IsFinal = accessor.DeclAttributes?.Contains("Final") == true,
            };

            return new SetAccessorDecl { Method = methodDecl };
        }

        /// <summary>
        /// Creates a type spec from a given node parsing the printed name
        /// </summary>
        internal TypeSpec CreateTypeSpec(Node node)
        {
            switch (node.Kind)
            {
                case kNominal:
                case kFunc:
                    // Handle ProtocolComposition (existential types like 'Any', 'any P1 & P2')
                    // In ABI JSON, 'Any' appears as TypeNominal with name="ProtocolComposition", printedName="Any"
                    if (node.Name == "ProtocolComposition")
                    {
                        return CreateProtocolCompositionTypeSpec(node);
                    }
                    // Handle OpaqueTypeArchetype (opaque return types like 'some Protocol')
                    // In ABI JSON, these appear as TypeNominal with name="OpaqueTypeArchetype",
                    // printedName="some ModuleName.ProtocolName", with children listing the protocol constraints.
                    // We represent them as a ProtocolListTypeSpec with IsOpaque=true, and generate a Swift
                    // wrapper that boxes the concrete return value into an existential container (any Protocol).
                    if (node.Name == "OpaqueTypeArchetype")
                    {
                        return CreateOpaqueReturnTypeSpec(node);
                    }
                    // Handle DependentMember (associated type references like "τ_0_0.Element").
                    // In ABI JSON, these appear as TypeNominal with name="DependentMember",
                    // so they match the kNominal case — the separate case "DependentMember"
                    // branch below was dead code.
                    if (node.Name == "DependentMember")
                    {
                        return new AssociatedTypeReferenceSpec(node.PrintedName);
                    }
                    // Handle parameter-position opaque types (`some P`, `some P<T>`).
                    // swift-api-digester emits these as TypeNominal with name="GenericTypeParam"
                    // and printedName starting with "some " — with NO children, so the
                    // OpaqueTypeArchetype branch (which is used for return-position opaque
                    // types) does not catch them. We lower them to a synthetic per-method
                    // generic parameter, mirroring the Swift compiler's own desugaring of
                    // `some P` in parameter position to an unnamed generic `<T: P>`.
                    // Only applies when we're inside CreateMethodDecl's param loop
                    // (_opaqueParamCapture != null). Outside it — notably a subscript index
                    // param `some P`, which does not install the capture — fall through to
                    // ParseTypeSpecOrDegrade below: the EOF-strict Parse throws on the bare
                    // "some P" string (not a single nominal), so the degrade path yields a
                    // leading-prefix NamedTypeSpec("some") — broken but present, matching
                    // pre-cb1ff96d behavior — rather than letting the throw drop the whole
                    // declaration via HandleNode's catch.
                    if (node.Name == kGenericTypeParam &&
                        node.PrintedName.StartsWith("some ", StringComparison.Ordinal) &&
                        _opaqueParamCapture != null)
                    {
                        return SynthesizeOpaqueParameter(node);
                    }
                    // Handle variadic parameters (T...). swift-api-digester emits these as
                    // TypeNominal Array nodes with printedName "T..." when T is a generic
                    // type parameter (for concrete element types it uses "[T]" instead).
                    // TypeSpecParser treats '.' as a valid in-name character, so Parse("T...")
                    // silently produces NamedTypeSpec("T...") instead of failing — the
                    // malformed name then crashes downstream validators that rely on
                    // HasModule()/FromModuleQualifiedName. Build the canonical demangler
                    // shape (Swift.Array<T> with IsVariadic on the element) directly from
                    // the child node so variadic detection in HasVariadicElement fires.
                    if (node.Name == "Array" &&
                        node.PrintedName.EndsWith("...", StringComparison.Ordinal) &&
                        node.Children.Any())
                    {
                        var elementSpec = CreateTypeSpec(node.Children.First());
                        elementSpec.IsVariadic = true;
                        var arraySpec = new NamedTypeSpec("Swift.Array");
                        arraySpec.GenericParameters.Add(elementSpec);
                        return arraySpec;
                    }
                    var spec = ParseTypeSpecOrDegrade(node.PrintedName);
                    // swift-api-digester prints an implicitly unwrapped optional with `?` and gives
                    // it Optional's USR, so printedName alone can't tell `T!` from `T?`. The node's
                    // structural name is the only surviving signal. Mark the spec rather than
                    // changing its type: IUO IS Optional (same layout, same C# projection) — the
                    // spelling only has to survive as far as a Swift witness signature, where the
                    // conformance checker rejects a `T?` witness for a `T!` requirement.
                    if (node.Name == "ImplicitlyUnwrappedOptional")
                        spec.IsImplicitlyUnwrappedOptional = true;
                    // Carry each reference node's Swift USR onto the matching spec — the outer type
                    // and, positionally, the generic-argument types from the node's child type nodes.
                    // The USR's mangling suffix letter is the only signal that distinguishes a value
                    // type (V struct / O enum) from a class (C) for a type the type database never
                    // registered; downstream emission-time validation needs it to avoid synthesizing
                    // a bogus bridged-class reference to an absent framework value type, and an absent
                    // type leaks in generic-argument position (e.g. the Transaction in
                    // Optional<Transaction>) as well as bare position, so it must reach the inner specs.
                    ThreadNominalUsrs(spec, node);
                    // Record the raw-ObjC-name → Swift-import-name mapping for every ObjC-imported
                    // nominal in this reference (outer type + nested generic args), for the mixed
                    // ObjC+Swift bridge. See _objcImportedTypeNames.
                    CaptureObjCImportedTypeNames(node);
                    // When a typealias appears inside Optional<T>, swift-api-digester encodes the
                    // underlying nominal in the TypeNameAlias child node — but TypeSpecParser
                    // only sees PrintedName ("simd.float4x4?"), so it keeps the alias name as the
                    // Optional's inner spec. The alias itself isn't registered in the type
                    // database, so the lookup falls back to SwiftOptional<IntPtr>. Substitute
                    // the resolved child via the existing TypeNameAlias unwrap path so the inner
                    // generic param refers to the real nominal (e.g. simd.simd_float4x4 →
                    // System.Numerics.Matrix4x4, RealityKit.…ShadowMapCullMode →
                    // RealityFoundation.MaterialParameterTypes.FaceCulling).
                    if (node.Name == "Optional" &&
                        spec is NamedTypeSpec optionalSpec &&
                        optionalSpec.Name == "Swift.Optional" &&
                        optionalSpec.GenericParameters.Count == 1 &&
                        node.Children.Count() == 1 &&
                        node.Children.First().Kind == "TypeNameAlias")
                    {
                        var unwrappedInner = CreateTypeSpec(node.Children.First());
                        optionalSpec.GenericParameters[0] = unwrappedInner;
                    }
                    // Propagate escaping attribute from ABI JSON typeAttributes.
                    // Swift public API convention: closures are @escaping unless
                    // explicitly marked noescape. TypeSpecParser doesn't parse
                    // @escaping from PrintedName, so we set it from ABI data.
                    if (spec is ClosureTypeSpec closureSpec)
                    {
                        bool isNoescape = node.typeAttributes?.Contains("noescape") == true;
                        if (!isNoescape && !closureSpec.IsEscaping)
                        {
                            closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
                        }
                    }
                    return spec;
                case kGenericTypeParam:
                    // Generic type parameter - parse the PrintedName (e.g., "T", "τ_0_0")
                    // which will create a NamedTypeSpec that can be matched in GenericTypeMapping
                    var genericSpec = TypeSpecParser.Parse(node.PrintedName);
                    if (genericSpec is null)
                    {
                        throw new Exception($"Error parsing generic type param from \"{node.PrintedName}\"");
                    }
                    return genericSpec;
                case "TypeNameAlias":
                    // swift-api-digester wraps typealias uses in a TypeNameAlias node whose
                    // single child is the underlying TypeNominal (e.g. SHA256.Digest → SHA256Digest).
                    // Unwrap to the underlying type — the alias itself is purely a naming shim
                    // and the downstream pipeline wants the real nominal type.
                    if (!node.Children.Any())
                    {
                        throw new Exception($"TypeNameAlias node \"{node.PrintedName}\" has no children to unwrap.");
                    }
                    return CreateTypeSpec(node.Children.First());
                default:
                    throw new NotImplementedException($"Can't handle node type {node.Kind} yet.");
            }
        }

        /// <summary>
        /// Threads each reference node's Swift USR onto the matching <see cref="NamedTypeSpec"/> —
        /// the outer spec from <paramref name="node"/> and, positionally, the generic-argument specs
        /// from the node's child type nodes. <see cref="NamedTypeSpec.Usr"/>'s mangling suffix records
        /// the nominal kind (V struct / O enum / C class), the only signal that distinguishes a value
        /// type from a class for a type the type database never registered. It must reach the inner
        /// specs (e.g. the Transaction in Optional&lt;Transaction&gt;) because an absent type leaks in
        /// generic-argument position as well as bare position. Child-to-generic-parameter matching is
        /// positional and applied only when the counts agree, so a structural mismatch simply leaves
        /// the inner USRs unset rather than misattributing them.
        /// </summary>
        private static void ThreadNominalUsrs(TypeSpec spec, Node node)
        {
            // A tuple element leaks an absent type in the same way a generic argument does — an enum
            // case's associated values are encoded as a tuple `(label: T, …)`, so the USR that marks
            // T's nominal kind (or, for a bridged NSError, the clang `…Code` enum) reaches T only if
            // threaded through the tuple. Match tuple elements positionally to the tuple node's
            // children, applied only when the counts agree so a shape mismatch leaves USRs unset.
            if (spec is TupleTypeSpec tuple)
            {
                var tupleChildren = node.Children?.ToList();
                if (tupleChildren != null && tupleChildren.Count == tuple.Elements.Count)
                {
                    for (int i = 0; i < tupleChildren.Count; i++)
                    {
                        ThreadNominalUsrs(tuple.Elements[i], tupleChildren[i]);
                    }
                }
                return;
            }
            if (spec is not NamedTypeSpec named)
                return;
            if (!string.IsNullOrEmpty(node.usr))
                named.Usr = node.usr;
            var childNodes = node.Children?.ToList();
            if (childNodes != null && childNodes.Count == named.GenericParameters.Count)
            {
                for (int i = 0; i < childNodes.Count; i++)
                {
                    ThreadNominalUsrs(named.GenericParameters[i], childNodes[i]);
                }
            }
        }

        /// <summary>
        /// Records the raw-ObjC-name → Swift-import-name mapping for every ObjC-imported nominal in
        /// <paramref name="node"/> and its children, into <see cref="_objcImportedTypeNames"/>. The
        /// raw ObjC name is decoded from the Clang <c>usr</c> (<see cref="ObjCImportedRawName"/>); the
        /// Swift-import name is the reference's <c>printedName</c> with its leading module component
        /// stripped (already module-qualified, e.g. <c>M.Greeter</c> → <c>Greeter</c>). Only names
        /// belonging to THIS module are harvested — an ObjC type imported by a dependency
        /// (<c>Dep.OtherThing</c>) would otherwise seed a rename under a raw name that can collide
        /// with this module's own and mis-key the record; cross-module bridging is out of scope.
        /// Recurses so an ObjC type in generic-argument position (e.g. inside
        /// <c>Optional&lt;M.Greeter&gt;</c>) is captured too.
        /// </summary>
        private void CaptureObjCImportedTypeNames(Node node)
        {
            var rawName = ObjCImportedRawName(node.usr);
            if (rawName != null && !string.IsNullOrEmpty(node.PrintedName))
            {
                // printedName is module-qualified. Split off the leading module component and harvest
                // the rename ONLY when it names THIS module: the bridge registers records into this
                // module's own database, and a dependency's import carries a different module prefix.
                // A bare (unqualified) name has no module component and is skipped.
                var printed = node.PrintedName;
                var firstDot = printed.IndexOf('.');
                if (firstDot == CurrentModuleName.Length
                    && string.CompareOrdinal(printed, 0, CurrentModuleName, 0, firstDot) == 0)
                {
                    var swiftName = printed.Substring(firstDot + 1);
                    // Only a pure identifier is handled: a remaining dot means a nested import
                    // (NS_SWIFT_NAME(Parent.Child)) whose companion type isn't emitted nested, and any
                    // other punctuation means a decorated/malformed name — skip so a truncated or bogus
                    // key can't be seeded (it could collide with a real flat type of the same name).
                    if (swiftName.Length > 0 && swiftName.All(c => char.IsLetterOrDigit(c) || c == '_'))
                        _objcImportedTypeNames[rawName] = swiftName;
                }
            }
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                    CaptureObjCImportedTypeNames(child);
            }
        }

        /// <summary>
        /// Decodes the raw Objective-C declaration name from a Clang USR for the ObjC-imported kinds
        /// the mixed bridge handles: a pure ObjC class (<c>c:objc(cs)&lt;Name&gt;</c>), a C/ObjC enum
        /// including <c>NS_ENUM</c> (<c>c:@E@&lt;Name&gt;</c> / <c>c:@EA@&lt;Name&gt;</c>), and a
        /// file-scoped typedef including <c>NS_TYPED_ENUM</c> / <c>NS_TYPED_EXTENSIBLE_ENUM</c>
        /// (<c>c:&lt;file&gt;@T@&lt;Name&gt;</c>, e.g. <c>c:FBSDKLoginAuthType.h@T@FBSDKLoginAuthType</c>;
        /// the file component may be empty for a compiler builtin typedef, <c>c:@T@&lt;Name&gt;</c>).
        /// Returns <c>null</c> for any other USR — notably an <c>@objc</c>-exported <em>Swift</em>
        /// class, whose USR carries a Swift-module origin marker (<c>c:@M@&lt;module&gt;@objc(cs)…</c>)
        /// and which is bound by the Swift pipeline under its Swift name, so it must not be treated
        /// as a pure-ObjC import. A typedef belonging to a dependency module (e.g. UIKit's
        /// <c>UIApplicationLaunchOptionsKey</c>) is decoded here but discarded by
        /// <see cref="CaptureObjCImportedTypeNames"/>'s own-module <c>printedName</c> guard.
        /// </summary>
        private static string? ObjCImportedRawName(string? usr)
        {
            if (string.IsNullOrEmpty(usr))
                return null;
            const string classMarker = "c:objc(cs)";
            if (usr.StartsWith(classMarker, StringComparison.Ordinal))
                return usr.Substring(classMarker.Length);
            const string enumArrayMarker = "c:@EA@";
            if (usr.StartsWith(enumArrayMarker, StringComparison.Ordinal))
                return usr.Substring(enumArrayMarker.Length);
            const string enumMarker = "c:@E@";
            if (usr.StartsWith(enumMarker, StringComparison.Ordinal))
                return usr.Substring(enumMarker.Length);
            // File-scoped typedef: the file prefix varies (and is empty for builtins), so match the
            // "@T@" segment wherever it falls rather than by a fixed-length prefix. Guard on the
            // "c:" scheme so a Swift-symbol USR ("s:…") can never be mis-decoded as a typedef.
            const string typedefMarker = "@T@";
            if (usr.StartsWith("c:", StringComparison.Ordinal))
            {
                // The typedef name is the trailing C identifier, so anchor on the LAST "@T@": a file
                // component that itself contains the marker (or any compound USR) would otherwise make
                // IndexOf keep the earlier segment as part of the name. Then require the remainder to be
                // a pure identifier (no residual "@" segment marker or punctuation) so a malformed USR
                // can't seed a bogus rename key — mirrors CaptureObjCImportedTypeNames's own-name guard.
                var idx = usr.LastIndexOf(typedefMarker, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var name = usr.Substring(idx + typedefMarker.Length);
                    if (name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_'))
                        return name;
                }
            }
            return null;
        }

        /// <summary>
        /// Creates a ProtocolListTypeSpec from a ProtocolComposition node.
        /// The node's children represent the protocols in the composition.
        /// An empty composition (no children with printedName "Any") represents 'Any'.
        /// In practice, ABI JSON ProtocolComposition nodes have no children —
        /// the protocol list is encoded in the printedName (e.g., "any Module.ProtocolA &amp; Module.ProtocolB").
        /// </summary>
        private TypeSpec CreateProtocolCompositionTypeSpec(Node node)
        {
            var protocols = new List<NamedTypeSpec>();
            foreach (var child in node.Children)
            {
                if (child.Kind == kNominal)
                {
                    // Parse the protocol name
                    var childSpec = TypeSpecParser.Parse(child.PrintedName) as NamedTypeSpec;
                    if (childSpec != null)
                    {
                        protocols.Add(childSpec);
                    }
                }
            }

            // ABI JSON ProtocolComposition nodes typically have no children.
            // The protocol list is encoded in printedName: "any P1 & P2" or just "Any".
            if (protocols.Count == 0 && !string.IsNullOrEmpty(node.PrintedName))
            {
                var printedName = node.PrintedName;
                if (printedName.StartsWith("any "))
                    printedName = printedName.Substring(4);

                if (printedName != "Any")
                {
                    var parts = printedName.Split(new[] { " & " }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        var spec = TypeSpecParser.Parse(part.Trim()) as NamedTypeSpec;
                        if (spec != null)
                        {
                            protocols.Add(spec);
                        }
                    }
                }
            }

            return new ProtocolListTypeSpec(protocols);
        }

        /// <summary>
        /// Creates a ProtocolListTypeSpec from an OpaqueTypeArchetype node (some Protocol).
        /// The node's children represent the protocol constraints of the opaque return type.
        /// Marked as IsOpaque=true to indicate a Swift wrapper is needed.
        /// </summary>
        private TypeSpec CreateOpaqueReturnTypeSpec(Node node)
        {
            var protocols = new List<NamedTypeSpec>();
            foreach (var child in node.Children)
            {
                if (child.Kind == kNominal)
                {
                    var childSpec = TypeSpecParser.Parse(child.PrintedName) as NamedTypeSpec;
                    if (childSpec != null)
                    {
                        protocols.Add(childSpec);
                    }
                }
            }
            return new ProtocolListTypeSpec(protocols) { IsOpaque = true };
        }

        /// <summary>
        /// Lowers a parameter-position opaque type (<c>some P</c>) into a synthetic
        /// per-method generic parameter. Returns a <see cref="NamedTypeSpec"/> that
        /// references the synthetic parameter so the ArgumentDecl threads through the
        /// existing generic-method machinery (GenericTypeMapping lookup in
        /// MethodSignature, where-clause construction in WrapperEmitter.Signature).
        /// </summary>
        /// <remarks>
        /// Swift semantics: a parameter typed <c>some P</c> is sugar for an unnamed
        /// generic parameter with a <c>: P</c> conformance, chosen by the caller at
        /// the call site. Functionally this is indistinguishable from writing
        /// <c>&lt;T: P&gt;(arg: T)</c>, so the lowering is exact.
        ///
        /// The synthetic TypeName uses a <c>τ_</c> prefix so <see cref="NameProvider.GetCSharpGenericParameterName"/>
        /// falls into the positional <c>T{index}</c> naming branch. The index is the
        /// param's final position in <see cref="MethodDecl.GenericParameters"/>, so
        /// multiple opaque parameters and any pre-existing real generics all get
        /// distinct C# names.
        ///
        /// Conformance extraction: the constraint protocol is read by stripping the
        /// <c>some</c> keyword and parsing the remainder.
        /// <list type="bullet">
        ///   <item>A single <see cref="NamedTypeSpec"/> constraint is carried through as-is.</item>
        ///   <item>Protocol compositions (<c>some P1 &amp; P2</c>) parse to a
        ///   <see cref="ProtocolListTypeSpec"/>; each protocol in the list is added as a
        ///   separate conformance so the where-clause emitters produce
        ///   <c>where T : P1, P2</c> on both the C# and Swift sides.</item>
        ///   <item>If any extracted protocol has associated types (a PAT), the
        ///   MemberValidationPipeline's <c>HasUnsupportedProtocolConstraints</c> gate
        ///   suppresses the method cleanly — same behavior as any hand-written
        ///   <c>&lt;T: Collection&gt;</c> method today. This relies on the conformance
        ///   actually being added; an unrepresentable constraint (parse failure)
        ///   silently falls back to <c>ISwiftObject</c> only and the resulting wrapper
        ///   may not match the original API.</item>
        /// </list>
        /// </remarks>
        private NamedTypeSpec SynthesizeOpaqueParameter(Node node)
        {
            // Assign a unique TypeName based on the current capture length so repeated
            // opaque params in the same signature don't collide.
            int captureIndex = _opaqueParamCapture!.Count;
            string syntheticTypeName = $"τ_opaque_{captureIndex}";

            // Strip "some " prefix. Constraint parsing is best-effort — if anything
            // unexpected comes back we fall through to a bare synthetic param and
            // rely on the default ISwiftObject base constraint.
            string constraintText = node.PrintedName.Substring("some ".Length).Trim();
            var conformances = new List<GenericParameterConformance>();
            var assocConformances = new List<GenericParameterConformance>();
            if (!string.IsNullOrEmpty(constraintText))
            {
                TypeSpec? constraintSpec = null;
                try
                {
                    constraintSpec = TypeSpecParser.Parse(constraintText);
                }
                catch
                {
                    // Parser can throw on unfamiliar shapes — treat as unparsable.
                }

                // Try* rather than the throwing factory: this name comes straight off a generic-signature
                // requirement node, the one place unsubstituted placeholders (τ_0_0.Something) reach the
                // parser looking like ordinary dotted type names. A refused constraint degrades exactly
                // like the unparsable-shape branch below — the synthetic parameter keeps its default
                // ISwiftObject base constraint — whereas a fabricated τ-rooted module would survive into
                // lookups and emitted symbols.
                if (constraintSpec is NamedTypeSpec constraintNamed &&
                    SwiftTypeName.TryFromModuleQualifiedName(constraintNamed.Name, out var constraintTypeName))
                {
                    conformances.Add(new GenericParameterConformance(
                        new[] { syntheticTypeName },
                        constraintTypeName,
                        ConformanceKind.Protocol));

                    // Primary-associated-type sugar: `some Collection<X>` == `some P where P.Element == X`.
                    // Swift's stdlib sequence/collection family uses "Element" as the primary associated
                    // type, so a single generic argument is projected as a same-type constraint on Element.
                    // This preserves the coupling that the CSM engine needs to pick the right conformer.
                    if (constraintNamed.GenericParameters.Count == 1 &&
                        constraintNamed.GenericParameters[0] is NamedTypeSpec elementNamed &&
                        !string.IsNullOrEmpty(elementNamed.Name))
                    {
                        // Generic element (rare) or a placeholder-rooted name; either way the associated
                        // constraint is skipped rather than fabricated.
                        SwiftTypeName.TryFromModuleQualifiedName(elementNamed.Name, out var elementTypeName);

                        if (elementTypeName != null)
                        {
                            assocConformances.Add(new GenericParameterConformance(
                                new[] { syntheticTypeName, "Element" },
                                elementTypeName,
                                ConformanceKind.ConcreteType));
                        }
                    }
                }
                else if (constraintSpec is ProtocolListTypeSpec compositionSpec &&
                         compositionSpec.Protocols.Count > 0)
                {
                    // Protocol composition: `some P1 & P2`. Add one conformance per
                    // protocol so both the C# and Swift where-clause emitters produce
                    // `where T : P1, P2`. If any protocol is a PAT/Self-requirement,
                    // the validation pipeline will still suppress the method.
                    foreach (var protoSpec in compositionSpec.Protocols.Keys)
                    {
                        // Same untrusted source as the single-constraint arm above: refuse rather than
                        // fabricate, dropping just this member of the composition.
                        if (!SwiftTypeName.TryFromModuleQualifiedName(protoSpec.Name, out var protoTypeName))
                            continue;
                        conformances.Add(new GenericParameterConformance(
                            new[] { syntheticTypeName },
                            protoTypeName,
                            ConformanceKind.Protocol));
                    }
                }
                else
                {
                    // Unparsable or unrepresentable shape. The synthetic param falls back
                    // to the default ISwiftObject base constraint downstream. Log so the
                    // degradation is visible during generation rather than silent.
                    _logger.LogDebug(
                        "Opaque parameter constraint '{Constraint}' not representable as a NamedTypeSpec or ProtocolListTypeSpec; " +
                        "synthetic generic '{Synthetic}' will fall back to the default ISwiftObject constraint.",
                        constraintText, syntheticTypeName);
                }
            }

            _opaqueParamCapture.Add(new GenericArgumentDecl(
                TypeName: syntheticTypeName,
                SugaredTypeName: syntheticTypeName,
                GenericConformances: conformances,
                AssosiatedTypeConformances: assocConformances));

            return new NamedTypeSpec(syntheticTypeName);
        }

        /// <summary>
        /// Extracts and processes parameter names from a method signature.
        /// </summary>
        /// <param name="signature">The method signature string.</param>
        /// <returns>A list of processed parameter names.</returns>
        private List<(string Name, string? OriginalSwiftName)> ExtractParameterNames(string signature)
        {
            // Split the signature to get parameter names part and process it.
            var rawNames = signature.Split('(', ')')[1]
                                    .Split(new[] { ":" }, StringSplitOptions.RemoveEmptyEntries)
                                    .ToList();

            var paramNames = new List<(string Name, string? OriginalSwiftName)>();
            for (int i = 0; i < rawNames.Count; i++)
            {
                var (csharpName, keywordOriginal) = ExtractUniqueNameWithOriginal(rawNames[i]);

                // If the parameter label is just "_", generate a unique generic name.
                // SwiftBuilder.IsAutoGeneratedArgName recognizes the synthesized name so no
                // Swift call label is emitted for it — OriginalSwiftName stays null.
                if (csharpName == "_")
                {
                    paramNames.Add(($"arg{i}", null));
                    continue;
                }

                // Capture the true Swift external label so downstream call-label emission
                // (CdeclParamMapper.BuildSwiftCallArgLabel) never reverse-engineers it by
                // stripping a leading underscore — which corrupts labels that genuinely begin
                // with '_' (e.g. `_self`, `__tag`). For keyword-escaped labels the captured
                // original is the un-prefixed keyword (`default` for the C# name `_default`);
                // for every other label it is the label verbatim. Mirrors the subscript path
                // (CreateSubscriptDecl), which already populates OriginalSwiftName.
                paramNames.Add((csharpName, keywordOriginal ?? csharpName));
            }

            // Return type is the first element in the signature
            paramNames.Insert(0, (string.Empty, null));

            return paramNames;
        }

        /// <summary>
        /// Check if the name is a keyword and prefix it with "_".
        /// Returns a tuple: (csharpSafeName, originalSwiftName).
        /// originalSwiftName is non-null only when the name was modified (C# keyword prefix added).
        /// </summary>
        private static (string CSharpName, string? OriginalSwiftName) ExtractUniqueNameWithOriginal(string name)
        {
            // Strip Swift backtick escaping (e.g., `default` → default).
            // Backticks are used in Swift to escape keywords as identifiers;
            // they are not part of the identifier itself.
            if (name.Length >= 2 && name[0] == '`' && name[name.Length - 1] == '`')
                name = name.Substring(1, name.Length - 2);

            if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
            {
                return ($"_{name}", name);
            }

            return (name, null);
        }

        /// <summary>
        /// Check if the name is a keyword and prefix it with "_".
        /// </summary>
        /// <param name="name">The name to check.</param>
        /// <returns>The processed name.</returns>
        private static string ExtractUniqueName(string name)
        {
            return ExtractUniqueNameWithOriginal(name).CSharpName;
        }

        /// <summary>
        /// Populates the Documentation property on a declaration from the symbol graph, if available.
        /// Join key: node.usr matches symbol identifier.precise.
        /// </summary>
        private void PopulateDocumentation(BaseDecl decl, Node node)
        {
            if (_docComments != null && node.usr != null && _docComments.TryGetValue(node.usr, out var doc))
            {
                decl.Documentation = doc;
            }
        }

        private static SwiftTypeName GetSwiftTypeName(BaseDecl parentDecl, string name, string? moduleNameOverride = null)
            => parentDecl switch
            {
                ModuleDecl moduleDecl => SwiftTypeName.FromModuleQualifiedName($"{moduleNameOverride ?? moduleDecl.Name}.{name}"),
                TypeDecl typeDecl => SwiftTypeName.FromModuleQualifiedName($"{typeDecl.SwiftTypeName.ModuleQualifiedName}.{name}"),
                _ => throw new InvalidOperationException("Parent declaration is not a module or type.")
            };

        /// <summary>
        /// Builds a best-effort SwiftTypeName for a conformance whose mangled name the
        /// demangler could not handle. Prefers the dotted printedName when present, else
        /// synthesizes a Swift-module-qualified name from the unqualified protocol name.
        /// Returns a placeholder name when the node carries no usable identifier — the
        /// resulting conformance entry is harmless because downstream code keys
        /// conformance lookups on specific stdlib protocol names (Equatable, Copyable,
        /// Escapable, Hashable, CaseIterable, etc.) and ignores unknown ones.
        /// </summary>
        private static SwiftTypeName BuildFallbackProtocolName(Node node)
        {
            var raw = !string.IsNullOrEmpty(node.PrintedName) ? node.PrintedName
                    : !string.IsNullOrEmpty(node.Name) ? node.Name
                    : "UnknownProtocol";
            // The dotted form is taken verbatim from ABI JSON, so it can be rooted at an unsubstituted
            // generic placeholder rather than a module. Accepting one would register a conformance
            // under a module that does not exist; refusing it falls through to the documented
            // placeholder shape below, which is inert for the same reason the rest of this fallback is.
            if (raw.Contains('.') && SwiftTypeName.TryFromModuleQualifiedName(raw, out var printedName))
                return printedName;
            return SwiftTypeName.FromModuleQualifiedName($"Swift.{raw.Split('.')[^1]}");
        }

        /// <summary>
        /// Parses a swift-api-digester <c>PrintedName</c> into a <see cref="TypeSpec"/>, degrading
        /// gracefully instead of dropping the enclosing declaration. The canonical
        /// <see cref="TypeSpecParser.Parse(string)"/> is EOF-strict (cb1ff96d), so a type string
        /// that is not a single nominal — an un-stripped opaque modifier in a non-capture position
        /// (a subscript index param <c>some P</c>) or a <c>sending</c>-modified closure result
        /// (<c>() -> sending Box</c>) — throws <see cref="TypeSpecParseException"/>. Unguarded, that
        /// throw propagates to <c>HandleNode</c>'s catch-all and silently drops the WHOLE member
        /// (regression from cb1ff96d, which replaced the lenient prefix parse with the strict one).
        /// On a strict failure, fall back to the lenient <see cref="TypeSpecParser.ParsePrefix"/> —
        /// the pre-cb1ff96d behavior — which yields a degraded-but-present spec so the member still
        /// emits, and log it so the degradation is observable rather than silent. If even the prefix
        /// parse yields nothing, let the original strict failure surface rather than return null.
        /// </summary>
        private TypeSpec ParseTypeSpecOrDegrade(string printedName)
        {
            try
            {
                var spec = TypeSpecParser.Parse(printedName);
                if (spec is null)
                {
                    throw new Exception($"Error parsing type from \"{printedName}\"");
                }
                return spec;
            }
            catch (TypeSpecParseException ex)
            {
                var degraded = TypeSpecParser.ParsePrefix(printedName);
                if (degraded is null)
                {
                    throw;
                }
                _logger.LogDebug(
                    $"Type string \"{printedName}\" is not a single strict type ({ex.Message}); " +
                    "degraded to a leading-prefix parse so the declaration is not dropped via HandleNode's catch.");
                return degraded;
            }
        }

        /// <summary>
        /// True when an ABI node's identity is rooted in ObjC/C interop rather than Swift — an
        /// imported or <c>@objc</c> ObjC class, or a C-typedef struct re-exported through a Swift
        /// module. Such nodes carry a Clang USR (<c>c:objc(...)</c> / <c>c:@T@...</c>) and/or an
        /// <c>ObjC</c> decl attribute and legitimately have no Swift mangled name. A Swift-defined
        /// <c>@objc</c> class is NOT ObjC-rooted by this test in the way that matters: it still
        /// carries a <c>$s...</c> mangled name, so it never reaches the missing-mangled-name gate
        /// this helper guards.
        /// </summary>
        private static bool IsObjCRootedIdentity(Node node)
        {
            if (node.DeclAttributes is not null && Array.IndexOf(node.DeclAttributes, "ObjC") != -1)
            {
                return true;
            }

            string? usr = node.usr;
            return usr is not null
                && (usr.StartsWith("c:objc(", StringComparison.Ordinal)
                    || usr.StartsWith("c:@T@", StringComparison.Ordinal));
        }

        /// <summary>
        /// True when an ABI node is a re-export stub the digester materialized for a Clang-rooted
        /// declaration this module does not own. Two facts have to hold together:
        /// <c>isExternal</c> (the digester's own statement that the declaration belongs to another
        /// module, so this node is a pointer to that module's record rather than a record of its
        /// own) and a Clang USR (<c>c:</c>…, e.g. <c>c:@S@CGSize</c> for a C struct,
        /// <c>c:@SA@simd_quatf</c> for a C aggregate typedef, <c>c:@E@NSComparisonResult</c> for a
        /// C enum, <c>c:@U@…</c> for a union). Such a declaration is written in C, not Swift, so it
        /// has no Swift mangled name to be missing — the field's absence is the expected shape, and
        /// the type resolves through the Apple-supplement / out-of-module path when referenced.
        /// The conjunction is deliberate: an <c>isExternal</c> node with a Swift USR, or a
        /// Clang-USR node the module actually owns, is not covered.
        /// </summary>
        private static bool IsForeignClangReexportStub(Node node)
            => node.isExternal == true
                && node.usr is { } usr
                && usr.StartsWith("c:", StringComparison.Ordinal);

        /// <summary>
        /// Extracts the defining module from a Swift USR's first length-prefixed segment.
        /// <c>s:17RealityFoundation12HasTransformP</c> → "RealityFoundation". Returns false for
        /// stdlib short-form USRs (<c>s:s9EscapableP</c>) and any non-Swift USR — callers keep
        /// their existing module in those cases. The USR records a symbol's REAL defining module,
        /// unlike the mangled name, which can carry an <c>@_originallyDefinedIn</c> original module.
        /// </summary>
        internal static bool TryGetModuleFromSwiftUsr(string? usr, [NotNullWhen(true)] out string? module)
        {
            module = null;
            if (string.IsNullOrEmpty(usr) || !usr.StartsWith("s:", StringComparison.Ordinal))
                return false;
            int i = 2;
            int digitStart = i;
            while (i < usr.Length && char.IsDigit(usr[i])) i++;
            if (i == digitStart) // no length prefix (e.g. stdlib short form "s:s9...")
                return false;
            if (!int.TryParse(usr.AsSpan(digitStart, i - digitStart), out int len) || len <= 0)
                return false;
            if (i + len > usr.Length)
                return false;
            module = usr.Substring(i, len);
            return true;
        }

        /// <summary>
        /// Check if the name is an operator.
        /// </summary>
        /// <param name="name">The name to check.</param>
        /// <returns>True if the name is an operator, false otherwise.</returns>
        private static bool IsOperator(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            return name.All(c => _operatorChars.Contains(c));
        }

        /// <summary>
        /// Patches the mangled name of a constructor.
        /// </summary>
        /// <param name="mangledName">The mangled name to patch.</param>
        /// <returns>The patched mangled name.</returns>
        private string PatchMangledName(string mangledName)
        {
            if (mangledName.Last() == 'c')
            {
                return mangledName.Substring(0, mangledName.Length - 1) + "C";
            }
            return mangledName;
        }
    }
}
