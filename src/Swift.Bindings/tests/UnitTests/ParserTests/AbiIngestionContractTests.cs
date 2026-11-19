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

    /// <summary>
    /// The drop-with-error census is backed by a structured ledger entry, not just a warning + a
    /// degradation decision: an unrecognized ABI node kind records an <see cref="IngestionPlane.Ingest"/>
    /// / <see cref="IngestionCause.UnrecognizedNodeKind"/> / <see cref="IngestionDisposition.ReportOnly"/>
    /// / <see cref="IngestionStatus.Dropped"/> entry. This is the machine-readable half of "no parser
    /// loss is ever silent" — every <c>DroppedWithError</c> increment routes through <c>RecordDropLedger</c>.
    /// </summary>
    [Fact]
    public void ParseModule_UnknownNodeKind_RecordsIngestPlaneDropLedgerEntry()
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        using var fixture = CreateParser(
            jsonFormatVersion: ExpectedVersion, logger,
            CreateNode("__UnmodeledFutureKind", name: "ghost", moduleName: "TestModule",
                mangledName: "$s10TestModule5ghostV"));

        fixture.Parser.ParseModule();

        var entry = Assert.Single(
            InputResolutionReport.Ledger,
            e => e.Cause == IngestionCause.UnrecognizedNodeKind);
        Assert.Equal(IngestionPlane.Ingest, entry.Plane);
        Assert.Equal(IngestionDisposition.ReportOnly, entry.Disposition);
        Assert.Equal(IngestionStatus.Dropped, entry.Status);
        Assert.Equal("TestModule", entry.Input.Module);
        Assert.Equal("__UnmodeledFutureKind", entry.Input.Kind);
        Assert.False(string.IsNullOrWhiteSpace(entry.ClosureEvidence));
    }

    // ---- ABI node-kind golden (Finding 58, amendment C) ----
    // The committed KnownAbiNodeKinds vocabulary stays in lockstep with the HandleNode dispatch
    // switch. This is the compile-time guard; it does NOT duplicate the SWIFTBIND034 runtime arm
    // (exercised above) — it pins that every declared-known kind is actually recognized.

    [Fact]
    public void KnownAbiNodeKinds_EveryMember_IsRecognized_NeverCensusedAsUnknown()
    {
        // Ties the set to the switch: a kind declared known but routed to the `default` (unknown)
        // arm — or a switch case removed without updating the set — turns this red.
        foreach (var kind in SwiftABIParser.KnownAbiNodeKinds)
        {
            InputResolutionReport.Reset();
            var logger = new CapturingLogger();
            using var fixture = CreateParser(
                jsonFormatVersion: ExpectedVersion, logger,
                CreateNode(kind, name: "member", moduleName: "TestModule"));

            var result = fixture.Parser.ParseModule();

            Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("SWIFTBIND034"));
            Assert.True(
                result.Reconciliation.UnknownNodeKinds is null
                || !result.Reconciliation.UnknownNodeKinds.ContainsKey(kind),
                $"'{kind}' is in KnownAbiNodeKinds but the dispatch switch censused it as unknown — "
                + "the set and the switch have drifted.");
        }
    }

    [Fact]
    public void KnownAbiNodeKinds_IsTheExactDispatchSwitchVocabulary()
    {
        // Frozen golden: bound kinds + recognized-and-skipped kinds, exactly. A new switch case (or a
        // removed one) must update this set deliberately — mirroring ClangAstParser.KnownTopLevelNodeKinds.
        var expected = new[]
        {
            "TypeDecl", "Function", "Constructor", "Var", "Subscript",
            "Import", "AssociatedType", "OperatorDecl",
        };
        Assert.Equal(
            expected.OrderBy(k => k, StringComparer.Ordinal),
            SwiftABIParser.KnownAbiNodeKinds.OrderBy(k => k, StringComparer.Ordinal));
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

    /// <summary>
    /// A module-level operator function (<c>public func &amp;&amp;(l: P, r: P) -&gt; P</c>) parses into an
    /// <see cref="OperatorDecl"/> with the module as its parent. The parser used to keep only the
    /// module's types, protocols, functions and properties, so the operator fell out of the model and
    /// no later stage could report it. It must reach <see cref="ModuleDecl.Operators"/>.
    /// </summary>
    [Fact]
    public void ParseModule_ModuleLevelOperatorFunction_ReachesModuleOperators()
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        static Node BoolType()
        {
            var n = CreateNode("TypeNominal", name: "Bool", moduleName: "Swift");
            n.PrintedName = "Swift.Bool";
            n.usr = "s:Sb";
            return n;
        }
        var op = CreateNode(
            "Function", declKind: "Func", name: "&&", moduleName: "TestModule",
            mangledName: "$s10TestModule2aaoiyS2b_SbtF",
            children: new[] { BoolType(), BoolType(), BoolType() });
        op.PrintedName = "&&(_:_:)";
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, op);

        var result = fixture.Parser.ParseModule();

        var parsed = Assert.Single(result.ModuleDecl.Operators);
        Assert.Equal("&&", parsed.OperatorSymbol);
        Assert.Same(result.ModuleDecl, parsed.ParentDecl);
        Assert.Empty(result.ModuleDecl.Methods);
    }

    // ---- missing load-bearing field: record drop (SWIFTBIND046) ----

    /// <summary>
    /// No silent loss of records: a bindable type declaration whose load-bearing mangled name is
    /// absent is a malformed type record — every downstream binder keys off that name, so the type
    /// cannot be bound. It is NOT dropped; it is QUARANTINED. The decl is kept in the module tree (so
    /// it carries a stable identity the proven-closure withdrawal can name) with
    /// <see cref="TypeDecl.IsIngestionQuarantined"/> set, warned (SWIFTBIND046), and recorded as a
    /// structured ingestion-ledger entry (<see cref="IngestionCause.MalformedTypeRecord"/> /
    /// <see cref="IngestionDisposition.QuarantineType"/> / <see cref="IngestionStatus.Quarantined"/>).
    /// Because it is emitted-then-tombstoned rather than lost mid-parse, it counts under <c>Emitted</c>,
    /// NOT <c>DroppedWithError</c>, and does not raise a coarse AbiJson degradation — the ledger, not the
    /// degradation gate, is the record of a proven quarantine (the emission-time closure walk withdraws
    /// it plus its dependents, or fails the module before emission if that closure is unprovable).
    /// </summary>
    [Theory]
    [InlineData("Struct")]
    [InlineData("Enum")]
    [InlineData("Class")]
    [InlineData("Protocol")]
    public void ParseModule_BindableTypeMissingMangledName_QuarantinedLoudly_Swiftbind046_AndLedgered(string bindableKind)
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        // Every bindable type kind in the gate must quarantine — a theory so dropping any one kind
        // from the SwiftABIParser allowlist would turn a case red instead of leaving the test green.
        var ghostType = CreateNode(
            "TypeDecl", declKind: bindableKind, name: "Ghost", moduleName: "TestModule", mangledName: "");
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, ghostType);

        var result = fixture.Parser.ParseModule();

        // Kept in the tree and marked (ProtocolDecl is a TypeDecl, so all four kinds land in Types)...
        var ghost = result.ModuleDecl.Types.FirstOrDefault(t => t.Name == "Ghost");
        Assert.NotNull(ghost);
        Assert.True(ghost!.IsIngestionQuarantined);
        // ...warned with the dedicated diagnostic...
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND046"));
        // ...counted as emitted-then-tombstoned, not a mid-parse record loss...
        Assert.Equal(0, result.Reconciliation.DroppedWithError);
        Assert.Equal(0, result.Reconciliation.SkippedWithReason);
        Assert.True(result.Reconciliation.IsBalanced);
        // ...recorded as a structured quarantine ledger entry naming the malformed type...
        Assert.Contains(
            InputResolutionReport.Ledger,
            e => e.Cause == IngestionCause.MalformedTypeRecord
                 && e.Disposition == IngestionDisposition.QuarantineType
                 && e.Status == IngestionStatus.Quarantined
                 && e.Plane == IngestionPlane.Ingest
                 && e.Input.Module == "TestModule"
                 && e.Input.Kind == bindableKind);
        // ...and NOT surfaced as a coarse AbiJson degradation (the ledger is the quarantine record).
        Assert.DoesNotContain(
            InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.AbiJson && d.Severity == InputResolutionSeverity.Degradation);
    }

    /// <summary>
    /// The actual motivating shape: the swift-api-digester OMITS the field entirely rather than
    /// emitting an empty string. Newtonsoft 13.0.3 sets members by reflection and does NOT honor
    /// C# 11 <c>required</c>, so an absent <c>mangledName</c> deserializes to null (not a parse error) —
    /// the silent gap this change closes. This test strips the property from the serialized document so
    /// the field is genuinely absent, then asserts the quarantine still fires (the gate's
    /// <c>IsNullOrEmpty</c> must cover null, not just empty). If Newtonsoft ever started enforcing
    /// <c>required</c>, the parser would throw on load and this test would surface that regression
    /// instead of mis-binding.
    /// </summary>
    [Fact]
    public void ParseModule_BindableTypeWithOmittedMangledNameField_QuarantinedLoudly_Swiftbind046()
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
            Assert.Equal(0, result.Reconciliation.DroppedWithError);
            var ghostDecl = result.ModuleDecl.Types.FirstOrDefault(t => t.Name == "Ghost");
            Assert.NotNull(ghostDecl);
            Assert.True(ghostDecl!.IsIngestionQuarantined);
            Assert.Contains(
                InputResolutionReport.Ledger,
                e => e.Cause == IngestionCause.MalformedTypeRecord
                     && e.Disposition == IngestionDisposition.QuarantineType
                     && e.Status == IngestionStatus.Quarantined);
            Assert.DoesNotContain(
                InputResolutionReport.Decisions,
                d => d.Category == InputResolutionCategory.AbiJson && d.Severity == InputResolutionSeverity.Degradation);
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

    /// <summary>
    /// Companion scope guard, Clang C-aggregate edge. When a Swift module retroactively conforms or
    /// extends a C aggregate imported from a system module, swift-api-digester emits a re-export stub
    /// node for that aggregate: a Clang USR for a C struct / typedef'd aggregate / enum / union,
    /// <c>isExternal: true</c>, a foreign <c>moduleName</c>, NO <c>ObjC</c> decl attribute, and — because
    /// the declaration is written in C rather than Swift — no Swift mangled name at all. That absence is
    /// the expected shape for a C declaration, not digester drift, so it must be a clean skip and never a
    /// quarantine: quarantining the aggregate withdraws every declaration that merely STORES it, which for
    /// a library whose only foreign edge is a system C type is the whole binding. The pre-existing
    /// exemption keyed on <c>c:objc(</c> / <c>c:@T@</c> USRs, which none of these shapes match.
    /// </summary>
    [Theory]
    [InlineData("Struct", "c:@S@CGPoint", "CoreFoundation")]            // C struct
    [InlineData("Struct", "c:@SA@simd_quatf", "simd")]                  // typedef'd C aggregate
    [InlineData("Enum", "c:@E@NSComparisonResult", "Foundation")]       // C enum
    [InlineData("Struct", "c:@U@CFRuntimeBase", "CoreFoundation")]      // C union
    public void ParseModule_ForeignClangAggregateReexportStub_SkippedNotQuarantined(
        string bindableKind, string usr, string foreignModule)
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        var stub = CreateNode(
            "TypeDecl", declKind: bindableKind, name: "ForeignAggregate", moduleName: foreignModule,
            mangledName: "", usr: usr, declAttributes: [], isExternal: true);
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, stub);

        var result = fixture.Parser.ParseModule();

        // Reached the malformed-record gate and was exempted there (rather than dropped earlier as a
        // plain re-export) — the log line names the stub, so the exemption is attributable.
        Assert.Contains(logger.Entries, e => e.Message.Contains("Clang-rooted re-export stub")
            && e.Message.Contains("ForeignAggregate"));
        Assert.DoesNotContain(result.ModuleDecl.Types, t => t.Name == "ForeignAggregate");
        Assert.DoesNotContain(result.ModuleDecl.Protocols, p => p.Name == "ForeignAggregate");
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("SWIFTBIND046"));
        Assert.Equal(0, result.Reconciliation.DroppedWithError);
        Assert.DoesNotContain(
            InputResolutionReport.Decisions,
            d => d.Category == InputResolutionCategory.AbiJson && d.Severity == InputResolutionSeverity.Degradation);
    }

    /// <summary>
    /// Negative control for the exemption above: BOTH facts have to hold. A node the module itself owns
    /// that happens to carry a Clang USR is not an external re-export stub, and an external node with a
    /// Swift USR is a Swift declaration whose mangled name genuinely went missing. Either shape is still
    /// a malformed type record and must still quarantine, or the exemption becomes a blanket amnesty that
    /// swallows real digester drift.
    /// </summary>
    [Theory]
    [InlineData("c:@S@LocalAggregate", null)]                 // Clang USR, but not external
    [InlineData("s:11TestModule5GhostV", true)]               // external, but a Swift USR
    public void ParseModule_ClangUsrWithoutExternal_OrExternalWithSwiftUsr_StillQuarantined(
        string usr, bool? isExternal)
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        var node = CreateNode(
            "TypeDecl", declKind: "Struct", name: "Ghost", moduleName: "TestModule", mangledName: "",
            usr: usr, declAttributes: [], isExternal: isExternal);
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, node);

        var result = fixture.Parser.ParseModule();

        Assert.Contains(logger.Entries, e => e.Message.Contains("SWIFTBIND046") && e.Message.Contains("Ghost"));
    }

    /// <summary>
    /// The digester writes a type-declaration node for every foreign type the module extends, with
    /// the type's real kind. The parser keeps that kind by module-qualified name whatever it does with
    /// the node afterwards (a third-party re-export is dropped, a system one is kept under its own
    /// module), because a foreign extension is bound by its receiver's kind, not its module's.
    /// </summary>
    [Theory]
    [InlineData("Foundation", "AttributeScopes", "Enum", "s:10Foundation15AttributeScopesO")]
    [InlineData("Foundation", "URL", "Struct", "s:10Foundation3URLV")]
    [InlineData("OtherLib", "Widget", "Class", "s:8OtherLib6WidgetC")]
    public void ParseModule_ForeignTypeNode_RecordsItsDeclaredKind(
        string foreignModule, string typeName, string declKind, string usr)
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        var local = CreateNode("TypeDecl", declKind: "Struct", name: "Local", mangledName: "$s10TestModule5LocalV");
        var foreign = CreateNode(
            "TypeDecl", declKind: declKind, name: typeName, moduleName: foreignModule,
            mangledName: "$s" + usr[2..], usr: usr, declAttributes: [], isExternal: true);
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, local, foreign);

        var result = fixture.Parser.ParseModule();

        Assert.Equal(declKind, result.ModuleDecl.ForeignTypeDeclKinds[$"{foreignModule}.{typeName}"]);
        Assert.DoesNotContain(result.ModuleDecl.ForeignTypeDeclKinds.Keys, k => k.EndsWith(".Local", StringComparison.Ordinal));
    }

    /// <summary>
    /// A value type nested in an ObjC-rooted foreign class is recorded too, although the parser drops
    /// the class itself without visiting its children: an extension on the nested type is bound by
    /// its kind like any other.
    /// </summary>
    [Fact]
    public void ParseModule_ValueTypeNestedInObjCRootedForeignClass_RecordsItsDeclaredKind()
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        var local = CreateNode("TypeDecl", declKind: "Struct", name: "Local", mangledName: "$s10TestModule5LocalV");
        var nested = CreateNode(
            "TypeDecl", declKind: "Struct", name: "Options", moduleName: "Foundation",
            mangledName: "$sSo8NSWidgetC10FoundationE7OptionsV", isExternal: true);
        var objcClass = CreateNode(
            "TypeDecl", declKind: "Class", name: "NSWidget", moduleName: "Foundation",
            mangledName: "", usr: "c:objc(cs)NSWidget", declAttributes: ["ObjC"], isExternal: true,
            children: [nested]);
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, local, objcClass);

        var result = fixture.Parser.ParseModule();

        Assert.Equal("Class", result.ModuleDecl.ForeignTypeDeclKinds["Foundation.NSWidget"]);
        Assert.Equal("Struct", result.ModuleDecl.ForeignTypeDeclKinds["Foundation.NSWidget.Options"]);
    }

    /// <summary>
    /// A foreign receiver the parser cannot keep (a Swift enum, from a system module or a third-party
    /// one) takes with it every declaration this module adds to it. Each of them is parked on the
    /// module so the emitter can report it, instead of vanishing with the receiver; a nested type of
    /// the module's own is one entry, and a foreign type nested in the receiver is walked for its own.
    /// </summary>
    [Theory]
    [InlineData("Foundation", "s:10Foundation15AttributeScopesO")]
    [InlineData("OtherLib", "s:8OtherLib15AttributeScopesO")]
    public void ParseModule_DroppedForeignEnumReceiver_ParksTheModulesDeclarationsOnIt(string foreignModule, string usr)
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        var local = CreateNode("TypeDecl", declKind: "Struct", name: "Local", mangledName: "$s10TestModule5LocalV");
        var ownNestedType = CreateNode(
            "TypeDecl", declKind: "Struct", name: "TranslationAttributes",
            mangledName: "$s" + usr[2..] + "10TestModuleE21TranslationAttributesV");
        var ownProperty = CreateNode("Var", declKind: "Var", name: "translation", mangledName: "$s_translation");
        var ownMethodOnNestedForeign = CreateNode("Function", declKind: "Func", name: "describe()", mangledName: "$s_describe");
        var foreignNested = CreateNode(
            "TypeDecl", declKind: "Enum", name: "FoundationAttributes", moduleName: foreignModule,
            mangledName: "$s" + usr[2..] + "20FoundationAttributesO", isExternal: true,
            children: [ownMethodOnNestedForeign]);
        var foreignOnly = CreateNode("Var", declKind: "Var", name: "foundation", moduleName: foreignModule, mangledName: "$s_foundation");
        var receiver = CreateNode(
            "TypeDecl", declKind: "Enum", name: "AttributeScopes", moduleName: foreignModule,
            mangledName: "$s" + usr[2..], usr: usr, declAttributes: [], isExternal: true,
            children: [ownNestedType, ownProperty, foreignNested, foreignOnly]);
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, local, receiver);

        var result = fixture.Parser.ParseModule();

        var parked = result.ModuleDecl.UnboundForeignReceiverMembers
            .Select(u => $"{u.ForeignTypeQualifiedName}|{u.Kind}|{u.Name}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                $"{foreignModule}.AttributeScopes.FoundationAttributes|Method|describe()",
                $"{foreignModule}.AttributeScopes|Property|translation",
                $"{foreignModule}.AttributeScopes|Type|TranslationAttributes",
            },
            parked);
        Assert.All(result.ModuleDecl.UnboundForeignReceiverMembers, u => Assert.StartsWith("Not bound: ", u.Details));
    }

    /// <summary>
    /// Overloads on a dropped receiver are parked one entry each, told apart by their symbols.
    /// </summary>
    [Fact]
    public void ParseModule_DroppedReceiverOverloads_ParkedWithTheirSymbols()
    {
        InputResolutionReport.Reset();
        var local = CreateNode("TypeDecl", declKind: "Struct", name: "Local", mangledName: "$s10TestModule5LocalV");
        var receiver = CreateNode(
            "TypeDecl", declKind: "Enum", name: "Scopes", moduleName: "OtherLib",
            mangledName: "$s8OtherLib6ScopesO", usr: "s:8OtherLib6ScopesO", isExternal: true,
            children:
            [
                CreateNode("Function", declKind: "Func", name: "describe", mangledName: "$s_describe_Int32"),
                CreateNode("Function", declKind: "Func", name: "describe", mangledName: "$s_describe_String"),
            ]);
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, new CapturingLogger(), local, receiver);

        var result = fixture.Parser.ParseModule();

        Assert.Equal(
            new[] { "$s_describe_Int32", "$s_describe_String" },
            result.ModuleDecl.UnboundForeignReceiverMembers.Select(u => u.Symbol).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// A dropped re-exported receiver is walked for this module's declarations however deep they sit:
    /// one on a foreign type nested in it is lost with the receiver, even when the receiver's own
    /// children are all foreign.
    /// </summary>
    [Fact]
    public void ParseModule_DroppedReceiverWithOnlyForeignChildren_ParksDeclarationsNestedDeeper()
    {
        InputResolutionReport.Reset();
        var local = CreateNode("TypeDecl", declKind: "Struct", name: "Local", mangledName: "$s10TestModule5LocalV");
        var inner = CreateNode(
            "TypeDecl", declKind: "Enum", name: "Inner", moduleName: "OtherLib",
            mangledName: "$s8OtherLib5OuterC5InnerO", isExternal: true,
            children: [CreateNode("TypeDecl", declKind: "Struct", name: "Added", mangledName: "$s_Added")]);
        var outer = CreateNode(
            "TypeDecl", declKind: "Class", name: "Outer", moduleName: "OtherLib",
            mangledName: "$s8OtherLib5OuterC", usr: "s:8OtherLib5OuterC", isExternal: true,
            children: [inner]);
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, new CapturingLogger(), local, outer);

        var result = fixture.Parser.ParseModule();

        var parked = Assert.Single(result.ModuleDecl.UnboundForeignReceiverMembers);
        Assert.Equal("OtherLib.Outer.Inner|Type|Added", $"{parked.ForeignTypeQualifiedName}|{parked.Kind}|{parked.Name}");
    }

    /// <summary>
    /// A member this module adds to a Swift standard-library generic is not bound (Swift.Runtime owns
    /// those types), and is parked rather than dropped with the receiver.
    /// </summary>
    [Fact]
    public void ParseModule_StdlibGenericReceiver_ParksTheModulesDeclarationsOnIt()
    {
        InputResolutionReport.Reset();
        var local = CreateNode("TypeDecl", declKind: "Struct", name: "Local", mangledName: "$s10TestModule5LocalV");
        var array = CreateNode(
            "TypeDecl", declKind: "Struct", name: "Array", moduleName: "Swift",
            mangledName: "$sSa", usr: "s:Sa", isExternal: true,
            children: [CreateNode("Var", declKind: "Var", name: "firstLocal", mangledName: "$s_firstLocal")]);
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, new CapturingLogger(), local, array);

        var result = fixture.Parser.ParseModule();

        var parked = Assert.Single(result.ModuleDecl.UnboundForeignReceiverMembers);
        Assert.Equal("Swift.Array|Property|firstLocal", $"{parked.ForeignTypeQualifiedName}|{parked.Kind}|{parked.Name}");
    }

    /// <summary>
    /// On an Objective-C class the interface's foreign-extension path binds or reports this module's
    /// methods and properties; the kinds it never carries (nested types, initializers, subscripts) are
    /// parked with the dropped node, and the rest are left to that path.
    /// </summary>
    [Fact]
    public void ParseModule_ObjCRootedReceiver_ParksOnlyWhatTheInterfacePathDoesNotCarry()
    {
        InputResolutionReport.Reset();
        var local = CreateNode("TypeDecl", declKind: "Struct", name: "Local", mangledName: "$s10TestModule5LocalV");
        var view = CreateNode(
            "TypeDecl", declKind: "Class", name: "UIView", moduleName: "UIKit",
            mangledName: "", usr: "c:objc(cs)UIView", declAttributes: ["ObjC"], isExternal: true,
            children:
            [
                CreateNode("Constructor", declKind: "Constructor", name: "init", mangledName: "$s_init_rank"),
                CreateNode("TypeDecl", declKind: "Struct", name: "Style", mangledName: "$s_Style"),
                CreateNode("Subscript", declKind: "Subscript", name: "subscript", mangledName: "$s_subscript"),
                CreateNode("Var", declKind: "Var", name: "rank", mangledName: "$s_rank"),
                CreateNode("Function", declKind: "Func", name: "refresh", mangledName: "$s_refresh"),
            ]);
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, new CapturingLogger(), local, view);

        var result = fixture.Parser.ParseModule();

        Assert.Equal(
            new[] { "UIKit.UIView|Method|init", "UIKit.UIView|Subscript|subscript", "UIKit.UIView|Type|Style" },
            result.ModuleDecl.UnboundForeignReceiverMembers
                .Select(u => $"{u.ForeignTypeQualifiedName}|{u.Kind}|{u.Name}")
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// A foreign receiver with nothing of this module's on it parks nothing, whatever the parser does
    /// with the receiver itself.
    /// </summary>
    [Fact]
    public void ParseModule_ForeignReceiverWithoutTheModulesDeclarations_ParksNothing()
    {
        InputResolutionReport.Reset();
        var logger = new CapturingLogger();
        var local = CreateNode("TypeDecl", declKind: "Struct", name: "Local", mangledName: "$s10TestModule5LocalV");
        var foreign = CreateNode(
            "TypeDecl", declKind: "Enum", name: "AttributeScopes", moduleName: "OtherLib",
            mangledName: "$s8OtherLib15AttributeScopesO", usr: "s:8OtherLib15AttributeScopesO",
            declAttributes: [], isExternal: true,
            children: [CreateNode("Var", declKind: "Var", name: "foundation", moduleName: "OtherLib", mangledName: "$s_f")]);
        using var fixture = CreateParser(jsonFormatVersion: ExpectedVersion, logger, local, foreign);

        var result = fixture.Parser.ParseModule();

        Assert.Empty(result.ModuleDecl.UnboundForeignReceiverMembers);
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
        string[]? declAttributes = null,
        bool? isExternal = null)
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
            isExternal = isExternal,
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
