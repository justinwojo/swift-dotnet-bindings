// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The lowering-evidence contract: a receipt authorizes a plan only for the artifact and the exact
/// slices it covers, only while every piece of its provenance still matches the build, and never
/// against a known mismatch; the SDK's qualification record must hold every qualified compiler
/// family and no other.
/// </summary>
public class SwiftLoweringEvidenceTests
{
    public static readonly ImmutableArray<string> QualifiedCompilers =
    [
        "Apple Swift version 6.2.4 (swiftlang-6.2.4.1.4 clang-1700.6.4.2)",
        "Apple Swift version 6.4.2 (swiftlang-6.4.2.1.9 clang-2100.4.1.1)",
    ];

    private const string Symbol = "$s1M1fyS2iF";
    private const string Callee = "i64 (i64)";
    private const string Agreeing = "swiftcc " + Callee;

    public static string Sdk(string triple) => $"{SwiftArtifactScope.SdkOf(triple)} 26.2 (23C53)";

    private static readonly ImmutableArray<SwiftEvidenceSource> Sources =
    [
        new(SwiftEvidenceSource.WrapperRole, "MSwiftBindings", "sha256:aa", ["-wmo", "-parse-as-library"]),
        new(SwiftEvidenceSource.ProbeRole, "MProbe", "sha256:bb", ["-wmo", "-parse-as-library"]),
    ];

    public static SwiftLoweringSlice Slice(string triple, IReadOnlyDictionary<string, string> symbols, IEnumerable<SwiftEvidenceSource>? sources = null) => new()
    {
        Sdk = Sdk(triple),
        Sources = (sources ?? Sources).ToImmutableArray(),
        Symbols = symbols.ToImmutableSortedDictionary(StringComparer.Ordinal),
    };

    /// <summary>A record of <paramref name="artifact"/> under each of <paramref name="compilers"/>, showing <paramref name="symbols"/> on each of <paramref name="triples"/>.</summary>
    public static SwiftLoweringEvidence Record(
        string artifact, IEnumerable<string> compilers, IEnumerable<string> triples, IReadOnlyDictionary<string, string> symbols,
        SwiftLoweringEvidence? into = null, SwiftLoweringContract? contract = null)
    {
        var evidence = into ?? SwiftLoweringEvidence.Empty;
        foreach (var compiler in compilers)
        {
            foreach (var triple in triples)
                evidence = evidence.WithSlice(artifact, compiler, contract ?? SwiftLoweringContract.Current, "test", triple, Slice(triple, symbols));
        }
        return evidence;
    }

    private static readonly IReadOnlyDictionary<string, string> Shows = new Dictionary<string, string> { [Symbol] = Agreeing };

    private static SwiftArtifactScope Ios => SwiftArtifactScope.ForPlatform("M", "ios");

    private static SwiftEvidenceExpectation Expect(SwiftArtifactScope scope, string? compiler = null) => new(
        scope, compiler ?? QualifiedCompilers[0], SwiftLoweringContract.Current,
        scope.Slices.ToImmutableSortedDictionary(t => t, Sdk, StringComparer.Ordinal));

    private static SwiftEvidenceExpectation WithSources(SwiftEvidenceExpectation expected, IEnumerable<SwiftEvidenceSource> sources) =>
        expected with { Sources = expected.Scope.Slices.ToImmutableSortedDictionary(t => t, _ => sources.ToImmutableArray(), StringComparer.Ordinal) };

    private static SwiftLoweringEvidence Own(SwiftArtifactScope scope, IReadOnlyDictionary<string, string>? symbols = null) =>
        Record(scope.Artifact, [QualifiedCompilers[0]], scope.Slices, symbols ?? Shows);

    // ---- scope ----

    [Fact]
    public void AReceipt_AuthorizesExactlyTheSlicesItCovers()
    {
        var judgement = SwiftLoweringAuthorization.Authorize(Expect(Ios), Own(Ios));
        Assert.True(judgement.Authorizes, judgement.Refusal);
        var (executable, evidence) = judgement.Judge(Symbol, Callee);
        Assert.True(executable, evidence);
        Assert.Equal($"swiftc gives {Agreeing} on 3 triple(s) under 1 toolchain(s)", evidence);
    }

