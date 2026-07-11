// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ReleaseGatesManifest"/> — the composed-release-gate result manifest. The
/// load-bearing guarantees under test are (1) <b>skip ≠ pass</b>: an all-passed run that still
/// carries undispositioned skips is never <c>ship_ready</c>; (2) <b>any fail ⇒ non-zero exit</b>
/// via the pure <see cref="ReleaseGatesManifest.RecommendedExitCode"/>; (3) <b>fail-closed
/// catalog</b>: a dropped / duplicated / malformed leg row is an integrity failure; and (4)
/// aggregation is <b>never trusted from disk</b> — a tampered outcome key cannot flip the verdict.
/// Tests drive the seed factory (not hand-built rows) so they prove the orchestrator's own catalog,
/// not a re-statement of it.
/// </summary>
public class ReleaseGatesManifestTests
{
    // Seed, then replace the four executed legs with the given status — the shape a real run produces.
    private static ReleaseGatesManifest SeedWithExecuted(params (string Id, GateLeg Leg)[] overrides)
    {
        var m = ReleaseGatesManifest.Seed(generatedUtc: "2026-07-10T00:00:00Z", gitSha: "abc1234", host: "test-host");
        foreach (var (_, leg) in overrides)
            m = m.WithLeg(leg);
        return m;
    }

    private static ReleaseGatesManifest AllExecutedPassed()
        => SeedWithExecuted(
            (ReleaseGatesManifest.LegIds.UnitTests, GateLeg.Pass(ReleaseGatesManifest.LegIds.UnitTests, 100)),
            (ReleaseGatesManifest.LegIds.BindingTestsCompileOnly, GateLeg.Pass(ReleaseGatesManifest.LegIds.BindingTestsCompileOnly, 200)),
            (ReleaseGatesManifest.LegIds.PackGate, GateLeg.Pass(ReleaseGatesManifest.LegIds.PackGate, 300)),
            (ReleaseGatesManifest.LegIds.AppStoreHygieneStructural, GateLeg.Pass(ReleaseGatesManifest.LegIds.AppStoreHygieneStructural, 400)));

    // ---- Catalog shape ----

    [Fact]
    public void Seed_produces_exactly_the_canonical_catalog()
    {
        var m = ReleaseGatesManifest.Seed();

        Assert.Equal(
            ReleaseGatesManifest.CanonicalCatalog.OrderBy(x => x),
            m.Legs.Select(l => l.Id).OrderBy(x => x));
        // No duplicate ids.
        Assert.Equal(m.Legs.Count, m.Legs.Select(l => l.Id).Distinct().Count());
        // A freshly seeded catalog is structurally sound.
        Assert.Empty(m.Validate());
    }

    [Fact]
    public void Seed_marks_executed_legs_not_reached_and_others_skipped()
    {
        var m = ReleaseGatesManifest.Seed();

        foreach (var id in ReleaseGatesManifest.ExecutedLegIds)
        {
            var leg = m.Legs.Single(l => l.Id == id);
            Assert.Equal(GateLegStatus.Fail, leg.Status);
            Assert.Equal(GateLegReasonCode.OrchestratorNotReached, leg.ReasonCode);
        }

        var skipIds = ReleaseGatesManifest.CanonicalCatalog.Except(ReleaseGatesManifest.ExecutedLegIds);
        foreach (var id in skipIds)
        {
            var leg = m.Legs.Single(l => l.Id == id);
            Assert.Equal(GateLegStatus.Skipped, leg.Status);
            Assert.Equal(GateLegReasonCode.NotRunInThisInvocation, leg.ReasonCode);
            Assert.False(string.IsNullOrWhiteSpace(leg.Reason));
        }
    }

    [Fact]
    public void An_unreached_seed_is_loud_any_fail_and_non_zero_exit()
    {
        // The crash-before-any-leg case: nothing overridden ⇒ the four executed legs are still
        // fail(not-reached), so an aborted run never looks green.
        var m = ReleaseGatesManifest.Seed();

        Assert.True(m.AnyFailed);
        Assert.Equal(ReleaseGatesManifest.OutcomeFailed, m.ExecutionOutcome);
        Assert.Equal(1, m.RecommendedExitCode());
    }

