// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

public static class SurfaceAccountingSchema
{
    public const string Artifact = "surface-accounting/1";
    public const string Request = "surface-accounting-request/1";
}

public sealed record SurfaceTargetKey(
    string Library,
    string FrameworkModule,
    string Platform,
    string TargetName);

public sealed record SurfaceTargetRequest
{
    public required SurfaceTargetKey Key { get; init; }
    public required string OldDirectory { get; init; }
    public required string TipDirectory { get; init; }
    public string SurfaceLane { get; init; } = "swift-managed";
    public string Mode { get; init; } = "unknown";
    public int Tier { get; init; }
    public int KnownErrors { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> DerivedDependencies { get; init; } = [];
    public IReadOnlyList<string> WrapperDependencies { get; init; } = [];
    public string? NamespacePattern { get; init; }
    public string? InputRevision { get; init; }
    public string? InputVersion { get; init; }
    public string? ActualSdk { get; init; }
    public string? Architecture { get; init; }
    public string? TargetTriple { get; init; }
    public IReadOnlyList<string> PreprocessorSymbols { get; init; } = [];
}

public sealed record SurfaceCaptureRequest
{
    public string Schema { get; init; } = SurfaceAccountingSchema.Artifact;
    public required string CaptureId { get; init; }
    public required string Side { get; init; }
    public required string SourceSha { get; init; }
    public required string InputLockSha256 { get; init; }
    public required string ToolchainSha256 { get; init; }
    public required string EvidenceDirectory { get; init; }
    public string? DirtyPatchSha256 { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public required IReadOnlyList<SurfaceCommandReceipt> Commands { get; init; }
    public required IReadOnlyList<SurfaceTargetStageReceipt> TargetStages { get; init; }
    public string? BaselineBeforeSha256 { get; init; }
    public string? BaselineAfterSha256 { get; init; }
    public bool Complete { get; init; }
    public IReadOnlyList<string> IncompletenessReasons { get; init; } = [];
}

public sealed record SurfaceCommandReceipt(
    string Command,
    int ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string LogRelativePath,
    string LogSha256);

public sealed record SurfaceTargetStageReceipt(
    string TargetName,
    string Generation,
    string CSharpCompile,
    string SwiftCompile,
    string? Reason,
    string CaptureId,
    string SourceSha,
    string ToolchainSha256,
    string OutputTreeSha256);

public sealed record SurfaceAccountingRequest
{
    public string Schema { get; init; } = SurfaceAccountingSchema.Request;
    public required string CorpusId { get; init; }
    public required string ManifestPath { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string InputLockPath { get; init; }
    public required string InputLockSha256 { get; init; }
    public required int ExpectedTargetCount { get; init; }
    public required SurfaceCaptureRequest OldCapture { get; init; }
    public required SurfaceCaptureRequest TipCapture { get; init; }
    public required IReadOnlyList<SurfaceTargetRequest> Targets { get; init; }
    public IReadOnlyList<string> PreprocessorSymbols { get; init; } = [];
    public IReadOnlyList<SurfaceOriginCrosswalk> OriginCrosswalks { get; init; } = [];
}

public sealed record SurfaceFileReference(
    string FileSha256,
    string RelativePath,
    int? StartLine = null,
    int? EndLine = null,
    string? JsonPointer = null);

public sealed record SurfaceFileRecord(
    string Schema,
    string CaptureId,
    SurfaceTargetKey TargetKey,
    string SurfaceLane,
    string RelativePath,
    string Sha256,
    long Bytes,
    string Role,
    string ProducingStage);

public sealed record SurfaceParameterKey(
    string Type,
    string Modifier);

public sealed record SurfaceParameterDefaultShape(
    bool Optional,
    string? DefaultValue);

public sealed record SurfaceAccessorShape(
    string Kind,
    string Accessibility);

public sealed record SurfacePublicKey
{
    public required string Namespace { get; init; }
    public required IReadOnlyList<string> EnclosingTypes { get; init; }
    public required string DeclarationKind { get; init; }
    public required string Name { get; init; }
    public int GenericArity { get; init; }
    public required IReadOnlyList<SurfaceParameterKey> Parameters { get; init; }
    public bool Static { get; init; }
    public string? ExplicitInterface { get; init; }
    public string? ConversionDestination { get; init; }

