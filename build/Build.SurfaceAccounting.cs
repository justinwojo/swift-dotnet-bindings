// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using Nuke.Common;
using Serilog;

partial class Build
{
    [Parameter("Path to an externally built surface-accounting-request/1 JSON file (checked against the live manifest roster). Exclusive with --surface-old")]
    readonly string? SurfaceRequest;

    [Parameter("Old side of the surface comparison: a kept `nuke validate` output base holding surface-capture.json. The tip side is this invocation's validate. Exclusive with --surface-request")]
    readonly string? SurfaceOld;

    [Parameter("Why the old capture's source is not the baseline's git_sha; written into the baseline as surface_bootstrap")]
    readonly string? SurfaceBootstrapJustification;

    [Parameter("New output directory for immutable surface-accounting/1 artifacts")]
    readonly string? SurfaceOutput;

    // Set by a full Validate in this invocation: its output base and the capture it wrote there.
    SurfaceValidateCapture? surfaceInvocationCapture;
    string? surfaceInvocationOutputBase;

    Target SurfaceAccounting => _ => _
        // Ordering only: keep standalone sinks totally ordered for Nuke --strict.
        // When Validate and SurfaceAccounting share an invocation, Validate must create the
        // process-local promotion candidate and its surface capture before this target runs.
        .After(Validate, ReleaseGatesAttest)
        .Executes(() =>
        {
            if (string.IsNullOrWhiteSpace(SurfaceRequest) == string.IsNullOrWhiteSpace(SurfaceOld))
                throw new ArgumentException("Pass exactly one of --surface-old or --surface-request.");
            if (string.IsNullOrWhiteSpace(SurfaceOutput))
                throw new ArgumentException("--surface-output is required.");

            SurfaceAccountingRequest request;
            if (!string.IsNullOrWhiteSpace(SurfaceOld))
                request = SurfaceRequestBuilder.Build(ManifestPath, SurfaceOld,
                    surfaceInvocationOutputBase, surfaceInvocationCapture);
            else
            {
                var requestPath = Path.GetFullPath(SurfaceRequest!);
                request = SurfaceCanonicalJson.DeserializeStrict<SurfaceAccountingRequest>(requestPath);
                request = ResolvePaths(request, Path.GetDirectoryName(requestPath)!);
            }
            var roster = SurfaceRequestBuilder.ValidateLiveRoster(request, ManifestPath);
            Log.Information("Surface accounting roster: manifest {Sha}, {Libraries} libraries, {Modules} modules, {Targets} targets.",
                roster.ManifestSha256, roster.Libraries, roster.Modules, roster.Targets);

            var result = SurfaceAccountingEngine.Analyze(request);
            var schemaPath = RootDirectory / "build" / "SurfaceAccounting" / "schema.surface-accounting-1.json";
            SurfaceAccountingWriter.Write(result, Path.GetFullPath(SurfaceOutput), schemaPath);

            var summary = result.Comparison.Summary;
            Log.Information(
                "Surface accounting wrote {Output}: {Comparable}/{Required} comparable target(s), " +
                "{Removed} removal(s), {Added} addition(s), {Shape} shape/accessor change(s), {Unresolved} unresolved change(s).",
                Path.GetFullPath(SurfaceOutput),
                summary.ComparableTargets,
                summary.RequiredTargets,
                summary.PublicRemovals,
                summary.PublicAdditions,
                summary.ShapeChanges + summary.AccessorLosses,
                summary.UnresolvedChanges);
            if (!summary.Complete)
                throw new Exception(
                    "Surface-accounting comparison is incomplete. Retained artifacts name the missing or unresolved evidence.");

            var decision = SurfacePromotionReceipt.Decide(request, surfaceInvocationCapture, surfaceInvocationOutputBase,
                ValidationBaseline.Load(BaselinePath).GitSha, SurfaceBootstrapJustification);
            switch (decision.Verdict)
            {
                case SurfaceReceiptVerdict.NotBound:
                    Log.Warning("{Message} Artifacts were written; the validation baseline cannot be promoted from them.", decision.Message);
                    return;
                case SurfaceReceiptVerdict.Refused:
                    throw new Exception(decision.Message);
                case SurfaceReceiptVerdict.Bootstrap:
                    Log.Warning("{Message} Justification: {Justification}", decision.Message, decision.BootstrapJustification);
                    validationPromotion?.RecordSurfaceBootstrap(decision.BootstrapOldSource!, decision.BootstrapJustification!);
                    break;
                default:
                    Log.Information("{Message}", decision.Message);
                    break;
            }
            validationPromotion?.Record("SurfaceAccounting");
        });

    private static SurfaceAccountingRequest ResolvePaths(SurfaceAccountingRequest request, string requestDirectory)
        => request with
        {
            ManifestPath = ResolvePath(requestDirectory, request.ManifestPath),
            InputLockPath = ResolvePath(requestDirectory, request.InputLockPath),
            OldCapture = request.OldCapture with
            {
                EvidenceDirectory = ResolvePath(requestDirectory, request.OldCapture.EvidenceDirectory),
            },
            TipCapture = request.TipCapture with
            {
                EvidenceDirectory = ResolvePath(requestDirectory, request.TipCapture.EvidenceDirectory),
            },
            Targets = request.Targets.Select(target => target with
            {
                OldDirectory = ResolvePath(requestDirectory, target.OldDirectory),
                TipDirectory = ResolvePath(requestDirectory, target.TipDirectory),
            }).ToList(),
        };

    private static string ResolvePath(string root, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
}
