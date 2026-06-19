// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration.Producers;

/// <summary>
/// Strategy for extracting facts from a .swiftinterface.
/// <see cref="SwiftSyntaxInterfaceFactsProducer"/> is the sole implementation: it shells out
/// to the SwiftSyntax-backed host program at
/// <c>tools/swift-interface-parser/SwiftInterfaceParser</c> and covers all
/// <see cref="InterfaceFactKind"/> values. The generator is macOS-only and hard-errors when
/// the host binary is absent — there is no fallback.
/// <para/>
/// Producers SHOULD degrade gracefully on parse failure (return empty facts + log) so a
/// single corrupt swiftinterface does not abort generation. <see cref="ProducerResult.CoveredFacts"/>
/// is the authoritative coverage signal — a producer that fails halfway should still
/// declare which facts it intended to cover, with the failed fact returned as an empty
/// collection.
/// </summary>
public interface IInterfaceFactsProducer
{
    /// <summary>Stable identifier for log lines and CLI flag values (e.g. "swift-syntax").</summary>
    string Name { get; }

    /// <summary>
    /// Extract facts from the given .swiftinterface path. Returns the partial fact bag plus
    /// the set of facts the producer covers. May return <see cref="PartialSwiftInterfaceFacts.Empty"/>
    /// + an empty coverage set when the file is unreadable; the aggregator handles that case.
    /// </summary>
    ProducerResult Produce(string swiftInterfacePath, ILogger logger);
}
