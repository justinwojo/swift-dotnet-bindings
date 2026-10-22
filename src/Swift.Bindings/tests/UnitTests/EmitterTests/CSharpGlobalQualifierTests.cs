// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The file-boundary pass that spells every external name in generated C# from <c>global::</c>, so a
/// bound member of the same name cannot capture it.
/// </summary>
public class CSharpGlobalQualifierTests
{
    private static readonly HashSet<string> Roots = CSharpGlobalQualifier.BuildRoots(new[] { "MyModule" });

    private static string Qualify(string source) => CSharpGlobalQualifier.Qualify(source, Roots);

    [Fact]
    public void NamespaceRootedChains_AreSpelledFromGlobal()
    {
        var output = Qualify("""
            namespace MyModule;
            public class Host
            {
                public System.IntPtr P => System.IntPtr.Zero;
                public Swift.Runtime.TypeMetadata M => default;
                public MyModule.Other O => default!;
            }
            public class Other { }
            """);

        Assert.Contains("public global::System.IntPtr P => global::System.IntPtr.Zero;", output);
        Assert.Contains("public global::Swift.Runtime.TypeMetadata M", output);
        Assert.Contains("public global::MyModule.Other O", output);
        Assert.Contains("namespace MyModule;", output);
    }

    [Fact]
    public void BareImportedTypeNames_AreSpelledWithTheirNamespace()
    {
        var output = Qualify("""
            namespace MyModule;
            public class Host
            {
                public IntPtr P => IntPtr.Zero;
                public Task<int> T() => Task.FromResult(1);
                public void K() => GC.KeepAlive(this);
            }
            """);

        Assert.Contains("public global::System.IntPtr P => global::System.IntPtr.Zero;", output);
        Assert.Contains("public global::System.Threading.Tasks.Task<int> T() => global::System.Threading.Tasks.Task.FromResult(1);", output);
        Assert.Contains("global::System.GC.KeepAlive(this)", output);
    }

    [Fact]
    public void BareRuntimeTypeNames_AreSpelledWithTheirNamespace()
    {
        var output = Qualify("""
            namespace MyModule;
            public class Host
            {
                public SwiftString S => default!;
                public TypeMetadata M => TypeMetadata.Zero;
            }
            """);

        Assert.Contains("public global::Swift.SwiftString S", output);
        Assert.Contains("public global::Swift.Runtime.TypeMetadata M => global::Swift.Runtime.TypeMetadata.Zero;", output);
    }

    [Fact]
    public void OwnTypeOfTheSameNameAndArity_KeepsTheBareName()
    {
        var output = Qualify("""
            namespace MyModule;
            public class Task { }
            public class Host
            {
                public Task Own() => new Task();
                public Task<int> Bcl() => default!;
            }
            """);

        Assert.Contains("public Task Own() => new Task();", output);
        Assert.Contains("public global::System.Threading.Tasks.Task<int> Bcl()", output);
    }

    [Fact]
    public void NestedTypeOfAnEnclosingType_KeepsTheBareName_OnlyInsideIt()
    {
        var output = Qualify("""
            namespace MyModule;
            public class Host
            {
                public struct Action { }
                public Action Own() => new Action();
            }
            public class Other
            {
                public Action Bcl() => () => { };
            }
            """);

        Assert.Contains("public Action Own() => new Action();", output);
        Assert.Contains("public global::System.Action Bcl()", output);
    }

    /// <summary>
    /// A nested type inherited from a base class declared elsewhere in the module is found by member
    /// lookup before any namespace, so a bare reference to it stays bare — here as the base of a
    /// subclass's own nested type of the same Swift name, projected under another C# name.
    /// </summary>
    [Fact]
    public void NestedTypeInheritedFromABaseClass_KeepsTheBareName()
    {
        const string source = """
            namespace MyModule
            {
                public class Player
                {
                    public class Queue { }
                }
                public class AppPlayer : Player
                {
                    public class QueueInfo : Queue { }
                    public Queue Make() => new Queue();
                }
                public class Unrelated
                {
                    public Queue<int> Bcl() => default!;
                }
            }
            """;

        var output = Qualify(source);

        Assert.Contains("public class QueueInfo : Queue { }", output);
        Assert.Contains("public Queue Make() => new Queue();", output);
        Assert.Contains("public global::System.Collections.Generic.Queue<int> Bcl()", output);
        Assert.Empty(CompileErrors(output.Replace("public global::System.Collections.Generic.Queue<int> Bcl() => default!;", "")));
    }