    [JsonIgnore]
    public IReadOnlyList<string> ManifestGenericNames { get; init; } = [];

    [JsonIgnore]
    public string Digest => SurfaceCanonicalJson.Hash(this);

    [JsonIgnore]
    public string Display
    {
        get
        {
            var owner = string.Join('.', EnclosingTypes);
            var prefix = string.IsNullOrEmpty(owner) ? "" : owner + ".";
            var parameters = string.Join(',', Parameters.Select(p =>
                string.IsNullOrEmpty(p.Modifier) ? p.Type : $"{p.Modifier} {p.Type}"));
            var arity = GenericArity == 0 ? "" : $"``{GenericArity}";
            var destination = ConversionDestination is null ? "" : $"->{ConversionDestination}";
            return $"{Namespace}:{prefix}{Name}{arity}({parameters}){destination}";
        }
    }
}

public sealed record SurfacePublicShape
{
    public required string Accessibility { get; init; }
    public string? Type { get; init; }
    public string? ConstantValue { get; init; }
    public required IReadOnlyList<SurfaceParameterDefaultShape> ParameterDefaults { get; init; }
    public required IReadOnlyList<SurfaceAccessorShape> Accessors { get; init; }
    public required IReadOnlyList<string> Constraints { get; init; }
    public required IReadOnlyList<string> BaseTypes { get; init; }
    public required IReadOnlyList<string> Attributes { get; init; }
    public bool Tombstoned { get; init; }

    [JsonIgnore]
    public string Digest => SurfaceCanonicalJson.Hash(this);
}

public sealed record SurfaceOriginCrosswalk
{
    public string Schema { get; init; } = SurfaceAccountingSchema.Artifact;
    public required string Side { get; init; }
    public required SurfaceTargetKey TargetKey { get; init; }
    public required string SurfaceLane { get; init; }
    public required SurfacePublicKey PublicKey { get; init; }
    public required string OriginId { get; init; }
    public string? OriginShape { get; init; }
    public required SurfaceFileReference Evidence { get; init; }

