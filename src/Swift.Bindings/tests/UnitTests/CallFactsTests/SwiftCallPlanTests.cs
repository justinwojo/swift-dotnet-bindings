// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// <see cref="SwiftCallPlanner"/> and <see cref="SwiftCallPlanSet"/>: the bridge plan of a Swift
/// transition on arm64 and x86_64 (where every argument arrives, where the callee reads it, which
/// special registers the bridge fills, how the result comes back and what the frame saves), every
/// refusal with its reason, the managed-view agreement across architectures, the hash-named and
/// deduplicated plan set with its coverage of every transition, and the hand-written facts tables
/// of the runtime and the Apple supplement.
/// </summary>
public class SwiftCallPlanTests
{
    // ---- helpers ----

    internal static readonly ISwiftValueLowering Layout = new SwiftLayoutValueLowering(Array.Empty<ModuleDecl>(), new TypeDatabase());

    internal static SwiftTypeFacts Type(string spelling, SwiftTypeCategory category = SwiftTypeCategory.Struct) =>
        new() { Spelling = spelling, Category = category, IsFrozen = true, IsClassBound = category == SwiftTypeCategory.Class };

    internal static SwiftParameterFacts Param(int index, string spelling) => new()
    {
        Index = index,
        Type = Type(spelling),
        Convention = SwiftParameterConvention.Guaranteed,
        ConventionSource = SwiftConventionSource.Default,
    };

    internal static SwiftResultFacts Returns(string? spelling) =>
        spelling is null ? SwiftResultFacts.Void : new SwiftResultFacts { Type = Type(spelling) };

    internal static SwiftCallFacts Free(string? result, params string[] parameters) => new()
    {
        Target = new SwiftCallTarget { Kind = SwiftCallTargetKind.FreeFunction, Symbol = "$s1M1fyyF" },
        Self = SwiftSelfFacts.None,
        Parameters = parameters.Select((p, i) => Param(i, p)).ToImmutableArray(),
        Result = Returns(result),
        Effects = SwiftCallEffects.None,
    };

    internal static SwiftCallFacts Throwing(SwiftCallFacts facts) => facts with { Effects = new SwiftCallEffects { Throws = true } };

    /// <summary>An instance method of the class <c>M.C</c>: self is a reference in the self register.</summary>
    internal static SwiftCallFacts ClassMethod(string? result, params string[] parameters) => Free(result, parameters) with
    {
        Target = new SwiftCallTarget { Kind = SwiftCallTargetKind.Method, Symbol = "$s1M1CC1fyyF", DeclaringType = "M.C" },
        Self = new SwiftSelfFacts
        {
            Kind = SwiftCallSelfKind.Reference,
            Mutation = SwiftSelfMutation.Nonmutating,
            Abstraction = SwiftSelfAbstraction.Concrete,
            Type = Type("M.C", SwiftTypeCategory.Class),
        },
    };

    /// <summary>A thick closure: its function takes the formals, then its context in the self register.</summary>
    internal static SwiftCallFacts Closure(string? result, params string[] parameters) => Free(result, parameters) with
    {
        Target = new SwiftCallTarget
        {
            Kind = SwiftCallTargetKind.ClosureInvocation,
            Entry = SwiftEntryKind.FunctionPointer,
            PointerSource = "closure argument 'body' of $s1M1g",
        },
        Self = new SwiftSelfFacts
        {
            Kind = SwiftCallSelfKind.ClosureContext,
            Mutation = SwiftSelfMutation.NotApplicable,
            Abstraction = SwiftSelfAbstraction.Concrete,
        },
    };

    internal static SwiftManagedSignature Signature(string returns, params string[] parameters) =>
        Signature(SwiftManagedMarshalling.SourceGenerated, returns, parameters);

    internal static SwiftManagedSignature Signature(SwiftManagedMarshalling marshalling, string returns, params string[] parameters) => new(
        parameters.Select((p, i) => new SwiftManagedParameter($"p{i}", p)).ToImmutableArray(), returns, null, marshalling);

    internal static SwiftManagedTypeIndex Index(string? source)
    {
        var index = new SwiftManagedTypeIndex();
        if (source is not null)
            index.Add(CSharpSyntaxTree.ParseText(source).GetRoot());
        return index;
    }

    internal static SwiftCallPlanner Planner(string? source = null, ISwiftValueLowering? lowering = null) =>
        new(Index(source), SwiftArchitectureModel.Default(lowering ?? Layout));

    /// <summary>The layout lowering, with some types answered by hand.</summary>
    internal static ISwiftValueLowering With(params (string Type, SwiftLoweringAnswer Answer)[] answers) =>
        new SwiftOverriddenLowering(Layout, answers.ToDictionary(a => a.Type, a => a.Answer, StringComparer.Ordinal));

    internal static SwiftCallPlan Planned(SwiftPlanOutcome outcome)
    {
        Assert.True(outcome.IsPlanned, string.Join("; ", outcome.Refusals));
        Assert.Empty(outcome.Refusals);
        return outcome.Plan!;
    }

    internal static SwiftNativePlan On(SwiftCallPlan plan, SwiftArchitecture architecture) =>
        plan.Body.Architectures.Single(a => a.Architecture == architecture);

    private static string Registers(IEnumerable<SwiftOperand> operands) => string.Join(" ", operands);

    private static string Moves(SwiftNativePlan native) =>
        string.Join(" ", native.Moves.Select(m => $"{m.Role}:{m.From}->{m.To}"));

    private static SwiftPlanRefusal Refused(SwiftPlanOutcome outcome, SwiftPlanRefusalKind kind)
    {
        Assert.False(outcome.IsPlanned);
        Assert.Null(outcome.Plan);
        var refusal = outcome.Refusals.FirstOrDefault(r => r.Kind == kind);
        Assert.True(refusal is not null, $"expected a {kind} refusal; got {string.Join("; ", outcome.Refusals)}");
        return refusal!;
    }

    // ---- planner: the prefix, the special registers, the result ----

    [Fact]
    public void Aggregate_TravelsAsItsSwiftPieces_InPlace()
    {
        // String is a count word and a bridge object; the runtime's buffer has the same two words.
        var plan = Planned(Planner().PlanForward(Free(null, "Swift.String"), Signature("void", "Swift.SwiftString.Buffer"), "$s1M1fyySSF"));

        Assert.Equal(SwiftPlanKind.Forward, plan.Body.Kind);
        Assert.Equal("$s1M1fyySSF", plan.Body.Target);
        Assert.Equal(
            new[] { (SwiftPieceSource.Piece, 0, 8), (SwiftPieceSource.Piece, 8, 8) },
            plan.Body.Managed.Arguments.Select(a => (a.Source, a.Offset, a.Size)));
        var arm64 = On(plan, SwiftArchitecture.Arm64);
        Assert.Equal("x0:8 x1:8", Registers(arm64.Incoming));
        Assert.Equal(Registers(arm64.Incoming), Registers(arm64.Outgoing));
        Assert.Empty(arm64.Moves);
        Assert.Equal("rdi:8 rsi:8", Registers(On(plan, SwiftArchitecture.X86_64).Outgoing));
        Assert.Equal(SwiftResultKind.Void, plan.Body.Managed.Result.Kind);
    }

    [Fact]
    public void Self_MovesFromItsTrailingArgumentIntoTheSelfRegister()
    {
        var plan = Planned(Planner().PlanForward(ClassMethod("Swift.Int", "Swift.Int"), Signature("long", "long", "SwiftSelf"), "$s1M1CC1fyS2iF"));

        Assert.Equal(new[] { SwiftTwinRole.Ordinary, SwiftTwinRole.Self }, plan.Body.Managed.Arguments.Select(a => a.Role));
        var arm64 = On(plan, SwiftArchitecture.Arm64);
        Assert.Equal("Self:x1:8->x20:8", Moves(arm64));
        Assert.Equal(new[] { "x29", "x30", "x20", "x19" }, arm64.Frame.SavedRegisters);
        Assert.Equal("x0:8", arm64.Result.Register!.ToString());
        var x64 = On(plan, SwiftArchitecture.X86_64);
        Assert.Equal("Self:rsi:8->r13:8", Moves(x64));
        Assert.Equal(new[] { "rbp", "r13" }, x64.Frame.SavedRegisters);
        Assert.Equal("rax:8", x64.Result.Register!.ToString());
        Assert.False(arm64.ZeroError);
    }

