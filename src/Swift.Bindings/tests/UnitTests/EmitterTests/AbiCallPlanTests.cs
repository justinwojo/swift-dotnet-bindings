// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Foundation tests for <see cref="AbiCallPlan"/> — the typed descriptor a retained native call records
/// as a side effect of rendering its P/Invoke declaration
/// (<see cref="PInvokeEmitHelper.FormatDeclarationLines"/>).
/// </summary>
/// <remarks>
/// This wave only proves the plans are <em>populated</em> and <em>stable</em>, and that a plan and the
/// text it describes agree by construction; a later session turns them into the validator. So these tests
/// assert three things and nothing about validation: (1) a plan is captured, from the resolved facts, when
/// an emission context is threaded; (2) re-emitting the same calls yields byte-identical plans
/// (double-emit determinism), including the pure builder being a function of its input alone; and
/// (3) each plan field appears verbatim in the declaration it was recorded alongside.
/// </remarks>
public class AbiCallPlanTests
{
    private static PInvokeEmissionInfo MakeInfo(
        string methodName = "DoWork",
        string entryPoint = "SBW_Mod_Type_doWork_abc",
        string returnType = "void",
        string parametersString = "",
        PInvokeCallingConvention cc = PInvokeCallingConvention.Cdecl,
        bool isAsync = false,
        IReadOnlyList<string>? metadata = null,
        ModuleEmissionContext? ctx = null) =>
        new()
        {
            LibraryPath = "libTest.dylib",
            EntryPoint = entryPoint,
            MethodName = methodName,
            ReturnType = returnType,
            ParametersString = parametersString,
            CallingConvention = cc,
            IsAsync = isAsync,
            MetadataParameters = metadata,
            EmissionContext = ctx,
        };

    // ── population ────────────────────────────────────────────────────────────

    [Fact]
    public void FormatDeclarationLines_WithContext_RecordsOnePlanFromResolvedFacts()
    {
        var ctx = new ModuleEmissionContext();
        var info = MakeInfo(
            methodName: "Fetch",
            entryPoint: "SBW_Mod_Store_fetch_abc",
            returnType: "global::System.IntPtr",
            parametersString: "global::System.IntPtr self_",
            ctx: ctx);

        PInvokeEmitHelper.FormatDeclarationLines(info);

        var plan = Assert.Single(ctx.AbiCallPlans);
        Assert.Equal("Fetch", plan.MethodName);
        Assert.Equal("SBW_Mod_Store_fetch_abc", plan.EntryPoint);
        Assert.Equal("libTest.dylib", plan.Library);
        Assert.Equal(PInvokeCallingConvention.Cdecl, plan.CallingConvention);
        Assert.Equal("global::System.IntPtr", plan.ReturnCarrier);
        Assert.Equal(new[] { "global::System.IntPtr" }, plan.ParameterCarriers);
        Assert.False(plan.IsAsync);
    }

    [Fact]
    public void FormatDeclarationLines_WithoutContext_RecordsNothing_AndTextIsIdentical()
    {
        var info = MakeInfo(parametersString: "global::System.IntPtr self_");
        var ctx = new ModuleEmissionContext();

        var withoutCtx = PInvokeEmitHelper.FormatDeclarationLines(info);
        var withCtx = PInvokeEmitHelper.FormatDeclarationLines(info with { EmissionContext = ctx });

        // Recording a plan is a pure side-table write: the rendered lines are unchanged whether or not a
        // context is threaded.
        Assert.Equal(withoutCtx, withCtx);
        // And with no context there is nowhere to record — the plan set is only ever populated via a context.
        Assert.Single(ctx.AbiCallPlans);
    }

    // ── determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void DoubleEmit_IntoFreshContexts_YieldsIdenticalPlans()
    {
        var infos = new[]
        {
            MakeInfo("A", "SBW_Mod_T_a_1", "void", "global::System.IntPtr self_"),
            MakeInfo("B", "SBW_Mod_T_b_2", "global::System.IntPtr", "int x, int y"),
            MakeInfo("C", "$s3Mod1cyyF", "void", "", PInvokeCallingConvention.Swift),
        };

        var first = new ModuleEmissionContext();
        var second = new ModuleEmissionContext();
        // Emit in different orders into the two contexts — the ordered snapshot must still match.
        foreach (var info in infos)
            PInvokeEmitHelper.FormatDeclarationLines(info with { EmissionContext = first });
        foreach (var info in infos.Reverse())
            PInvokeEmitHelper.FormatDeclarationLines(info with { EmissionContext = second });

        Assert.Equal(first.AbiCallPlans, second.AbiCallPlans);
        Assert.Equal(3, first.AbiCallPlans.Count);
    }

