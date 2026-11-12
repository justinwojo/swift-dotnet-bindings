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
    private const string Triple0 = "arm64-apple-ios15.0";

    private static SwiftLoweringEvidence Evidence(string symbol, string signature, string toolchain = "Swift version 6.2")
    {
        var evidence = SwiftLoweringEvidence.Empty;
        foreach (var triple in SwiftLoweringEvidence.RequiredTriples)
            evidence = evidence.WithTriple(toolchain, triple, new Dictionary<string, string> { [symbol] = signature });
        return evidence;
    }

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
        Assert.False(Assert.Single(set.WithEvidence(SwiftLoweringEvidence.Empty).Coverage).Executable);
    }

    [Fact]
    public void APlanIsExecutable_OnlyWhenSwiftcAgreesOnEveryTripleUnderEveryToolchain()
    {
        var set = Set(Free("Swift.Int", "Swift.Int"), Signature("long", "long"), "$s1M1fyS2iF");
        var callee = set.Coverage[0].Callee!;
        var agreeing = Evidence("$s1M1fyS2iF", "swiftcc " + callee);

        var joined = Assert.Single(set.WithEvidence(agreeing).Coverage);
        Assert.True(joined.Executable, joined.Evidence);

        // Another toolchain that disagrees on one triple.
        var split = agreeing.WithTriple("Swift version 6.3", Triple0, new Dictionary<string, string> { ["$s1M1fyS2iF"] = "swiftcc i64 (i64, i64)" });
        var disagreed = Assert.Single(set.WithEvidence(split).Coverage);
        Assert.False(disagreed.Executable);
        Assert.Contains("the plan predicts", disagreed.Evidence);

        // A triple with no evidence, and a symbol swiftc never showed.
        var partial = SwiftLoweringEvidence.Empty.WithTriple("Swift version 6.2", Triple0, new Dictionary<string, string> { ["$s1M1fyS2iF"] = "swiftcc " + callee });
        Assert.False(Assert.Single(set.WithEvidence(partial).Coverage).Executable);
        Assert.Contains("shows no", Assert.Single(set.WithEvidence(Evidence("$s1M1other", "swiftcc " + callee)).Coverage).Evidence);
        // A different convention is not agreement.
        Assert.False(Assert.Single(set.WithEvidence(Evidence("$s1M1fyS2iF", "ccc " + callee)).Coverage).Executable);
    }

    [Fact]
    public void AThickOrUnexplainedPlan_IsNeverExecutable()
    {
        var thick = Planned(Planner().PlanForward(Closure("Swift.Int", "Swift.Int"), Signature(SwiftManagedMarshalling.None, "long", "long", "SwiftSelf"), null));
        var entry = new SwiftPlanCoverageEntry(SwiftTransitionKind.ForwardPInvoke, "M::g", "g", SwiftPlanStatus.Planned) { Symbol = thick.Symbol, Callee = "i64 (i64, ptr swiftself)" };

        var joined = Assert.Single(SwiftLoweringEvidence.Empty.WithTriple("t", Triple0, new Dictionary<string, string>()).Join([entry], [thick]));
        Assert.False(joined.Executable);
        Assert.Equal(SwiftLoweringEvidence.ClosureReason, joined.Evidence);

        var refused = new SwiftPlanCoverageEntry(SwiftTransitionKind.ForwardPInvoke, "M::h", "h", SwiftPlanStatus.Refused);
        Assert.Null(Assert.Single(Evidence("h", "swiftcc void ()").Join([refused], [])).Evidence);
    }

    [Fact]
    public void Evidence_RoundTripsInOrdinalOrder_AndAToolchainSectionIsReplacedWhole()
    {
        var evidence = Evidence("$sB", "swiftcc void ()").WithTriple("Swift version 6.2", Triple0, new Dictionary<string, string> { ["$sZ"] = "swiftcc void ()", ["$sA"] = "swiftcc i64 ()" });
        var path = Path.Combine(Path.GetTempPath(), $"evidence-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, evidence.ToJson());
            var read = SwiftLoweringEvidence.Load(path);
            Assert.Equal(evidence.ToJson(), read.ToJson());
            var device = read.Toolchains.Single().Triples[Triple0];
            Assert.Equal(new[] { "$sA", "$sZ" }, device.Keys);
            Assert.True(read.Judge("$sA", "i64 ()").Executable is false); // not on the simulators
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Throws<InvalidDataException>(() =>
        {
            var bad = Path.Combine(Path.GetTempPath(), $"evidence-{Guid.NewGuid():N}.json");
            File.WriteAllText(bad, "{\"schema\": 99, \"triples\": [\"x\"], \"toolchains\": []}");
            try { SwiftLoweringEvidence.Load(bad); } finally { File.Delete(bad); }
        });
    }

    [Fact]
    public void TheRequiredTriples_AreTheGenerators_NotTheEvidenceFiles()
    {
        // Device-only evidence that names only the device triple: neither the judge nor the loader trusts its list.
        var deviceOnly = SwiftLoweringEvidence.Empty.WithTriple("Swift version 6.2", Triple0, new Dictionary<string, string> { ["$sA"] = "swiftcc void ()" })
            with { Triples = [Triple0] };
        Assert.False(deviceOnly.Judge("$sA", "void ()").Executable);

        var path = Path.Combine(Path.GetTempPath(), $"evidence-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, deviceOnly.ToJson());
            Assert.Contains("requires", Assert.Throws<InvalidDataException>(() => SwiftLoweringEvidence.Load(path)).Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- the sidecar ----

    [Fact]
    public void ASidecar_ReadsBack_AndAnEditedPlanIsRejected()
    {
        var set = Set(Free("Swift.Int", "Swift.Int"), Signature("long", "long"), "$s1M1fyS2iF");
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
