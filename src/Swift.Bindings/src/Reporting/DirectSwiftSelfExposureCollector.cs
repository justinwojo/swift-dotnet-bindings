// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Joins settled final-file observations to the report's exact Swift declaration/reason records.
/// Reporting is additive: it never changes eligibility, emitted attributes, routes, or withdrawals.
/// </summary>
public static class DirectSwiftSelfExposureCollector
{
    public static void Apply(BindingReport report, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var scan = GeneratedSwiftCallReader.ScanDirectory(outputDirectory);
        Apply(report, scan);
    }

    internal static void Apply(BindingReport report, GeneratedSwiftCallScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(scan);

        // Idempotent for retry/projection tests and defensive against a caller applying settlement twice.
        report.DegradedMembers.RemoveAll(item =>
            string.Equals(item.DiagnosticId, DirectSwiftSelfExposure.DiagnosticId, StringComparison.Ordinal));

        foreach (var observation in scan.Observations)
            report.DegradedMembers.Add(CreateRow(report, observation));

        report.DegradedMembers.Sort(CompareRows);
        report.ExposureReportingVersion = DirectSwiftSelfExposure.ReportingVersion;
        report.ExposureCompleteness = new ExposureCompletenessReceipt
        {
            Version = DirectSwiftSelfExposure.ReportingVersion,
            Status = scan.ScannedFileCount > 0 && scan.UnresolvedSpecimens.Count == 0
                ? ExposureCompletenessStatus.Complete
                : ExposureCompletenessStatus.Invalid,
            ScannedFileCount = scan.ScannedFileCount,
            ClassifiedCallCount = scan.Observations.Count,
            UnresolvedCount = scan.UnresolvedSpecimens.Count,
            InventoryHash = scan.InventoryHash,
            UnresolvedSpecimens = scan.UnresolvedSpecimens.ToList(),
        };
        RecomputeSummary(report);
    }

