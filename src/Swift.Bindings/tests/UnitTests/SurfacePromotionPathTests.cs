// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The formal validation-baseline promotion path: validate's own surface capture, the request
/// built from two validate output bases, the live-manifest roster, the receipt's binding to the
/// candidate, and Promote()'s provenance writes.
/// </summary>
public class SurfacePromotionPathTests
{
    private const string OldCommit = "1111111111111111111111111111111111111111";
    private const string TipCommit = "2222222222222222222222222222222222222222";
    private const string Source = "public class C { public void M() { } }";

    // --- Capture builder ---

    [Fact]
    public void Capture_CleanPassingRunIsCompleteAndHashesEveryTarget()
    {
        using var fixture = new PromotionPathFixture();
        var capture = fixture.WriteCapture(fixture.TipBase, TipCommit);

        Assert.True(capture.Capture.Complete);
        Assert.Empty(capture.Capture.IncompletenessReasons);
        Assert.Null(capture.Capture.DirtyPatchSha256);
        Assert.Equal(TipCommit, capture.Capture.SourceSha);
        Assert.Equal(PromotionPathFixture.TargetNames, capture.Capture.TargetStages.Select(s => s.TargetName));
        foreach (var stage in capture.Capture.TargetStages)
        {
            Assert.Equal(("success", "success", "success"), (stage.Generation, stage.CSharpCompile, stage.SwiftCompile));
            Assert.Equal(SurfaceAccountingEngine.HashDirectoryTree(Path.Combine(fixture.TipBase, stage.TargetName)), stage.OutputTreeSha256);
        }
        Assert.All(capture.Capture.Commands, command =>
        {
            Assert.Equal(0, command.ExitCode);
            Assert.Equal(SurfaceCanonicalJson.Sha256File(Path.Combine(fixture.TipBase, command.LogRelativePath)), command.LogSha256);
        });
        Assert.Equal(SurfaceCanonicalJson.Sha256File(Path.Combine(fixture.TipBase, "input-lock.json")), capture.Capture.InputLockSha256);
        Assert.Equal(SurfaceCanonicalJson.Hash(capture), SurfaceCanonicalJson.Hash(SurfaceCaptureBuilder.Read(fixture.TipBase)));
    }

    [Fact]
    public void Capture_DirtyCheckoutIsIncompleteAndRecordsThePatch()
    {
        using var fixture = new PromotionPathFixture();
        var capture = fixture.WriteCapture(fixture.TipBase, TipCommit, porcelain: " M build/Build.cs\n");

        Assert.False(capture.Capture.Complete);
        Assert.Contains(capture.Capture.IncompletenessReasons, r => r.Contains("dirty", StringComparison.Ordinal));
        Assert.NotNull(capture.Capture.DirtyPatchSha256);
    }

    [Fact]
    public void Capture_HeadMovingDuringTheRunIsIncomplete()
    {
        using var fixture = new PromotionPathFixture();
        var capture = fixture.WriteCapture(fixture.TipBase, TipCommit, endCommit: OldCommit);

        Assert.False(capture.Capture.Complete);
        Assert.Contains(capture.Capture.IncompletenessReasons, r => r.Contains("HEAD moved", StringComparison.Ordinal));
    }

    [Fact]
    public void Capture_ValidateFailureIsIncomplete()
    {
        using var fixture = new PromotionPathFixture();
        var capture = fixture.WriteCapture(fixture.TipBase, TipCommit, runFailures: ["1 regression"]);

        Assert.False(capture.Capture.Complete);
        Assert.Contains(capture.Capture.Commands, c => c.Command == "nuke validate" && c.ExitCode != 0);
    }

    [Theory]
    [InlineData("fail", 1, "ok", "ok", "failed", "success", "success")]
    [InlineData("ok", 0, "fail", "ok", "success", "failed", "success")]
    [InlineData("ok", 0, "known_errors", "ok", "success", "known-errors", "success")]
    [InlineData("ok", 0, "skip", "ok", "success", "not-run", "success")]
    [InlineData("ok", 0, "ok", "fail", "success", "success", "failed")]
    public void Capture_AFailedTargetStageMakesTheCaptureIncomplete(
        string generation, int exitCode, string compile, string swift,
        string expectedGeneration, string expectedCSharp, string expectedSwift)
    {
        using var fixture = new PromotionPathFixture();
        var capture = fixture.WriteCapture(fixture.TipBase, TipCommit, failing: (generation, exitCode, compile, swift));

        Assert.False(capture.Capture.Complete);
        var stage = capture.Capture.TargetStages.Single(s => s.TargetName == "TestModule");
        Assert.Equal((expectedGeneration, expectedCSharp, expectedSwift), (stage.Generation, stage.CSharpCompile, stage.SwiftCompile));
        Assert.NotNull(stage.Reason);
        Assert.Contains(capture.Capture.IncompletenessReasons, r => r.StartsWith("TestModule:", StringComparison.Ordinal));
        Assert.Null(capture.Capture.TargetStages.Single(s => s.TargetName == "TestModule@macos").Reason);
    }

