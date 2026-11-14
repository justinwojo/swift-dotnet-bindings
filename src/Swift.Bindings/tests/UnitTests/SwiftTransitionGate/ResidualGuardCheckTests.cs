// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Swift.Runtime;
using SwiftBindings.TransitionGate;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The residual-guard check (<see cref="ResidualGuardCheck"/>) over small assemblies compiled here,
/// in Debug and Release: each rule goes red on the shape it names (the negative controls), and each
/// guarded form the generator writes passes (the positive controls), so the gate can neither pass an
/// unguarded known-unsafe transition nor fail a correctly guarded one.
/// </summary>
public class ResidualGuardCheckTests
{
    private const string Preamble = """
        using System;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Swift;
        using System.Threading.Tasks;
        using Swift.Runtime;
        [assembly: DisableRuntimeMarshalling]

        internal static unsafe class Native
        {
            // Self in the self register, after an ordinary argument: SelfRegister | LoweredSignature.
            [DllImport("lib", EntryPoint = "f")] [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
            internal static extern long F(long a, SwiftSelf self);
        }

        """;

    /// <summary>SelfRegister | LoweredSignature, the shapes of <c>Native.F</c>.</summary>
    private const ulong Shapes = SwiftTransitionShapes.SelfRegister | SwiftTransitionShapes.LoweredSignature;

    private const string Guard = "SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)5, \"member\");";
    private const string Call = "Native.F(1, default)";

    private static readonly string RuntimeDll = Path.Combine(AppContext.BaseDirectory, "Swift.Runtime.dll");

    public static TheoryData<OptimizationLevel> Levels => new() { OptimizationLevel.Debug, OptimizationLevel.Release };

    // ---- positive controls ----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Levels))]
    public void AGuardedHolder_AndAGuardedRootReachingIt_Pass(OptimizationLevel level)
    {
        var result = Check($$"""
            public sealed class C
            {
                private static long Inner() { {{Guard}} return {{Call}}; }
                private static long Between() => Inner();
                public long Outer() { {{Guard}} return Between() + 1; }
                internal long Hidden() => Between();
                public long Unrelated() => 2;
            }
            """, level);

        AssertPassed(result);
        Assert.Equal(Shapes, Assert.Single(result.Hazards).Shapes);
        Assert.Equal(["C::Inner()", "C::Outer()"], result.Holders.Select(h => h.Member).Order(StringComparer.Ordinal));
        Assert.Equal(2, result.GuardCalls);
    }

