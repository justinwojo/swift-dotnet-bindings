// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.Producers;

/// <summary>
/// The legacy producer — wraps the <see cref="SwiftInterfaceAccessParser"/> regex/state-machine
/// path that's been the only fact source since M4 introduced <see cref="SwiftInterfaceFacts"/>.
/// Covers all 24 fact kinds. This producer is the default until SwiftSyntax reaches full coverage.
/// <para/>
/// Per-fact extraction is wrapped in try/catch (matching the existing TryParseSwiftInterface
/// degrade-to-empty pattern in Program.cs); a single bad regex match still leaves the rest of
/// the facts populated. We always declare full coverage — even if a fact's extraction throws
/// and we return an empty collection for it, the aggregator considers it "covered" so the
/// SwiftSyntax producer is not invoked behind the regex producer's back. This matches the
/// historical behavior where a degraded fact is empty, not absent.
/// </summary>
public sealed class RegexInterfaceFactsProducer : IInterfaceFactsProducer
{
    public string Name => "regex";

    public ProducerResult Produce(string swiftInterfacePath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(swiftInterfacePath) || !System.IO.File.Exists(swiftInterfacePath))
        {
            // No swiftinterface — match SwiftInterfaceFacts.Empty semantics. The regex producer
            // STILL declares full coverage in this case: we are the source-of-truth for these
            // facts, and our truth is "empty" when there's no input. Without coverage, the
            // aggregator would fall through to a downstream producer that almost certainly
            // also has nothing — better to short-circuit cleanly.
            return new ProducerResult(PartialSwiftInterfaceFacts.Empty, new HashSet<InterfaceFactKind>(InterfaceFactKindHelpers.AllFactKinds));
        }

        // Per-fact try wrappers (mirror Program.cs lines 232-352 behavior). Failures degrade
        // to empty; coverage is preserved either way.
        int parseFailures = 0;

        var (internalMemberKeys, publicMemberNames) = TryParse(
            "internal members", logger, ref parseFailures,
            () => {
                var k = SwiftInterfaceAccessParser.GetInternalMembers(swiftInterfacePath, out var pub);
                return (k, pub);
            },
            () => (new HashSet<string>(), new HashSet<string>()));

        var parameterNames = TryParse("parameter names", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetParameterNames(swiftInterfacePath),
            () => new Dictionary<string, List<string>>());

        var typedThrowsErrors = TryParse("typed throws", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetTypedThrowsErrors(swiftInterfacePath),
            () => new Dictionary<string, string>());

        var enumCaseLabels = TryParse("enum case labels", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetEnumCaseLabels(swiftInterfacePath),
            () => new Dictionary<string, List<string?>>());

        var enumCaseRawValues = TryParse("enum raw values", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetEnumRawValues(swiftInterfacePath),
            () => new Dictionary<string, string>());

        var publicTypeNames = TryParse("public type names", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetPublicTypeNames(swiftInterfacePath),
            () => new HashSet<string>());

        var (mainActorTypes, mainActorTypePositions) = TryParse(
            "@MainActor types", logger, ref parseFailures,
            () => {
                var s = SwiftInterfaceAccessParser.GetMainActorTypes(swiftInterfacePath, out var pos);
                return (s, pos);
            },
            () => (new HashSet<string>(), new Dictionary<string, SourcePosition>()));

        var customActorTypes = TryParse("custom actor types", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetCustomActorTypes(swiftInterfacePath),
            () => new HashSet<string>());

        var customActorIsolatorMap = TryParse("custom-actor-isolated types", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetCustomActorIsolatorMap(swiftInterfacePath, customActorTypes),
            () => new Dictionary<string, string>());

        var (actorIsolatedMembers, mainActorIsolatedMembers) = TryParse(
            "actor-isolated members", logger, ref parseFailures,
            () => {
                var members = SwiftInterfaceAccessParser.GetActorIsolatedMembers(swiftInterfacePath, customActorTypes, out var ma);
                return (members, ma);
            },
            () => (new HashSet<string>(), new HashSet<string>()));

        var nonisolatedMembers = TryParse("nonisolated members", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetNonisolatedMembers(swiftInterfacePath),
            () => new HashSet<string>());

        var markerProtocolConformances = TryParse("marker protocol conformances", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetMarkerProtocolConformances(swiftInterfacePath),
            () => new Dictionary<string, List<string>>());

