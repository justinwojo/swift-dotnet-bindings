// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the pure logic behind the <c>--partial-success-kitchen</c> gate:
/// <see cref="KitchenReportProjection.ParseReport"/> (reading the gate-relevant slice of a
/// generator <c>binding-report.json</c>), <see cref="PartialSuccessKitchenBaseline.CheckFloors"/>
/// (the shape-independent design invariants), and <see cref="PartialSuccessKitchenBaseline.Compare"/>
/// (the exact drift diff against the frozen budget). Link-compiled from <c>build/Models</c> so the
/// gate's report accounting is verified without running the generator.
/// </summary>
public class PartialSuccessKitchenBaselineTests
{
    // A realistic SkipTriage block mirroring the shape the kitchen fixture actually produces:
    // ReviewCount 0, the expected disposition mix, and a per-reason multiset.
    private const string HealthyReportJson = """
        {
          "ModuleName": "PartialSuccessKitchen",
          "EmittedTypes": 12,
          "SkippedItems": [],
          "SkipTriage": {
            "Total": 11,
            "ByDisposition": {
              "ExpectedNonPublic": 2,
              "ExpectedStructural": 6,
              "KnownLimitation": 3
            },
            "ByReason": {
              "SynthesizedCodable": 2,
              "EveryProtocolConformanceSkipped": 2,
              "ParentModuleInternalNoFallback": 2,
              "UnsupportedSignature": 3,
              "Pattern2InternalTypeReach": 1,
              "SwiftUIView": 1
            },
            "PublicSurfaceLost": 9,
            "ReviewCount": 0,
            "ReviewItems": []
          }
        }
        """;

    private static KitchenReportProjection Healthy() => KitchenReportProjection.ParseReport(HealthyReportJson);

    // ── ParseReport ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseReport_ReadsReviewCountAndMultisets()
    {
        var r = Healthy();
        Assert.Equal(0, r.ReviewCount);
        Assert.Equal(3, r.ByReason["UnsupportedSignature"]);
        Assert.Equal(2, r.ByReason["SynthesizedCodable"]);
        Assert.Equal(6, r.ByDisposition["ExpectedStructural"]);
        Assert.Equal(2, r.ByDisposition["ExpectedNonPublic"]);
    }

