// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SwiftBindings.TransitionGate;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Seeded reds for the Swift transition gate's rules, over small assemblies compiled here with
/// Roslyn: each rule fires on the shape it names and nowhere else, the precedence and allow-list
/// edges hold, and a fixture of only safe transitions passes with a non-vacuous inventory.
/// </summary>
public class SwiftTransitionScannerTests
{
    private const string Preamble = """
        using System;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Swift;
        [assembly: DisableRuntimeMarshalling]
        public struct Pair { public long A; public long B; }
        public enum Small : short { A }

        """;

    private const string Swift = "[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]";

    private static readonly string RuntimeDll = Path.Combine(AppContext.BaseDirectory, "Swift.Runtime.dll");

    // ---- R1 -------------------------------------------------------------------------------------

    [Fact]
    public void R1_SwiftPInvokeWithAggregateParameter_Fires()
    {
        var report = Scan($$"""
            public static class C
            {
                [DllImport("lib", EntryPoint = "agg")] {{Swift}}
                public static extern long Agg(Pair p, long n);
            }
            """);
        var v = Assert.Single(report.Violations);
        Assert.Equal(TransitionRule.R1, v.Rule);
        Assert.Equal(TransitionSiteKind.PInvoke, v.Site);
        Assert.Equal("C::Agg", v.Member);
        Assert.Equal("agg", v.EntryPoint);
        Assert.Equal("lib", v.Library);
        Assert.Equal(new[] { "Aggregate" }, v.Classes);
        Assert.Contains("parameter 0", v.Reason);
        Assert.Equal(1, report.Inventory.SwiftPInvokesAggregateOnly);
    }

    [Fact]
    public void R1_SwiftPInvokeWithAggregateResult_Fires()
    {
        var report = Scan($$"""
            public static class C
            {
                [DllImport("lib", EntryPoint = "ret")] {{Swift}}
                public static extern Pair Ret(long n);
            }
            """);
        var v = Assert.Single(report.Violations);
        Assert.Equal(TransitionRule.R1, v.Rule);
        Assert.Equal(new[] { "AggregateResult" }, v.Classes);
    }

    [Fact]
    public void R1_SwiftPInvokeWithSpecialRegisters_FiresAndCountsTheSpecials()
    {
        var report = Scan($$"""
            public static unsafe class C
            {
                [DllImport("lib", EntryPoint = "self")] {{Swift}}
                public static extern long WithSelf(long n, SwiftSelf self, SwiftError* error);

                [DllImport("lib", EntryPoint = "ind")] {{Swift}}
                public static extern void WithIndirect(SwiftIndirectResult result, long n);
            }
            """);
        Assert.Equal(2, report.Violations.Length);
        Assert.All(report.Violations, v => Assert.Equal(TransitionRule.R1, v.Rule));
        Assert.Equal(new[] { "SwiftError", "SwiftSelf" }, report.Violations.Single(v => v.EntryPoint == "self").Classes);
        Assert.Equal(new[] { "SwiftIndirectResult" }, report.Violations.Single(v => v.EntryPoint == "ind").Classes);
        Assert.Equal(2, report.Inventory.SwiftPInvokesSpecialBearing);
        Assert.Equal(1, report.Inventory.SwiftPInvokesWithSwiftSelf);
        Assert.Equal(1, report.Inventory.SwiftPInvokesWithSwiftError);
        Assert.Equal(1, report.Inventory.SwiftPInvokesWithSwiftIndirectResult);
    }

    [Fact]
    public void R1_LibraryImportShapedLocalExtern_IsReportedAtTheOuterMethod()
    {
        // LibraryImport's generated stub declares the native signature on a local extern function.
        var report = Scan($$"""
            public static partial class C
            {
                public static long Outer(Pair p)
                {
                    return __PInvoke(p);

                    [DllImport("lib", EntryPoint = "local")] {{Swift}}
                    static extern long __PInvoke(Pair p);
                }
            }
            """);
        var v = Assert.Single(report.Violations);
        Assert.Equal(TransitionRule.R1, v.Rule);
        Assert.Equal("C::Outer", v.Member);
        Assert.Equal("local", v.EntryPoint);
    }

    [Fact]
    public void DllImportCallingConvention_OverridesUnmanagedCallConv()
    {
        // The runtime honours an explicit DllImport CallingConvention over [UnmanagedCallConv].
        var report = Scan($$"""
            public static class C
            {
                [DllImport("lib", EntryPoint = "cdecl", CallingConvention = CallingConvention.Cdecl)] {{Swift}}
                public static extern long Agg(Pair p);
            }
            """);
        Assert.Empty(report.Violations);
        Assert.Equal(0, report.Inventory.SwiftPInvokes);
        Assert.Equal(1, report.Inventory.NonSwiftPInvokes);
        Assert.Equal(TransitionConvention.Cdecl, Assert.Single(report.Sites).Convention);
    }

    // ---- R2 -------------------------------------------------------------------------------------

