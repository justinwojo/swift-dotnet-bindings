// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BindingsGeneration.Producers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Parity gate for the M2 SwiftSyntax migration. Drives a representative corpus of
/// .swiftinterface inputs through the regex producer and the SwiftSyntax producer
/// and asserts every fact SwiftSyntax covers comes out byte-equal.
/// <para/>
/// Session 2 covers MainActor* + the actor isolation cluster (5 facts) +
/// availability annotations (2 facts) + typed throws — 10 facts total.
/// <para/>
/// SKIP BEHAVIOR: when the SwiftInterfaceParser host binary isn't built, every
/// fact in the test class is skipped instead of failing — `dotnet test` is a no-op
/// in environments without a Swift toolchain. CI runs `nuke compile` first, which
/// produces the binary. Local devs hit `Skip` reasons telling them how to fix.
/// </summary>
public class InterfaceFactsProducerParityTests
{
    /// <summary>
    /// Corpus for the MainActor parity gate inherited from M2 Session 1. Every entry
    /// is one the regex parser is documented to handle correctly today, so any
    /// divergence between the two producers is a real regression.
    /// </summary>
    public static IEnumerable<object[]> MainActorCorpus =>
        new[]
        {
            new object[] { "BasicMainActor",
                "import Swift\n" +
                "\n" +
                "@MainActor\n" +
                "public struct Widget {\n" +
                "}\n" },
            new object[] { "QualifiedMainActor",
                "import Swift\n" +
                "@_Concurrency.MainActor\n" +
                "public class Notifier {\n" +
                "}\n" },
            new object[] { "InlineAttribute",
                "import Swift\n" +
                "@MainActor public struct Inline {\n" +
                "}\n" },
            new object[] { "NestedType",
                "public struct Outer {\n" +
                "  @MainActor\n" +
                "  public struct Inner {\n" +
                "  }\n" +
                "}\n" },
            new object[] { "InternalAccessModifier",
                "@MainActor\n" +
                "internal struct Hidden {\n" +
                "}\n" },
            new object[] { "OpenAccessModifier",
                "@MainActor\n" +
                "open class Base {\n" +
                "}\n" },
            new object[] { "ProtocolDecl",
                "@MainActor\n" +
                "public protocol P {\n" +
                "}\n" },
            new object[] { "EnumDecl",
                "@MainActor\n" +
                "public enum E {\n" +
                "}\n" },
            new object[] { "MainActorOnActorIsSuppressed",
                // Regex parser's ActorDeclRegex matches `public|open actor`, so @MainActor
                // on a public actor is suppressed.
                "@MainActor\n" +
                "public actor Suppressed {\n" +
                "}\n" },
            new object[] { "MainActorOnInternalActorIsEmitted",
                // ActorDeclRegex requires public/open — internal actor falls through and IS
                // emitted by the regex parser. SwiftSyntax must match this quirk exactly.
                "@MainActor\n" +
                "internal actor InternallyHidden {\n" +
                "}\n" },
            new object[] { "MultipleTypes",
                "@MainActor\n" +
                "public struct First {}\n" +
                "@MainActor\n" +
                "public struct Second {}\n" +
                "public struct Third {}\n" },
            new object[] { "NoMainActorAtAll",
                "import Swift\n" +
                "public struct Plain {\n" +
                "}\n" },
            new object[] { "ExtensionScopeNotEmitted",
                // Extensions push scope but never emit. Regex parser doesn't add `extension`
                // bodies' types as MainActorTypes, even when @MainActor decorates them.
                "public struct Outer {}\n" +
                "@MainActor\n" +
                "extension Mod.Outer {\n" +
                "  public struct AppendedNested {}\n" +
                "}\n" },
            new object[] { "FinalClass",
                "@MainActor\n" +
                "public final class Locked {\n" +
                "}\n" },
            new object[] { "IndentedNested",
                "public struct Outer {\n" +
                "    @MainActor\n" +
                "    public struct DeepInner {\n" +
                "    }\n" +
                "}\n" },
        };

