// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using static BindingsGeneration.Tests.SwiftCallPlanTests;

namespace BindingsGeneration.Tests;

/// <summary>
/// <see cref="SwiftBridgeRouting"/> and the shim and twin it writes: an executable forward plan's
/// Swift-convention import becomes a shim of the same name and signature that calls a C-convention
/// twin of the plan's bridge, passing the trailing pointers the bridge moves into Swift's registers;
/// anything the shim cannot reproduce keeps its direct import with a reason; nothing that is not
/// executable is touched.
/// </summary>
public sealed class SwiftBridgeRoutingTests
{
    private const string Library = "MSwiftBindings";

    private static SwiftCallPlan SelfPlan() =>
        Planned(Planner().PlanForward(ClassMethod("Swift.Int", "Swift.Int"), Signature("long", "long", "SwiftSelf"), "$s1M1CC1fyS2iF"));

    private static SwiftCallPlan ThrowingPlan() =>
        Planned(Planner().PlanForward(Throwing(Free("Swift.Int", "Swift.Int")), Signature("long", "long", "SwiftError*"), "$s1M1tyS2iKF"));

    private static SwiftCallPlan DirectResultPlan() =>
        Planned(Planner().PlanForward(Free("Swift.String"), Signature("Swift.SwiftString.Buffer"), "$s1M1fSSyF"));

    private static string Source(string import) => $$"""
        namespace M
        {
            public static unsafe partial class C
            {
                public static long Call(long a) => PInvoke_f(a, default);

                {{import}}
            }
        }
        """;

    private static string Import(string entryPoint, string signature, string extraAttribute = "") => $$"""
        {{extraAttribute}}[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
                [global::System.Runtime.InteropServices.LibraryImport("M", EntryPoint = "{{entryPoint}}", SetLastError = false)]
                {{signature}}
        """;

    private static SwiftCallPlanSet Set(SwiftCallPlan plan, string entryPoint, string member = "M.C::PInvoke_f", bool executable = true) => new()
    {
        Module = "M",
        Coverage = [new SwiftPlanCoverageEntry(SwiftTransitionKind.ForwardPInvoke, member, entryPoint, SwiftPlanStatus.Planned)
        {
            Symbol = plan.Symbol,
            Hash = plan.Hash,
            Executable = executable,
        }],
        Plans = [plan],
    };

    private static MethodDeclarationSyntax Method(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);

    private static IEnumerable<string> AttributeNames(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes).Select(a => a.ToString());

    [Fact]
    public void ExecutablePlan_BecomesAShimCallingACdeclTwinOfTheBridge()
    {
        var plan = SelfPlan();
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var routing = SwiftBridgeRouting.Plan(Set(plan, "$s1M1CC1fyS2iF"), source, Library);

        var route = Assert.Single(routing.Routes);
        Assert.True(route.IsBridged, route.Refusal);
        var twinName = SwiftBridgeRouting.TwinName("PInvoke_f", plan.Symbol);
        Assert.Equal($"M.C::{twinName}", route.Twin);
        Assert.Equal(plan.Symbol, Assert.Single(routing.Bridged).Symbol);
        Assert.StartsWith(SwiftCallPlan.ForwardPrefix, plan.Symbol);

        var rewritten = routing.Rewrite(source);
        Assert.Empty(CSharpSyntaxTree.ParseText(rewritten).GetDiagnostics());

        // The shim keeps the import's name and signature, so no caller changes, and holds no import.
        var shim = Method(rewritten, "PInvoke_f");
        Assert.NotNull(shim.Body);
        Assert.DoesNotContain(shim.Modifiers, m => m.IsKind(SyntaxKind.PartialKeyword));
        Assert.Equal("(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self)", shim.ParameterList.ToString());
        Assert.Contains("public static long Call(long a) => PInvoke_f(a, default);", rewritten);

        // The twin imports the bridge from the wrapper under the C convention; self is a trailing pointer.
        var twin = Method(rewritten, twinName);
        Assert.Contains(twin.Modifiers, m => m.IsKind(SyntaxKind.PartialKeyword));
        Assert.Contains(AttributeNames(twin), a => a.Contains("CallConvCdecl", StringComparison.Ordinal));
        Assert.DoesNotContain(AttributeNames(twin), a => a.Contains("CallConvSwift", StringComparison.Ordinal));
        var import = Assert.Single(AttributeNames(twin), a => a.Contains("LibraryImport", StringComparison.Ordinal));
        Assert.Contains($"\"{Library}\"", import);
        Assert.Contains($"EntryPoint = \"{plan.Symbol}\"", import);
        Assert.Contains("SetLastError = false", import);
        Assert.Equal("(long a, nint __self)", twin.ParameterList.ToString());
        Assert.Contains($"{twinName}(a, (nint)self.Value)", shim.Body!.ToString());

        // No Swift-convention import is left.
        Assert.DoesNotContain("CallConvSwift", rewritten);
    }