    [Fact]
    public void AMissingSlice_IsNonAuthorizing_Arm64DoesNotImplyX86_64()
    {
        var arm64Only = Record("M", [QualifiedCompilers[0]], Ios.Slices.Where(t => t.StartsWith("arm64-", StringComparison.Ordinal)), Shows);

        var judgement = SwiftLoweringAuthorization.Authorize(Expect(Ios), arm64Only);

        Assert.False(judgement.Authorizes);
        Assert.Contains("has no evidence for x86_64-apple-ios15.0-simulator", judgement.Refusal);
        Assert.False(judgement.Judge(Symbol, Callee).Executable);
        // The arm64-only scope it does cover is authorized.
        var arm64Scope = new SwiftArtifactScope("M", Ios.Slices.Where(t => t.StartsWith("arm64-", StringComparison.Ordinal)));
        Assert.True(SwiftLoweringAuthorization.Authorize(Expect(arm64Scope), arm64Only).Judge(Symbol, Callee).Executable);
    }

    [Fact]
    public void DeviceAndSimulator_AreDistinctSlices()
    {
        var deviceOnly = Record("M", [QualifiedCompilers[0]], ["arm64-apple-ios15.0"], Shows);
        var simulatorScope = new SwiftArtifactScope("M", ["arm64-apple-ios15.0-simulator"]);

        var judgement = SwiftLoweringAuthorization.Authorize(Expect(simulatorScope), deviceOnly);

        Assert.False(judgement.Authorizes);
        Assert.Contains("has no evidence for arm64-apple-ios15.0-simulator", judgement.Refusal);
    }

    [Fact]
    public void TvosEvidence_MustNameTvos()
    {
        var tvos = SwiftArtifactScope.ForPlatform("M", "tvos");

        var fromIos = SwiftLoweringAuthorization.Authorize(Expect(tvos), Own(Ios));
        Assert.False(fromIos.Authorizes);
        Assert.All(tvos.Slices, t => Assert.Contains($"has no evidence for {t}", fromIos.Refusal));

        Assert.True(SwiftLoweringAuthorization.Authorize(Expect(tvos), Own(tvos)).Judge(Symbol, Callee).Executable);
    }

    [Fact]
    public void AnExtraSlice_AuthorizesNothing_ButItsContradictionStillCounts()
    {
        var tvos = SwiftArtifactScope.ForPlatform("M", "tvos");
        var withTvos = Record("M", [QualifiedCompilers[0]], tvos.Slices, Shows, into: Own(Ios));
        Assert.True(SwiftLoweringAuthorization.Authorize(Expect(Ios), withTvos).Judge(Symbol, Callee).Executable);

        var contradicting = withTvos.WithSlice("M", QualifiedCompilers[0], SwiftLoweringContract.Current, "test", tvos.Slices[0],
            Slice(tvos.Slices[0], new Dictionary<string, string> { [Symbol] = "swiftcc i64 (i64, i64)" }));
        var (executable, evidence) = SwiftLoweringAuthorization.Authorize(Expect(Ios), contradicting).Judge(Symbol, Callee);

        Assert.False(executable);
        Assert.Contains($"on {tvos.Slices[0]}", evidence);
        Assert.Contains("the plan predicts", evidence);
    }

    [Fact]
    public void AnotherArtifactsReceipt_AuthorizesNothing()
    {
        var other = Own(new SwiftArtifactScope("Other", Ios.Slices));

        var judgement = SwiftLoweringAuthorization.Authorize(Expect(Ios), other);

        Assert.Equal("no receipt of M", judgement.Refusal);
        Assert.False(judgement.Judge(Symbol, Callee).Executable);
    }

