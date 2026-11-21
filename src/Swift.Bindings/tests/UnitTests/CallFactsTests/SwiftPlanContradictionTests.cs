// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A call plan that contradicts the callee's lowering leaves no callable import: its member is
/// withdrawn, or the module fails. The whole-module withdrawal is pinned in
/// <see cref="EmitterFaultContainmentTests"/>; these pin which entries count and every fail-closed arm.
/// </summary>
public class SwiftPlanContradictionTests
{
    private const string EntryPoint = "$s1M3BoxC4make7messages5Error_pSSSg_tF_optbuf";
    private const string Member = "M.Box::PInvoke_make_0A1B2C3D";

    private static readonly DeclId Method = DeclId.Create("M", "Box", BindingItemKind.Method, "make", ["message"], ["Swift.Optional<Swift.String>"]);

    [Theory]
    [InlineData(SwiftPlanRefusalKind.ResultMismatch, true)]
    [InlineData(SwiftPlanRefusalKind.PrefixMismatch, true)]
    [InlineData(SwiftPlanRefusalKind.SpecialRegisterMismatch, true)]
    [InlineData(SwiftPlanRefusalKind.Lowering, false)]
    [InlineData(SwiftPlanRefusalKind.Facts, false)]
    [InlineData(SwiftPlanRefusalKind.ManagedViewDisagreement, false)]
    [InlineData(SwiftPlanRefusalKind.UnknownAggregate, false)]
    public void OnlyRefusalsThatProveTheImportWrong_AreContradictions(SwiftPlanRefusalKind kind, bool contradiction)
    {
        var set = Set(Refused(kind));

        Assert.Equal(contradiction, SwiftPlanContradictions.IsContradiction(kind));
        Assert.Equal(contradiction ? 1 : 0, SwiftPlanContradictions.Find(set).Length);
    }

    [Fact]
    public void ContradictionOnAPlannedOrUnbridgedEntry_IsNotFound()
    {
        // Status, not the refusal list, says what the plan set decided; only a refusal is withdrawn.
        var planned = Refused(SwiftPlanRefusalKind.ResultMismatch) with { Status = SwiftPlanStatus.Planned };
        var unbridged = Refused(SwiftPlanRefusalKind.ResultMismatch) with { Status = SwiftPlanStatus.NotBridged };

        Assert.Empty(SwiftPlanContradictions.Find(Set(planned, unbridged)));
    }

    [Fact]
    public void ContradictedImport_ResolvesToTheMemberItsCallPlanNames()
    {
        var (withdrawals, unresolved) = SwiftPlanContradictions.Resolve(
            [Refused(SwiftPlanRefusalKind.ResultMismatch)], [CallPlan(ArtifactId.Create(Method, ArtifactRole.PInvoke))]);

        Assert.Empty(unresolved);
        var withdrawal = Assert.Single(withdrawals);
        Assert.Equal(Method, withdrawal.Unit.Decl);
        Assert.Equal(RecoveryScope.LeafApi, withdrawal.Unit.Scope);
        // One line per contradiction, not one per architecture that found it.
        Assert.Contains("(ResultMismatch: the managed signature takes an indirect result", withdrawal.Reason);
        Assert.DoesNotContain("Arm64", withdrawal.Reason);
    }

    [Fact]
    public void ContradictedImportWithNoCallPlan_IsUnresolved()
    {
        var other = CallPlan(ArtifactId.Create(Method, ArtifactRole.PInvoke)) with { EntryPoint = "$s1M5otheryyF" };

        var (withdrawals, unresolved) = SwiftPlanContradictions.Resolve([Refused(SwiftPlanRefusalKind.ResultMismatch)], [other]);

        Assert.Empty(withdrawals);
        Assert.Contains("no ABI call plan", Assert.Single(unresolved).Why);
    }

    [Fact]
    public void ContradictedImportWhoseCallPlanHasNoOwner_IsUnresolved()
    {
        var (withdrawals, unresolved) = SwiftPlanContradictions.Resolve([Refused(SwiftPlanRefusalKind.ResultMismatch)], [CallPlan(owner: null)]);

        Assert.Empty(withdrawals);
        Assert.Contains("no owning member", Assert.Single(unresolved).Why);
    }

