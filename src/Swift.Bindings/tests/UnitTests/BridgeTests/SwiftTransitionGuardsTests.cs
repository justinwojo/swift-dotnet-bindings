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
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// <see cref="SwiftTransitionGuards"/>: which direct Swift-convention transitions have a shape some
/// runtime mishandles, which members are guarded (each function that holds one, and each member
/// code outside the binding can call whose calls reach one), where the guard goes (first, before
/// anything the member does natively, including a constructor initializer's arguments), and the
/// guard class, which is a pure function of the C#.
/// </summary>
public sealed class SwiftTransitionGuardsTests
{
    private const string Import = """
        [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
        [global::System.Runtime.InteropServices.LibraryImport("MSwiftBindings", EntryPoint = "$sENTRY")]
        """;

    /// <summary>A module whose one import takes an untyped self, and whatever members <paramref name="members"/> declares.</summary>
    private static string Module(string members, string import = "internal static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);") => $$"""
        namespace M
        {
            internal static unsafe partial class Native
            {
                {{Import}}
                {{import}}
            }

            {{members}}
        }
        """;

    private const string CallNative = "Native.PInvoke_f(1, default)";

    private static SwiftTransitionGuards Plan(params string[] sources) => SwiftTransitionGuards.Plan("M", sources);

    private static SwiftTransitionGuardSite? Site(SwiftTransitionGuards guards, string display) =>
        guards.Sites.SingleOrDefault(s => s.Display == display);

    // ---- shapes ----

    [Theory]
    [InlineData("long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self", "long", SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.LoweredSignature)]
    [InlineData("global::System.Runtime.InteropServices.Swift.SwiftSelf self, long a", "long",
        SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.SpecialBeforeArguments | SwiftTransitionFacts.LoweredSignature)]
    [InlineData("global::System.Runtime.InteropServices.Swift.SwiftError* error, long a", "long", SwiftTransitionFacts.SpecialBeforeArguments | SwiftTransitionFacts.LoweredSignature)]
    // A trailing error is not special before the arguments.
    [InlineData("long a, global::System.Runtime.InteropServices.Swift.SwiftError* error", "long", SwiftTransitionFacts.LoweredSignature)]
    // A LibraryImport stub pins a ref error to the same register pointer.
    [InlineData("ref global::System.Runtime.InteropServices.Swift.SwiftError error, long a", "long", SwiftTransitionFacts.SpecialBeforeArguments | SwiftTransitionFacts.LoweredSignature)]
    [InlineData("global::System.Runtime.InteropServices.Swift.SwiftIndirectResult result, long a", "void", SwiftTransitionFacts.LoweredSignature)]
    [InlineData("long a, long b", "global::M.Pair", SwiftTransitionFacts.LoweredSignature)]
    [InlineData("long a, nint b", "double", SwiftTransitionFacts.None)]
    public void AnImportsShapes_AreReadFromItsSignature(string parameters, string result, SwiftTransitionFacts expected)
    {
        var source = Module("public struct Pair { public long A, B; }", $"internal static partial {result} PInvoke_f({parameters});");
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var found = SwiftTransitionGuards.Discover(root, SwiftTransitionGuards.EnumsOf([root]));
        if (expected == SwiftTransitionFacts.None)
            Assert.Empty(found);
        else
            Assert.Equal(expected, Assert.Single(found).Hazard.Facts);
    }

    [Fact]
    public void ACallThroughASwiftFunctionPointer_HasTheFunctionPointerShapes()
    {
        var source = """
            namespace M
            {
                public sealed unsafe class Box
                {
                    private nint _fn;
                    public long Get(nint context) =>
                        ((delegate* unmanaged[Swift]<global::System.Runtime.InteropServices.Swift.SwiftSelf, long>)_fn)(new global::System.Runtime.InteropServices.Swift.SwiftSelf((void*)context));
                }
            }
            """;
        var guards = Plan(source);
        var hazard = Assert.Single(guards.Hazards);
        Assert.Equal(SwiftTransitionKind.ForwardCalli, hazard.Kind);
        Assert.Equal(SwiftTransitionFacts.FunctionPointerSelfRegister | SwiftTransitionFacts.FunctionPointerLoweredSignature, hazard.Facts);
        Assert.NotNull(Site(guards, "Box.Get(nint)"));
    }

