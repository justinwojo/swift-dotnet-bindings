// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Concurrent;
using BindingsGeneration.ObjC;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Xunit;

namespace BindingsGeneration.Tests;

public class BindingArtifactManifestTests
{
    private static BindingReport NewReport(string module = "Demo")
    {
        var report = new BindingReport { ModuleName = module };
        report.TotalTypes = 1;
        report.EmittedTypes = 1;
        report.SkippedTypes = 0;
        report.TotalMembers = 18;
        report.EmittedMembers = 18;
        report.SkippedMembers = 0;
        report.EmittedMembersByKind[BindingItemKind.Method] = 12;
        report.EmittedMembersByKind[BindingItemKind.Property] = 6;
        return report;
    }

    private static CoGatedMember Mangled(string name, string type, string symbol, int ordinal,
        BindingItemKind kind = BindingItemKind.Method, string? sourceFile = "Demo.cs") => new()
    {
        Name = name,
        ContainingType = type,
        Kind = kind,
        MangledSymbol = symbol,
        Ordinal = ordinal,
        Confidence = IdentityConfidence.Mangled,
        SourceFile = sourceFile,
    };

    private static CoGatedMember Heuristic(string name, string type, int ordinal,
        BindingItemKind kind = BindingItemKind.Method, string? sourceFile = "Demo.cs") => new()
    {
        Name = name,
        ContainingType = type,
        Kind = kind,
        MangledSymbol = null,
        Ordinal = ordinal,
        Confidence = IdentityConfidence.Heuristic,
        SourceFile = sourceFile,
    };

    [Fact]
    public void Projection_AllMethodsCoGated_DrainsToZero()
    {
        // 18 emitted methods, 18 cogated → 0 emitted, 18 distinct skipped items.
        // Wrapper-side capture produces Heuristic identities — public C# decls have no
        // 1:1 mangled wrapper symbol; the wrapper that triggered the cascade is an
        // internal trampoline.
        var report = new BindingReport { ModuleName = "RecordStore" };
        report.TotalMembers = 18;
        report.EmittedMembers = 18;
        report.EmittedMembersByKind[BindingItemKind.Method] = 18;

        var wrapper = new WrapperSection { Status = PhaseStatus.Success };
        for (int i = 0; i < 18; i++)
            ((List<CoGatedMember>)wrapper.CSharpCoGatedMembers).Add(
                Heuristic($"M{i}", "RecordStore.Db", i));

        var manifest = new BindingArtifactManifest
        {
            Module = "RecordStore",
            Generation = GenerationSection.From(report),
            Wrapper = wrapper,
        };

        var projected = BindingReportProjection.Project(manifest);

        Assert.Equal(0, projected.EmittedMembers);
        Assert.Equal(18, projected.SkippedMembers);
        Assert.Equal(18, projected.SkippedItems.Count);
        Assert.False(projected.EmittedMembersByKind.ContainsKey(BindingItemKind.Method));
        Assert.Equal(18, projected.SkippedMembersByKind[BindingItemKind.Method]);
        Assert.All(projected.SkippedItems, item =>
            Assert.Equal(SkipReason.MissingWrapperSymbol, item.Reason));
    }

    [Fact]
    public void Projection_PreservesOverloadDuplicates()
    {
        // Two overloads with identical Name+Kind+ContainingType but distinct ordinals
        // must NOT collapse — overload-correctness.
        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Generation = GenerationSection.From(NewReport()),
            Wrapper = new WrapperSection(),
        };
        var coGated = (List<CoGatedMember>)manifest.Wrapper!.CSharpCoGatedMembers;
        coGated.Add(Heuristic("Combine", "Demo.Foo", 0));
        coGated.Add(Heuristic("Combine", "Demo.Foo", 1));

        var report = BindingReportProjection.Project(manifest);