    [Fact]
    public void DirectMultiPieceResult_IsStoredThroughTheResultBuffer()
    {
        var plan = Planned(Planner().PlanForward(Free("Swift.String"), Signature("Swift.SwiftString.Buffer"), "$s1M1fSSyF"));

        var result = plan.Body.Managed.Result;
        Assert.Equal(SwiftResultKind.Direct, result.Kind);
        Assert.Equal(16, result.Size);
        Assert.Equal(SwiftTwinRole.ResultBuffer, Assert.Single(plan.Body.Managed.Arguments).Role);

        var arm64 = On(plan, SwiftArchitecture.Arm64);
        Assert.Equal("ResultBuffer:x0:8->x22:8", Moves(arm64));
        Assert.Equal("x22", arm64.Frame.ResultBuffer);
        Assert.Equal(new[] { ("x0:8", 0), ("x1:8", 8) }, arm64.Result.Moves.Select(m => (m.Register.ToString(), m.BufferOffset)));
        var x64 = On(plan, SwiftArchitecture.X86_64);
        Assert.Equal("ResultBuffer:rdi:8->r14:8", Moves(x64));
        Assert.Equal(new[] { ("rax:8", 0), ("rdx:8", 8) }, x64.Result.Moves.Select(m => (m.Register.ToString(), m.BufferOffset)));
    }

    [Fact]
    public void IndirectResult_MovesIntoTheIndirectResultRegister()
    {
        var lowering = With(("M.Wide", SwiftLoweringAnswer.IndirectBecause("five pieces")));
        var plan = Planned(Planner(lowering: lowering).PlanForward(Free("M.Wide", "Swift.Int"), Signature("void", "SwiftIndirectResult", "long"), "$s1M1f"));

        Assert.Equal(SwiftResultKind.Indirect, plan.Body.Managed.Result.Kind);
        // The ordinary argument stays first; the result address trails it whatever the declaration's order.
        Assert.Equal(new[] { SwiftTwinRole.Ordinary, SwiftTwinRole.IndirectResult }, plan.Body.Managed.Arguments.Select(a => a.Role));
        Assert.Equal(new int?[] { 1, 0 }, plan.Body.Managed.Arguments.Select(a => a.Parameter));
        Assert.Equal("IndirectResult:x1:8->x8:8", Moves(On(plan, SwiftArchitecture.Arm64)));
        Assert.Equal("IndirectResult:rsi:8->rax:8", Moves(On(plan, SwiftArchitecture.X86_64)));
    }

    [Fact]
    public void Throwing_KeepsTheErrorSlotInACalleeSavedRegister_AndZeroesTheErrorRegister()
    {
        var plan = Planned(Planner().PlanForward(Throwing(Free("Swift.Int", "Swift.Int")), Signature("long", "long", "SwiftError*"), "$s1M1f"));

        var arm64 = On(plan, SwiftArchitecture.Arm64);
        Assert.True(arm64.ZeroError);
        Assert.Equal("Error:x1:8->x19:8", Moves(arm64));
        Assert.Equal("x19", arm64.Frame.ErrorSlot);
        Assert.Equal(new[] { "x29", "x30", "x20", "x19", "x22", "x21" }, arm64.Frame.SavedRegisters);
        var x64 = On(plan, SwiftArchitecture.X86_64);
        Assert.True(x64.ZeroError);
        Assert.Equal("Error:rsi:8->rbx:8", Moves(x64));
        Assert.Equal(new[] { "rbp", "rbx", "r12" }, x64.Frame.SavedRegisters);
        // The return address and three pushes leave sp 16-aligned: no padding.
        Assert.Equal(0, x64.Frame.LocalBytes);
    }

    [Fact]
    public void StackPrefix_IsWhatSpillsPastTheArgumentRegisters()
    {
        var nine = Enumerable.Repeat("Swift.Int", 9).ToArray();
        var plan = Planned(Planner().PlanForward(Free(null, nine), Signature("void", Enumerable.Repeat("long", 9).ToArray()), "$s1M1f"));

        var arm64 = On(plan, SwiftArchitecture.Arm64);
        Assert.Equal(8, arm64.StackPrefixBytes);
        Assert.Equal("[stack+0]:8", arm64.Outgoing[8].ToString());
        Assert.Equal(16, arm64.Frame.LocalBytes);
        var x64 = On(plan, SwiftArchitecture.X86_64);
        Assert.Equal(24, x64.StackPrefixBytes);
        Assert.Equal("[stack+16]:8", x64.Outgoing[8].ToString());
        Assert.Equal(24, x64.Frame.OutgoingStackBytes);
        Assert.Equal(32, x64.Frame.LocalBytes);
    }

    [Theory]
    [InlineData("Swift.UInt8", "byte", "zero")]
    [InlineData("Swift.Int8", "sbyte", "sign")]
    [InlineData("Swift.Int16", "short", "sign")]
    [InlineData("Swift.Int32", "int", null)]
    public void SmallIntegerResult_IsReExtendedTo32Bits_AsTheManagedTypeExpects(string swift, string managed, string? extension)
    {
        var plan = Planned(Planner().PlanForward(Free(swift), Signature(managed), "$s1M1f"));

        Assert.All(plan.Body.Architectures, a => Assert.Equal(extension, a.Result.ExtendTo32));
    }

    [Theory]
    [InlineData("Swift.Int8", "sbyte", "sign")]
    [InlineData("Swift.UInt16", "ushort", "zero")]
    [InlineData("Swift.Int", "long", null)]
    public void SmallIntegerReverseResult_IsReExtendedTo32Bits_ForSwift(string swift, string managed, string? extension)
    {
        var plan = Planned(Planner().PlanReverse(Closure(swift, "Swift.Int"), Signature(SwiftManagedMarshalling.None, managed, "long", "SwiftSelf")));

        Assert.All(plan.Body.Architectures, a => Assert.Equal(extension, a.Result.ExtendTo32));
    }

    [Fact]
    public void ExtraTrailingManagedArgument_TheFactsDoNotExplain_IsRefused()
    {
        var refusal = Refused(Planner().PlanForward(Free(null, "Swift.Int"), Signature("void", "long", "nint"), "$s1M1f"), SwiftPlanRefusalKind.PrefixMismatch);

        Assert.Contains("benign", refusal.Reason);
    }

    private static SwiftManagedSignature Named(string returns, params (string Name, string Type)[] parameters) => new(
        parameters.Select(p => new SwiftManagedParameter(p.Name, p.Type)).ToImmutableArray(), returns, null, SwiftManagedMarshalling.SourceGenerated);

    [Fact]
    public void AThinMetatypeArgument_IsUnread_BecauseSwiftTakesNoMetatypeForAConcreteType()
    {
        var facts = Free("Swift.Int") with
        {
            Self = new SwiftSelfFacts { Kind = SwiftCallSelfKind.ThinMetatype, Mutation = SwiftSelfMutation.NotApplicable, Abstraction = SwiftSelfAbstraction.Concrete, Type = Type("M.S") },
        };
        var plan = Planned(Planner().PlanForward(facts, Named("long", ("metatype", "nint")), "$s1M1SVACycfC"));

        var unread = Assert.Single(plan.Body.Managed.Arguments, a => a.Unread);
        Assert.Equal(SwiftCallPlanner.ThinMetatypeReason, unread.UnreadReason);
        Assert.Empty(On(plan, SwiftArchitecture.Arm64).Outgoing);

        // Named otherwise, the same argument is not explained.
        Refused(Planner().PlanForward(facts, Named("long", ("other", "nint")), "$s1M1SVACycfC"), SwiftPlanRefusalKind.PrefixMismatch);
    }