    [Fact]
    public void AnotherArtifactsContradiction_StillCounts()
    {
        var contradicting = new Dictionary<string, string> { [Symbol] = "swiftcc i64 (i64, i64)" };
        var other = new SwiftArtifactScope("Other", Ios.Slices);
        var authorized = Record("Other", [QualifiedCompilers[0]], Ios.Slices, contradicting, into: Own(Ios));
        var qualified = Record("Other", QualifiedCompilers, other.Slices, contradicting, into: Record("M", QualifiedCompilers, Ios.Slices, Shows));

        foreach (var judgement in new[] { SwiftLoweringAuthorization.Authorize(Expect(Ios), authorized), SwiftLoweringQualification.Qualify(qualified, Ios) })
        {
            Assert.True(judgement.Authorizes, judgement.Refusal);
            var (executable, evidence) = judgement.Judge(Symbol, Callee);
            Assert.False(executable);
            Assert.Contains("(the receipt of Other); the plan predicts", evidence);
        }

        // A plan set takes only its own module's judgement.
        Assert.Throws<ArgumentException>(() => new SwiftCallPlanSet { Module = "Other", Coverage = [], Plans = [] }
            .WithEvidence(SwiftLoweringQualification.Qualify(qualified, Ios)));
    }

    [Theory]
    [InlineData("ios", "iphoneos", "iphonesimulator")]
    [InlineData("tvos", "appletvos", "appletvsimulator")]
    [InlineData("maccatalyst", "macosx", null)]
    [InlineData("macos", "macosx", null)]
    public void PlatformSlices_NameThePlatformsOwnTriples(string platform, string device, string? simulator)
    {
        var slices = SwiftArtifactScope.PlatformSlices(platform);

        Assert.Contains(slices, t => t.StartsWith("arm64-", StringComparison.Ordinal));
        Assert.Contains(slices, t => t.StartsWith("x86_64-", StringComparison.Ordinal));
        Assert.All(slices, t => Assert.Contains(SwiftArtifactScope.SdkOf(t), new[] { device, simulator }));
        if (simulator is not null)
        {
            Assert.Contains(slices, t => SwiftArtifactScope.SdkOf(t) == device);
            Assert.Contains(slices, t => SwiftArtifactScope.SdkOf(t) == simulator);
        }
        Assert.All(slices, t => Assert.Contains(platform == "maccatalyst" ? "-macabi" : platform == "macos" ? "macosx" : platform, t));
    }

    [Theory]
    [InlineData("arm64-apple-ios15.0", "iphoneos")]
    [InlineData("arm64-apple-ios15.0-simulator", "iphonesimulator")]
    [InlineData("x86_64-apple-ios15.0-macabi", "macosx")]
    [InlineData("arm64-apple-tvos15.0", "appletvos")]
    [InlineData("x86_64-apple-tvos15.0-simulator", "appletvsimulator")]
    [InlineData("arm64-apple-macosx12.0", "macosx")]
    public void SdkOf_ReadsTheTriplesPlatformAndEnvironment(string triple, string sdk) =>
        Assert.Equal(sdk, SwiftArtifactScope.SdkOf(triple));

    [Theory]
    [InlineData("arm64-apple-watchos9.0")]
    [InlineData("armv7-apple-ios15.0")]
    [InlineData("arm64-unknown-linux")]
    public void AScope_RejectsATripleItCannotJudge(string triple) =>
        Assert.Throws<ArgumentException>(() => new SwiftArtifactScope("M", [triple]));

    // ---- provenance ----

    public static IEnumerable<object[]> ProvenanceDrifts =>
    [
        ["compiler", "was collected by"],
        ["compiler family", "is not a qualified compiler family"],
        ["plan version", "plan version"],
        ["facts version", "facts version"],
        ["collector version", "collector version"],
        ["sdk", "was collected against"],
        ["source digest", "changed since the evidence"],
        ["source flags", "was compiled with"],
        ["missing source", "was not read from"],
        ["extra source", "which this build does not compile"],
    ];

