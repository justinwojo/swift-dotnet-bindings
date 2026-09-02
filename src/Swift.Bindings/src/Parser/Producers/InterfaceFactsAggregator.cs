// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.Producers;

/// <summary>
/// Runs an ordered list of <see cref="IInterfaceFactsProducer"/> against a single
/// .swiftinterface and merges their per-fact contributions into one
/// <see cref="SwiftInterfaceFacts"/>.
/// <para/>
/// MERGE RULE: per <see cref="InterfaceFactKind"/>, the FIRST producer in the ordered
/// list that declares coverage AND ships a non-null payload wins. Producers that don't
/// cover a fact are skipped for that fact. If no producer covers a fact, the empty
/// collection from <see cref="SwiftInterfaceFacts.Empty"/> is used.
/// <para/>
/// The production wiring is a single producer — <see cref="SwiftSyntaxInterfaceFactsProducer"/>,
/// which covers every <see cref="InterfaceFactKind"/> (see <see cref="CreateDefault"/>). The
/// ordered-merge machinery is retained because it is the natural shape for layering an
/// additional producer in the future; with one producer the "first with coverage wins" rule
/// degenerates to "use that producer's payload for every fact it covers." Coverage is data,
/// not policy — the aggregator does not hard-code how many producers it runs.
/// </summary>
public sealed class InterfaceFactsAggregator
{
    private readonly IReadOnlyList<IInterfaceFactsProducer> _producers;

    /// <summary>
    /// Construct an aggregator with producers in priority order. Earlier producers shadow
    /// later producers' coverage of the same fact. The default wiring is a single
    /// <see cref="SwiftSyntaxInterfaceFactsProducer"/>; see <see cref="CreateDefault"/>.
    /// </summary>
    public InterfaceFactsAggregator(IReadOnlyList<IInterfaceFactsProducer> producers)
    {
        _producers = producers ?? throw new ArgumentNullException(nameof(producers));
        if (_producers.Count == 0)
            throw new ArgumentException("At least one producer is required.", nameof(producers));
    }

    /// <summary>
    /// Build the production interface-facts aggregator: a single
    /// <see cref="SwiftSyntaxInterfaceFactsProducer"/> backed by the SwiftInterfaceParser
    /// host binary, which covers every <see cref="InterfaceFactKind"/>.
    /// <para/>
    /// This generator is macOS-only by design: the host binary is built only for Darwin and
    /// there is no fallback producer. Hard-fails (rather than silently degrading) on non-Darwin
    /// or when the host binary cannot be located — emitting bindings without interface facts
    /// would silently drop actor-isolation, availability, typed-throws, default-parameter, and
    /// SPI metadata.
    /// </summary>
    public static InterfaceFactsAggregator CreateDefault(ILogger logger)
    {
        if (!OperatingSystem.IsMacOS())
            throw new InvalidOperationException(
                "Swift bindings generation requires macOS: the SwiftInterfaceParser host binary " +
                "(which extracts .swiftinterface facts) is built only for Darwin, and there is no " +
                "fallback producer. Run the generator on macOS.");

        var binaryPath = SwiftSyntaxInterfaceFactsProducer.TryLocateBinary()
            ?? throw new InvalidOperationException(
                "Could not locate the SwiftInterfaceParser host binary. Run `nuke compile` to build " +
                "tools/SwiftInterfaceParser, or set SWIFT_INTERFACE_PARSER_PATH to point at it.");

        logger.LogInformation("Using SwiftSyntax interface facts producer at: {Path}", binaryPath);
        return new InterfaceFactsAggregator(new IInterfaceFactsProducer[]
        {
            new SwiftSyntaxInterfaceFactsProducer(binaryPath, GeneratorTimeouts.ResolveParserTimeout()),
        });
    }

