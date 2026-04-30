// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
        /// (e.g., <c>"ImagePipeline" → "ImagePipelineActor"</c> for
        /// <c>@ImagePipelineActor class ImagePipeline</c>). Distinct from
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
            SubscriptLabels = new Dictionary<string, List<string>>(),
            VariadicMembers = new HashSet<string>(),
            ConventionCProtocols = new HashSet<string>(),
            HiddenRequirementProtocols = new Dictionary<string, HashSet<string>>(),
        };
    }
}