    [Fact]
    public void ATypeLevelMetadataArgument_IsUnread_WhenSelfCarriesTheMetadata()
    {
        var method = ClassMethod("Swift.Int", "Swift.Int");
        var facts = method with
        {
            Self = method.Self with { Abstraction = SwiftSelfAbstraction.GenericParent },
            Generics = new SwiftGenericSignatureFacts { Parameters = ["τ_0_0"] },
        };
        var plan = Planned(Planner().PlanForward(facts, Named("long", ("index", "long"), ("TMetadata", "nint"), ("self", "SwiftSelf")), "$s1M1CC1fyS2iF"));

        var unread = Assert.Single(plan.Body.Managed.Arguments, a => a.Unread);
        Assert.Equal(SwiftCallPlanner.FulfilledMetadataReason, unread.UnreadReason);
        Assert.Equal(1, unread.Parameter);

        // A member's own generic parameter is not in self's metadata: the callee takes and reads it.
        var own = facts with { Generics = new SwiftGenericSignatureFacts { Parameters = ["τ_0_0"], OwnParameters = ["τ_0_0"] } };
        var read = Planned(Planner().PlanForward(own, Named("long", ("index", "long"), ("TMetadata", "nint"), ("self", "SwiftSelf")), "$s1M1CC1fyS2iF"));
        Assert.DoesNotContain(read.Body.Managed.Arguments, a => a.Unread);
        // More surplus arguments than type-level parameters are not explained either.
        Refused(Planner().PlanForward(facts, Named("long", ("index", "long"), ("TMetadata", "nint"), ("UMetadata", "nint"), ("self", "SwiftSelf")), "$s1M1CC1fyS2iF"),
            SwiftPlanRefusalKind.PrefixMismatch);
    }

    [Fact]
    public void AMetadataArgument_InTheCalleesWitnessTableSlot_IsRefused()
    {
        // `func f<T: P>(_ x: Int)` takes (x, T's metadata, T: P's witness table). A managed
        // declaration that leaves the table out and passes a trailing metatype instead fits every
        // register by class and size, and the callee reads the metatype as the table.
        var facts = Free(null, "Swift.Int") with
        {
            Generics = new SwiftGenericSignatureFacts
            {
                Parameters = ["τ_0_0"],
                Requirements = [new SwiftGenericRequirementFacts("τ_0_0", "M.P", SwiftGenericRequirementKind.Conformance) { TargetKind = SwiftRequirementTargetKind.Protocol }],
            },
        };

        var refusal = Refused(Planner().PlanForward(facts, Named("void", ("x", "long"), ("TMetadata", "nint"), ("metatype", "nint")), "$s1M1fyySilF"),
            SwiftPlanRefusalKind.PrefixMismatch);
        Assert.Contains("metatype", refusal.Reason);
        Assert.Contains(nameof(SwiftPhysicalArgumentRole.WitnessTable), refusal.Reason);

        // A witness table where the callee takes metadata is the same misbinding the other way round.
        Refused(Planner().PlanForward(facts, Named("void", ("x", "long"), ("TPPWT", "nint"), ("TMetadata", "nint")), "$s1M1fyySilF"),
            SwiftPlanRefusalKind.PrefixMismatch);

        // Passed in the callee's order, the same arguments plan.
        var plan = Planned(Planner().PlanForward(facts, Named("void", ("x", "long"), ("TMetadata", "nint"), ("TPPWT", "nint")), "$s1M1fyySilF"));
        Assert.DoesNotContain(plan.Body.Managed.Arguments, a => a.Unread);
    }

    [Fact]
    public void ThickClosure_TakesItsContextInSelf_AndIsCalledThroughTheFunctionRegister()
    {
        var plan = Planned(Planner().PlanForward(Closure("Swift.Int", "Swift.Int"), Signature(SwiftManagedMarshalling.None, "long", "long", "SwiftSelf"), null));

        Assert.Equal(SwiftPlanKind.ForwardThick, plan.Body.Kind);
        Assert.Null(plan.Body.Target);
        Assert.StartsWith("SBSR_thick_", plan.Symbol);
        Assert.Equal(new[] { SwiftTwinRole.Ordinary, SwiftTwinRole.Self, SwiftTwinRole.Function }, plan.Body.Managed.Arguments.Select(a => a.Role));
        Assert.Equal("Self:x1:8->x20:8 Function:x2:8->x16:8", Moves(On(plan, SwiftArchitecture.Arm64)));
        Assert.Equal("Self:rsi:8->r13:8 Function:rdx:8->r11:8", Moves(On(plan, SwiftArchitecture.X86_64)));
    }

    [Fact]
    public void Reverse_ReadsTheTargetAndPayloadFromTheContext()
    {
        var plan = Planned(Planner().PlanReverse(Closure("Swift.Int", "Swift.Int"), Signature(SwiftManagedMarshalling.None, "long", "long", "SwiftSelf")));

        Assert.Equal(SwiftPlanKind.Reverse, plan.Body.Kind);
        Assert.StartsWith("SBSRR_", plan.Symbol);
        Assert.Equal(new[] { SwiftTwinRole.Ordinary, SwiftTwinRole.Payload }, plan.Body.Managed.Arguments.Select(a => a.Role));
        var arm64 = On(plan, SwiftArchitecture.Arm64);
        Assert.Equal("x0:8 x20:8", Registers(arm64.Incoming));
        Assert.Equal("x0:8 x1:8", Registers(arm64.Outgoing));
        Assert.Equal(
            $"Payload:[context+{SwiftCallPlanner.ContextPayloadOffset}]:8->x1:8 Function:[context+{SwiftCallPlanner.ContextTargetOffset}]:8->x16:8",
            Moves(arm64));
        var x64 = On(plan, SwiftArchitecture.X86_64);
        Assert.Equal("rdi:8 r13:8", Registers(x64.Incoming));
        Assert.Equal("rax:8", x64.Result.Register!.ToString());
    }

    [Fact]
    public void ThrowingReverse_PassesAFrameErrorSlotAndReceivesTheErrorRegister()
    {
        var plan = Planned(Planner().PlanReverse(Throwing(Closure(null, "Swift.Int")),
            Signature(SwiftManagedMarshalling.None, "void", "long", "SwiftSelf", "SwiftError*")));

        Assert.Equal(new[] { SwiftTwinRole.Ordinary, SwiftTwinRole.Error, SwiftTwinRole.Payload }, plan.Body.Managed.Arguments.Select(a => a.Role));
        var arm64 = On(plan, SwiftArchitecture.Arm64);
        Assert.Equal("x0:8 x20:8 x21:8", Registers(arm64.Incoming));
        Assert.Equal("[local+0]:8", Assert.Single(arm64.Frame.Locals).To.ToString());
        Assert.StartsWith("Error:[local+0]:8->x1:8 Payload:", Moves(arm64));
        Assert.Equal(16, arm64.Frame.LocalBytes);
        // The slot starts zeroed, and what the callback left in it is the error Swift receives.
        Assert.True(arm64.ZeroError);
        Assert.Equal("Error:[local+0]:8->x21:8", string.Join(" ", arm64.AfterCall.Select(m => $"{m.Role}:{m.From}->{m.To}")));
        var x64 = On(plan, SwiftArchitecture.X86_64);
        Assert.True(x64.ZeroError);
        Assert.Equal("Error:[local+0]:8->r12:8", string.Join(" ", x64.AfterCall.Select(m => $"{m.Role}:{m.From}->{m.To}")));
    }

