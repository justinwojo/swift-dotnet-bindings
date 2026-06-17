// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 45 (ABI-JSON ingestion contract): the parser's single ingestion chokepoint now
/// (a) gates the swift-api-digester <c>json_format_version</c> loudly — present-and-matching is an
/// informational input-resolution decision, absence or a mismatch is both warned (SWIFTBIND033) and
/// recorded as an <see cref="InputResolutionCategory.AbiJson"/> degradation so <c>--strict-inputs</c>
/// fails closed instead of binding against an uncalibrated input shape — and (b) treats the dispatch
/// switch as an allowlist: an unrecognized node kind is censused by kind, warned (SWIFTBIND034), and
/// recorded as an AbiJson degradation rather than silently dropped, while <c>AssociatedType</c> and
/// <c>OperatorDecl</c> are recognized-and-skipped (bound elsewhere / not a bindable member) so they
/// never pollute the unknown census or the dropped-with-error channel.
///
/// These assertions target the isolated channels — the injected logger and the
/// <see cref="ParseReconciliation"/> returned from <c>ParseModule</c> — plus the per-thread
/// <see cref="InputResolutionReport"/>, reset at the top of each test.
/// </summary>
public class AbiIngestionContractTests
{
    private const int ExpectedVersion = 8; // mirrors SwiftABIParser.ExpectedAbiFormatVersion (internal)

    // ---- json_format_version gate (SWIFTBIND033) ----

    [Fact]
    public void ParseModule_NoJsonFormatVersion_WarnsSwiftbind033AndRecordsAbiJsonDegradation()
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        using var fixture = CreateParser(jsonFormatVersion: null, logger, CreateNode("Import", moduleName: "TestModule"));