    /// <summary>
    /// The same inheritance across files: each output file is qualified on its own, and the base
    /// class usually lives in another file than its subclass, so the module's type hierarchy is
    /// what tells the pass the bare name is inherited.
    /// </summary>
    [Fact]
    public void NestedTypeInheritedFromABaseClassInAnotherFile_KeepsTheBareName()
    {
        const string baseFile = """
            namespace MyModule;
            public class Player
            {
                public class Queue { }
            }
            """;
        const string subclassFile = """
            namespace MyModule;
            public class AppPlayer : MyModule.Player
            {
                public class QueueInfo : Queue { }
                public Queue Make() => new Queue();
            }
            """;
        var hierarchy = CSharpGlobalQualifier.TypeHierarchy.Build(baseFile, subclassFile);
        var declared = CSharpGlobalQualifier.CollectDeclaredTypeNames(baseFile + "\n" + subclassFile);

        var output = CSharpGlobalQualifier.Qualify(subclassFile, Roots, declared,
            collisionNamespace: null, collisionNestedTypeNames: null, journal: null, hierarchy);

        Assert.Contains("public class QueueInfo : Queue { }", output);
        Assert.Contains("public Queue Make() => new Queue();", output);
        Assert.Contains("public class AppPlayer : global::MyModule.Player", output);
    }

    [Fact]
    public void LocalsParametersAndTypeParameters_AreNotQualified()
    {
        var output = Qualify("""
            namespace MyModule;
            public class Host<Span>
            {
                public int M(int Math) { var GC = 1; return Math + GC; }
                public Span Echo(Span value) => value;
            }
            """);

        Assert.Contains("return Math + GC;", output);
        Assert.Contains("public Span Echo(Span value)", output);
    }

    [Fact]
    public void MemberNames_InvokedMethods_AndLabels_AreNotQualified()
    {
        var output = Qualify("""
            namespace MyModule;
            public class Host
            {
                public nint Index(int i) => Index((nint)i);
                public nint Index(nint i) => i;
                public object Build() => new Holder { Action = null, Task = 1 };
                public void Call() => Take(Action: 1);
                public void Take(int Action) { }
                public int Read(Holder h) => h.Task;
            }
            public class Holder { public object? Action; public int Task; }
            """);

        Assert.Contains("public nint Index(int i) => Index((nint)i);", output);
        Assert.Contains("new Holder { Action = null, Task = 1 }", output);
        Assert.Contains("Take(Action: 1)", output);
        Assert.Contains("h.Task", output);
    }

    [Fact]
    public void AttributeNames_ResolveThroughTheAttributeSuffix()
    {
        var output = Qualify("""
            namespace MyModule;
            public class Host
            {
                [Obsolete("gone")]
                [EditorBrowsable(EditorBrowsableState.Never)]
                public void M() { }
            }
            """);

        Assert.Contains("[global::System.Obsolete(\"gone\")]", output);
        Assert.Contains("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]", output);
    }

    [Fact]
    public void BareReferenceEquals_IsSpelledOnObject()
    {
        var output = Qualify("""
            namespace MyModule;
            public class Host
            {
                public bool Same(object a) => ReferenceEquals(a, this);
            }
            """);

        Assert.Contains("=> object.ReferenceEquals(a, this);", output);
    }

    [Fact]
    public void StringsCommentsUsingsAndGlobalNames_AreLeftAlone()
    {
        var source = """
            using MyModule.SwiftInterop;
            namespace MyModule;
            /// <summary>Returns an IntPtr from System.IntPtr.</summary>
            public class Host
            {
                // IntPtr in a comment
                public string S => "IntPtr System.IntPtr";
                public global::System.IntPtr P => global::System.IntPtr.Zero;
            }
            """;

        var output = Qualify(source);

        Assert.Contains("using MyModule.SwiftInterop;", output);
        Assert.Contains("/// <summary>Returns an IntPtr from System.IntPtr.</summary>", output);
        Assert.Contains("// IntPtr in a comment", output);
        Assert.Contains("\"IntPtr System.IntPtr\"", output);
        Assert.DoesNotContain("global::global::", output);
    }