    [Fact]
    public void ContradictedImportOwnedByWhatCannotBeDroppedAlone_IsUnresolved()
    {
        // The module initializer and a type's metadata accessor are droppable units, but not members:
        // withdrawing one for a contradicted import would take the whole module or type with it.
        var initializer = ArtifactId.Create(DeclId.Create("M", "", BindingItemKind.Module, "M"), ArtifactRole.ModuleInitializer);
        var metadata = ArtifactId.Create(DeclId.Create("M", "", BindingItemKind.Type, "Box"), ArtifactRole.MetadataHelper);

        var (withdrawals, unresolved) = SwiftPlanContradictions.Resolve(
            [Refused(SwiftPlanRefusalKind.ResultMismatch)], [CallPlan(initializer), CallPlan(metadata)]);

        Assert.Empty(withdrawals);
        Assert.Equal(2, unresolved.Length);
        Assert.All(unresolved, u => Assert.Contains("is not a member that can be withdrawn alone", u.Why));
    }

    [Fact]
    public void ContradictedCallThroughAPointer_IsUnresolved()
    {
        var entry = Refused(SwiftPlanRefusalKind.PrefixMismatch) with { Transition = SwiftTransitionKind.ForwardCalli };

        var (withdrawals, unresolved) = SwiftPlanContradictions.Resolve([entry], [CallPlan(ArtifactId.Create(Method, ArtifactRole.PInvoke))]);

        Assert.Empty(withdrawals);
        Assert.Contains("is not an import", Assert.Single(unresolved).Why);
    }

    [Theory]
    [InlineData(SwiftTransitionKind.ReverseCallback)]
    [InlineData(SwiftTransitionKind.CallbackAddress)]
    public void ContradictedCallback_ResolvesToTheMemberWhoseImportPassesItsClosure(SwiftTransitionKind transition)
    {
        var (withdrawals, unresolved) = SwiftPlanContradictions.Resolve(
            [CallbackRefused(transition)], [CallPlan(ArtifactId.Create(Method, ArtifactRole.PInvoke))], NoOrphans,
            PassedBy(CallPlan(ArtifactId.Create(Method, ArtifactRole.PInvoke))));

        Assert.Empty(unresolved);
        var withdrawal = Assert.Single(withdrawals);
        Assert.Equal(Method, withdrawal.Unit.Decl);
        Assert.Contains("closure callback", withdrawal.Reason);
    }

    [Theory]
    [InlineData(SwiftTransitionKind.ReverseCallback)]
    [InlineData(SwiftTransitionKind.CallbackAddress)]
    public void ContradictedCallbackOnlyAnImportNothingCallsPasses_IsSpared(SwiftTransitionKind transition)
    {
        // The ABI floor replaced the member's body with a throw: the callback is still declared, but
        // nothing hands it to Swift any more.
        var tombstone = ArtifactId.Create(Method, ArtifactRole.PInvoke);

        var (withdrawals, unresolved) = SwiftPlanContradictions.Resolve(
            [CallbackRefused(transition)], [CallPlan(tombstone)], Orphaned(tombstone), PassedBy(CallPlan(tombstone)));

        Assert.Empty(withdrawals);
        Assert.Empty(unresolved);
    }

    [Theory]
    [InlineData(SwiftTransitionKind.ReverseCallback, false)]
    [InlineData(SwiftTransitionKind.CallbackAddress, false)]
    [InlineData(SwiftTransitionKind.ReverseCallback, true)]
    public void ContradictedCallbackWithNoImportPassingItsClosure_IsUnresolved(SwiftTransitionKind transition, bool importRecorded)
    {
        // The recorded import is the member's own symbol without the wrapper suffix its plan binds:
        // not the import the plan records, so nothing is withdrawn and the module fails closed.
        var plan = CallPlan(ArtifactId.Create(Method, ArtifactRole.PInvoke));
        var (withdrawals, unresolved) = SwiftPlanContradictions.Resolve(
            [CallbackRefused(transition)], [plan], NoOrphans,
            importRecorded ? PassedBy(plan with { EntryPoint = EntryPoint[..^"_optbuf".Length] }) : NoCallbackImports);

        Assert.Empty(withdrawals);
        Assert.Contains(importRecorded ? "no ABI call plan records the import that passes the closure" : "no import is recorded",
            Assert.Single(unresolved).Why);
    }

