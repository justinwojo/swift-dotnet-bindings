// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests.ReportingTests;

public sealed class DirectSwiftSelfExposureCollectorTests
{
    [Fact]
    public void SameMember_CarriesAttributeAndReportOnlyDiagnostic_WithoutChangingMemberCounts()
    {
        var report = new BindingReport { ModuleName = "Sample", EmittedMembers = 1 };
        report.DegradedMembers.Add(new DegradedMemberItem
        {
            Kind = BindingItemKind.Method,
            Name = "Run",
            ContainingType = "Sample.Holder",
            DiagnosticId = "SB0001",
            WrapperReason = "unsupported_generic_container",
        });

        DirectSwiftSelfExposureCollector.Apply(report, ScanOne());

        Assert.Equal(1, report.EmittedMembers);
        Assert.Equal(2, report.DegradedMembers.Count);
        Assert.True(report.DegradedMembers.Single(row => row.DiagnosticId == "SB0001").IsAttributeEmitted);
        var exposure = report.DegradedMembers.Single(row => row.DiagnosticId == DirectSwiftSelfExposure.DiagnosticId);
        Assert.False(exposure.IsAttributeEmitted);
        Assert.Equal("unsupported_generic_container", exposure.WrapperReason);
        Assert.Equal("emitted_safety_marker", exposure.ReasonProvenance);
        Assert.Equal(2, report.DegradedSurface!.Total);
        Assert.Equal(1, report.DegradedSurface.Exposure!.PublicMemberCount);
    }

    [Fact]
    public void Apply_IsIdempotent_AndValidZeroNeedsCurrentReceipt()
    {
        var report = new BindingReport { ModuleName = "Sample" };
        var scan = ScanOne();

        DirectSwiftSelfExposureCollector.Apply(report, scan);
        DirectSwiftSelfExposureCollector.Apply(report, scan);
        Assert.Single(report.DegradedMembers);

        DirectSwiftSelfExposureCollector.Apply(report, new GeneratedSwiftCallScanResult
        {
            ScannedFileCount = 1,
            InventoryHash = "empty",
            Observations = [],
            UnresolvedSpecimens = [],
        });
        Assert.Empty(report.DegradedMembers);
        Assert.Equal(ExposureCompletenessStatus.Complete, report.ExposureCompleteness!.Status);
        Assert.Equal(0, report.ExposureCompleteness.ClassifiedCallCount);
    }

    [Fact]
    public void LegacyManifest_ProjectsAbsentReceiptAsUnknownCoverage()
    {
        var manifest = JsonConvert.DeserializeObject<BindingArtifactManifest>("""
            { "SchemaVersion": 3, "Module": "Sample", "Generation": { "Status": "Success" } }
            """, new JsonSerializerSettings
            {
                Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
            })!;

        var report = BindingReportProjection.Project(manifest);

        Assert.Null(report.ExposureReportingVersion);
        Assert.Null(report.ExposureCompleteness);
        Assert.DoesNotContain(report.DegradedMembers,
            row => row.DiagnosticId == DirectSwiftSelfExposure.DiagnosticId);
    }

    [Fact]
    public void ExactCoGating_RemovesOnlyMatchingAccessorExposure()
    {
        var report = new BindingReport { ModuleName = "Sample" };
        var first = ScanOne().Observations.Single();
        var second = first with
        {
            Accessor = "set",
            NativeCall = first.NativeCall with { EntryPoint = "$sSet", StableKey = "set-call" },
        };
        DirectSwiftSelfExposureCollector.Apply(report, new GeneratedSwiftCallScanResult
        {
            ScannedFileCount = 1,
            InventoryHash = "hash",
            Observations = [first, second],
            UnresolvedSpecimens = [],
        });
        var manifest = new BindingArtifactManifest
        {
            Module = "Sample",
            Generation = GenerationSection.From(report),
            Wrapper = new WrapperSection
            {
                CSharpCoGatedMembers =
                {
                    new CoGatedMember
                    {
                        Name = "Run",
                        ContainingType = "Holder",
                        Kind = BindingItemKind.Method,
                        MangledSymbol = "$sSet",
                        Ordinal = 0,
                        Confidence = IdentityConfidence.Mangled,
                    },
                },
            },
        };

        var projected = BindingReportProjection.Project(manifest);

        var survivor = Assert.Single(projected.DegradedMembers);
        Assert.Null(survivor.Accessor);
        Assert.Equal("$sRun", survivor.NativeCall!.EntryPoint);
        Assert.Equal(1, projected.DegradedSurface!.Exposure!.RowCount);
    }

    [Fact]
    public void HeuristicCoGating_DoesNotNameMatchSurvivingExposureOverload()
    {
        var report = new BindingReport { ModuleName = "Sample" };
        DirectSwiftSelfExposureCollector.Apply(report, ScanOne());
        var manifest = new BindingArtifactManifest
        {
            Module = "Sample",
            Generation = GenerationSection.From(report),
            Wrapper = new WrapperSection
            {
                CSharpCoGatedMembers =
                {
                    new CoGatedMember
                    {
                        Name = "Run",
                        ContainingType = "Holder",
                        Kind = BindingItemKind.Method,
                        MangledSymbol = null,
                        PublicApiKey = null,
                        Ordinal = 0,
                        Confidence = IdentityConfidence.Heuristic,
                    },
                },
            },
        };

        var projected = BindingReportProjection.Project(manifest);

        var survivor = Assert.Single(projected.DegradedMembers);
        Assert.Equal("Sample.Holder.Run()", survivor.PublicApiKey);
        Assert.Equal("$sRun", survivor.NativeCall!.EntryPoint);
    }

    private static GeneratedSwiftCallScanResult ScanOne()
    {
        var native = new DirectSwiftSelfNativeCall
        {
            CallKind = "PInvoke",
            ManagedHolder = "Sample.Native",
            ManagedName = "PInvoke_run_12345678",
            Library = "Sample",
            EntryPoint = "$sRun",
            Convention = "CallConvSwift",
            ReturnCarrier = "void",
            ParameterCarriers = ["SwiftSelf"],
            StableKey = "run-call",
        };
        return new GeneratedSwiftCallScanResult
        {
            ScannedFileCount = 1,
            InventoryHash = "hash",
            UnresolvedSpecimens = [],
            Observations =
            [
                new DirectSwiftSelfObservation
                {
                    PublicApiKey = "Sample.Holder.Run()",
                    PublicName = "Run",
                    ContainingType = "Sample.Holder",
                    DeclarationKind = "method",
                    IsStatic = false,
                    SourceMemberName = "run",
                    OriginalSwiftSymbol = "$sRun",
                    NativeCall = native,
                    Route = "swift_native",
                    SelfRole = "instance",
                    HasSwiftError = false,
                    GeneratedFile = "Sample.cs",
                    GeneratedSpan = new GeneratedSourceSpan { StartLine = 1, EndLine = 4 },
                },
            ],
        };
    }
}