        fixture.Parser.ParseModule();

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND033"));
        Assert.Contains(
            InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.AbiJson
                 && d.Severity == InputResolutionSeverity.Degradation
                 && d.Detail.Contains("no json_format_version"));
    }

    [Fact]
    public void ParseModule_MismatchedJsonFormatVersion_WarnsSwiftbind033AndRecordsAbiJsonDegradation()
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion + 1, logger, CreateNode("Import", moduleName: "TestModule"));

        fixture.Parser.ParseModule();

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND033"));
        Assert.Contains(
            InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.AbiJson
                 && d.Severity == InputResolutionSeverity.Degradation
                 && d.Detail.Contains($"json_format_version {ExpectedVersion + 1}"));
    }

    [Fact]
    public void ParseModule_MatchingJsonFormatVersion_RecordsAbiJsonInfo_NoWarningNoDegradation()
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, CreateNode("Import", moduleName: "TestModule"));

        fixture.Parser.ParseModule();

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("SWIFTBIND033"));
        Assert.Contains(
            InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.AbiJson && d.Severity == InputResolutionSeverity.Info);
        Assert.DoesNotContain(
            InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.AbiJson && d.Severity == InputResolutionSeverity.Degradation);
    }

    // ---- unknown-kind census (SWIFTBIND034) ----

    [Fact]
    public void ParseModule_UnknownNodeKind_WarnsSwiftbind034_CensusCountsIt_RecordsDegradation()
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        // A kind outside the dispatch allowlist (not TypeDecl/Function/Constructor/Var/Subscript/
        // Import/AssociatedType/OperatorDecl). Synthetic so it can never become a real allowlist entry.
        using var fixture = CreateParser(
            jsonFormatVersion: ExpectedVersion, logger,
            CreateNode("__UnmodeledFutureKind", name: "ghost", moduleName: "TestModule"));

        var result = fixture.Parser.ParseModule();

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND034"));
        Assert.NotNull(result.Reconciliation.UnknownNodeKinds);
        Assert.Equal(1, result.Reconciliation.UnknownNodeKinds!["__UnmodeledFutureKind"]);
        Assert.True(result.Reconciliation.DroppedWithError >= 1);
        Assert.Contains(
            InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.AbiJson
                 && d.Severity == InputResolutionSeverity.Degradation
                 && d.Detail.Contains("__UnmodeledFutureKind"));
    }

    // ---- recognized-and-skipped kinds (Finding 45: AssociatedType / OperatorDecl) ----

    [Theory]
    [InlineData("AssociatedType")] // consumed structurally in CreateProtocolDecl, not lost
    [InlineData("OperatorDecl")]   // fixity decl; the backing Function arrives separately
    public void ParseModule_RecognizedNonBindableKind_SkippedNotDroppedNorCensused(string kind)
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        using var fixture = CreateParser(
            jsonFormatVersion: ExpectedVersion, logger,
            CreateNode(kind, name: "member", moduleName: "TestModule"));

        var result = fixture.Parser.ParseModule();

        // Recognized: no unknown-kind warning, not in the census, not dropped-with-error.
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("SWIFTBIND034"));
        Assert.Null(result.Reconciliation.UnknownNodeKinds);
        Assert.Equal(0, result.Reconciliation.DroppedWithError);
        // It is a deliberate skip, not an emission.
        Assert.Equal(1, result.Reconciliation.SkippedWithReason);
        Assert.DoesNotContain(
            InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.AbiJson && d.Severity == InputResolutionSeverity.Degradation);
    }

    /// <summary>
    /// Finding 45's substantive half: an associated type is not merely tolerated as a recognized
    /// top-level skip — when it appears as a protocol member (the in-the-wild shape, Kind ==
    /// "AssociatedType", DeclKind == "AssociatedType"), the parser consumes it structurally into the
    /// protocol's <see cref="ProtocolDecl.AssociatedTypes"/>. This is the truth the back half no longer
    /// has to re-derive from generic-signature text. Deleting that structural-consumption code would
    /// leave the recognized-skip theory above green but fail this assertion.
    /// </summary>
    [Fact]
    public void ParseModule_ProtocolWithAssociatedTypeChild_ConsumesItStructurally()
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        var associated = CreateNode("AssociatedType", declKind: "AssociatedType", name: "Element", moduleName: "TestModule");
        var protocolNode = CreateNode(
            "TypeDecl", declKind: "Protocol", name: "Container", moduleName: "TestModule",
            mangledName: "$s10TestModule9ContainerP", children: new[] { associated });
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, protocolNode);

        var result = fixture.Parser.ParseModule();

        var protocol = Assert.Single(result.ModuleDecl.Protocols);
        var assoc = Assert.Single(protocol.AssociatedTypes);
        Assert.Equal("Element", assoc.Name);
        // Structural consumption is not a dropped-with-error event for the associated-type member.
        Assert.Equal(0, result.Reconciliation.DroppedWithError);
    }

    // ---- missing load-bearing field: record drop (SWIFTBIND046) ----

    /// <summary>
    /// Finding 45 (no silent loss of records): a bindable type declaration whose load-bearing mangled
    /// name is absent is not a benign skip — the binder cannot produce a record for it. It is dropped
    /// LOUDLY: warned (SWIFTBIND046), censused as a DroppedWithError record loss in the reconciliation
    /// (NOT folded into SkippedWithReason next to imports), and recorded as an AbiJson degradation so
    /// <c>--strict-inputs</c> fails closed rather than binding a module that quietly lost a type. This
    /// is the same fail-closed channel the SWIFTBIND034 unknown-kind gate uses.
    /// </summary>
    [Theory]
    [InlineData("Struct")]
    [InlineData("Enum")]
    [InlineData("Class")]
    [InlineData("Protocol")]
    public void ParseModule_BindableTypeMissingMangledName_DropsLoudly_Swiftbind046_AndDegrades(string bindableKind)
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        // Every bindable type kind in the gate must drop loudly — a theory so dropping any one kind
        // from the SwiftABIParser allowlist would turn a case red instead of leaving the test green.
        var ghostType = CreateNode(
            "TypeDecl", declKind: bindableKind, name: "Ghost", moduleName: "TestModule", mangledName: "");
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, ghostType);

        var result = fixture.Parser.ParseModule();

        // Not bound...
        Assert.DoesNotContain(result.ModuleDecl.Types, t => t.Name == "Ghost");
        Assert.DoesNotContain(result.ModuleDecl.Protocols, p => p.Name == "Ghost");
        // ...warned with the dedicated diagnostic...
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND046"));
        // ...counted as a record loss, not a deliberate skip...
        Assert.Equal(1, result.Reconciliation.DroppedWithError);
        Assert.Equal(0, result.Reconciliation.SkippedWithReason);
        Assert.True(result.Reconciliation.IsBalanced);
        // ...and recorded as a fail-closed AbiJson degradation naming the lost type.
        Assert.Contains(
            InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.AbiJson
                 && d.Severity == InputResolutionSeverity.Degradation
                 && d.Detail.Contains("Ghost")
                 && d.Detail.Contains("no mangled name"));
    }

    /// <summary>
    /// Finding 45's actual motivating shape: the swift-api-digester OMITS the field entirely rather
    /// than emitting an empty string. Newtonsoft 13.0.3 sets members by reflection and does NOT honor
    /// C# 11 <c>required</c>, so an absent <c>mangledName</c> deserializes to null (not a parse error) —
    /// the silent gap this change closes. This test strips the property from the serialized document so
    /// the field is genuinely absent, then asserts the drop still fires (the gate's <c>IsNullOrEmpty</c>
    /// must cover null, not just empty). If Newtonsoft ever started enforcing <c>required</c>, the
    /// parser would throw on load and this test would surface that regression instead of mis-binding.
    /// </summary>
    [Fact]
    public void ParseModule_BindableTypeWithOmittedMangledNameField_DropsLoudly_Swiftbind046()
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();

        // Build the document the harness would, then remove the child's mangledName property so the
        // field is absent (modeling the digester omitting it) rather than present-but-empty.
        var ghost = CreateNode(
            "TypeDecl", declKind: "Struct", name: "Ghost", moduleName: "TestModule", mangledName: "placeholder");
        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                json_format_version = ExpectedVersion,
                Children = new[] { ghost },
            },
        };
        var doc = JObject.Parse(JsonConvert.SerializeObject(root));
        var child = (JObject)doc["ABIRoot"]!["Children"]![0]!;
        Assert.True(child.Remove("MangledName"), "expected the serialized child to carry a MangledName property to strip");

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, doc.ToString());
        try
        {
            var parser = new SwiftABIParser(
                filePath, new TypeDatabase(), CreateEmptyDemanglingResults(), logger, SwiftInterfaceFacts.Empty);
            var result = parser.ParseModule();

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND046"));
            Assert.Equal(1, result.Reconciliation.DroppedWithError);
            Assert.Equal(0, result.Reconciliation.SkippedWithReason);
            Assert.Contains(
                InputResolutionReport.Decisions,
                d => d.Category == InputResolutionCategory.AbiJson
                     && d.Severity == InputResolutionSeverity.Degradation
                     && d.Detail.Contains("no mangled name"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
    /// Companion scope guard: a NON-bindable structural container (a <c>Module</c>-kind TypeDecl)
    /// legitimately carries no mangled name and must NOT be mis-reported as a lost record. The
    /// record-drop gate is scoped to bindable kinds (Struct/Enum/Class/Protocol), so a Module node is
    /// a deliberate skip — never a DroppedWithError entry and never an AbiJson degradation. Without the
    /// scoping, every module-wrapper node would falsely fail <c>--strict-inputs</c>.
    /// </summary>
    [Fact]
    public void ParseModule_NonBindableModuleNodeMissingMangledName_IsSkippedNotDropped()
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        var moduleNode = CreateNode(
            "TypeDecl", declKind: "Module", name: "TestModule", moduleName: "TestModule", mangledName: "");
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, moduleNode);

        var result = fixture.Parser.ParseModule();

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("SWIFTBIND046"));
        Assert.Equal(0, result.Reconciliation.DroppedWithError);
        Assert.Equal(1, result.Reconciliation.SkippedWithReason);
        Assert.DoesNotContain(
            InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.AbiJson && d.Severity == InputResolutionSeverity.Degradation);
    }

    /// <summary>
    /// Companion scope guard, ObjC/C-interop edge: a bindable node whose identity is rooted in
    /// ObjC/C interop legitimately carries no Swift mangled name. An imported / <c>@objc</c> ObjC
    /// class carries a <c>c:objc(...)</c> USR + an <c>ObjC</c> decl attribute; a C-typedef struct
    /// re-exported through a Swift module carries a <c>c:@T@...</c> USR with no <c>ObjC</c> attribute.
    /// Both are the EXPECTED foreign-interop ABI shape, not digester drift — the type resolves
    /// through the Apple supplement / out-of-module path when referenced, and the re-export node is
    /// never bound. So it must be a deliberate skip (SkippedWithReason), never a DroppedWithError and
    /// never an AbiJson degradation. Without this exemption every ObjC-touching binding's
    /// reconciliation tally is poisoned and <c>--strict-inputs</c> fails closed spuriously
    /// (regression from b297b66f: Quick/BonMot/Firebase/… all tripped the gate). Both arms of the
    /// exemption (decl-attribute and USR-prefix) are exercised so dropping either turns a case red.
    /// The non-ObjC <c>Ghost</c> drop test above remains the negative control: a genuine Swift type
    /// that lost its mangled name still drops loudly.
    /// </summary>
    [Theory]
    [InlineData("Class", "c:objc(cs)QuickSpec", true)]          // ObjC class: USR + ObjC attr (real Quick shape)
    [InlineData("Protocol", "c:objc(pl)NSObject", true)]        // ObjC protocol: USR + ObjC attr
    [InlineData("Struct", "c:@T@NSAttributedStringKey", false)] // C-typedef struct: USR-prefix arm only, no ObjC attr
    public void ParseModule_ObjCRootedBindableMissingMangledName_SkippedNotDropped(
        string bindableKind, string usr, bool hasObjCAttr)
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        var objcNode = CreateNode(
            "TypeDecl", declKind: bindableKind, name: "Foreign", moduleName: "TestModule", mangledName: "",
            usr: usr, declAttributes: hasObjCAttr ? ["ObjC", "Dynamic"] : []);
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, objcNode);

        var result = fixture.Parser.ParseModule();

        // Not bound, but skipped cleanly — no record-loss diagnostic.
        Assert.DoesNotContain(result.ModuleDecl.Types, t => t.Name == "Foreign");
        Assert.DoesNotContain(result.ModuleDecl.Protocols, p => p.Name == "Foreign");
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("SWIFTBIND046"));
        Assert.Equal(0, result.Reconciliation.DroppedWithError);
        Assert.Equal(1, result.Reconciliation.SkippedWithReason);
        Assert.DoesNotContain(
            InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.AbiJson && d.Severity == InputResolutionSeverity.Degradation);
    }

    #region Harness

    private static ParserFixture CreateParser(int? jsonFormatVersion, ILogger logger, params Node[] nodes)
    {
        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                json_format_version = jsonFormatVersion,
                Children = nodes,
            },
        };

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, JsonConvert.SerializeObject(root));

        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateEmptyDemanglingResults(),
            logger,
            SwiftInterfaceFacts.Empty);

        return new ParserFixture(parser, filePath);
    }

    private static DemanglingResults CreateEmptyDemanglingResults()
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;

        return (DemanglingResults)ctor.Invoke([Array.Empty<IReduction>(), null]);
    }

    private static Node CreateNode(
        string kind,
        string declKind = "",
        string name = "",
        string moduleName = "TestModule",
        string mangledName = "$s",
        IEnumerable<Node>? children = null,
        string? usr = null,
        string[]? declAttributes = null)
    {
        return new Node
        {
            Kind = kind,
            DeclKind = declKind,
            Name = name,
            MangledName = mangledName,
            PrintedName = name,
            ModuleName = moduleName,
            usr = usr,
            DeclAttributes = declAttributes ?? [],
            @static = false,
            IsInternal = false,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = children ?? [],
            Conformances = [],
            Accessors = [],
        };
    }

    private sealed class ParserFixture : IDisposable
    {
        public ParserFixture(SwiftABIParser parser, string filePath)
        {
            Parser = parser;
            _filePath = filePath;
        }

        public SwiftABIParser Parser { get; }
        private readonly string _filePath;

        public void Dispose()
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    #endregion
}
