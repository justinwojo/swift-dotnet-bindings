// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration.Producers;

/// <summary>
/// Strategy for extracting facts from a .swiftinterface.
/// <list type="bullet">
/// <item><see cref="RegexInterfaceFactsProducer"/> wraps the existing
///   <see cref="SwiftInterfaceAccessParser"/> and covers all 24 facts.</item>
/// <item><see cref="SwiftSyntaxInterfaceFactsProducer"/> shells out to the
///   SwiftSyntax-backed host program at <c>tools/swift-interface-parser/SwiftInterfaceParser</c>
///   and covers a growing subset of facts (initially <see cref="InterfaceFactKind.MainActorTypes"/>
///   + <see cref="InterfaceFactKind.MainActorTypePositions"/> only).</item>
/// </list>
/// Partial coverage is a first-class state, not an error: the
/// <see cref="InterfaceFactsAggregator"/> chains producers per a per-fact source plan
/// so the regex producer fills any fact the SwiftSyntax producer hasn't migrated yet.
/// <para/>
/// Producers SHOULD degrade gracefully on parse failure (return empty facts + log) so a
/// single corrupt swiftinterface does not abort generation. <see cref="ProducerResult.CoveredFacts"/>
/// is the authoritative coverage signal — a producer that fails halfway should still
/// declare which facts it intended to cover, with the failed fact returned as an empty
/// collection. The regex producer follows this pattern today (per-fact try/catch).
/// </summary>
public interface IInterfaceFactsProducer
{
    /// <summary>Stable identifier for log lines and CLI flag values (e.g. "regex", "swift-syntax").</summary>
    string Name { get; }

    /// <summary>
    /// Extract facts from the given .swiftinterface path. Returns the partial fact bag plus
    /// the set of facts the producer covers. May return <see cref="PartialSwiftInterfaceFacts.Empty"/>
    /// + an empty coverage set when the file is unreadable; the aggregator handles that case.
    /// </summary>
    ProducerResult Produce(string swiftInterfacePath, ILogger logger);
}
