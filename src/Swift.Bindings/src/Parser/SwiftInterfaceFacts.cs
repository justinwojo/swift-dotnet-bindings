// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace BindingsGeneration
{
    /// <summary>
    /// Immutable aggregate of every supplementary fact <see cref="SwiftInterfaceAccessParser"/>
    /// extracts from a public swiftinterface and feeds into <see cref="SwiftABIParser"/>.
    /// Replaces 21 individually-threaded nullable side-channel maps with one drift-loud
    /// hand-off: every field is a <c>required</c> init property, so adding one without also
    /// updating <see cref="Empty"/> and the producer/consumer call sites is a compile error,
    /// not a silent miss.
    /// <para/>
    /// Field types match what <see cref="SwiftInterfaceAccessParser"/> returns and what
    /// downstream consumers (parser fields, <c>StringEmitter</c>) already expect — the record
    /// itself is the immutable surface; collection contents inherit the mutability profile
    /// of the existing pre-aggregator code.
    /// </summary>
    public sealed record SwiftInterfaceFacts
    {
        /// <summary>"TypeName.printedName" keys for ABI-visible members that are actually
        /// internal in the swiftinterface (e.g., @inlinable internal). Detected when the
        /// member is absent from the public swiftinterface but present in ABI JSON.</summary>
        public required HashSet<string> InternalMemberKeys { get; init; }

        /// <summary>"TypeName.printedName" keys (or bare printedName for free functions) for
        /// every public member discovered in the swiftinterface — used to flip undeclared
        /// members to internal via negative-space detection.</summary>
        public required HashSet<string> PublicMemberNames { get; init; }

        /// <summary>Per-member internal parameter names. Key: "QualifiedType.printedName" or
        /// bare printedName. Value: index-aligned list of internal names. Drives meaningful
        /// C# parameter identifiers instead of <c>arg0</c>/<c>arg1</c>.</summary>
        public required Dictionary<string, List<string>> ParameterNames { get; init; }

        /// <summary>Per-member typed-throws error type. Key: "QualifiedType.printedName" or
        /// bare printedName. Value: fully-qualified Swift error type
        /// (e.g., "SwiftBindingsTestLib.ParseError").</summary>
        public required Dictionary<string, string> TypedThrowsErrors { get; init; }

        /// <summary>Per-enum-case associated-value labels. Key: "TypeName.caseName"
        /// (e.g., "Shape.circle"). Value: list of labels with <c>null</c> entries for
        /// unlabeled associated values.</summary>
        public required Dictionary<string, List<string?>> EnumCaseLabels { get; init; }

        /// <summary>String-typed enum raw values. Key: "TypeName.caseName"
        /// (e.g., "HttpMethod.get"). Value: literal raw string (e.g., "GET").</summary>
        public required Dictionary<string, string> EnumCaseRawValues { get; init; }

        /// <summary>Set of dot-qualified public type names from the swiftinterface. Types
        /// not in this set (when the set is non-empty) are internal to the module. Also used
        /// as keep-override for underscore-prefixed type suppression.</summary>
        public required HashSet<string> PublicTypeNames { get; init; }

        /// <summary>Qualified type paths for types annotated <c>@MainActor</c>.</summary>
        public required HashSet<string> MainActorTypes { get; init; }

        /// <summary>Qualified type paths for types declared with the <c>actor</c>
        /// keyword.</summary>
        public required HashSet<string> CustomActorTypes { get; init; }

        /// <summary>Qualified type path → matched custom-global-actor short name
        /// (e.g., <c>"TypeName" → "CustomActorName"</c> for
        /// <c>@CustomActorName class TypeName</c>). Distinct from
        /// <see cref="CustomActorTypes"/>, which holds the <c>actor X { }</c> keyword form.
        /// Drives <c>TypeDecl.CustomActorIsolatorName</c> and SWIFTBIND022 diagnostics.
        /// </summary>
        public required Dictionary<string, string> CustomActorIsolatorMap { get; init; }

        /// <summary>"TypeName.memberName" keys for actor-isolated members (both
        /// <c>@MainActor</c> and custom actors).</summary>
        public required HashSet<string> ActorIsolatedMembers { get; init; }

        /// <summary>"TypeName.memberName" keys for <c>@MainActor</c>-isolated members only
        /// — strict subset of <see cref="ActorIsolatedMembers"/>. Used to distinguish
        /// <c>@MainActor</c> from custom-actor isolation when populating
        /// <c>IsMainActorIsolated</c>.</summary>
        public required HashSet<string> MainActorIsolatedMembers { get; init; }

        /// <summary>"TypeName.memberName" keys for <c>nonisolated</c> members.</summary>
        public required HashSet<string> NonisolatedMembers { get; init; }

        /// <summary>Marker-protocol conformances harvested from the swiftinterface.
        /// Key: protocol name. Value: list of conforming type names. Consumed by
        /// <c>StringEmitter</c>'s marker-protocol overload emitter — not by the parser
        /// itself — but lives here because the producer is the same swiftinterface pass.
        /// </summary>
        public required Dictionary<string, List<string>> MarkerProtocolConformances { get; init; }

        /// <summary>Per-decl <c>@available</c> annotations. Key: qualified type path or
        /// "TypePath.printedName". Value: list of platform/version annotations.</summary>
        public required Dictionary<string, List<AvailabilityAnnotation>> AvailabilityAnnotations { get; init; }

        /// <summary>Per-method default-parameter expressions. Key: "QualifiedType.printedName".
        /// Value: index-aligned list of raw Swift default expressions (<c>null</c> entries
        /// for parameters without a default).</summary>
        public required Dictionary<string, List<string?>> DefaultParameterValues { get; init; }

        /// <summary>Per-method <c>@autoclosure</c> parameter flags.
        /// Key: "QualifiedType.printedName". Value: index-aligned list of booleans.</summary>
        public required Dictionary<string, List<bool>> AutoclosureParameters { get; init; }

        /// <summary>Per-method <c>_const</c> (compile-time-constant) parameter flags.
        /// Key: "QualifiedType.printedName" or bare printedName for free functions.
        /// Value: index-aligned list of booleans — <c>true</c> for parameters declared with
        /// the <c>_const</c> modifier in the swiftinterface (e.g.,
        /// <c>init(min: _const Swift.Int, max: _const Swift.Int)</c>). The ABI JSON strips
        /// the annotation; the swiftinterface is the only source. Wrapper emitters reject
        /// any member with a <c>_const</c> parameter because the @_cdecl boundary passes
        /// runtime values and Swift would reject the call with
        /// "expect a compile-time constant literal".</summary>
        public required Dictionary<string, List<bool>> ConstLiteralParameters { get; init; }

        /// <summary>Per-parameter closure type-level attributes (<c>@MainActor</c>,
        /// <c>@Sendable</c>) on protocol requirements. Key: "TypeName.member(labels:)".
        /// Value: index-aligned with parameters; each entry is the list of normalized
        /// attribute names (e.g. <c>["MainActor", "Sendable"]</c>) carried by that
        /// parameter's closure type, empty for params without such attributes. ABI JSON
        /// strips these attributes; the swiftinterface is the only source. Consumed by
        /// <c>SwiftABIParser.ApplyMemberClosureAttributeFlags</c> so the synthesized
        /// <c>extension EveryProtocol: SomeProtocol</c> conformance reproduces the
        /// requirement's exact closure type and compiles.</summary>
        public required Dictionary<string, List<List<string>>> ClosureParameterAttributes { get; init; }

        /// <summary>Subscript external labels. Key: "TypeName.subscript(label1:label2:)"
        /// (e.g., "AES.subscript(bitAt:)"). Value: list of external labels (e.g.,
        /// <c>["bitAt"]</c>).</summary>
        public required Dictionary<string, List<string>> SubscriptLabels { get; init; }

        /// <summary>"TypeName.printedName" keys for members with variadic parameters.
        /// ABI JSON represents variadics as <c>Array&lt;T&gt;</c>, so this set is the only
        /// way to recover <c>T...</c> for <c>@_cdecl</c> wrapper emission.</summary>
        public required HashSet<string> VariadicMembers { get; init; }

        /// <summary>Protocol names whose methods carry <c>@convention(c)</c> or
        /// <c>@convention(block)</c> closure parameters. Detected from swiftinterface
        /// because ABI JSON lacks convention attributes on <c>TypeFunc</c> nodes.</summary>
        public required HashSet<string> ConventionCProtocols { get; init; }

        /// <summary>Protocol name → set of underscore-prefixed requirement names declared
        /// in the swiftinterface body but lacking any same-module extension default.
        /// Drives <c>HasUnsatisfiedHiddenRequirements</c> on protocols whose witness can
        /// never be satisfied from generated code (e.g.
        /// <c>RealityFoundation.MaterialFunction.__linkSPI</c>).</summary>
        public required Dictionary<string, HashSet<string>> HiddenRequirementProtocols { get; init; }

        /// <summary>Best-effort source positions for entries in <see cref="MainActorTypes"/>.
        /// Key matches the entry in <see cref="MainActorTypes"/>; missing keys mean the parser
        /// could not attribute the fact to a specific match offset — callers should treat
        /// that as <c>null</c> rather than fabricating a position.</summary>
        public required Dictionary<string, SourcePosition> MainActorTypePositions { get; init; }

        /// <summary>Best-effort source positions for entries in
        /// <see cref="AvailabilityAnnotations"/>. Key matches the dictionary key in
        /// <see cref="AvailabilityAnnotations"/> (qualified type path or
        /// "TypePath.printedName"); missing keys mean no position was extractable.</summary>
        public required Dictionary<string, SourcePosition> AvailabilityAnnotationPositions { get; init; }

        /// <summary>Best-effort source positions for entries in
        /// <see cref="ConventionCProtocols"/>. Key matches the protocol name; the position
        /// points at the protocol declaration line that triggered the convention-c
        /// detection. Missing keys mean no position was extractable.</summary>
        public required Dictionary<string, SourcePosition> ConventionCProtocolPositions { get; init; }

        /// <summary>Names of every <c>public</c> / <c>open</c> protocol declared in this
        /// module's swiftinterface. Unqualified (e.g., "ProtocolName"). Drives same-module
        /// protocol-extension classification in
        /// <see cref="ResolveForeignExtensions"/> and <see cref="ProtocolExtensionMethods"/>.
        /// </summary>
        public required HashSet<string> ProtocolNames { get; init; }

        /// <summary>Per-protocol direct-extension members keyed by the verbatim extension
        /// target (e.g. "Mod.MyProto" or unqualified "MyProto"). Each member is a default
        /// implementation provided in an <c>extension MyProto { ... }</c> block. Direct
        /// members only — declarations inside nested types within the extension body
        /// are excluded.</summary>
        public required Dictionary<string, List<ProtocolExtensionMethodDecl>> ProtocolExtensionMethods { get; init; }

        /// <summary>SPI-only conformances harvested from <c>*.private.swiftinterface</c> —
        /// each entry is <c>"QualifiedType::ProtocolName"</c>. Populated when a
        /// <c>@_spi(...) extension Mod.Type : Proto1, Proto2</c> block is found and the
        /// matching public swiftinterface does not declare the same conformance. The
        /// <see cref="SwiftABIParser"/> filters these entries out of struct/class/enum
        /// conformance lists so the generated wrapper does not reference operators or
        /// protocol methods that are unreachable under a plain (non-@_spi) <c>import</c>.
        /// Empty when no private interface is available or when private/public agree.
        /// </summary>
        public required HashSet<string> SpiOnlyConformances { get; init; }

        /// <summary>Flat list of every direct member from every <c>extension X { ... }</c>
        /// block, module-context-free. Foreign-type-extension partitioning is deferred to
        /// <see cref="ResolveForeignExtensions"/> because <c>moduleTypeNames</c> is only
        /// available after the ABI parse. Same shape as
        /// <see cref="ProtocolExtensionMethods"/>'s value list, but keyed off
        /// <see cref="ExtensionMemberCandidate.ExtendedTypeName"/> instead of by
        /// protocol.</summary>
        public required List<ExtensionMemberCandidate> ExtensionMemberCandidates { get; init; }

        /// <summary>
        /// Best-effort lookup helper. Searches every position dictionary on this facts
        /// instance for <paramref name="key"/> and returns the first hit. Used by skip-
        /// emission sites that have a fact key but don't know which producer created it.
        /// Returns <c>null</c> when no position is recorded — that's the "best-effort"
        /// signal: facts derived from ABI JSON or synthesized decls have no source line.
        /// </summary>
        public SourcePosition? TryGetPosition(string key)
        {
            if (MainActorTypePositions.TryGetValue(key, out var p)) return p;
            if (AvailabilityAnnotationPositions.TryGetValue(key, out p)) return p;
            if (ConventionCProtocolPositions.TryGetValue(key, out p)) return p;
            return null;
        }

        /// <summary>
        /// Partitions <see cref="ExtensionMemberCandidates"/> into a foreign-type-extension
        /// dictionary using the same first-dot rule as the legacy
        /// <c>SwiftInterfaceAccessParser.GetForeignTypeExtensionMembers</c>:
        /// <list type="bullet">
        /// <item>Qualified extension target (<c>Mod.X</c>) is foreign when the first segment
        /// is not <paramref name="moduleName"/>.</item>
        /// <item>Unqualified extension target (<c>X</c>) is foreign when neither
        /// <paramref name="moduleTypeNames"/> nor <see cref="ProtocolNames"/> contains it.</item>
        /// <item>Protocol extensions (target appears in <see cref="ProtocolNames"/>) are
        /// excluded — they are surfaced via <see cref="ProtocolExtensionMethods"/>.</item>
        /// </list>
        /// Result key is the verbatim <see cref="ExtensionMemberCandidate.ExtendedTypeName"/>;
        /// values are <see cref="ProtocolExtensionMethodDecl"/> instances with
        /// <c>ProtocolQualifiedName</c> set to the same key.
        /// </summary>
        public Dictionary<string, List<ProtocolExtensionMethodDecl>> ResolveForeignExtensions(
            string moduleName, ISet<string> moduleTypeNames)
        {
            var result = new Dictionary<string, List<ProtocolExtensionMethodDecl>>();
            foreach (var candidate in ExtensionMemberCandidates)
            {
                var qualified = candidate.ExtendedTypeName;
                var firstDotIdx = qualified.IndexOf('.');
                var typePath = firstDotIdx >= 0 ? qualified.Substring(firstDotIdx + 1) : qualified;

                // Protocol extensions are NOT foreign — surfaced via ProtocolExtensionMethods.
                if (ProtocolNames.Contains(typePath))
                    continue;

                bool isForeign;
                if (firstDotIdx >= 0)
                {
                    var modulePrefix = qualified.Substring(0, firstDotIdx);
                    isForeign = !string.Equals(modulePrefix, moduleName, StringComparison.Ordinal);
                }
                else
                {
                    isForeign = !moduleTypeNames.Contains(typePath) && !ProtocolNames.Contains(typePath);
                }

                if (!isForeign)
                    continue;

                if (!result.TryGetValue(qualified, out var list))
                {
                    list = new List<ProtocolExtensionMethodDecl>();
                    result[qualified] = list;
                }
                list.Add(CandidateToDecl(candidate, qualified));
            }
            return result;
        }

        /// <summary>1:1 conversion from the candidate row to the decl shape downstream
        /// emitters expect. The only field that changes is the type-side key
        /// (<see cref="ExtensionMemberCandidate.ExtendedTypeName"/> →
        /// <see cref="ProtocolExtensionMethodDecl.ProtocolQualifiedName"/>).</summary>
        internal static ProtocolExtensionMethodDecl CandidateToDecl(
            ExtensionMemberCandidate candidate, string qualifiedName)
        {
            return new ProtocolExtensionMethodDecl
            {
                ProtocolQualifiedName = qualifiedName,
                MethodName = candidate.MethodName,
                RawSignature = candidate.RawSignature,
                PrintedName = candidate.PrintedName,
                ReturnsSelf = candidate.ReturnsSelf,
                IsMainActorIsolated = candidate.IsMainActorIsolated,
                IsStatic = candidate.IsStatic,
                IsProperty = candidate.IsProperty,
                HasSetter = candidate.HasSetter,
                IsDeprecated = candidate.IsDeprecated,
                IsMutating = candidate.IsMutating,
                WhereConstraints = new List<string>(candidate.WhereConstraints),
            };
        }

        /// <summary>The "no swiftinterface" sentinel — every collection empty. Hand this to
        /// <see cref="SwiftABIParser"/> when no swiftinterface is available (dependency
        /// modules, test fixtures); behavior is identical to passing <c>null</c> for each
        /// individual map under the old API.
        /// <para/>
        /// Returns a fresh instance on every access. The fields are concrete mutable
        /// collections (matching producer return types and consumer signatures), so a shared
        /// singleton would let any caller's <c>.Add</c>/index-assign contaminate every other
        /// caller's "empty" baseline. A fresh instance per access keeps each empty fact bag
        /// isolated even if a downstream consumer mutates one of its collections.</summary>
        public static SwiftInterfaceFacts Empty => new()
        {
            InternalMemberKeys = new HashSet<string>(),
            PublicMemberNames = new HashSet<string>(),
            ParameterNames = new Dictionary<string, List<string>>(),
            TypedThrowsErrors = new Dictionary<string, string>(),
            EnumCaseLabels = new Dictionary<string, List<string?>>(),
            EnumCaseRawValues = new Dictionary<string, string>(),
            PublicTypeNames = new HashSet<string>(),
            MainActorTypes = new HashSet<string>(),
            CustomActorTypes = new HashSet<string>(),
            CustomActorIsolatorMap = new Dictionary<string, string>(),
            ActorIsolatedMembers = new HashSet<string>(),
            MainActorIsolatedMembers = new HashSet<string>(),
            NonisolatedMembers = new HashSet<string>(),
            MarkerProtocolConformances = new Dictionary<string, List<string>>(),
            AvailabilityAnnotations = new Dictionary<string, List<AvailabilityAnnotation>>(),
            DefaultParameterValues = new Dictionary<string, List<string?>>(),
            AutoclosureParameters = new Dictionary<string, List<bool>>(),
            ConstLiteralParameters = new Dictionary<string, List<bool>>(),
            ClosureParameterAttributes = new Dictionary<string, List<List<string>>>(),
            SubscriptLabels = new Dictionary<string, List<string>>(),
            VariadicMembers = new HashSet<string>(),
            ConventionCProtocols = new HashSet<string>(),
            HiddenRequirementProtocols = new Dictionary<string, HashSet<string>>(),
            MainActorTypePositions = new Dictionary<string, SourcePosition>(),
            AvailabilityAnnotationPositions = new Dictionary<string, SourcePosition>(),
            ConventionCProtocolPositions = new Dictionary<string, SourcePosition>(),
            ProtocolNames = new HashSet<string>(),
            ProtocolExtensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>(),
            ExtensionMemberCandidates = new List<ExtensionMemberCandidate>(),
            SpiOnlyConformances = new HashSet<string>(),
        };
    }
}
