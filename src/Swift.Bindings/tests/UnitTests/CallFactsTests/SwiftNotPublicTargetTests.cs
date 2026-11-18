// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using SwiftBindings.TransitionGate;
using Xunit;
using static BindingsGeneration.Tests.SwiftCallPlanTests;

namespace BindingsGeneration.Tests;

/// <summary>
/// A forward plan whose target no client of the module's public interface can name (it, or a type it
/// is declared in, is internal, <c>@usableFromInline internal</c> or SPI): a consumer's own receipt,
/// read from the wrapper and the probe, can never show it. Judged under that receipt, it is refused
/// with the reason and keeps its guarded direct import, so the packaged transition checks see a
/// refusal rather than an evidence gap. A receipt that contradicts the plan, and the qualification
/// record (which reads definitions and must show every target), still leave a fatal gap.
/// </summary>
public class SwiftNotPublicTargetTests
{
    private const string Target = "$s1M6RecordC8describeyS2iF";

    private const string InternalReason = SwiftLoweringProbe.InternalReason;

    private static SwiftArtifactScope Scope => SwiftArtifactScope.ForPlatform("M", "ios");

    private static string Compiler => SwiftLoweringEvidenceTests.QualifiedCompilers[0];

    private static SwiftCallPlanSet Set(Func<SwiftPlanCoverageEntry, SwiftPlanCoverageEntry>? shape = null)
    {
        var outcome = Planner().PlanForward(Free("Swift.Int", "Swift.Int"), Signature("long", "long"), Target);
        var plan = Planned(outcome);
        var entry = new SwiftPlanCoverageEntry(SwiftTransitionKind.ForwardPInvoke, "M.Record::PInvoke_describe", Target, SwiftPlanStatus.Planned)
        {
            Symbol = plan.Symbol,
            Hash = plan.Hash,
            Callee = outcome.Callee,
            Evidence = SwiftCallPlanSet.NoEvidenceReason,
        };
        return new SwiftCallPlanSet { Module = "M", Coverage = [shape is null ? entry : shape(entry)], Plans = [plan] };
    }

