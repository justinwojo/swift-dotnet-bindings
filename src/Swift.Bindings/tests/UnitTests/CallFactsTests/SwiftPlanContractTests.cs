// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;
using static BindingsGeneration.Tests.SwiftCallPlanTests;

namespace BindingsGeneration.Tests;

/// <summary>
/// What a plan hands its consumer: the executable flag from swiftc's evidence (fail-closed without
/// it), the twin's canonical parameter types and the managed marshalling obligations, and a sidecar
/// that reads back only when every plan still hashes to its name.
/// </summary>
public class SwiftPlanContractTests
{
    private static SwiftArtifactScope Scope => SwiftArtifactScope.ForPlatform("M", "ios");

    /// <summary>A qualification record whose every qualified family shows <paramref name="symbol"/> as <paramref name="signature"/> on every iOS slice.</summary>
    private static SwiftLoweringEvidence Qualified(string symbol, string signature) =>
        SwiftLoweringEvidenceTests.Record("M", SwiftLoweringEvidenceTests.QualifiedCompilers, Scope.Slices, new Dictionary<string, string> { [symbol] = signature });

    private static SwiftLoweringJudgement Qualify(SwiftLoweringEvidence record) => SwiftLoweringQualification.Qualify(record, Scope);

    private static SwiftCallPlanSet Set(SwiftCallFacts facts, SwiftManagedSignature managed, string target)
    {
        var outcome = Planner().PlanForward(facts, managed, target);
        var plan = Planned(outcome);
        var entry = new SwiftPlanCoverageEntry(SwiftTransitionKind.ForwardPInvoke, "M::f", target, SwiftPlanStatus.Planned)
        {
            Target = target,
            Symbol = plan.Symbol,
            Hash = plan.Hash,
            Callee = outcome.Callee,
            Evidence = SwiftCallPlanSet.NoEvidenceReason,
        };
        return new SwiftCallPlanSet { Module = "M", Coverage = [entry], Plans = [plan] };
    }

    // ---- the executable flag ----

    [Fact]
    public void WithoutEvidence_NoPlanIsExecutable()
    {
        var set = Set(Free("Swift.Int", "Swift.Int"), Signature("long", "long"), "$s1M1fyS2iF");

        Assert.False(Assert.Single(set.Coverage).Executable);
        Assert.Equal(SwiftCallPlanSet.NoEvidenceReason, set.Coverage[0].Evidence);
        Assert.Null(set.EvidenceScope);
        var judged = set.WithEvidence(Qualify(SwiftLoweringEvidence.Empty));
        Assert.False(Assert.Single(judged.Coverage).Executable);
        Assert.Contains("no receipt of M", judged.Coverage[0].Evidence);
    }

    [Fact]
    public void APlanIsExecutable_OnlyWhenSwiftcAgreesOnEveryClaimedSliceUnderEveryQualifiedFamily()
    {
        var set = Set(Free("Swift.Int", "Swift.Int"), Signature("long", "long"), "$s1M1fyS2iF");
        var callee = set.Coverage[0].Callee!;
        var agreeing = Qualified("$s1M1fyS2iF", "swiftcc " + callee);

        var judged = set.WithEvidence(Qualify(agreeing));
        var joined = Assert.Single(judged.Coverage);
        Assert.True(joined.Executable, joined.Evidence);
        Assert.Equal($"swiftc gives swiftcc {callee} on 3 triple(s) under 2 toolchain(s)", joined.Evidence);
        Assert.Equal(new SwiftPlanEvidenceScope("M", Scope.Slices, SwiftLoweringJudgement.QualificationBasis), judged.EvidenceScope);

        // One qualified family that disagrees on one slice.
        var split = agreeing.WithSlice("M", SwiftLoweringEvidenceTests.QualifiedCompilers[1], SwiftLoweringContract.Current, null, Scope.Slices[0],
            SwiftLoweringEvidenceTests.Slice(Scope.Slices[0], new Dictionary<string, string> { ["$s1M1fyS2iF"] = "swiftcc i64 (i64, i64)" }));
        var disagreed = Assert.Single(set.WithEvidence(Qualify(split)).Coverage);
        Assert.False(disagreed.Executable);
        Assert.Contains("the plan predicts", disagreed.Evidence);

        // A symbol swiftc never showed, and a different convention.
        Assert.Contains("shows no", Assert.Single(set.WithEvidence(Qualify(Qualified("$s1M1other", "swiftcc " + callee))).Coverage).Evidence);
        Assert.False(Assert.Single(set.WithEvidence(Qualify(Qualified("$s1M1fyS2iF", "ccc " + callee))).Coverage).Executable);
    }