        var (availabilityAnnotations, availabilityAnnotationPositions) = TryParse(
            "availability annotations", logger, ref parseFailures,
            () => {
                var d = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(swiftInterfacePath, out var pos);
                return (d, pos);
            },
            () => (new Dictionary<string, List<AvailabilityAnnotation>>(), new Dictionary<string, SourcePosition>()));

        var defaultParameterValues = TryParse("default parameter values", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetDefaultParameterValues(swiftInterfacePath),
            () => new Dictionary<string, List<string?>>());

        var autoclosureParameters = TryParse("@autoclosure parameters", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetAutoclosureParameters(swiftInterfacePath),
            () => new Dictionary<string, List<bool>>());

        var constLiteralParameters = TryParse("_const parameters", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetConstLiteralParameters(swiftInterfacePath),
            () => new Dictionary<string, List<bool>>());

        var closureParameterAttributes = TryParse("closure parameter attributes", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetClosureParameterAttributes(swiftInterfacePath),
            () => new Dictionary<string, List<List<string>>>());

        var objcRuntimeNames = TryParse("@objc(custom-name) runtime names", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetObjCRuntimeNames(swiftInterfacePath),
            () => new Dictionary<string, string>());

        var subscriptLabels = TryParse("subscript labels", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetSubscriptLabels(swiftInterfacePath),
            () => new Dictionary<string, List<string>>());

        var variadicMembers = TryParse("variadic members", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetVariadicMembers(swiftInterfacePath),
            () => new HashSet<string>());

        var (conventionCProtocols, conventionCProtocolPositions) = TryParse(
            "convention(c) protocols", logger, ref parseFailures,
            () => {
                var s = SwiftInterfaceAccessParser.GetProtocolsWithConventionClosures(swiftInterfacePath, out var pos);
                return (s, pos);
            },
            () => (new HashSet<string>(), new Dictionary<string, SourcePosition>()));

        var hiddenRequirementProtocols = TryParse("hidden-requirement protocols", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetProtocolsWithUnsatisfiedHiddenRequirements(swiftInterfacePath),
            () => new Dictionary<string, HashSet<string>>());

        // Non-fact methods migrated behind the producer abstraction.
        var protocolNames = TryParse("protocol names", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetProtocolNames(swiftInterfacePath),
            () => new HashSet<string>());

        // ExtensionMemberCandidates is the module-context-free flat list. Both the regex
        // path and the SwiftSyntax host produce it from the same direct-extension-members
        // walk so SwiftInterfaceFacts.ResolveForeignExtensions has identical input on both
        // sides; downstream parity is gated on this list, not on the partitioned dict.
        var extensionMemberCandidates = TryParse("extension member candidates", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetExtensionMemberCandidates(swiftInterfacePath),
            () => new List<ExtensionMemberCandidate>());

        // ProtocolExtensionMethods is derived from the same candidate list using the
        // first-dot-stripped name lookup against protocolNames. This deliberately routes
        // through the candidate walk (direct members only) instead of the legacy
        // GetProtocolExtensionMethods so the regex producer parity-matches the host.
        var protocolExtensionMethods = TryParse("protocol extension methods", logger, ref parseFailures,
            () => DeriveProtocolExtensionMethods(extensionMemberCandidates, protocolNames),
            () => new Dictionary<string, List<ProtocolExtensionMethodDecl>>());

        // SDK 0.11.0 R2 — derive private-interface companion path from the public path.
        // Apple's swiftinterface layout co-locates "{arch}.swiftinterface" with
        // "{arch}.private.swiftinterface" inside the same .swiftmodule dir, so suffix
        // replacement is the cheap way to find it. Missing file is normal (frameworks
        // without SPI ship no private interface) and produces an empty set.
        var privateSwiftInterfacePath = DerivePrivateSwiftInterfacePath(swiftInterfacePath);
        var spiOnlyConformances = TryParse("SPI-only conformances", logger, ref parseFailures,
            () => SwiftInterfaceAccessParser.GetSpiOnlyConformances(privateSwiftInterfacePath),
            () => new HashSet<string>());

        if (parseFailures > 0)
            logger.LogWarning("{Count} swiftinterface parsing pass(es) failed and were skipped (regex producer). Bindings will be generated with reduced metadata.", parseFailures);