    [Fact]
    public void R2_CdeclPInvokeWithSwiftIndirectResult_Fires()
    {
        var report = Scan("""
            public static class C
            {
                [DllImport("lib", EntryPoint = "op", CallingConvention = CallingConvention.Cdecl)]
                public static extern void Op(SwiftIndirectResult result, long n);
            }
            """);
        var v = Assert.Single(report.Violations);
        Assert.Equal(TransitionRule.R2, v.Rule);
        Assert.Equal(TransitionSiteKind.PInvoke, v.Site);
        Assert.Equal(new[] { "SwiftIndirectResult" }, v.Classes);
        Assert.Equal(1, report.Inventory.NonSwiftPInvokesWithSwiftSpecial);
    }

    [Fact]
    public void R2_SwiftErrorPointerOutsideSwift_IsAllowed()
    {
        var report = Scan("""
            public static unsafe class C
            {
                [DllImport("lib", EntryPoint = "err", CallingConvention = CallingConvention.Cdecl)]
                public static extern long Err(long n, SwiftError* error);
            }
            """);
        Assert.Empty(report.Violations);
        Assert.Equal(1, report.Inventory.NonSwiftPInvokesWithSwiftErrorPointer);
    }

    [Fact]
    public void R2_CdeclFunctionPointerFieldWithSwiftSelf_Fires()
    {
        var report = Scan("""
            public static unsafe class C
            {
                public static delegate* unmanaged[Cdecl]<SwiftSelf, long, void> Field;
            }
            """);
        var v = Assert.Single(report.Violations);
        Assert.Equal(TransitionRule.R2, v.Rule);
        Assert.Equal(TransitionSiteKind.FunctionPointerType, v.Site);
        Assert.Equal("C::Field", v.Member);
        Assert.Equal(new[] { "SwiftSelf" }, v.Classes);
    }

    [Fact]
    public void R2_CdeclCalliWithSwiftSelf_FiresAtTheCalli()
    {
        var report = Scan("""
            public static unsafe class C
            {
                public static void Call(nint p, SwiftSelf self) => ((delegate* unmanaged[Cdecl]<SwiftSelf, void>)p)(self);
            }
            """);
        Assert.All(report.Violations, v => Assert.Equal(TransitionRule.R2, v.Rule));
        var calli = Assert.Single(report.Violations, v => v.Site == TransitionSiteKind.Calli);
        Assert.Equal("C::Call", calli.Member);
        Assert.NotNull(calli.IlOffset);
        // The pointer's type also sits in the body's locals; that is the same shape, reported as a type.
        Assert.All(report.Violations.Where(v => v.Site != TransitionSiteKind.Calli),
            v => Assert.Equal(TransitionSiteKind.FunctionPointerType, v.Site));
        Assert.Equal(1, report.Inventory.NonSwiftUnmanagedCalliSites);
    }

    [Fact]
    public void R2_ManagedCalliWithSwiftSelf_Fires()
    {
        var report = Scan("""
            public static unsafe class C
            {
                public static void Call(nint p, SwiftSelf self) => ((delegate*<SwiftSelf, void>)p)(self);
            }
            """);
        var calli = Assert.Single(report.Violations, v => v.Site == TransitionSiteKind.Calli);
        Assert.Equal(TransitionRule.R2, calli.Rule);
        Assert.Contains(report.Sites, s => s.Kind == TransitionSiteKind.Calli && s.Convention == TransitionConvention.Managed
                                           && s.Rules.SequenceEqual(new[] { TransitionRule.R2 }));
    }

    [Fact]
    public void R2_FunctionPointerTypeOnlyInACalliSignature_Fires()
    {
        // The returned pointer is discarded, so its type lives only in the call site's signature.
        var report = Scan("""
            public static unsafe class C
            {
                public static void Call(nint p) => _ = ((delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<SwiftSelf, void>>)p)();
            }
            """);
        Assert.Contains(report.Violations, v => v.Rule == TransitionRule.R2 && v.Site == TransitionSiteKind.FunctionPointerType
                                                && v.Reason.Contains("calli signature"));
    }

    [Fact]
    public void R2_FunctionPointerTypes_AreReportedPerOverload()
    {
        var report = Scan("""
            public static unsafe class C
            {
                public static void M(delegate* unmanaged[Cdecl]<SwiftSelf, void> f) { }
                public static void M(int n, delegate* unmanaged[Cdecl]<SwiftSelf, void> f) { }
            }
            """);
        Assert.Equal(2, report.Violations.Count(v => v.Rule == TransitionRule.R2 && v.Member == "C::M"));
        Assert.Equal(2, report.Sites.Select(s => s.MemberToken).Distinct().Count());
    }