    /// <summary>A receipt of M under the build's compiler, on every iOS slice, that shows <paramref name="shows"/> and leaves the target unobserved for <paramref name="unobserved"/> (by slice).</summary>
    private static SwiftLoweringEvidence Receipt(Func<string, string?> unobserved, IReadOnlyDictionary<string, string>? shows = null, string? compiler = null)
    {
        var evidence = SwiftLoweringEvidence.Empty;
        foreach (var triple in Scope.Slices)
        {
            var slice = SwiftLoweringEvidenceTests.Slice(triple, shows ?? new Dictionary<string, string>());
            if (unobserved(triple) is { } why)
                slice = slice with { Unobserved = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create(Target, why)]) };
            evidence = evidence.WithSlice("M", compiler ?? Compiler, SwiftLoweringContract.Current, "test", triple, slice);
        }
        return evidence;
    }

    private static SwiftLoweringJudgement Authorize(SwiftLoweringEvidence receipt) =>
        SwiftLoweringAuthorization.Authorize(
            new SwiftEvidenceExpectation(Scope, Compiler, SwiftLoweringContract.Current,
                Scope.Slices.ToImmutableSortedDictionary(t => t, SwiftLoweringEvidenceTests.Sdk, StringComparer.Ordinal)),
            receipt);

    /// <summary>What the engine (and SWIFTBIND133/132, which run it on the packed sidecar) reports for the sidecar.</summary>
    private static ImmutableArray<string> EvidenceGaps(SwiftCallPlanSet set) =>
        BridgeTwinPlans.FromSidecars([("M" + BridgeTwinPlans.SidecarSuffix, set.ToJson())]).EvidenceGaps;

    private static void AssertRefusedNotPublic(SwiftPlanCoverageEntry entry, string because)
    {
        Assert.Equal(SwiftPlanStatus.Refused, entry.Status);
        Assert.False(entry.Executable);
        var refusal = Assert.Single(entry.Refusals);
        Assert.Equal(SwiftPlanRefusalKind.NotPublicFromInterface, refusal.Kind);
        Assert.Contains(because, refusal.Reason);
    }

    // ---- the probe's reason ----

    [Theory]
    [InlineData(InternalReason)]
    [InlineData(SwiftLoweringProbe.UsableFromInlineReason)]
    [InlineData(SwiftLoweringProbe.SpiReason)]
    public void AConsumersReceipt_ThatCannotNameTheTarget_RefusesItsPlan_AndLeavesNoEvidenceGap(string why)
    {
        var judged = Set().WithEvidence(Authorize(Receipt(_ => why)));

        var entry = Assert.Single(judged.Coverage);
        AssertRefusedNotPublic(entry, why);
        // The plan stays recorded for the entry, and the guard explains the direct import.
        Assert.Equal(judged.Plans[0].Symbol, entry.Symbol);
        Assert.Empty(EvidenceGaps(judged));
        Assert.Contains("refused", SwiftTransitionGuards.ResidualReason(
            nameof(SwiftTransitionKind.ForwardPInvoke), entry.Member, entry.EntryPoint, SwiftTransitionGuards.CoverageOf(judged)));
    }

    /// <summary>The probe could not write or compile a call for another reason: nothing says no client can name it, so it stays a gap.</summary>
    [Theory]
    [InlineData("unsupported in the probe: its call did not compile: no type named 'Record' in module 'M'")]
    [InlineData("other: the probe did not compile: it timed out")]
    [InlineData(null)]
    public void AReceiptThatOmitsTheTargetForAnyOtherReason_StaysAnEvidenceGap(string? why)
    {
        var judged = Set().WithEvidence(Authorize(Receipt(_ => why)));

        var entry = Assert.Single(judged.Coverage);
        Assert.Equal(SwiftPlanStatus.Planned, entry.Status);
        Assert.Single(EvidenceGaps(judged));
    }

    [Fact]
    public void ATargetNotPublicOnOneSlice_AndUnobservedForAnotherReasonOnAnother_StaysAnEvidenceGap()
    {
        var judged = Set().WithEvidence(Authorize(Receipt(t => t == Scope.Slices[0] ? "inlined: the probe's IR defines its own copy instead of calling the library's" : InternalReason)));

        Assert.Equal(SwiftPlanStatus.Planned, Assert.Single(judged.Coverage).Status);
        Assert.Single(EvidenceGaps(judged));
    }

    [Fact]
    public void AReceiptThatContradictsThePlan_StaysFatal_WhateverElseItSays()
    {
        var receipt = Receipt(t => t == Scope.Slices[0] ? null : InternalReason);
        receipt = receipt.WithSlice("M", Compiler, SwiftLoweringContract.Current, "test", Scope.Slices[0],
            SwiftLoweringEvidenceTests.Slice(Scope.Slices[0], new Dictionary<string, string> { [Target] = "swiftcc i64 (i64, i64)" }));

        var judged = Set().WithEvidence(Authorize(receipt));

        var entry = Assert.Single(judged.Coverage);
        Assert.Equal(SwiftPlanStatus.Planned, entry.Status);
        Assert.Contains("the plan predicts", Assert.Single(EvidenceGaps(judged)));
    }

    /// <summary>
    /// The SDK's qualification record is read from definitions, which show internal and
    /// <c>@usableFromInline</c> targets too: a record that does not show one is stale, and stays a gap.
    /// </summary>
    [Fact]
    public void UnderQualification_ATargetTheRecordDoesNotShow_StaysAnEvidenceGap()
    {
        var record = SwiftLoweringEvidence.Empty;
        foreach (var compiler in SwiftLoweringEvidenceTests.QualifiedCompilers)
            record = Receipt(_ => InternalReason, compiler: compiler) is var r ? Merge(record, r) : record;

        var judged = Set().WithEvidence(SwiftLoweringQualification.Qualify(record, Scope));

        Assert.Equal(SwiftPlanStatus.Planned, Assert.Single(judged.Coverage).Status);
        Assert.Single(EvidenceGaps(judged));
    }

    private static SwiftLoweringEvidence Merge(SwiftLoweringEvidence into, SwiftLoweringEvidence from)
    {
        foreach (var receipt in from.Receipts)
            foreach (var (triple, slice) in receipt.Slices)
                into = into.WithSlice(receipt.Artifact, receipt.Compiler, receipt.Contract, "test", triple, slice);
        return into;
    }

    /// <summary>A refusal made by a judgement is the judgement's: without evidence the plan is planned again, and judging it again gives the same verdict.</summary>
    [Fact]
    public void WithoutEvidence_PlansARefusedTargetAgain_AndRejudgingIsStable()
    {
        var judged = Set().WithEvidence(Authorize(Receipt(_ => InternalReason)));

        var unjudged = judged.WithoutEvidence();

        var entry = Assert.Single(unjudged.Coverage);
        Assert.Equal(SwiftPlanStatus.Planned, entry.Status);
        Assert.Empty(entry.Refusals);
        Assert.Equal(SwiftCallPlanSet.NoEvidenceReason, entry.Evidence);
        Assert.Equal(Set().ToJson(), unjudged.ToJson());
        Assert.Equal(judged.ToJson(), unjudged.WithEvidence(Authorize(Receipt(_ => InternalReason))).ToJson());
        Assert.Equal(judged.ToJson(), judged.WithEvidence(Authorize(Receipt(_ => InternalReason))).ToJson());
    }

    // ---- the facts' reason ----

    private const string NotPublic = "M.Record is internal, @usableFromInline internal or unavailable";

    private static SwiftCallPlanSet NotPublicSet() => Set(e => e with { NotPublic = NotPublic });

    /// <summary>
    /// The generator knows from the interface that no client can name the target, so whatever the
    /// probe made of it (no call, a call that did not compile, a slice it never reached), the receipt
    /// of the build cannot show it.
    /// </summary>
    [Theory]
    [InlineData("unsupported in the probe: its call did not compile: no type named 'Record' in module 'M'")]
    [InlineData("other: the probe did not compile: it timed out")]
    [InlineData(null)]
    public void ATargetTheFactsSayIsNotPublic_IsRefused_WhateverTheProbeSaid(string? why)
    {
        var judged = NotPublicSet().WithEvidence(Authorize(Receipt(_ => why)));

        AssertRefusedNotPublic(Assert.Single(judged.Coverage), NotPublic);
        Assert.Empty(EvidenceGaps(judged));
    }

    /// <summary>A receipt that does show the target, as an inlined client call can, makes it executable: the refusal never takes a proven plan away.</summary>
    [Fact]
    public void ATargetTheFactsSayIsNotPublic_ThatTheReceiptShows_StaysExecutable()
    {
        var set = NotPublicSet();
        var judged = set.WithEvidence(Authorize(Receipt(_ => null, new Dictionary<string, string> { [Target] = "swiftcc " + set.Coverage[0].Callee })));

        var entry = Assert.Single(judged.Coverage);
        Assert.Equal(SwiftPlanStatus.Planned, entry.Status);
        Assert.True(entry.Executable, entry.Evidence);
    }

    [Fact]
    public void ATargetTheFactsSayIsNotPublic_ThatTheReceiptContradicts_StaysFatal()
    {
        var judged = NotPublicSet().WithEvidence(Authorize(Receipt(_ => null, new Dictionary<string, string> { [Target] = "swiftcc i64 (i64, i64)" })));

        Assert.Equal(SwiftPlanStatus.Planned, Assert.Single(judged.Coverage).Status);
        Assert.Contains("the plan predicts", Assert.Single(EvidenceGaps(judged)));
    }

    [Fact]
    public void ATargetTheFactsSayIsNotPublic_UnderQualification_StaysAnEvidenceGap()
    {
        var record = SwiftLoweringEvidence.Empty;
        foreach (var compiler in SwiftLoweringEvidenceTests.QualifiedCompilers)
            record = Merge(record, Receipt(_ => null, compiler: compiler));

        var judged = NotPublicSet().WithEvidence(SwiftLoweringQualification.Qualify(record, Scope));

        Assert.Equal(SwiftPlanStatus.Planned, Assert.Single(judged.Coverage).Status);
        Assert.Single(EvidenceGaps(judged));
    }

    /// <summary>A receipt that authorizes nothing (another compiler's) leaves every plan unproven; one no client can call is still refused for that.</summary>
    [Fact]
    public void ATargetTheFactsSayIsNotPublic_IsRefused_EvenWhenTheReceiptAuthorizesNothing()
    {
        var judged = NotPublicSet().WithEvidence(Authorize(Receipt(_ => null, compiler: SwiftLoweringEvidenceTests.QualifiedCompilers[1])));

        AssertRefusedNotPublic(Assert.Single(judged.Coverage), NotPublic);
        Assert.Contains("this build compiles with", judged.Coverage[0].Evidence);
    }

    // ---- from the facts to the coverage ----

    private const string Source = """
        using System.Runtime.InteropServices;
        using Swift;
        namespace Acme
        {
            public static unsafe partial class Calls
            {
                [LibraryImport("MSwiftBindings", EntryPoint = "$s1M6RecordC8describeyySSF")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                internal static partial void Describe(SwiftString.Buffer s);

                [LibraryImport("MSwiftBindings", EntryPoint = "$s1M4HostC5shownyySSF")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                internal static partial void Shown(SwiftString.Buffer s);
            }
        }
        """;

    private static SwiftTransitionRecord Record(string method, string entryPoint, string? notPublic) => new()
    {
        MethodName = method,
        EntryPoint = entryPoint,
        Library = "MSwiftBindings",
        Facts = Free(null, "Swift.String") with { Target = Free(null, "Swift.String").Target with { Symbol = entryPoint, NotPublic = notPublic } },
        Source = SwiftCallFactsSource.Declared,
    };

    [Fact]
    public void APlannedForwardEntry_CarriesItsTargetsNotPublicReason_FromTheFacts()
    {
        var ledger = SwiftTransitionLedger.Build("M", Source,
        [
            Record("Describe", "$s1M6RecordC8describeyySSF", NotPublic),
            Record("Shown", "$s1M4HostC5shownyySSF", null),
        ]);

        var set = SwiftCallPlanSet.Build(ledger, new SwiftCallPlanner(ledger.Types, SwiftArchitectureModel.Default(Layout)));

        var describe = set.Coverage.Single(e => e.EntryPoint == "$s1M6RecordC8describeyySSF");
        Assert.Equal(SwiftPlanStatus.Planned, describe.Status);
        Assert.Equal(NotPublic, describe.NotPublic);
        Assert.Null(set.Coverage.Single(e => e.EntryPoint == "$s1M4HostC5shownyySSF").NotPublic);
        // The plans are the same whoever can name the target.
        var public_ = SwiftTransitionLedger.Build("M", Source,
        [
            Record("Describe", "$s1M6RecordC8describeyySSF", null),
            Record("Shown", "$s1M4HostC5shownyySSF", null),
        ]);
        Assert.Equal(
            SwiftCallPlanSet.Build(public_, new SwiftCallPlanner(public_.Types, SwiftArchitectureModel.Default(Layout))).Plans.Select(p => p.Hash),
            set.Plans.Select(p => p.Hash));
        Assert.Contains("\"notPublic\": \"" + NotPublic + "\"", set.ToJson());
    }
}
