// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration.Producers;

/// <summary>
/// Identifies a single fact field on <see cref="SwiftInterfaceFacts"/>. One enum member
/// per top-level field on the record (31 total today). The aggregator selects per-fact
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
    // Protocol-discovery results surfaced through the producer abstraction so
    // Program.cs does not need to reach past IInterfaceFactsProducer.
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
    // Per-parameter closure type-level attributes (`@MainActor`, `@Sendable`) on protocol
    // requirements. swift-api-digester strips these from the ABI JSON, so the swiftinterface
    // is the only source. Needed so the synthesized `extension EveryProtocol: SomeProtocol`
    // conformance reproduces the requirement's exact closure type.
    ClosureParameterAttributes,
    // Qualified-type-path → explicit `@objc(CustomName)` runtime name. The ABI JSON keeps an
    // `@objc` Swift class's `$s…` mangled name and drops the ObjC selector argument, so the
    // custom runtime name lives only in the swiftinterface. Threads into TypeDecl.ObjCRuntimeName
    // and the swift-types.json ownership manifest so mixed-framework dedup matches ObjC decls
    // by their runtime name (Finding 23).
    ObjCRuntimeNames,
}

internal static class InterfaceFactKindHelpers
{
    /// <summary>All fact kinds, including the producer-abstracted fields
    /// (ProtocolNames/ProtocolExtensionMethods/ExtensionMemberCandidates), SPI-only conformances
    /// (0.11.0 R2), and const-literal parameters (0.12.0). Used by
    /// <see cref="SwiftSyntaxInterfaceFactsProducer"/> to declare full coverage and by tests
    /// asserting 1:1 alignment with <see cref="SwiftInterfaceFacts"/>.</summary>
    internal static readonly HashSet<InterfaceFactKind> AllFactKinds = new(
        System.Enum.GetValues<InterfaceFactKind>());
}
