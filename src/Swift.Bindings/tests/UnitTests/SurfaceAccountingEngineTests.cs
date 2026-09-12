// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace BindingsGeneration.Tests;

public class SurfaceAccountingEngineTests
{
    [Fact]
    public void Compare_EqualCountOverloadSubstitutionDoesNotCancel()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var request = fixture.Request(
            "public class C { public void A(int x) { } public void B(string x) { } }",
            "public class C { public void B(string x) { } public void C(long x) { } }");

        var result = SurfaceAccountingEngine.Analyze(request);

        Assert.True(result.Comparison.Summary.Complete);
        Assert.Equal(1, result.Comparison.Summary.PublicRemovals);
        Assert.Equal(1, result.Comparison.Summary.PublicAdditions);
        Assert.Contains(result.Comparison.Changes, c => c.Classification == "removal" && c.OldPublicKeys.Single().Contains("A(int)", StringComparison.Ordinal));
        Assert.Contains(result.Comparison.Changes, c => c.Classification == "addition" && c.TipPublicKeys.Single().Contains("C(long)", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_ReturnOnlyChangeAndSetterLossRemainVisible()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var request = fixture.Request(
            "public class C { public int M() => 0; public int Value { get; set; } }",
            "public class C { public long M() => 0; public int Value { get; } } ");

        var result = SurfaceAccountingEngine.Analyze(request);

        Assert.Equal(1, result.Comparison.Summary.ShapeChanges);
        Assert.Equal(1, result.Comparison.Summary.AccessorLosses);
        Assert.Contains(result.Comparison.Changes, c => c.Classification == "shape-change" && c.OldPublicKeys.Single().Contains("M()", StringComparison.Ordinal));
        Assert.Contains(result.Comparison.Changes, c => c.Classification == "accessor-loss" && c.Cause.Contains("set", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_ConcurrentTypeChangeAndSetterLossProduceBothChanges()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var request = fixture.Request(
            "public class C { public int Value { get; set; } }",
            "public class C { public long Value { get; } }");

        var result = SurfaceAccountingEngine.Analyze(request);

        Assert.Equal(1, result.Comparison.Summary.ShapeChanges);
        Assert.Equal(1, result.Comparison.Summary.AccessorLosses);
    }

    [Fact]
    public void Compare_DefaultValueChangeIsShapeChangeNotApiReplacement()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var request = fixture.Request(
            "public class C { public void M(int value = 1) { } }",
            "public class C { public void M(int value = 2) { } }");

        var result = SurfaceAccountingEngine.Analyze(request);

        Assert.Equal(1, result.Comparison.Summary.ShapeChanges);
        Assert.Equal(0, result.Comparison.Summary.PublicAdditions);
        Assert.Equal(0, result.Comparison.Summary.PublicRemovals);
    }

    [Fact]
    public void Compare_ManifestSymbolChurnIsRawChangeAndUnresolvedRetarget()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        const string oldSource = """
            using System.Runtime.InteropServices;
            public static partial class C
            {
                public static int M() => PInvoke_M();
                [LibraryImport("Native", EntryPoint = "SBW_Old")]
                private static partial int PInvoke_M();
            }
            """;
        const string tipSource = """
            using System.Runtime.InteropServices;
            public static partial class C
            {
                public static int M() => PInvoke_M();
                [LibraryImport("Native", EntryPoint = "SBW_New")]
                private static partial int PInvoke_M();
            }
            """;
        var request = fixture.Request(
            oldSource,
            tipSource,
            [("C.M()", "SBW_Old")],
            [("C.M()", "SBW_New")]);

        var result = SurfaceAccountingEngine.Analyze(request);

        Assert.Equal(1, result.Comparison.Summary.ManifestSymbolChanges);
        Assert.Contains(result.Comparison.Changes, c => c.Classification == "unresolved-retarget");
        Assert.False(result.Comparison.Summary.Complete);
    }

    [Fact]
    public void Compare_ExplicitOriginCrosswalkDistinguishesSymbolOnlyAndSemanticRetarget()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        const string oldSource = """
            using System.Runtime.InteropServices;
            public static partial class C
            {
                public static int M() => PInvoke_M();
                [LibraryImport("Native", EntryPoint = "SBW_Old")]
                private static partial int PInvoke_M();
            }
            """;
        const string tipSource = """
            using System.Runtime.InteropServices;
            public static partial class C
            {
                public static int M() => PInvoke_M();
                [LibraryImport("Native", EntryPoint = "SBW_New")]
                private static partial int PInvoke_M();
            }
            """;
        var request = fixture.Request(
            oldSource,
            tipSource,
            [("C.M()", "SBW_Old")],
            [("C.M()", "SBW_New")]);
        var unjoined = SurfaceAccountingEngine.Analyze(request);
        var oldMember = unjoined.Old.Members.Single(member => member.PublicKey.Name == "M");
        var tipMember = unjoined.Tip.Members.Single(member => member.PublicKey.Name == "M");
        var oldCrosswalk = Crosswalk("old", oldMember, "TestModule|C|Method|M|:Swift.Int|None||$sM|");
        var tipCrosswalk = Crosswalk("tip", tipMember, oldCrosswalk.OriginId);

        var symbolOnly = SurfaceAccountingEngine.Analyze(request with
        {
            OriginCrosswalks = [oldCrosswalk, tipCrosswalk],
        });
        var semantic = SurfaceAccountingEngine.Analyze(request with
        {
            OriginCrosswalks = [oldCrosswalk, tipCrosswalk with { OriginId = "TestModule|C|Method|N|:Swift.Int|None||$sN|" }],
        });

        Assert.Contains(symbolOnly.Comparison.Changes, change => change.Classification == "symbol-only");
        Assert.DoesNotContain(symbolOnly.Comparison.Changes, change => change.Classification == "unresolved-retarget");
        Assert.True(symbolOnly.Comparison.Summary.Complete);
        Assert.Equal(2, symbolOnly.Comparison.Crosswalks.Count);
        Assert.Contains(semantic.Comparison.Changes, change => change.Classification == "semantic-retarget");
    }