    [Fact]
    public void ReverseWithoutTheContext_IsRefused()
    {
        var outcome = Planner().PlanReverse(Closure(null, "Swift.Int"), Signature(SwiftManagedMarshalling.None, "void", "long"));

        Assert.Contains("the callee takes a self the managed signature does not pass", Refused(outcome, SwiftPlanRefusalKind.SpecialRegisterMismatch).Reason);
    }

    // ---- planner: refusals ----

    [Fact]
    public void NoFacts_IsRefused()
    {
        Assert.Equal("the transition has no facts",
            Refused(Planner().PlanForward(null, Signature("void"), "$s1M1f"), SwiftPlanRefusalKind.Facts).Reason);
    }

    [Fact]
    public void IncompleteFacts_AreRefused_WithTheUnknowns()
    {
        var facts = Free(null) with { Unknowns = ImmutableArray.Create("parameter 0: type 'X' is not known") };

        var refusal = Refused(Planner().PlanForward(facts, Signature("void"), "$s1M1f"), SwiftPlanRefusalKind.Facts);

        Assert.Contains("type 'X' is not known", refusal.Reason);
    }

    [Theory]
    [InlineData("async")]
    [InlineData("coroutine")]
    public void NonSynchronousCall_IsRefused(string kind)
    {
        var facts = Free(null) with
        {
            Effects = kind == "async" ? new SwiftCallEffects { IsAsync = true } : new SwiftCallEffects { IsCoroutine = true },
        };

        Assert.False(string.IsNullOrEmpty(Refused(Planner().PlanForward(facts, Signature("void"), "$s1M1f"), SwiftPlanRefusalKind.NotSynchronous).Reason));
    }

    [Fact]
    public void ClosurePlan_NeedsAClosuresFacts()
    {
        var refusal = Refused(Planner().PlanReverse(Free(null), Signature(SwiftManagedMarshalling.None, "void", "SwiftSelf")), SwiftPlanRefusalKind.Facts);

        Assert.Contains("needs a closure's facts", refusal.Reason);
    }

    [Theory]
    [InlineData("System.Runtime.Intrinsics.Vector128<float>", SwiftPlanRefusalKind.Vector, "vector carrier")]
    [InlineData("System.Numerics.Vector<float>", SwiftPlanRefusalKind.Vector, "vector carrier")]
    [InlineData("System.Numerics.Vector3", SwiftPlanRefusalKind.Vector, "vector carrier")]
    [InlineData("Int128", SwiftPlanRefusalKind.UnsupportedScalar, "Int128")]
    [InlineData("Half", SwiftPlanRefusalKind.UnsupportedScalar, "Float16")]
    [InlineData("decimal", SwiftPlanRefusalKind.UnsupportedScalar, "decimal")]
    [InlineData("string", SwiftPlanRefusalKind.ManagedReference, "managed reference")]
    [InlineData("Acme.Widget", SwiftPlanRefusalKind.ManagedReference, "managed reference")]
    [InlineData("int[]", SwiftPlanRefusalKind.ManagedReference, "array")]
    [InlineData("Acme.Explicit", SwiftPlanRefusalKind.ExplicitLayout, "names its own offset")]
    [InlineData("Acme.Offsets", SwiftPlanRefusalKind.ExplicitLayout, "names its own offset")]
    [InlineData("Acme.Automatic", SwiftPlanRefusalKind.ExplicitLayout, "has Auto layout")]
    [InlineData("Acme.Sized", SwiftPlanRefusalKind.ExplicitLayout, "sets an explicit size")]
    [InlineData("Acme.Fixed", SwiftPlanRefusalKind.NonBlittable, "fixed buffer")]
    [InlineData("Acme.Flagged", SwiftPlanRefusalKind.NonBlittable, "bool field")]
    [InlineData("int?", SwiftPlanRefusalKind.NonBlittable, "Nullable")]
    [InlineData("Acme.Box<int>", SwiftPlanRefusalKind.UnresolvedGeneric, "generic")]
    [InlineData("Acme.Mystery", SwiftPlanRefusalKind.UnknownAggregate, "neither in the carrier table nor")]
    public void ManagedCarrier_IsRefused_WithItsReason(string type, SwiftPlanRefusalKind kind, string reason)
    {
        const string source = """
            using System.Runtime.InteropServices;
            namespace Acme
            {
                public class Widget { }
                [StructLayout(LayoutKind.Explicit)] public struct Explicit { [FieldOffset(0)] public long A; }
                public struct Offsets { [FieldOffset(0)] public long A; }
                [StructLayout(LayoutKind.Auto)] public struct Automatic { public long A; }
                [StructLayout(LayoutKind.Sequential, Size = 16)] public struct Sized { public long A; }
                public unsafe struct Fixed { public fixed byte Bytes[8]; }
                public struct Flagged { public long A; public bool B; }
                public struct Box<T> { public T Value; }
            }
            """;

        var refusal = Refused(Planner(source).PlanForward(Free(null, "Swift.Int"), Signature("void", type), "$s1M1f"), kind);

        Assert.Contains(reason, refusal.Reason);
    }

    [Fact]
    public void StructWithABoolField_CrossesAsIsOnlyWithNoMarshaller()
    {
        const string source = "namespace Acme { public struct Flagged { public long A; public bool B; } }";
        var lowering = With(("M.Flagged", SwiftLoweringAnswer.DirectLayout(
            ImmutableArray.Create(new SwiftLoweredPiece(0, 8, SwiftScalarKind.Int64), new SwiftLoweredPiece(8, 1, SwiftScalarKind.Int8)), "a word and a byte")));
        var planner = Planner(source, lowering);

        Refused(planner.PlanForward(Free(null, "M.Flagged"), Signature(SwiftManagedMarshalling.SourceGenerated, "void", "Acme.Flagged"), "$s1M1f"), SwiftPlanRefusalKind.NonBlittable);
        Refused(planner.PlanForward(Free(null, "M.Flagged"), Signature(SwiftManagedMarshalling.Runtime, "void", "Acme.Flagged"), "$s1M1f"), SwiftPlanRefusalKind.NonBlittable);
        var plan = Planned(planner.PlanForward(Closure(null, "M.Flagged"), Signature(SwiftManagedMarshalling.None, "void", "Acme.Flagged", "SwiftSelf"), null));
        Assert.Equal(new[] { 8, 1 }, plan.Body.Managed.Arguments.Where(a => a.Role == SwiftTwinRole.Ordinary).Select(a => a.Size));
    }

    [Fact]
    public void BoolWithoutMarshalAs_IsRefusedUnderAMarshaller()
    {
        var refusal = Refused(Planner().PlanForward(Free(null, "Swift.Bool"), Signature("void", "bool"), "$s1M1f"), SwiftPlanRefusalKind.NonBlittable);

        Assert.Contains("four-byte BOOL", refusal.Reason);
    }

    [Theory]
    [InlineData(new[] { "Swift.Int", "Swift.Int" }, new[] { "long" }, "passes 1 ordinary argument(s); the callee takes 2")]
    [InlineData(new[] { "Swift.Double" }, new[] { "long" }, "argument 0: the managed long gives General 8")]
    [InlineData(new[] { "Swift.Int32" }, new[] { "long" }, "argument 0")]
    public void ArgumentsTheCalleeDoesNotReadInPlace_AreRefused(string[] swift, string[] managed, string reason)
    {
        var refusal = Refused(Planner().PlanForward(Free(null, swift), Signature("void", managed), "$s1M1f"), SwiftPlanRefusalKind.PrefixMismatch);

        Assert.Contains(reason, refusal.Reason);
    }