    public static void RecomputeSummary(BindingReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var summary = new DegradedSurfaceSummary { Total = report.DegradedMembers.Count };
        foreach (var item in report.DegradedMembers)
        {
            summary.ByDiagnosticId[item.DiagnosticId] =
                summary.ByDiagnosticId.GetValueOrDefault(item.DiagnosticId) + 1;
            if (item.WrapperReason is { } reason)
                summary.ByWrapperReason[reason] = summary.ByWrapperReason.GetValueOrDefault(reason) + 1;
        }

        summary.TopDegradedMembers.AddRange(report.DegradedMembers
            .Where(item => item.IsAttributeEmitted && !item.IsDeprecated && item.ProminenceScore > 0)
            .OrderByDescending(item => item.ProminenceScore)
            .ThenBy(item => item.ContainingType, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(ReportCollector.TopDegradedMemberCount));
        summary.Exposure = BuildExposureSummary(report.DegradedMembers);
        report.DegradedSurface = summary;
    }

    public static DirectSwiftSelfExposureSummary BuildExposureSummary(
        IReadOnlyList<DegradedMemberItem> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var exposureRows = rows.Where(item =>
            string.Equals(item.DiagnosticId, DirectSwiftSelfExposure.DiagnosticId, StringComparison.Ordinal)).ToList();
        var summary = new DirectSwiftSelfExposureSummary
        {
            RowCount = exposureRows.Count,
            PublicMemberCount = exposureRows.Select(item => item.PublicApiKey ?? FallbackPublicKey(item))
                .Distinct(StringComparer.Ordinal).Count(),
            RootCount = exposureRows.Select(item => item.RootCauseId ?? item.DeclId ?? item.PublicApiKey ?? FallbackPublicKey(item))
                .Distinct(StringComparer.Ordinal).Count(),
            NativeCallCount = exposureRows.Select(item => item.NativeCall?.StableKey ?? FallbackNativeKey(item))
                .Distinct(StringComparer.Ordinal).Count(),
        };
        foreach (var row in exposureRows)
        {
            var route = row.Route ?? "unknown";
            summary.ByRoute[route] = summary.ByRoute.GetValueOrDefault(route) + 1;
            var reason = row.WrapperReason ?? "unknown";
            summary.ByReason[reason] = summary.ByReason.GetValueOrDefault(reason) + 1;
        }
        return summary;
    }

    private static DegradedMemberItem CreateRow(
        BindingReport report,
        DirectSwiftSelfObservation observation)
    {
        var skip = report.SkippedItems.FirstOrDefault(item =>
            string.Equals(item.ContainingType, observation.ContainingType, StringComparison.Ordinal)
            && string.Equals(item.Name, observation.SourceMemberName, StringComparison.Ordinal));
        var marker = report.DegradedMembers.FirstOrDefault(item =>
            item.IsAttributeEmitted
            && string.Equals(item.ContainingType, observation.ContainingType, StringComparison.Ordinal)
            && string.Equals(item.Name, observation.PublicName, StringComparison.Ordinal));

        string reason;
        string provenance;
        if (observation.NativeCall.CallKind == "FunctionPointer")
        {
            reason = "swift_function_pointer";
            provenance = "final_function_pointer_call";
        }
        else if (!string.IsNullOrWhiteSpace(skip?.Details))
        {
            reason = skip.Details!;
            provenance = "emitter_property_wrapper_rejection";
        }
        else if (!string.IsNullOrWhiteSpace(marker?.WrapperReason))
        {
            reason = marker.WrapperReason!;
            provenance = "emitted_safety_marker";
        }
        else if (observation.Route == "swift_silgen_wrapper")
        {
            reason = "deliberate_silgen_route";
            provenance = "final_call_form";
        }
        else
        {
            reason = "direct_swift_native";
            provenance = "final_call_form";
        }

        return new DegradedMemberItem
        {
            Kind = ParseKind(observation.DeclarationKind),
            Name = observation.PublicName,
            ContainingType = observation.ContainingType,
            DiagnosticId = DirectSwiftSelfExposure.DiagnosticId,
            IsAttributeEmitted = false,
            DeclId = skip?.DeclId ?? marker?.DeclId,
            RootCauseId = skip?.RootCauseId ?? marker?.RootCauseId,
            Accessor = observation.Accessor,
            PublicApiKey = observation.PublicApiKey,
            OriginalSwiftSymbol = observation.OriginalSwiftSymbol,
            NativeCall = observation.NativeCall,
            Route = observation.Route,
            SelfRole = observation.SelfRole,
            HasSwiftError = observation.HasSwiftError,
            WrapperReason = reason,
            WrapperReasonDescription = DescribeReason(reason),
            ReasonProvenance = provenance,
            EmitterSite = nameof(GeneratedSwiftCallReader),
            GeneratedFile = observation.GeneratedFile,
            GeneratedSpan = observation.GeneratedSpan,
            IsDeprecated = marker?.IsDeprecated ?? false,
            IsStatic = observation.IsStatic,
        };
    }

    private static BindingItemKind ParseKind(string kind) => kind switch
    {
        "property" => BindingItemKind.Property,
        "subscript" => BindingItemKind.Subscript,
        "operator" or "conversion" => BindingItemKind.Operator,
        _ => BindingItemKind.Method,
    };

    private static string DescribeReason(string reason) => reason switch
    {
        "swift_function_pointer" =>
            "a Swift-ABI function pointer returned by the public member is invoked with an untyped SwiftSelf closure context",
        "direct_swift_native" =>
            "the settled public member deliberately calls its original Swift symbol with an untyped SwiftSelf carrier",
        "deliberate_silgen_route" =>
            "the settled public member calls a Swift-ABI silgen wrapper with an untyped SwiftSelf carrier",
        _ => WrapperRejectionReasons.Describe(reason),
    };

    private static int CompareRows(DegradedMemberItem left, DegradedMemberItem right)
    {
        var prominence = right.ProminenceScore.CompareTo(left.ProminenceScore);
        if (prominence != 0) return prominence;
        var type = StringComparer.Ordinal.Compare(left.ContainingType ?? string.Empty, right.ContainingType ?? string.Empty);
        if (type != 0) return type;
        var name = StringComparer.Ordinal.Compare(left.Name, right.Name);
        if (name != 0) return name;
        var diagnostic = StringComparer.Ordinal.Compare(left.DiagnosticId, right.DiagnosticId);
        if (diagnostic != 0) return diagnostic;
        var accessor = StringComparer.Ordinal.Compare(left.Accessor ?? string.Empty, right.Accessor ?? string.Empty);
        if (accessor != 0) return accessor;
        return StringComparer.Ordinal.Compare(left.NativeCall?.StableKey ?? string.Empty, right.NativeCall?.StableKey ?? string.Empty);
    }

    private static string FallbackPublicKey(DegradedMemberItem item)
        => string.Join('\u001f', item.ContainingType ?? string.Empty, item.Name);

    private static string FallbackNativeKey(DegradedMemberItem item)
        => string.Join('\u001f', item.OriginalSwiftSymbol ?? string.Empty, item.Route ?? string.Empty);
}
