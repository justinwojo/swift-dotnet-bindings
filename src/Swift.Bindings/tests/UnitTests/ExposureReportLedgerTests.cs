// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

public sealed class ExposureReportLedgerTests
{
    [Fact]
    public void ExactReportAndReceipt_Pass()
    {
        var (scan, json) = Fixture();

        Assert.Empty(ExposureReportLedger.Evaluate(json, scan, "Sample"));
    }

    [Fact]
    public void SameCountSubstitution_IsRejected()
    {
        var (scan, json) = Fixture(entryPoint: "$sSubstituted");

        var errors = ExposureReportLedger.Evaluate(json, scan, "Sample");

        Assert.Contains(errors, error => error.StartsWith("missing exposure row:", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("stale or substituted exposure row:", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingOrStaleReceipt_IsRejectedEvenForValidZero()
    {
        var scan = new GeneratedSwiftCallScanResult
        {
            ScannedFileCount = 1,
            InventoryHash = "current",
            Observations = [],
            UnresolvedSpecimens = [],
        };
        const string missing = """
            { "ModuleName":"Sample", "DegradedMembers":[],
              "DegradedSurface":{"Total":0,"ByDiagnosticId":{},
                "Exposure":{"RowCount":0,"PublicMemberCount":0,"RootCount":0,"NativeCallCount":0}} }
            """;
        const string stale = """
            { "ModuleName":"Sample", "ExposureReportingVersion":1,
              "ExposureCompleteness":{"Version":1,"Status":"Complete","ScannedFileCount":1,
                "ClassifiedCallCount":0,"UnresolvedCount":0,"InventoryHash":"stale","UnresolvedSpecimens":[]},
              "DegradedMembers":[], "DegradedSurface":{"Total":0,"ByDiagnosticId":{},
                "Exposure":{"RowCount":0,"PublicMemberCount":0,"RootCount":0,"NativeCallCount":0}} }
            """;

        Assert.Contains(ExposureReportLedger.Evaluate(missing, scan, "Sample"),
            error => error.Contains("receipt is absent", StringComparison.Ordinal));
        Assert.Contains(ExposureReportLedger.Evaluate(stale, scan, "Sample"),
            error => error.Contains("inventory hash is stale", StringComparison.Ordinal));
    }

    [Fact]
    public void BridgedBindingTestFixture_NeedsItsPlanToSayItIsBridged_NotJustZeroRows()
    {
        // Zero direct rows is also what a fixture that vanished from the emission looks like; only the
        // generation's plans can say the transition really went through its bridge.
        const string StableTag = "$s20SwiftBindingsTestLib26GlobalActorStorageBoundaryC9stableTagSSvg";
        const string StableDepth = "$s20SwiftBindingsTestLib26GlobalActorStorageBoundaryC11stableDepths5Int32Vvg";
        var scan = new GeneratedSwiftCallScanResult
        {
            ScannedFileCount = 1,
            InventoryHash = "current",
            Observations = [],
            UnresolvedSpecimens = [],
        };
        const string json = """
            { "ModuleName":"SwiftBindingsTestLib", "ExposureReportingVersion":1,
              "ExposureCompleteness":{"Version":1,"Status":"Complete","ScannedFileCount":1,
                "ClassifiedCallCount":0,"UnresolvedCount":0,"InventoryHash":"current","UnresolvedSpecimens":[]},
              "DegradedMembers":[], "DegradedSurface":{"Total":0,"ByDiagnosticId":{},
                "Exposure":{"RowCount":0,"PublicMemberCount":0,"RootCount":0,"NativeCallCount":0}} }
            """;

        var errors = ExposureReportLedger.Evaluate(json, scan, "SwiftBindingsTestLib",
            new HashSet<string>(StringComparer.Ordinal) { StableTag });

        Assert.DoesNotContain(errors, e => e.Contains(StableTag, StringComparison.Ordinal) && e.Contains("not routed", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains(StableDepth, StringComparison.Ordinal) && e.Contains("not routed", StringComparison.Ordinal));
        // The refused plan stays an exposure, so its row is still expected.
        Assert.Contains(errors, e => e.Contains("PendingRequestTask", StringComparison.Ordinal) && e.Contains("0 rows", StringComparison.Ordinal));
        // The returned closure is invoked through a Cdecl thunk, so no raw function-pointer call is expected.
        Assert.DoesNotContain(errors, e => e.Contains("ClassCallback", StringComparison.Ordinal));
    }

    private static (GeneratedSwiftCallScanResult Scan, string Json) Fixture(string entryPoint = "$sRun")
    {
        var native = new DirectSwiftSelfNativeCall
        {
            CallKind = "PInvoke",
            ManagedHolder = "Sample.Native",
            ManagedName = "Invoke",
            Library = "Sample",
            EntryPoint = "$sRun",
            Convention = "CallConvSwift",
            ReturnCarrier = "void",
            ParameterCarriers = ["SwiftSelf"],
            StableKey = "native-key",
        };
        var observation = new DirectSwiftSelfObservation
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
            GeneratedSpan = new GeneratedSourceSpan { StartLine = 1, EndLine = 3 },
        };
        var scan = new GeneratedSwiftCallScanResult
        {
            ScannedFileCount = 1,
            InventoryHash = "current",
            Observations = [observation],
            UnresolvedSpecimens = [],
        };
        var stableKey = entryPoint == "$sRun" ? "native-key" : "substituted-key";
        var json = """
            { "ModuleName":"Sample", "ExposureReportingVersion":1,
              "ExposureCompleteness":{"Version":1,"Status":"Complete","ScannedFileCount":1,
                "ClassifiedCallCount":1,"UnresolvedCount":0,"InventoryHash":"current","UnresolvedSpecimens":[]},
              "DegradedMembers":[{"Name":"Run","ContainingType":"Sample.Holder",
                "DiagnosticId":"DirectSwiftSelfExposure","IsAttributeEmitted":false,
                "DeclId":"decl","RootCauseId":"root","Accessor":null,
                "PublicApiKey":"Sample.Holder.Run()","OriginalSwiftSymbol":"__ENTRY__",
                "NativeCall":{"CallKind":"PInvoke","EntryPoint":"__ENTRY__","Convention":"CallConvSwift",
                  "ParameterCarriers":["SwiftSelf"],"StableKey":"__KEY__"},
                "Route":"swift_native","SelfRole":"instance","WrapperReason":"direct_swift_native",
                "ReasonProvenance":"final_call_form"}],
              "DegradedSurface":{"Total":1,"ByDiagnosticId":{"DirectSwiftSelfExposure":1},
                "Exposure":{"RowCount":1,"PublicMemberCount":1,"RootCount":1,"NativeCallCount":1}} }
            """
            .Replace("__ENTRY__", entryPoint, StringComparison.Ordinal)
            .Replace("__KEY__", stableKey, StringComparison.Ordinal);
        return (scan, json);
    }
}