    [Fact]
    public void Compare_SameOriginDirectSwiftToCdeclIsRouteChange()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        const string oldSource = """
            using System.Runtime.InteropServices;
            public static partial class C
            {
                public static int M() => PInvoke_M();
                [LibraryImport("Native", EntryPoint = "$sDirect")]
                private static partial int PInvoke_M();
            }
            """;
        const string tipSource = """
            using System.Runtime.InteropServices;
            public static partial class C
            {
                public static int M() => PInvoke_M();
                [LibraryImport("Native", EntryPoint = "SBW_M")]
                private static partial int PInvoke_M();
            }
            """;
        var request = fixture.Request(
            oldSource,
            tipSource,
            [("C.M()", "$sDirect")],
            [("C.M()", "SBW_M")]);
        var unjoined = SurfaceAccountingEngine.Analyze(request);
        var oldMember = unjoined.Old.Members.Single(member => member.PublicKey.Name == "M");
        var tipMember = unjoined.Tip.Members.Single(member => member.PublicKey.Name == "M");
        const string originId = "TestModule|C|Method|M|:Swift.Int|None||$sDirect|";

        var result = SurfaceAccountingEngine.Analyze(request with
        {
            OriginCrosswalks = [Crosswalk("old", oldMember, originId), Crosswalk("tip", tipMember, originId)],
        });

