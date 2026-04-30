// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration.Producers;

/// <summary>
/// Identifies a single fact field on <see cref="SwiftInterfaceFacts"/>. One enum member
/// per top-level field on the record (24 total today). The aggregator selects per-fact
/// which producer wins, so coverage maps are <see cref="System.Collections.Generic.HashSet{T}"/>
/// of these.
/// <para/>
/// Adding a fact to <see cref="SwiftInterfaceFacts"/> WITHOUT also adding the matching
/// <see cref="InterfaceFactKind"/> entry is a parity-test failure: the aggregator would
/// silently drop the new fact from any producer's coverage. The unit test
/// <c>InterfaceFactKindCoversEveryFactsField</c> enforces 1:1 alignment.
/// </summary>
public enum InterfaceFactKind
{
    InternalMemberKeys,
    PublicMemberNames,
    ParameterNames,
    TypedThrowsErrors,
    EnumCaseLabels,
    EnumCaseRawValues,
    PublicTypeNames,
    MainActorTypes,
    CustomActorTypes,
    CustomActorIsolatorMap,
    ActorIsolatedMembers,
    MainActorIsolatedMembers,
    NonisolatedMembers,
    MarkerProtocolConformances,
    AvailabilityAnnotations,
    DefaultParameterValues,
    AutoclosureParameters,
    SubscriptLabels,
    VariadicMembers,
    ConventionCProtocols,
    HiddenRequirementProtocols,
    MainActorTypePositions,
    AvailabilityAnnotationPositions,
    ConventionCProtocolPositions,
}

internal static class InterfaceFactKindHelpers
{
    /// <summary>All 24 fact kinds. Used by RegexProducer to declare full coverage and
    /// by tests asserting 1:1 alignment with <see cref="SwiftInterfaceFacts"/>.</summary>
    internal static readonly HashSet<InterfaceFactKind> AllFactKinds = new(
        System.Enum.GetValues<InterfaceFactKind>());
}