        Assert.Equal(2, report.SkippedItems.Count);
        Assert.Equal(16, report.EmittedMembers);
        Assert.Equal(2, report.SkippedMembers);
    }

    [Fact]
    public void Projection_PopulatesSkipTriage_PostCoGating()
    {
        // Two generation-time skips (one expected-nonpublic, one unexplained EveryProtocol drop),
        // plus a wrapper-stripped member co-gated in. The triage must be computed AFTER co-gating so
        // the MissingWrapperSymbol member — which only exists post-projection — lands in Review.
        var report = new BindingReport { ModuleName = "TriageDemo" };
        report.EmittedMembers = 1;
        report.EmittedMembersByKind[BindingItemKind.Method] = 1;
        report.SkippedItems.Add(new SkippedItem
        {
            Kind = BindingItemKind.Type,
            Name = "InternalThing",
            Reason = SkipReason.ModuleInternal,
        });
        report.SkippedItems.Add(new SkippedItem
        {
            Kind = BindingItemKind.Type,
            Name = "CAPIReporterProxy",
            ContainingType = "TriageDemo.CAPIReporter",
            Reason = SkipReason.EveryProtocolConformanceSkipped,
            Details = "Protocol proxy skipped: EveryProtocol conformance was not emitted (no decision recorded).",
        });

        var manifest = new BindingArtifactManifest
        {
            Module = "TriageDemo",
            Generation = GenerationSection.From(report),
            Wrapper = new WrapperSection { Status = PhaseStatus.Success },
        };
        ((List<CoGatedMember>)manifest.Wrapper!.CSharpCoGatedMembers).Add(Heuristic("M0", "TriageDemo.Db", 0));

        var projected = BindingReportProjection.Project(manifest);

        Assert.NotNull(projected.SkipTriage);
        var triage = projected.SkipTriage!;
        Assert.Equal(3, triage.Total);
        Assert.Equal(1, triage.ByDisposition["ExpectedNonPublic"]);
        Assert.Equal(2, triage.ByDisposition["Review"]);
        Assert.Equal(2, triage.ReviewCount);
        // The co-gated wrapper-stripped member proves the roll-up ran post-cogating.
        Assert.Contains(triage.ReviewItems, i => i.Reason == SkipReason.MissingWrapperSymbol);
        Assert.Contains(triage.ReviewItems, i => i.Reason == SkipReason.EveryProtocolConformanceSkipped);
    }

    [Fact]
    public void Projection_ClampsEmittedMembersAtZero()
    {
        // Pathological case: more cogated than emitted. Top-level scalar must not go negative.
        var report = NewReport();
        report.EmittedMembers = 1;
        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Generation = GenerationSection.From(report),
            Wrapper = new WrapperSection(),
        };
        var coGated = (List<CoGatedMember>)manifest.Wrapper!.CSharpCoGatedMembers;
        coGated.Add(Heuristic("A", "Demo.X", 0));
        coGated.Add(Heuristic("B", "Demo.X", 1));

        var projected = BindingReportProjection.Project(manifest);

        Assert.Equal(0, projected.EmittedMembers);
        Assert.Equal(2, projected.SkippedMembers);
    }

    [Fact]
    public void JsonRoundTrip_PreservesAllSections()
    {
        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            GeneratorVersion = "1.0.0",
            Generation = GenerationSection.From(NewReport()),
            Emission = new EmissionSection
            {
                Status = PhaseStatus.Success,
                SuppressedProxyClassCount = 2,
            },
            Wrapper = new WrapperSection
            {
                Status = PhaseStatus.Fatal,
                RawOutcome = "Fatal",
                ExitCode = 1,
                DiagnosticCode = null,
                Message = "All Swift wrapper code was stripped as broken (3 block(s)).",
                SliceCount = 1,
                CompiledFileCount = 7,
            },
            Bridge = new BridgeSection
            {
                Status = PhaseStatus.NoOp,
                BridgeCompiled = false,
                Message = "No bridge files.",
            },
        };
        ((List<CoGatedMember>)manifest.Wrapper.CSharpCoGatedMembers).Add(
            Mangled("Foo", "Demo.X", "$sFooSymbol", 0));
        ((List<string>)manifest.Wrapper.StrippedSymbols).Add("$sFooSymbol");

        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter> { new StringEnumConverter() },
        };
        var json = JsonConvert.SerializeObject(manifest, settings);
        var parsed = JsonConvert.DeserializeObject<BindingArtifactManifest>(json, settings)!;

        Assert.Equal("Demo", parsed.Module);
        Assert.Equal(3, parsed.SchemaVersion);
        Assert.Equal("1.0.0", parsed.GeneratorVersion);
        Assert.NotNull(parsed.Generation);
        Assert.Equal(PhaseStatus.Fatal, parsed.Wrapper!.Status);
        Assert.Equal(1, parsed.Wrapper.ExitCode);
        Assert.Null(parsed.Wrapper.DiagnosticCode);
        Assert.Single(parsed.Wrapper.CSharpCoGatedMembers);
        Assert.Equal(IdentityConfidence.Mangled, parsed.Wrapper.CSharpCoGatedMembers[0].Confidence);
        Assert.Equal(PhaseStatus.NoOp, parsed.Bridge!.Status);
        // Proxy suppression is now emission telemetry (the co-gating sections are retired).
        Assert.NotNull(parsed.Emission);
        Assert.Equal(2, parsed.Emission!.SuppressedProxyClassCount);
    }

    [Fact]
    public void GenerationSection_From_ThreadsParseReconciliation()
    {
        // Finding 14a: the optional reconciliation flows onto the GenerationSection; omitting it
        // (legacy callers) leaves the field null.
        var recon = new ParseReconciliation(Parsed: 10, Emitted: 7, SkippedWithReason: 2, DroppedWithError: 1);

        var with = GenerationSection.From(NewReport(), recon);
        Assert.NotNull(with.ParseReconciliation);
        Assert.Equal(10, with.ParseReconciliation!.Parsed);
        Assert.Equal(1, with.ParseReconciliation.DroppedWithError);
        Assert.True(with.ParseReconciliation.IsBalanced);

        var without = GenerationSection.From(NewReport());
        Assert.Null(without.ParseReconciliation);
    }

    [Fact]
    public void EmissionSection_From_ThreadsAppleSupplementProvenance()
    {
        // Finding 14c: the per-identity provenance snapshot flows onto the EmissionSection and
        // survives a JSON round-trip, so the consumer's SwiftBindings.Apple PackageReference is
        // auditable from the manifest rather than opaque.
        var emissionReport = EmissionReportEmitter.BuildReport(new ModuleEmissionContext(), "Demo");
        var snapshot = new List<(string Identity, IReadOnlyList<string> Provenance)>
        {
            ("Foundation.AnyError", new[] { "ExistentialHandler:AnyError", "TypeDatabase.TryGetTypeRecord:SwiftError" }),
            ("Foundation.Data", new[] { "TypeProjectionFactory:FoundationData" }),
        };

        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Generation = GenerationSection.From(NewReport()),
            Emission = EmissionSection.From(emissionReport, snapshot),
        };

        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter> { new StringEnumConverter() },
        };
        var json = JsonConvert.SerializeObject(manifest, settings);
        var parsed = JsonConvert.DeserializeObject<BindingArtifactManifest>(json, settings)!;

        var refs = parsed.Emission!.AppleSupplementReferences;
        Assert.Equal(2, refs.Count);
        Assert.Equal("Foundation.AnyError", refs[0].Identity);
        Assert.Equal(
            new[] { "ExistentialHandler:AnyError", "TypeDatabase.TryGetTypeRecord:SwiftError" },
            refs[0].Provenance);
        Assert.Equal("Foundation.Data", refs[1].Identity);
        Assert.Equal(new[] { "TypeProjectionFactory:FoundationData" }, refs[1].Provenance);

        // Omitting the snapshot (legacy callers) leaves the list empty, not null.
        var without = EmissionSection.From(emissionReport);
        Assert.Empty(without.AppleSupplementReferences);
    }

    [Fact]
    public void EmissionSection_From_CarriesSuppressedProxyClassCount()
    {
        // Proxy suppression migrated out of the retired ProxyCoGating section into emission
        // telemetry: BuildReport counts the distinct proxy classes withheld at emission and
        // EmissionSection.From copies that count onto the manifest.
        var ctx = new ModuleEmissionContext();
        ctx.RecordSuppressedProxy("FooProxy");
        ctx.RecordSuppressedProxy("BarProxy");
        ctx.RecordSuppressedProxy("FooProxy"); // deduped — distinct count is 2

        var report = EmissionReportEmitter.BuildReport(ctx, "Demo");
        Assert.Equal(2, report.SuppressedProxyClassCount);

        var section = EmissionSection.From(report);
        Assert.Equal(2, section.SuppressedProxyClassCount);
    }

    [Fact]
    public void GenerationSection_And_Projection_RoundTripCommentDropsAndObjectDegradations()
    {
        // Finding 53 (Codex Medium): binding-report.json is rederived from the manifest via
        // BindingReportProjection.Project — NOT written directly from the live report. So the
        // SWIFTBIND025 comment-drops and SWIFTBIND026 object-degradations must survive both
        // GenerationSection.From (report -> manifest) AND Project (manifest -> projected report),
        // or the diagnostics' "recorded in binding-report.json" promise silently breaks.
        var report = NewReport();
        report.UnsupportedCommentDrops.Add("Unsupported: method 'Loader.fetch' — closure param");
        report.ObjectDegradations.Add("any Shape");
        report.ObjectDegradations.Add("any AttributeKind");

        var section = GenerationSection.From(report);
        Assert.Equal(report.UnsupportedCommentDrops, section.UnsupportedCommentDrops);
        Assert.Equal(report.ObjectDegradations, section.ObjectDegradations);

        // Survives a JSON round-trip on the manifest, then the projected report restores both lists.
        var manifest = new BindingArtifactManifest { Module = "Demo", Generation = section };
        var settings = new JsonSerializerSettings
        {
            Converters = new List<JsonConverter> { new StringEnumConverter() },
        };
        var json = JsonConvert.SerializeObject(manifest, settings);
        var parsed = JsonConvert.DeserializeObject<BindingArtifactManifest>(json, settings)!;

        var projected = BindingReportProjection.Project(parsed);
        Assert.Equal(
            new[] { "Unsupported: method 'Loader.fetch' — closure param" },
            projected.UnsupportedCommentDrops);
        Assert.Equal(new[] { "any Shape", "any AttributeKind" }, projected.ObjectDegradations);

        // Legacy: a report with no degradations projects to empty (not null) lists.
        var emptyProjected = BindingReportProjection.Project(
            new BindingArtifactManifest { Module = "Demo", Generation = GenerationSection.From(NewReport()) });
        Assert.Empty(emptyProjected.UnsupportedCommentDrops);
        Assert.Empty(emptyProjected.ObjectDegradations);
    }

    [Fact]
    public void GenerationSection_And_Projection_RoundTripObjCPrefixBridges()
    {
        // F10 Stage 20: the ObjC-prefix bridge observability channel rides the SAME manifest
        // rederivation path as the Finding-53 degradation lists — binding-report.json is projected
        // from the manifest, not written from the live report — so the guesses must survive both
        // GenerationSection.From (report -> manifest) AND Project (manifest -> projected report),
        // or the "recorded in binding-report.json" observability is silently lost.
        var report = NewReport();
        report.ObjCPrefixBridges.Add("UIKit.UIImage");
        report.ObjCPrefixBridges.Add("Foundation.NSURL");

        var section = GenerationSection.From(report);
        Assert.Equal(report.ObjCPrefixBridges, section.ObjCPrefixBridges);

        var manifest = new BindingArtifactManifest { Module = "Demo", Generation = section };
        var settings = new JsonSerializerSettings
        {
            Converters = new List<JsonConverter> { new StringEnumConverter() },
        };
        var json = JsonConvert.SerializeObject(manifest, settings);
        var parsed = JsonConvert.DeserializeObject<BindingArtifactManifest>(json, settings)!;

        var projected = BindingReportProjection.Project(parsed);
        Assert.Equal(new[] { "UIKit.UIImage", "Foundation.NSURL" }, projected.ObjCPrefixBridges);

        // Legacy: a report with no bridges projects to an empty (not null) list.
        var emptyProjected = BindingReportProjection.Project(
            new BindingArtifactManifest { Module = "Demo", Generation = GenerationSection.From(NewReport()) });
        Assert.Empty(emptyProjected.ObjCPrefixBridges);
    }

    [Fact]
    public void ObjCSection_And_Projection_FoldMixedObjCSkipsIntoSkipTriage()
    {
        // A1: a mixed (ObjC+Swift) binding writes the Swift surface through Generation and attaches the
        // ObjC drop set as an ObjCSection. binding-report.json is rederived from the manifest, so the
        // ObjC skips must survive a JSON round-trip AND fold into the SAME SkipTriage/ReviewCount gate
        // as the Swift surface — otherwise an ObjC-heavy library's drops stay invisible to the release
        // signal (the exact "47 dropped in FBSDKCoreKit, none in any persisted artifact" gap).
        var diagnostics = new ObjCBindingDiagnostics();
        diagnostics.RecordSkip("Method", "FBSDKBasicUtility.jsonObjectWithData",
            ObjCSkipReason.UnresolvableType, "NSJSONReadingOptions not in registry");
        diagnostics.RecordSkip("class", "OMIDAdSession",
            ObjCSkipReason.MissingNativeSymbol, "no _OBJC_CLASS_$_OMIDAdSession symbol");
        diagnostics.RecordSkip("Function", "FBSDKLog",
            ObjCSkipReason.VariadicFunction, "variadic function");

        var swift = NewReport("Mixed");
        var manifest = new BindingArtifactManifest
        {
            Module = "Mixed",
            Generation = GenerationSection.From(swift),
            ObjC = ObjCSection.From(diagnostics),
        };

        // Section carries the mapped report vocabulary and a by-reason roll-up.
        Assert.Equal(3, manifest.ObjC!.SkippedSymbolCount);
        Assert.Equal(1, manifest.ObjC.SkippedByReason["ObjCUnresolvableType"]);

        var settings = new JsonSerializerSettings
        {
            Converters = new List<JsonConverter> { new StringEnumConverter() },
        };
        var json = JsonConvert.SerializeObject(manifest, settings);
        var parsed = JsonConvert.DeserializeObject<BindingArtifactManifest>(json, settings)!;

        var projected = BindingReportProjection.Project(parsed);

        // All three ObjC drops appear in the projected report's skip list with their mapped reasons.
        Assert.Contains(projected.SkippedItems,
            i => i.Reason == SkipReason.ObjCUnresolvableType && i.Name == "FBSDKBasicUtility.jsonObjectWithData");
        Assert.Contains(projected.SkippedItems, i => i.Reason == SkipReason.ObjCMissingNativeSymbol);
        Assert.Contains(projected.SkippedItems, i => i.Reason == SkipReason.ObjCVariadicFunction);

        // They roll into the triage gate: unresolvable-type + variadic are KnownLimitation, the
        // over-binding is ExpectedStructural — none land in Review (every ObjC drop is attributed).
        Assert.NotNull(projected.SkipTriage);
        var triage = projected.SkipTriage!;
        Assert.Equal(0, triage.ReviewCount);
        Assert.Equal(2, triage.ByDisposition["KnownLimitation"]);
        Assert.Equal(1, triage.ByDisposition["ExpectedStructural"]);

        // The ObjC drops also update the scalar roll-ups, not just the flat list, so the report stays
        // internally consistent: the two ObjC methods (the `Function` maps to Method) land in
        // SkippedMembers + the per-kind roll-up and the dropped `class` lands in SkippedTypes — and the
        // whole thing satisfies SkippedItems.Count == SkippedTypes + SkippedMembers.
        Assert.Equal(2, projected.SkippedMembers);
        Assert.Equal(2, projected.SkippedMembersByKind[BindingItemKind.Method]);
        Assert.Equal(1, projected.SkippedTypes);
        Assert.Equal(projected.SkippedItems.Count, projected.SkippedTypes + projected.SkippedMembers);
    }

    [Fact]
    public void ObjCSection_Only_PureObjCManifest_ProjectsSkipTriage()
    {
        // A1: a pure-ObjC binding runs no Swift generation pass, so the manifest has NO Generation
        // section — only the ObjC one. The projection must still fold those drops and build the triage,
        // so a pure-ObjC library's drop set is visible in binding-report.json exactly like a mixed one.
        var diagnostics = new ObjCBindingDiagnostics();
        diagnostics.RecordSkip("Method", "OUThing.doStuff",
            ObjCSkipReason.UnresolvableType, "SomeType not in registry");

        var manifest = new BindingArtifactManifest
        {
            Module = "PureObjC",
            ObjC = ObjCSection.From(diagnostics),
        };

        var projected = BindingReportProjection.Project(manifest);

        Assert.Single(projected.SkippedItems);
        Assert.Equal(SkipReason.ObjCUnresolvableType, projected.SkippedItems[0].Reason);
        Assert.NotNull(projected.SkipTriage);
        Assert.Equal(1, projected.SkipTriage!.Total);
        Assert.Equal(0, projected.SkipTriage.ReviewCount);

        // Even with NO Generation section the scalar roll-ups reflect the ObjC drop (they would
        // otherwise stay at their zero defaults while SkippedItems.Count is 1 — the inconsistency the
        // fold is careful to avoid). The single dropped method counts as a member, not a type.
        Assert.Equal(1, projected.SkippedMembers);
        Assert.Equal(1, projected.SkippedMembersByKind[BindingItemKind.Method]);
        Assert.Equal(0, projected.SkippedTypes);
        Assert.Equal(projected.SkippedItems.Count, projected.SkippedTypes + projected.SkippedMembers);
    }

    [Fact]
    public void Projection_NoObjCSection_LeavesSkipListUnchanged()
    {
        // Legacy / Swift-only: a manifest with no ObjC section projects with no ObjC-path skips added.
        var projected = BindingReportProjection.Project(
            new BindingArtifactManifest { Module = "Demo", Generation = GenerationSection.From(NewReport()) });
        Assert.DoesNotContain(projected.SkippedItems, i => i.Reason.ToString().StartsWith("ObjC"));
    }

    [Fact]
    public void JsonRoundTrip_PreservesParseReconciliation()
    {
        // Finding 14a: the reconciliation counts survive serialization so the manifest is the
        // durable, gate-able signal for parser-side emitted-surface loss.
        var recon = new ParseReconciliation(Parsed: 42, Emitted: 40, SkippedWithReason: 1, DroppedWithError: 1);
        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Generation = GenerationSection.From(NewReport(), recon),
        };

        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter> { new StringEnumConverter() },
        };
        var json = JsonConvert.SerializeObject(manifest, settings);
        var parsed = JsonConvert.DeserializeObject<BindingArtifactManifest>(json, settings)!;

        Assert.NotNull(parsed.Generation!.ParseReconciliation);
        Assert.Equal(42, parsed.Generation.ParseReconciliation!.Parsed);
        Assert.Equal(40, parsed.Generation.ParseReconciliation.Emitted);
        Assert.Equal(1, parsed.Generation.ParseReconciliation.SkippedWithReason);
        Assert.Equal(1, parsed.Generation.ParseReconciliation.DroppedWithError);
    }

    [Fact]
    public void InputResolutionSection_From_CountsAndStatusReflectDegradations()
    {
        // Finding 50: a section built from decisions that include at least one degradation reports
        // Warning status and a non-zero degradation count; an all-Info list reports Success.
        var degraded = InputResolutionSection.From(new List<InputResolutionDecision>
        {
            new(InputResolutionCategory.SliceSelection, InputResolutionSeverity.Info, "preferred slice"),
            new(InputResolutionCategory.Tbd, InputResolutionSeverity.Degradation, "2 TBD files present"),
        });
        Assert.Equal(PhaseStatus.Warning, degraded.Status);
        Assert.Equal(2, degraded.DecisionCount);
        Assert.Equal(1, degraded.DegradationCount);

        var clean = InputResolutionSection.From(new List<InputResolutionDecision>
        {
            new(InputResolutionCategory.SliceSelection, InputResolutionSeverity.Info, "preferred slice"),
            new(InputResolutionCategory.AbiJson, InputResolutionSeverity.Info, "arch-specific abi"),
        });
        Assert.Equal(PhaseStatus.Success, clean.Status);
        Assert.Equal(2, clean.DecisionCount);
        Assert.Equal(0, clean.DegradationCount);

        var empty = InputResolutionSection.From(System.Array.Empty<InputResolutionDecision>());
        Assert.Equal(PhaseStatus.Success, empty.Status);
        Assert.Equal(0, empty.DecisionCount);
        Assert.Empty(empty.Decisions);
    }

    [Fact]
    public void JsonRoundTrip_PreservesInputResolution()
    {
        // Finding 50: the input-resolution decisions (category, severity, detail) survive
        // serialization so a degraded input edge is durable + auditable on the manifest.
        var section = InputResolutionSection.From(new List<InputResolutionDecision>
        {
            new(InputResolutionCategory.SliceSelection, InputResolutionSeverity.Degradation,
                "device slice absent; fell back to simulator"),
            new(InputResolutionCategory.SwiftInterface, InputResolutionSeverity.Info, "found interface"),
        });
        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Generation = GenerationSection.From(NewReport()),
            InputResolution = section,
        };

        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter> { new StringEnumConverter() },
        };
        var json = JsonConvert.SerializeObject(manifest, settings);
        var parsed = JsonConvert.DeserializeObject<BindingArtifactManifest>(json, settings)!;

        // Enum members must serialize as names (StringEnumConverter), not integers.
        Assert.Contains("Degradation", json);
        Assert.Contains("SliceSelection", json);

        var ir = parsed.InputResolution!;
        Assert.Equal(PhaseStatus.Warning, ir.Status);
        Assert.Equal(2, ir.DecisionCount);
        Assert.Equal(1, ir.DegradationCount);
        Assert.Equal(2, ir.Decisions.Count);
        Assert.Equal(InputResolutionCategory.SliceSelection, ir.Decisions[0].Category);
        Assert.Equal(InputResolutionSeverity.Degradation, ir.Decisions[0].Severity);
        Assert.Equal("device slice absent; fell back to simulator", ir.Decisions[0].Detail);
        Assert.Equal(InputResolutionCategory.SwiftInterface, ir.Decisions[1].Category);
        Assert.Equal(InputResolutionSeverity.Info, ir.Decisions[1].Severity);
    }

    private static IngestionLedgerEntry LedgerEntry(
        string symbol,
        IngestionStatus status,
        IngestionDisposition disposition = IngestionDisposition.QuarantineType,
        string? referenced = null,
        string module = "Demo",
        string kind = "Struct") =>
        new(
            Input: new IngestionInputIdentity(module, kind, symbol),
            Parent: null,
            Plane: IngestionPlane.Ingest,
            Cause: IngestionCause.MalformedTypeRecord,
            Referenced: referenced,
            Disposition: disposition,
            ClosureEvidence: "closure proven complete",
            Status: status);

    [Fact]
    public void InputResolutionSection_From_ProjectsLedgerAndCountsPerStatus()
    {
        var section = InputResolutionSection.From(
            System.Array.Empty<InputResolutionDecision>(),
            new List<IngestionLedgerEntry>
            {
                LedgerEntry("QuarantinedPayload", IngestionStatus.Quarantined, referenced: "MissingType"),
                LedgerEntry("makeQuarantinedPayload", IngestionStatus.Quarantined, IngestionDisposition.DegradeLeaf),
                LedgerEntry("droppedLeaf", IngestionStatus.Dropped, IngestionDisposition.ReportOnly),
                // Retained is a completeness record, not a loss — it is projected but counts toward none of
                // the loss buckets.
                LedgerEntry("boundAfterAll", IngestionStatus.Retained),
            });

        Assert.Equal(4, section.LedgerEntryCount);
        Assert.Equal(4, section.Ledger.Count);
        Assert.Equal(2, section.QuarantinedCount);
        Assert.Equal(1, section.DroppedCount);
        Assert.Equal(0, section.FatalCount);
        // Any quarantine or drop makes the section a Warning even with no degraded decisions.
        Assert.Equal(PhaseStatus.Warning, section.Status);

        var first = section.Ledger[0];
        Assert.Equal("Demo.Struct:QuarantinedPayload", first.Input);
        Assert.Equal("MissingType", first.Referenced);
        Assert.Equal(IngestionStatus.Quarantined, first.Status);
        Assert.Equal(IngestionDisposition.QuarantineType, first.Disposition);
        Assert.Equal("closure proven complete", first.Evidence);
    }

    [Fact]
    public void InputResolutionSection_From_FatalLedgerEntry_EscalatesStatusToFatal()
    {
        // A fatal loss dominates the section status even when the decision stream is clean — a published
        // manifest never carries one, but the projection must be total so a fatal is never silently absent.
        var section = InputResolutionSection.From(
            System.Array.Empty<InputResolutionDecision>(),
            new List<IngestionLedgerEntry>
            {
                LedgerEntry("Unclosable", IngestionStatus.Fatal, IngestionDisposition.ReportOnlyFatal),
            });

        Assert.Equal(1, section.FatalCount);
        Assert.Equal(PhaseStatus.Fatal, section.Status);
    }

    [Fact]
    public void InputResolutionSection_From_NoLedger_IsEmptyProjectionNotNull()
    {
        var section = InputResolutionSection.From(System.Array.Empty<InputResolutionDecision>());
        Assert.Equal(0, section.LedgerEntryCount);
        Assert.Empty(section.Ledger);
        Assert.Equal(0, section.QuarantinedCount);
        Assert.Equal(PhaseStatus.Success, section.Status);
    }

    [Fact]
    public void JsonRoundTrip_PreservesIngestionLedgerProjection()
    {
        var section = InputResolutionSection.From(
            System.Array.Empty<InputResolutionDecision>(),
            new List<IngestionLedgerEntry>
            {
                LedgerEntry("QuarantinedPayload", IngestionStatus.Quarantined, referenced: "MissingType"),
            });
        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Generation = GenerationSection.From(NewReport()),
            InputResolution = section,
        };

        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter> { new StringEnumConverter() },
        };
        var json = JsonConvert.SerializeObject(manifest, settings);
        var parsed = JsonConvert.DeserializeObject<BindingArtifactManifest>(json, settings)!;

        // Enum members serialize as names, not integers.
        Assert.Contains("Quarantined", json);
        Assert.Contains("MalformedType", json);

        var ir = parsed.InputResolution!;
        Assert.Equal(1, ir.LedgerEntryCount);
        Assert.Equal(1, ir.QuarantinedCount);
        var entry = Assert.Single(ir.Ledger);
        Assert.Equal("Demo.Struct:QuarantinedPayload", entry.Input);
        Assert.Equal(IngestionStatus.Quarantined, entry.Status);
        Assert.Equal(IngestionDisposition.QuarantineType, entry.Disposition);
        Assert.Equal(IngestionPlane.Ingest, entry.Plane);
        Assert.Equal(IngestionCause.MalformedTypeRecord, entry.Cause);
        Assert.Equal("MissingType", entry.Referenced);
        Assert.Equal("closure proven complete", entry.Evidence);
    }

    [Fact]
    public void Store_WriteThenRead_ProducesEquivalentManifest()
    {
        using var temp = new TempDirectory();
        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Generation = GenerationSection.From(NewReport()),
        };

        BindingArtifactManifestStore.Write(manifest, temp.Path, NullLogger.Instance);

        Assert.True(File.Exists(Path.Combine(temp.Path, BindingArtifactManifestStore.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(temp.Path, BindingArtifactManifestStore.ReportFileName)));

        var read = BindingArtifactManifestStore.TryRead(temp.Path)!;
        Assert.Equal("Demo", read.Module);
        Assert.Equal(ManifestStatus.Complete, read.Status);
        Assert.NotNull(read.Generation);
    }

    [Fact]
    public void Store_StatusComplete_OnlyWhenGenerationPresent()
    {
        using var temp = new TempDirectory();
        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Wrapper = new WrapperSection { Status = PhaseStatus.Success },
        };

        BindingArtifactManifestStore.Write(manifest, temp.Path, NullLogger.Instance);

        var read = BindingArtifactManifestStore.TryRead(temp.Path)!;
        Assert.Equal(ManifestStatus.Partial, read.Status);
        Assert.NotNull(read.PartialReason);
    }

    [Fact]
    public void Store_OrphanedReportWithoutManifest_Throws()
    {
        // ReadModifyWrite is the wrapper/bridge-phase entry point — those phases don't own
        // the output dir, so a stray report without a manifest is corruption to them.
        using var temp = new TempDirectory();
        File.WriteAllText(
            Path.Combine(temp.Path, BindingArtifactManifestStore.ReportFileName),
            "{\"moduleName\":\"Demo\"}");

        Assert.Throws<InvalidDataException>(() =>
            BindingArtifactManifestStore.ReadModifyWrite(
                temp.Path, "Demo", _ => { }, NullLogger.Instance));
    }

    [Fact]
    public void Store_DirectWrite_OverwritesOrphanReport()
    {
        // Generation phase OWNS the output dir; a pre-M1 binding-report.json without a
        // manifest is normal and must be overwritten cleanly. Direct Write is the
        // generation-phase entry point and bypasses the orphan-report failure.
        using var temp = new TempDirectory();
        File.WriteAllText(
            Path.Combine(temp.Path, BindingArtifactManifestStore.ReportFileName),
            "{\"moduleName\":\"Stale\"}");

        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Generation = GenerationSection.From(NewReport()),
        };
        BindingArtifactManifestStore.Write(manifest, temp.Path, NullLogger.Instance);

        Assert.True(File.Exists(Path.Combine(temp.Path, BindingArtifactManifestStore.ManifestFileName)));
        var rederived = JsonConvert.DeserializeObject<BindingReport>(
            File.ReadAllText(Path.Combine(temp.Path, BindingArtifactManifestStore.ReportFileName)),
            new JsonSerializerSettings { Converters = new List<JsonConverter> { new StringEnumConverter() } })!;
        Assert.Equal("Demo", rederived.ModuleName);
    }

    [Fact]
    public void Store_ModuleMismatch_Throws()
    {
        using var temp = new TempDirectory();
        var manifest = new BindingArtifactManifest
        {
            Module = "ModuleA",
            Generation = GenerationSection.From(NewReport("ModuleA")),
        };
        BindingArtifactManifestStore.Write(manifest, temp.Path, NullLogger.Instance);

        var ex = Assert.Throws<InvalidDataException>(() =>
            BindingArtifactManifestStore.ReadModifyWrite(
                temp.Path, "ModuleB",
                m => m.Wrapper = new WrapperSection { Status = PhaseStatus.Success },
                NullLogger.Instance));
        Assert.Contains("ModuleA", ex.Message);
        Assert.Contains("ModuleB", ex.Message);
    }

    [Fact]
    public void Store_StandalonePartial_PopulatesGeneratorVersion()
    {
        using var temp = new TempDirectory();
        var manifest = BindingArtifactManifestStore.ReadModifyWrite(
            temp.Path, "Demo",
            m => m.Wrapper = new WrapperSection { Status = PhaseStatus.Success },
            NullLogger.Instance);

        Assert.NotNull(manifest.GeneratorVersion);
        Assert.NotEmpty(manifest.GeneratorVersion);
    }

    [Fact]
    public void Projection_PopulatesRecommendedWorkaround()
    {
        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Generation = GenerationSection.From(NewReport()),
            Wrapper = new WrapperSection(),
        };
        ((List<CoGatedMember>)manifest.Wrapper!.CSharpCoGatedMembers).Add(
            Heuristic("Foo", "Demo.X", 0));

        var report = BindingReportProjection.Project(manifest);

        Assert.Single(report.SkippedItems);
        var item = report.SkippedItems[0];
        Assert.Equal(
            WorkaroundRecommendations.GetRecommendation(SkipReason.MissingWrapperSymbol),
            item.RecommendedWorkaround);
        Assert.NotNull(item.RecommendedWorkaround);
    }

    [Fact]
    public void Store_NoExistingFiles_TreatsAsStandalonePartial()
    {
        using var temp = new TempDirectory();

        var manifest = BindingArtifactManifestStore.ReadModifyWrite(
            temp.Path, "Demo",
            m => m.Wrapper = new WrapperSection { Status = PhaseStatus.Success },
            NullLogger.Instance,
            partialReasonWhenNew: "standalone wrapper compile");

        Assert.Equal(ManifestStatus.Partial, manifest.Status);
        Assert.Equal("standalone wrapper compile", manifest.PartialReason);
        Assert.NotNull(manifest.Wrapper);
        Assert.Null(manifest.Generation);
    }

    [Fact]
    public void Store_CorruptManifest_ThrowsInvalidData()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(
            Path.Combine(temp.Path, BindingArtifactManifestStore.ManifestFileName),
            "{ this is not valid json");

        Assert.Throws<InvalidDataException>(() =>
            BindingArtifactManifestStore.TryRead(temp.Path));
    }

    [Fact]
    public void Store_AtomicWrite_LeavesNoTempFiles()
    {
        using var temp = new TempDirectory();
        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Generation = GenerationSection.From(NewReport()),
        };

        BindingArtifactManifestStore.Write(manifest, temp.Path, NullLogger.Instance);

        // No leftover .tmp files from the same-dir-temp + rename dance.
        var stragglers = Directory.GetFiles(temp.Path, "*.tmp");
        Assert.Empty(stragglers);
    }

    [Fact]
    public async Task Store_ConcurrentWrites_SameOutputDirectory_AllSucceed()
    {
        // Two generator invocations legitimately target one output directory at the same time:
        // a parallel build matrix regenerates the same RID-agnostic dependency project
        // (obj/<cfg>/<tfm>/swift-binding/) from two cells at once. With a fixed temp file name
        // the second writer's exclusive create failed — "the process cannot access the file
        // ... because it is being used by another process" — and took the generator down.
        using var temp = new TempDirectory();
        const int Writers = 8;
        const int Rounds = 12;

        using var gate = new Barrier(Writers);
        var failures = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, Writers).Select(_ => RunOnDedicatedThread(() =>
        {
            gate.SignalAndWait();
            for (var round = 0; round < Rounds; round++)
            {
                try
                {
                    BindingArtifactManifestStore.Write(
                        new BindingArtifactManifest
                        {
                            Module = "Demo",
                            Generation = GenerationSection.From(NewReport()),
                        },
                        temp.Path,
                        NullLogger.Instance);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(failures.IsEmpty, DescribeFailures(failures));

        // Interleaved writers are last-writer-wins — identical inputs through a deterministic
        // generator produce identical bytes — so what has to hold is that both files ended up
        // whole and describing the module that was written.
        var manifest = BindingArtifactManifestStore.TryRead(temp.Path)!;
        Assert.Equal("Demo", manifest.Module);
        Assert.Equal(ManifestStatus.Complete, manifest.Status);
        Assert.Equal("Demo", ReadReport(temp.Path).ModuleName);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task Store_ConcurrentReadModifyWrite_SameOutputDirectory_AllSucceed()
    {
        // The wrapper/bridge phases go through ReadModifyWrite, which reads the manifest,
        // mutates its own section and writes the pair back. Concurrent cycles must complete
        // rather than fault; the sections they contribute race, but every write is whole.
        using var temp = new TempDirectory();
        BindingArtifactManifestStore.Write(
            new BindingArtifactManifest
            {
                Module = "Demo",
                Generation = GenerationSection.From(NewReport()),
            },
            temp.Path,
            NullLogger.Instance);

        const int Writers = 8;
        const int Rounds = 12;
        using var gate = new Barrier(Writers);
        var failures = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, Writers).Select(_ => RunOnDedicatedThread(() =>
        {
            gate.SignalAndWait();
            for (var round = 0; round < Rounds; round++)
            {
                try
                {
                    BindingArtifactManifestStore.ReadModifyWrite(
                        temp.Path, "Demo",
                        m => m.Wrapper = new WrapperSection { Status = PhaseStatus.Success },
                        NullLogger.Instance);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(failures.IsEmpty, DescribeFailures(failures));

        var manifest = BindingArtifactManifestStore.TryRead(temp.Path)!;
        Assert.Equal("Demo", manifest.Module);
        Assert.NotNull(manifest.Generation);
        Assert.NotNull(manifest.Wrapper);
        Assert.Equal("Demo", ReadReport(temp.Path).ModuleName);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task Store_ReadDuringConcurrentWrites_NeverObservesAPartialFile()
    {
        // The temp+rename dance exists so a reader racing a writer sees either the previous
        // whole file or the next one, never a half-flushed one. Unique temp names must not
        // cost that: the rename is still the only thing that ever touches the final path.
        using var temp = new TempDirectory();
        const int Writers = 4;
        const int Rounds = 30;

        using var gate = new Barrier(Writers + 1);
        var writerFailures = new ConcurrentBag<Exception>();
        var readerFailures = new ConcurrentBag<Exception>();
        using var writersDone = new CancellationTokenSource();

        var readerTask = RunOnDedicatedThread(() =>
        {
            gate.SignalAndWait();
            while (!writersDone.IsCancellationRequested)
            {
                try
                {
                    var manifest = BindingArtifactManifestStore.TryRead(temp.Path);
                    if (manifest != null)
                        Assert.Equal("Demo", manifest.Module);
                    if (File.Exists(Path.Combine(temp.Path, BindingArtifactManifestStore.ReportFileName)))
                        Assert.Equal("Demo", ReadReport(temp.Path).ModuleName);
                }
                catch (Exception ex)
                {
                    readerFailures.Add(ex);
                }
            }
        });

        var writerTasks = Enumerable.Range(0, Writers).Select(_ => RunOnDedicatedThread(() =>
        {
            gate.SignalAndWait();
            for (var round = 0; round < Rounds; round++)
            {
                try
                {
                    BindingArtifactManifestStore.Write(
                        new BindingArtifactManifest
                        {
                            Module = "Demo",
                            Generation = GenerationSection.From(NewReport()),
                        },
                        temp.Path,
                        NullLogger.Instance);
                }
                catch (Exception ex)
                {
                    writerFailures.Add(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(writerTasks);
        writersDone.Cancel();
        await readerTask;

        Assert.True(writerFailures.IsEmpty, DescribeFailures(writerFailures));
        Assert.True(readerFailures.IsEmpty, DescribeFailures(readerFailures));
    }

    [Fact]
    public void Store_FailedWrite_DoesNotLeaveItsTempBehind()
    {
        // Unique temp names would accumulate one orphan per failed write if the failure path
        // did not clean up. A directory squatting on the manifest's name makes the rename fail
        // deterministically, after the temp has already been created.
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, BindingArtifactManifestStore.ManifestFileName));

        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Generation = GenerationSection.From(NewReport()),
        };

        Assert.ThrowsAny<IOException>(() =>
            BindingArtifactManifestStore.Write(manifest, temp.Path, NullLogger.Instance));

        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void Store_Write_ReclaimsAbandonedTempsButNotInFlightOnes()
    {
        // A process killed between creating its temp and renaming it leaves the temp behind.
        // Later writes reclaim those, but only once they are far older than any plausible
        // write — a peer process still filling its temp must never have it deleted underneath.
        using var temp = new TempDirectory();
        var reportName = BindingArtifactManifestStore.ReportFileName;

        var abandoned = Path.Combine(temp.Path, $"{reportName}.424242-1.tmp");
        var legacyAbandoned = Path.Combine(temp.Path, $"{reportName}.tmp");
        var inFlight = Path.Combine(temp.Path, $"{reportName}.535353-1.tmp");
        foreach (var path in new[] { abandoned, legacyAbandoned, inFlight })
            File.WriteAllText(path, "{}");
        foreach (var path in new[] { abandoned, legacyAbandoned })
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));

        BindingArtifactManifestStore.Write(
            new BindingArtifactManifest
            {
                Module = "Demo",
                Generation = GenerationSection.From(NewReport()),
            },
            temp.Path,
            NullLogger.Instance);

        Assert.False(File.Exists(abandoned));
        Assert.False(File.Exists(legacyAbandoned));
        Assert.True(File.Exists(inFlight));
        Assert.Equal("Demo", ReadReport(temp.Path).ModuleName);
    }

    // Every participant blocks on a Barrier, so they need threads that cannot be starved by
    // each other; the default pool would inject them only after a delay.
    private static Task RunOnDedicatedThread(Action body) =>
        Task.Factory.StartNew(body, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static BindingReport ReadReport(string outputDirectory) =>
        JsonConvert.DeserializeObject<BindingReport>(
            File.ReadAllText(Path.Combine(outputDirectory, BindingArtifactManifestStore.ReportFileName)),
            new JsonSerializerSettings { Converters = new List<JsonConverter> { new StringEnumConverter() } })
        ?? throw new InvalidDataException("Report deserialized to null.");

    private static string DescribeFailures(ConcurrentBag<Exception> failures) =>
        failures.IsEmpty
            ? string.Empty
            : $"{failures.Count} failure(s) across " +
              $"[{string.Join(", ", failures.Select(f => f.GetType().Name).Distinct().Order())}]; " +
              $"first: {failures.First()}";

    [Fact]
    public void WrapperSection_From_OutcomeSeverity_MapsToPhaseStatus()
    {
        // Compile exception = Fatal per EvaluateResult (fail-closed in every mode).
        var fatal = WrapperBuildOutcome.From(
            compilationResult: null,
            compilationException: new InvalidOperationException("boom"));
        var fatalSection = WrapperSection.From(fatal, Array.Empty<CoGatedMember>());
        Assert.Equal(PhaseStatus.Fatal, fatalSection.Status);

        // Null result = no swift files = Success.
        var ok = WrapperBuildOutcome.From(
            compilationResult: null,
            compilationException: null);
        var okSection = WrapperSection.From(ok, Array.Empty<CoGatedMember>());
        Assert.Equal(PhaseStatus.Success, okSection.Status);
    }

    [Fact]
    public void WrapperSection_From_ReconciliationFailure_RecordsPhaseAsFatal()
    {
        // The wrapper itself compiled, but the generated C# could not be made sound against it.
        // The manifest is the authoritative artifact record, so it must not report a phase that
        // produced an unusable binding as a success — the surviving wrapper on disk is exactly
        // what makes this case easy to log as green.
        using var temp = new TempDirectory();
        var built = Path.Combine(temp.Path, "Wrapper.xcframework");
        Directory.CreateDirectory(built);

        var result = new SwiftWrapperCompilationResult
        {
            XCFrameworkPath = built,
            CompiledFileCount = 3,
            StrippedBlockCount = 1,
            SliceCount = 1,
            StrippedSymbols = new HashSet<string> { "$sBroken" },
        };
        var outcome = WrapperBuildOutcome.From(result, compilationException: null);

        // Sanity: without the reconciliation failure this is a clean success.
        var clean = WrapperSection.From(outcome, Array.Empty<CoGatedMember>());
        Assert.Equal(PhaseStatus.Success, clean.Status);
        Assert.True(clean.WrapperXcfwExists);

        var failed = WrapperSection.From(
            outcome, Array.Empty<CoGatedMember>(), "SWIFTBIND057: 'Gauge.Level' could not be rewritten.");

        Assert.Equal(PhaseStatus.Fatal, failed.Status);
        Assert.NotEqual(0, failed.ExitCode);
        Assert.Equal("SWIFTBIND057", failed.DiagnosticCode);
        Assert.Contains("Gauge.Level", failed.Message);
        // The SDK gate reads this flag; leaving it true would let the binding ship.
        Assert.False(failed.WrapperXcfwExists);
    }

    [Fact]
    public void WrapperSection_From_CompileAndReconciliationBothFailed_CodeAndMessageAgree()
    {
        // An unmet explicit architecture contract is fatal while still leaving a compiled primary
        // on disk, so reconciliation runs anyway and can fail on top of it. The machine-readable
        // diagnostic and the human-readable message must describe the same failure, and neither
        // failure may be dropped.
        var result = new SwiftWrapperCompilationResult
        {
            XCFrameworkPath = "/tmp/does-not-exist.xcframework",
            CompiledFileCount = 3,
            StrippedBlockCount = 1,
            SliceCount = 1,
        };
        var outcome = WrapperBuildOutcome.From(
            result, compilationException: null, contractualUnmetArchitectures: new[] { "x86_64" });
        Assert.True(outcome.IsFatal);
        Assert.NotNull(outcome.DiagnosticCode);

        var section = WrapperSection.From(
            outcome, Array.Empty<CoGatedMember>(), "SWIFTBIND057: 'Gauge.Level' could not be rewritten.");

        Assert.Equal(PhaseStatus.Fatal, section.Status);
        // The compile's own diagnostic stays authoritative, and its message survives alongside
        // the reconciliation failure rather than being overwritten by it.
        Assert.Equal(outcome.DiagnosticCode, section.DiagnosticCode);
        Assert.Contains("Gauge.Level", section.Message);
        Assert.Contains(outcome.Message, section.Message);
        Assert.False(section.WrapperXcfwExists);
    }

    [Fact]
    public void WrapperSection_From_StrippedSymbols_AreOrdinalSorted()
    {
        var result = new SwiftWrapperCompilationResult
        {
            XCFrameworkPath = "/tmp/none.xcframework",
            CompiledFileCount = 4,
            StrippedBlockCount = 1,
            SliceCount = 1,
            StrippedSymbols = new HashSet<string> { "$sZ", "$sA", "$sM" },
        };
        var outcome = WrapperBuildOutcome.From(result, compilationException: null);
        var section = WrapperSection.From(outcome, Array.Empty<CoGatedMember>());

        Assert.Equal(new[] { "$sA", "$sM", "$sZ" }, section.StrippedSymbols);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* test cleanup */ }
        }
    }
}