    // ---- skip ≠ pass (the load-bearing case) ----

    [Fact]
    public void All_executed_passed_with_skips_is_passed_but_not_ship_ready()
    {
        var m = AllExecutedPassed();

        Assert.False(m.AnyFailed);
        Assert.Equal(ReleaseGatesManifest.OutcomePassed, m.ExecutionOutcome);
        // The four not-run legs are undispositioned ⇒ catalog is incomplete ⇒ NOT ship-ready,
        // even though nothing that ran failed. This is skip ≠ pass.
        Assert.Equal(ReleaseGatesManifest.CompletenessIncomplete, m.CatalogCompleteness);
        Assert.False(m.ShipReady);
        Assert.Equal(
            new[]
            {
                ReleaseGatesManifest.LegIds.AppStoreHygieneSignedIpa,
                ReleaseGatesManifest.LegIds.BindingTestsDevice,
                ReleaseGatesManifest.LegIds.MixedDirect,
                ReleaseGatesManifest.LegIds.MixedPack,
            }.OrderBy(x => x),
            m.UndispositionedSkipIds.OrderBy(x => x));
    }

    [Fact]
    public void All_executed_passed_default_exits_zero_but_require_complete_exits_non_zero()
    {
        var m = AllExecutedPassed();

        // Default: intentional skips do not fail the target.
        Assert.Equal(0, m.RecommendedExitCode(requireComplete: false));
        // RC-strict: undispositioned skips block.
        Assert.Equal(1, m.RecommendedExitCode(requireComplete: true));
    }

    [Fact]
    public void Any_executed_fail_is_failed_and_non_zero_exit()
    {
        var m = AllExecutedPassed()
            .WithLeg(GateLeg.Fail(ReleaseGatesManifest.LegIds.PackGate, "pack-gate exited 1"));

        Assert.True(m.AnyFailed);
        Assert.Equal(ReleaseGatesManifest.OutcomeFailed, m.ExecutionOutcome);
        Assert.False(m.ShipReady);
        Assert.Equal(1, m.RecommendedExitCode(requireComplete: false));
        Assert.Equal(1, m.RecommendedExitCode(requireComplete: true));
    }

    [Fact]
    public void Fail_dominates_skip_regardless_of_disposition()
    {
        // Even a fully-dispositioned catalog with one hard failure is failed / non-ship-ready.
        var m = DispositionAllSkips(AllExecutedPassed())
            .WithLeg(GateLeg.Fail(ReleaseGatesManifest.LegIds.UnitTests, "a unit test failed"));

        Assert.True(m.AnyFailed);
        Assert.Equal(ReleaseGatesManifest.OutcomeFailed, m.ExecutionOutcome);
        Assert.Equal(ReleaseGatesManifest.CompletenessComplete, m.CatalogCompleteness); // skips dispositioned
        Assert.False(m.ShipReady);                                                      // …but a leg failed
        Assert.Equal(1, m.RecommendedExitCode());
    }

    [Fact]
    public void Fully_dispositioned_all_passed_is_ship_ready()
    {
        var m = DispositionAllSkips(AllExecutedPassed());

        Assert.False(m.AnyFailed);
        Assert.Empty(m.UndispositionedSkipIds);
        Assert.Equal(ReleaseGatesManifest.CompletenessComplete, m.CatalogCompleteness);
        Assert.True(m.ShipReady);
        Assert.Equal(0, m.RecommendedExitCode(requireComplete: true));
    }

    [Fact]
    public void Empty_manifest_is_not_ship_ready_and_fails_integrity()
    {
        var m = new ReleaseGatesManifest();

        Assert.False(m.ShipReady);                 // no legs ⇒ nothing proven
        Assert.NotEmpty(m.Validate());             // …and the missing catalog rows are integrity errors
    }

