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
                public static extern void Twin(void* args, void* result, SwiftError* error);

                [DllImport("lib", EntryPoint = "plain")]
                public static extern int Plain(Pair p);

                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
                public static void Reverse(void* context, long n, SwiftError* error) { }

                public static long CallSwift(nint p, long a) => ((delegate* unmanaged[Swift]<long, long>)p)(a);

                public static void CallCdecl(nint p, Pair a) => ((delegate* unmanaged[Cdecl]<Pair, void>)p)(a);

                public static long CallManaged(delegate*<long, long> p) => p(1);
            }
            """, references: new[] { File.ReadAllBytes(RuntimeDll) });

        Assert.True(report.Passed, string.Join("\n", report.Violations.Select(v => $"{v.Rule} {v.Member}: {v.Reason}")
            .Concat(report.InspectionErrors.Select(e => $"{e.Member}: {e.Message}"))));
        var i = report.Inventory;
        Assert.Equal(3, i.SwiftPInvokes);
        Assert.Equal(3, i.SwiftPInvokesPrimitiveOnly);
        Assert.Equal(2, i.NonSwiftPInvokes);
        Assert.Equal(1, i.NonSwiftPInvokesWithSwiftErrorPointer);
        Assert.Equal(1, i.BridgeTwins);
        Assert.Equal(1, i.NonSwiftUnmanagedCallersOnly);
        Assert.Equal(1, i.SwiftCalliSites);
        Assert.Equal(1, i.NonSwiftUnmanagedCalliSites);
        Assert.Equal(1, i.ManagedCalliSites);
        Assert.True(i.MethodBodies > 0 && i.Instructions > 0);
        // Every transition is listed, managed calli excepted; none carries a rule.
        Assert.Equal(8, report.Sites.Length);
        Assert.All(report.Sites, s => Assert.Empty(s.Rules));
        Assert.Contains(report.Sites, s => s.Member == "C::ViaLocal" && s.EntryPoint == "$slocal");
    }

    // ---- inventory export and report -------------------------------------------------------------

    [Fact]
    public void Exports_ListEverySiteWithItsRules_AndTheReportRecordsThePlanSource()
    {
        var plan = new TransitionPlanSource(TransitionPlanSourceKind.BuildReport, "/tree/binding-report.json");
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
        Assert.Equal(new[] { "R1", "R2", "R4", "R5", "inspectionErrors" },
            root.GetProperty("totals").EnumerateObject().Select(p => p.Name));
        var source = root.GetProperty("assemblies")[0].GetProperty("planSource");
        Assert.Equal("BuildReport", source.GetProperty("kind").GetString());
        Assert.Equal("/tree/binding-report.json", source.GetProperty("location").GetString());
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