    [JsonIgnore]
    public string QualifiedPublicKey => SurfaceCanonicalJson.Hash(new { TargetKey, SurfaceLane, PublicKey });
}

public sealed record SurfaceNativeBinding
{
    public required string Library { get; init; }
    public required string EntryPoint { get; init; }
    public required string Convention { get; init; }
    public required string AbiSignature { get; init; }
    public string? Accessor { get; init; }
    public required string Route { get; init; }
    public bool UntypedSwiftSelf { get; init; }
    public bool TypedSwiftSelf { get; init; }
    public bool SwiftError { get; init; }
    public required SurfaceFileReference Evidence { get; init; }
}

public sealed record SurfaceManifestBinding(
    string Signature,
    string RawSymbol,
    string MatchStatus,
    SurfaceFileReference Evidence);

public sealed record SurfaceMemberObservation
{
    public required string Schema { get; init; }
    public required string ObservationId { get; init; }
    public required string CaptureId { get; init; }
    public required SurfaceTargetKey TargetKey { get; init; }
    public required string Module { get; init; }
    public required string SurfaceLane { get; init; }
    public required SurfacePublicKey PublicKey { get; init; }
    public required SurfacePublicShape PublicShape { get; init; }
    public string? OriginId { get; init; }
    public string? OriginShape { get; init; }
    public required IReadOnlyList<SurfaceFileReference> SourceReferences { get; init; }
    public required IReadOnlyList<SurfaceManifestBinding> ApiManifest { get; init; }
    public required IReadOnlyList<SurfaceNativeBinding> NativeBindings { get; init; }
    public required string State { get; init; }
    public required string JoinStatus { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    [JsonIgnore]
    public string QualifiedKey => SurfaceCanonicalJson.Hash(new { TargetKey, SurfaceLane, PublicKey });
}

public sealed record SurfaceWithdrawalObservation
{
    public required string Schema { get; init; }
    public required string ObservationId { get; init; }
    public required string CaptureId { get; init; }
    public required SurfaceTargetKey TargetKey { get; init; }
    public string? CanonicalRecoveryId { get; init; }
    public string? DeclId { get; init; }
    public required string Scope { get; init; }
    public required string RawDisplay { get; init; }
    public int Multiplicity { get; init; } = 1;
    public required SurfaceFileReference OccurrenceReference { get; init; }
    public IReadOnlyList<SurfaceFileReference> ReportReferences { get; init; } = [];
    public IReadOnlyList<string> AffectedPublicKeys { get; init; } = [];
    public required string ResolutionStatus { get; init; }
}

public sealed record SurfaceCoverageRecord
{
    public required string Schema { get; init; }
    public required string CaptureId { get; init; }
    public required SurfaceTargetKey TargetKey { get; init; }
    public required string SurfaceLane { get; init; }
    public required IReadOnlyDictionary<string, string> StageStates { get; init; }
    public int SyntaxMembers { get; init; }
    public int ManifestMatched { get; init; }
    public int ManifestUnrecorded { get; init; }
    public int ManifestPhantom { get; init; }
    public int ManifestAmbiguous { get; init; }
    public int OriginJoined { get; init; }
    public int NativeBindingJoined { get; init; }
    public IReadOnlyDictionary<string, int?> ReportCounters { get; init; } = new Dictionary<string, int?>();
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed record SurfaceCaptureArtifacts(
    SurfaceCaptureRequest Capture,
    IReadOnlyList<SurfaceFileRecord> Files,
    IReadOnlyList<SurfaceMemberObservation> Members,
    IReadOnlyList<SurfaceWithdrawalObservation> Withdrawals,
    IReadOnlyList<SurfaceCoverageRecord> Coverage);

public sealed record SurfaceChange
{
    public required string Schema { get; init; }
    public required string ChangeId { get; init; }
    public required SurfaceTargetKey TargetKey { get; init; }
    public required string Axis { get; init; }
    public required string Classification { get; init; }
    public required IReadOnlyList<string> OldObservationIds { get; init; }
    public required IReadOnlyList<string> TipObservationIds { get; init; }
    public required IReadOnlyList<string> OldPublicKeys { get; init; }
    public required IReadOnlyList<string> TipPublicKeys { get; init; }
    public required IReadOnlyList<string> OriginIds { get; init; }
    public required IReadOnlyList<SurfaceFileReference> EvidenceReferences { get; init; }
    public required string Certainty { get; init; }
    public required string Cause { get; init; }
    public required string OwnerPacket { get; init; }
    public required string Disposition { get; init; }
    public string? ReopenTrigger { get; init; }
    public IReadOnlyList<string> ValidationReferences { get; init; } = [];
}

public sealed record SurfaceComparisonSummary
{
    public required string Schema { get; init; }
    public required string ComparisonId { get; init; }
    public required string OldCaptureId { get; init; }
    public required string TipCaptureId { get; init; }
    public bool CorpusEquivalent { get; init; }
    public bool InputEquivalent { get; init; }
    public bool ToolchainEquivalent { get; init; }
    public bool Complete { get; init; }
    public int RequiredTargets { get; init; }
    public int ComparableTargets { get; init; }
    public int PublicAdditions { get; init; }
    public int PublicRemovals { get; init; }
    public int ShapeChanges { get; init; }
    public int AccessorLosses { get; init; }
    public int ManifestSymbolChanges { get; init; }
    public int DispatchRetargets { get; init; }
    public int WithdrawalAdditions { get; init; }
    public int WithdrawalRemovals { get; init; }
    public int UnresolvedChanges { get; init; }
}

public sealed record SurfaceComparisonArtifacts(
    SurfaceComparisonSummary Summary,
    IReadOnlyList<SurfaceChange> Changes,
    IReadOnlyList<SurfaceOriginCrosswalk> Crosswalks);

public sealed record SurfaceAccountingResult(
    SurfaceAccountingRequest Request,
    SurfaceCaptureArtifacts Old,
    SurfaceCaptureArtifacts Tip,
    SurfaceComparisonArtifacts Comparison);