    [Fact]
    public void PlansDifferingOnlyByLibrary_AreTotallyOrdered_RegardlessOfRecordingOrder()
    {
        // Two calls that share method name, entry point, convention, return, and carriers but bind
        // different libraries are distinct plan values (library is part of equality). The ordered snapshot
        // must place them in a stable, content-derived order — library included — so a downstream
        // owner-preference dedup keyed on (RuleId, MethodName, EntryPoint) can never attribute to a
        // different owner merely because emission recorded the two in a different order.
        var a = MakeInfo(methodName: "Do", entryPoint: "SBW_dup", parametersString: "int x"); // libTest.dylib
        var b = a with { LibraryPath = "libOther.dylib" };

        var first = new ModuleEmissionContext();
        var second = new ModuleEmissionContext();
        PInvokeEmitHelper.FormatDeclarationLines(a with { EmissionContext = first });
        PInvokeEmitHelper.FormatDeclarationLines(b with { EmissionContext = first });
        // Reverse the recording order into the second context.
        PInvokeEmitHelper.FormatDeclarationLines(b with { EmissionContext = second });
        PInvokeEmitHelper.FormatDeclarationLines(a with { EmissionContext = second });

        Assert.Equal(first.AbiCallPlans, second.AbiCallPlans);
        Assert.Equal(2, first.AbiCallPlans.Count);
        // Library breaks the tie in ordinal order both times.
        Assert.Equal(
            new[] { "libOther.dylib", "libTest.dylib" },
            first.AbiCallPlans.Select(p => p.Library));
    }

    [Fact]
    public void ReEmittingSameCall_IntoOneContext_IsIdempotent()
    {
        var ctx = new ModuleEmissionContext();
        var info = MakeInfo(parametersString: "int a, int b", ctx: ctx);

        PInvokeEmitHelper.FormatDeclarationLines(info);
        PInvokeEmitHelper.FormatDeclarationLines(info);

        var plan = Assert.Single(ctx.AbiCallPlans);
        Assert.Equal(new[] { "int", "int" }, plan.ParameterCarriers);
    }

    [Fact]
    public void SameKey_DistinctCarriers_AreBothRetained_NotOverwritten()
    {
        // Two calls that share a method name + entry point but differ in carriers (the same symbol surfaced
        // under two containing C# types) are distinct plan values. The value-keyed side table keeps both,
        // rather than letting one silently overwrite the other on a shared Key.
        var ctx = new ModuleEmissionContext();
        var one = MakeInfo(methodName: "Do", entryPoint: "SBW_dup", parametersString: "int x", ctx: ctx);
        var two = MakeInfo(methodName: "Do", entryPoint: "SBW_dup", parametersString: "int x, int y", ctx: ctx);

        PInvokeEmitHelper.FormatDeclarationLines(one);
        PInvokeEmitHelper.FormatDeclarationLines(two);

        Assert.Equal(2, ctx.AbiCallPlans.Count);
        Assert.All(ctx.AbiCallPlans, p => Assert.Equal("Do SBW_dup", p.Key));
        Assert.Contains(ctx.AbiCallPlans, p => p.ParameterCarriers.Length == 1);
        Assert.Contains(ctx.AbiCallPlans, p => p.ParameterCarriers.Length == 2);
    }

    [Fact]
    public void BuildAbiCallPlan_IsPure_SameInputYieldsEqualPlan()
    {
        var info = MakeInfo(
            returnType: "global::System.IntPtr",
            parametersString: "global::System.IntPtr self_, int n",
            metadata: new[] { "global::Swift.Runtime.TypeMetadata TMeta" });

        Assert.Equal(PInvokeEmitHelper.BuildAbiCallPlan(info), PInvokeEmitHelper.BuildAbiCallPlan(info));
    }

    [Fact]
    public void EqualContentPlans_AreValueEqual_AndDedupInAHashSet()
    {
        // Two plans built independently have distinct backing arrays for their carriers. Value equality
        // must see through that — otherwise a future consumer's HashSet<AbiCallPlan> would keep both.
        var info = MakeInfo(
            returnType: "global::System.IntPtr",
            parametersString: "global::System.IntPtr self_, int n");
        var a = PInvokeEmitHelper.BuildAbiCallPlan(info);
        var b = PInvokeEmitHelper.BuildAbiCallPlan(info);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Single(new HashSet<AbiCallPlan> { a, b });
        // A carrier difference makes them distinct — equality is not degenerate.
        Assert.NotEqual(a, a with { ParameterCarriers = a.ParameterCarriers.Add("int") });
    }

    [Fact]
    public void RecordedPlan_EqualsPureBuilderOutput_ForSameInfo()
    {
        // Pins the render path's recorded plan to the standalone builder so the two cannot drift.
        var ctx = new ModuleEmissionContext();
        var info = MakeInfo(
            returnType: "global::System.IntPtr",
            parametersString: "global::System.IntPtr self_, [global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.U1)] bool flag",
            metadata: new[] { "global::Swift.Runtime.TypeMetadata TMeta" },
            ctx: ctx);

        PInvokeEmitHelper.FormatDeclarationLines(info);

        Assert.Equal(PInvokeEmitHelper.BuildAbiCallPlan(info), Assert.Single(ctx.AbiCallPlans));
    }