    [Theory]
    [MemberData(nameof(ProvenanceDrifts))]
    public void AProvenanceMismatch_OnAnyField_IsNonAuthorizing(string field, string reason)
    {
        var receipt = Own(Ios);
        var expected = WithSources(Expect(Ios), Sources);
        Assert.True(SwiftLoweringAuthorization.Authorize(expected, receipt).Authorizes);

        var current = SwiftLoweringContract.Current;
        expected = field switch
        {
            "compiler" => expected with { Compiler = "Apple Swift version 6.2.5 (swiftlang-6.2.5.1.1 clang-1700.6.5.1)" },
            "compiler family" => expected with { Compiler = "Apple Swift version 7.0 (swiftlang-7.0.0.1.1 clang-2200.0.0.1)" },
            "plan version" => expected with { Contract = current with { PlanVersion = current.PlanVersion + 1 } },
            "facts version" => expected with { Contract = current with { FactsVersion = current.FactsVersion + 1 } },
            "collector version" => expected with { Contract = current with { CollectorVersion = current.CollectorVersion + 1 } },
            "sdk" => expected with { Sdks = expected.Sdks.SetItem(Ios.Slices[1], "iphonesimulator 26.4 (23E200)") },
            "source digest" => WithSources(expected, [Sources[0] with { Digest = "sha256:cc" }, Sources[1]]),
            "source flags" => WithSources(expected, [Sources[0] with { Flags = ["-wmo", "-parse-as-library", "-O"] }, Sources[1]]),
            "missing source" => WithSources(expected, [.. Sources, new(SwiftEvidenceSource.InterfaceRole, "M", "sha256:dd", [])]),
            "extra source" => WithSources(expected, [Sources[0]]),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var judgement = SwiftLoweringAuthorization.Authorize(expected, receipt);

        Assert.False(judgement.Authorizes, field);
        Assert.Contains(reason, judgement.Refusal);
        var (executable, evidence) = judgement.Judge(Symbol, Callee);
        Assert.False(executable);
        Assert.Equal(judgement.Refusal, evidence);
    }

    [Fact]
    public void AStaleDigest_IsNonAuthorizing_UntilTheEvidenceIsRecollected()
    {
        var expected = WithSources(Expect(Ios), [Sources[0] with { Digest = "sha256:new" }, Sources[1]]);

        Assert.False(SwiftLoweringAuthorization.Authorize(expected, Own(Ios)).Authorizes);

        var recollected = Ios.Slices.Aggregate(SwiftLoweringEvidence.Empty, (e, t) => e.WithSlice("M", QualifiedCompilers[0], SwiftLoweringContract.Current, "test", t,
            Slice(t, Shows, [Sources[0] with { Digest = "sha256:new" }, Sources[1]])));
        Assert.True(SwiftLoweringAuthorization.Authorize(expected, recollected).Judge(Symbol, Callee).Executable);
    }

    [Fact]
    public void WithoutSources_TheReceiptsSourcesAreNotCompared()
    {
        // Generation precedes the wrapper's final source: the expectation names none, and the build
        // compares them once they are final.
        var expected = Expect(Ios);
        Assert.Null(expected.Sources);
        Assert.True(SwiftLoweringAuthorization.Authorize(expected, Own(Ios)).Authorizes);
    }

    // ---- schema ----

    [Theory]
    [InlineData("{\"schema\": 1, \"triples\": [\"arm64-apple-ios15.0\"], \"toolchains\": []}", "schema 1")]
    [InlineData("{\"schema\": 3, \"receipts\": []}", "schema 3")]
    [InlineData("{\"receipts\": []}", "schema -1")]
    public void ASchemaVersionMismatch_IsRejected(string json, string reported)
    {
        var error = Assert.Throws<InvalidDataException>(() => SwiftLoweringEvidence.Parse(json, "evidence.json"));
        Assert.Contains(reported, error.Message);
        Assert.Contains($"reads {SwiftLoweringEvidence.CurrentSchema}", error.Message);
    }

    [Fact]
    public void AMalformedReceipt_IsRejected()
    {
        var duplicate = Own(Ios);
        duplicate = duplicate with { Receipts = [duplicate.Receipts[0], duplicate.Receipts[0]] };
        Assert.Contains("two receipts", Assert.Throws<InvalidDataException>(() => SwiftLoweringEvidence.Parse(duplicate.ToJson(), "e")).Message);

        var noSdk = Own(Ios).ToJson().Replace("\"sdk\": \"iphoneos 26.2 (23C53)\"", "\"sdk\": \"\"");
        Assert.Contains("records no SDK", Assert.Throws<InvalidDataException>(() => SwiftLoweringEvidence.Parse(noSdk, "e")).Message);

        // A source read twice under one role and name could not be compared against a build's sources.
        var twice = Record("M", [QualifiedCompilers[0]], [Ios.Slices[0]], Shows).WithSlice("M", QualifiedCompilers[0], SwiftLoweringContract.Current, "test",
            Ios.Slices[0], Slice(Ios.Slices[0], Shows, [Sources[0], Sources[0] with { Digest = "sha256:cc" }]));
        Assert.Contains("records the wrapper MSwiftBindings twice", Assert.Throws<InvalidDataException>(() => SwiftLoweringEvidence.Parse(twice.ToJson(), "e")).Message);
    }

    [Fact]
    public void Evidence_RoundTripsInOrdinalOrder()
    {
        var evidence = Record("M", QualifiedCompilers.Reverse(), Ios.Slices.Reverse(),
            new Dictionary<string, string> { ["$sZ"] = "swiftcc void ()", ["$sA"] = "swiftcc i64 ()" });

        var read = SwiftLoweringEvidence.Parse(evidence.ToJson(), "e");

        Assert.Equal(evidence.ToJson(), read.ToJson());
        Assert.Equal(QualifiedCompilers, read.Receipts.Select(r => r.Compiler));
        Assert.Equal(Ios.Slices, read.Receipts[0].Slices.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["$sA", "$sZ"], read.Receipts[0].Slices[Ios.Slices[0]].Symbols.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WithSlice_KeepsOneReceiptPerArtifactAndCompilerFamily()
    {
        var first = Own(Ios);
        var patched = first.WithSlice("M", "Apple Swift version 6.2.5 (swiftlang-6.2.5.1.1 clang-1700.6.5.1)", SwiftLoweringContract.Current, "test",
            Ios.Slices[0], Slice(Ios.Slices[0], Shows));

        var receipt = Assert.Single(patched.Receipts);
        Assert.StartsWith("Apple Swift version 6.2.5", receipt.Compiler);
        Assert.Equal([Ios.Slices[0]], receipt.Slices.Keys);

        // Another contract starts the receipt over; another artifact is kept.
        var other = Record("Other", [QualifiedCompilers[0]], Ios.Slices, Shows, into: first);
        var stale = other.WithSlice("M", QualifiedCompilers[0], SwiftLoweringContract.Current with { CollectorVersion = 99 }, "test",
            Ios.Slices[1], Slice(Ios.Slices[1], Shows));
        Assert.Equal([Ios.Slices[1]], stale.ReceiptsOf("M").Single().Slices.Keys);
        Assert.Equal(3, stale.ReceiptsOf("Other").Single().Slices.Count);
    }

    // ---- a known mismatch ----

    [Fact]
    public void AKnownMismatch_IsNeverOverridden()
    {
        var mismatch = new Dictionary<string, string> { [Symbol] = "swiftcc i64 (i64, i64)" };

        // Another compiler's receipt of the same artifact contradicts the one that authorizes.
        var otherCompiler = Record("M", [QualifiedCompilers[1]], [Ios.Slices[2]], mismatch, into: Own(Ios));
        var authorization = SwiftLoweringAuthorization.Authorize(Expect(Ios), otherCompiler);
        Assert.True(authorization.Authorizes);
        var (executable, evidence) = authorization.Judge(Symbol, Callee);
        Assert.False(executable);
        Assert.Contains("the plan predicts", evidence);

        // A receipt too stale to authorize still contradicts.
        var stale = Record("M", [QualifiedCompilers[0]], Ios.Slices, mismatch, contract: SwiftLoweringContract.Current with { PlanVersion = 0 });
        Assert.Contains("the plan predicts", SwiftLoweringAuthorization.Authorize(Expect(Ios), stale).Judge(Symbol, Callee).Evidence);

        // Under qualification, one family's mismatch refuses whatever the others show.
        var qualified = Record("M", QualifiedCompilers, Ios.Slices, Shows);
        Assert.True(SwiftLoweringQualification.Qualify(qualified, Ios).Judge(Symbol, Callee).Executable);
        var split = Record("M", [QualifiedCompilers[1]], [Ios.Slices[0]], mismatch, into: qualified);
        Assert.Contains("the plan predicts", SwiftLoweringQualification.Qualify(split, Ios).Judge(Symbol, Callee).Evidence);

        // A collector conflict is no agreement either.
        var conflict = Own(Ios, new Dictionary<string, string> { [Symbol] = "conflict: swiftcc i64 (i64) | swiftcc void ()" });
        Assert.Contains("the plan predicts", SwiftLoweringAuthorization.Authorize(Expect(Ios), conflict).Judge(Symbol, Callee).Evidence);
    }

    // ---- zero bridges ----

    [Fact]
    public void AZeroBridgeArtifact_IsValid()
    {
        var empty = Own(Ios, new Dictionary<string, string>());
        var read = SwiftLoweringEvidence.Parse(empty.ToJson(), "e");

        var judgement = SwiftLoweringAuthorization.Authorize(Expect(Ios), read);
        Assert.True(judgement.Authorizes, judgement.Refusal);
        Assert.Empty(judgement.Join([], []));
        var refused = new SwiftPlanCoverageEntry(SwiftTransitionKind.ForwardPInvoke, "M::h", "h", SwiftPlanStatus.Refused);
        Assert.Null(Assert.Single(judgement.Join([refused], [])).Evidence);
        Assert.True(SwiftLoweringQualification.Qualify(Record("M", QualifiedCompilers, Ios.Slices, new Dictionary<string, string>()), Ios).Authorizes);
    }

    [Fact]
    public void AnUnobservedTarget_SaysWhy()
    {
        var withReason = Ios.Slices.Aggregate(SwiftLoweringEvidence.Empty, (e, t) => e.WithSlice("M", QualifiedCompilers[0], SwiftLoweringContract.Current, "test", t,
            Slice(t, new Dictionary<string, string>()) with { Unobserved = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create(Symbol, "@usableFromInline internal")]) }));

        var (executable, evidence) = SwiftLoweringAuthorization.Authorize(Expect(Ios), withReason).Judge(Symbol, Callee);

        Assert.False(executable);
        Assert.StartsWith($"swiftc shows no {Symbol} on", evidence);
        Assert.Contains("@usableFromInline internal", evidence);
    }

    // ---- qualification ----

    [Fact]
    public void Qualification_NeedsEveryQualifiedFamily_AndNoOther()
    {
        var both = Record("M", QualifiedCompilers, Ios.Slices, Shows);
        var judgement = SwiftLoweringQualification.Qualify(both, Ios);
        Assert.True(judgement.Authorizes, judgement.Refusal);
        Assert.Equal($"swiftc gives {Agreeing} on 3 triple(s) under 2 toolchain(s)", judgement.Judge(Symbol, Callee).Evidence);

        var oneFamily = Record("M", [QualifiedCompilers[0]], Ios.Slices, Shows);
        Assert.Contains("no receipt of M under Apple Swift 6.4", SwiftLoweringQualification.Qualify(oneFamily, Ios).Refusal);

        var unqualified = Record("M", ["Apple Swift version 6.3.1 (swiftlang-6.3.1.1.1 clang-2000.1.1.1)"], Ios.Slices, Shows, into: both);
        Assert.Contains("not a qualified compiler family", SwiftLoweringQualification.Qualify(unqualified, Ios).Refusal);

        var stale = Record("M", QualifiedCompilers, Ios.Slices, Shows, contract: SwiftLoweringContract.Current with { FactsVersion = 1 });
        Assert.Contains("this generator judges under", SwiftLoweringQualification.Qualify(stale, Ios).Refusal);

        var missing = Record("M", QualifiedCompilers, Ios.Slices.Take(2), Shows);
        Assert.Contains("has no evidence for", SwiftLoweringQualification.Qualify(missing, Ios).Refusal);
    }

    [Fact]
    public void Qualification_IsTheSame_WhicheverCompilerRuns()
    {
        // Qualification never reads the host toolchain: the record alone decides.
        var both = Record("M", QualifiedCompilers, Ios.Slices, Shows);
        var once = SwiftLoweringQualification.Qualify(both, Ios).Judge(Symbol, Callee);
        var again = SwiftLoweringQualification.Qualify(SwiftLoweringEvidence.Parse(both.ToJson(), "e"), Ios).Judge(Symbol, Callee);
        Assert.Equal(once, again);
    }

    [Theory]
    [InlineData("Apple Swift version 6.2.4 (swiftlang-6.2.4.1.4 clang-1700.6.4.2)", "Apple Swift 6.2")]
    [InlineData("Apple Swift version 6.4 (swiftlang-6.4.0.1.1 clang-2100.0.0.1)", "Apple Swift 6.4")]
    [InlineData("Swift version 6.1.2 (swift-6.1.2-RELEASE)", "Swift 6.1")]
    [InlineData("swift-driver version: 1.120.5", null)]
    public void ACompilersFamily_IsItsVendorAndMajorMinor(string compiler, string? family) =>
        Assert.Equal(family, SwiftCompilerIdentity.Family(compiler));

    // ---- the bridge gate ----

    [Fact]
    public void TheGate_ReJudgesEachSidecarByTheScopeItRecords_AndReportsOneThatDoesNotQualify()
    {
        var outcome = SwiftCallPlanTests.Planner().PlanForward(SwiftCallPlanTests.Free("Swift.Int", "Swift.Int"), SwiftCallPlanTests.Signature("long", "long"), Symbol);
        var plan = SwiftCallPlanTests.Planned(outcome);
        var entry = new SwiftPlanCoverageEntry(SwiftTransitionKind.ForwardPInvoke, "M::f", Symbol, SwiftPlanStatus.Planned)
        {
            Target = Symbol, Symbol = plan.Symbol, Hash = plan.Hash, Callee = outcome.Callee, Evidence = SwiftCallPlanSet.NoEvidenceReason,
        };
        var set = new SwiftCallPlanSet { Module = "M", Coverage = [entry], Plans = [plan] };
        var shows = new Dictionary<string, string> { [Symbol] = "swiftcc " + outcome.Callee };
        var record = Record("M", QualifiedCompilers, Ios.Slices, shows);
        var directory = Path.Combine(Path.GetTempPath(), $"gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string Write(string name, SwiftCallPlanSet written)
            {
                var path = Path.Combine(directory, name);
                File.WriteAllText(path, written.ToJson());
                return path;
            }
            var unjudged = Write("unjudged.json", set);
            var qualified = Write("qualified.json", set.WithEvidence(SwiftLoweringQualification.Qualify(record, Ios)));
            var authorized = Write("authorized.json", set.WithEvidence(SwiftLoweringAuthorization.Authorize(Expect(Ios), Own(Ios, shows))));
            // A sidecar of M that records Other's scope, which Other's receipts qualify.
            var borrowed = Write("borrowed.json", new SwiftCallPlanSet
            {
                Module = "M",
                EvidenceScope = new SwiftPlanEvidenceScope("Other", Ios.Slices, SwiftLoweringJudgement.QualificationBasis),
                Coverage = [entry with { Executable = true }],
                Plans = [plan],
            });
            var withOther = Record("Other", QualifiedCompilers, Ios.Slices, shows, into: record);

            // An unjudged sidecar is a finding only where it is a routed generation's: a hand-written
            // table that routes nothing is planned without being judged.
            var (sets, _, executable, unqualified) = SwiftBridgeGate.Load([unjudged], [unjudged, qualified, authorized, borrowed], withOther);

            Assert.Equal([plan.Symbol], executable);
            Assert.False(sets[0].Coverage[0].Executable);
            Assert.False(sets[1].Coverage[0].Executable);
            Assert.True(sets[2].Coverage[0].Executable, sets[2].Coverage[0].Evidence);
            Assert.False(sets[3].Coverage[0].Executable);
            Assert.False(sets[4].Coverage[0].Executable);
            Assert.Equal(3, unqualified.Length);
            Assert.Contains("records no evidence scope", unqualified[0]);
            Assert.Contains("was judged by authorization", unqualified[1]);
            Assert.Contains("records the evidence scope of Other, not of its own module", unqualified[2]);

            // A record that no longer qualifies the artifact makes nothing of it executable, and says so.
            var (_, _, none, findings) = SwiftBridgeGate.Load([qualified], [], Record("M", [QualifiedCompilers[0]], Ios.Slices, shows));
            Assert.Empty(none);
            Assert.Contains("no receipt of M under Apple Swift 6.4", Assert.Single(findings));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- the committed record and the tables ----

    private static string RepoRoot => SwiftCallPlanTests.RepoRoot;

    [Fact]
    public void TheCommittedRecord_HoldsExactlyTheQualifiedFamilies_UnderTheCurrentContract()
    {
        var record = SwiftLoweringEvidence.Load(SwiftCallPlanTests.LoweringEvidencePath);

        Assert.NotEmpty(record.Receipts);
        foreach (var artifact in record.Receipts.GroupBy(r => r.Artifact))
        {
            Assert.Equal(SwiftLoweringQualification.Families.Order(StringComparer.Ordinal),
                artifact.Select(r => SwiftCompilerIdentity.Family(r.Compiler)).Order(StringComparer.Ordinal));
            Assert.All(artifact, r => Assert.Equal(SwiftLoweringContract.Current, r.Contract));
        }
    }

    [Fact]
    public void TheRuntimeTable_ClaimsExactlyTheSlicesItsNativeBuildCompiles()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "src", "Swift.Runtime", "swift", "build-runtime.sh"));
        var built = Regex.Matches(script, @"^\s*build_single_arch\s+\S+\s+""\w+""\s+""([^""]+)""", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var table = SwiftHandWrittenTransitionTable.Load(Path.Combine(RepoRoot, "src", "Swift.Runtime", "src", "transition-facts.json"));

        Assert.Equal(10, built.Length);
        Assert.Equal(built, table.Slices.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheAppleTable_ClaimsExactlyTheSlicesItsFrameworkShips()
    {
        var plist = XDocument.Load(Path.Combine(RepoRoot, "src", "Swift.Bindings.Apple", "native", "SBApple.xcframework", "Info.plist"));
        var libraries = plist.Descendants("key").Single(k => k.Value == "AvailableLibraries").ElementsAfterSelf("array").First().Elements("dict");
        static string? Value(XElement dict, string key) => dict.Elements("key").SingleOrDefault(k => k.Value == key)?.ElementsAfterSelf().First().Value;
        var shipped = libraries.SelectMany(dict => dict.Elements("key").Single(k => k.Value == "SupportedArchitectures").ElementsAfterSelf("array").First()
                .Elements("string").Select(arch => (Arch: arch.Value, Platform: Value(dict, "SupportedPlatform")!, Variant: Value(dict, "SupportedPlatformVariant"))))
            .Order()
            .ToArray();
        var table = SwiftHandWrittenTransitionTable.Load(Path.Combine(RepoRoot, "src", "Swift.Bindings.Apple", "transition-facts.json"));
        var claimed = table.Slices.Select(t =>
        {
            var parts = t.Split('-');
            var platform = new string(parts[2].TakeWhile(char.IsLetter).ToArray());
            var variant = parts.Length > 3 ? (parts[3] == "macabi" ? "maccatalyst" : parts[3]) : null;
            return (Arch: parts[0], Platform: platform == "macosx" ? "macos" : platform, Variant: variant);
        }).Order().ToArray();

        Assert.Equal(shipped, claimed);
    }

    [SkippableFact]
    public void TheToolchainProbe_IdentifiesTheSelectedCompilerAndSdks()
    {
        Skip.IfNot(OperatingSystem.IsMacOS() && File.Exists("/usr/bin/xcrun"), "xcrun is required");
        var expected = SwiftToolchainProbe.Expect(Ios, out var why);
        Skip.If(expected is null && why!.Contains("SDK", StringComparison.Ordinal), why);

        Assert.NotNull(expected);
        // The receipt's compiler is compared by exact string, so the generator's probe must read the
        // line the oracle records (the version line on stdout, without the driver's stderr prefix).
        Assert.Equal(SwiftLoweringOracle.ToolchainVersion, expected!.Compiler);
        Assert.StartsWith("Apple Swift version ", expected.Compiler);
        Assert.NotNull(SwiftCompilerIdentity.Family(expected.Compiler));
        Assert.Equal(Ios.Slices, expected.Sdks.Keys.Order(StringComparer.Ordinal));
        Assert.All(expected.Sdks, p => Assert.Matches(@"^\w+ \d+(\.\d+)+ \(\w+\)$", p.Value));
        Assert.Null(expected.Sources);
    }
}