    /// <summary>
    /// A state machine's guard sits after the compiler's dispatch to its resume points, which the
    /// first entry, in the initial state, falls through: one await (a test of the state), several (a
    /// switch on it), and an iterator, whose first entry runs in another state.
    /// </summary>
    [Theory]
    [InlineData(OptimizationLevel.Debug, 1)]
    [InlineData(OptimizationLevel.Release, 1)]
    [InlineData(OptimizationLevel.Debug, 3)]
    [InlineData(OptimizationLevel.Release, 3)]
    public void AnAsyncMethod_GuardedFirstInItsStateMachine_Passes(OptimizationLevel level, int awaits)
    {
        var result = Check($$"""
            public sealed class C
            {
                public async Task<long> RunAsync(long value)
                {
                    {{Guard}}
                    {{string.Concat(Enumerable.Repeat("await Task.Yield(); value++; ", awaits))}}
                    return {{Call}} + value;
                }
            }
            """, level);

        AssertPassed(result);
        Assert.Equal("C::RunAsync(long)", Assert.Single(result.Holders).Member);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void AnIterator_GuardedFirstInItsStateMachine_Passes(OptimizationLevel level)
    {
        var result = Check($$"""
            public sealed class C
            {
                public global::System.Collections.Generic.IEnumerable<long> Values()
                {
                    {{Guard}}
                    yield return {{Call}};
                    yield return {{Call}} + 1;
                }
            }
            """, level);

        AssertPassed(result);
        Assert.Equal("C::Values()", Assert.Single(result.Holders).Member);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void ALambda_IsCoveredByTheGuardOfTheMemberThatCreatesIt_EvenAfterItsClosureIsAllocated(OptimizationLevel level)
    {
        var result = Check($$"""
            public sealed class C
            {
                public Func<long> Make() { {{Guard}} return () => {{Call}}; }
                public Func<long> Capture(long value) { {{Guard}} return () => {{Call}} + value; }
            }
            """, level);

        AssertPassed(result);
        Assert.Equal(["C::Capture(long)", "C::Make()"], result.Holders.Select(h => h.Member).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void ACallThroughAVirtualOrADelegateToADeclaredMethod_IsABoundary(OptimizationLevel level)
    {
        var result = Check($$"""
            public class Base { public virtual long Run() => 0; }
            public sealed class Derived : Base { public override long Run() { {{Guard}} return {{Call}}; } }
            public static class Caller
            {
                private static long Helper() { {{Guard}} return {{Call}}; }
                public static long ThroughVirtual(Base value) => value.Run();
                public static Func<long> ThroughDelegate() => Helper;
            }
            """, level);

        AssertPassed(result);
        Assert.Equal(["Caller::Helper()", "Derived::Run()"], result.Holders.Select(h => h.Member).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A <c>LibraryImport</c> stub is the declared method around a local <c>extern</c> function named
    /// <c>__PInvoke</c>: the stub is part of its import, so the guard belongs to the stub's callers,
    /// and a stub code outside can call is exposed like the import itself.
    /// </summary>
    [Theory]
    [InlineData("internal", null)]
    [InlineData("public", ResidualGuardCheck.ExposedRule)]
    public void ALibraryImportStub_IsPartOfItsImport(string access, string? rule)
    {
        var result = Check($$"""
            public static class Stubbed
            {
                {{access}} static long F(long a, SwiftSelf self)
                {
                    return __PInvoke(a, self);

                    [DllImport("lib", EntryPoint = "g")] [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                    static extern long __PInvoke(long a, SwiftSelf self);
                }
            }
            public sealed class C { public long Run() { {{Guard}} return Stubbed.F(1, default); } }
            """);

        Assert.Equal("C::Run()", Assert.Single(result.Holders).Member);
        if (rule is null)
            AssertPassed(result);
        else
            AssertViolation(result, rule, "Stubbed::F(long, SwiftSelf)");
    }

    [Fact]
    public void AnImportNothingCalls_IsUnreferenced_AndNeedsNoGuard()
    {
        var result = Check("public sealed class C { public long Two() => 2; }");

        AssertPassed(result);
        Assert.False(Assert.Single(result.Hazards).Referenced);
        Assert.Empty(result.Holders);
    }

    [Fact]
    public void AGuardRefusingNothing_IsARoutedGuard_AndPasses()
    {
        var result = Check("public sealed class C { public long Two() { SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)0, \"C.Two()\"); return 2; } }");

        AssertPassed(result);
        Assert.Equal(1, result.RoutedGuardCalls);
    }

    // ---- negative controls ----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Levels))]
    public void AHolderWithNoGuard_IsUnguarded(OptimizationLevel level)
    {
        var result = Check($"public sealed class C {{ private static long Inner() => {Call}; public long Outer() {{ {Guard} return Inner(); }} }}", level);
        AssertViolation(result, ResidualGuardCheck.UnguardedRule, "C::Inner()");
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void ARootWithNoGuard_IsUnguarded_EvenWhenTheHolderIsGuarded(OptimizationLevel level)
    {
        var result = Check($"public sealed class C {{ private static long Inner() {{ {Guard} return {Call}; }} public long Outer() => Inner(); }}", level);
        AssertViolation(result, ResidualGuardCheck.UnguardedRule, "C::Outer()");
    }

    [Fact]
    public void AGuardMissingAShape_IsUncovered()
    {
        var result = Check($$"""public sealed class C { public long Run() { SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)4, "C.Run()"); return {{Call}}; } }""");
        AssertViolation(result, ResidualGuardCheck.UncoveredRule, "C::Run()");
    }

    [Theory]
    [InlineData("Other(); " + Guard)]
    [InlineData("Marshal.FreeHGlobal(Marshal.AllocHGlobal(8)); " + Guard)]
    [InlineData("if (Environment.TickCount == 3) return 0; " + Guard)]
    [InlineData("try { " + Guard + " } finally { Other(); }")]
    public void AGuardAfterACallABranchOrInsideATry_IsLate(string prologue)
    {
        var result = Check($$"""
            public sealed class C
            {
                private static void Other() { }
                public long Run() { {{prologue}} return {{Call}}; }
            }
            """);
        AssertViolation(result, ResidualGuardCheck.LateRule, "C::Run()");
    }

    /// <summary>
    /// In a state machine, only the dispatch on its own state may branch ahead of the guard: a branch
    /// the method itself makes there can jump past the guard to the transition.
    /// </summary>
    [Theory]
    [MemberData(nameof(Levels))]
    public void AnAsyncMethod_BranchingPastItsGuard_IsLate(OptimizationLevel level)
    {
        var result = Check($$"""
            public sealed class C
            {
                public async Task<long> RunAsync(bool bypass)
                {
                    if (bypass) goto Resume;
                    {{Guard}}
                    Resume:
                    await Task.Yield();
                    return {{Call}};
                }
            }
            """, level);
        AssertViolation(result, ResidualGuardCheck.LateRule, "C::RunAsync(bool)");
    }

    /// <summary>A local written through its address is not known, so a branch that tests it is one.</summary>
    [Theory]
    [MemberData(nameof(Levels))]
    public void ABranchOnALocalWrittenThroughAnAlias_IsLate(OptimizationLevel level)
    {
        var result = Check($$"""
            public sealed class C
            {
                public long Run(int bypass)
                {
                    int x = 0;
                    ref int alias = ref x;
                    alias = bypass;
                    if (x != 0) goto Native;
                    {{Guard}}
                    Native:
                    return {{Call}};
                }
            }
            """, level);
        AssertViolation(result, ResidualGuardCheck.LateRule, "C::Run(int)");
    }

    [Theory]
    [InlineData("private static readonly long s_value = " + Call + ";")]
    [InlineData("static C() { _ = " + Call + "; }")]
    [InlineData("~C() { _ = " + Call + "; }")]
    public void ATypeInitializerOrFinalizerReachingATransition_IsAnInitializerViolation(string member)
    {
        var result = Check($"public sealed class C {{ {member} }}");
        Assert.Contains(result.Violations, v => v.Rule == ResidualGuardCheck.InitializerRule);
    }

    [Fact]
    public void AnImportCodeOutsideCanCall_IsExposed()
    {
        var result = Check("""
            public static class Open
            {
                [DllImport("lib", EntryPoint = "g")] [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                public static extern long G(long a, SwiftSelf self);
                public static long Run() { SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)5, "Open.Run()"); return G(1, default); }
            }
            """);
        AssertViolation(result, ResidualGuardCheck.ExposedRule, "Open::G(long, SwiftSelf)");
    }

    [Fact]
    public void AGuardWithShapesThatAreNotAConstant_IsMalformed()
    {
        var result = Check($$"""
            public sealed class C
            {
                public long Run(SwiftTransitionFacts facts) { SwiftTransitionGuard.ThrowIfUnsupported(facts, "C.Run()"); return {{Call}}; }
            }
            """);
        Assert.Contains(result.Violations, v => v.Rule == ResidualGuardCheck.MalformedRule);
    }

    [Theory]
    [InlineData("public long Two() { " + Guard + " return 2; }", "C::Two()")]
    [InlineData("public long Run() { SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)7, \"C.Run()\"); return " + Call + "; }", "C::Run()")]
    public void AGuardRefusingAShapeNothingNeeds_IsSuperfluous(string member, string display)
    {
        var result = Check($"public sealed class C {{ {member} }}");
        AssertViolation(result, ResidualGuardCheck.SuperfluousRule, display);
    }

    // ---- Swift-convention callbacks ------------------------------------------------------------

    /// <summary>A Swift-convention callback and the static field the closure emitters store its address in.</summary>
    private const string Callback = """
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            private static unsafe void Body_Callback(void* arg0, SwiftSelf context) { }
            private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_Body_Callback = &Body_Callback;
        """;

    private const string CallbackGuard = "SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)0x10000, \"member\");";

    [Theory]
    [MemberData(nameof(Levels))]
    public void AMemberHandingSwiftACallbackFromItsField_IsAHolder_AndPassesGuarded(OptimizationLevel level)
    {
        var result = Check($$"""
            public sealed unsafe class Factory
            {
                {{Callback}}
                public static nint Make() { {{CallbackGuard}} return (nint)s_Body_Callback; }
                public static nint Unrelated() => 2;
            }
            """, level);

        AssertPassed(result);
        var hazard = Assert.Single(result.Hazards, h => h.Referenced);
        Assert.Equal(TransitionSiteKind.UnmanagedCallersOnly, hazard.Kind);
        Assert.Equal("Factory::Body_Callback", hazard.Member);
        Assert.Equal(SwiftTransitionShapes.ReverseCallback, hazard.Shapes);
        // The type initializer that stores the address is not a holder; the member reading it is.
        Assert.Equal(["Factory::Make()"], result.Holders.Select(h => h.Member));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void AnUnguardedCallbackHandOff_IsUnguarded(OptimizationLevel level)
    {
        var result = Check($$"""
            public sealed unsafe class Factory
            {
                {{Callback}}
                public static nint Make() => (nint)s_Body_Callback;
            }
            """, level);

        AssertViolation(result, ResidualGuardCheck.UnguardedRule, "Factory::Make()");
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void ACallbackAddressTakenInAMember_MakesThatMemberTheHolder(OptimizationLevel level)
    {
        var result = Check($$"""
            public sealed unsafe class Factory
            {
                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
                private static void Body_Callback(SwiftSelf context) { }
                public static nint Make() { {{CallbackGuard}} return (nint)(delegate* unmanaged[Swift]<SwiftSelf, void>)&Body_Callback; }
            }
            """, level);

        AssertPassed(result);
        Assert.Equal(SwiftTransitionShapes.ReverseCallback, Assert.Single(result.Hazards, h => h.Referenced).Shapes);
        Assert.Equal(["Factory::Make()"], result.Holders.Select(h => h.Member));
    }

    [Fact]
    public void ACallbackGuardRefusingAForwardShape_DoesNotCoverTheCallback()
    {
        var result = Check($$"""
            public sealed unsafe class Factory
            {
                {{Callback}}
                public static nint Make() { {{Guard}} return (nint)s_Body_Callback; }
            }
            """);

        AssertViolation(result, ResidualGuardCheck.UncoveredRule, "Factory::Make()");
    }

    [Fact]
    public void ACallbackFieldReadByATypeInitializer_IsAnInitializerViolation()
    {
        var result = Check($$"""
            public sealed unsafe class Factory
            {
                {{Callback}}
                private static readonly nint s_copy = (nint)s_Body_Callback;
                public static nint Copy() => s_copy;
            }
            """);

        Assert.Contains(result.Violations, v => v.Rule == ResidualGuardCheck.InitializerRule && v.Member.StartsWith("Factory::.cctor", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSidecars_RecordACallbackAsAReverseCallbackTransition()
    {
        var result = Check($$"""
            public sealed unsafe class Factory
            {
                {{Callback}}
                public static nint Make() { {{CallbackGuard}} return (nint)s_Body_Callback; }
            }
            """);
        AssertPassed(result);
        var directory = Directory.CreateTempSubdirectory("residual-guards").FullName;
        try
        {
            Assert.Contains(ResidualGuardCheck.CrossCheckSidecars(result, directory, []), p => p.Contains("record 0", StringComparison.Ordinal));
            WriteSidecar(directory, ("ReverseCallback", "Factory::Body_Callback", "", SwiftTransitionShapes.ReverseCallback));
            Assert.Empty(ResidualGuardCheck.CrossCheckSidecars(result, directory, []));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- the inventory tripwire -----------------------------------------------------------------

    [Fact]
    public void TheSidecars_MustRecordEveryReferencedHazard_WithItsShapes_AndNothingElse()
    {
        var result = Check($"public sealed class C {{ public long Run() {{ {Guard} return {Call}; }} }}");
        AssertPassed(result);
        var directory = Directory.CreateTempSubdirectory("residual-guards").FullName;
        try
        {
            Assert.Contains(ResidualGuardCheck.CrossCheckSidecars(result, directory, []), p => p.Contains("record 0", StringComparison.Ordinal));

            WriteSidecar(directory, ("ForwardPInvoke", "Native::F", "f", Shapes));
            Assert.Empty(ResidualGuardCheck.CrossCheckSidecars(result, directory, []));

            WriteSidecar(directory, ("ForwardPInvoke", "Native::F", "f", SwiftTransitionShapes.LoweredSignature));
            Assert.Contains(ResidualGuardCheck.CrossCheckSidecars(result, directory, []), p => p.Contains("not the recorded ones", StringComparison.Ordinal));

            WriteSidecar(directory, ("ForwardPInvoke", "Native::F", "f", Shapes), ("ForwardPInvoke", "Native::Gone", "gone", Shapes));
            Assert.Contains(ResidualGuardCheck.CrossCheckSidecars(result, directory, []), p => p.Contains("the IL does not hold", StringComparison.Ordinal));

            // A hand-written type's hazards are not the sidecars' to record, but the type must still hold one.
            File.Delete(Path.Combine(directory, "M.swift-transition-guards.json"));
            Assert.Empty(ResidualGuardCheck.CrossCheckSidecars(result, directory, ["Native"]));
            Assert.Contains(ResidualGuardCheck.CrossCheckSidecars(result, directory, ["Native", "Elsewhere"]), p => p.Contains("Elsewhere", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- the two readings of a shape agree ------------------------------------------------------

    [Fact]
    public void TheShapeBits_AreTheRuntimesFacts()
    {
        Assert.Equal((ulong)SwiftTransitionFacts.SelfRegister, SwiftTransitionShapes.SelfRegister);
        Assert.Equal((ulong)SwiftTransitionFacts.SpecialBeforeArguments, SwiftTransitionShapes.SpecialBeforeArguments);
        Assert.Equal((ulong)SwiftTransitionFacts.LoweredSignature, SwiftTransitionShapes.LoweredSignature);
        Assert.Equal((ulong)SwiftTransitionFacts.FunctionPointerSelfRegister, SwiftTransitionShapes.FunctionPointerSelfRegister);
        Assert.Equal((ulong)SwiftTransitionFacts.FunctionPointerSpecialBeforeArguments, SwiftTransitionShapes.FunctionPointerSpecialBeforeArguments);
        Assert.Equal((ulong)SwiftTransitionFacts.FunctionPointerLoweredSignature, SwiftTransitionShapes.FunctionPointerLoweredSignature);
        Assert.Equal((ulong)SwiftTransitionFacts.ReverseCallback, SwiftTransitionShapes.ReverseCallback);
        // Every runtime fact has a shape the gate knows, so the gate never describes a guard's bit as unknown.
        var known = SwiftTransitionShapes.SelfRegister | SwiftTransitionShapes.SpecialBeforeArguments | SwiftTransitionShapes.LoweredSignature
            | SwiftTransitionShapes.FunctionPointerSelfRegister | SwiftTransitionShapes.FunctionPointerSpecialBeforeArguments | SwiftTransitionShapes.FunctionPointerLoweredSignature
            | SwiftTransitionShapes.ReverseCallback;
        Assert.Equal(known, Enum.GetValues<SwiftTransitionFacts>().Aggregate(0UL, (all, f) => all | (ulong)f));
    }

    /// <summary>The generator reads an import's shapes from its C# syntax; the gate reads them from the compiled IL. They must agree.</summary>
    [Theory]
    [InlineData("long a, SwiftSelf self", "long")]
    [InlineData("SwiftSelf self, long a", "long")]
    [InlineData("SwiftError* error, long a", "long")]
    [InlineData("ref SwiftError error, long a", "long")]
    [InlineData("long a, SwiftError* error", "long")]
    [InlineData("SwiftIndirectResult result, long a", "void")]
    [InlineData("long a, long b", "Pair")]
    [InlineData("Pair p", "void")]
    [InlineData("long a, nint b, void* c", "double")]
    public void TheGeneratorAndTheGate_ReadTheSameShapes(string parameters, string result)
    {
        var source = $$"""
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Swift;
            [assembly: DisableRuntimeMarshalling]
            namespace M
            {
                public struct Pair { public long A, B; }
                internal static unsafe class Native
                {
                    [UnmanagedCallConv(CallConvs = new System.Type[] { typeof(CallConvSwift) })]
                    [DllImport("lib", EntryPoint = "f")]
                    internal static extern {{result}} F({{parameters}});
                }
            }
            """;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var syntax = SwiftTransitionGuards.Discover(root, SwiftTransitionGuards.EnumsOf([root])).Select(d => (ulong)d.Hazard.Facts).SingleOrDefault();

        var image = Compile(source, OptimizationLevel.Release);
        var report = SwiftTransitionScanner.Scan(image, "Fixture.dll", Options());
        var il = report.Sites.Where(s => s.Convention == TransitionConvention.Swift).Select(s => s.Shapes).SingleOrDefault();

        Assert.Equal(syntax, il);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static void AssertPassed(ResidualGuardResult result) =>
        Assert.True(result.Passed, ResidualGuardCheck.ToSummary(result));

    private static void AssertViolation(ResidualGuardResult result, string rule, string member) =>
        Assert.True(result.Violations.Any(v => v.Rule == rule && v.Member == member), ResidualGuardCheck.ToSummary(result));

    private static void WriteSidecar(string directory, params (string Kind, string Member, string EntryPoint, ulong Facts)[] transitions)
    {
        var entries = transitions.Select(t => $$"""{ "kind": "{{t.Kind}}", "member": "{{t.Member}}", "entryPoint": "{{t.EntryPoint}}", "facts": {{t.Facts}} }""");
        File.WriteAllText(Path.Combine(directory, "M.swift-transition-guards.json"), $$"""{ "transitions": [ {{string.Join(", ", entries)}} ] }""");
    }

    private static SwiftTransitionScanOptions Options() => new()
    {
        ReferenceDirectories = new[] { System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), AppContext.BaseDirectory },
        ReferenceImages = new[] { File.ReadAllBytes(RuntimeDll) },
    };

    private static ResidualGuardResult Check(string body, OptimizationLevel level = OptimizationLevel.Release)
    {
        var image = Compile(Preamble + body, level);
        var report = SwiftTransitionScanner.Scan(image, "Fixture.dll", Options());
        Assert.Empty(report.InspectionErrors);
        return ResidualGuardCheck.Check(image, report);
    }

    private static byte[] Compile(string source, OptimizationLevel level)
    {
        // Framework assemblies only, plus the runtime the guards live in.
        var frameworkDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p) && p.StartsWith(frameworkDir, StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(RuntimeDll));
        var compilation = CSharpCompilation.Create("Fixture",
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, optimizationLevel: level));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }
}