    [Fact]
    public void Contradiction_OutsideContainment_FailsTheModuleClosed()
    {
        Assert.Null(EmissionAttempt.Current);

        var thrown = Assert.Throws<SwiftPlanContradictionException>(() => SwiftPlanContradictions.WithdrawOrFail(
            Set(Refused(SwiftPlanRefusalKind.ResultMismatch)), [CallPlan(ArtifactId.Create(Method, ArtifactRole.PInvoke))], NoOrphans, NoCallbackImports, NullLogger.Instance));

        Assert.Contains(SwiftPlanContradictionException.Code, thrown.Message);
        Assert.Contains(EntryPoint, thrown.Message);
    }

    [Fact]
    public void Contradiction_WithdrawsTheMemberAndAbandonsTheAttempt()
    {
        var poison = new EmitterPoisonList();
        using var attempt = EmissionAttempt.Begin(poison);

        Assert.Throws<EmissionAttemptAbandoned>(() => SwiftPlanContradictions.WithdrawOrFail(
            Set(Refused(SwiftPlanRefusalKind.ResultMismatch)), [CallPlan(ArtifactId.Create(Method, ArtifactRole.PInvoke))], NoOrphans, NoCallbackImports, NullLogger.Instance));

        Assert.True(attempt.Abandoned);
        Assert.True(EmissionAttempt.IsDenied(Method));
        Assert.Equal(EmitterFaultOrigin.AbiRecoveryWithdrawal, Assert.Single(poison.Faults).Origin);
    }

    [Fact]
    public void ContradictionThatOutlivesItsMembersWithdrawal_FailsTheModuleClosed()
    {
        // The member was denied in an earlier attempt and its import is back: withdrawing it again
        // would loop, so the module fails instead.
        var poison = new EmitterPoisonList();
        poison.Record(EmitterFaultRecord.ForRecoveryWithdrawal(Method, RecoveryScope.LeafApi, "earlier", origin: EmitterFaultOrigin.AbiRecoveryWithdrawal));
        using var attempt = EmissionAttempt.Begin(poison);

        var thrown = Assert.Throws<SwiftPlanContradictionException>(() => SwiftPlanContradictions.WithdrawOrFail(
            Set(Refused(SwiftPlanRefusalKind.ResultMismatch)), [CallPlan(ArtifactId.Create(Method, ArtifactRole.PInvoke))], NoOrphans, NoCallbackImports, NullLogger.Instance));

        Assert.Contains("already withdrawn", thrown.Message);
    }

    [Fact]
    public void ContradictedImportNothingCalls_IsSpared_WhileALiveOwnerOfItIsWithdrawn()
    {
        // The ABI floor replaced one owner's body with a throw; the other still calls the import.
        var tombstone = ArtifactId.Create(Method, ArtifactRole.PInvoke);
        var live = ArtifactId.Create(OtherMethod, ArtifactRole.PInvoke);

        var (withdrawals, unresolved) = SwiftPlanContradictions.Resolve(
            [Refused(SwiftPlanRefusalKind.ResultMismatch)], [CallPlan(tombstone), CallPlan(live)], Orphaned(tombstone));

        Assert.Empty(unresolved);
        Assert.Equal(OtherMethod, Assert.Single(withdrawals).Unit.Decl);
    }

    [Fact]
    public void ContradictionOnlyOnImportsNothingCalls_ReturnsWithoutTouchingTheAttempt()
    {
        var owner = ArtifactId.Create(Method, ArtifactRole.PInvoke);
        var poison = new EmitterPoisonList();
        using var attempt = EmissionAttempt.Begin(poison);

        SwiftPlanContradictions.WithdrawOrFail(
            Set(Refused(SwiftPlanRefusalKind.ResultMismatch)), [CallPlan(owner)], Orphaned(owner), NoCallbackImports, NullLogger.Instance);

        Assert.False(attempt.Abandoned);
        Assert.True(poison.IsEmpty);
    }

