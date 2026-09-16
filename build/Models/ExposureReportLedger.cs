// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BindingsGeneration;

/// <summary>Pure comparison logic for the final-file exposure-report truth gate.</summary>
public static class ExposureReportLedger
{
    public static IReadOnlyList<string> Evaluate(
        string reportJson,
        GeneratedSwiftCallScanResult scan,
        string expectedModule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJson);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedModule);

        var report = JsonSerializer.Deserialize(
            reportJson,
            ExposureReportLedgerJsonContext.Default.ExposureReportDocument)
            ?? throw new InvalidDataException("Exposure report deserialized to null.");
        var errors = new List<string>();

        if (!string.Equals(report.ModuleName, expectedModule, StringComparison.Ordinal))
            errors.Add($"report module '{report.ModuleName}' does not match expected '{expectedModule}'");
        if (report.ExposureReportingVersion != DirectSwiftSelfExposure.ReportingVersion)
            errors.Add($"ExposureReportingVersion is {report.ExposureReportingVersion?.ToString() ?? "absent"}, expected {DirectSwiftSelfExposure.ReportingVersion}");
        if (report.ExposureCompleteness is null)
        {
            errors.Add("ExposureCompleteness receipt is absent (legacy coverage is Unknown, not zero)");
        }
        else
        {
            var receipt = report.ExposureCompleteness;
            if (!string.Equals(receipt.Status, "Complete", StringComparison.Ordinal))
                errors.Add($"receipt status is '{receipt.Status ?? "absent"}', expected Complete");
            if (receipt.Version != DirectSwiftSelfExposure.ReportingVersion)
                errors.Add($"receipt version is {receipt.Version}, expected {DirectSwiftSelfExposure.ReportingVersion}");
            if (receipt.ScannedFileCount != scan.ScannedFileCount)
                errors.Add($"receipt scanned {receipt.ScannedFileCount} file(s), final inventory has {scan.ScannedFileCount}");
            if (!string.Equals(receipt.InventoryHash, scan.InventoryHash, StringComparison.Ordinal))
                errors.Add("receipt inventory hash is stale");
            if (receipt.UnresolvedCount != 0 || receipt.UnresolvedSpecimens.Count != 0)
                errors.Add($"receipt has {receipt.UnresolvedCount} unresolved call(s)");
            if (receipt.ClassifiedCallCount != scan.Observations.Count)
                errors.Add($"receipt classified {receipt.ClassifiedCallCount} call(s), final reader found {scan.Observations.Count}");
        }

        if (scan.UnresolvedSpecimens.Count != 0)
            errors.AddRange(scan.UnresolvedSpecimens.Select(specimen => "final reader unresolved: " + specimen));

        var reported = report.DegradedMembers
            .Where(row => string.Equals(row.DiagnosticId, DirectSwiftSelfExposure.DiagnosticId, StringComparison.Ordinal))
            .ToList();
        var expectedKeys = scan.Observations.Select(ObservationKey).ToHashSet(StringComparer.Ordinal);
        var reportedKeys = reported.Select(ReportKey).ToList();
        foreach (var duplicate in reportedKeys.GroupBy(key => key, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add("duplicate exposure row: " + duplicate.Key);
        foreach (var missing in expectedKeys.Except(reportedKeys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
            errors.Add("missing exposure row: " + missing);
        foreach (var extra in reportedKeys.Except(expectedKeys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
            errors.Add("stale or substituted exposure row: " + extra);

        foreach (var row in reported)
        {
            if (row.IsAttributeEmitted)
                errors.Add($"{row.PublicApiKey}: report-only diagnostic claims an emitted attribute");
            if (string.IsNullOrWhiteSpace(row.PublicApiKey) || string.IsNullOrWhiteSpace(row.ContainingType))
                errors.Add($"{row.Name}: missing exact public owner/key");
            if (string.IsNullOrWhiteSpace(row.WrapperReason) || string.IsNullOrWhiteSpace(row.ReasonProvenance))
                errors.Add($"{row.PublicApiKey}: missing reason or reason provenance");
            if (row.NativeCall is null)
            {
                errors.Add($"{row.PublicApiKey}: missing native call identity");
                continue;
            }
            if (!string.Equals(row.NativeCall.Convention, "CallConvSwift", StringComparison.Ordinal))
                errors.Add($"{row.PublicApiKey}: contradictory final convention '{row.NativeCall.Convention}'");
            if (!row.NativeCall.ParameterCarriers.Any(IsUntypedSwiftSelfCarrier))
                errors.Add($"{row.PublicApiKey}: no top-level untyped SwiftSelf carrier");
            if (row.NativeCall.ParameterCarriers.Any(carrier => carrier.Contains("SwiftSelf<", StringComparison.Ordinal)))
                errors.Add($"{row.PublicApiKey}: typed SwiftSelf misclassified as untyped");
            if (row.NativeCall.CallKind == "PInvoke"
                && row.NativeCall.EntryPoint?.StartsWith("$s", StringComparison.Ordinal) == true
                && !string.Equals(row.OriginalSwiftSymbol, row.NativeCall.EntryPoint, StringComparison.Ordinal))
                errors.Add($"{row.PublicApiKey}: original Swift symbol does not match the direct import");
        }

        if (string.Equals(expectedModule, "SwiftBindingsTestLib", StringComparison.Ordinal))
            ValidateNamedBindingTestFixtures(reported, errors);

        var allRows = report.DegradedMembers.Count;
        if (report.DegradedSurface is null)
        {
            errors.Add("DegradedSurface summary is absent");
        }
        else
        {
            if (report.DegradedSurface.Total != allRows)
                errors.Add($"DegradedSurface.Total is {report.DegradedSurface.Total}, expected diagnostic row count {allRows}");
            if (report.DegradedSurface.ByDiagnosticId.GetValueOrDefault(DirectSwiftSelfExposure.DiagnosticId) != reported.Count)
                errors.Add("DegradedSurface.ByDiagnosticId exposure count disagrees with rows");
            if (report.DegradedSurface.Exposure is null)
                errors.Add("DegradedSurface.Exposure summary is absent");
            else
                ValidateSummary(report.DegradedSurface.Exposure, reported, errors);
        }

        return errors;
    }

    private static void ValidateNamedBindingTestFixtures(
        IReadOnlyList<ExposureRowDocument> rows,
        List<string> errors)
    {
        foreach (var expected in BindingTestFixtures)
        {
            var matches = rows.Where(row =>
                string.Equals(row.NativeCall?.EntryPoint, expected.Symbol, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1)
            {
                errors.Add($"named fixture {expected.Owner}.{expected.Member}.{expected.Accessor} has {matches.Count} rows for {expected.Symbol}, expected 1");
                continue;
            }

            var row = matches[0];
            if (!string.Equals(row.ContainingType, expected.Owner, StringComparison.Ordinal)
                || row.PublicApiKey?.Contains('.' + expected.Member, StringComparison.Ordinal) != true
                || !string.Equals(row.Accessor, expected.Accessor, StringComparison.Ordinal)
                || !string.Equals(row.WrapperReason, expected.Reason, StringComparison.Ordinal))
            {
                errors.Add($"named fixture {expected.Symbol} was misattributed: "
                    + $"owner={row.ContainingType}, key={row.PublicApiKey}, accessor={row.Accessor}, reason={row.WrapperReason}");
            }
        }

        foreach (var resolved in ResolvedBindingTestFixtures)
        {
            var matches = rows.Count(row =>
                string.Equals(row.NativeCall?.EntryPoint, resolved.Symbol, StringComparison.Ordinal));
            if (matches != 0)
            {
                errors.Add($"resolved fixture {resolved.Owner}.{resolved.Member}.{resolved.Accessor} has {matches} direct SwiftSelf row(s) for {resolved.Symbol}, expected 0 after Cdecl-wrapper routing");
            }
        }

        var functionPointer = rows.Where(row =>
            string.Equals(row.Route, "swift_function_pointer", StringComparison.Ordinal)
            && string.Equals(row.ContainingType, "SwiftBindingsTestLib.OwnershipCallbackOwner", StringComparison.Ordinal)
            && row.PublicApiKey?.Contains(".ClassCallback:", StringComparison.Ordinal) == true
            && string.Equals(row.Accessor, "get", StringComparison.Ordinal)).ToList();
        if (functionPointer.Count != 1)
            errors.Add($"named fixture OwnershipCallbackOwner.ClassCallback.get has {functionPointer.Count} Swift function-pointer rows, expected 1");
    }

    private static readonly FixtureExpectation[] BindingTestFixtures =
    [
        new("SwiftBindingsTestLib.GlobalActorStorageBoundary", "StableTag", "get", "actor_isolated", "$s20SwiftBindingsTestLib26GlobalActorStorageBoundaryC9stableTagSSvg"),
        new("SwiftBindingsTestLib.GlobalActorStorageBoundary", "StableDepth", "get", "actor_isolated", "$s20SwiftBindingsTestLib26GlobalActorStorageBoundaryC11stableDepths5Int32Vvg"),
        new("SwiftBindingsTestLib.GlobalActorStorageBoundary", "MutableCount", "get", "actor_isolated", "$s20SwiftBindingsTestLib26GlobalActorStorageBoundaryC12mutableCounts5Int32VvgTj"),
        new("SwiftBindingsTestLib.GlobalActorStorageBoundary", "MutableCount", "set", "actor_isolated", "$s20SwiftBindingsTestLib26GlobalActorStorageBoundaryC12mutableCounts5Int32VvsTj"),
        new("SwiftBindingsTestLib.GlobalActorStorageBoundary", "DoubledCount", "get", "actor_isolated", "$s20SwiftBindingsTestLib26GlobalActorStorageBoundaryC12doubledCounts5Int32VvgTj"),
        new("SwiftBindingsTestLib.GlobalActorStorageBoundary", "PrivatelySet", "get", "actor_isolated", "$s20SwiftBindingsTestLib26GlobalActorStorageBoundaryC12privatelySets5Int32VvgTj"),
        new("SwiftBindingsTestLib.BugReproBindTarget.ScenePath", "Self", "get", "self_property", "$s20SwiftBindingsTestLib18BugReproBindTargetV9ScenePathV4selfACvg"),
        new("SwiftBindingsTestLib.DirectReturnLocalNameCollider", "OptionalExistentialReturn", null, "unsupported_generic_container", "$s20SwiftBindingsTestLib29DirectReturnLocalNameColliderV019optionalExistentialF011swiftResultAA0gH14CollisionShape_pSgs5Int32V_tF"),
    ];

    // These setters used to be intentional direct-call specimens. They now reach typed Cdecl
    // wrappers, so the final-file scanner must keep them out of the direct-SwiftSelf ledger.
    private static readonly ResolvedFixtureExpectation[] ResolvedBindingTestFixtures =
    [
        new("SwiftBindingsTestLib.OptionalThrowingModifierHolder", "Validator", "set", "$s20SwiftBindingsTestLib30OptionalThrowingModifierHolderC9validatoryyKcvs"),
        new("SwiftBindingsTestLib.OwnershipCallbackOwner", "Callback", "set", "$s20SwiftBindingsTestLib22OwnershipCallbackOwnerC8callbackyyAA0F10OwnedValueVcvs"),
        new("SwiftBindingsTestLib.OwnershipCallbackOwner", "PodCallback", "set", "$s20SwiftBindingsTestLib22OwnershipCallbackOwnerC03podF0yyAA0F8PodValueVcvs"),
        new("SwiftBindingsTestLib.OwnershipCallbackOwner", "ClassCallback", "set", "$s20SwiftBindingsTestLib22OwnershipCallbackOwnerC05classF0yyAA0E5TokenCcvs"),
    ];

    private sealed record FixtureExpectation(
        string Owner,
        string Member,
        string? Accessor,
        string Reason,
        string Symbol);

    private sealed record ResolvedFixtureExpectation(
        string Owner,
        string Member,
        string Accessor,
        string Symbol);

    private static void ValidateSummary(
        ExposureSummaryDocument summary,
        IReadOnlyList<ExposureRowDocument> rows,
        List<string> errors)
    {
        var publicCount = rows.Select(row => row.PublicApiKey).Distinct(StringComparer.Ordinal).Count();
        var rootCount = rows.Select(row => row.RootCauseId ?? row.DeclId ?? row.PublicApiKey)
            .Distinct(StringComparer.Ordinal).Count();
        var callCount = rows.Select(row => row.NativeCall?.StableKey ?? string.Empty)
            .Distinct(StringComparer.Ordinal).Count();
        if (summary.RowCount != rows.Count) errors.Add("exposure RowCount disagrees with rows");
        if (summary.PublicMemberCount != publicCount) errors.Add("exposure PublicMemberCount disagrees with rows");
        if (summary.RootCount != rootCount) errors.Add("exposure RootCount disagrees with rows");
        if (summary.NativeCallCount != callCount) errors.Add("exposure NativeCallCount disagrees with rows");
    }

    private static string ObservationKey(DirectSwiftSelfObservation observation)
        => string.Join('\u001f', observation.PublicApiKey, observation.Accessor ?? string.Empty,
            observation.NativeCall.StableKey, observation.Route, observation.SelfRole);

    private static string ReportKey(ExposureRowDocument row)
        => string.Join('\u001f', row.PublicApiKey ?? string.Empty, row.Accessor ?? string.Empty,
            row.NativeCall?.StableKey ?? string.Empty, row.Route ?? string.Empty, row.SelfRole ?? string.Empty);

    private static bool IsUntypedSwiftSelfCarrier(string carrier)
    {
        var type = carrier.StartsWith("ref ", StringComparison.Ordinal)
            || carrier.StartsWith("out ", StringComparison.Ordinal)
            || carrier.StartsWith("in ", StringComparison.Ordinal)
            ? carrier[(carrier.IndexOf(' ') + 1)..]
            : carrier;
        return string.Equals(type, "SwiftSelf", StringComparison.Ordinal)
            || type.EndsWith(".SwiftSelf", StringComparison.Ordinal);
    }

    internal sealed class ExposureReportDocument
    {
        public string ModuleName { get; set; } = string.Empty;
        public int? ExposureReportingVersion { get; set; }
        public ReceiptDocument? ExposureCompleteness { get; set; }
        public List<ExposureRowDocument> DegradedMembers { get; set; } = new();
        public DegradedSurfaceDocument? DegradedSurface { get; set; }
    }

    internal sealed class ReceiptDocument
    {
        public int Version { get; set; }
        public string? Status { get; set; }
        public int ScannedFileCount { get; set; }
        public int ClassifiedCallCount { get; set; }
        public int UnresolvedCount { get; set; }
        public string? InventoryHash { get; set; }
        public List<string> UnresolvedSpecimens { get; set; } = new();
    }

    internal sealed class ExposureRowDocument
    {
        public string Name { get; set; } = string.Empty;
        public string? ContainingType { get; set; }
        public string DiagnosticId { get; set; } = string.Empty;
        public bool IsAttributeEmitted { get; set; }
        public string? DeclId { get; set; }
        public string? RootCauseId { get; set; }
        public string? Accessor { get; set; }
        public string? PublicApiKey { get; set; }
        public string? OriginalSwiftSymbol { get; set; }
        public NativeCallDocument? NativeCall { get; set; }
        public string? Route { get; set; }
        public string? SelfRole { get; set; }
        public string? WrapperReason { get; set; }
        public string? ReasonProvenance { get; set; }
    }

    internal sealed class NativeCallDocument
    {
        public string CallKind { get; set; } = string.Empty;
        public string? EntryPoint { get; set; }
        public string Convention { get; set; } = string.Empty;
        public List<string> ParameterCarriers { get; set; } = new();
        public string StableKey { get; set; } = string.Empty;
    }

    internal sealed class DegradedSurfaceDocument
    {
        public int Total { get; set; }
        public Dictionary<string, int> ByDiagnosticId { get; set; } = new(StringComparer.Ordinal);
        public ExposureSummaryDocument? Exposure { get; set; }
    }

    internal sealed class ExposureSummaryDocument
    {
        public int RowCount { get; set; }
        public int PublicMemberCount { get; set; }
        public int RootCount { get; set; }
        public int NativeCallCount { get; set; }
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ExposureReportLedger.ExposureReportDocument))]
internal partial class ExposureReportLedgerJsonContext : JsonSerializerContext
{
}