    [Theory]
    [InlineData(0x7F000001u)] // not a metadata table
    [InlineData(0x110000FFu)] // a StandAloneSig row that does not exist
    public void CalliWithABadSignatureToken_IsAnInspectionError(uint badToken)
    {
        var image = Compile(Preamble + """
            public static unsafe class C
            {
                public static long Call(nint p, long a) => ((delegate* unmanaged[Cdecl]<long, long>)p)(a);
            }
            """, "Fixture");
        // Patch the one calli's StandAloneSig token (0x29 then 11xxxxxx little-endian).
        var at = Enumerable.Range(0, image.Length - 5).Single(i => image[i] == 0x29 && image[i + 4] == 0x11 && image[i + 3] == 0x00);
        BitConverter.GetBytes(badToken).CopyTo(image, at + 1);
        var report = SwiftTransitionScanner.Scan(image, "Fixture.dll", Options());
        Assert.False(report.Passed);
        var error = Assert.Single(report.InspectionErrors);
        Assert.StartsWith("C::Call", error.Member);
    }

    [Fact]
    public void R2_CdeclUnmanagedCallersOnlyWithSwiftSelf_Fires()
    {
        var report = Scan("""
            public static class C
            {
                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                public static void Callback(SwiftSelf self, long n) { }
            }
            """);
        var v = Assert.Single(report.Violations);
        Assert.Equal(TransitionRule.R2, v.Rule);
        Assert.Equal(TransitionSiteKind.UnmanagedCallersOnly, v.Site);
    }

    // ---- R4 -------------------------------------------------------------------------------------

    [Fact]
    public void R4_SwiftUnmanagedCallersOnly_Fires()
    {
        var report = Scan("""
            public static class C
            {
                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
                public static long Callback(long n) => n;
            }
            """);
        var v = Assert.Single(report.Violations);
        Assert.Equal(TransitionRule.R4, v.Rule);
        Assert.Equal(TransitionSiteKind.UnmanagedCallersOnly, v.Site);
        Assert.Equal(new[] { "swift-uco" }, v.Classes);
        Assert.Equal(1, report.Inventory.SwiftUnmanagedCallersOnly);
    }

    [Fact]
    public void R4_TypedSelfInACdeclReverseSignature_FiresAlongsideR2()
    {
        // SwiftSelf<T> is forbidden in any reverse signature (R4) and in any non-Swift one (R2).
        var report = Scan("""
            public static class C
            {
                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                public static void Callback(SwiftSelf<Pair> self) { }
            }
            """);
        Assert.Equal(new[] { TransitionRule.R2, TransitionRule.R4 }, report.Violations.Select(v => v.Rule));
        Assert.Equal(new[] { TransitionRule.R2, TransitionRule.R4 }, Assert.Single(report.Sites).Rules);
    }

    // ---- R5 -------------------------------------------------------------------------------------

    [Fact]
    public void R5_SwiftCalliWithAggregate_Fires()
    {
        var report = Scan("""
            public static unsafe class C
            {
                public static void Call(nint p, Pair a) => ((delegate* unmanaged[Swift]<Pair, void>)p)(a);
            }
            """);
        var v = Assert.Single(report.Violations);
        Assert.Equal(TransitionRule.R5, v.Rule);
        Assert.Equal(TransitionSiteKind.Calli, v.Site);
        Assert.Equal("C::Call", v.Member);
        Assert.NotNull(v.IlOffset);
        Assert.Equal(new[] { "Aggregate" }, v.Classes);
    }

    // ---- allow-list and fail-closed resolution ----------------------------------------------------

    [Fact]
    public void AllowList_IsExplicitAndHoldsAgainstTheRealRuntime()
    {
        using var pe = new PEReader(File.OpenRead(RuntimeDll));
        Assert.Empty(CarrierClassifier.VerifyAllowList(pe.GetMetadataReader()));
        Assert.Null(CarrierClassifier.FindWrapper("Swift.Foundation.Data"));
        Assert.Null(CarrierClassifier.FindWrapper("Foundation.Data"));
        Assert.DoesNotContain(CarrierClassifier.OneWordWrappers, w => w.FullName.EndsWith(".Data", StringComparison.Ordinal));
    }

    [Fact]
    public void AllowListedNameDefinedOutsideTheRuntime_IsAnAggregate()
    {
        var report = Scan($$"""
            namespace Swift.Runtime { public readonly struct TypeMetadata { private readonly nint _handle; } }
            public static class C
            {
                [DllImport("lib", EntryPoint = "spoof")] {{Swift}}
                public static extern long Spoof(Swift.Runtime.TypeMetadata m);
            }
            """);
        var v = Assert.Single(report.Violations);
        Assert.Equal(TransitionRule.R1, v.Rule);
        Assert.Contains("allow-listed only from Swift.Runtime", v.Reason);
    }

