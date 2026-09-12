// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

public static class SurfaceAccountingComparer
{
    public static SurfaceComparisonArtifacts Compare(
        SurfaceAccountingRequest request,
        SurfaceCaptureArtifacts oldArtifacts,
        SurfaceCaptureArtifacts tipArtifacts)
    {
        var comparisonId = SurfaceCanonicalJson.Hash(new
        {
            schema = SurfaceAccountingSchema.Artifact,
            corpus = request.CorpusId,
            oldCapture = oldArtifacts.Capture.CaptureId,
            tipCapture = tipArtifacts.Capture.CaptureId,
        });
        var inputEquivalent = string.Equals(
            oldArtifacts.Capture.InputLockSha256,
            tipArtifacts.Capture.InputLockSha256,
            StringComparison.Ordinal);
        var toolchainEquivalent = string.Equals(
            oldArtifacts.Capture.ToolchainSha256,
            tipArtifacts.Capture.ToolchainSha256,
            StringComparison.Ordinal);
        var comparableTargets = ComparableTargets(request, oldArtifacts, tipArtifacts, inputEquivalent, toolchainEquivalent);
        var changes = new List<SurfaceChange>();

        foreach (var target in comparableTargets.OrderBy(k => k, StringComparer.Ordinal))
        {
            var oldMembers = oldArtifacts.Members.Where(m => TargetDigest(m.TargetKey) == target)
                .ToDictionary(m => m.QualifiedKey, StringComparer.Ordinal);
            var tipMembers = tipArtifacts.Members.Where(m => TargetDigest(m.TargetKey) == target)
                .ToDictionary(m => m.QualifiedKey, StringComparer.Ordinal);
            foreach (var key in oldMembers.Keys.Union(tipMembers.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
            {
                oldMembers.TryGetValue(key, out var oldMember);
                tipMembers.TryGetValue(key, out var tipMember);
                if (oldMember is null)
                {
                    changes.Add(Change(comparisonId, tipMember!.TargetKey, "public", "addition", [], [tipMember]));
                    continue;
                }
                if (tipMember is null)
                {
                    changes.Add(Change(comparisonId, oldMember.TargetKey, "public", "removal", [oldMember], []));
                    continue;
                }

                if (!string.Equals(oldMember.PublicShape.Digest, tipMember.PublicShape.Digest, StringComparison.Ordinal))
                {
                    var oldAccessors = oldMember.PublicShape.Accessors.Select(a => a.Kind).ToHashSet(StringComparer.Ordinal);
                    var tipAccessors = tipMember.PublicShape.Accessors.Select(a => a.Kind).ToHashSet(StringComparer.Ordinal);
                    var lostAccessors = oldAccessors.Except(tipAccessors).OrderBy(a => a, StringComparer.Ordinal).ToList();
                    if (lostAccessors.Count > 0)
                        changes.Add(Change(
                            comparisonId,
                            oldMember.TargetKey,
                            "public",
                            "accessor-loss",
                            [oldMember],
                            [tipMember],
                            cause: $"lost accessors: {string.Join(", ", lostAccessors)}"));
                    var oldNonAccessorShape = oldMember.PublicShape with { Accessors = [] };
                    var tipNonAccessorShape = tipMember.PublicShape with { Accessors = [] };
                    if (lostAccessors.Count == 0
                        || !string.Equals(oldNonAccessorShape.Digest, tipNonAccessorShape.Digest, StringComparison.Ordinal))
                        changes.Add(Change(
                            comparisonId,
                            oldMember.TargetKey,
                            "public",
                            "shape-change",
                            [oldMember],
                            [tipMember]));
                }

                if (!string.Equals(oldMember.State, tipMember.State, StringComparison.Ordinal))
                    changes.Add(Change(comparisonId, oldMember.TargetKey, "public", "state-change", [oldMember], [tipMember]));

                CompareManifest(comparisonId, oldMember, tipMember, changes);
                CompareDispatch(comparisonId, oldMember, tipMember, changes);
            }

            CompareWithdrawals(
                comparisonId,
                oldArtifacts.Withdrawals.Where(w => TargetDigest(w.TargetKey) == target).ToList(),
                tipArtifacts.Withdrawals.Where(w => TargetDigest(w.TargetKey) == target).ToList(),
                changes);
        }

        foreach (var unresolved in oldArtifacts.Withdrawals.Concat(tipArtifacts.Withdrawals)
                     .Where(w => w.CanonicalRecoveryId is null))
        {
            changes.Add(WithdrawalChange(
                comparisonId,
                unresolved.TargetKey,
                "unresolved-withdrawal",
                unresolved.CaptureId == oldArtifacts.Capture.CaptureId ? [unresolved] : [],
                unresolved.CaptureId == tipArtifacts.Capture.CaptureId ? [unresolved] : [],
                "evidence multiplicity did not uniquely identify a canonical recovery root"));
        }

        changes = changes.OrderBy(c => c.TargetKey.TargetName, StringComparer.Ordinal)
            .ThenBy(c => c.Axis, StringComparer.Ordinal)
            .ThenBy(c => c.Classification, StringComparer.Ordinal)
            .ThenBy(c => c.ChangeId, StringComparer.Ordinal)
            .ToList();
        var summary = new SurfaceComparisonSummary
        {
            Schema = SurfaceAccountingSchema.Artifact,
            ComparisonId = comparisonId,
            OldCaptureId = oldArtifacts.Capture.CaptureId,
            TipCaptureId = tipArtifacts.Capture.CaptureId,
            CorpusEquivalent = true,
            InputEquivalent = inputEquivalent,
            ToolchainEquivalent = toolchainEquivalent,
            Complete = oldArtifacts.Capture.Complete
                && tipArtifacts.Capture.Complete
                && inputEquivalent
                && toolchainEquivalent
                && comparableTargets.Count == request.ExpectedTargetCount
                && changes.All(c => !c.Classification.StartsWith("unresolved", StringComparison.Ordinal)),
            RequiredTargets = request.ExpectedTargetCount,
            ComparableTargets = comparableTargets.Count,
            PublicAdditions = changes.Count(c => c.Classification == "addition"),
            PublicRemovals = changes.Count(c => c.Classification == "removal"),
            ShapeChanges = changes.Count(c => c.Classification == "shape-change"),
            AccessorLosses = changes.Count(c => c.Classification == "accessor-loss"),
            ManifestSymbolChanges = changes.Count(c => c.Classification == "manifest-symbol-change"),
            DispatchRetargets = changes.Count(c => c.Classification is "route-change" or "semantic-retarget" or "unresolved-retarget"),
            WithdrawalAdditions = changes.Count(c => c.Classification == "withdrawal-addition"),
            WithdrawalRemovals = changes.Count(c => c.Classification == "withdrawal-removal"),
            UnresolvedChanges = changes.Count(c => c.Classification.StartsWith("unresolved", StringComparison.Ordinal)),
        };
        var crosswalks = request.OriginCrosswalks
            .OrderBy(crosswalk => crosswalk.Side, StringComparer.Ordinal)
            .ThenBy(crosswalk => crosswalk.TargetKey.TargetName, StringComparer.Ordinal)
            .ThenBy(crosswalk => crosswalk.PublicKey.Display, StringComparer.Ordinal)
            .ToList();
        return new SurfaceComparisonArtifacts(summary, changes, crosswalks);
    }

    private static HashSet<string> ComparableTargets(
        SurfaceAccountingRequest request,
        SurfaceCaptureArtifacts oldArtifacts,
        SurfaceCaptureArtifacts tipArtifacts,
        bool inputEquivalent,
        bool toolchainEquivalent)
    {
        if (!inputEquivalent || !toolchainEquivalent) return [];
        var oldCoverage = oldArtifacts.Coverage.ToDictionary(c => TargetDigest(c.TargetKey), StringComparer.Ordinal);
        var tipCoverage = tipArtifacts.Coverage.ToDictionary(c => TargetDigest(c.TargetKey), StringComparer.Ordinal);
        return request.Targets.Select(t => TargetDigest(t.Key)).Where(key =>
                oldCoverage.TryGetValue(key, out var oldTarget)
                && tipCoverage.TryGetValue(key, out var tipTarget)
                && oldTarget.Errors.Count == 0
                && tipTarget.Errors.Count == 0
                && CompletedStages(oldTarget.StageStates)
                && CompletedStages(tipTarget.StageStates))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool CompletedStages(IReadOnlyDictionary<string, string> stages)
        => stages.TryGetValue("generation", out var generation)
            && string.Equals(generation, "success", StringComparison.Ordinal)
            && stages.TryGetValue("csharp_compile", out var csharp)
            && string.Equals(csharp, "success", StringComparison.Ordinal)
            && stages.TryGetValue("swift_compile", out var swift)
            && swift is "success" or "not-applicable";

    private static void CompareManifest(
        string comparisonId,
        SurfaceMemberObservation oldMember,
        SurfaceMemberObservation tipMember,
        List<SurfaceChange> changes)
    {
        var oldRows = oldMember.ApiManifest.ToDictionary(m => m.Signature, m => m.RawSymbol, StringComparer.Ordinal);
        var tipRows = tipMember.ApiManifest.ToDictionary(m => m.Signature, m => m.RawSymbol, StringComparer.Ordinal);
        foreach (var signature in oldRows.Keys.Union(tipRows.Keys, StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            oldRows.TryGetValue(signature, out var oldSymbol);
            tipRows.TryGetValue(signature, out var tipSymbol);
            if (oldSymbol is not null
                && tipSymbol is not null
                && string.Equals(oldSymbol, tipSymbol, StringComparison.Ordinal))
                continue;
            changes.Add(Change(
                comparisonId,
                oldMember.TargetKey,
                "dispatch",
                "manifest-symbol-change",
                [oldMember],
                [tipMember],
                cause: $"{signature}: {oldSymbol ?? "<missing>"} -> {tipSymbol ?? "<missing>"}"));
        }
    }

    private static void CompareDispatch(
        string comparisonId,
        SurfaceMemberObservation oldMember,
        SurfaceMemberObservation tipMember,
        List<SurfaceChange> changes)
    {
        var oldBindings = oldMember.NativeBindings.Select(NativeIdentity).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var tipBindings = tipMember.NativeBindings.Select(NativeIdentity).OrderBy(v => v, StringComparer.Ordinal).ToList();
        if (oldBindings.SequenceEqual(tipBindings, StringComparer.Ordinal)) return;

        var classification = oldMember.OriginId is not null && tipMember.OriginId is not null
            ? string.Equals(oldMember.OriginId, tipMember.OriginId, StringComparison.Ordinal)
                ? SameNativeContractExceptSymbol(oldMember.NativeBindings, tipMember.NativeBindings)
                    ? "symbol-only"
                    : IsProvenDirectSwiftToCdecl(oldMember.NativeBindings, tipMember.NativeBindings)
                        ? "route-change"
                        : "unresolved-retarget"
                : "semantic-retarget"
            : "unresolved-retarget";
        changes.Add(Change(
            comparisonId,
            oldMember.TargetKey,
            "dispatch",
            classification,
            [oldMember],
            [tipMember],
            cause: "native call set changed"));
    }

    private static void CompareWithdrawals(
        string comparisonId,
        IReadOnlyList<SurfaceWithdrawalObservation> oldRows,
        IReadOnlyList<SurfaceWithdrawalObservation> tipRows,
        List<SurfaceChange> changes)
    {
        var oldById = oldRows.Where(w => w.CanonicalRecoveryId is not null)
            .ToDictionary(w => w.CanonicalRecoveryId!, StringComparer.Ordinal);
        var tipById = tipRows.Where(w => w.CanonicalRecoveryId is not null)
            .ToDictionary(w => w.CanonicalRecoveryId!, StringComparer.Ordinal);
        foreach (var id in oldById.Keys.Union(tipById.Keys, StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal))
        {
            oldById.TryGetValue(id, out var oldRow);
            tipById.TryGetValue(id, out var tipRow);
            if (oldRow is null)
                changes.Add(WithdrawalChange(comparisonId, tipRow!.TargetKey, "withdrawal-addition", [], [tipRow]));
            else if (tipRow is null)
                changes.Add(WithdrawalChange(comparisonId, oldRow.TargetKey, "withdrawal-removal", [oldRow], []));
        }
    }

    private static SurfaceChange Change(
        string comparisonId,
        SurfaceTargetKey targetKey,
        string axis,
        string classification,
        IReadOnlyList<SurfaceMemberObservation> oldMembers,
        IReadOnlyList<SurfaceMemberObservation> tipMembers,
        string cause = "pending")
    {
        var oldIds = oldMembers.Select(m => m.ObservationId).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var tipIds = tipMembers.Select(m => m.ObservationId).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var oldKeys = oldMembers.Select(m => m.PublicKey.Display).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var tipKeys = tipMembers.Select(m => m.PublicKey.Display).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var origins = oldMembers.Concat(tipMembers).Select(m => m.OriginId).OfType<string>()
            .Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var evidence = oldMembers.Concat(tipMembers).SelectMany(m => m.SourceReferences)
            .Concat(oldMembers.Concat(tipMembers).SelectMany(m => m.ApiManifest.Select(a => a.Evidence)))
            .Distinct().OrderBy(e => e.RelativePath, StringComparer.Ordinal).ThenBy(e => e.StartLine).ToList();
        var changeId = SurfaceCanonicalJson.Hash(new
        {
            comparisonId,
            targetKey,
            axis,
            classification,
            oldObservationIds = oldIds,
            tipObservationIds = tipIds,
            cause,
        });
        return new SurfaceChange
        {
            Schema = SurfaceAccountingSchema.Artifact,
            ChangeId = changeId,
            TargetKey = targetKey,
            Axis = axis,
            Classification = classification,
            OldObservationIds = oldIds,
            TipObservationIds = tipIds,
            OldPublicKeys = oldKeys,
            TipPublicKeys = tipKeys,
            OriginIds = origins,
            EvidenceReferences = evidence,
            Certainty = classification.StartsWith("unresolved", StringComparison.Ordinal) ? "observed-unresolved" : "observed",
            Cause = cause,
            OwnerPacket = axis == "dispatch" ? "P3" : "P2",
            Disposition = "pending",
        };
    }

    private static SurfaceChange WithdrawalChange(
        string comparisonId,
        SurfaceTargetKey targetKey,
        string classification,
        IReadOnlyList<SurfaceWithdrawalObservation> oldRows,
        IReadOnlyList<SurfaceWithdrawalObservation> tipRows,
        string cause = "pending")
    {
        var oldIds = oldRows.Select(w => w.ObservationId).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var tipIds = tipRows.Select(w => w.ObservationId).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var evidence = oldRows.Concat(tipRows).Select(w => w.OccurrenceReference)
            .Concat(oldRows.Concat(tipRows).SelectMany(w => w.ReportReferences))
            .Distinct().OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToList();
        var changeId = SurfaceCanonicalJson.Hash(new
        {
            comparisonId,
            targetKey,
            axis = "withdrawal",
            classification,
            oldObservationIds = oldIds,
            tipObservationIds = tipIds,
            cause,
        });
        return new SurfaceChange
        {
            Schema = SurfaceAccountingSchema.Artifact,
            ChangeId = changeId,
            TargetKey = targetKey,
            Axis = "withdrawal",
            Classification = classification,
            OldObservationIds = oldIds,
            TipObservationIds = tipIds,
            OldPublicKeys = oldRows.SelectMany(w => w.AffectedPublicKeys).Distinct().Order().ToList(),
            TipPublicKeys = tipRows.SelectMany(w => w.AffectedPublicKeys).Distinct().Order().ToList(),
            OriginIds = oldRows.Concat(tipRows).Select(w => w.DeclId).OfType<string>().Distinct().Order().ToList(),
            EvidenceReferences = evidence,
            Certainty = classification.StartsWith("unresolved", StringComparison.Ordinal) ? "observed-unresolved" : "observed",
            Cause = cause,
            OwnerPacket = "P2",
            Disposition = "pending",
        };
    }

    private static string TargetDigest(SurfaceTargetKey key) => SurfaceCanonicalJson.Hash(key);

    private static string NativeIdentity(SurfaceNativeBinding binding)
        => $"{NativeContractExceptSymbol(binding)}\u001f{binding.EntryPoint}";

    private static bool SameNativeContractExceptSymbol(
        IReadOnlyList<SurfaceNativeBinding> oldBindings,
        IReadOnlyList<SurfaceNativeBinding> tipBindings)
        => oldBindings.Select(NativeContractExceptSymbol).OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(
                tipBindings.Select(NativeContractExceptSymbol).OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static bool IsProvenDirectSwiftToCdecl(
        IReadOnlyList<SurfaceNativeBinding> oldBindings,
        IReadOnlyList<SurfaceNativeBinding> tipBindings)
    {
        if (oldBindings.Count == 0 || oldBindings.Count != tipBindings.Count
            || oldBindings.Any(binding => binding.Route != "direct-swift")
            || tipBindings.Any(binding => binding.Route != "cdecl"))
            return false;

        static IEnumerable<string> Accessors(IReadOnlyList<SurfaceNativeBinding> bindings)
            => bindings.Select(binding => binding.Accessor ?? "")
                .OrderBy(accessor => accessor, StringComparer.Ordinal);

        return oldBindings.GroupBy(binding => binding.Accessor ?? "", StringComparer.Ordinal).All(group => group.Count() == 1)
            && tipBindings.GroupBy(binding => binding.Accessor ?? "", StringComparer.Ordinal).All(group => group.Count() == 1)
            && Accessors(oldBindings).SequenceEqual(Accessors(tipBindings), StringComparer.Ordinal);
    }

    private static string NativeContractExceptSymbol(SurfaceNativeBinding binding)
        => $"{binding.Accessor}\u001f{binding.Library}\u001f{binding.Convention}\u001f{binding.AbiSignature}" +
            $"\u001f{binding.Route}\u001f{binding.UntypedSwiftSelf}\u001f{binding.TypedSwiftSelf}\u001f{binding.SwiftError}";
}
