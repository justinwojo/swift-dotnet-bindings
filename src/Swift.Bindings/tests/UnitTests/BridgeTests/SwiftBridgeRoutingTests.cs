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