    [Fact]
    public void ParameterNamedLikeASwiftRegister_IsBridged_OnlyTheShimsOwnLocalsCollide()
    {
        var plan = SelfPlan();
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf __self);"));
        Assert.True(Assert.Single(SwiftBridgeRouting.Plan(Set(plan, "$s1M1CC1fyS2iF"), source, Library).Routes).IsBridged);

        var colliding = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long __bridgeResult, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var route = Assert.Single(SwiftBridgeRouting.Plan(Set(plan, "$s1M1CC1fyS2iF"), colliding, Library).Routes);
        Assert.False(route.IsBridged);
        Assert.Contains("collides", route.Refusal);
    }

    /// <summary>
    /// An ordinary argument the import names like a trailing pointer — a wrapper that takes its own self
    /// word as a plain parameter — is still bridged: the twin declares it under another name, since the
    /// transition gate tells the trailing pointers apart by name, and the shim passes it by position.
    /// </summary>
    [Fact]
    public void OrdinaryParameterNamedLikeATrailingPointer_IsBridged_UnderAnotherTwinName()
    {
        var plan = SelfPlan();
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long __self, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var routing = SwiftBridgeRouting.Plan(Set(plan, "$s1M1CC1fyS2iF"), source, Library);
        var route = Assert.Single(routing.Routes);
        Assert.True(route.IsBridged, route.Refusal);

        var rewritten = routing.Rewrite(source);
        Assert.Empty(CSharpSyntaxTree.ParseText(rewritten).GetDiagnostics());
        var twinName = SwiftBridgeRouting.TwinName("PInvoke_f", plan.Symbol);
        Assert.Equal("(long __selfArgument, nint __self)", Method(rewritten, twinName).ParameterList.ToString());
        Assert.Contains($"{twinName}(__self, (nint)self.Value)", Method(rewritten, "PInvoke_f").Body!.ToString());

        // The twin's names are its own: an import whose self is already called by the renamed name
        // still maps each argument by position.
        var crossed = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long __self, global::System.Runtime.InteropServices.Swift.SwiftSelf __selfArgument);"));
        var crossedRouting = SwiftBridgeRouting.Plan(Set(plan, "$s1M1CC1fyS2iF"), crossed, Library);
        Assert.True(Assert.Single(crossedRouting.Routes).IsBridged, crossedRouting.Routes[0].Refusal);
        var crossedText = crossedRouting.Rewrite(crossed);
        Assert.Equal("(long __selfArgument, nint __self)", Method(crossedText, twinName).ParameterList.ToString());
        Assert.Contains($"{twinName}(__self, (nint)__selfArgument.Value)", Method(crossedText, "PInvoke_f").Body!.ToString());
    }

    [Theory]
    [InlineData("out", "fixed (global::System.Runtime.InteropServices.Swift.SwiftError* __bridgeErrorSlot = &error)", true)]
    [InlineData("ref", "fixed (global::System.Runtime.InteropServices.Swift.SwiftError* __bridgeErrorSlot = &error)", false)]
    public void ErrorByReference_IsPinnedForTheBridge_AndAnOutSlotIsClearedFirst(string modifier, string pin, bool cleared)
    {
        var plan = ThrowingPlan();
        var source = Source(Import("$s1M1tyS2iKF",
            $"private static partial long PInvoke_f(long a, {modifier} global::System.Runtime.InteropServices.Swift.SwiftError error);"));
        var routing = SwiftBridgeRouting.Plan(Set(plan, "$s1M1tyS2iKF"), source, Library);
        Assert.True(Assert.Single(routing.Routes).IsBridged, routing.Routes[0].Refusal);

        var shim = Method(routing.Rewrite(source), "PInvoke_f").Body!.ToString();
        Assert.Contains(pin, shim);
        Assert.Equal(cleared, shim.Contains("error = default;", StringComparison.Ordinal));
        if (cleared)
            Assert.True(shim.IndexOf("error = default;", StringComparison.Ordinal) < shim.IndexOf("fixed", StringComparison.Ordinal));
        Assert.Contains("(nint)__bridgeErrorSlot", shim);
    }

    [Fact]
    public void DirectResult_ComesBackThroughAResultBuffer_TheShimReturns()
    {
        var plan = DirectResultPlan();
        var source = Source(Import("$s1M1fSSyF", "private static partial global::Swift.SwiftString.Buffer PInvoke_f();"));
        var routing = SwiftBridgeRouting.Plan(Set(plan, "$s1M1fSSyF"), source, Library);
        Assert.True(Assert.Single(routing.Routes).IsBridged, routing.Routes[0].Refusal);

        var rewritten = routing.Rewrite(source);
        var twin = Method(rewritten, SwiftBridgeRouting.TwinName("PInvoke_f", plan.Symbol));
        Assert.Equal("void", twin.ReturnType.ToString());
        Assert.Equal("(nint __resultBuffer)", twin.ParameterList.ToString());
        var shim = Method(rewritten, "PInvoke_f").Body!.ToString();
        Assert.Contains("global::Swift.SwiftString.Buffer __bridgeResult = default;", shim);
        Assert.Contains("return __bridgeResult;", shim);
    }

    [Fact]
    public void NotExecutable_RoutesNothing_AndLeavesTheSourceAlone()
    {
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var routing = SwiftBridgeRouting.Plan(Set(SelfPlan(), "$s1M1CC1fyS2iF", executable: false), source, Library);

        Assert.Empty(routing.Routes);
        Assert.False(routing.BridgesAny);
        Assert.Same(source, routing.Rewrite(source));
        Assert.Null(routing.Assembly(SwiftArchitecture.Arm64));
    }

    // ---- a generation's requested bridge routes ----

    [Fact]
    public void RouteRequest_IsScopedToTheGeneration_AndKeyedBySymbol()
    {
        Assert.False(SwiftBridgeRouteRequests.IsRequested("$s1M1CC1fyS2iF"));
        using (SwiftBridgeRouteRequests.Use(["$s1M1CC1fyS2iF"]))
        {
            Assert.True(SwiftBridgeRouteRequests.IsRequested("$s1M1CC1fyS2iF"));
            Assert.False(SwiftBridgeRouteRequests.IsRequested("$s1M1CC1gyS2iF"));
            Assert.False(SwiftBridgeRouteRequests.IsRequested(null));
        }
        Assert.False(SwiftBridgeRouteRequests.IsRequested("$s1M1CC1fyS2iF"));
        Assert.Empty(SwiftBridgeRouteRequests.Unsatisfied());
    }

    [Fact]
    public void RouteRequest_BridgedImport_IsSatisfied()
    {
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var set = Set(SelfPlan(), "$s1M1CC1fyS2iF");
        using var requests = SwiftBridgeRouteRequests.Use(["$s1M1CC1fyS2iF"]);

        SwiftBridgeRouteRequests.Record("M", SwiftBridgeRouting.Plan(set, source, Library).Annotate(set));

        Assert.Empty(SwiftBridgeRouteRequests.Unsatisfied());
    }

    [Fact]
    public void RouteRequest_NamesTheMember_WhoseImportCallsItsDispatchThunk()
    {
        // A class member dispatched through the vtable is imported at its dispatch thunk; the request
        // names the declaration's own symbol either way.
        var source = Source(Import("$s1M1CC1fyS2iFTj", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var set = Set(SelfPlan(), "$s1M1CC1fyS2iFTj");
        using var requests = SwiftBridgeRouteRequests.Use(["$s1M1CC1fyS2iF"]);

        SwiftBridgeRouteRequests.Record("M", SwiftBridgeRouting.Plan(set, source, Library).Annotate(set));

        Assert.Empty(SwiftBridgeRouteRequests.Unsatisfied());
    }

    [Fact]
    public void RouteRequest_ImportKeptDirect_FailsWithTheMemberAndTheReason()
    {
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var set = Set(SelfPlan(), "$s1M1CC1fyS2iF", executable: false);
        using var requests = SwiftBridgeRouteRequests.Use(["$s1M1CC1fyS2iF"]);

        SwiftBridgeRouteRequests.Record("M", SwiftBridgeRouting.Plan(set, source, Library).Annotate(set));

        var failure = Assert.Single(SwiftBridgeRouteRequests.Unsatisfied());
        Assert.StartsWith("$s1M1CC1fyS2iF: M.C::PInvoke_f keeps its Swift-convention import (", failure);
    }

    [Fact]
    public void RouteRequest_WithNoSwiftConventionImport_Fails()
    {
        // Another route took the member (a wrapper, a thunk) or it was not bound: nothing calls the
        // requested symbol through a Swift-convention import, so there is nothing to bridge.
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var set = Set(SelfPlan(), "$s1M1CC1fyS2iF");
        using var requests = SwiftBridgeRouteRequests.Use(["$s1M1CC1gyS2iF"]);

        SwiftBridgeRouteRequests.Record("M", SwiftBridgeRouting.Plan(set, source, Library).Annotate(set));

        var failure = Assert.Single(SwiftBridgeRouteRequests.Unsatisfied());
        Assert.StartsWith("$s1M1CC1gyS2iF: no Swift-convention import calls it", failure);
    }

    [Fact]
    public void RouteRequest_ALaterEmissionOfTheModule_ReplacesTheEarlierRecord()
    {
        // Verify-recover can emit a module again; only the emission that ships is judged.
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var direct = Set(SelfPlan(), "$s1M1CC1fyS2iF", executable: false);
        var bridged = Set(SelfPlan(), "$s1M1CC1fyS2iF");
        using var requests = SwiftBridgeRouteRequests.Use(["$s1M1CC1fyS2iF"]);

        SwiftBridgeRouteRequests.Record("M", SwiftBridgeRouting.Plan(direct, source, Library).Annotate(direct));
        Assert.NotEmpty(SwiftBridgeRouteRequests.Unsatisfied());
        SwiftBridgeRouteRequests.Record("M", SwiftBridgeRouting.Plan(bridged, source, Library).Annotate(bridged));
        Assert.Empty(SwiftBridgeRouteRequests.Unsatisfied());
    }

    [Theory]
    [InlineData("[global::System.Runtime.InteropServices.SuppressGCTransition]", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);", "carries the attribute")]
    [InlineData("", "private static partial long PInvoke_f<T>(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);", "generic")]
    [InlineData("", "private static partial long PInvoke_f(long a = 1, global::System.Runtime.InteropServices.Swift.SwiftSelf self = default);", "default value")]
    [InlineData("", "private static partial long PInvoke_f(long a, ref global::System.Runtime.InteropServices.Swift.SwiftSelf self);", "by reference")]
    public void ShapeTheShimCannotReproduce_KeepsItsDirectImport_WithTheReason(string attribute, string signature, string reason)
    {
        var plan = SelfPlan();
        var source = Source(Import("$s1M1CC1fyS2iF", signature, attribute));
        var set = Set(plan, "$s1M1CC1fyS2iF");
        var routing = SwiftBridgeRouting.Plan(set, source, Library);

        var route = Assert.Single(routing.Routes);
        Assert.False(route.IsBridged);
        Assert.Contains(reason, route.Refusal);
        Assert.False(routing.BridgesAny);
        Assert.Equal(source, routing.Rewrite(source));

        // The sidecar records the refusal, which the bridge gate and the build log read as SWIFTBIND128.
        var entry = Assert.Single(routing.Annotate(set).Coverage);
        Assert.False(entry.Bridged);
        Assert.Null(entry.Twin);
        Assert.Contains(reason, entry.BridgeRefusal);
    }

    private static string ExternImport(string entryPoint, string signature, string dllImportExtra = "", string containerModifiers = "public static unsafe partial") => $$"""
        namespace M
        {
            {{containerModifiers}} class C
            {
                public static long Call(long a) => PInvoke_f(a, default);

                [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
                [global::System.Runtime.InteropServices.DllImport("M", EntryPoint = "{{entryPoint}}"{{dllImportExtra}})]
                {{signature}}
            }
        }
        """;

    /// <summary>
    /// The runtime declares its Swift-convention imports as <c>static extern</c> DllImports. The shim
    /// reproduces one exactly as it does a LibraryImport: same name and signature, a body calling the
    /// C-convention twin, and no extern or Swift-convention import left.
    /// </summary>
    [Fact]
    public void ExternDllImport_BecomesAShimCallingACdeclTwinOfTheBridge()
    {
        var plan = SelfPlan();
        var source = ExternImport("$s1M1CC1fyS2iF", "public static extern long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);");
        var routing = SwiftBridgeRouting.Plan(Set(plan, "$s1M1CC1fyS2iF"), source, Library);

        var route = Assert.Single(routing.Routes);
        Assert.True(route.IsBridged, route.Refusal);
        var rewritten = routing.Rewrite(source);
        Assert.Empty(CSharpSyntaxTree.ParseText(rewritten).GetDiagnostics());

        var shim = Method(rewritten, "PInvoke_f");
        Assert.NotNull(shim.Body);
        Assert.DoesNotContain(shim.Modifiers, m => m.IsKind(SyntaxKind.ExternKeyword));
        Assert.Contains(shim.Modifiers, m => m.IsKind(SyntaxKind.PublicKeyword));
        Assert.Equal("(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self)", shim.ParameterList.ToString());
        Assert.DoesNotContain(AttributeNames(shim), a => a.Contains("DllImport", StringComparison.Ordinal));

        var twin = Method(rewritten, SwiftBridgeRouting.TwinName("PInvoke_f", plan.Symbol));
        Assert.Contains(twin.Modifiers, m => m.IsKind(SyntaxKind.PartialKeyword));
        Assert.Contains(AttributeNames(twin), a => a.Contains("LibraryImport", StringComparison.Ordinal) && a.Contains($"\"{Library}\"", StringComparison.Ordinal));
        Assert.Equal("(long a, nint __self)", twin.ParameterList.ToString());
        Assert.DoesNotContain("CallConvSwift", rewritten);
        Assert.DoesNotContain("DllImport", rewritten);
    }

    [Theory]
    [InlineData(", SetLastError = true", "public static partial", "public static extern long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);", "SetLastError")]
    [InlineData(", CharSet = global::System.Runtime.InteropServices.CharSet.Unicode", "public static partial", "public static extern long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);", "CharSet")]
    [InlineData("", "public static partial", "[return: global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.I8)] public static extern long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);", "marshals its return value")]
    [InlineData("", "public static", "public static extern long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);", "not partial")]
    [InlineData("", "public static partial", "public extern long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);", "neither a static partial nor a static extern")]
    public void ExternDllImportTheShimCannotReproduce_KeepsItsDirectImport_WithTheReason(string dllImportExtra, string container, string signature, string reason)
    {
        var plan = SelfPlan();
        var source = ExternImport("$s1M1CC1fyS2iF", signature, dllImportExtra, container);
        var routing = SwiftBridgeRouting.Plan(Set(plan, "$s1M1CC1fyS2iF"), source, Library);

        var route = Assert.Single(routing.Routes);
        Assert.False(route.IsBridged);
        Assert.Contains(reason, route.Refusal);
        Assert.Equal(source, routing.Rewrite(source));
    }

    [Fact]
    public void ImportMissingFromTheSource_IsRefused_NotSilentlyDropped()
    {
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_g(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var route = Assert.Single(SwiftBridgeRouting.Plan(Set(SelfPlan(), "$s1M1CC1fyS2iF"), source, Library).Routes);

        Assert.False(route.IsBridged);
        Assert.Contains("not in the emitted C#", route.Refusal);
    }

    [Fact]
    public void Annotate_RecordsTheTwinOnTheCoverageEntry()
    {
        var plan = SelfPlan();
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var set = Set(plan, "$s1M1CC1fyS2iF");
        var entry = Assert.Single(SwiftBridgeRouting.Plan(set, source, Library).Annotate(set).Coverage);

        Assert.True(entry.Bridged);
        Assert.Equal($"M.C::{SwiftBridgeRouting.TwinName("PInvoke_f", plan.Symbol)}", entry.Twin);
        Assert.Null(entry.BridgeRefusal);
        Assert.Empty(SwiftBridgeGate.RoutingDisagreements(SwiftBridgeRouting.Plan(set, source, Library).Annotate(set)));
    }

    [Fact]
    public void Rewrite_RecordsTheReplacementInTheJournal()
    {
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var routing = SwiftBridgeRouting.Plan(Set(SelfPlan(), "$s1M1CC1fyS2iF"), source, Library);
        var journal = new TextEditJournal();
        var rewritten = routing.Rewrite(source, journal);

        var edit = Assert.Single(journal.Edits);
        Assert.Equal(rewritten.Length - source.Length, edit.NewLength - edit.OldLength);
        // Text before the import maps to itself; text after it moves by the growth.
        var call = source.IndexOf("public static long Call", StringComparison.Ordinal);
        Assert.Equal(call, journal.MapOffset(call));
        var tail = source.LastIndexOf('}');
        Assert.Equal(rewritten.LastIndexOf('}'), journal.MapOffset(tail));
    }

    [Fact]
    public void Rewrite_OfAnImportChangedSincePlanning_Throws()
    {
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var routing = SwiftBridgeRouting.Plan(Set(SelfPlan(), "$s1M1CC1fyS2iF"), source, Library);

        var changed = source.Replace("long a,", "long b,", StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => routing.Rewrite(changed));
    }

    [Fact]
    public void Assembly_HoldsOneWeaklyReferencingBridgePerBridgedPlan_OnEveryArchitecture()
    {
        var plan = SelfPlan();
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var routing = SwiftBridgeRouting.Plan(Set(plan, "$s1M1CC1fyS2iF"), source, Library);

        foreach (var architecture in SwiftBridgeRouting.Architectures)
        {
            var assembly = routing.Assembly(architecture)!;
            Assert.Equal(new[] { plan.Symbol }, assembly.Bridges);
            Assert.Contains($".globl _{plan.Symbol}", assembly.Source);
            Assert.Contains(".weak_reference _$s1M1CC1fyS2iF", assembly.Source);
        }
        Assert.Equal("M.Bridges.arm64.s", SwiftBridgeRouting.AssemblyFileName("M", SwiftArchitecture.Arm64));
        Assert.Equal("M.Bridges.x86_64.s", SwiftBridgeRouting.AssemblyFileName("M", SwiftArchitecture.X86_64));
    }

    // ---- the bridge gate's view of a routed generation ----

    [Fact]
    public void RoutingDisagreements_ExecutableButNotRouted_AndBridgedButNotExecutable_BothFail()
    {
        var plan = SelfPlan();
        var unrouted = Set(plan, "$s1M1CC1fyS2iF");
        Assert.Contains(SwiftBridgeGate.RoutingDisagreements(unrouted), d => d.Contains("did not route it", StringComparison.Ordinal));

        var refused = new SwiftCallPlanSet
        {
            Module = "M",
            Coverage = [unrouted.Coverage[0] with { Bridged = false, BridgeRefusal = "the import is generic" }],
            Plans = unrouted.Plans,
        };
        Assert.Contains(SwiftBridgeGate.RoutingDisagreements(refused), d => d.Contains("kept its direct import", StringComparison.Ordinal));

        var stale = new SwiftCallPlanSet
        {
            Module = "M",
            Coverage = [unrouted.Coverage[0] with { Executable = false, Bridged = true, Twin = "M.C::x" }],
            Plans = unrouted.Plans,
        };
        Assert.Contains(SwiftBridgeGate.RoutingDisagreements(stale), d => d.Contains("does not prove executable", StringComparison.Ordinal));

        var notExecutable = Set(plan, "$s1M1CC1fyS2iF", executable: false);
        Assert.Empty(SwiftBridgeGate.RoutingDisagreements(notExecutable));
    }

    [Fact]
    public void RoutedFindings_FailWhenNoRoutedGenerationBridgesAnything_AndPassOnceOneDoes()
    {
        var plan = SelfPlan();
        var nothingExecutable = Set(plan, "$s1M1CC1fyS2iF", executable: false);
        var finding = Assert.Single(SwiftBridgeGate.RoutedFindings([nothingExecutable]));
        Assert.Contains("checked nothing", finding, StringComparison.Ordinal);

        var routed = Set(plan, "$s1M1CC1fyS2iF");
        routed = new SwiftCallPlanSet
        {
            Module = "M",
            Coverage = [routed.Coverage[0] with { Bridged = true, Twin = "M.C::x" }],
            Plans = routed.Plans,
        };
        // A routed module with nothing to bridge is fine beside one that bridges.
        Assert.Empty(SwiftBridgeGate.RoutedFindings([nothingExecutable, routed]));
        Assert.Empty(SwiftBridgeGate.RoutedFindings([]));
    }

    // ---- a module split across files ----

    private static string TypeSource(string type, string import) => $$"""
        namespace M
        {
            public static unsafe partial class {{type}}
            {
                {{import}}
            }
        }
        """;

    [Fact]
    public void SplitModule_EachImportIsRoutedInTheFileThatDeclaresIt_AndEachRewriteNamesThatFile()
    {
        var self = SelfPlan();
        var throwing = ThrowingPlan();
        var plans = new SwiftCallPlanSet
        {
            Module = "M",
            Coverage = [Set(self, "$s1M1CC1fyS2iF").Coverage[0], Set(throwing, "$s1M1tyS2iKF", member: "M.D::PInvoke_t").Coverage[0]],
            Plans = [self, throwing],
        };
        var main = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var split = TypeSource("D", Import("$s1M1tyS2iKF", "private static partial long PInvoke_t(long a, out global::System.Runtime.InteropServices.Swift.SwiftError error);"));

        var routing = SwiftBridgeRouting.Plan(plans, [new SwiftBridgeSource("M.cs", main), new SwiftBridgeSource("M.Types.D.cs", split)], Library);

        Assert.All(routing.Routes, r => Assert.True(r.IsBridged, r.Refusal));
        // Rewrites are ordered by file name, ordinally.
        Assert.Equal(new[] { "M.Types.D.cs", "M.cs" }, routing.Rewrites.Select(r => r.File));
        Assert.Equal(new[] { "M.D::PInvoke_t", "M.C::PInvoke_f" }, routing.Rewrites.Select(r => r.Member));
        // Each file's rewrite touches only the imports that file declares.
        var mainRewritten = routing.Rewrite(main);
        var splitRewritten = routing.Rewrite(split);
        Assert.Contains(SwiftBridgeRouting.TwinName("PInvoke_f", self.Symbol), mainRewritten);
        Assert.DoesNotContain(SwiftBridgeRouting.TwinName("PInvoke_t", throwing.Symbol), mainRewritten);
        Assert.Contains(SwiftBridgeRouting.TwinName("PInvoke_t", throwing.Symbol), splitRewritten);
        Assert.DoesNotContain("CallConvSwift", mainRewritten + splitRewritten);
        // Each rewrite is the import as declared and what replaced it.
        foreach (var (rewrite, before, after) in new[] { (routing.Rewrites.Single(r => r.File == "M.cs"), main, mainRewritten), (routing.Rewrites.Single(r => r.File == "M.Types.D.cs"), split, splitRewritten) })
        {
            Assert.Contains(rewrite.Original, before);
            Assert.Contains(rewrite.Replacement, after);
            Assert.Equal(before, after.Replace(rewrite.Replacement, rewrite.Original, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void SplitModule_TheSameImportInTwoFiles_IsRefused()
    {
        var plan = SelfPlan();
        var import = Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);");
        var routing = SwiftBridgeRouting.Plan(Set(plan, "$s1M1CC1fyS2iF"),
            [new SwiftBridgeSource("M.cs", Source(import)), new SwiftBridgeSource("M.Types.C.cs", TypeSource("C", import))], Library);

        var route = Assert.Single(routing.Routes);
        Assert.False(route.IsBridged);
        Assert.Contains("two imports share", route.Refusal);
        Assert.Empty(routing.Rewrites);
    }

    [Fact]
    public void Withdraw_KeepsEveryImport_WithTheReason_AndARefusalKeepsItsOwn()
    {
        var self = SelfPlan();
        var direct = DirectResultPlan();
        var plans = new SwiftCallPlanSet
        {
            Module = "M",
            Coverage = [Set(self, "$s1M1CC1fyS2iF").Coverage[0], Set(direct, "$s1M1fSSyF", member: "M.C::PInvoke_missing").Coverage[0]],
            Plans = [self, direct],
        };
        var source = Source(Import("$s1M1CC1fyS2iF", "private static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);"));
        var routing = SwiftBridgeRouting.Plan(plans, source, Library);
        var refused = routing.Routes.Single(r => !r.IsBridged);

        var withdrawn = routing.Withdraw("the relink failed");

        Assert.Empty(withdrawn.Bridged);
        Assert.Empty(withdrawn.Rewrites);
        Assert.Equal(source, withdrawn.Rewrite(source));
        Assert.All(withdrawn.Routes, r => Assert.False(r.IsBridged));
        Assert.Equal("the relink failed", withdrawn.Routes.Single(r => r.Member == "M.C::PInvoke_f").Refusal);
        Assert.Equal(refused.Refusal, withdrawn.Routes.Single(r => r.Member == refused.Member).Refusal);
        Assert.Null(withdrawn.Assembly(SwiftArchitecture.Arm64));
    }

    // ---- the calling convention of a bridge symbol ----

    [Fact]
    public void BridgeSymbol_TakesTheCConvention_AndRefusesTheSwiftOne()
    {
        Assert.True(SwiftCallPlan.IsForwardBridgeSymbol("SBSR_0123456789abcdef"));
        Assert.True(SwiftCallPlan.IsForwardBridgeSymbol("SBSR_thick_0123456789abcdef"));
        Assert.False(SwiftCallPlan.IsForwardBridgeSymbol("SBSRR_0123456789abcdef"));
        Assert.False(SwiftCallPlan.IsForwardBridgeSymbol("$s1M1fyyF"));
        Assert.False(SwiftCallPlan.IsForwardBridgeSymbol(null));

        Assert.Equal(PInvokeCallingConvention.Cdecl,
            PInvokeEmitHelper.SelectCallingConvention("SBSR_0123456789abcdef", PInvokeCallingConvention.Cdecl));
        Assert.Throws<InvalidOperationException>(() =>
            PInvokeEmitHelper.SelectCallingConvention("SBSR_0123456789abcdef", PInvokeCallingConvention.Swift));
    }
}