    /// <summary>
    /// Actor isolation cluster corpus. Exercises ActorIsolatedMembers,
    /// MainActorIsolatedMembers, NonisolatedMembers, CustomActorTypes, and
    /// CustomActorIsolatorMap. Each entry covers one or more drift-prone shapes.
    /// </summary>
    public static IEnumerable<object[]> ActorIsolationCorpus =>
        new[]
        {
            new object[] { "MemberLevelMainActor",
                "public class Mixed {\n" +
                "  @MainActor\n" +
                "  public func uiOnly()\n" +
                "  public func bgOnly()\n" +
                "}\n" },
            new object[] { "InlineQualifiedMainActor",
                "public class Mixed {\n" +
                "  @_Concurrency.MainActor public func uiInline()\n" +
                "}\n" },
            new object[] { "NonisolatedFunc",
                "public class C {\n" +
                "  nonisolated public func neutral()\n" +
                "}\n" },
            new object[] { "NonisolatedVar",
                "public class C {\n" +
                "  nonisolated public var name: Swift.String\n" +
                "}\n" },
            new object[] { "TopLevelMainActorFunc",
                "@MainActor public func tlMain()\n" },
            new object[] { "BareProtocolMainActor",
                // Bare protocol member: the regex's BareFuncRegex catches it inside
                // a protocol body. SwiftSyntax must match.
                "public protocol P {\n" +
                "  @MainActor func bare()\n" +
                "}\n" },
            new object[] { "PublicActorKeywordType",
                "public actor Pipeline {\n" +
                "}\n" },
            new object[] { "InternalActorKeywordIsNotCustom",
                // ActorDeclRegex requires public/open — internal actors are NOT in
                // CustomActorTypes per regex semantics. Mirror.
                "internal actor Hidden {\n" +
                "}\n" },
            new object[] { "CustomActorIsolation_BareName",
                // Local-actor regex matches bare @ActorName when ActorName is in the
                // file's customActorTypeNames set.
                "public actor PipelineActor {\n" +
                "}\n" +
                "@PipelineActor\n" +
                "public class Pipeline {\n" +
                "}\n" },
            new object[] { "CustomActorIsolation_QualifiedName",
                // Qualified module-prefixed annotation — local-actor regex's
                // (?:\\w+\\.)? handles one prefix. Same actor declared in this file.
                "public actor PipelineActor {\n" +
                "}\n" +
                "@MyMod.PipelineActor\n" +
                "public class Pipeline {\n" +
                "}\n" },
            new object[] { "ImportedCustomActor",
                // No local actor decl. ImportedCustomActorAnnotationRegex matches
                // `@<Module>.<Name>Actor` heuristically. MainActor excluded.
                "@SomeMod.RemoteActor\n" +
                "public class RemoteThing {\n" +
                "}\n" },
            new object[] { "MainActorOnTypeDoesNotEmitMembers",
                // Regex doesn't propagate type-level @MainActor to its members.
                // ActorIsolatedMembers stays empty for `bgOnly` even though `Pipeline`
                // is in MainActorTypes.
                "@MainActor\n" +
                "public class Pipeline {\n" +
                "  public func bgOnly()\n" +
                "}\n" },
        };

    /// <summary>
    /// Availability annotations corpus.
    /// </summary>
    public static IEnumerable<object[]> AvailabilityCorpus =>
        new[]
        {
            new object[] { "TypeLevelShorthand",
                "@available(iOS 16.0, macOS 13, *)\n" +
                "public struct Modern {\n" +
                "}\n" },
            new object[] { "TypeLevelDeprecated",
                "@available(*, deprecated, message: \"use Modern\")\n" +
                "public struct Old {\n" +
                "}\n" },
            new object[] { "MemberLevelLifecycle",
                "public struct Holder {\n" +
                "  @available(iOS, introduced: 10, deprecated: 12)\n" +
                "  public func legacy()\n" +
                "}\n" },
            new object[] { "MemberInline",
                "public struct Holder {\n" +
                "  @available(*, unavailable) public var blocked: Swift.Int\n" +
                "}\n" },
            new object[] { "FreeFunctionAvailability",
                "@available(iOS 17.0, *)\n" +
                "public func recent()\n" },
            new object[] { "MessageWithEmbeddedParens",
                "@available(*, deprecated, message: \"Use init(config:) instead\")\n" +
                "public struct Old {\n" +
                "}\n" },
            new object[] { "EnumCase_AvailabilityOnSingle",
                "public enum Status {\n" +
                "  @available(iOS 16.0, *)\n" +
                "  case fresh\n" +
                "}\n" },
            new object[] { "ExtensionScopeInherited",
                // @available on the extension propagates to every public member.
                "public struct T {}\n" +
                "@available(iOS 17.0, *)\n" +
                "extension Mod.T {\n" +
                "  public func added()\n" +
                "}\n" },
        };