    [Fact]
    public void BothArmsOfAConditionalBlock_AreQualified()
    {
        var output = Qualify("""
            namespace MyModule;
            public class Host
            {
            #if __IOS__
                public IntPtr A => IntPtr.Zero;
            #else
                public UIntPtr B => UIntPtr.Zero;
            #endif
            }
            """);

        Assert.Contains("public global::System.IntPtr A => global::System.IntPtr.Zero;", output);
        Assert.Contains("public global::System.UIntPtr B => global::System.UIntPtr.Zero;", output);
    }

    [Fact]
    public void Qualify_IsIdempotent()
    {
        var once = Qualify("""
            namespace MyModule;
            public class Host { public IntPtr P => System.IntPtr.Zero; public bool E(object a) => ReferenceEquals(a, null); }
            """);

        Assert.Equal(once, Qualify(once));
    }

    [Fact]
    public void Journal_RecordsEachInsertion()
    {
        var source = "namespace MyModule;\npublic class Host { public IntPtr P => System.IntPtr.Zero; }\n";
        var journal = new TextEditJournal();

        var output = CSharpGlobalQualifier.Qualify(source, Roots,
            CSharpGlobalQualifier.CollectDeclaredTypeNames(source), collisionNamespace: null,
            collisionNestedTypeNames: null, journal);

        Assert.Equal(output.Length - source.Length, journal.Edits.Sum(e => e.NewLength - e.OldLength));
        Assert.All(journal.Edits, e => Assert.Equal(0, e.OldLength));
        Assert.Equal(2, journal.Edits.Count);
    }