        var partial = new PartialSwiftInterfaceFacts
        {
            InternalMemberKeys = internalMemberKeys,
            PublicMemberNames = publicMemberNames,
            ParameterNames = parameterNames,
            TypedThrowsErrors = typedThrowsErrors,
            EnumCaseLabels = enumCaseLabels,
            EnumCaseRawValues = enumCaseRawValues,
            PublicTypeNames = publicTypeNames,
            MainActorTypes = mainActorTypes,
            CustomActorTypes = customActorTypes,
            CustomActorIsolatorMap = customActorIsolatorMap,
            ActorIsolatedMembers = actorIsolatedMembers,
            MainActorIsolatedMembers = mainActorIsolatedMembers,
            NonisolatedMembers = nonisolatedMembers,
            MarkerProtocolConformances = markerProtocolConformances,
            AvailabilityAnnotations = availabilityAnnotations,
            DefaultParameterValues = defaultParameterValues,
            AutoclosureParameters = autoclosureParameters,
            ConstLiteralParameters = constLiteralParameters,
            ClosureParameterAttributes = closureParameterAttributes,
            ObjCRuntimeNames = objcRuntimeNames,
            SubscriptLabels = subscriptLabels,
            VariadicMembers = variadicMembers,
            ConventionCProtocols = conventionCProtocols,
            HiddenRequirementProtocols = hiddenRequirementProtocols,
            MainActorTypePositions = mainActorTypePositions,
            AvailabilityAnnotationPositions = availabilityAnnotationPositions,
            ConventionCProtocolPositions = conventionCProtocolPositions,
            ProtocolNames = protocolNames,
            ProtocolExtensionMethods = protocolExtensionMethods,
            ExtensionMemberCandidates = extensionMemberCandidates,
            SpiOnlyConformances = spiOnlyConformances,
        };
        return new ProducerResult(partial, new HashSet<InterfaceFactKind>(InterfaceFactKindHelpers.AllFactKinds));
    }

    /// <summary>
    /// Convert a public swiftinterface path to its private companion path
    /// (<c>foo.swiftinterface</c> → <c>foo.private.swiftinterface</c>).
    /// Returns null when the input does not end in <c>.swiftinterface</c> so callers
    /// can treat absence as "no private interface" rather than constructing a bogus
    /// path and probing for it.
    /// </summary>
    internal static string? DerivePrivateSwiftInterfacePath(string swiftInterfacePath)
    {
        const string suffix = ".swiftinterface";
        if (string.IsNullOrEmpty(swiftInterfacePath) ||
            !swiftInterfacePath.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase))
            return null;

        // Idempotent: if the caller already passed the private path, return it as-is.
        const string privateSuffix = ".private.swiftinterface";
        if (swiftInterfacePath.EndsWith(privateSuffix, System.StringComparison.OrdinalIgnoreCase))
            return swiftInterfacePath;

        return swiftInterfacePath.Substring(0, swiftInterfacePath.Length - suffix.Length) + privateSuffix;
    }

    private static Dictionary<string, List<ProtocolExtensionMethodDecl>> DeriveProtocolExtensionMethods(
        List<ExtensionMemberCandidate> candidates,
        HashSet<string> protocolNames)
    {
        var result = new Dictionary<string, List<ProtocolExtensionMethodDecl>>();
        if (protocolNames.Count == 0)
            return result;
        foreach (var candidate in candidates)
        {
            var qualified = candidate.ExtendedTypeName;
            var firstDotIdx = qualified.IndexOf('.');
            var typePath = firstDotIdx >= 0 ? qualified.Substring(firstDotIdx + 1) : qualified;
            if (!protocolNames.Contains(typePath))
                continue;
            if (!result.TryGetValue(qualified, out var list))
            {
                list = new List<ProtocolExtensionMethodDecl>();
                result[qualified] = list;
            }
            list.Add(SwiftInterfaceFacts.CandidateToDecl(candidate, qualified));
        }
        return result;
    }

    private static T TryParse<T>(string description, ILogger logger, ref int parseFailures, Func<T> action, Func<T> fallback)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogWarning("Swiftinterface parse pass '{Pass}' failed: {Message}", description, ex.Message);
            parseFailures++;
            return fallback();
        }
    }
}