    [Fact]
    public void AllowListedNameWithTheWrongLayout_IsAnAggregate_AndScanningThatRuntimeIsAnInspectionError()
    {
        var fakeRuntime = Compile("""
            namespace Swift.Runtime { public readonly struct TypeMetadata { private readonly nint _a; private readonly nint _b; } }
            """, "Swift.Runtime");
        var report = Scan($$"""
            public static class C
            {
                [DllImport("lib", EntryPoint = "wide")] {{Swift}}
                public static extern long Wide(Swift.Runtime.TypeMetadata m);
            }
            """, references: new[] { fakeRuntime });
        var v = Assert.Single(report.Violations);
        Assert.Contains("not a one-field sequential struct (2 instance field(s))", v.Reason);

        var runtimeReport = SwiftTransitionScanner.Scan(fakeRuntime, "Swift.Runtime.dll", Options());
        Assert.False(runtimeReport.Passed);
        Assert.Contains(runtimeReport.InspectionErrors, e => e.Message.Contains("TypeMetadata is not a one-field"));
        Assert.Contains(runtimeReport.InspectionErrors, e => e.Message.Contains("ProtocolWitnessTable is not defined"));
    }

    [Fact]
    public void OneWordStructNotOnTheAllowList_IsAnAggregate()
    {
        var report = Scan($$"""
            public struct Data { private readonly nint _storage; }
            public static class C
            {
                [DllImport("lib", EntryPoint = "data")] {{Swift}}
                public static extern long Count(Data d);
            }
            """);
        Assert.Equal(TransitionRule.R1, Assert.Single(report.Violations).Rule);
    }

    [Fact]
    public void ValueTypeThatCannotBeResolved_FailsClosed()
    {
        var elsewhere = Compile("public struct Remote { public nint Word; }", "Elsewhere");
        var report = Scan($$"""
            public static class C
            {
                [DllImport("lib", EntryPoint = "remote")] {{Swift}}
                public static extern long Remote(Remote r);
            }
            """, references: new[] { elsewhere }, giveScannerTheReferences: false);
        var v = Assert.Single(report.Violations);
        Assert.Equal(new[] { "Unresolved" }, v.Classes);
        Assert.Contains("could not be resolved", v.Reason);
    }

    [Fact]
    public void Classifier_SpellsEnumsAndWrappersAsTheWordTheyCarry()
    {
        var image = Compile(Preamble + $$"""
            public static unsafe class C
            {
                [DllImport("lib", EntryPoint = "words")] {{Swift}}
                public static extern Small Words(Swift.Runtime.TypeMetadata m, Swift.Runtime.TypeMetadataRequest r,
                    Swift.UnsafeMutablePointer<long> p, Swift.UnsafeRawPointer q, Swift.Runtime.ProtocolWitnessTable w, double d);
            }
            """, "Fixture", new[] { File.ReadAllBytes(RuntimeDll) });
        using var pe = new PEReader(new MemoryStream(image));
        var md = pe.GetMetadataReader();
        var method = md.MethodDefinitions.Select(md.GetMethodDefinition).Single(m => md.GetString(m.Name) == "Words");
        var signature = new SigTypeProvider(md).DecodeMethod(method.Signature);
        using var resolver = new AssemblyResolver(new[] { AppContext.BaseDirectory, System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory() });
        resolver.Add(md);
        var classifier = new CarrierClassifier(resolver);
        var verdicts = signature.ParameterTypes.Prepend(signature.ReturnType).Select(classifier.Classify).ToList();
        Assert.All(verdicts, v => Assert.Equal(CarrierKind.Primitive, v.Kind));
        Assert.Equal(new[] { "i16", "isize", "i32", "ptr", "ptr", "isize", "f64" }, verdicts.Select(v => v.Canonical));
    }

    // ---- R3 -------------------------------------------------------------------------------------

    private const string Cdecl = "[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]";

    [Fact]
    public void R3_TwinMatchingItsPlan_Passes_AsDeclaredAndAsTheSourceGeneratorRenamesIt()
    {
        var report = Scan($$"""
            public static class C
            {
                [DllImport("lib", EntryPoint = "SBSR_1")] {{Cdecl}}
                public static extern long Twin(long a, nint __self, nint __error);

                [DllImport("lib", EntryPoint = "SBSR_1")] {{Cdecl}}
                public static extern long Generated(long __a_native, nint ____self_native, nint ____error_native);
            }
            """, plan: Plans(SelfErrorPlan("SBSR_1")));

        Assert.True(report.Passed, string.Join("\n", report.Violations.Select(v => $"{v.Rule} {v.Member}: {v.Reason}")));
        Assert.Equal(2, report.Inventory.BridgeTwins);
    }

    [Fact]
    public void R3_ReorderedTrailingPointers_Fire()
    {
        // Every trailing pointer is pointer-sized, so the carriers agree; only the order is wrong, and a
        // bridge would move the error slot into the self register.
        var report = Scan($$"""
            public static class C
            {
                [DllImport("lib", EntryPoint = "SBSR_1")] {{Cdecl}}
                public static extern long Twin(long a, nint __error, nint __self);
            }
            """, plan: Plans(SelfErrorPlan("SBSR_1")));

        var v = Assert.Single(report.Violations);
        Assert.Equal(TransitionRule.R3, v.Rule);
        Assert.Equal("SBSR_1", v.EntryPoint);
        Assert.Contains("Self pointer (__self)", v.Reason);
    }