    // ---- serialization ----

    [Fact]
    public void Round_trip_preserves_legs_reasons_and_versions()
    {
        var original = AllExecutedPassed()
            .WithLeg(GateLeg.Pass(ReleaseGatesManifest.LegIds.UnitTests, 4242, "artifacts/release-gates/unit-tests.log"));

        var round = ReleaseGatesManifest.Parse(original.ToJson());

        Assert.Equal(ReleaseGatesManifest.CurrentSchemaVersion, round.SchemaVersion);
        Assert.Equal(ReleaseGatesManifest.CurrentCatalogVersion, round.CatalogVersion);
        Assert.Equal(original.Legs.Count, round.Legs.Count);

        foreach (var before in original.Legs)
        {
            var after = round.Legs.Single(l => l.Id == before.Id);
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.Reason, after.Reason);
            Assert.Equal(before.ReasonCode, after.ReasonCode);
            Assert.Equal(before.RequiredForShip, after.RequiredForShip);
            Assert.Equal(before.Log, after.Log);
            Assert.Equal(before.DurationMs, after.DurationMs);
        }
        // A serialized empty catalog would be caught; the round-tripped real one stays sound.
        Assert.Empty(round.Validate());
    }

    [Fact]
    public void Disposition_socket_round_trips()
    {
        var m = DispositionAllSkips(AllExecutedPassed());

        var round = ReleaseGatesManifest.Parse(m.ToJson());
        var device = round.Legs.Single(l => l.Id == ReleaseGatesManifest.LegIds.BindingTestsDevice);

        Assert.NotNull(device.Disposition);
        Assert.Equal("waived", device.Disposition.Decision);
        Assert.True(round.ShipReady);
    }

    [Fact]
    public void Derived_outcome_is_recomputed_not_trusted_from_disk()
    {
        // A crash-seeded (failing) manifest, then a hand-tampered JSON that injects a green outcome.
        var failing = ReleaseGatesManifest.Seed();
        Assert.Equal(ReleaseGatesManifest.OutcomeFailed, failing.ExecutionOutcome);

        var json = failing.ToJson();
        var tampered = json.Insert(
            json.IndexOf('{') + 1,
            "\"execution_outcome\":\"passed\",\"ship_ready\":true,\"catalog_completeness\":\"complete\",");

        var parsed = ReleaseGatesManifest.Parse(tampered);

        // The injected keys are ignored; the verdict is recomputed from the (still-failing) legs.
        Assert.True(parsed.AnyFailed);
        Assert.Equal(ReleaseGatesManifest.OutcomeFailed, parsed.ExecutionOutcome);
        Assert.False(parsed.ShipReady);
    }

    // ---- fail-closed catalog integrity ----

    [Fact]
    public void Validate_catches_a_missing_leg()
    {
        var dropped = ReleaseGatesManifest.Seed().Legs
            .Where(l => l.Id != ReleaseGatesManifest.LegIds.PackGate).ToList();
        var m = new ReleaseGatesManifest { Legs = dropped };

        Assert.Contains(m.Validate(), e => e.Contains("missing") && e.Contains(ReleaseGatesManifest.LegIds.PackGate));
    }

    [Fact]
    public void Validate_catches_a_duplicate_leg()
    {
        var legs = ReleaseGatesManifest.Seed().Legs.ToList();
        legs.Add(GateLeg.Pass(ReleaseGatesManifest.LegIds.UnitTests));
        var m = new ReleaseGatesManifest { Legs = legs };

        Assert.Contains(m.Validate(), e => e.Contains("duplicate") && e.Contains(ReleaseGatesManifest.LegIds.UnitTests));
    }

    [Fact]
    public void Validate_catches_an_unknown_leg_id()
    {
        var legs = ReleaseGatesManifest.Seed().Legs.ToList();
        legs.Add(GateLeg.Pass("not-a-real-leg"));
        var m = new ReleaseGatesManifest { Legs = legs };

        Assert.Contains(m.Validate(), e => e.Contains("unknown leg id") && e.Contains("not-a-real-leg"));
    }

    [Fact]
    public void Validate_catches_an_unknown_status()
    {
        var m = ReleaseGatesManifest.Seed()
            .WithLeg(new GateLeg { Id = ReleaseGatesManifest.LegIds.UnitTests, Status = "weird", Reason = "x" });

        Assert.Contains(m.Validate(), e => e.Contains("unknown status") && e.Contains("weird"));
    }

    [Fact]
    public void Validate_catches_a_skip_without_a_reason()
    {
        var m = ReleaseGatesManifest.Seed()
            .WithLeg(new GateLeg { Id = ReleaseGatesManifest.LegIds.MixedPack, Status = GateLegStatus.Skipped, Reason = "" });

        Assert.Contains(m.Validate(), e => e.Contains(ReleaseGatesManifest.LegIds.MixedPack) && e.Contains("without a reason"));
    }

    [Fact]
    public void Validate_catches_a_schema_version_mismatch()
    {
        var m = ReleaseGatesManifest.Seed() with { SchemaVersion = ReleaseGatesManifest.CurrentSchemaVersion + 1 };

        Assert.Contains(m.Validate(), e => e.Contains("schema_version"));
    }

    [Fact]
    public void WithLeg_replaces_in_place_and_keeps_the_catalog_sound()
    {
        var m = ReleaseGatesManifest.Seed()
            .WithLeg(GateLeg.Pass(ReleaseGatesManifest.LegIds.UnitTests, 10));

        Assert.Equal(ReleaseGatesManifest.CanonicalCatalog.Count, m.Legs.Count);
        Assert.Equal(GateLegStatus.Pass, m.Legs.Single(l => l.Id == ReleaseGatesManifest.LegIds.UnitTests).Status);
        Assert.Empty(m.Validate());
    }

    // ---- integrity is a precondition of a green verdict, not a separate check ----

    [Fact]
    public void A_malformed_catalog_never_reads_ship_ready_or_zero_exit()
    {
        // Start from a fully green, fully-dispositioned catalog, then drop one required row. Nothing
        // that ran failed and every remaining skip is dispositioned — the ONLY thing wrong is the
        // missing leg. Ship-readiness and the exit code must reflect that integrity break rather
        // than the (now vacuously green) execution / completeness axes, so a consumer that trusts
        // ShipReady without separately calling Validate() cannot be misled.
        var full = DispositionAllSkips(AllExecutedPassed());
        var m = full with { Legs = full.Legs.Where(l => l.Id != ReleaseGatesManifest.LegIds.PackGate).ToList() };

        Assert.False(m.AnyFailed);                                                        // nothing that ran failed
        Assert.Equal(ReleaseGatesManifest.CompletenessComplete, m.CatalogCompleteness);   // no undispositioned skip
        Assert.False(m.IsCatalogSound);                                                   // …but a required row is gone
        Assert.False(m.ShipReady);                                                        // integrity break can't read green
        Assert.Equal(1, m.RecommendedExitCode(requireComplete: false));
        Assert.Equal(1, m.RecommendedExitCode(requireComplete: true));
    }

    // ---- fail-closed disposition socket: only a real accept/waive resolves a skip ----

    [Fact]
    public void An_empty_disposition_does_not_resolve_a_skip()
    {
        // A bare {} disposition carries no decision or owner — it must NOT clear the skip, and it is
        // itself an integrity error (unknown decision + missing accountability).
        var m = DisposeAllSkipsWith(AllExecutedPassed(), new LegDisposition());

        Assert.NotEmpty(m.UndispositionedSkipIds);
        Assert.False(m.ShipReady);
        Assert.Contains(m.Validate(), e => e.Contains("disposition") && e.Contains("decision"));
        Assert.Contains(m.Validate(), e => e.Contains("disposition") && e.Contains("by"));
    }

    [Fact]
    public void A_pending_run_before_ship_disposition_does_not_resolve_a_skip()
    {
        // "run-before-ship" is a known, valid acknowledgment (Validate stays clean) but is explicitly
        // non-resolving — the leg still has to run, so the skip stays undispositioned.
        var m = DisposeAllSkipsWith(AllExecutedPassed(),
            new LegDisposition { Decision = DispositionDecision.RunBeforeShip, By = "owner" });

        Assert.DoesNotContain(m.Validate(), e => e.Contains("disposition"));   // a known decision + owner is valid
        Assert.NotEmpty(m.UndispositionedSkipIds);                            // …but does not resolve the skip
        Assert.False(m.ShipReady);
    }

    [Fact]
    public void A_resolving_disposition_without_an_owner_fails_integrity_and_does_not_resolve()
    {
        // Even an accept/waive needs accountability; no 'by' ⇒ integrity error + still unresolved.
        var m = DisposeAllSkipsWith(AllExecutedPassed(),
            new LegDisposition { Decision = DispositionDecision.Accepted, By = "" });

        Assert.NotEmpty(m.UndispositionedSkipIds);
        Assert.False(m.ShipReady);
        Assert.Contains(m.Validate(), e => e.Contains("disposition") && e.Contains("by"));
    }

    // ---- attest / disposition write path (release-gates-attest) ----

    private const string FixedAt = "2026-07-10T12:00:00Z";

    private static readonly string[] AttendedLegIds =
        ReleaseGatesManifest.CanonicalCatalog.Except(ReleaseGatesManifest.ExecutedLegIds).ToArray();

    [Fact]
    public void AttestPass_flips_exactly_one_leg_to_pass_and_preserves_the_rest()
    {
        var before = AllExecutedPassed();
        var m = before.AttestPass(ReleaseGatesManifest.LegIds.BindingTestsDevice, "justin",
            "artifacts/release-gates/binding-tests-device.log", FixedAt);

        var device = m.Legs.Single(l => l.Id == ReleaseGatesManifest.LegIds.BindingTestsDevice);
        Assert.Equal(GateLegStatus.Pass, device.Status);
        Assert.NotNull(device.Attestation);
        Assert.Equal("justin", device.Attestation!.By);
        Assert.Equal(FixedAt, device.Attestation.At);
        Assert.Equal("artifacts/release-gates/binding-tests-device.log", device.Log);   // evidence lives in Log
        Assert.Null(device.Disposition);                                                // a pass is not a disposition
        Assert.Empty(m.Validate());

        // Every OTHER leg is byte-for-byte the row it was before the attest.
        foreach (var b in before.Legs.Where(l => l.Id != ReleaseGatesManifest.LegIds.BindingTestsDevice))
            Assert.Equal(b, m.Legs.Single(l => l.Id == b.Id));
    }

    [Fact]
    public void AttestPass_on_every_attended_leg_reaches_ship_ready_and_is_no_longer_a_skip()
    {
        // Verdict integration: an attested pass counts as executed-green with no special-casing —
        // the leg drops out of the skip set, and once all four attended legs are attested the catalog
        // is complete and ship-ready, and --require-complete exits zero.
        var m = AllExecutedPassed();
        foreach (var id in AttendedLegIds)
            m = m.AttestPass(id, "justin", $"artifacts/release-gates/{id}.log", FixedAt);

        Assert.Empty(m.Validate());
        Assert.False(m.AnyFailed);
        Assert.Empty(m.UndispositionedSkipIds);
        Assert.Equal(ReleaseGatesManifest.CompletenessComplete, m.CatalogCompleteness);
        Assert.True(m.ShipReady);
        Assert.Equal(0, m.RecommendedExitCode(requireComplete: true));
        foreach (var id in AttendedLegIds)
        {
            var leg = m.Legs.Single(l => l.Id == id);
            Assert.Equal(GateLegStatus.Pass, leg.Status);
            Assert.NotNull(leg.Attestation);
        }
    }

    [Fact]
    public void Attested_passes_and_resolving_dispositions_together_are_ship_ready()
    {
        // Two attended legs actually ran green (attested pass); two are owner-waived/accepted. Mixed
        // resolution still reaches ship-ready — and the disposed legs are NOT passes.
        var m = AllExecutedPassed()
            .AttestPass(ReleaseGatesManifest.LegIds.BindingTestsDevice, "justin",
                "artifacts/release-gates/binding-tests-device.log", FixedAt)
            .AttestPass(ReleaseGatesManifest.LegIds.MixedPack, "justin",
                "artifacts/release-gates/mixed-pack.log", FixedAt)
            .DispositionSkip(ReleaseGatesManifest.LegIds.MixedDirect, DispositionDecision.Waived, "justin", FixedAt)
            .DispositionSkip(ReleaseGatesManifest.LegIds.AppStoreHygieneSignedIpa, DispositionDecision.Accepted, "justin", FixedAt);

        Assert.Empty(m.Validate());
        Assert.True(m.ShipReady);
        Assert.Equal(0, m.RecommendedExitCode(requireComplete: true));

        var waived = m.Legs.Single(l => l.Id == ReleaseGatesManifest.LegIds.MixedDirect);
        Assert.Equal(GateLegStatus.Skipped, waived.Status);   // disposition != attested pass
        Assert.Null(waived.Attestation);
        Assert.True(waived.Disposition!.IsResolving);
    }

    [Fact]
    public void A_bare_attended_pass_without_attestation_fails_integrity_and_is_not_ship_ready()
    {
        // The silent-ship-ready hole: a plain Pass on an attended leg drops out of the skip set
        // (UndispositionedSkipIds is skip-only). It MUST fail catalog integrity for lacking
        // accountability, so it can never read ship-ready — the fail-closed twin of "a waive without
        // a 'by' does not resolve".
        var m = AllExecutedPassed()
            .WithLeg(GateLeg.Pass(ReleaseGatesManifest.LegIds.BindingTestsDevice));   // no attestation, no by, no log
        foreach (var id in new[]
                 {
                     ReleaseGatesManifest.LegIds.MixedPack,
                     ReleaseGatesManifest.LegIds.MixedDirect,
                     ReleaseGatesManifest.LegIds.AppStoreHygieneSignedIpa,
                 })
            m = m.DispositionSkip(id, DispositionDecision.Waived, "justin", FixedAt);

        // Completeness is otherwise satisfied (device no longer a skip, others waived)…
        Assert.Empty(m.UndispositionedSkipIds);
        Assert.Equal(ReleaseGatesManifest.CompletenessComplete, m.CatalogCompleteness);
        // …but the accountability-less attended pass breaks integrity, which vetoes ship-readiness.
        Assert.Contains(m.Validate(), e =>
            e.Contains(ReleaseGatesManifest.LegIds.BindingTestsDevice) && e.Contains("attended"));
        Assert.False(m.IsCatalogSound);
        Assert.False(m.ShipReady);
        Assert.Equal(1, m.RecommendedExitCode(requireComplete: false));
    }

    [Fact]
    public void AttestPass_never_overwrites_an_orchestrator_pass_or_a_fail()
    {
        var passed = AllExecutedPassed();   // unit-tests is an orchestrator pass, no attestation
        Assert.Throws<InvalidOperationException>(() =>
            passed.AttestPass(ReleaseGatesManifest.LegIds.UnitTests, "justin", "x.log", FixedAt));

        var failed = passed.WithLeg(GateLeg.Fail(ReleaseGatesManifest.LegIds.PackGate, "pack-gate exited 1"));
        Assert.Throws<InvalidOperationException>(() =>
            failed.AttestPass(ReleaseGatesManifest.LegIds.PackGate, "justin", "x.log", FixedAt));
    }

    [Fact]
    public void AttestPass_allows_correcting_an_already_attested_pass()
    {
        var typo = AllExecutedPassed()
            .AttestPass(ReleaseGatesManifest.LegIds.BindingTestsDevice, "jstin", "device.log", FixedAt);
        var corrected = typo.AttestPass(ReleaseGatesManifest.LegIds.BindingTestsDevice, "justin", "device.log", FixedAt);

        var leg = corrected.Legs.Single(l => l.Id == ReleaseGatesManifest.LegIds.BindingTestsDevice);
        Assert.Equal("justin", leg.Attestation!.By);
        Assert.Empty(corrected.Validate());
    }

    [Fact]
    public void AttestPass_rejects_unknown_leg_blank_evidence_blank_by_and_blank_timestamp()
    {
        var m = AllExecutedPassed();
        Assert.Throws<ArgumentException>(() => m.AttestPass("not-a-leg", "justin", "e.log", FixedAt));
        Assert.Throws<ArgumentException>(() =>
            m.AttestPass(ReleaseGatesManifest.LegIds.BindingTestsDevice, "justin", "   ", FixedAt));
        Assert.Throws<ArgumentException>(() =>
            m.AttestPass(ReleaseGatesManifest.LegIds.BindingTestsDevice, "", "e.log", FixedAt));
        // The write API fail-closes a blank timestamp symmetrically with 'by'/evidence — an
        // attested pass stamps who/when, so it never reaches the manifest missing its 'at'.
        Assert.Throws<ArgumentException>(() =>
            m.AttestPass(ReleaseGatesManifest.LegIds.BindingTestsDevice, "justin", "e.log", "   "));
    }

    [Fact]
    public void DispositionSkip_waive_resolves_but_stays_skipped()
    {
        var m = AllExecutedPassed()
            .DispositionSkip(ReleaseGatesManifest.LegIds.BindingTestsDevice, DispositionDecision.Waived, "justin", FixedAt);
        var leg = m.Legs.Single(l => l.Id == ReleaseGatesManifest.LegIds.BindingTestsDevice);

        Assert.Equal(GateLegStatus.Skipped, leg.Status);   // NOT flipped to pass
        Assert.Null(leg.Attestation);
        Assert.True(leg.Disposition!.IsResolving);
        Assert.DoesNotContain(ReleaseGatesManifest.LegIds.BindingTestsDevice, m.UndispositionedSkipIds);
        Assert.Empty(m.Validate());
    }

    [Fact]
    public void DispositionSkip_rejects_non_resolving_unknown_and_non_skip_targets()
    {
        var m = AllExecutedPassed();
        // run-before-ship is a known but non-resolving decision — the attest write path only records
        // resolving accept/waive, never a still-pending acknowledgment.
        Assert.Throws<ArgumentException>(() =>
            m.DispositionSkip(ReleaseGatesManifest.LegIds.MixedPack, DispositionDecision.RunBeforeShip, "justin", FixedAt));
        Assert.Throws<ArgumentException>(() =>
            m.DispositionSkip(ReleaseGatesManifest.LegIds.MixedPack, "bogus", "justin", FixedAt));
        Assert.Throws<ArgumentException>(() =>
            m.DispositionSkip("not-a-leg", DispositionDecision.Waived, "justin", FixedAt));
        Assert.Throws<ArgumentException>(() =>
            m.DispositionSkip(ReleaseGatesManifest.LegIds.MixedPack, DispositionDecision.Waived, " ", FixedAt));
        // An executed (non-skip) leg cannot be dispositioned.
        Assert.Throws<InvalidOperationException>(() =>
            m.DispositionSkip(ReleaseGatesManifest.LegIds.UnitTests, DispositionDecision.Waived, "justin", FixedAt));
    }

    [Fact]
    public void Validate_rejects_an_attestation_on_a_non_pass_status()
    {
        var m = ReleaseGatesManifest.Seed()
            .WithLeg(new GateLeg
            {
                Id = ReleaseGatesManifest.LegIds.MixedPack,
                Status = GateLegStatus.Skipped,
                Reason = "x",
                Attestation = new LegAttestation { By = "justin", At = FixedAt },
            });

        Assert.Contains(m.Validate(), e =>
            e.Contains(ReleaseGatesManifest.LegIds.MixedPack) && e.Contains("attestation") && e.Contains("not 'pass'"));
    }

    [Fact]
    public void Validate_rejects_a_disposition_on_a_non_skip_status()
    {
        var m = AllExecutedPassed()
            .WithLeg(GateLeg.Pass(ReleaseGatesManifest.LegIds.UnitTests, 10) with
            {
                Disposition = new LegDisposition { Decision = DispositionDecision.Waived, By = "justin", At = FixedAt },
            });

        Assert.Contains(m.Validate(), e =>
            e.Contains(ReleaseGatesManifest.LegIds.UnitTests) && e.Contains("disposition") && e.Contains("not 'skipped'"));
    }

    [Fact]
    public void Validate_rejects_an_attested_pass_with_blank_evidence_or_timestamp()
    {
        // Twin of the bare-attended-pass case: an attestation is present with a valid 'by', but the
        // evidence Log is whitespace (H1 rule) or the 'at' timestamp is blank — both break integrity,
        // so an attended pass cannot read accountable on a partial/hand-edited row.
        var blankLog = AllExecutedPassed()
            .WithLeg(new GateLeg
            {
                Id = ReleaseGatesManifest.LegIds.BindingTestsDevice,
                Status = GateLegStatus.Pass,
                Log = "   ",
                Attestation = new LegAttestation { By = "justin", At = FixedAt },
            });
        Assert.Contains(blankLog.Validate(), e =>
            e.Contains(ReleaseGatesManifest.LegIds.BindingTestsDevice) && e.Contains("attended"));
        Assert.False(blankLog.ShipReady);

        var blankAt = AllExecutedPassed()
            .WithLeg(new GateLeg
            {
                Id = ReleaseGatesManifest.LegIds.MixedPack,
                Status = GateLegStatus.Pass,
                Log = "artifacts/release-gates/mixed-pack.log",
                Attestation = new LegAttestation { By = "justin", At = "" },
            });
        Assert.Contains(blankAt.Validate(), e =>
            e.Contains(ReleaseGatesManifest.LegIds.MixedPack) && e.Contains("'at'"));
        Assert.False(blankAt.ShipReady);
    }

    [Fact]
    public void Attestation_round_trips_and_preserves_ship_ready()
    {
        var m = AllExecutedPassed();
        foreach (var id in AttendedLegIds)
            m = m.AttestPass(id, "justin", $"artifacts/release-gates/{id}.log", FixedAt);
        Assert.True(m.ShipReady);

        var round = ReleaseGatesManifest.Parse(m.ToJson());
        var device = round.Legs.Single(l => l.Id == ReleaseGatesManifest.LegIds.BindingTestsDevice);
        Assert.Equal(GateLegStatus.Pass, device.Status);
        Assert.NotNull(device.Attestation);
        Assert.Equal("justin", device.Attestation!.By);
        Assert.Equal(FixedAt, device.Attestation.At);
        Assert.Equal("artifacts/release-gates/binding-tests-device.log", device.Log);
        Assert.True(round.ShipReady);
        Assert.Empty(round.Validate());
    }

    // Attach the same disposition to every recorded skip.
    private static ReleaseGatesManifest DisposeAllSkipsWith(ReleaseGatesManifest m, LegDisposition disposition)
    {
        foreach (var leg in m.Legs.Where(l => l.Status == GateLegStatus.Skipped).ToList())
            m = m.WithLeg(leg with { Disposition = disposition });
        return m;
    }

    // Attach an accept/waive disposition to every recorded skip.
    private static ReleaseGatesManifest DispositionAllSkips(ReleaseGatesManifest m)
    {
        foreach (var leg in m.Legs.Where(l => l.Status == GateLegStatus.Skipped).ToList())
            m = m.WithLeg(leg with
            {
                Disposition = new LegDisposition
                {
                    Decision = "waived",
                    By = "test",
                    At = "2026-07-10T00:00:00Z",
                    Note = "not applicable to this invocation",
                },
            });
        return m;
    }
}