    // ---- Swift-convention callbacks ----

    /// <summary>A Swift-convention callback whose address a static field holds, the shape the closure emitters write.</summary>
    private const string SwiftCallback = """
            [global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
            private static unsafe void Body_Callback(void* arg0, global::System.Runtime.InteropServices.Swift.SwiftSelf context) { }
            private static unsafe readonly delegate* unmanaged[Swift]<void*, global::System.Runtime.InteropServices.Swift.SwiftSelf, void> s_Body_Callback = &Body_Callback;
        """;

    [Fact]
    public void AMemberHandingSwiftASwiftConventionCallback_IsGuardedForTheReverseCallback()
    {
        var guards = Plan($$"""
            namespace M
            {
                public sealed unsafe class Factory
                {
                    {{SwiftCallback}}
                    public static nint Make() => (nint)s_Body_Callback;
                    public static nint Unrelated() => 2;
                }
            }
            """);

        var hazard = Assert.Single(guards.Hazards);
        Assert.Equal(SwiftTransitionKind.ReverseCallback, hazard.Kind);
        Assert.Equal("M.Factory::Body_Callback", hazard.Member);
        Assert.Equal(SwiftTransitionFacts.ReverseCallback, hazard.Facts);
        // The field's initializer only takes the address; the member that reads it hands it to Swift.
        Assert.True(Site(guards, "Factory.Make()") is { Direct: true, Visible: true });
        Assert.Null(Site(guards, "Factory.Unrelated()"));
        Assert.Empty(guards.Issues);
    }

    [Fact]
    public void ACallbackAddressTakenInsideAMember_GuardsThatMember_AndItsPublicCallers()
    {
        var guards = Plan("""
            namespace M
            {
                public sealed unsafe class Factory
                {
                    [global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
                    private static void Body_Callback(global::System.Runtime.InteropServices.Swift.SwiftSelf context) { }
                    private static nint Address() => (nint)(delegate* unmanaged[Swift]<global::System.Runtime.InteropServices.Swift.SwiftSelf, void>)&Body_Callback;
                    public static nint Make() => Address();
                }
            }
            """);

        Assert.Equal(SwiftTransitionKind.ReverseCallback, Assert.Single(guards.Hazards).Kind);
        Assert.True(Site(guards, "Factory.Address()") is { Direct: true, Visible: false });
        Assert.True(Site(guards, "Factory.Make()") is { Direct: false, Visible: true });
        Assert.Empty(guards.Issues);
    }

    [Fact]
    public void ACdeclCallback_IsNoHazard()
    {
        var guards = Plan("""
            namespace M
            {
                public sealed unsafe class Factory
                {
                    [global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                    private static void Body_Callback(nint context) { }
                    private static readonly delegate* unmanaged[Cdecl]<nint, void> s_Body_Callback = &Body_Callback;
                    public static nint Make() => (nint)s_Body_Callback;
                }
            }
            """);

        Assert.Empty(guards.Hazards);
        Assert.False(guards.GuardsAny);
    }

    [Fact]
    public void ACallbackGuard_IsWrittenFirst_AndItsConstantHoldsTheReverseCallbackFact()
    {
        var source = $$"""
            namespace M
            {
                public sealed unsafe class Factory
                {
                    {{SwiftCallback}}
                    public static nint Make() => (nint)s_Body_Callback;
                }
            }
            """;
        var guards = Plan(source);
        var rewritten = guards.Rewrite(source);
        Assert.Contains("SwiftTransitionGuard.ThrowIfUnsupported(global::M.__SwiftBindingsTransitionGuards.", rewritten);

        var issues = new List<string>();
        var mask = Assert.Single(SwiftTransitionGuards.MasksOf([rewritten], [], issues));
        Assert.Equal(SwiftTransitionFacts.ReverseCallback, mask.Facts);
        Assert.Empty(issues);
        // Without the guard, the C# keeps a callback no guard names.
        SwiftTransitionGuards.MasksOf([source], [], issues);
        Assert.Contains(issues, i => i.Contains("M.Factory::Body_Callback", StringComparison.Ordinal));
    }