    [Fact]
    public void R3_OrdinaryArgumentNamedAsATrailingPointer_Fires()
    {
        var report = Scan($$"""
            public static class C
            {
                [DllImport("lib", EntryPoint = "SBSR_1")] {{Cdecl}}
                public static extern long Twin(long __self, nint a, nint __error);
            }
            """, plan: Plans(SelfErrorPlan("SBSR_1")));

        Assert.Contains(report.Violations, v => v.Rule == TransitionRule.R3 && v.Reason.Contains("ordinary argument"));
    }

    [Theory]
    [InlineData("SBSR_1", "long Twin(long a, nint __self, nint __error)", "Swift", "convention")]
    [InlineData("SBSR_9", "long Twin(long a, nint __self, nint __error)", "Cdecl", "no plan in")]
    [InlineData("SBSR_R", "long Twin(long a, nint __self, nint __error)", "Cdecl", "Reverse plan")]
    [InlineData("SBSR_1", "long Twin(long a, nint __self)", "Cdecl", "takes 2 argument(s)")]
    [InlineData("SBSR_1", "int Twin(long a, nint __self, nint __error)", "Cdecl", "returns")]
    [InlineData("SBSR_1", "long Twin(double a, nint __self, nint __error)", "Cdecl", "as argument 0")]
    [InlineData("SBSR_I", "void Twin(uint a)", "Cdecl", "as argument 0")]
    public void R3_TwinThatIsNotItsPlan_Fires(string symbol, string signature, string convention, string reason)
    {
        var callConv = convention == "Swift" ? Swift : Cdecl;
        var report = Scan($$"""
            public static class C
            {
                [DllImport("lib", EntryPoint = "{{symbol}}")] {{callConv}}
                public static extern {{signature}};
            }
            """, plan: Plans(SelfErrorPlan("SBSR_1"), ReversePlan("SBSR_R"), SignedIntPlan("SBSR_I")));

        var v = Assert.Single(report.Violations, v => v.Rule == TransitionRule.R3);
        Assert.Contains(reason, v.Reason);
        Assert.False(report.Passed);
    }

    [Fact]
    public void R3_TwinWithNoPlanSource_Fires()
    {
        var report = Scan($$"""
            public static class C
            {
                [DllImport("lib", EntryPoint = "SBSR_1")] {{Cdecl}}
                public static extern long Twin(long a, nint __self, nint __error);
            }
            """);

        Assert.Contains("no plan source", Assert.Single(report.Violations).Reason);
    }

    [Fact]
    public void R3_PlanSourceThatDoesNotRead_FailsEveryTwin()
    {
        var plan = Plans();
        File.WriteAllText(Path.Combine(plan.Location, "Bad" + BridgeTwinPlans.SidecarSuffix), """{ "planVersion": 2, "plans": [] }""");
        var report = Scan($$"""
            public static class C
            {
                [DllImport("lib", EntryPoint = "SBSR_1")] {{Cdecl}}
                public static extern long Twin(long a, nint __self, nint __error);
            }
            """, plan: plan);

        Assert.Contains("does not read", Assert.Single(report.Violations).Reason);
    }