    [Fact]
    public void NoContradiction_ReturnsWithoutTouchingTheAttempt()
    {
        var poison = new EmitterPoisonList();
        using var attempt = EmissionAttempt.Begin(poison);

        SwiftPlanContradictions.WithdrawOrFail(
            Set(Refused(SwiftPlanRefusalKind.Lowering), new SwiftPlanCoverageEntry(SwiftTransitionKind.ForwardPInvoke, "M.Box::PInvoke_size", "$s1M3BoxC4sizeSivg", SwiftPlanStatus.Planned)),
            [CallPlan(ArtifactId.Create(Method, ArtifactRole.PInvoke))],
            NoOrphans,
            NoCallbackImports,
            NullLogger.Instance);

        Assert.False(attempt.Abandoned);
        Assert.True(poison.IsEmpty);
    }

    private static readonly DeclId OtherMethod = DeclId.Create("M", "Box", BindingItemKind.Method, "make", ["note"], ["Swift.Optional<Swift.String>"]);

    private static readonly IReadOnlySet<SwiftPlanContradictions.ImportKey> NoOrphans =
        new HashSet<SwiftPlanContradictions.ImportKey>();

    private static IReadOnlySet<SwiftPlanContradictions.ImportKey> Orphaned(ArtifactId owner) =>
        new HashSet<SwiftPlanContradictions.ImportKey> { new(EntryPoint, CallPlan(owner).MethodName, owner) };

    private const string Callback = "make_message_1A2B3C4D_Callback";

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<SwiftPlanContradictions.ImportKey>> NoCallbackImports =
        new Dictionary<string, IReadOnlySet<SwiftPlanContradictions.ImportKey>>();

    // The import that hands the callback's closure to Swift, as the producer recorded it.
    private static IReadOnlyDictionary<string, IReadOnlySet<SwiftPlanContradictions.ImportKey>> PassedBy(AbiCallPlan import) =>
        new Dictionary<string, IReadOnlySet<SwiftPlanContradictions.ImportKey>>
        {
            [Callback] = new HashSet<SwiftPlanContradictions.ImportKey> { SwiftPlanContradictions.ImportKey.Of(import)! },
        };

    // A callback is the method Swift calls; its address is taken in the member that stores it.
    private static SwiftPlanCoverageEntry CallbackRefused(SwiftTransitionKind transition) =>
        Refused(SwiftPlanRefusalKind.ResultMismatch) with
        {
            Transition = transition,
            Member = transition == SwiftTransitionKind.ReverseCallback ? "M.Box::" + Callback : "M.Box::.cctor",
            EntryPoint = "",
            Target = transition == SwiftTransitionKind.CallbackAddress ? "M.Box::" + Callback : null,
        };

    private static SwiftPlanCoverageEntry Refused(SwiftPlanRefusalKind kind) =>
        new(SwiftTransitionKind.ForwardPInvoke, Member, EntryPoint, SwiftPlanStatus.Refused)
        {
            Refusals =
            [
                new SwiftPlanRefusal(kind, "the managed signature takes an indirect result (Swift.Runtime.ExistentialContainer1); the callee returns ptr directly", SwiftArchitecture.Arm64),
                new SwiftPlanRefusal(kind, "the managed signature takes an indirect result (Swift.Runtime.ExistentialContainer1); the callee returns ptr directly", SwiftArchitecture.X86_64),
            ],
        };

    private static SwiftCallPlanSet Set(params SwiftPlanCoverageEntry[] coverage) => new()
    {
        Module = "M",
        Coverage = [.. coverage],
        Plans = ImmutableArray<SwiftCallPlan>.Empty,
    };

    private static AbiCallPlan CallPlan(ArtifactId? owner) => new()
    {
        MethodName = Member[(Member.IndexOf("::", System.StringComparison.Ordinal) + 2)..],
        EntryPoint = EntryPoint,
        Library = "M.framework/M",
        CallingConvention = PInvokeCallingConvention.Swift,
        ReturnCarrier = "Swift.Runtime.ExistentialContainer1",
        ParameterCarriers = ["Swift.Runtime.SwiftIndirectResult", "System.IntPtr"],
        Owner = owner,
    };
}