    [Fact]
    public void ACallbackFieldReadByATypeInitializer_IsAnIssue()
    {
        var guards = Plan($$"""
            namespace M
            {
                public sealed unsafe class Factory
                {
                    {{SwiftCallback}}
                    private static readonly nint s_copy = (nint)s_Body_Callback;
                }
            }
            """);

        Assert.Contains(guards.Issues, i => i.Contains("Body_Callback", StringComparison.Ordinal));
    }

    // ---- which members ----

    [Fact]
    public void TheFunctionThatCallsTheImport_AndEveryPublicMemberReachingIt_AreGuarded_ButNotThePrivateHelpersBetween()
    {
        var guards = Plan(Module($$"""
            public sealed class C
            {
                public long Value => Outer();
                private long Outer() => Inner();
                private long Inner() => {{CallNative}};
                internal long Hidden() => Inner();
                public long Unrelated() => 2;
            }
            """));

        Assert.True(Site(guards, "C.Inner()") is { Direct: true, Visible: false });
        Assert.True(Site(guards, "C.Value.get") is { Direct: false, Visible: true });
        Assert.Null(Site(guards, "C.Outer()"));
        Assert.Null(Site(guards, "C.Hidden()"));
        Assert.Null(Site(guards, "C.Unrelated()"));
        Assert.Empty(guards.Issues);
    }

    [Fact]
    public void ACallThroughAnInterfaceOrAVirtualMember_IsADispatchBoundary_TheImplementationGuardsItself()
    {
        var guards = Plan(Module($$"""
            public interface IThing { long Run(); }
            public sealed class Impl : IThing
            {
                private long Native_() => {{CallNative}};
                long IThing.Run() => Native_();
            }
            public class Base
            {
                public virtual long Run() => 0;
            }
            public class Derived : Base
            {
                private long Native_() => {{CallNative}};
                public override long Run() => Native_();
            }
            public static class Caller
            {
                public static long ThroughInterface(IThing thing) => thing.Run();
                public static long ThroughVirtual(Base value) => value.Run();
            }
            """));

        Assert.True(Site(guards, "Impl.IThing.Run()") is { Visible: true });
        Assert.True(Site(guards, "Derived.Run()") is { Visible: true });
        Assert.Null(Site(guards, "Caller.ThroughInterface(IThing)"));
        Assert.Null(Site(guards, "Caller.ThroughVirtual(Base)"));
    }

    [Fact]
    public void ABaseCall_IsDirect_SoItsCallerIsGuarded()
    {
        var guards = Plan(Module($$"""
            public class Base
            {
                public virtual long Run() => {{CallNative}};
            }
            public class Derived : Base
            {
                public override long Run() => base.Run() + 1;
            }
            """));

        Assert.NotNull(Site(guards, "Base.Run()"));
        Assert.NotNull(Site(guards, "Derived.Run()"));
    }

    [Fact]
    public void ALambdaOrLocalFunction_IsGuardedThroughTheMemberThatDeclaresIt()
    {
        var guards = Plan(Module($$"""
            public sealed class C
            {
                public global::System.Func<long> Make() => () => {{CallNative}};
                public long Local()
                {
                    long Run() => {{CallNative}};
                    return Run();
                }
            }
            """));

        Assert.True(Site(guards, "C.Make()") is { Direct: true });
        Assert.True(Site(guards, "C.Local()") is { Direct: true });
    }

    [Fact]
    public void ConstructorsPropertiesIndexersAndOperators_AreFollowed()
    {
        var guards = Plan(Module($$"""
            public sealed class Box
            {
                private readonly long _v;
                internal Box(long v) { _v = v + {{CallNative}}; }
                public static Box Make() => new Box(1);
                public long this[int i] => {{CallNative}};
                public long Read() => this[0];
                public static Box operator +(Box a, Box b) => new(a._v + b._v);
                public static Box Sum(Box a, Box b) => a + b;
                private long Prop { set { _ = {{CallNative}}; } }
                public void Write() => Prop = 3;
            }
            """));

        Assert.NotNull(Site(guards, "Box.Make()"));
        Assert.NotNull(Site(guards, "Box[int].get"));
        Assert.NotNull(Site(guards, "Box.Read()"));
        Assert.NotNull(Site(guards, "Box.operator +(Box, Box)"));
        Assert.NotNull(Site(guards, "Box.Sum(Box, Box)"));
        Assert.NotNull(Site(guards, "Box.Write()"));
        Assert.Empty(guards.Issues);
    }