    [Fact]
    public void Capture_NoWrapperIsNotApplicableAndStillComplete()
    {
        using var fixture = new PromotionPathFixture();
        var capture = fixture.WriteCapture(fixture.TipBase, TipCommit, failing: ("ok", 0, "ok", "no_wrapper"));

        Assert.True(capture.Capture.Complete);
        Assert.Equal("not-applicable", capture.Capture.TargetStages.Single(s => s.TargetName == "TestModule").SwiftCompile);
    }

    [Fact]
    public void Capture_InputLockDependsOnlyOnInputBytes()
    {
        using var fixture = new PromotionPathFixture();
        var old = fixture.WriteCapture(fixture.OldBase, OldCommit);
        var tip = fixture.WriteCapture(fixture.TipBase, TipCommit);
        Assert.Equal(old.Capture.InputLockSha256, tip.Capture.InputLockSha256);

        File.WriteAllText(Path.Combine(fixture.InputDirectory, "Info.plist"), "changed");
        var changed = fixture.WriteCapture(fixture.TipBase, TipCommit);
        Assert.NotEqual(old.Capture.InputLockSha256, changed.Capture.InputLockSha256);
    }

    // --- Request builder ---

    [Fact]
    public void Request_RosterIsTheLiveManifestsExpandedTargets()
    {
        using var fixture = new PromotionPathFixture();
        fixture.WriteCapture(fixture.OldBase, OldCommit);
        var tip = fixture.WriteCapture(fixture.TipBase, TipCommit);

        var request = SurfaceRequestBuilder.Build(fixture.ManifestPath, fixture.OldBase, fixture.TipBase, tip);

        var expanded = ValidationManifest.Load(fixture.ManifestPath).ExpandTargets(librariesDir: fixture.Root);
        Assert.Equal(expanded.Select(t => t.Name), request.Targets.Select(t => t.Key.TargetName));
        Assert.Equal(expanded.Count, request.ExpectedTargetCount);
        Assert.Equal(SurfaceCanonicalJson.Sha256File(fixture.ManifestPath), request.ManifestSha256);
        var macos = request.Targets.Single(t => t.Key.TargetName == "TestModule@macos");
        Assert.Equal(("TestLibrary", "TestModule", "macos"), (macos.Key.Library, macos.Key.FrameworkModule, macos.Key.Platform));
        Assert.Equal(("macosx", "arm64", "arm64-apple-macos12.0"), (macos.ActualSdk, macos.Architecture, macos.TargetTriple));
        Assert.Equal(Path.Combine(fixture.OldBase, "TestModule@macos"), macos.OldDirectory);
        Assert.Equal(Path.Combine(fixture.TipBase, "TestModule@macos"), macos.TipDirectory);
        Assert.Equal(("old", "tip"), (request.OldCapture.Side, request.TipCapture.Side));

        var roster = SurfaceRequestBuilder.ValidateLiveRoster(request, fixture.ManifestPath);
        Assert.Equal((1, 1, 2), (roster.Libraries, roster.Modules, roster.Targets));
    }

    [Fact]
    public void Request_BuiltFromTwoValidateCapturesIsACompleteComparison()
    {
        using var fixture = new PromotionPathFixture();
        fixture.WriteCapture(fixture.OldBase, OldCommit);
        var tip = fixture.WriteCapture(fixture.TipBase, TipCommit);

        var result = SurfaceAccountingEngine.Analyze(SurfaceRequestBuilder.Build(fixture.ManifestPath, fixture.OldBase, fixture.TipBase, tip));

        Assert.True(result.Comparison.Summary.Complete);
        Assert.Equal(2, result.Comparison.Summary.ComparableTargets);
    }