    /// <summary>
    /// Typed throws corpus.
    /// </summary>
    public static IEnumerable<object[]> TypedThrowsCorpus =>
        new[]
        {
            new object[] { "FreeFunctionTypedThrows",
                "public func parseNumber(_ input: Swift.String) throws(SwiftBindingsTestLib.ParseError) -> Swift.Int32\n" },
            new object[] { "InstanceMethodTypedThrows",
                "public class Parser {\n" +
                "  public func parse(_ s: Swift.String) throws(MyMod.E) -> Swift.Int\n" +
                "}\n" },
            new object[] { "InitTypedThrows",
                "public struct Reader {\n" +
                "  public init(buffer: Swift.String) throws(MyMod.IOError)\n" +
                "}\n" },
            new object[] { "ExtensionMethodTypedThrows",
                // Extension keys use the LAST-component (simple) name, distinct from
                // ActorIsolatedMembers' first-stripped path. Mirror exactly.
                "extension SomeMod.Outer.Target {\n" +
                "  public func emit() throws(MyMod.E)\n" +
                "}\n" },
            new object[] { "UntypedThrowsExcluded",
                // Plain `throws` does NOT contribute to TypedThrowsErrors.
                "public func unsafe() throws -> Swift.Int\n" },
            new object[] { "NonThrowingExcluded",
                "public func plain() -> Swift.Int\n" },
        };