    [Fact]
    public void ParseReport_ReadsReviewItemSummaries()
    {
        const string json = """
            {
              "SkipTriage": {
                "ReviewCount": 1,
                "ByDisposition": { "Review": 1 },
                "ByReason": { "MissingHandler": 1 },
                "ReviewItems": [
                  { "Kind": "Method", "Name": "mystery", "Reason": "MissingHandler" }
                ]
              }
            }
            """;
        var r = KitchenReportProjection.ParseReport(json);
        Assert.Equal(1, r.ReviewCount);
        Assert.Single(r.ReviewItemSummaries);
        Assert.Contains("mystery", r.ReviewItemSummaries[0]);
        Assert.Contains("MissingHandler", r.ReviewItemSummaries[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ \"ModuleName\": \"X\" }")] // no SkipTriage block (a module that skipped nothing)
    public void ParseReport_NoTriage_ProjectsEmpty(string json)
    {
        var r = KitchenReportProjection.ParseReport(json);
        Assert.Equal(0, r.ReviewCount);
        Assert.Empty(r.ByReason);
        Assert.Empty(r.ByDisposition);
    }

    // ── CheckFloors ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CheckFloors_HealthyReport_NoViolations()
    {
        Assert.Empty(PartialSuccessKitchenBaseline.CheckFloors(Healthy()));
    }

    [Fact]
    public void CheckFloors_NonZeroReview_Fails()
    {
        const string json = """
            {
              "SkipTriage": {
                "ReviewCount": 1,
                "ByDisposition": { "ExpectedNonPublic": 2, "ExpectedStructural": 6, "KnownLimitation": 3, "Review": 1 },
                "ByReason": { "UnsupportedSignature": 3, "SynthesizedCodable": 2, "EveryProtocolConformanceSkipped": 2, "ParentModuleInternalNoFallback": 2, "Pattern2InternalTypeReach": 1, "SwiftUIView": 1, "MissingHandler": 1 },
                "ReviewItems": [ { "Kind": "Method", "Name": "mystery", "Reason": "MissingHandler" } ]
              }
            }
            """;
        var failures = PartialSuccessKitchenBaseline.CheckFloors(KitchenReportProjection.ParseReport(json));
        Assert.Contains(failures, f => f.Contains("ReviewCount"));
    }

    [Fact]
    public void CheckFloors_MissingWrapperSymbolPresent_Fails()
    {
        // A dangling wrapper symbol row must fail even if it were (wrongly) not Review-classified.
        const string json = """
            {
              "SkipTriage": {
                "ReviewCount": 0,
                "ByDisposition": { "ExpectedNonPublic": 2, "ExpectedStructural": 6, "KnownLimitation": 4 },
                "ByReason": { "UnsupportedSignature": 3, "SynthesizedCodable": 2, "EveryProtocolConformanceSkipped": 2, "ParentModuleInternalNoFallback": 2, "Pattern2InternalTypeReach": 1, "SwiftUIView": 1, "MissingWrapperSymbol": 1 },
                "ReviewItems": []
              }
            }
            """;
        var failures = PartialSuccessKitchenBaseline.CheckFloors(KitchenReportProjection.ParseReport(json));
        Assert.Contains(failures, f => f.Contains("MissingWrapperSymbol"));
    }

    [Fact]
    public void CheckFloors_TooFewExpectedDispositions_Fails()
    {
        const string json = """
            {
              "SkipTriage": {
                "ReviewCount": 0,
                "ByDisposition": { "ExpectedStructural": 1, "KnownLimitation": 1 },
                "ByReason": { "SwiftUIView": 1, "UnsupportedSignature": 1 },
                "ReviewItems": []
              }
            }
            """;
        var failures = PartialSuccessKitchenBaseline.CheckFloors(KitchenReportProjection.ParseReport(json));
        Assert.Contains(failures, f => f.Contains("ExpectedStructural"));
        Assert.Contains(failures, f => f.Contains("ExpectedNonPublic"));
        Assert.Contains(failures, f => f.Contains("KnownLimitation"));
    }

    // ── Compare (exact drift) ────────────────────────────────────────────────────────────────

    [Fact]
    public void Compare_SelfConsistent_NoDrift()
    {
        var report = Healthy();
        var baseline = PartialSuccessKitchenBaseline.FromReport(report, "abc1234");
        Assert.Empty(baseline.Compare(report));
    }

    [Fact]
    public void Compare_RoundTripsThroughJson()
    {
        var report = Healthy();
        var baseline = PartialSuccessKitchenBaseline.FromReport(report, "abc1234");
        var reloaded = PartialSuccessKitchenBaseline.Parse(baseline.ToJson());
        Assert.Empty(reloaded.Compare(report));
        Assert.Equal("abc1234", reloaded.GitSha);
    }

    [Fact]
    public void Compare_ReviewCountChange_IsDrift()
    {
        var baseline = PartialSuccessKitchenBaseline.FromReport(Healthy());
        var changed = Healthy() with { ReviewCount = 1 };
        Assert.Contains(baseline.Compare(changed), d => d.Contains("ReviewCount"));
    }

    [Fact]
    public void Compare_NewReason_IsDrift()
    {
        var baseline = PartialSuccessKitchenBaseline.FromReport(Healthy());
        var withNew = Healthy() with
        {
            ByReason = Healthy().ByReason.Concat(new[] { new System.Collections.Generic.KeyValuePair<string, int>("UnsupportedClosure", 1) })
                .ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        Assert.Contains(baseline.Compare(withNew), d => d.Contains("NEW") && d.Contains("UnsupportedClosure"));
    }

    [Fact]
    public void Compare_ReasonCountShift_IsDrift()
    {
        var baseline = PartialSuccessKitchenBaseline.FromReport(Healthy());
        var shifted = Healthy() with
        {
            ByReason = Healthy().ByReason
                .ToDictionary(kv => kv.Key, kv => kv.Key == "UnsupportedSignature" ? kv.Value + 1 : kv.Value),
        };
        Assert.Contains(baseline.Compare(shifted), d => d.Contains("UnsupportedSignature") && d.Contains("drift"));
    }

    [Fact]
    public void Compare_DroppedReason_IsDrift()
    {
        var baseline = PartialSuccessKitchenBaseline.FromReport(Healthy());
        var dropped = Healthy() with
        {
            ByReason = Healthy().ByReason.Where(kv => kv.Key != "SwiftUIView").ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        Assert.Contains(baseline.Compare(dropped), d => d.Contains("GONE") && d.Contains("SwiftUIView"));
    }
}
