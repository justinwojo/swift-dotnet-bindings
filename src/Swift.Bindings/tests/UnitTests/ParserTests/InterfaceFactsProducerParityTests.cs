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
/// and asserts the two facts SwiftSyntax covers in Session 1 (MainActorTypes,
/// MainActorTypePositions) come out byte-equal.
/// <para/>
/// SKIP BEHAVIOR: when the SwiftInterfaceParser host binary isn't built, every
/// fact in the test class is skipped instead of failing — `dotnet test` is a no-op
/// in environments without a Swift toolchain. CI runs `nuke compile` first, which
/// produces the binary. Local devs hit `Skip` reasons telling them how to fix.
/// </summary>
public class InterfaceFactsProducerParityTests
{
    /// <summary>Edge cases sourced from <see cref="SourceProvenanceTests"/> and the regex
    /// parser's known-handled list (see SwiftInterfaceAccessParser.cs lines 245-328).
    /// Every case is one the regex parser is documented to handle correctly today, so
    /// any divergence between the two producers is a real regression.</summary>
    public static IEnumerable<object[]> ParityCorpus =>
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

    [SkippableTheory]
    [MemberData(nameof(ParityCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalMainActorFacts(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            // Coverage: SwiftSyntax declares MainActorTypes + MainActorTypePositions in Session 1.
            Assert.Contains(InterfaceFactKind.MainActorTypes, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.MainActorTypePositions, swiftSyntax.CoveredFacts);

            var regexTypes = regex.Facts.MainActorTypes ?? new HashSet<string>();
            var swiftSyntaxTypes = swiftSyntax.Facts.MainActorTypes ?? new HashSet<string>();
            Assert.True(regexTypes.SetEquals(swiftSyntaxTypes),
                $"[{label}] MainActorTypes diverged.\n  regex:        {Join(regexTypes)}\n  swift-syntax: {Join(swiftSyntaxTypes)}");

            var regexPositions = regex.Facts.MainActorTypePositions ?? new Dictionary<string, SourcePosition>();
            var swiftSyntaxPositions = swiftSyntax.Facts.MainActorTypePositions ?? new Dictionary<string, SourcePosition>();
            Assert.Equal(regexPositions.Count, swiftSyntaxPositions.Count);
            foreach (var key in regexPositions.Keys)
            {
                Assert.True(swiftSyntaxPositions.ContainsKey(key),
                    $"[{label}] swift-syntax missing position for '{key}'");
                var r = regexPositions[key];
                var s = swiftSyntaxPositions[key];
                // FilePath byte-equal (input path round-trips), Line/Column 1-based and identical.
                Assert.Equal(r.FilePath, s.FilePath);
                Assert.Equal(r.Line, s.Line);
                Assert.Equal(r.Column, s.Column);
            }
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
    }

    [SkippableFact]
    public void Aggregator_WithSwiftSyntaxThenRegex_RoutesMainActorToSwiftSyntaxRestToRegex()
    {
        // End-to-end seam test: the aggregator must merge per fact. With
        // [SwiftSyntax, Regex], MainActor* come from SwiftSyntax and the other 22 facts
        // (which SwiftSyntax does NOT cover) fall through to Regex.
        var binaryPath = ResolveBinaryOrSkip("AggregatorRouting");
        var swiftInterface =
            "import Swift\n" +
            "\n" +
            "@MainActor\n" +
            "public struct A {\n" +
            "}\n" +
            "\n" +
            "public struct B {\n" +
            "}\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var aggregator = new InterfaceFactsAggregator(new IInterfaceFactsProducer[]
            {
                new SwiftSyntaxInterfaceFactsProducer(binaryPath),
                new RegexInterfaceFactsProducer(),
            });
            var facts = aggregator.Aggregate(path, NullLogger.Instance);

            // SwiftSyntax-covered facts: MainActor only annotates A.
            Assert.Contains("A", facts.MainActorTypes);
            Assert.DoesNotContain("B", facts.MainActorTypes);
            Assert.True(facts.MainActorTypePositions.ContainsKey("A"));

            // Regex-covered facts (SwiftSyntax doesn't cover): PublicTypeNames sees both.
            Assert.Contains("A", facts.PublicTypeNames);
            Assert.Contains("B", facts.PublicTypeNames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string ResolveBinaryOrSkip(string label)
    {
        var path = SwiftSyntaxInterfaceFactsProducer.TryLocateBinary();
        // Xunit.Skip.IfNot from Xunit.SkippableFact: marks the [SkippableFact]/[SkippableTheory]
        // as Skipped (not Failed) when the precondition isn't met. Required because nuke compile
        // doesn't build the host binary on non-Darwin (CompileSwiftInterfaceParser is gated to
        // OperatingSystem.IsMacOS()), and we don't want those test runs to fail when there's
        // no swift-syntax producer to compare against.
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

