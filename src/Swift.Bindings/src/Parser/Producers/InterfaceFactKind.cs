// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration.Producers;

/// <summary>
/// Identifies a single fact field on <see cref="SwiftInterfaceFacts"/>. One enum member
/// per top-level field on the record (28 total today). The aggregator selects per-fact
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
    // M2 S4 — non-fact methods migrated from SwiftInterfaceAccessParser to the producer
    // abstraction so Program.cs no longer reaches past IInterfaceFactsProducer.
    ProtocolNames,
    ProtocolExtensionMethods,
    ExtensionMemberCandidates,
    // SDK 0.11.0 R2 — SPI-only conformances harvested from *.private.swiftinterface so
    // wrapper emission can drop conformances that vanish under a plain (non-@_spi) import.
    SpiOnlyConformances,
    // Per-parameter `_const` annotation. Swift's `@_const` / `_const` parameter modifier
    // requires the caller to pass a compile-time-constant literal. The runtime @_cdecl
    // wrapper cannot satisfy that — it receives a runtime value — so wrapper emission
    // must skip any member with a `_const` parameter. ABI JSON strips this annotation;
    // the swiftinterface is the only source.
    ConstLiteralParameters,
}

internal static class InterfaceFactKindHelpers
{
    /// <summary>All fact kinds (24 + 3 added in M2 S4 + 1 SPI-only conformances in 0.11.0 R2 +
    /// 1 const-literal parameters in 0.12.0). Used by RegexProducer to declare full coverage
    /// and by tests asserting 1:1 alignment with <see cref="SwiftInterfaceFacts"/>.</summary>
    internal static readonly HashSet<InterfaceFactKind> AllFactKinds = new(
        System.Enum.GetValues<InterfaceFactKind>());
}
