// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Best-effort source-provenance pipeline: exercises <see cref="SourcePosition"/>
/// through the regex parser, the <see cref="SwiftInterfaceFacts"/> aggregator,
/// and the <see cref="SkippedItem"/> diagnostic shape that lands in
/// <c>binding-report.json</c>.
/// </summary>
[Collection("ReportCollector")]
public class SourceProvenanceTests
{
    #region SourcePosition formatting

    [Fact]
    public void FormatPrefix_Null_ReturnsEmptyString()
    {
        // Best-effort contract: callers prepend the prefix unconditionally and get an
        // empty string when no position is known, instead of fabricating a fake location.
        Assert.Equal(string.Empty, SourcePosition.FormatPrefix(null));
    }

    [Fact]
    public void FormatPrefix_NonNull_RendersClangStylePrefix()
    {
        var pos = new SourcePosition("/tmp/Mod.swiftinterface", 42, 17);
        Assert.Equal("/tmp/Mod.swiftinterface:42:17: ", SourcePosition.FormatPrefix(pos));
    }

    [Fact]
    public void ToString_Renders_PathLineColumn()
    {
        var pos = new SourcePosition("Mod.swiftinterface", 3, 5);
        Assert.Equal("Mod.swiftinterface:3:5", pos.ToString());
    }

    #endregion

    #region Positive — regex parser supplies positions for 3 fact types