    [Theory]
    [InlineData("ISwiftObject", "IExistentialBoxable")]
    [InlineData("Swift.Runtime.ISwiftObject", "Swift.Runtime.IExistentialBoxable")]
    [InlineData("global::Swift.Runtime.ISwiftObject", "global::Swift.Runtime.IExistentialBoxable")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only dynamically compiled interface dispatch probe, retained in full.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test-only dynamically compiled interface dispatch probe, retained in full.")]
    public void RuntimeExplicitInterfaces_HaveCanonicalMetadataAndPreserveDispatch(string swiftObject, string boxable)
    {
        var source = $$"""
            using Swift.Runtime;
            namespace MyModule;
            public class Host : {{swiftObject}}, {{boxable}}
            {
                public class __SwiftBindingsISwiftObject { }
                public class __SwiftBindingsISwiftObject1 { }
                public class __SwiftBindingsIExistentialBoxable { }
                public int Swift => 1;
                static int {{swiftObject}}.GetTypeMetadata() => 17;
                static int {{swiftObject}}.GetProtocolConformanceDescriptor<T>() => 19;
                static global::Swift.Runtime.ISwiftObject {{swiftObject}}.NewFromPayload(int value) => new Host();
                static int {{swiftObject}}.PayloadConstructionSemantics => 31;
                int {{swiftObject}}.SwiftHandle => 29;
                int {{swiftObject}}.MarshalToSwift() => 23;
                int {{boxable}}.BoxAsExistential1<T>() => 37;
            }
            public static class Probe
            {
                private static int StaticDispatch<T>() where T : global::Swift.Runtime.ISwiftObject
                    => T.GetTypeMetadata() + T.GetProtocolConformanceDescriptor<object>()
                        + T.PayloadConstructionSemantics + T.NewFromPayload(0).SwiftHandle;
                public static int Run()
                {
                    global::Swift.Runtime.ISwiftObject value = new Host();
                    return StaticDispatch<Host>() + value.SwiftHandle + value.MarshalToSwift()
                        + ((global::Swift.Runtime.IExistentialBoxable)value).BoxAsExistential1<object>();
                }
            }
            """;
        var journal = new TextEditJournal();
        var output = CSharpGlobalQualifier.Qualify(source, Roots,
            CSharpGlobalQualifier.CollectDeclaredTypeNames(source), null, null, journal);
        Assert.Equal(output, Qualify(output));
        Assert.Equal(output.Length - source.Length, journal.Edits.Sum(e => e.NewLength - e.OldLength));
        Assert.Equal(output.IndexOf("public static int Run()", StringComparison.Ordinal),
            journal.MapOffset(source.IndexOf("public static int Run()", StringComparison.Ordinal)));

        const string contracts = """
            namespace Swift.Runtime;
            public interface ISwiftObject
            {
                static abstract int GetTypeMetadata();
                static abstract int GetProtocolConformanceDescriptor<T>() where T : class;
                static abstract ISwiftObject NewFromPayload(int value);
                static abstract int PayloadConstructionSemantics { get; }
                int SwiftHandle { get; }
                int MarshalToSwift();
            }
            public interface IExistentialBoxable { int BoxAsExistential1<T>() where T : class; }
            """;
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll")
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("ExplicitInterface" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(contracts), CSharpSyntaxTree.ParseText(output) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        stream.Position = 0;
        using (var pe = new PEReader(stream, PEStreamOptions.LeaveOpen))
        {
            var reader = pe.GetMetadataReader();
            var host = reader.TypeDefinitions.Select(reader.GetTypeDefinition)
                .Single(t => reader.GetString(t.Name) == "Host");
            var methods = host.GetMethods().Select(reader.GetMethodDefinition)
                .Select(m => reader.GetString(m.Name)).Where(n => n.StartsWith("Swift.Runtime.", StringComparison.Ordinal)).ToArray();
            Assert.Equal(7, methods.Length);
            Assert.Contains("Swift.Runtime.ISwiftObject.GetTypeMetadata", methods);
            Assert.Contains("Swift.Runtime.ISwiftObject.GetProtocolConformanceDescriptor", methods);
            Assert.Contains("Swift.Runtime.ISwiftObject.NewFromPayload", methods);
            Assert.Contains("Swift.Runtime.ISwiftObject.get_PayloadConstructionSemantics", methods);
            Assert.Contains("Swift.Runtime.ISwiftObject.get_SwiftHandle", methods);
            Assert.Contains("Swift.Runtime.ISwiftObject.MarshalToSwift", methods);
            Assert.Contains("Swift.Runtime.IExistentialBoxable.BoxAsExistential1", methods);
        }
        var assembly = Assembly.Load(stream.ToArray());
        var run = assembly.GetType("MyModule.Probe")!.GetMethod("Run")!.CreateDelegate<Func<int>>();
        Assert.Equal(185, run());
    }

    [Fact]
    public void RuntimeExplicitInterfaces_QualifyBothConditionalBranches()
    {
        var source = """
            using Swift.Runtime;
            namespace MyModule;
            #if __IOS__
            class Host { int ISwiftObject.SwiftHandle => 1; }
            #else
            class Host { int global::Swift.Runtime.ISwiftObject.SwiftHandle => 2; }
            class __SwiftBindingsISwiftObject { }
            #endif
            """;
        var output = Qualify(source);
        var tree = CSharpSyntaxTree.ParseText(output);
        var alias = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax>()
            .Single(u => u.Alias != null).Alias!.Name.Identifier.ValueText;
        Assert.NotEqual("__SwiftBindingsISwiftObject", alias);
        Assert.Contains($"int {alias}.SwiftHandle => 1;", output);
        Assert.Contains($"int {alias}.SwiftHandle => 2;", output);
        Assert.Contains($"using {alias} = global::Swift.Runtime.ISwiftObject;", output);
        Assert.Equal(output, Qualify(output));
    }

    [Fact]
    public void RuntimeExplicitInterfaces_LeaveModuleOwnedLookalikesAlone()
    {
        const string source = """
            namespace MyModule;
            public interface ISwiftObject { int Value { get; } }
            public class Host : ISwiftObject { int ISwiftObject.Value => 1; }
            """;
        var output = Qualify(source);
        Assert.Equal(source, output);
        Assert.Empty(CompileErrors(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("extern alias External;\n")]
    public void RuntimeExplicitInterfaces_PreserveDirectiveAndExternAliasOrdering(string externAlias)
    {
        var source = "// header\n#define ENABLED\n#undef DISABLED\n#nullable enable\n" + externAlias + """
            using Swift.Runtime;
            #if ENABLED
            namespace MyModule;
            class Host { int ISwiftObject.SwiftHandle => 1; }
            #endif
            """;
        var journal = new TextEditJournal();
        var output = CSharpGlobalQualifier.Qualify(source, Roots,
            CSharpGlobalQualifier.CollectDeclaredTypeNames(source), null, null, journal);
        var tree = CSharpSyntaxTree.ParseText(output);
        Assert.DoesNotContain(tree.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var alias = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax>()
            .Single(u => u.Alias != null);
        Assert.True(alias.SpanStart > output.IndexOf("#undef DISABLED", StringComparison.Ordinal));
        if (externAlias.Length > 0)
            Assert.True(alias.SpanStart > output.IndexOf(externAlias.Trim(), StringComparison.Ordinal));
        Assert.True(alias.SpanStart < output.IndexOf("#if ENABLED", StringComparison.Ordinal));
        Assert.StartsWith("// header\n#define ENABLED\n#undef DISABLED\n#nullable enable\n", output);
        Assert.Equal(output, Qualify(output));
        Assert.Equal(output.Length - source.Length, journal.Edits.Sum(e => e.NewLength - e.OldLength));
        Assert.Equal(output.IndexOf("class Host", StringComparison.Ordinal),
            journal.MapOffset(source.IndexOf("class Host", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The capture itself: a bound member named after a namespace root and a property named after
    /// an imported type both capture the bare spellings, and the qualified output compiles.
    /// </summary>
    [Fact]
    public void QualifiedOutput_CompilesWhereBoundMembersCaptureTheBareNames()
    {
        const string source = """
            namespace MyModule
            {
                public class Host
                {
                    public int System => 1;
                    public int IntPtr => 2;
                    public int GC => 3;
                    public object Read() => System.IntPtr.Zero;
                    public object Zero() => IntPtr.Zero;
                    public void Keep() => GC.KeepAlive(this);
                }
            }
            """;

        Assert.NotEmpty(CompileErrors(source));
        Assert.Empty(CompileErrors(Qualify(source)));
    }

    /// <summary>
    /// The embedded name table lists every public top-level type of the runtime's formerly imported
    /// namespaces under the namespace that declares it, so a runtime type added later cannot be
    /// written bare without the table knowing where it lives.
    /// </summary>
    [Fact]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Test-only enumeration of the referenced runtime assembly, which the test host loads in full.")]
    public void ImportedTypeNames_CoverTheRuntimeNamespaces()
    {
        var table = CSharpGlobalQualifier.ImportedTypeNamespaces;
        var runtimeNamespaces = new[] { "Swift", "Swift.Runtime", "Swift.Runtime.InteropServices" };
        var missing = typeof(Swift.Runtime.TypeMetadata).Assembly.GetExportedTypes()
            .Where(t => !t.IsNested && t.Namespace is { } ns && runtimeNamespaces.Contains(ns))
            .Select(t => (Name: t.Name.Split('`')[0], t.Namespace))
            .Distinct()
            .Where(t => !table.TryGetValue(t.Name, out var ns) || ns != t.Namespace)
            .Select(t => $"{t.Namespace}.{t.Name}")
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void ImportedTypeNames_PlaceCoreLibraryTypesInTheirNamespaces()
    {
        var table = CSharpGlobalQualifier.ImportedTypeNamespaces;
        foreach (var type in new[] { typeof(IntPtr), typeof(System.Runtime.InteropServices.GCHandle),
                     typeof(System.Runtime.CompilerServices.Unsafe), typeof(System.Threading.Tasks.Task),
                     typeof(System.Runtime.InteropServices.Swift.SwiftSelf), typeof(List<>), typeof(Enumerable) })
        {
            Assert.Equal(type.Namespace, table[type.Name.Split('`')[0]]);
        }
    }

    private static List<Diagnostic> CompileErrors(string source)
    {
        var tpa = (string)(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "");
        var references = tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll")
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("capture",
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    }
}