    [Theory]
    // Swift widens an Int32 and a tag byte to one eight-byte integer, past the five-byte carrier's
    // end; whether the Swift value ends at five bytes too is not in the facts (an Int64 does not).
    [InlineData("M.Five")]
    [InlineData("Swift.Int64")]
    public void PieceRunningPastTheManagedValuesEnd_IsRefused(string swift)
    {
        const string five = "Swift.Runtime.Marshalling.BlittableOptionalInt32";
        var lowering = With(("M.Five", SwiftLoweringAnswer.DirectLayout([new SwiftLoweredPiece(0, 8, SwiftScalarKind.Int64)], "an Int32 and a tag byte")));
        var planner = Planner(lowering: lowering);

        var argument = Refused(planner.PlanForward(Free(null, swift), Signature("void", five), "$s1M1f"), SwiftPlanRefusalKind.PrefixMismatch);
        Assert.Contains("is 5 bytes, and its piece at offset 0 spans 8, past its end", argument.Reason);
        var result = Refused(planner.PlanForward(Free(swift), Signature(five), "$s1M1f"), SwiftPlanRefusalKind.ResultMismatch);
        Assert.Contains("is 5 bytes, and its piece at offset 0 spans 8, past its end", result.Reason);
    }

    [Fact]
    public void AggregatePieceAtTheWrongOffset_IsRefused()
    {
        // The managed struct puts its double first; Swift's pair puts it second.
        const string source = "namespace Acme { public struct Swapped { public double D; public long L; } }";
        var lowering = With(("M.Pair", SwiftLoweringAnswer.DirectLayout(
            ImmutableArray.Create(new SwiftLoweredPiece(0, 8, SwiftScalarKind.Int64), new SwiftLoweredPiece(8, 8, SwiftScalarKind.Double)), "a word and a double")));

        var refusal = Refused(Planner(source, lowering).PlanForward(Free(null, "M.Pair"), Signature("void", "Acme.Swapped"), "$s1M1f"), SwiftPlanRefusalKind.PrefixMismatch);

        Assert.Contains("argument 0", refusal.Reason);
    }

    [Fact]
    public void ResultReturnedIndirectly_ButDeclaredDirect_IsRefused()
    {
        var lowering = With(("M.Wide", SwiftLoweringAnswer.IndirectBecause("five pieces")));

        var refusal = Refused(Planner(lowering: lowering).PlanForward(Free("M.Wide"), Signature("nint"), "$s1M1f"), SwiftPlanRefusalKind.ResultMismatch);

        Assert.Contains("the callee returns indirectly; the managed signature returns nint", refusal.Reason);
    }

    [Theory]
    [InlineData("Swift.Int", "double", "is not the callee's")]
    [InlineData("Swift.Int", "void", "returns nothing")]
    [InlineData("Swift.String", "long", "returns one long")]
    public void ResultOfAnotherShape_IsRefused(string swift, string managed, string reason)
    {
        var refusal = Refused(Planner().PlanForward(Free(swift), Signature(managed), "$s1M1f"), SwiftPlanRefusalKind.ResultMismatch);

        Assert.Contains(reason, refusal.Reason);
    }

    [Theory]
    [InlineData("class", "long", "the callee takes a self the managed signature does not pass")]
    [InlineData("free", "long,SwiftSelf", "passes a self the callee does not take")]
    [InlineData("throws", "long", "the callee throws but the managed signature has no error")]
    [InlineData("free", "long,SwiftError*", "takes an error but the callee does not throw")]
    [InlineData("free", "long,SwiftSelf,SwiftSelf", "two selves")]
    public void SpecialRegisterDisagreement_IsRefused(string callee, string managed, string reason)
    {
        var facts = callee switch
        {
            "class" => ClassMethod(null, "Swift.Int"),
            "throws" => Throwing(Free(null, "Swift.Int")),
            _ => Free(null, "Swift.Int"),
        };

        var refusal = Refused(Planner().PlanForward(facts, Signature("void", managed.Split(',')), "$s1M1f"), SwiftPlanRefusalKind.SpecialRegisterMismatch);

        Assert.Contains(reason, refusal.Reason);
    }

    [Fact]
    public void ThickCallWithNoContext_IsRefused()
    {
        var refusal = Refused(Planner().PlanForward(Closure(null, "Swift.Int"), Signature(SwiftManagedMarshalling.None, "void", "long"), null),
            SwiftPlanRefusalKind.SpecialRegisterMismatch);

        Assert.Contains("passes no context", refusal.Reason);
    }

    [Fact]
    public void UnpredictableCallee_IsRefusedOnEveryArchitecture()
    {
        var outcome = Planner().PlanForward(Free(null, "M.Unknown"), Signature("void", "long"), "$s1M1f");

        var refusals = outcome.Refusals.Where(r => r.Kind == SwiftPlanRefusalKind.Lowering).ToList();
        Assert.Equal(new SwiftArchitecture?[] { SwiftArchitecture.Arm64, SwiftArchitecture.X86_64 }, refusals.Select(r => r.Architecture));
        Assert.All(refusals, r => Assert.Contains("M.Unknown", r.Reason));
        Assert.Null(outcome.Plan);
    }

    [Fact]
    public void CalleeArgumentWiderThanOneRegister_IsRefused()
    {
        var lowering = With(("M.Wide128", SwiftLoweringAnswer.DirectLayout(
            ImmutableArray.Create(new SwiftLoweredPiece(0, 16, SwiftScalarKind.Opaque)), "one 128-bit integer")));

        var outcome = Planner(lowering: lowering).PlanForward(Free(null, "M.Wide128"), Signature("void", "long", "long"), "$s1M1f");

        Assert.Contains("i128 argument, which is not one scalar register", Refused(outcome, SwiftPlanRefusalKind.UnsupportedScalar).Reason);
    }

    [Fact]
    public void Arm64e_IsRefused_AndRefusesEveryArchitecture()
    {
        var models = SwiftArchitectureModel.Default(Layout).Add(new SwiftArchitectureModel(SwiftArchitecture.Arm64e, Layout, SwiftAggregateLowering.Lower));
        var planner = new SwiftCallPlanner(SwiftManagedTypeIndex.Empty, models);

        var outcome = planner.PlanForward(Free(null, "Swift.Int"), Signature("void", "long"), "$s1M1f");

        var refusal = Refused(outcome, SwiftPlanRefusalKind.Arm64e);
        Assert.Equal(SwiftArchitecture.Arm64e, refusal.Architecture);
        Assert.Contains("pointer-authenticated", refusal.Reason);
    }

    [Fact]
    public void ManagedViewThatDiffersAcrossArchitectures_IsRefused()
    {
        // Seeded: on x86_64 L splits the two Int32 fields into two pieces (and Swift agrees there),
        // while arm64 merges them into one word. Each architecture is self-consistent; the IL is not.
        const string source = "namespace Acme { public struct TwoInts { public int A; public int B; } }";
        var pair = ImmutableArray.Create(new SwiftLoweredPiece(0, 4, SwiftScalarKind.Int32), new SwiftLoweredPiece(4, 4, SwiftScalarKind.Int32));
        var arm64 = With(("M.TwoInts", SwiftLoweringAnswer.DirectLayout(ImmutableArray.Create(new SwiftLoweredPiece(0, 8, SwiftScalarKind.Int64)), "one word")));
        var x64 = With(("M.TwoInts", SwiftLoweringAnswer.DirectLayout(pair, "two halves")));
        var planner = new SwiftCallPlanner(Index(source),
        [
            new SwiftArchitectureModel(SwiftArchitecture.Arm64, arm64, SwiftAggregateLowering.Lower),
            new SwiftArchitectureModel(SwiftArchitecture.X86_64, x64, _ => pair),
        ]);

        var outcome = planner.PlanForward(Free(null, "M.TwoInts"), Signature("void", "Acme.TwoInts"), "$s1M1f");

        var refusal = Refused(outcome, SwiftPlanRefusalKind.ManagedViewDisagreement);
        Assert.Contains("X86_64", refusal.Reason);
        Assert.Contains("Arm64", refusal.Reason);

        // The same planner with the same L on both plans it.
        var agreeing = new SwiftCallPlanner(Index(source),
        [
            new SwiftArchitectureModel(SwiftArchitecture.Arm64, arm64, SwiftAggregateLowering.Lower),
            new SwiftArchitectureModel(SwiftArchitecture.X86_64, arm64, SwiftAggregateLowering.Lower),
        ]);
        Planned(agreeing.PlanForward(Free(null, "M.TwoInts"), Signature("void", "Acme.TwoInts"), "$s1M1f"));
    }

