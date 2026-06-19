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
/// formatting, the <see cref="SwiftInterfaceFacts"/> position lookup, and the
/// <see cref="SkippedItem"/> diagnostic shape that lands in <c>binding-report.json</c>.
/// The producer-level "positions are emitted for MainActor / availability / convention(c)"
/// coverage lives in <c>SwiftSyntaxInterfaceFactsProducerTests</c>.
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
}