    [Fact]
    public void BridgeTwinPlans_TwoModulesHoldingOnePlanSymbolWithDifferentBodies_IsAProblem()
    {
        var plans = BridgeTwinPlans.FromSidecars(new[]
        {
            ("A" + BridgeTwinPlans.SidecarSuffix, Sidecar(SelfErrorPlan("SBSR_1"))),
            ("B" + BridgeTwinPlans.SidecarSuffix, Sidecar(SelfErrorPlan("SBSR_1"))),
        });
        Assert.Empty(plans.Problems);
        Assert.Equal(1, plans.Count);

        var differing = BridgeTwinPlans.FromSidecars(new[]
        {
            ("A" + BridgeTwinPlans.SidecarSuffix, Sidecar(SelfErrorPlan("SBSR_1"))),
            ("B" + BridgeTwinPlans.SidecarSuffix, Sidecar(SignedIntPlan("SBSR_1"))),
        });
        Assert.Contains(differing.Problems, p => p.Contains("differs", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, true, null)]
    [InlineData(false, false, null)]
    [InlineData(true, false, "kept its direct import: parameter x has a default value")]
    [InlineData(false, true, "does not prove executable")]
    public void BridgeTwinPlans_RoutingGaps_AreTransitionsWhoseBridgingDisagreesWithTheirEvidence(bool executable, bool bridged, string? gap)
    {
        var sidecar = JsonNode.Parse(Sidecar(SelfErrorPlan("SBSR_1")))!.AsObject();
        var entry = new JsonObject { ["member"] = "M::PInvoke_f", ["symbol"] = "SBSR_1", ["executable"] = executable };
        if (bridged)
            entry["bridged"] = true;
        if (executable && !bridged)
            entry["bridgeRefusal"] = "parameter x has a default value";
        sidecar["coverage"] = new JsonArray(entry);

        var plans = BridgeTwinPlans.FromSidecars(new[] { ("A" + BridgeTwinPlans.SidecarSuffix, sidecar.ToJsonString()) });

        Assert.Empty(plans.Problems);
        if (gap is null)
            Assert.Empty(plans.RoutingGaps);
        else
            Assert.Contains(gap, Assert.Single(plans.RoutingGaps));
    }

    public static TheoryData<string, bool, string?, bool> EvidenceGapCases => new()
    {
        // A forward plan the evidence does not name (the evidence predates its code) is a gap.
        { "Forward", false, "swiftc shows no $sTarget on arm64-apple-ios15.0 under Apple Swift version 6.2.4", true },
        // So is one the evidence contradicts.
        { "Forward", false, "swiftc gives swiftcc i64 (ptr) on arm64-apple-ios15.0 under X; the plan predicts swiftcc void (ptr)", true },
        // A generation given no evidence at all (a lane that does not bridge) is not.
        { "Forward", false, BridgeTwinPlans.NoEvidenceReason, false },
        // Neither is an executable plan, nor a reverse or thick one, which swiftc never defines.
        { "Forward", true, "swiftc gives swiftcc void (ptr) on 3 triple(s) under 2 toolchain(s)", false },
        { "Reverse", false, "a closure invocation has no swiftc definition to compare: its plan has no evidence", false },
        { "ForwardThick", false, "a closure invocation has no swiftc definition to compare: its plan has no evidence", false },
        // A planned entry with no recorded evidence is not judged.
        { "Forward", false, null, false },
    };

    [Theory]
    [MemberData(nameof(EvidenceGapCases))]
    public void BridgeTwinPlans_EvidenceGaps_ArePlannedForwardTransitionsTheEvidenceDidNotMakeExecutable(
        string kind, bool executable, string? evidence, bool gap)
    {
        var sidecar = JsonNode.Parse(Sidecar(SelfErrorPlan("SBSR_1") with { Kind = kind }))!.AsObject();
        var entry = new JsonObject
        {
            ["member"] = "M::PInvoke_f", ["status"] = "Planned", ["symbol"] = "SBSR_1",
            ["executable"] = executable, ["bridged"] = executable,
        };
        if (evidence is not null)
            entry["evidence"] = evidence;
        sidecar["coverage"] = new JsonArray(entry);

        var plans = BridgeTwinPlans.FromSidecars(new[] { ("A" + BridgeTwinPlans.SidecarSuffix, sidecar.ToJsonString()) });

        Assert.Empty(plans.Problems);
        Assert.Empty(plans.RoutingGaps);
        if (gap)
            Assert.Contains(evidence!, Assert.Single(plans.EvidenceGaps));
        else
            Assert.Empty(plans.EvidenceGaps);
    }

    [Fact]
    public void BridgeTwinPlans_EvidenceGaps_IgnoreTransitionsThatWereNotPlanned()
    {
        var sidecar = JsonNode.Parse(Sidecar(SelfErrorPlan("SBSR_1")))!.AsObject();
        sidecar["coverage"] = new JsonArray(
            new JsonObject { ["member"] = "M::PInvoke_r", ["status"] = "Refused", ["executable"] = false, ["evidence"] = "anything" },
            new JsonObject { ["member"] = "M::PInvoke_n", ["status"] = "NotBridged", ["executable"] = false });

        var plans = BridgeTwinPlans.FromSidecars(new[] { ("A" + BridgeTwinPlans.SidecarSuffix, sidecar.ToJsonString()) });

        Assert.Empty(plans.EvidenceGaps);
    }

    [Fact]
    public void BridgeTwinPlans_NoEvidenceReason_IsTheOneTheGeneratorRecords() =>
        Assert.Equal(SwiftCallPlanSet.NoEvidenceReason, BridgeTwinPlans.NoEvidenceReason);

    [Theory]
    [InlineData("General", 8, false, "isize", true)]
    [InlineData("General", 8, false, "ptr", true)]
    [InlineData("General", 4, true, "i32", true)]
    [InlineData("General", 4, true, "u32", false)]
    [InlineData("General", 1, false, "bool", true)]
    [InlineData("General", 1, true, "bool", false)]
    [InlineData("General", 2, false, "char", true)]
    [InlineData("Float", 4, false, "f64", false)]
    [InlineData("Float", 8, false, "f64", true)]
    public void TwinSlot_AllowsTheCarriersOfItsSizeAndSignedness(string @class, int size, bool signed, string carrier, bool allowed) =>
        Assert.Equal(allowed, new TwinSlot(@class, size, signed).Carriers.Contains(carrier));

    // A plan: (role, class, size, signed) per argument, and the scalar result, if any.
    internal sealed record PlanSpec(string Symbol, string Kind, (string Role, string Class, int Size, bool Signed)[] Arguments,
        (string Class, int Size, bool Signed)? Result);

    internal static PlanSpec SelfErrorPlan(string symbol) => new(symbol, "Forward",
        new[] { ("Ordinary", "General", 8, true), ("Self", "General", 8, false), ("Error", "General", 8, false) }, ("General", 8, true));

    internal static PlanSpec ReversePlan(string symbol) => SelfErrorPlan(symbol) with { Kind = "Reverse" };

    internal static PlanSpec SignedIntPlan(string symbol) => new(symbol, "Forward", new[] { ("Ordinary", "General", 4, true) }, null);

    internal static string Sidecar(params PlanSpec[] plans)
    {
        var array = new JsonArray();
        foreach (var p in plans)
        {
            var arguments = new JsonArray();
            foreach (var a in p.Arguments)
                arguments.Add((JsonNode)new JsonObject { ["role"] = a.Role, ["class"] = a.Class, ["size"] = a.Size, ["signed"] = a.Signed });
            var result = p.Result is { } r
                ? new JsonObject { ["kind"] = "Scalar", ["class"] = r.Class, ["size"] = r.Size, ["signed"] = r.Signed }
                : new JsonObject { ["kind"] = "Void" };
            array.Add((JsonNode)new JsonObject
            {
                ["symbol"] = p.Symbol,
                ["body"] = new JsonObject
                {
                    ["kind"] = p.Kind,
                    ["managed"] = new JsonObject { ["arguments"] = arguments, ["result"] = result },
                },
            });
        }
        return new JsonObject { ["planVersion"] = BridgeTwinPlans.PlanVersion, ["plans"] = array }.ToJsonString();
    }

    /// <summary>A plan source: a fresh directory holding one sidecar of <paramref name="plans"/>.</summary>
    internal static TransitionPlanSource Plans(params PlanSpec[] plans)
    {
        var directory = Path.Combine(Path.GetTempPath(), "swift-transition-plans", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Fixture" + BridgeTwinPlans.SidecarSuffix), Sidecar(plans));
        return new TransitionPlanSource(TransitionPlanSourceKind.CallPlanSidecars, directory);
    }

    // ---- negative control -------------------------------------------------------------------------

    [Fact]
    public void NegativeControl_OnlySafeTransitions_PassesWithANonVacuousInventory()
    {
        var report = Scan($$"""
            public static unsafe class C
            {
                [DllImport("lib", EntryPoint = "$sMa")] {{Swift}}
                public static extern Swift.Runtime.TypeMetadata Accessor(Swift.Runtime.TypeMetadataRequest request);

                [DllImport("lib", EntryPoint = "$sprim")] {{Swift}}
                public static extern double Prim(long a, nint b, nuint c, float d, byte e, ushort f, void* g, Small h,
                    Swift.Runtime.ProtocolWitnessTable w, Swift.UnsafeMutablePointer<Pair> p, Swift.UnsafeRawPointer r);

                public static long ViaLocal(long a)
                {
                    return __PInvoke(a);

                    [DllImport("lib", EntryPoint = "$slocal")] {{Swift}}
                    static extern long __PInvoke(long a);
                }

                [DllImport("lib", EntryPoint = "SBSR_0123", CallingConvention = CallingConvention.Cdecl)]
                public static extern long Twin(long a, nint __self, nint __error);

                [DllImport("lib", EntryPoint = "cerror", CallingConvention = CallingConvention.Cdecl)]
                public static extern void WithError(SwiftError* error);

                [DllImport("lib", EntryPoint = "plain")]
                public static extern int Plain(Pair p);

                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                public static void Reverse(void* context, long n, SwiftError* error) { }

                public static long CallSwift(nint p, long a) => ((delegate* unmanaged[Swift]<long, long>)p)(a);

                public static void CallCdecl(nint p, Pair a) => ((delegate* unmanaged[Cdecl]<Pair, void>)p)(a);

                public static long CallManaged(delegate*<long, long> p) => p(1);
            }
            """, references: new[] { File.ReadAllBytes(RuntimeDll) }, plan: Plans(SelfErrorPlan("SBSR_0123")));

        Assert.True(report.Passed, string.Join("\n", report.Violations.Select(v => $"{v.Rule} {v.Member}: {v.Reason}")
            .Concat(report.InspectionErrors.Select(e => $"{e.Member}: {e.Message}"))));
        var i = report.Inventory;
        Assert.Equal(3, i.SwiftPInvokes);
        Assert.Equal(3, i.SwiftPInvokesPrimitiveOnly);
        Assert.Equal(3, i.NonSwiftPInvokes);
        Assert.Equal(1, i.NonSwiftPInvokesWithSwiftErrorPointer);
        Assert.Equal(1, i.BridgeTwins);
        Assert.Equal(1, i.NonSwiftUnmanagedCallersOnly);
        Assert.Equal(1, i.SwiftCalliSites);
        Assert.Equal(1, i.NonSwiftUnmanagedCalliSites);
        Assert.Equal(1, i.ManagedCalliSites);
        Assert.True(i.MethodBodies > 0 && i.Instructions > 0);
        // Every transition is listed, managed calli excepted; none carries a rule.
        Assert.Equal(9, report.Sites.Length);
        Assert.All(report.Sites, s => Assert.Empty(s.Rules));
        Assert.Contains(report.Sites, s => s.Member == "C::ViaLocal" && s.EntryPoint == "$slocal");
    }

    // ---- inventory export and report -------------------------------------------------------------

    [Fact]
    public void Exports_ListEverySiteWithItsRules_AndTheReportRecordsThePlanSource()
    {
        var plan = Plans();
        var report = Scan($$"""
            public static class C
            {
                [DllImport("lib", EntryPoint = "agg")] {{Swift}}
                public static extern long Agg(Pair p);
                [DllImport("lib", EntryPoint = "agg2")] {{Swift}}
                public static extern long Agg(Pair p, long n);
                [DllImport("lib", EntryPoint = "ok")] {{Swift}}
                public static extern long Ok(long n);
            }
            """, plan: plan);

        using var inventory = JsonDocument.Parse(SwiftTransitionReport.ToInventoryJson(new[] { report }));
        var sites = inventory.RootElement.GetProperty("assemblies")[0].GetProperty("sites").EnumerateArray().ToList();
        Assert.Equal(3, sites.Count);
        var overloads = sites.Where(s => s.GetProperty("member").GetString() == "C::Agg").ToList();
        Assert.Equal(2, overloads.Count);
        Assert.NotEqual(overloads[0].GetProperty("token").GetString(), overloads[1].GetProperty("token").GetString());
        Assert.All(overloads, s => Assert.Equal("R1", Assert.Single(s.GetProperty("rules").EnumerateArray()).GetString()));
        var ok = sites.Single(s => s.GetProperty("member").GetString() == "C::Ok");
        Assert.Empty(ok.GetProperty("rules").EnumerateArray());
        Assert.Equal("Swift", ok.GetProperty("convention").GetString());
        Assert.Equal("PInvoke", ok.GetProperty("kind").GetString());

        using var json = JsonDocument.Parse(SwiftTransitionReport.ToJson(new[] { report }));
        var root = json.RootElement;
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.Equal(2, root.GetProperty("totals").GetProperty("R1").GetInt32());
        Assert.Equal(new[] { "R1", "R2", "R3", "R4", "R5", "inspectionErrors" },
            root.GetProperty("totals").EnumerateObject().Select(p => p.Name));
        var source = root.GetProperty("assemblies")[0].GetProperty("planSource");
        Assert.Equal("CallPlanSidecars", source.GetProperty("kind").GetString());
        Assert.Equal(plan.Location, source.GetProperty("location").GetString());
        Assert.Contains("R1 [PInvoke] C::Agg -> agg", SwiftTransitionReport.ToSummary(new[] { report }));
    }

    [Theory]
    [InlineData("<Get>g____PInvoke|12_0", "Get")]
    [InlineData("<Outer>g__Local|3_1", "Outer.Local")]
    [InlineData("<ClassCallback_Get>b__0", "ClassCallback_Get.<lambda>")]
    [InlineData("PInvoke_getMetadata", "PInvoke_getMetadata")]
    [InlineData("<>c", "<>c")]
    public void DisplayMethodName_NamesTheDeclarationAReaderCanFind(string raw, string expected) =>
        Assert.Equal(expected, SwiftTransitionScanner.DisplayMethodName(raw));

    // ---- helpers ---------------------------------------------------------------------------------

    internal static SwiftTransitionScanOptions Options(IReadOnlyList<byte[]>? images = null, TransitionPlanSource? plan = null) => new()
    {
        ReferenceDirectories = new[] { System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), AppContext.BaseDirectory },
        ReferenceImages = images ?? Array.Empty<byte[]>(),
        PlanSource = plan,
    };

    private static AssemblyTransitionReport Scan(string body, IReadOnlyList<byte[]>? references = null,
        bool giveScannerTheReferences = true, TransitionPlanSource? plan = null)
    {
        var image = Compile(Preamble + body, "Fixture", references);
        return SwiftTransitionScanner.Scan(image, "Fixture.dll",
            Options(giveScannerTheReferences ? references : null, plan));
    }

    internal static byte[] Compile(string source, string assemblyName, IReadOnlyList<byte[]>? references = null)
    {
        // Framework assemblies only: the test's own Swift.Runtime must not shadow a fixture's.
        var frameworkDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p) &&
                        p.StartsWith(frameworkDir, StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var extra = (references ?? Array.Empty<byte[]>()).Select(b => (MetadataReference)MetadataReference.CreateFromImage(b));
        var compilation = CSharpCompilation.Create(assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            tpa.Concat(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                optimizationLevel: OptimizationLevel.Release));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }
}
