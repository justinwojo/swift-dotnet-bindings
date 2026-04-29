// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

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
        // GRDB-style: 18 emitted methods, 18 cogated → 0 emitted, 18 distinct skipped items.
        // Wrapper-side capture produces Heuristic identities — public C# decls have no
        // 1:1 mangled wrapper symbol; the wrapper that triggered the cascade is an
        // internal trampoline.
        var report = new BindingReport { ModuleName = "GRDB" };
        report.TotalMembers = 18;
        report.EmittedMembers = 18;
        report.EmittedMembersByKind[BindingItemKind.Method] = 18;

        var wrapper = new WrapperSection { Status = PhaseStatus.Success };
        for (int i = 0; i < 18; i++)
            ((List<CoGatedMember>)wrapper.CSharpCoGatedMembers).Add(
                Heuristic($"M{i}", "GRDB.Db", i));

        var manifest = new BindingArtifactManifest
        {
            Module = "GRDB",
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
    public void Projection_SuppressedProxyMethods_CarryProxyReason()
    {
        var manifest = new BindingArtifactManifest
        {
            Module = "Demo",
            Generation = GenerationSection.From(NewReport()),
            ProxyCoGating = new ProxyCoGatingSection { SuppressedProxyClassCount = 1 },
        };
        ((List<CoGatedMember>)manifest.ProxyCoGating.CoGatedMethods).Add(
            Heuristic("Resolve", "Demo.Foo", 0));

        var report = BindingReportProjection.Project(manifest);

        Assert.Equal(17, report.EmittedMembers);
        Assert.Single(report.SkippedItems);
        Assert.Equal(SkipReason.SuppressedProxyMethodBody, report.SkippedItems[0].Reason);
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
            ProxyCoGating = new ProxyCoGatingSection { SuppressedProxyClassCount = 2 },
            Wrapper = new WrapperSection
            {
                Status = PhaseStatus.Warning,
                RawOutcome = "Warning",
                EffectiveOutcome = "Warning",
                ExitCode = 0,
                DiagnosticCode = "SWIFTBIND050",
                Message = "stripped 3 unsupported APIs",
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
        Assert.Equal(1, parsed.SchemaVersion);
        Assert.Equal("1.0.0", parsed.GeneratorVersion);
        Assert.NotNull(parsed.Generation);
        Assert.Equal(PhaseStatus.Warning, parsed.Wrapper!.Status);
        Assert.Equal("SWIFTBIND050", parsed.Wrapper.DiagnosticCode);
        Assert.Single(parsed.Wrapper.CSharpCoGatedMembers);
        Assert.Equal(IdentityConfidence.Mangled, parsed.Wrapper.CSharpCoGatedMembers[0].Confidence);
        Assert.Equal(PhaseStatus.NoOp, parsed.Bridge!.Status);
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
    public void WrapperSection_From_OutcomeSeverity_MapsToPhaseStatus()
    {
        // Exception + auto-wired async library = Fatal per EvaluateResult.
        var fatal = WrapperBuildOutcome.From(
            compilationResult: null,
            asyncLibraryAutoWired: true,
            sdkMode: false,
            compilationException: new InvalidOperationException("boom"));
        var fatalSection = WrapperSection.From(fatal, Array.Empty<CoGatedMember>());
        Assert.Equal(PhaseStatus.Fatal, fatalSection.Status);

        // Null result = no swift files = Success.
        var ok = WrapperBuildOutcome.From(
            compilationResult: null,
            asyncLibraryAutoWired: false,
            sdkMode: false,
            compilationException: null);
        var okSection = WrapperSection.From(ok, Array.Empty<CoGatedMember>());
        Assert.Equal(PhaseStatus.Success, okSection.Status);
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
        var outcome = WrapperBuildOutcome.From(result, asyncLibraryAutoWired: false, sdkMode: false, compilationException: null);
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