    [Fact]
    public void ADerivedConstructorWithNoInitializer_RunsTheBaseConstructorImplicitly_AndIsGuarded()
    {
        var guards = Plan(Module($$"""
            public class Base
            {
                protected Base() { _ = {{CallNative}}; }
            }
            public class Derived : Base
            {
                public Derived(int x) { }
            }
            """));

        Assert.NotNull(Site(guards, "Base()"));
        Assert.NotNull(Site(guards, "Derived(int)"));
    }

    [Fact]
    public void AMethodGroupOrFunctionPointer_IsABoundary_SoItsCreatorIsNotGuarded_AndAFieldMayHoldIt()
    {
        var guards = Plan(Module($$"""
            public sealed unsafe class C
            {
                private static readonly global::System.Func<long> s_callback = Run;
                private static readonly delegate*<long> s_pointer = &Run;
                private static long Run() => {{CallNative}};
                public global::System.Func<long> Make() => Run;
                public nint Address() => (nint)(delegate*<long>)&Run;
                public long Call() => Run();
            }
            """));

        Assert.True(Site(guards, "C.Run()") is { Direct: true });
        Assert.Equal(["C.Call()", "C.Run()"], guards.Sites.Select(s => s.Display).Order(StringComparer.Ordinal));
        Assert.Empty(guards.Issues);
    }

    [Fact]
    public void AnOperationThatDidNotBind_ReachesOnlyAMemberOfAnOperandsType()
    {
        // Outside.* is not in the module, so it does not bind; nothing of it can run the module's members.
        var guards = Plan(Module($$"""
            public sealed class Box
            {
                private readonly long _v;
                public Box(long v) { _v = v + {{CallNative}}; }
                public static Box operator +(Box a, Box b) => new Box(a._v);
                public static explicit operator Box(long v) => new Box(v);
            }
            public sealed class Derived : global::Outside.Base
            {
                private static readonly long s_x = 1 + 2;
                private long _y;
                public Derived(long v) : base(v) { _y = v; }
                public object Unwrap(global::Outside.Thing t) => t.Get()!;
                public object Add(global::Outside.Thing a, global::Outside.Thing b) => a + b;
                public object Cast(global::Outside.Thing t) => (global::Outside.Other)t;
                public object Index(global::Outside.Thing t) => t[0];
            }
            public static class Caller
            {
                // Bound to Box on one side: may still pick Box's operator.
                public static object Mixed(Box a, global::Outside.Thing b) => a + b;
            }
            """));

        // Every guarded member is a Box member or reaches one through a Box operand; no Derived member is.
        Assert.Equal(
            ["Box(long)", "Box.explicit operator Box(long)", "Box.operator +(Box, Box)", "Caller.Mixed(Box, Thing)"],
            guards.Sites.Select(s => s.Display).Order(StringComparer.Ordinal));
        Assert.Empty(guards.Issues);
    }