    [Fact]
    public void Request_RefusesWithoutThisInvocationsValidateCapture()
    {
        using var fixture = new PromotionPathFixture();
        fixture.WriteCapture(fixture.OldBase, OldCommit);

        var error = Assert.Throws<InvalidOperationException>(() =>
            SurfaceRequestBuilder.Build(fixture.ManifestPath, fixture.OldBase, null, null));
        Assert.Contains("no validate capture exists in this invocation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_RefusesAnOldBaseWithoutACapture()
    {
        using var fixture = new PromotionPathFixture();
        var tip = fixture.WriteCapture(fixture.TipBase, TipCommit);
        Directory.CreateDirectory(fixture.OldBase);

        Assert.Throws<FileNotFoundException>(() =>
            SurfaceRequestBuilder.Build(fixture.ManifestPath, fixture.OldBase, fixture.TipBase, tip));
    }

    [Fact]
    public void Request_RefusesAnIncompleteTipCapture()
    {
        using var fixture = new PromotionPathFixture();
        fixture.WriteCapture(fixture.OldBase, OldCommit);
        var tip = fixture.WriteCapture(fixture.TipBase, TipCommit, porcelain: "?? stray\n");

        var error = Assert.Throws<InvalidDataException>(() =>
            SurfaceRequestBuilder.Build(fixture.ManifestPath, fixture.OldBase, fixture.TipBase, tip));
        Assert.Contains("incomplete", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_RefusesCapturesOfDifferentLibraryInputs()
    {
        using var fixture = new PromotionPathFixture();
        fixture.WriteCapture(fixture.OldBase, OldCommit);
        File.WriteAllText(Path.Combine(fixture.InputDirectory, "Info.plist"), "rebuilt");
        var tip = fixture.WriteCapture(fixture.TipBase, TipCommit);

        var error = Assert.Throws<InvalidDataException>(() =>
            SurfaceRequestBuilder.Build(fixture.ManifestPath, fixture.OldBase, fixture.TipBase, tip));
        Assert.Contains(".libraries/TestLibrary/TestModule.xcframework", error.Message, StringComparison.Ordinal);
    }

    // --- Live manifest roster (no frozen constants) ---

    [Fact]
    public void LiveRoster_AcceptsARequestNamingTheLiveManifest()
    {
        var request = LiveManifestRequest(out var manifestPath);

        var roster = SurfaceRequestBuilder.ValidateLiveRoster(request, manifestPath);

        Assert.Equal(SurfaceCanonicalJson.Sha256File(manifestPath), roster.ManifestSha256);
        Assert.Equal(request.Targets.Count, roster.Targets);
    }

    [Theory]
    [InlineData("4b89d88dcdddf6208f930db770e37f7f09da78d8d6599fc95f4a1235a870fa46")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void LiveRoster_RejectsAnyOtherManifestSha(string manifestSha)
    {
        var request = LiveManifestRequest(out var manifestPath) with { ManifestSha256 = manifestSha };

        var error = Assert.Throws<InvalidDataException>(() => SurfaceRequestBuilder.ValidateLiveRoster(request, manifestPath));
        Assert.Contains("live build/validation-libraries.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveRoster_RejectsARequestMissingARosterTarget()
    {
        var request = LiveManifestRequest(out var manifestPath);
        var missing = request.Targets[^1].Key.TargetName;
        request = request with { Targets = request.Targets.Take(request.Targets.Count - 1).ToList() };

        var error = Assert.Throws<InvalidDataException>(() => SurfaceRequestBuilder.ValidateLiveRoster(request, manifestPath));
        Assert.Contains($"missing target '{missing}'", error.Message, StringComparison.Ordinal);

        request = request with { ExpectedTargetCount = request.Targets.Count };
        error = Assert.Throws<InvalidDataException>(() => SurfaceRequestBuilder.ValidateLiveRoster(request, manifestPath));
        Assert.Contains("expands to", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveRoster_RejectsATargetTheManifestDoesNotName()
    {
        var request = LiveManifestRequest(out var manifestPath);
        var renamed = request.Targets[0] with { Key = request.Targets[0].Key with { TargetName = "NotInTheManifest" } };
        request = request with { Targets = [renamed, .. request.Targets.Skip(1)] };

        var error = Assert.Throws<InvalidDataException>(() => SurfaceRequestBuilder.ValidateLiveRoster(request, manifestPath));
        Assert.Contains("NotInTheManifest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveRoster_RejectsChangedRowMetadata()
    {
        var request = LiveManifestRequest(out var manifestPath);
        request = request with { Targets = [request.Targets[0] with { Tier = request.Targets[0].Tier + 1 }, .. request.Targets.Skip(1)] };

        var error = Assert.Throws<InvalidDataException>(() => SurfaceRequestBuilder.ValidateLiveRoster(request, manifestPath));
        Assert.Contains("differs from the live manifest", error.Message, StringComparison.Ordinal);
    }

    // --- Receipt bound to the candidate ---

    [Fact]
    public void Receipt_BoundTipWhoseOldSideIsTheBaselineSourceIsRecorded()
    {
        using var fixture = new PromotionPathFixture();
        var (request, tip) = fixture.Request();

        var decision = SurfacePromotionReceipt.Decide(request, tip, fixture.TipBase, OldCommit[..9], null);

        Assert.Equal(SurfaceReceiptVerdict.Receipt, decision.Verdict);
    }

    [Fact]
    public void Receipt_TipTreeAlteredAfterValidateIsRefused()
    {
        using var fixture = new PromotionPathFixture();
        var (request, tip) = fixture.Request();
        File.AppendAllText(Path.Combine(fixture.TipBase, "TestModule@macos", "TestModule.cs"), "\n// edited after validate\n");

        var decision = SurfacePromotionReceipt.Decide(request, tip, fixture.TipBase, OldCommit[..9], null);

        Assert.Equal(SurfaceReceiptVerdict.Refused, decision.Verdict);
        Assert.Contains("TestModule@macos", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_ComparisonWithoutAValidateInThisInvocationIsNotAReceipt()
    {
        using var fixture = new PromotionPathFixture();
        var (request, _) = fixture.Request();

        var decision = SurfacePromotionReceipt.Decide(request, null, null, OldCommit[..9], null);

        Assert.Equal(SurfaceReceiptVerdict.NotBound, decision.Verdict);
    }

    [Fact]
    public void Receipt_TipTreesOtherThanThisInvocationsValidateAreNotAReceipt()
    {
        using var fixture = new PromotionPathFixture();
        var (request, tip) = fixture.Request();
        var elsewhere = Path.Combine(fixture.Root, "elsewhere");
        foreach (var name in PromotionPathFixture.TargetNames)
            WriteTree(Path.Combine(elsewhere, name));
        request = request with
        {
            Targets = request.Targets.Select(t => t with { TipDirectory = Path.Combine(elsewhere, t.Key.TargetName) }).ToList(),
        };

        var decision = SurfacePromotionReceipt.Decide(request, tip, fixture.TipBase, OldCommit[..9], null);

        Assert.Equal(SurfaceReceiptVerdict.NotBound, decision.Verdict);
    }

    [Fact]
    public void Receipt_OldSideThatIsNotTheBaselineSourceIsRefusedWithoutJustification()
    {
        using var fixture = new PromotionPathFixture();
        var (request, tip) = fixture.Request();

        var decision = SurfacePromotionReceipt.Decide(request, tip, fixture.TipBase, "328d740f9", null);

        Assert.Equal(SurfaceReceiptVerdict.Refused, decision.Verdict);
        Assert.Contains("--surface-bootstrap-justification", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_OldSideThatIsNotTheBaselineSourceBootstrapsWithJustification()
    {
        using var fixture = new PromotionPathFixture();
        var (request, tip) = fixture.Request();

        var decision = SurfacePromotionReceipt.Decide(request, tip, fixture.TipBase, "328d740f9", "  first formal promotion  ");

        Assert.Equal(SurfaceReceiptVerdict.Bootstrap, decision.Verdict);
        Assert.Equal((OldCommit, "first formal promotion"), (decision.BootstrapOldSource, decision.BootstrapJustification));
    }

    [Theory]
    [InlineData("111111111", true)]
    [InlineData("1111111", true)]
    [InlineData("111111", false)]
    [InlineData("", false)]
    [InlineData("11111111A", false)]
    [InlineData("222222222", false)]
    public void Receipt_BaselineSourceIsAShortShaPrefix(string gitSha, bool matches)
        => Assert.Equal(matches, SurfacePromotionReceipt.MatchesBaselineSource(OldCommit, gitSha));

    // --- Promote() provenance writes ---

    [Fact]
    public void Promote_WritesTheCleanSourceAsAShortGitSha()
    {
        using var baseline = new PromotionBaseline();
        var candidate = baseline.Candidate();
        candidate.RecordSource(TipCommit, clean: true);

        Assert.True(candidate.Promote());

        Assert.Equal(TipCommit[..9], baseline.Read()["git_sha"]!.GetValue<string>());
    }

    [Fact]
    public void Promote_DirtySourceLeavesGitShaAlone()
    {
        using var baseline = new PromotionBaseline();
        var candidate = baseline.Candidate();
        candidate.RecordSource(TipCommit, clean: false);

        Assert.True(candidate.Promote());

        Assert.Equal("328d740f9", baseline.Read()["git_sha"]!.GetValue<string>());
    }

    [Fact]
    public void Promote_WritesTheSurfaceBootstrap()
    {
        using var baseline = new PromotionBaseline();
        var candidate = baseline.Candidate();
        candidate.RecordSource(TipCommit, clean: true);
        candidate.RecordSurfaceBootstrap(OldCommit, "first formal promotion");

        Assert.True(candidate.Promote());

        var bootstrap = baseline.Read()["surface_bootstrap"]!.AsObject();
        Assert.Equal(OldCommit, bootstrap["old_source"]!.GetValue<string>());
        Assert.Equal("first formal promotion", bootstrap["justification"]!.GetValue<string>());
    }

    [Fact]
    public void Promote_FormalPromotionWithoutBootstrapClearsAStaleOne()
    {
        using var baseline = new PromotionBaseline(staleBootstrap: true);
        var candidate = baseline.Candidate();
        candidate.RecordSource(TipCommit, clean: true);

        Assert.True(candidate.Promote());

        Assert.Null(baseline.Read()["surface_bootstrap"]);
    }

    [Fact]
    public void Promote_NeverWritesSkipMetrics()
    {
        using var baseline = new PromotionBaseline();
        var candidate = baseline.Candidate();
        candidate.RecordSource(TipCommit, clean: true);
        candidate.RecordSurfaceBootstrap(OldCommit, "reason");

        Assert.True(candidate.Promote());

        var root = baseline.Read();
        Assert.Equal(PromotionBaseline.SkipMetrics, root["skip_metrics"]!.ToJsonString());
        Assert.Equal(7, root["compile_gate"]!["libraries"]!["Fixture"]!["lines"]!.GetValue<int>());
    }

    // --- Helpers ---

    private static SurfaceAccountingRequest LiveManifestRequest(out string manifestPath)
    {
        manifestPath = Path.Combine(RepoRoot, "build", "validation-libraries.json");
        var expanded = ValidationManifest.Load(manifestPath).ExpandTargets(librariesDir: Path.Combine(RepoRoot, ".libraries"));
        var capture = SurfaceAccountingTestFixture.Capture("c", "tip", TipCommit, new string('a', 64), new string('b', 64), true, RepoRoot);
        return new SurfaceAccountingRequest
        {
            CorpusId = "live",
            ManifestPath = manifestPath,
            ManifestSha256 = SurfaceCanonicalJson.Sha256File(manifestPath),
            InputLockPath = manifestPath,
            InputLockSha256 = new string('a', 64),
            ExpectedTargetCount = expanded.Count,
            OldCapture = capture with { Side = "old" },
            TipCapture = capture,
            Targets = expanded.Select(t =>
            {
                var (sdk, triple) = PromotionPathFixture.Provenance(t.Platform);
                return new SurfaceTargetRequest
                {
                    Key = new SurfaceTargetKey(t.LibraryName, t.FrameworkModule!, t.Platform, t.Name),
                    OldDirectory = "old/" + t.Name,
                    TipDirectory = "tip/" + t.Name,
                    Mode = t.Mode,
                    Tier = t.Tier,
                    KnownErrors = t.KnownErrors,
                    Dependencies = t.Dependencies,
                    WrapperDependencies = t.WrapperDeps,
                    NamespacePattern = t.NamespacePattern,
                    ActualSdk = sdk,
                    Architecture = "arm64",
                    TargetTriple = triple,
                };
            }).ToList(),
        };
    }

    private static void WriteTree(string directory)
    {
        Directory.CreateDirectory(directory);
        SurfaceAccountingTestFixture.WriteTarget(directory, Source, [], []);
    }

    private static string RepoRoot { get; } = LocateRepoRoot();

    private static string LocateRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "build", "validation-libraries.json")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    private sealed class PromotionPathFixture : IDisposable
    {
        public static readonly string[] TargetNames = ["TestModule", "TestModule@macos"];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"surface-promotion-tests-{Guid.NewGuid():N}");
        public string ManifestPath => Path.Combine(Root, "build", "validation-libraries.json");
        public string InputDirectory => Path.Combine(Root, ".libraries", "TestLibrary", "TestModule.xcframework");
        public string OldBase => Path.Combine(Root, "old-base");
        public string TipBase => Path.Combine(Root, "tip-base");

        public PromotionPathFixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            File.WriteAllText(ManifestPath, """
                {"libraries":[{"name":"TestLibrary","mode":"source","tier":1,"platforms":["ios","macos"],
                  "products":[{"framework":"TestModule"}]}]}
                """);
            Directory.CreateDirectory(InputDirectory);
            File.WriteAllText(Path.Combine(InputDirectory, "Info.plist"), "input");
        }

        public static (string Sdk, string Triple) Provenance(string platform) => platform switch
        {
            "ios" => ("iphonesimulator", "arm64-apple-ios15.0-simulator"),
            "macos" => ("macosx", "arm64-apple-macos12.0"),
            "maccatalyst" => ("macosx", "arm64-apple-ios15.0-macabi"),
            "tvos" => ("appletvsimulator", "arm64-apple-tvos15.0-simulator"),
            _ => throw new ArgumentOutOfRangeException(nameof(platform)),
        };

        public SurfaceValidateCapture WriteCapture(
            string outputBase,
            string commit,
            string porcelain = "",
            string? endCommit = null,
            IReadOnlyList<string>? runFailures = null,
            (string Generation, int ExitCode, string Compile, string Swift)? failing = null)
        {
            var start = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
            var targets = TargetNames.Select(name =>
            {
                WriteTree(Path.Combine(outputBase, name));
                var platform = name.Contains('@') ? name.Split('@')[1] : "ios";
                var (sdk, triple) = Provenance(platform);
                var (generation, exitCode, compile, swift) = name == "TestModule" && failing is { } f ? f : ("ok", 0, "ok", "ok");
                return new SurfaceValidateTargetResult(name,
                    new SurfaceCaptureTargetProvenance(name, platform, sdk, "arm64", triple),
                    generation, exitCode, compile, "none", swift, 0, 10);
            }).ToList();
            return SurfaceCaptureBuilder.Write(new SurfaceCaptureBuildInput
            {
                OutputBase = outputBase,
                ManifestSha256 = SurfaceCanonicalJson.Sha256File(ManifestPath),
                Toolchain = new SurfaceToolchainIdentity("unprofiled", "Xcode 26.3", "swift 6.2", "10.0.100"),
                SourceAtStart = new SurfaceSourceState(commit, porcelain, ""),
                SourceAtEnd = new SurfaceSourceState(endCommit ?? commit, porcelain, ""),
                StartedAt = start,
                FinishedAt = start.AddMinutes(5),
                Targets = targets,
                Inputs = [new SurfaceLibraryInput("TestLibrary", ".libraries/TestLibrary/TestModule.xcframework", InputDirectory)],
                RunFailures = runFailures ?? [],
            });
        }

        public (SurfaceAccountingRequest Request, SurfaceValidateCapture Tip) Request()
        {
            WriteCapture(OldBase, OldCommit);
            var tip = WriteCapture(TipBase, TipCommit);
            return (SurfaceRequestBuilder.Build(ManifestPath, OldBase, TipBase, tip), tip);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class PromotionBaseline : IDisposable
    {
        public const string SkipMetrics = "{\"total_skipped_members\":3}";
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"surface-promotion-baseline-{Guid.NewGuid():N}");
        private string Path_ => Path.Combine(directory, "validation-baseline.json");

        public PromotionBaseline(bool staleBootstrap = false)
        {
            Directory.CreateDirectory(directory);
            var bootstrap = staleBootstrap ? "\"surface_bootstrap\":{\"old_source\":\"x\",\"justification\":\"y\"}," : "";
            File.WriteAllText(Path_,
                "{\"git_sha\":\"328d740f9\"," + bootstrap +
                "\"compile_gate\":{\"libraries\":{\"Fixture\":{\"compile\":\"fail\",\"lines\":1}}}," +
                "\"skip_metrics\":" + SkipMetrics + "}");
        }

        public ValidationPromotion Candidate()
        {
            var candidate = new ValidationPromotion(Path_, true,
                new Dictionary<string, ValidationBaseline.LibraryResult> { ["Fixture"] = new() { Compile = "ok", Lines = 7 } },
                "Validate", "SurfaceAccounting");
            candidate.Record("Validate");
            candidate.Record("SurfaceAccounting");
            return candidate;
        }

        public JsonObject Read() => JsonNode.Parse(File.ReadAllText(Path_))!.AsObject();

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