    [Fact]
    public void GetMainActorTypes_EmitsLineAndColumnFromTypeDeclaration()
    {
        // Line 1: header import
        // Line 2: blank
        // Line 3: @MainActor pending annotation
        // Line 4: type declaration — position should point here
        var swiftInterface =
            "import Swift\n" +
            "\n" +
            "@MainActor\n" +
            "public struct Widget {\n" +
            "}\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetMainActorTypes(path, out var positions);

            Assert.Contains("Widget", result);
            Assert.True(positions.TryGetValue("Widget", out var pos),
                "Widget should have a recorded position.");
            Assert.Equal(path, pos.FilePath);
            Assert.Equal(4, pos.Line);
            // "public struct Widget" begins at column 1 (no leading whitespace), and
            // TypeDeclRegex matches the "public" keyword as the first capture site.
            Assert.Equal(1, pos.Column);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_EmitsLineAndColumnAtDeclaration()
    {
        // Line 1: header
        // Line 2: pending @available
        // Line 3: type — position should point here, NOT at the annotation line
        var swiftInterface =
            "import Foundation\n" +
            "@available(iOS 16.0, *)\n" +
            "public struct Gadget {\n" +
            "}\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path, out var positions);

            Assert.True(result.ContainsKey("Gadget"));
            Assert.True(positions.TryGetValue("Gadget", out var pos),
                "Gadget should have a recorded position.");
            Assert.Equal(path, pos.FilePath);
            Assert.Equal(3, pos.Line);
            Assert.Equal(1, pos.Column);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_InlineAnnotation_ColumnSkipsPastAtToken()
    {
        // `@available(iOS 16, *) public struct Inline {` — the column should point at
        // `public` (after the inline @available), not at `@`. Matches the @MainActor /
        // @convention(c) parsers, which already use regex match offsets to advance past
        // leading annotations.
        var swiftInterface =
            "import Foundation\n" +
            "@available(iOS 16.0, *) public struct Inline {\n" +
            "}\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path, out var positions);

            Assert.True(result.ContainsKey("Inline"));
            Assert.True(positions.TryGetValue("Inline", out var pos),
                "Inline should have a recorded position.");
            Assert.Equal(2, pos.Line);
            // "@available(iOS 16.0, *) " is 24 chars; "public" starts at column 25.
            Assert.Equal(25, pos.Column);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_QualifiedInlineAttribute_ColumnSkipsPastDottedAt()
    {
        // Swiftinterface attributes can be dotted (`@_Concurrency.MainActor`,
        // `@Module.Actor`). The annotation skipper must walk through the dot-separated
        // identifier components, not stop at the first `.`.
        var swiftInterface =
            "import Foundation\n" +
            "@available(iOS 16.0, *) @_Concurrency.MainActor public struct Stacked {\n" +
            "}\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path, out var positions);

            Assert.True(result.ContainsKey("Stacked"));
            Assert.True(positions.TryGetValue("Stacked", out var pos),
                "Stacked should have a recorded position.");
            Assert.Equal(2, pos.Line);
            // "@available(iOS 16.0, *) @_Concurrency.MainActor " is 48 chars; "public" at column 49.
            Assert.Equal(49, pos.Column);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithConventionClosures_EmitsPositionAtProtocolHeader()
    {
        // Line 1: header
        // Line 2: blank
        // Line 3: protocol declaration — position target
        // Line 4: convention(c) param triggers detection
        // Line 5: closing brace
        var swiftInterface =
            "import Swift\n" +
            "\n" +
            "public protocol Callback {\n" +
            "  func register(_ cb: @convention(c) (Swift.Int) -> Swift.Void)\n" +
            "}\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithConventionClosures(path, out var positions);

            Assert.Contains("Callback", result);
            Assert.True(positions.TryGetValue("Callback", out var pos),
                "Callback should have a recorded position.");
            Assert.Equal(path, pos.FilePath);
            Assert.Equal(3, pos.Line);
            Assert.Equal(1, pos.Column);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Parser_RecordsLeadingWhitespaceColumn()
    {
        // Indented type — column must reflect the leading whitespace, not start at 1.
        // "  public struct Inner" — "public" begins at column 3.
        var swiftInterface =
            "public struct Outer {\n" +
            "  @MainActor\n" +
            "  public struct Inner {\n" +
            "  }\n" +
            "}\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetMainActorTypes(path, out var positions);

            // Outer.Inner is the qualified path for the nested @MainActor type.
            Assert.True(positions.TryGetValue("Outer.Inner", out var pos),
                "Outer.Inner should have a recorded position.");
            Assert.Equal(3, pos.Line);
            Assert.Equal(3, pos.Column);
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region Negative — graceful degradation

    [Fact]
    public void GetMainActorTypes_AbiOnlyFact_HasNoPosition()
    {
        // SwiftInterfaceFacts assembled directly (as if from an ABI-JSON-only path with
        // no swiftinterface input): the position dictionary stays empty, and
        // TryGetPosition returns null rather than fabricating a location.
        var facts = SwiftInterfaceFacts.Empty with
        {
            MainActorTypes = new HashSet<string> { "AbiOnlyType" },
        };

        Assert.Null(facts.TryGetPosition("AbiOnlyType"));
        Assert.Empty(facts.MainActorTypePositions);
    }

    [Fact]
    public void Parser_NoSwiftInterfaceFile_EmitsEmptyPositions()
    {
        // Dependency module with no swiftinterface: the parser returns empty results AND
        // an empty position dictionary — no fabricated entries.
        var bogusPath = "/tmp/this-file-does-not-exist-" + System.Guid.NewGuid() + ".swiftinterface";

        SwiftInterfaceAccessParser.GetMainActorTypes(bogusPath, out var mainActorPositions);
        SwiftInterfaceAccessParser.GetAvailabilityAnnotations(bogusPath, out var availabilityPositions);
        SwiftInterfaceAccessParser.GetProtocolsWithConventionClosures(bogusPath, out var conventionCPositions);

        Assert.Empty(mainActorPositions);
        Assert.Empty(availabilityPositions);
        Assert.Empty(conventionCPositions);
    }

    [Fact]
    public void TryGetPosition_UnknownKey_ReturnsNull()
    {
        var pos = new SourcePosition("/tmp/x.swiftinterface", 10, 5);
        var facts = SwiftInterfaceFacts.Empty with
        {
            MainActorTypes = new HashSet<string> { "Known" },
            MainActorTypePositions = new Dictionary<string, SourcePosition> { ["Known"] = pos },
        };

        Assert.Equal(pos, facts.TryGetPosition("Known"));
        Assert.Null(facts.TryGetPosition("Unknown"));
    }

    #endregion

    #region SkippedItem provenance round-trip

    [Fact]
    public void SkippedItem_PositionField_SerializesAsStructuredJson()
    {
        // Provenance is a structured field on binding-report.json — not buried in Details.
        var item = new SkippedItem
        {
            Kind = BindingItemKind.Type,
            Name = "Gadget",
            Reason = SkipReason.UnsupportedType,
            Position = new SourcePosition("Mod.swiftinterface", 12, 4),
        };

        var json = JsonConvert.SerializeObject(item, new JsonSerializerSettings
        {
            Converters = new List<JsonConverter> { new StringEnumConverter() },
        });

        Assert.Contains("\"Position\":", json);
        Assert.Contains("\"FilePath\":\"Mod.swiftinterface\"", json);
        Assert.Contains("\"Line\":12", json);
        Assert.Contains("\"Column\":4", json);
    }

    [Fact]
    public void SkippedItem_NullPosition_OmittedOrNullInJson()
    {
        // ABI-only diagnostics emit Position == null; the JSON shape stays valid.
        var item = new SkippedItem
        {
            Kind = BindingItemKind.Method,
            Name = "abiOnly()",
            Reason = SkipReason.UnsupportedSignature,
        };

        var json = JsonConvert.SerializeObject(item);
        // Either "Position":null or no Position field at all is acceptable —
        // both signal "no source position known" to consumers. What we DON'T
        // want is a fabricated position.
        Assert.DoesNotContain("\"FilePath\":", json);
    }

    [Fact]
    public void RecordTypeSkipped_WithPosition_AttachesPositionToSkippedItem()
    {
        // End-to-end: a skip site that has a SourcePosition in hand passes it through
        // ReportCollector and lands in BindingReport.SkippedItems with the position
        // intact for binding-report.json serialization.
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        var typeDecl = new StructDecl
        {
            Name = "Skipped",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Skipped"),
            MangledName = "$s10TestModule7SkippedV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = false,
            MetadataAccessor = "$s10TestModule7SkippedVMa",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        try
        {
            var pos = new SourcePosition("Test.swiftinterface", 7, 8);
            ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.UnsupportedType, position: pos);

            var report = ReportCollector.Complete();
            Assert.NotNull(report);
            var item = Assert.Single(report!.SkippedItems);
            Assert.Equal("Skipped", item.Name);
            Assert.NotNull(item.Position);
            Assert.Equal(pos, item.Position);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    #endregion

    private static string WriteTempFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }
}