    /// <summary>
    /// Run every producer over <paramref name="swiftInterfacePath"/> and assemble a single
    /// <see cref="SwiftInterfaceFacts"/> from their merged output. Producers run sequentially.
    /// Any producer throw bubbles: a thrown <see cref="SwiftSyntaxInterfaceFactsProducer"/>
    /// exception is the chosen drift-signal — we fail visibly rather than emit half-correct
    /// bindings.
    /// </summary>
    public SwiftInterfaceFacts Aggregate(string swiftInterfacePath, ILogger logger)
    {
        var results = new List<ProducerResult>(_producers.Count);
        foreach (var producer in _producers)
        {
            logger.LogDebug("Running interface facts producer '{Name}' on {Path}", producer.Name, swiftInterfacePath);
            results.Add(producer.Produce(swiftInterfacePath, logger));
        }

        // Build the final facts by walking each kind and picking the first producer whose
        // coverage AND payload are both populated.
        var empty = SwiftInterfaceFacts.Empty;
        return new SwiftInterfaceFacts
        {
            InternalMemberKeys = Pick(results, InterfaceFactKind.InternalMemberKeys, p => p.InternalMemberKeys, empty.InternalMemberKeys),
            PublicMemberNames = Pick(results, InterfaceFactKind.PublicMemberNames, p => p.PublicMemberNames, empty.PublicMemberNames),
            ParameterNames = Pick(results, InterfaceFactKind.ParameterNames, p => p.ParameterNames, empty.ParameterNames),
            TypedThrowsErrors = Pick(results, InterfaceFactKind.TypedThrowsErrors, p => p.TypedThrowsErrors, empty.TypedThrowsErrors),
            EnumCaseLabels = Pick(results, InterfaceFactKind.EnumCaseLabels, p => p.EnumCaseLabels, empty.EnumCaseLabels),
            EnumCaseRawValues = Pick(results, InterfaceFactKind.EnumCaseRawValues, p => p.EnumCaseRawValues, empty.EnumCaseRawValues),
            PublicTypeNames = Pick(results, InterfaceFactKind.PublicTypeNames, p => p.PublicTypeNames, empty.PublicTypeNames),
            MainActorTypes = Pick(results, InterfaceFactKind.MainActorTypes, p => p.MainActorTypes, empty.MainActorTypes),
            CustomActorTypes = Pick(results, InterfaceFactKind.CustomActorTypes, p => p.CustomActorTypes, empty.CustomActorTypes),
            CustomActorIsolatorMap = Pick(results, InterfaceFactKind.CustomActorIsolatorMap, p => p.CustomActorIsolatorMap, empty.CustomActorIsolatorMap),
            ActorIsolatedMembers = Pick(results, InterfaceFactKind.ActorIsolatedMembers, p => p.ActorIsolatedMembers, empty.ActorIsolatedMembers),
            MainActorIsolatedMembers = Pick(results, InterfaceFactKind.MainActorIsolatedMembers, p => p.MainActorIsolatedMembers, empty.MainActorIsolatedMembers),
            NonisolatedMembers = Pick(results, InterfaceFactKind.NonisolatedMembers, p => p.NonisolatedMembers, empty.NonisolatedMembers),
            MarkerProtocolConformances = Pick(results, InterfaceFactKind.MarkerProtocolConformances, p => p.MarkerProtocolConformances, empty.MarkerProtocolConformances),
            AvailabilityAnnotations = Pick(results, InterfaceFactKind.AvailabilityAnnotations, p => p.AvailabilityAnnotations, empty.AvailabilityAnnotations),
            DefaultParameterValues = Pick(results, InterfaceFactKind.DefaultParameterValues, p => p.DefaultParameterValues, empty.DefaultParameterValues),
            AutoclosureParameters = Pick(results, InterfaceFactKind.AutoclosureParameters, p => p.AutoclosureParameters, empty.AutoclosureParameters),
            ConstLiteralParameters = Pick(results, InterfaceFactKind.ConstLiteralParameters, p => p.ConstLiteralParameters, empty.ConstLiteralParameters),
            ClosureParameterAttributes = Pick(results, InterfaceFactKind.ClosureParameterAttributes, p => p.ClosureParameterAttributes, empty.ClosureParameterAttributes),
            ObjCRuntimeNames = Pick(results, InterfaceFactKind.ObjCRuntimeNames, p => p.ObjCRuntimeNames, empty.ObjCRuntimeNames),
            SubscriptLabels = Pick(results, InterfaceFactKind.SubscriptLabels, p => p.SubscriptLabels, empty.SubscriptLabels),
            VariadicMembers = Pick(results, InterfaceFactKind.VariadicMembers, p => p.VariadicMembers, empty.VariadicMembers),
            AsyncAccessorMembers = Pick(results, InterfaceFactKind.AsyncAccessorMembers, p => p.AsyncAccessorMembers, empty.AsyncAccessorMembers),
            ConventionCProtocols = Pick(results, InterfaceFactKind.ConventionCProtocols, p => p.ConventionCProtocols, empty.ConventionCProtocols),
            HiddenRequirementProtocols = Pick(results, InterfaceFactKind.HiddenRequirementProtocols, p => p.HiddenRequirementProtocols, empty.HiddenRequirementProtocols),
            MainActorTypePositions = Pick(results, InterfaceFactKind.MainActorTypePositions, p => p.MainActorTypePositions, empty.MainActorTypePositions),
            AvailabilityAnnotationPositions = Pick(results, InterfaceFactKind.AvailabilityAnnotationPositions, p => p.AvailabilityAnnotationPositions, empty.AvailabilityAnnotationPositions),
            ConventionCProtocolPositions = Pick(results, InterfaceFactKind.ConventionCProtocolPositions, p => p.ConventionCProtocolPositions, empty.ConventionCProtocolPositions),
            ProtocolNames = Pick(results, InterfaceFactKind.ProtocolNames, p => p.ProtocolNames, empty.ProtocolNames),
            ProtocolExtensionMethods = Pick(results, InterfaceFactKind.ProtocolExtensionMethods, p => p.ProtocolExtensionMethods, empty.ProtocolExtensionMethods),
            ExtensionMemberCandidates = Pick(results, InterfaceFactKind.ExtensionMemberCandidates, p => p.ExtensionMemberCandidates, empty.ExtensionMemberCandidates),
            SpiOnlyConformances = Pick(results, InterfaceFactKind.SpiOnlyConformances, p => p.SpiOnlyConformances, empty.SpiOnlyConformances),
        };
    }

    private static T Pick<T>(
        List<ProducerResult> results,
        InterfaceFactKind kind,
        Func<PartialSwiftInterfaceFacts, T?> getter,
        T fallback)
        where T : class
    {
        foreach (var r in results)
        {
            if (!r.CoveredFacts.Contains(kind)) continue;
            var value = getter(r.Facts);
            // Defense-in-depth: a producer that declares coverage MUST ship a non-null
            // payload. Empty is fine ("I covered it, found nothing"); null is incoherent.
            // The SwiftSyntax producer already validates this internally, but we re-check
            // at the aggregator boundary to catch any future producer that wires up
            // coverage without populating the payload — silent-fallthrough would mask
            // migration bugs.
            if (value is null)
            {
                throw new InvalidOperationException(
                    $"InterfaceFactsAggregator: producer declared coverage of '{kind}' but " +
                    "emitted a null payload. This is a producer-side bug — empty collections " +
                    "are the correct way to signal 'covered but found nothing'.");
            }
            return value;
        }
        return fallback;
    }
}