        Assert.Contains(result.Comparison.Changes, change => change.Classification == "route-change");
        Assert.DoesNotContain(result.Comparison.Changes, change => change.Classification == "unresolved-retarget");
        Assert.True(result.Comparison.Summary.Complete);
    }

    [Fact]
    public void Compare_SameOriginNonRouteContractChangeRemainsUnresolved()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        const string oldSource = """
            using System.Runtime.InteropServices;
            public static partial class C
            {
                public static int M() => PInvoke_M();
                [LibraryImport("OldNative", EntryPoint = "SBW_M")]
                private static partial int PInvoke_M();
            }
            """;
        const string tipSource = """
            using System.Runtime.InteropServices;
            public static partial class C
            {
                public static int M() => PInvoke_M();
                [LibraryImport("NewNative", EntryPoint = "SBW_M")]
                private static partial int PInvoke_M();
            }
            """;
        var request = fixture.Request(
            oldSource,
            tipSource,
            [("C.M()", "SBW_M")],
            [("C.M()", "SBW_M")]);
        var unjoined = SurfaceAccountingEngine.Analyze(request);
        var oldMember = unjoined.Old.Members.Single(member => member.PublicKey.Name == "M");
        var tipMember = unjoined.Tip.Members.Single(member => member.PublicKey.Name == "M");
        const string originId = "TestModule|C|Method|M|:Swift.Int|None||$sM|";

        var result = SurfaceAccountingEngine.Analyze(request with
        {
            OriginCrosswalks = [Crosswalk("old", oldMember, originId), Crosswalk("tip", tipMember, originId)],
        });

        Assert.Contains(result.Comparison.Changes, change => change.Classification == "unresolved-retarget");
        Assert.DoesNotContain(result.Comparison.Changes, change => change.Classification == "route-change");
        Assert.False(result.Comparison.Summary.Complete);
    }

    [Fact]
    public void Compare_ManifestRowAdditionOrRemovalRemainsVisible()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        const string source = "public class C { public int M() => 0; }";
        var request = fixture.Request(source, source, [("C.M()", "SBW_M")], []);

        var result = SurfaceAccountingEngine.Analyze(request);

        Assert.Equal(1, result.Comparison.Summary.ManifestSymbolChanges);
        Assert.Contains(result.Comparison.Changes, change =>
            change.Classification == "manifest-symbol-change"
            && change.Cause.Contains("SBW_M -> <missing>", StringComparison.Ordinal));
    }

    [Fact]
    public void ManifestJoinMatchesIndexerAndGenericProducerKeys()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        const string source = """
            public class C<TOuter>
            {
                public int this[int index] => index;
                public void Create<TMethod>(TOuter outer, TMethod method) { }
            }
            """;
        var manifest = new[]
        {
            ("C.this[int]", "SBW_Index"),
            ("C.Create(TOuter,TMethod)`2", "SBW_Create"),
        };
        var request = fixture.Request(source, source, manifest, manifest);

        var result = SurfaceAccountingEngine.Analyze(request);
        var coverage = Assert.Single(result.Old.Coverage);

        Assert.Equal(2, coverage.ManifestMatched);
        Assert.Equal(0, coverage.ManifestPhantom);
        Assert.True(result.Comparison.Summary.Complete);
    }

    [Fact]
    public void Withdrawals_PreserveDuplicateDisplayNamesAsDistinctCanonicalRoots()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        const string first = "TestModule|C|Method|buildEither|:T,first:T|None|<T>|$sfirst|!leaf-api";
        const string second = "TestModule|C|Method|buildEither|:T,second:T|None|<T>|$ssecond|!leaf-api";
        const string display = "TestModule.C.buildEither (leaf-api)";
        var request = fixture.Request(
            "public class C { }",
            "public class C { }",
            oldWithdrawals: [(display, first), (display, second)],
            tipWithdrawals: [(display, second)]);

        var result = SurfaceAccountingEngine.Analyze(request);
        var oldRows = result.Old.Withdrawals;

        Assert.Equal(2, oldRows.Count);
        Assert.Equal(2, oldRows.Select(w => w.CanonicalRecoveryId).Distinct().Count());
        Assert.All(oldRows, w => Assert.Equal("resolved-by-multiset", w.ResolutionStatus));
        Assert.Equal(1, result.Comparison.Summary.WithdrawalRemovals);
        Assert.Contains(result.Comparison.Changes, c => c.Classification == "withdrawal-removal" && c.OriginIds.Contains(first[..first.LastIndexOf('!')]));
    }

    [Fact]
    public void Withdrawals_MultiplicityMismatchStaysUnresolved()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        const string root = "TestModule|C|Method|buildEither||None|||!leaf-api";
        const string display = "TestModule.C.buildEither (leaf-api)";
        fixture.Request(
            "public class C { }",
            "public class C { }",
            oldWithdrawals: [(display, root), (display, root)]);
        var sidecars = SurfaceSidecarReader.Read(fixture.OldDirectory);

        var rows = SurfaceSidecarReader.ReconcileWithdrawals("old", SurfaceAccountingTestFixture.TargetKey, sidecars);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Null(row.CanonicalRecoveryId);
            Assert.Equal("unresolved-multiplicity", row.ResolutionStatus);
        });
    }

    [Fact]
    public void ManifestHoleAndPhantomAreReportedSeparately()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        const string source = """
            using System.Runtime.InteropServices;
            public static partial class C
            {
                public static int M() => PInvoke_M();
                public static int Unrecorded => 42;
                [LibraryImport("Native", EntryPoint = "SBW_M")]
                private static partial int PInvoke_M();
            }
            """;
        var request = fixture.Request(
            source,
            source,
            [("C.M()", "SBW_M"), ("C.Phantom()", "SBW_Phantom")],
            [("C.M()", "SBW_M"), ("C.Phantom()", "SBW_Phantom")]);

        var result = SurfaceAccountingEngine.Analyze(request);
        var coverage = Assert.Single(result.Old.Coverage);

        Assert.Equal(1, coverage.ManifestMatched);
        Assert.Equal(1, coverage.ManifestPhantom);
        Assert.True(coverage.ManifestUnrecorded >= 2); // type + direct property
        Assert.Equal("matched-source-and-call-edge", result.Old.Members.Single(m => m.PublicKey.Name == "M").ApiManifest.Single().MatchStatus);
    }

    [Fact]
    public void MissingApiManifestFailsCoverageEvenWithoutNativeImports()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var request = fixture.Request("public class C { }", "public class C { }");
        File.Delete(Path.Combine(fixture.OldDirectory, "TestModule.api-manifest.json"));

        var result = SurfaceAccountingEngine.Analyze(request);

        Assert.Contains(result.Old.Coverage.Single().Errors, error => error.Contains("missing API manifest", StringComparison.Ordinal));
        Assert.Equal(0, result.Comparison.Summary.ComparableTargets);
        Assert.False(result.Comparison.Summary.Complete);
    }

    [Fact]
    public void FailedStageWithLeftoverArtifactsIsNotCompared()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var request = fixture.Request(
            "public class OldOnly { }",
            "public class TipOnly { }",
            oldComplete: false);

        var result = SurfaceAccountingEngine.Analyze(request);

        Assert.Equal(0, result.Comparison.Summary.ComparableTargets);
        Assert.Empty(result.Comparison.Changes);
        Assert.False(result.Comparison.Summary.Complete);
    }

    [Fact]
    public void InventoryUsesCapturedProjectCompileItemsIncludingNestedSources()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var request = fixture.Request("public class IgnoredOld { }", "public class IgnoredTip { }");
        foreach (var directory in new[] { fixture.OldDirectory, fixture.TipDirectory })
        {
            var nested = Path.Combine(directory, "Generated");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "Surface.cs"), "public class Included { public int Value => 1; }");
            File.WriteAllText(Path.Combine(directory, "TestModule.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
                  <ItemGroup><Compile Include="Generated/**/*.cs" /></ItemGroup>
                </Project>
                """);
        }

        var result = SurfaceAccountingEngine.Analyze(request);

        Assert.Contains(result.Old.Members, m => m.PublicKey.Name == "Included");
        Assert.DoesNotContain(result.Old.Members, m => m.PublicKey.Name == "IgnoredOld");
        Assert.Contains(result.Old.Files, f => f.RelativePath == "Generated/Surface.cs");
    }

    [Fact]
    public void RequestValidationRejectsTargetReceiptHolesAndHashDrift()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var request = fixture.Request("public class C { }", "public class C { }");
        var missingReceipt = request with
        {
            TipCapture = request.TipCapture with { TargetStages = [] },
        };
        Assert.Throws<InvalidDataException>(() => SurfaceAccountingEngine.ValidateRequest(missingReceipt));

        var badHash = request with { ManifestSha256 = new string('0', 64) };
        Assert.Throws<InvalidDataException>(() => SurfaceAccountingEngine.ValidateRequest(badHash));
    }

    [Theory]
    [InlineData("ios", "iphoneos", "arm64", "arm64-apple-ios15.0")]
    [InlineData("ios", "iphonesimulator", "x86_64", "x86_64-apple-ios15.0-simulator")]
    [InlineData("macos", "macosx", "arm64", "arm64-apple-macos12.0")]
    [InlineData("macos", "macosx", "x86_64", "x86_64-apple-macosx12.0")]
    [InlineData("maccatalyst", "macosx", "arm64", "arm64-apple-ios15.0-macabi")]
    [InlineData("tvos", "appletvos", "arm64", "arm64-apple-tvos15.0")]
    [InlineData("tvos", "appletvsimulator", "arm64", "arm64-apple-tvos15.0-simulator")]
    public void TargetProvenanceAcceptsCanonicalAppleSdkAndTriple(
        string platform,
        string sdk,
        string architecture,
        string triple)
        => SurfaceAccountingEngine.ValidateTargetProvenance(TargetProvenance(platform, sdk, architecture, triple));

    [Theory]
    [InlineData("ios", "ios18", "arm64", "arm64-apple-ios18.0")]
    [InlineData("ios", "iphoneos", "arm64", "arm64-apple-ios18.0-simulator")]
    [InlineData("ios", "iphoneos", "arm64", "x86_64-apple-ios18.0")]
    [InlineData("ios", "macosx", "arm64", "arm64-apple-macos18.0")]
    [InlineData("maccatalyst", "macosx", "arm64", "arm64-apple-ios18.0")]
    [InlineData("ios", "iphoneos", "arm64", "arm64-apple-ios18.0-macabi")]
    public void TargetProvenanceRejectsAliasesAndInconsistentTriples(
        string platform,
        string sdk,
        string architecture,
        string triple)
        => Assert.Throws<InvalidDataException>(() =>
            SurfaceAccountingEngine.ValidateTargetProvenance(TargetProvenance(platform, sdk, architecture, triple)));

    [Fact]
    public void ToolchainDriftMakesEveryTargetIncomparable()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var request = fixture.Request(
            "public class C { }",
            "public class C { }",
            tipToolchainHash: new string('d', 64));

        var result = SurfaceAccountingEngine.Analyze(request);

        Assert.False(result.Comparison.Summary.ToolchainEquivalent);
        Assert.Equal(0, result.Comparison.Summary.ComparableTargets);
        Assert.False(result.Comparison.Summary.Complete);
    }

    [Fact]
    public void StrictJsonRejectsDuplicateProperties()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var path = Path.Combine(fixture.Root, "duplicate.json");
        File.WriteAllText(path, "{\"module\":\"A\",\"module\":\"B\"}");

        var exception = Assert.Throws<InvalidDataException>(() => SurfaceCanonicalJson.ParseStrictFile(path));

        Assert.Contains("duplicate JSON property 'module'", exception.Message);
    }

    [Fact]
    public void WriterProducesDeterministicVersionedArtifactSet()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var request = fixture.Request("public class C { public int Value => 1; }", "public class C { public int Value => 1; }");
        var result = SurfaceAccountingEngine.Analyze(request);
        var schemaPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../../../build/SurfaceAccounting/schema.surface-accounting-1.json"));
        var first = Path.Combine(fixture.Root, "artifacts-a");
        var second = Path.Combine(fixture.Root, "artifacts-b");

        SurfaceAccountingWriter.Write(result, first, schemaPath);
        SurfaceAccountingWriter.Write(result, second, schemaPath);

        var firstFiles = Directory.EnumerateFiles(first, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(first, path)).Order().ToList();
        var secondFiles = Directory.EnumerateFiles(second, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(second, path)).Order().ToList();
        Assert.Equal(firstFiles, secondFiles);
        Assert.All(firstFiles, relative => Assert.Equal(
            File.ReadAllBytes(Path.Combine(first, relative)),
            File.ReadAllBytes(Path.Combine(second, relative))));
        Assert.Contains("schema.json", firstFiles);
        Assert.Contains(firstFiles, path => path.EndsWith("/changes.jsonl", StringComparison.Ordinal));
        Assert.Contains(firstFiles, path => path.EndsWith("/summary.md", StringComparison.Ordinal));

        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "schema.json")));
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
        Assert.Throws<IOException>(() => SurfaceAccountingWriter.Write(result, first, schemaPath));
    }

    private static SurfaceOriginCrosswalk Crosswalk(
        string side,
        SurfaceMemberObservation member,
        string originId)
        => new()
        {
            Side = side,
            TargetKey = member.TargetKey,
            SurfaceLane = member.SurfaceLane,
            PublicKey = member.PublicKey,
            OriginId = originId,
            OriginShape = "fixture-origin-shape",
            Evidence = member.SourceReferences.Single(),
        };

    private static SurfaceTargetRequest TargetProvenance(
        string platform,
        string sdk,
        string architecture,
        string triple)
        => new()
        {
            Key = SurfaceAccountingTestFixture.TargetKey with { Platform = platform },
            OldDirectory = "old",
            TipDirectory = "tip",
            ActualSdk = sdk,
            Architecture = architecture,
            TargetTriple = triple,
        };
}