    // ── plan-vs-rendered-text agreement ─────────────────────────────────────────

    [Fact]
    public void Plan_AgreesWith_RenderedDeclarationText()
    {
        var ctx = new ModuleEmissionContext();
        var info = MakeInfo(
            methodName: "Combine",
            entryPoint: "SBW_Mod_T_combine_xyz",
            returnType: "global::System.IntPtr",
            parametersString: "global::System.IntPtr self_, int count",
            metadata: new[] { "global::Swift.Runtime.TypeMetadata TMeta" },
            ctx: ctx);

        var lines = PInvokeEmitHelper.FormatDeclarationLines(info);
        var plan = Assert.Single(ctx.AbiCallPlans);
        var text = string.Join("\n", lines);
        var signature = lines.Single(l => l.Contains($" {plan.MethodName}("));

        // Library + entry point appear verbatim in the LibraryImport attribute.
        Assert.Contains($"\"{plan.Library}\"", text);
        Assert.Contains($"EntryPoint = \"{plan.EntryPoint}\"", text);
        // Resolved calling convention matches the emitted CallConv attribute.
        Assert.Contains(
            plan.CallingConvention == PInvokeCallingConvention.Swift ? "CallConvSwift" : "CallConvCdecl",
            text);
        // Return carrier + method name front the signature line.
        Assert.Contains($"{plan.ReturnCarrier} {plan.MethodName}(", signature);
        // Every parameter carrier is a substring of the rendered signature.
        foreach (var carrier in plan.ParameterCarriers)
            Assert.Contains(carrier, signature);
    }

    [Fact]
    public void Plan_CapturesResolvedCallingConvention_NotRequested()
    {
        // A Swift mangled entry point requested as Cdecl is silently coerced to Swift CC by
        // SelectCallingConvention; the plan must record the RESOLVED convention that the text shows.
        var ctx = new ModuleEmissionContext();
        var info = MakeInfo(
            entryPoint: "$s3Mod6doWorkyyF",
            cc: PInvokeCallingConvention.Cdecl,
            ctx: ctx);

        var lines = PInvokeEmitHelper.FormatDeclarationLines(info);
        var plan = Assert.Single(ctx.AbiCallPlans);

        Assert.Equal(PInvokeCallingConvention.Swift, plan.CallingConvention);
        Assert.Contains(lines, l => l.Contains("CallConvSwift"));
    }

    [Fact]
    public void Plan_ForAsyncCall_HasVoidReturnCarrier_AndIsAsync()
    {
        var ctx = new ModuleEmissionContext();
        // Async P/Invokes always render a void return regardless of the declared ReturnType.
        var info = MakeInfo(returnType: "global::System.IntPtr", isAsync: true, ctx: ctx);

        var lines = PInvokeEmitHelper.FormatDeclarationLines(info);
        var plan = Assert.Single(ctx.AbiCallPlans);

        Assert.True(plan.IsAsync);
        Assert.Equal("void", plan.ReturnCarrier);
        Assert.Contains(lines, l => l.Contains($"void {plan.MethodName}("));
    }

    [Fact]
    public void ParameterCarriers_StripNames_KeepTypesAndMetadata_InOrder()
    {
        var info = MakeInfo(
            parametersString: "global::System.IntPtr self_, int count",
            metadata: new[] { "global::Swift.Runtime.TypeMetadata TMeta" });

        var plan = PInvokeEmitHelper.BuildAbiCallPlan(info);

        Assert.Equal(
            new[] { "global::System.IntPtr", "int", "global::Swift.Runtime.TypeMetadata" },
            plan.ParameterCarriers);
    }

    [Fact]
    public void ParameterCarriers_AreStable_UnderNameDeduplication()
    {
        // A duplicate parameter name is renamed by FormatDeclarationLines (…_1), but a carrier is the type
        // portion, so it is unaffected — the plan stays stable across the rename.
        var ctx = new ModuleEmissionContext();
        var info = MakeInfo(parametersString: "int self_, int self_", ctx: ctx);

        var lines = PInvokeEmitHelper.FormatDeclarationLines(info);
        var plan = Assert.Single(ctx.AbiCallPlans);
        var signature = lines.Single(l => l.Contains($" {plan.MethodName}("));

        Assert.Equal(new[] { "int", "int" }, plan.ParameterCarriers);
        // The rendered signature actually deduplicated the collision, confirming the carriers ignore names.
        Assert.Contains("self__1", signature);
    }
}