    [Fact]
    public void AnUnboundConstructorCall_ReachesOnlyAConstructorOfItsTypeThatTakesItsArguments()
    {
        var guards = Plan(Module($$"""
            public sealed class Box
            {
                public Box(long v) { _ = {{CallNative}}; }
                public Box(long v, long w) { }
                public static Box One(global::Outside.Thing t) => new Box(t.Value);
                public static Box Two(global::Outside.Thing t) => new Box(t.Value, t.Value);
                public static object Other(global::Outside.Thing t) => new global::Outside.Thing(t.Value);
            }
            """));

        Assert.Equal(["Box(long)", "Box.One(Thing)"], guards.Sites.Select(s => s.Display).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("private static readonly long Field = Native.PInvoke_f(1, default);", "the initializer of Field")]
    [InlineData("static C() { _ = Native.PInvoke_f(1, default); }", "type initializer")]
    [InlineData("~C() { _ = Native.PInvoke_f(1, default); }", "finalizer")]
    public void ATransitionThatRunsBeforeAnyGuardCould_IsAnIssue(string member, string where)
    {
        var guards = Plan(Module($"public sealed class C {{ {member} }}"));
        Assert.Contains(guards.Issues, i => i.Contains(where, StringComparison.Ordinal));
    }

    [Fact]
    public void AnImportNothingCalls_GuardsNothing()
    {
        var guards = Plan(Module("public sealed class C { public long Two() => 2; }"));
        Assert.False(guards.GuardsAny);
        Assert.Empty(guards.Hazards);
    }

    // ---- where the guard goes ----

    [Fact]
    public void TheGuard_IsTheFirstStatement_OfEveryForm_AndTheRewrittenModuleCompiles()
    {
        var source = Module($$"""
            public sealed class C
            {
                private readonly long _v;
                public C(long v) : this(v, Native.PInvoke_f(v, default)) { }
                private C(long v, long w) { _v = v + w; }
                public C() { _v = {{CallNative}}; }
                public long Block() { var x = {{CallNative}}; return x; }
                public long Expression() => {{CallNative}};
                public void Void() => {{CallNative}};
                public long Property => {{CallNative}};
                public long Accessors { get => {{CallNative}}; set { _ = {{CallNative}}; } }
                public long Throws() => _v > 0 ? {{CallNative}} : throw new global::System.InvalidOperationException();
                public async global::System.Threading.Tasks.Task Async() { await global::System.Threading.Tasks.Task.Yield(); _ = {{CallNative}}; }
                public async global::System.Threading.Tasks.Task<long> AsyncValue() => await global::System.Threading.Tasks.Task.FromResult({{CallNative}});
                public global::System.Collections.Generic.IEnumerable<long> Iterate() { yield return {{CallNative}}; }
                public static C operator -(C a) => new(-a._v + {{CallNative}});
            }
            """);
        var guards = Plan(source);
        var rewritten = guards.Rewrite(source);

        Assert.Contains("public C(long v) : this(global::Swift.Runtime.SwiftTransitionGuard.EnsureSupported(", rewritten);
        Assert.Contains("public long Expression() { global::Swift.Runtime.SwiftTransitionGuard.ThrowIfUnsupported(", rewritten);
        Assert.Contains("public long Property { get { global::Swift.Runtime.SwiftTransitionGuard.ThrowIfUnsupported(", rewritten);
        Assert.Contains("\"C.Accessors.set\"", rewritten);
        Assert.Contains("\"C.Iterate()\"", rewritten);

        foreach (var site in guards.Sites)
            Assert.Contains($"\"{site.Display}\"", rewritten);

        var masks = SwiftTransitionGuards.RenderMasks("M", guards.Masks())!;
        AssertCompiles(rewritten, masks);
    }

    [Fact]
    public void AGuardedFile_IsGuardedAlike_WhetherRewrittenWholeOrAsItsOwnFile()
    {
        var first = Module($$"""public sealed class A { public long Run() => {{CallNative}}; }""");
        var second = """
            namespace M
            {
                public sealed class B { public long Run() => new A().Run(); }
            }
            """;
        var whole = Plan(first + "\n" + second);
        var files = new[] { first, second }.Select(f => whole.Rewrite(f, written: true)).ToList();

        whole.VerifyWritten();
        Assert.Contains("\"A.Run()\"", files[0]);
        Assert.Contains("\"B.Run()\"", files[1]);
    }

    [Fact]
    public void VerifyWritten_FailsWhenAGuardWasNeverWritten()
    {
        var source = Module($$"""public sealed class A { public long Run() => {{CallNative}}; }""");
        var guards = Plan(source);
        Assert.Throws<InvalidOperationException>(guards.VerifyWritten);
    }

    // ---- the guard class ----

    [Fact]
    public void TheGuardClass_IsAPureFunctionOfTheCSharp()
    {
        var source = Module($$"""public sealed class A { public long Run() => {{CallNative}}; }""");
        var guards = Plan(source);
        var rewritten = guards.Rewrite(source);
        var issues = new List<string>();

        var masks = SwiftTransitionGuards.MasksOf([rewritten], [], issues);

        Assert.Empty(issues);
        var mask = Assert.Single(masks);
        Assert.Equal(Assert.Single(guards.Hazards).Name, mask.Name);
        Assert.Equal(SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.LoweredSignature, mask.Facts);
        Assert.Equal(SwiftTransitionGuards.RenderMasks("M", guards.Masks()), SwiftTransitionGuards.RenderMasks("M", masks));
    }

    [Fact]
    public void ARoutedImport_KeepsItsConstant_AtNone()
    {
        var source = Module($$"""public sealed class A { public long Run() => {{CallNative}}; }""");
        var guards = Plan(source);
        var hazard = Assert.Single(guards.Hazards);
        // Routing replaces the import with a shim of the same name that is no Swift-convention import.
        var routed = guards.Rewrite(source)
            .Replace(Import, "", StringComparison.Ordinal)
            .Replace("internal static partial long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self);",
                "internal static long PInvoke_f(long a, global::System.Runtime.InteropServices.Swift.SwiftSelf self) => a;", StringComparison.Ordinal);
        var issues = new List<string>();

        var masks = SwiftTransitionGuards.MasksOf([routed], [(hazard.Member, hazard.EntryPoint)], issues);

        Assert.Empty(issues);
        Assert.Equal(SwiftTransitionFacts.None, Assert.Single(masks).Facts);
    }

    [Fact]
    public void AnImportCalledWithoutAGuard_OrAGuardNamingNothing_IsAnIssue()
    {
        var unguarded = Module($$"""public sealed class A { public long Run() => {{CallNative}}; }""");
        var issues = new List<string>();
        SwiftTransitionGuards.MasksOf([unguarded], [], issues);
        Assert.Contains(issues, i => i.Contains("no guard names", StringComparison.Ordinal));

        var stray = """
            namespace M { public sealed class A { public void Run() { global::Swift.Runtime.SwiftTransitionGuard.ThrowIfUnsupported(global::M.__SwiftBindingsTransitionGuards.Gone_00000000, "A.Run()"); } } }
            """;
        issues.Clear();
        SwiftTransitionGuards.MasksOf([stray], [], issues);
        Assert.Contains(issues, i => i.Contains("neither declares nor routes", StringComparison.Ordinal));
    }

    [Fact]
    public void TheUnion_IsAtLeastAsRestrictiveAsEither_AndTheClassRoundTrips()
    {
        var full = new[] { new SwiftTransitionGuards.Mask("A_1", SwiftTransitionFacts.SelfRegister, "a"), new SwiftTransitionGuards.Mask("B_2", SwiftTransitionFacts.None, "b") };
        var routed = new[] { new SwiftTransitionGuards.Mask("A_1", SwiftTransitionFacts.None, "a"), new SwiftTransitionGuards.Mask("B_2", SwiftTransitionFacts.LoweredSignature, "b") };

        var union = SwiftTransitionGuards.Union(full, routed).ToDictionary(m => m.Name, m => m.Facts);

        Assert.Equal(SwiftTransitionFacts.SelfRegister, union["A_1"]);
        Assert.Equal(SwiftTransitionFacts.LoweredSignature, union["B_2"]);
        var text = SwiftTransitionGuards.RenderMasks("M", full)!;
        Assert.Equal(full.Select(m => (m.Name, m.Facts)), SwiftTransitionGuards.ParseMasks(text).Select(m => (m.Name, m.Facts)));
        Assert.Null(SwiftTransitionGuards.RenderMasks("M", []));
    }

    private static void AssertCompiles(params string[] sources)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll" or "Swift.Runtime.dll")
            .Select(p => MetadataReference.CreateFromFile(p));
        // LibraryImport is source-generated; the partial declarations are made extern for this check.
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.Replace("static partial long PInvoke_f", "static extern long PInvoke_f", StringComparison.Ordinal)
            .Replace("[global::System.Runtime.InteropServices.LibraryImport(", "[global::System.Runtime.InteropServices.DllImport(", StringComparison.Ordinal)));
        var compilation = CSharpCompilation.Create("Guarded", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }
}