    [Fact]
    public void AThickOrUnexplainedPlan_IsNeverExecutable()
    {
        var thick = Planned(Planner().PlanForward(Closure("Swift.Int", "Swift.Int"), Signature(SwiftManagedMarshalling.None, "long", "long", "SwiftSelf"), null));
        var entry = new SwiftPlanCoverageEntry(SwiftTransitionKind.ForwardPInvoke, "M::g", "g", SwiftPlanStatus.Planned) { Symbol = thick.Symbol, Callee = "i64 (i64, ptr swiftself)" };

        var joined = Assert.Single(Qualify(Qualified("g", "swiftcc i64 (i64, ptr swiftself)")).Join([entry], [thick]));
        Assert.False(joined.Executable);
        Assert.Equal(SwiftLoweringEvidence.ClosureReason, joined.Evidence);

        var refused = new SwiftPlanCoverageEntry(SwiftTransitionKind.ForwardPInvoke, "M::h", "h", SwiftPlanStatus.Refused);
        Assert.Null(Assert.Single(Qualify(Qualified("h", "swiftcc void ()")).Join([refused], [])).Evidence);
    }

    // ---- the sidecar ----

    [Fact]
    public void ASidecar_ReadsBack_AndAnEditedPlanIsRejected()
    {
        var set = Set(Free("Swift.Int", "Swift.Int"), Signature("long", "long"), "$s1M1fyS2iF");
        set = set.WithEvidence(Qualify(Qualified("$s1M1fyS2iF", "swiftcc " + set.Coverage[0].Callee)));
        var path = Path.Combine(Path.GetTempPath(), $"plans-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, set.ToJson());
            var read = SwiftCallPlanSet.From(SwiftCallPlanSet.Read(path));
            Assert.Equal(set.ToJson(), read.ToJson());

            File.WriteAllText(path, set.ToJson().Replace("\"$s1M1fyS2iF\"", "\"$s1M1gyS2iF\""));
            var error = Assert.Throws<InvalidDataException>(() => SwiftCallPlanSet.Read(path));
            Assert.Contains("does not hash to its name", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- the twin ----

    [Theory]
    [InlineData(SwiftRegisterClass.General, 1, true, "sbyte")]
    [InlineData(SwiftRegisterClass.General, 1, false, "byte")]
    [InlineData(SwiftRegisterClass.General, 2, true, "short")]
    [InlineData(SwiftRegisterClass.General, 4, false, "uint")]
    [InlineData(SwiftRegisterClass.General, 8, true, "nint")]
    [InlineData(SwiftRegisterClass.General, 8, false, "nint")]
    [InlineData(SwiftRegisterClass.Float, 4, true, "float")]
    [InlineData(SwiftRegisterClass.Float, 8, true, "double")]
    public void TwinTypes_FollowTheLayout(SwiftRegisterClass @class, int size, bool signed, string expected) =>
        Assert.Equal(expected, SwiftTwinSignature.TypeOf(@class, size, signed));

    [Fact]
    public void TwinSignature_IsTheLayout_NotTheSpelling()
    {
        // A long and an nint are the same eight bytes; a String and its buffer struct the same two words.
        var spelledLong = Planned(Planner().PlanForward(Free("Swift.Int", "Swift.Int", "Swift.String"), Signature("long", "long", "Swift.SwiftString.Buffer"), "$s1M1f"));
        var spelledNint = Planned(Planner().PlanForward(Free("Swift.Int", "Swift.Int", "Swift.String"), Signature("nint", "nint", "Swift.SwiftString.Buffer"), "$s1M1f"));

        Assert.Equal("nint (nint, nint, nint)", spelledLong.Twin.ToString());
        Assert.Equal(spelledLong.Twin, spelledNint.Twin);

        var throwing = Planned(Planner().PlanForward(Throwing(Free("Swift.Double", "Swift.Int")), Signature("double", "long", "SwiftError*"), "$s1M1t"));
        Assert.Equal("double (nint, nint)", throwing.Twin.ToString());
        var direct = Planned(Planner().PlanForward(Free("Swift.String"), Signature("Swift.SwiftString.Buffer"), "$s1M1s"));
        Assert.Equal("void (nint)", direct.Twin.ToString());
    }

    // ---- a Swift Bool result ----

    /// <summary>
    /// A Swift-convention Bool comes back as an <c>i1</c>: only bit 0 is defined. The import reads it
    /// through the runtime's Swift Bool marshaller, which is a one-byte carrier like <c>MarshalAs(U1)</c>,
    /// so the plan must accept it rather than refuse it as a four-byte BOOL. A refusal here keeps the
    /// import on its direct Swift-convention route, which Mono refuses outright when it has a self.
    /// </summary>
    [Fact]
    public void BoolResultReadThroughTheSwiftBoolMarshaller_IsPlannedAsOneByteReadFromBitZero()
    {
        var method = (Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax)Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseMemberDeclaration(
            "[return: global::System.Runtime.InteropServices.Marshalling.MarshalUsing(typeof(global::Swift.Runtime.Marshalling.SwiftBoolMarshaller))] " +
            "private static partial bool PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);")!;
        var managed = SwiftManagedSignature.Of(method, SwiftManagedMarshalling.SourceGenerated);

        Assert.Equal(SwiftManagedSignature.SwiftBoolMarshalling, managed.ReturnMarshalAs);
        var obligations = Obligations(ClassMethod("Swift.Bool", "Swift.Int"), managed);
        Assert.Contains(obligations, o => o.Kind == SwiftMarshallingObligationKind.NormalizeBool && o.Parameter is null
            && o.Detail.Contains("1-byte integer, bit 0 is the value"));
    }

    // ---- the obligations ----

    private static ImmutableArray<SwiftMarshallingObligation> Obligations(SwiftCallFacts facts, SwiftManagedSignature managed, string? source = null, ISwiftValueLowering? lowering = null)
    {
        var outcome = Planner(source, lowering).PlanForward(facts, managed, "$s1M1f");
        Planned(outcome);
        return outcome.Obligations;
    }

    [Fact]
    public void Obligations_NameWhatTheManagedSideMustDo()
    {
        var kinds = Obligations(Throwing(ClassMethod("Swift.String", "Swift.Int")), Signature("Swift.SwiftString.Buffer", "long", "SwiftSelf", "SwiftError*"))
            .Select(o => (o.Kind, o.Parameter)).ToList();
        Assert.Contains((SwiftMarshallingObligationKind.SelfValue, (int?)1), kinds);
        Assert.Contains((SwiftMarshallingObligationKind.ErrorSlot, (int?)2), kinds);
        Assert.Contains((SwiftMarshallingObligationKind.ResultBuffer, (int?)null), kinds);

        var wide = With(("M.Wide", SwiftLoweringAnswer.IndirectBecause("five pieces")));
        Assert.Contains(Obligations(Free("M.Wide"), Signature("void", "SwiftIndirectResult"), lowering: wide),
            o => o.Kind == SwiftMarshallingObligationKind.IndirectResultAddress && o.Parameter == 0);

        Assert.Contains(Obligations(Free(null, "Swift.String"), Signature("void", "Swift.SwiftString.Buffer")),
            o => o.Kind == SwiftMarshallingObligationKind.AggregatePieces && o.Detail.Contains("0:8, 8:8"));
    }

    [Fact]
    public void Obligations_CarryOwnershipAndInoutFromTheFacts()
    {
        var facts = Free(null, "Swift.Int", "Swift.Int");
        facts = facts with
        {
            Parameters =
            [
                facts.Parameters[0] with { Convention = SwiftParameterConvention.Owned },
                facts.Parameters[1] with { Convention = SwiftParameterConvention.Inout },
            ],
        };
        var obligations = Obligations(facts, Signature("void", "long", "long*"));

        Assert.Contains(obligations, o => o.Kind == SwiftMarshallingObligationKind.OwnedTransfer && o.Formal == 0);
        Assert.Contains(obligations, o => o.Kind == SwiftMarshallingObligationKind.InoutExclusive && o.Formal == 1);
    }

    [Theory]
    [InlineData(SwiftManagedMarshalling.SourceGenerated, "I1", SwiftManagedRefKind.Ref, "1-byte temporary holding true or false, and copy it back")]
    [InlineData(SwiftManagedMarshalling.Runtime, null, SwiftManagedRefKind.Ref, "4-byte temporary holding true or false, and copy it back")]
    [InlineData(SwiftManagedMarshalling.SourceGenerated, "I1", SwiftManagedRefKind.Out, "1-byte temporary, and copy it back")]
    public void Obligations_OfAMarshalledBoolByReference_NameTheReferentNotTheAddress(SwiftManagedMarshalling marshalling, string? marshalAs, SwiftManagedRefKind refKind, string expected)
    {
        var facts = Free(null, "Swift.Bool");
        facts = facts with { Parameters = [facts.Parameters[0] with { Convention = SwiftParameterConvention.Inout }] };
        var managed = new SwiftManagedSignature([new SwiftManagedParameter("p0", "bool", refKind, marshalAs)], "void", null, marshalling);

        var obligations = Obligations(facts, managed);

        var normalize = Assert.Single(obligations, o => o.Kind == SwiftMarshallingObligationKind.NormalizeBool);
        Assert.Contains(expected, normalize.Detail);
        Assert.DoesNotContain(obligations, o => o.Kind == SwiftMarshallingObligationKind.PinByReference);
    }

    [Fact]
    public void Obligations_OfAnUnmarshalledBoolByReference_PinTheManagedByte()
    {
        var facts = Free(null, "Swift.Bool");
        facts = facts with { Parameters = [facts.Parameters[0] with { Convention = SwiftParameterConvention.Inout }] };
        var managed = new SwiftManagedSignature([new SwiftManagedParameter("p0", "bool", SwiftManagedRefKind.Ref)], "void", null, SwiftManagedMarshalling.None);

        var obligations = Obligations(facts, managed);

        Assert.Contains(obligations, o => o.Kind == SwiftMarshallingObligationKind.PinByReference);
        Assert.DoesNotContain(obligations, o => o.Kind == SwiftMarshallingObligationKind.NormalizeBool);
    }

    [Fact]
    public void Obligations_OfAPrimitiveOnlyCall_AreNone() =>
        Assert.Empty(Obligations(Free("Swift.Int", "Swift.Int", "Swift.Double"), Signature("long", "long", "double")));
}