    [SkippableTheory]
    [MemberData(nameof(MainActorCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalMainActorFacts(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            // Coverage: SwiftSyntax declares MainActorTypes + MainActorTypePositions in Session 1+.
            Assert.Contains(InterfaceFactKind.MainActorTypes, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.MainActorTypePositions, swiftSyntax.CoveredFacts);

            AssertSetParity(label, "MainActorTypes", regex.Facts.MainActorTypes, swiftSyntax.Facts.MainActorTypes);
            AssertPositionsParity(label, "MainActorTypePositions",
                regex.Facts.MainActorTypePositions, swiftSyntax.Facts.MainActorTypePositions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(ActorIsolationCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalActorIsolationFacts(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.ActorIsolatedMembers, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.MainActorIsolatedMembers, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.NonisolatedMembers, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.CustomActorTypes, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.CustomActorIsolatorMap, swiftSyntax.CoveredFacts);

            AssertSetParity(label, "ActorIsolatedMembers",
                regex.Facts.ActorIsolatedMembers, swiftSyntax.Facts.ActorIsolatedMembers);
            AssertSetParity(label, "MainActorIsolatedMembers",
                regex.Facts.MainActorIsolatedMembers, swiftSyntax.Facts.MainActorIsolatedMembers);
            AssertSetParity(label, "NonisolatedMembers",
                regex.Facts.NonisolatedMembers, swiftSyntax.Facts.NonisolatedMembers);
            AssertSetParity(label, "CustomActorTypes",
                regex.Facts.CustomActorTypes, swiftSyntax.Facts.CustomActorTypes);
            AssertStringDictParity(label, "CustomActorIsolatorMap",
                regex.Facts.CustomActorIsolatorMap, swiftSyntax.Facts.CustomActorIsolatorMap);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(AvailabilityCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalAvailabilityFacts(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.AvailabilityAnnotations, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.AvailabilityAnnotationPositions, swiftSyntax.CoveredFacts);

            AssertAvailabilityParity(label,
                regex.Facts.AvailabilityAnnotations, swiftSyntax.Facts.AvailabilityAnnotations);
            AssertPositionsParity(label, "AvailabilityAnnotationPositions",
                regex.Facts.AvailabilityAnnotationPositions, swiftSyntax.Facts.AvailabilityAnnotationPositions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(TypedThrowsCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalTypedThrowsFacts(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.TypedThrowsErrors, swiftSyntax.CoveredFacts);

            AssertStringDictParity(label, "TypedThrowsErrors",
                regex.Facts.TypedThrowsErrors, swiftSyntax.Facts.TypedThrowsErrors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public void SwiftSyntaxProducer_NonexistentInputFile_ReturnsEmptyAndZeroCoverage()
    {
        var binaryPath = ResolveBinaryOrSkip("NoFileGuard");
        var bogus = "/tmp/nonexistent-" + Guid.NewGuid() + ".swiftinterface";
        var result = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(bogus, NullLogger.Instance);
        Assert.Empty(result.CoveredFacts);
        Assert.Null(result.Facts.MainActorTypes);
        Assert.Null(result.Facts.MainActorTypePositions);
        Assert.Null(result.Facts.ActorIsolatedMembers);
        Assert.Null(result.Facts.AvailabilityAnnotations);
        Assert.Null(result.Facts.TypedThrowsErrors);
    }

    [SkippableFact]
    public void Aggregator_WithSwiftSyntaxThenRegex_RoutesMigratedFactsToSwiftSyntax()
    {
        // End-to-end seam test: the aggregator must merge per fact. With
        // [SwiftSyntax, Regex], all facts SwiftSyntax declares come from SwiftSyntax;
        // facts SwiftSyntax does NOT cover (e.g., PublicTypeNames) fall through to Regex.
        var binaryPath = ResolveBinaryOrSkip("AggregatorRouting");
        var swiftInterface =
            "import Swift\n" +
            "\n" +
            "@MainActor\n" +
            "public struct A {\n" +
            "}\n" +
            "\n" +
            "public struct B {\n" +
            "}\n" +
            "\n" +
            "public func parse(_ s: Swift.String) throws(MyMod.E) -> Swift.Int\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var aggregator = new InterfaceFactsAggregator(new IInterfaceFactsProducer[]
            {
                new SwiftSyntaxInterfaceFactsProducer(binaryPath),
                new RegexInterfaceFactsProducer(),
            });
            var facts = aggregator.Aggregate(path, NullLogger.Instance);

            // SwiftSyntax-covered facts.
            Assert.Contains("A", facts.MainActorTypes);
            Assert.DoesNotContain("B", facts.MainActorTypes);
            Assert.True(facts.MainActorTypePositions.ContainsKey("A"));
            Assert.True(facts.TypedThrowsErrors.ContainsKey("parse(_:)"));
            Assert.Equal("MyMod.E", facts.TypedThrowsErrors["parse(_:)"]);

            // Regex-covered facts (SwiftSyntax doesn't cover): PublicTypeNames sees both.
            Assert.Contains("A", facts.PublicTypeNames);
            Assert.Contains("B", facts.PublicTypeNames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertSetParity(
        string label, string factName,
        HashSet<string>? regexSet, HashSet<string>? swiftSyntaxSet)
    {
        Assert.NotNull(regexSet);
        Assert.NotNull(swiftSyntaxSet);
        Assert.True(regexSet!.SetEquals(swiftSyntaxSet!),
            $"[{label}] {factName} diverged.\n  regex:        {Join(regexSet)}\n  swift-syntax: {Join(swiftSyntaxSet)}");
    }

    private static void AssertStringDictParity(
        string label, string factName,
        Dictionary<string, string>? regexDict, Dictionary<string, string>? swiftSyntaxDict)
    {
        Assert.NotNull(regexDict);
        Assert.NotNull(swiftSyntaxDict);
        Assert.True(regexDict!.Count == swiftSyntaxDict!.Count,
            $"[{label}] {factName} count diverged. regex={regexDict.Count} swift-syntax={swiftSyntaxDict.Count}");
        foreach (var key in regexDict.Keys)
        {
            Assert.True(swiftSyntaxDict.ContainsKey(key),
                $"[{label}] {factName}: swift-syntax missing key '{key}'");
            Assert.True(regexDict[key] == swiftSyntaxDict[key],
                $"[{label}] {factName}['{key}'] diverged. regex='{regexDict[key]}' swift-syntax='{swiftSyntaxDict[key]}'");
        }
    }

    private static void AssertPositionsParity(
        string label, string factName,
        Dictionary<string, SourcePosition>? regexPositions,
        Dictionary<string, SourcePosition>? swiftSyntaxPositions)
    {
        Assert.NotNull(regexPositions);
        Assert.NotNull(swiftSyntaxPositions);
        Assert.True(regexPositions!.Count == swiftSyntaxPositions!.Count,
            $"[{label}] {factName} count diverged. regex={regexPositions.Count} swift-syntax={swiftSyntaxPositions.Count}\n  regex keys: {Join(regexPositions.Keys)}\n  swift keys: {Join(swiftSyntaxPositions.Keys)}");
        foreach (var key in regexPositions.Keys)
        {
            Assert.True(swiftSyntaxPositions.ContainsKey(key),
                $"[{label}] {factName}: swift-syntax missing position for '{key}'");
            var r = regexPositions[key];
            var s = swiftSyntaxPositions[key];
            Assert.True(r.FilePath == s.FilePath,
                $"[{label}] {factName}['{key}'] FilePath diverged.");
            Assert.True(r.Line == s.Line,
                $"[{label}] {factName}['{key}'] Line diverged. regex={r.Line} swift-syntax={s.Line}");
            Assert.True(r.Column == s.Column,
                $"[{label}] {factName}['{key}'] Column diverged. regex={r.Column} swift-syntax={s.Column}");
        }
    }

    private static void AssertAvailabilityParity(
        string label,
        Dictionary<string, List<AvailabilityAnnotation>>? regexDict,
        Dictionary<string, List<AvailabilityAnnotation>>? swiftSyntaxDict)
    {
        Assert.NotNull(regexDict);
        Assert.NotNull(swiftSyntaxDict);
        Assert.True(regexDict!.Count == swiftSyntaxDict!.Count,
            $"[{label}] AvailabilityAnnotations count diverged. regex={regexDict.Count} swift-syntax={swiftSyntaxDict.Count}\n  regex keys: {Join(regexDict.Keys)}\n  swift keys: {Join(swiftSyntaxDict.Keys)}");
        foreach (var key in regexDict.Keys)
        {
            Assert.True(swiftSyntaxDict.ContainsKey(key),
                $"[{label}] AvailabilityAnnotations: swift-syntax missing key '{key}'");
            var r = regexDict[key];
            var s = swiftSyntaxDict[key];
            Assert.True(r.Count == s.Count,
                $"[{label}] AvailabilityAnnotations['{key}'] count diverged. regex={r.Count} swift-syntax={s.Count}");
            for (int i = 0; i < r.Count; i++)
            {
                Assert.True(r[i] == s[i],
                    $"[{label}] AvailabilityAnnotations['{key}'][{i}] diverged.\n  regex:        {r[i]}\n  swift-syntax: {s[i]}");
            }
        }
    }

    private static string ResolveBinaryOrSkip(string label)
    {
        var path = SwiftSyntaxInterfaceFactsProducer.TryLocateBinary();
        // Xunit.Skip.IfNot from Xunit.SkippableFact: marks the [SkippableFact]/[SkippableTheory]
        // as Skipped (not Failed) when the precondition isn't met.
        Xunit.Skip.IfNot(path is not null && File.Exists(path),
            $"[{label}] SwiftInterfaceParser binary not found. Run `nuke compile` " +
            "(or set SWIFT_INTERFACE_PARSER_PATH) and re-run tests.");
        return path!;
    }

    private static string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"InterfaceFactsParity-{Guid.NewGuid()}.swiftinterface");
        File.WriteAllText(path, content);
        return path;
    }

    private static string Join<T>(IEnumerable<T> items) => "[" + string.Join(", ", items.Select(x => x?.ToString() ?? "<null>")) + "]";
}
