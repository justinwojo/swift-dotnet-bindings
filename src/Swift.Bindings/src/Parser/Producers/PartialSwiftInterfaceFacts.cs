// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration.Producers;

/// <summary>
/// A producer's contribution to <see cref="SwiftInterfaceFacts"/>. Every field is
/// nullable; null means "this producer did not populate this fact." The companion
/// <see cref="ProducerResult.CoveredFacts"/> set is the authoritative coverage signal —
/// nulls are a defense-in-depth check, not the contract surface.
/// <para/>
/// The aggregator merges per fact: for each <see cref="InterfaceFactKind"/>, it picks
/// whichever producer covers it (per a per-fact source plan) and copies that producer's
/// non-null collection into the final <see cref="SwiftInterfaceFacts"/> instance.
/// Producers that don't cover a fact leave the corresponding field null and never
/// have their data used for that fact.
/// </summary>
public sealed record PartialSwiftInterfaceFacts
{
    public HashSet<string>? InternalMemberKeys { get; init; }
    public HashSet<string>? PublicMemberNames { get; init; }
    public Dictionary<string, List<string>>? ParameterNames { get; init; }
    public Dictionary<string, string>? TypedThrowsErrors { get; init; }
    public Dictionary<string, List<string?>>? EnumCaseLabels { get; init; }
    public Dictionary<string, string>? EnumCaseRawValues { get; init; }
    public HashSet<string>? PublicTypeNames { get; init; }
    public HashSet<string>? MainActorTypes { get; init; }
    public HashSet<string>? CustomActorTypes { get; init; }
    public Dictionary<string, string>? CustomActorIsolatorMap { get; init; }
    public HashSet<string>? ActorIsolatedMembers { get; init; }
    public HashSet<string>? MainActorIsolatedMembers { get; init; }
    public HashSet<string>? NonisolatedMembers { get; init; }
    public Dictionary<string, List<string>>? MarkerProtocolConformances { get; init; }
    public Dictionary<string, List<AvailabilityAnnotation>>? AvailabilityAnnotations { get; init; }
    public Dictionary<string, List<string?>>? DefaultParameterValues { get; init; }
    public Dictionary<string, List<bool>>? AutoclosureParameters { get; init; }
    public Dictionary<string, List<bool>>? ConstLiteralParameters { get; init; }
    public Dictionary<string, List<string>>? SubscriptLabels { get; init; }
    public HashSet<string>? VariadicMembers { get; init; }
    public HashSet<string>? ConventionCProtocols { get; init; }
    public Dictionary<string, HashSet<string>>? HiddenRequirementProtocols { get; init; }
    public Dictionary<string, SourcePosition>? MainActorTypePositions { get; init; }
    public Dictionary<string, SourcePosition>? AvailabilityAnnotationPositions { get; init; }
    public Dictionary<string, SourcePosition>? ConventionCProtocolPositions { get; init; }

    // M2 S4 — non-fact methods migrated behind the producer abstraction.
    public HashSet<string>? ProtocolNames { get; init; }
    public Dictionary<string, List<ProtocolExtensionMethodDecl>>? ProtocolExtensionMethods { get; init; }
    public List<ExtensionMemberCandidate>? ExtensionMemberCandidates { get; init; }

    /// <summary>SPI-only conformances harvested from <c>*.private.swiftinterface</c>.
    /// Each entry is <c>"QualifiedType::ProtocolName"</c> (e.g.,
    /// <c>"StripeCore.StripeAPI.BankAccountToken::Equatable"</c>). Drives the
    /// <see cref="SwiftABIParser"/> conformance filter so the wrapper does not call
    /// operators or methods that vanish under a plain <c>import</c>.</summary>
    public HashSet<string>? SpiOnlyConformances { get; init; }

    /// <summary>An empty partial — every fact null. Useful as a starting point in tests.</summary>
    public static PartialSwiftInterfaceFacts Empty => new();
}

/// <summary>Result of a producer invocation: facts payload + the set of facts the
/// producer actually populated.</summary>
public sealed record ProducerResult(
    PartialSwiftInterfaceFacts Facts,
    HashSet<InterfaceFactKind> CoveredFacts);