    // ---- the plan: hash, symbol, version ----

    [Fact]
    public void Plan_IsNamedByTheHashOfItsVersionedCanonicalBody()
    {
        var plan = Planned(Planner().PlanForward(Free(null, "Swift.String"), Signature("void", "Swift.SwiftString.Buffer"), "$s1M1fyySSF"));

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"v{SwiftCallPlan.Version}:{plan.Body.Canonical()}"))).ToLowerInvariant();
        Assert.Equal(expected, plan.Hash);
        Assert.Equal("SBSR_" + expected[..SwiftCallPlan.SymbolHashLength], plan.Symbol);
        Assert.Equal(SwiftCallPlan.Version, plan.PlanVersion);
    }

    [Fact]
    public void Plan_IsDeterministic_AndItsSemanticsStayOutOfTheHash()
    {
        var facts = Free(null, "Swift.Int");
        var a = Planned(Planner().PlanForward(facts, Signature("void", "long"), "$s1M1f"));
        var b = Planned(Planner().PlanForward(facts with { Parameters = [Param(0, "Swift.Int") with { Convention = SwiftParameterConvention.Owned }] },
            Signature("void", "long"), "$s1M1f"));
        var other = Planned(Planner().PlanForward(facts, Signature("void", "long"), "$s1M1g"));

        Assert.Equal(a.Body.Canonical(), b.Body.Canonical());
        Assert.Equal(a.Hash, b.Hash);
        Assert.NotEqual(a.Hash, other.Hash);
    }

    // ---- the plan set ----

    private const string Lib = "TestModuleSwiftBindings";

    private const string SetSource = """
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using Swift;
        using Swift.Runtime;
        using Buf = Swift.SwiftString.Buffer;
        namespace Acme
        {
            public static unsafe partial class Calls
            {
                [LibraryImport("TestModuleSwiftBindings", EntryPoint = "$s1M1fyySSF")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                internal static partial void Takes(SwiftString.Buffer s);

                [LibraryImport("TestModuleSwiftBindings", EntryPoint = "$s1M1fyySSF")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                internal static partial void TakesAgain(SwiftString.Buffer s);

                [LibraryImport("TestModuleSwiftBindings", EntryPoint = "$s1M1fyySSF")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                internal static partial void Aliased(Buf s);

                [LibraryImport("TestModuleSwiftBindings", EntryPoint = "$s1M1gyyF")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                internal static partial void Primitive(nint x);

                [LibraryImport("TestModuleSwiftBindings", EntryPoint = "$s1M1hyySdF")]
                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                internal static partial void Mismatched(SwiftString.Buffer s);

                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
                private static long Callback(long x, SwiftSelf context) => x;

                internal static void* Address() => (delegate* unmanaged[Swift]<long, SwiftSelf, long>)&Callback;
            }
        }
        """;

    private static SwiftTransitionRecord Record(string method, string entryPoint, SwiftCallFacts facts) => new()
    {
        MethodName = method,
        EntryPoint = entryPoint,
        Library = Lib,
        // Facts are cited by their symbol.
        Facts = facts with { Target = facts.Target with { Symbol = entryPoint } },
        Source = SwiftCallFactsSource.Declared,
    };

    private static SwiftTransitionLedger SetLedger() => SwiftTransitionLedger.Build(
        "TestModule",
        SetSource,
        [
            Record("Takes", "$s1M1fyySSF", Free(null, "Swift.String")),
            Record("TakesAgain", "$s1M1fyySSF", Free(null, "Swift.String")),
            Record("Aliased", "$s1M1fyySSF", Free(null, "Swift.String")),
            Record("Primitive", "$s1M1gyyF", Free(null, "Swift.Int")),
            Record("Mismatched", "$s1M1hyySdF", Free(null, "Swift.Double")),
        ],
        callbacks: [new SwiftCallbackRecord { CallbackName = "Callback", Facts = Closure("Swift.Int", "Swift.Int") }]);

    private static SwiftCallPlanSet BuildSet(SwiftTransitionLedger ledger) =>
        SwiftCallPlanSet.Build(ledger, new SwiftCallPlanner(ledger.Types, SwiftArchitectureModel.Default(Layout)));

    [Fact]
    public void PlanSet_CoversEveryTransition_PlannedRefusedOrNotBridged()
    {
        var ledger = SetLedger();
        var set = BuildSet(ledger);

        Assert.Equal(ledger.Entries.Length, set.Coverage.Length);
        var byMember = set.Coverage.ToLookup(e => e.Member.Split("::")[^1]);
        Assert.Equal(SwiftPlanStatus.NotBridged, byMember["Primitive"].Single().Status);
        Assert.Equal(SwiftCallPlanSet.PrimitiveOnlyReason, byMember["Primitive"].Single().Reason);
        var mismatched = byMember["Mismatched"].Single();
        Assert.Equal(SwiftPlanStatus.Refused, mismatched.Status);
        Assert.Equal(SwiftPlanRefusalKind.PrefixMismatch, Assert.Single(mismatched.Refusals.Select(r => r.Kind).Distinct()));
        Assert.All(set.Coverage.Where(e => e.Status == SwiftPlanStatus.Refused), e => Assert.NotEmpty(e.Refusals));
        Assert.All(set.Coverage.Where(e => e.Status == SwiftPlanStatus.NotBridged), e => Assert.False(string.IsNullOrEmpty(e.Reason)));
    }

    [Fact]
    public void PlanSet_DedupesIdenticalPlans_ByHash()
    {
        var set = BuildSet(SetLedger());

        // The buffer spelled through a namespace import and through an alias is the same carrier.
        var takes = set.Coverage.Where(e => e.EntryPoint == "$s1M1fyySSF").ToList();
        Assert.Equal(3, takes.Count);
        Assert.All(takes, e => Assert.Equal(SwiftPlanStatus.Planned, e.Status));
        Assert.Single(takes.Select(e => e.Symbol).Distinct());
        Assert.Single(set.Plans, p => p.Symbol == takes[0].Symbol);
        Assert.Equal(set.Plans.Length, set.Plans.Select(p => p.Symbol).Distinct().Count());
    }

    [Fact]
    public void CallbackAddress_UsesItsCallbacksPlan()
    {
        var set = BuildSet(SetLedger());

        var callback = set.Coverage.Single(e => e.Transition == SwiftTransitionKind.ReverseCallback);
        var address = set.Coverage.Single(e => e.Transition == SwiftTransitionKind.CallbackAddress);
        Assert.Equal(SwiftPlanStatus.Planned, callback.Status);
        Assert.StartsWith("SBSRR_", callback.Symbol);
        Assert.Equal(callback.Member, address.Target);
        Assert.Equal(callback.Symbol, address.Symbol);
        Assert.Equal(callback.Status, address.Status);
    }

    [Fact]
    public void PlanSet_TwoRunsWriteTheSameBytes()
    {
        var first = BuildSet(SetLedger()).ToJson();
        var second = BuildSet(SetLedger()).ToJson();

        Assert.Equal(first, second);
        Assert.Contains($"\"planVersion\": {SwiftCallPlan.Version}", first);
    }

    [Fact]
    public void ProducersThatDisagreeOnAnImportsFacts_RefuseIt()
    {
        var ledger = SwiftTransitionLedger.Build("TestModule", SetSource,
        [
            Record("Takes", "$s1M1fyySSF", Free(null, "Swift.String")),
            Record("Takes", "$s1M1fyySSF", Free(null, "Swift.Double")),
        ]);

        var takes = BuildSet(ledger).Coverage.Single(e => e.Member.Split("::")[^1] == "Takes");
        Assert.Equal(SwiftPlanStatus.Refused, takes.Status);
        var refusal = Assert.Single(takes.Refusals);
        Assert.Equal(SwiftPlanRefusalKind.Facts, refusal.Kind);
        Assert.Contains("2 different facts", refusal.Reason);
    }

    [Fact]
    public void TwoDifferentPlansUnderOneBridgeName_RefuseEveryUser()
    {
        var ledger = SetLedger();
        var planner = new SwiftCallPlanner(ledger.Types, SwiftArchitectureModel.Default(Layout));
        var takes = ledger.Entries.First(e => e.Method() == "Takes");
        var again = ledger.Entries.First(e => e.Method() == "TakesAgain");
        var a = SwiftCallPlanSet.Plan(takes, Free(null, "Swift.String"), planner);
        var b = Planner().PlanForward(Free(null, "Swift.Int"), Signature("void", "long"), "$s1M1other");
        // Seeded: the second plan's body differs but it claims the first's name.
        var colliding = b with { Plan = b.Plan! with { Symbol = a.Plan!.Symbol } };

        var set = SwiftCallPlanSet.Assemble("TestModule", [(takes, a, null), (again, colliding, null)]);

        Assert.All(set.Coverage, e =>
        {
            Assert.Equal(SwiftPlanStatus.Refused, e.Status);
            Assert.Equal(SwiftPlanRefusalKind.HashCollision, Assert.Single(e.Refusals).Kind);
        });
        Assert.Empty(set.Plans);
    }

    // ---- hand-written tables ----

    internal static string RepoRoot { get; } = LocateRepoRoot();

    private static string LocateRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BindingTests", "RuntimeTestsApp", "RuntimeTestsApp.csproj")))
                return directory.FullName;
        }
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    internal static IEnumerable<string> SourcesUnder(params string[] relative)
    {
        var directory = Path.Combine(new[] { RepoRoot }.Concat(relative).ToArray());
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal) && !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .ToList();
    }

    internal static TypeDatabase StandardLibraryDatabase()
    {
        var db = new TypeDatabase();
        foreach (var name in new[] { "SwiftDatabase.xml", "FoundationDatabase.xml" })
            db.LoadModuleDatabaseFromFile(Path.Combine(AppContext.BaseDirectory, "Swift", name)).Wait();
        return db;
    }

    internal static SwiftCallPlanSet PlanHandWritten(string table, IEnumerable<string> sources, out SwiftHandWrittenTransitionTable loaded) =>
        SwiftHandWrittenPlans.Plan(Path.Combine(RepoRoot, table), sources, StandardLibraryDatabase(), out loaded);

    internal static string LoweringEvidencePath => Path.Combine(RepoRoot, "build", "baselines", "swift-lowering-evidence.json");

    internal static SwiftHandWrittenProducts ProduceHandWritten(string table, string sources, SwiftLoweringEvidence? qualification) =>
        SwiftHandWrittenPlans.Produce(Path.Combine(RepoRoot, table), [Path.Combine(RepoRoot, sources)], StandardLibraryDatabase(), qualification);

    private static void AssertCurrent(string path, string expected)
    {
        Assert.True(File.Exists(path), $"{path} is missing: run `nuke swift-bridge-products`");
        Assert.True(expected == File.ReadAllText(path), $"{path} is stale: run `nuke swift-bridge-products`");
    }

    [Theory]
    [InlineData("src/Swift.Runtime/src/transition-facts.json", "src/Swift.Runtime/src")]
    [InlineData("src/Swift.Bindings.Apple/transition-facts.json", "src/Swift.Bindings.Apple/Sources")]
    public void HandWrittenPlanProducts_AreCurrent(string table, string sources)
    {
        // The committed products are what the bridge gate reads, what the runtime compiles (its shims)
        // and what build-runtime.sh links (its bridges); `nuke swift-bridge-products` rewrites them all.
        var products = ProduceHandWritten(table, sources, SwiftLoweringEvidence.Load(LoweringEvidencePath));
        var product = Path.Combine(RepoRoot, "build", "baselines", "swift-call-plans", SwiftCallPlanSet.FileName(products.Table.Assembly));

        AssertCurrent(product, products.Plans.ToJson());
        Assert.Equal(products.Plans.Plans.Select(p => p.Symbol), SwiftCallPlanSet.Read(product).Plans.Select(p => p.Symbol));
        if (products.Table.Bridges is { } bridges)
        {
            var directory = Path.GetDirectoryName(Path.Combine(RepoRoot, table))!;
            AssertCurrent(Path.Combine(directory, bridges.Managed), Assert.IsType<string>(products.Managed));
            Assert.Equal(SwiftBridgeRouting.Architectures.Order(), products.Native.Keys.Order());
            foreach (var (architecture, source) in products.Native)
                AssertCurrent(Path.Combine(directory, bridges.NativeFile(architecture)), source);
        }
    }

    /// <summary>
    /// The runtime routes all four of its Swift-convention imports through SBSR_ bridges in its own
    /// framework: the compiled shims hold no Swift-convention import, each twin imports its bridge
    /// from SwiftBindingsRuntime under the C convention, and each architecture's bridge assembly
    /// defines every routed bridge.
    /// </summary>
    [Fact]
    public void RuntimeTable_RoutesEveryTransitionThroughItsBridge()
    {
        var products = ProduceHandWritten("src/Swift.Runtime/src/transition-facts.json", "src/Swift.Runtime/src",
            SwiftLoweringEvidence.Load(LoweringEvidencePath));

        Assert.Equal(4, products.Routing.Routes.Length);
        Assert.All(products.Routing.Routes, r => Assert.True(r.IsBridged, $"{r.Member}: {r.Refusal}"));
        var planned = products.Plans.Coverage.Where(e => e.Status == SwiftPlanStatus.Planned).ToList();
        Assert.Equal(4, planned.Count);
        Assert.All(planned, e => Assert.True(e.Bridged == true, $"{e.Member} {e.EntryPoint}: {e.BridgeRefusal ?? e.Evidence}"));
        // Qualified on every slice the runtime's native code is built for, under every qualified compiler family.
        var scope = Assert.IsType<SwiftPlanEvidenceScope>(products.Plans.EvidenceScope);
        Assert.Equal(products.Table.Scope, scope.Scope);
        Assert.Equal(SwiftLoweringJudgement.QualificationBasis, scope.Basis);
        Assert.All(planned, e => Assert.EndsWith(
            $"on {products.Table.Slices.Length} triple(s) under {SwiftLoweringQualification.Families.Length} toolchain(s)", e.Evidence));
        var managed = Assert.IsType<string>(products.Managed);
        Assert.DoesNotContain("CallConvSwift", managed);
        Assert.DoesNotContain("extern ", managed);
        foreach (var symbol in planned.Select(e => e.Symbol!))
        {
            Assert.Contains($"LibraryImport(\"SwiftBindingsRuntime\", EntryPoint = \"{symbol}\")", managed);
            foreach (var (architecture, source) in products.Native)
                Assert.True(source.Contains($".globl _{symbol}", StringComparison.Ordinal), $"{architecture} does not define {symbol}");
        }
        // The managed output is no input: the next generation reads only the declarations.
        Assert.DoesNotContain(products.Plans.Coverage, e => e.Member.Contains("__SBSR_", StringComparison.Ordinal));
    }

    /// <summary>
    /// Fail-closed: with a qualification record that holds no receipt of the runtime, every runtime
    /// import is compiled exactly as declared (a Swift-convention DllImport), no bridge is emitted,
    /// and the sidecar says why.
    /// </summary>
    [Fact]
    public void RuntimeTable_WithoutProof_KeepsEveryDirectImport()
    {
        var products = ProduceHandWritten("src/Swift.Runtime/src/transition-facts.json", "src/Swift.Runtime/src", SwiftLoweringEvidence.Empty);

        Assert.False(products.Routing.BridgesAny);
        Assert.All(products.Plans.Coverage, e => Assert.False(e.Executable));
        Assert.All(products.Plans.Coverage.Where(e => e.Status == SwiftPlanStatus.Planned),
            e => Assert.Contains("the qualification record has no receipt of Swift.Runtime", e.Evidence));
        var managed = Assert.IsType<string>(products.Managed);
        Assert.Equal(4, managed.Split("CallConvSwift").Length - 1);
        Assert.Equal(4, managed.Split("public static extern void").Length - 1);
        Assert.DoesNotContain("SBSR_", managed);
        Assert.All(products.Native.Values, source => Assert.DoesNotContain(".globl", source));
    }

    [Fact]
    public void RoutingTable_WithoutEvidence_IsRefused_RatherThanWrittenUnrouted()
    {
        var output = Path.Combine(Path.GetTempPath(), $"hand-written-{Guid.NewGuid():N}");
        try
        {
            var code = SwiftHandWrittenPlans.Run(Path.Combine(RepoRoot, "src", "Swift.Runtime", "src", "transition-facts.json"),
                [Path.Combine(RepoRoot, "src", "Swift.Runtime", "src")], output, qualificationPath: null,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            Assert.Equal(1, code);
            Assert.False(Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any());
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void RoutingTable_WithARecordThatDoesNotQualifyIt_IsRefused_RatherThanWrittenUnrouted()
    {
        var output = Path.Combine(Path.GetTempPath(), $"hand-written-{Guid.NewGuid():N}");
        var record = Path.Combine(Path.GetTempPath(), $"record-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(record, SwiftLoweringEvidence.Empty.ToJson());
            var code = SwiftHandWrittenPlans.Run(Path.Combine(RepoRoot, "src", "Swift.Runtime", "src", "transition-facts.json"),
                [Path.Combine(RepoRoot, "src", "Swift.Runtime", "src")], output, record,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            Assert.Equal(1, code);
            Assert.False(Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any());
        }
        finally
        {
            File.Delete(record);
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    private static void AssertEveryTransitionPlanned(SwiftCallPlanSet set, SwiftHandWrittenTransitionTable table, int expected)
    {
        var refused = set.Coverage.Where(e => e.Status == SwiftPlanStatus.Refused)
            .Select(e => $"{e.Member} {e.EntryPoint}: {string.Join("; ", e.Refusals)}")
            .ToList();
        Assert.True(refused.Count == 0, "refused:\n" + string.Join("\n", refused));
        Assert.Equal(expected, set.Count(SwiftPlanStatus.Planned));
        Assert.Equal(expected, table.Transitions.Length);
        // Each table entry is a declaration the sources make, and planned.
        foreach (var transition in table.Transitions)
            Assert.Contains(set.Coverage, e => e.EntryPoint == transition.EntryPoint && e.Status == SwiftPlanStatus.Planned);
    }

    [Fact]
    public void RuntimeTable_PlansEveryHandWrittenNonPrimitiveTransition()
    {
        var set = PlanHandWritten(Path.Combine("src", "Swift.Runtime", "src", "transition-facts.json"),
            SourcesUnder("src", "Swift.Runtime", "src"), out var table);

        AssertEveryTransitionPlanned(set, table, expected: 4);
        Assert.All(set.Plans, p => Assert.Equal(SwiftResultKind.Indirect, p.Body.Managed.Result.Kind));
    }

    [Fact]
    public void AppleTable_PlansEveryHandWrittenNonPrimitiveTransition()
    {
        var set = PlanHandWritten(Path.Combine("src", "Swift.Bindings.Apple", "transition-facts.json"),
            SourcesUnder("src", "Swift.Bindings.Apple", "Sources"), out var table);

        // Data's count and copyBytes take the value as its two words, which every runtime places
        // identically; only the initializer, which returns Data, still needs a plan.
        Assert.Empty(set.Coverage.Where(e => e.Status == SwiftPlanStatus.Refused));
        Assert.Equal(3, table.Transitions.Length);
        Assert.Equal(1, set.Count(SwiftPlanStatus.Planned));
        foreach (var transition in table.Transitions)
            Assert.Contains(set.Coverage, e => e.EntryPoint == transition.EntryPoint
                && e.Status == (transition.EntryPoint == "$s10Foundation4DataV5bytes5countACSV_SitcfC" ? SwiftPlanStatus.Planned : SwiftPlanStatus.NotBridged));
        // Data travels as its two words, in and out.
        var init = set.Plans.Single(p => p.Body.Target == "$s10Foundation4DataV5bytes5countACSV_SitcfC");
        Assert.Equal(SwiftResultKind.Direct, init.Body.Managed.Result.Kind);
        Assert.Equal(2, init.Body.Managed.Result.Pieces.Length);
    }

    /// <summary>
    /// The Apple table routes nothing through a bridge, so the record does not judge its plans even
    /// though it holds the supplement's receipts: a plan judged executable that nothing routes would be
    /// a transition left on its direct import.
    /// </summary>
    [Fact]
    public void AppleTable_RoutesNothing_SoItsPlansAreNotJudged()
    {
        var record = SwiftLoweringEvidence.Load(LoweringEvidencePath);
        Assert.Contains(record.Receipts, r => r.Artifact == "SwiftBindings.Apple");

        var products = ProduceHandWritten("src/Swift.Bindings.Apple/transition-facts.json", "src/Swift.Bindings.Apple/Sources", record);

        Assert.Null(products.Table.Bridges);
        Assert.Null(products.Plans.EvidenceScope);
        Assert.False(products.Routing.BridgesAny);
        Assert.All(products.Plans.Coverage, e => Assert.False(e.Executable));
    }

    [Fact]
    public void CarrierTable_MatchesTheStructsTheSourcesDeclare()
    {
        var index = new SwiftManagedTypeIndex();
        foreach (var source in SourcesUnder("src", "Swift.Runtime", "src").Concat(SourcesUnder("src", "Swift.Bindings.Apple", "Sources")))
            index.Add(CSharpSyntaxTree.ParseText(source).GetRoot());
        var classifier = new SwiftManagedCarrierClassifier(index);

        var problems = new List<string>();
        foreach (var (name, (layout, size)) in SwiftManagedCarrierTable.Entries)
        {
            var tick = name.IndexOf('`');
            if (!index.TryGetStruct(tick < 0 ? name : name[..tick], out var declared))
            {
                problems.Add($"{name}: no such struct is declared");
                continue;
            }
            if (tick >= 0)
            {
                // A generic carrier's argument only appears behind a pointer: one field per word.
                if (declared.Fields.Length != layout.Leaves.Length)
                    problems.Add($"{name}: {declared.Fields.Length} field(s), the table has {layout.Leaves.Length}");
                continue;
            }
            var value = classifier.LayoutOfDeclaration(declared);
            if (value.Kind != SwiftManagedValueKind.Aggregate)
            {
                problems.Add($"{name}: {value.Refusal}");
                continue;
            }
            if (value.ManagedSize != size || value.Layout!.Size != layout.Size || value.Layout.Alignment != layout.Alignment
                || !value.Layout.Leaves.SequenceEqual(layout.Leaves))
                problems.Add($"{name}: declared {Describe(value.Layout!, value.ManagedSize)}, the table has {Describe(layout, size)}");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));

        static string Describe(SwiftLeafLayout l, int managedSize) =>
            $"size {l.Size}/{managedSize} align {l.Alignment} [{string.Join(", ", l.Leaves.Select(x => $"{x.Kind}@{x.Offset}"))}]";
    }
}

internal static class SwiftTransitionLedgerEntryTestExtensions
{
    /// <summary>The method name of an entry's member (<c>Type::Method</c>).</summary>
    public static string Method(this SwiftTransitionLedgerEntry entry) => entry.Member.Split("::")[^1];
}
