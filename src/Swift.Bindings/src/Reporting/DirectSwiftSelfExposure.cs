// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>Stable contract constants for direct untyped-SwiftSelf reporting.</summary>
public static class DirectSwiftSelfExposure
{
    public const int ReportingVersion = 1;
    public const string DiagnosticId = "DirectSwiftSelfExposure";
}

/// <summary>The native call reached by one emitted public member/accessor.</summary>
public sealed record DirectSwiftSelfNativeCall
{
    public required string CallKind { get; init; }
    public required string ManagedHolder { get; init; }
    public required string ManagedName { get; init; }
    public string? Library { get; init; }
    public string? EntryPoint { get; init; }
    public required string Convention { get; init; }
    public required string ReturnCarrier { get; init; }
    public required IReadOnlyList<string> ParameterCarriers { get; init; }
    public required string StableKey { get; init; }
}

/// <summary>Line range in the generated C# file that contains the public consumer.</summary>
public sealed record GeneratedSourceSpan
{
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
}

/// <summary>
/// One final-file observation used by both the generator-side collector and the independent
/// compile-only gate. This type is intentionally independent of emitter/recovery models so the
/// Nuke build can link-compile the reader without referencing the generator assembly.
/// </summary>
public sealed record DirectSwiftSelfObservation
{
    public required string PublicApiKey { get; init; }
    public required string PublicName { get; init; }
    public required string ContainingType { get; init; }
    public required string DeclarationKind { get; init; }
    public required bool IsStatic { get; init; }
    public string? Accessor { get; init; }
    public required string SourceMemberName { get; init; }
    public string? OriginalSwiftSymbol { get; init; }
    public required DirectSwiftSelfNativeCall NativeCall { get; init; }
    public required string Route { get; init; }
    public required string SelfRole { get; init; }
    public required bool HasSwiftError { get; init; }
    public required string GeneratedFile { get; init; }
    public required GeneratedSourceSpan GeneratedSpan { get; init; }

    public string IdentityKey => string.Join('\u001f',
        PublicApiKey,
        Accessor ?? string.Empty,
        NativeCall.StableKey,
        Route,
        SelfRole);
}

/// <summary>Result of scanning the settled generated C# inventory.</summary>
public sealed record GeneratedSwiftCallScanResult
{
    public required int ScannedFileCount { get; init; }
    public required string InventoryHash { get; init; }
    public required IReadOnlyList<DirectSwiftSelfObservation> Observations { get; init; }
    public required IReadOnlyList<string> UnresolvedSpecimens { get; init; }
}

/// <summary>Versioned proof that the final generated files were classified.</summary>
public sealed class ExposureCompletenessReceipt
{
    public int Version { get; init; } = DirectSwiftSelfExposure.ReportingVersion;
    public ExposureCompletenessStatus Status { get; init; } = ExposureCompletenessStatus.Unknown;
    public int ScannedFileCount { get; init; }
    public int ClassifiedCallCount { get; init; }
    public int UnresolvedCount { get; init; }
    public string? InventoryHash { get; init; }
    public List<string> UnresolvedSpecimens { get; init; } = new();
}

public enum ExposureCompletenessStatus
{
    Unknown,
    Complete,
    Invalid,
}

/// <summary>Roll-up over final <see cref="DirectSwiftSelfExposure.DiagnosticId"/> rows.</summary>
public sealed class DirectSwiftSelfExposureSummary
{
    public int RowCount { get; set; }
    public int PublicMemberCount { get; set; }
    public int RootCount { get; set; }
    public int NativeCallCount { get; set; }
    public Dictionary<string, int> ByRoute { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ByReason { get; } = new(StringComparer.Ordinal);
}
