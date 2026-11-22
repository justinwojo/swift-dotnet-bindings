// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A simple enum's floored case held in an optional, array, set, dictionary or tuple parameter is
/// written straight into Swift memory by the container the runtime builds, with no raw-value
/// conversion and no wrapper switch to refuse it. The parameter's value is therefore walked before
/// it is marshalled and every enum in it passes through the enum's <c>Require</c>. These tests
/// compile the emitted walk against a stand-in <c>Require</c> and run it.
/// </summary>
[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only dynamically compiled probe, retained in full.")]
[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test-only dynamically compiled probe, retained in full.")]
public class ContainedSimpleEnumRefusalTests
{
    private const string Module = "TestModule";

    private static readonly NamedTypeSpec Mode = new($"{Module}.Mode");
    private static readonly NamedTypeSpec Int32 = new("Swift.Int32");
    private static readonly NamedTypeSpec SwiftString = new("Swift.String");

    [Fact]
    public void FlooredCaseInsideAnyContainer_IsRefused_AndAvailableCasesPass()
    {
        var shapes = new (string Name, TypeSpec Spec, string CSharpType, string Refused, string Passed)[]
        {
            ("Optional", Optional(Mode), "Mode?", "Mode.Future", "Mode.Standard"),
            ("Array", Generic("Swift.Array", Mode), "IEnumerable<Mode>", "new[] { Mode.Standard, Mode.Future }", "new[] { Mode.Standard }"),
            ("Set", Generic("Swift.Set", Mode), "IReadOnlySet<Mode>", "new HashSet<Mode> { Mode.Future }", "new HashSet<Mode> { Mode.Standard }"),
            ("Keys", Generic("Swift.Dictionary", Mode, Int32), "IDictionary<Mode, int>",
                "new Dictionary<Mode, int> { [Mode.Future] = 1 }", "new Dictionary<Mode, int> { [Mode.Standard] = 1 }"),
            ("Values", Generic("Swift.Dictionary", SwiftString, Mode), "IDictionary<string, Mode>",
                "new Dictionary<string, Mode> { [\"a\"] = Mode.Future }", "new Dictionary<string, Mode> { [\"a\"] = Mode.Standard }"),
            ("Tuple", new TupleTypeSpec(new TypeSpec[] { Int32, Mode }), "(int, Mode)", "(1, Mode.Future)", "(1, Mode.Standard)"),
            ("Nested", Generic("Swift.Array", Optional(Generic("Swift.Array", Mode))), "IEnumerable<IReadOnlyList<Mode>?>",
                "new IReadOnlyList<Mode>?[] { null, new[] { Mode.Future } }", "new IReadOnlyList<Mode>?[] { null, new[] { Mode.Standard } }"),
        };
        var methods = new StringBuilder();
        foreach (var shape in shapes)
        {
            methods.AppendLine($"public static void {shape.Name}({shape.CSharpType} value) {{");
            Assert.True(Emit(shape.Spec, "value", out var walk), shape.Name);
            methods.AppendLine(walk);
            // The marshalling after the walk reads the parameter as non-null; the walk must leave
            // it so, or the binding fails to compile (CS8602/CS8604 are warnings-as-errors there).
            methods.AppendLine("_ = value.GetHashCode();");
            methods.AppendLine("}");
            methods.AppendLine($"public static void {shape.Name}Refused() => {shape.Name}({shape.Refused});");
            methods.AppendLine($"public static void {shape.Name}Passed() => {shape.Name}({shape.Passed});");
        }
        var probe = Compile(methods.ToString());

        foreach (var shape in shapes)
        {
            var refused = Assert.Throws<TargetInvocationException>(() => probe.GetMethod($"{shape.Name}Refused")!.Invoke(null, null));
            Assert.IsType<PlatformNotSupportedException>(refused.InnerException);
            probe.GetMethod($"{shape.Name}Passed")!.Invoke(null, null);
        }
    }

    /// <summary>
    /// A sequence that is not a collection is materialized before the walk, so the walk and the
    /// container built after it read the same elements from one enumeration. Without it a one-shot
    /// sequence would reach the container empty, and a lazily computed one would run twice.
    /// </summary>
    [Fact]
    public void LazySequence_IsEnumeratedOnce_AndTheContainerReadsTheSameElements()
    {
        Assert.True(Emit(Generic("Swift.Array", Mode), "modes", out var walk));
        var probe = Compile($$"""
            public static int Enumerations;
            private static IEnumerable<Mode> Lazy() { Enumerations++; yield return Mode.Standard; yield return Mode.Standard; }
            public static int Run() {
                IEnumerable<Mode> modes = Lazy();
                {{walk}}
                return Enumerable.Count(modes) * 10 + Enumerations;
            }
            """);

        Assert.Equal(21, probe.GetMethod("Run")!.Invoke(null, null));
    }

    /// <summary>
    /// An array of arrays arrives as a sequence of sequences, so each inner sequence is made a
    /// collection too: one that yields an available case to the walk and a floored one to the
    /// container read after it would otherwise reach Swift unrefused.
    /// </summary>
    [Fact]
    public void LazyInnerSequence_IsEnumeratedOnce_SoTheWalkSeesWhatTheContainerReads()
    {
        Assert.True(Emit(Generic("Swift.Array", Generic("Swift.Array", Mode)), "groups", out var walk));
        var probe = Compile($$"""
            public static int Enumerations;
            // Standard on the first enumeration, Future on every later one.
            private static IEnumerable<Mode> Shifting() { yield return Enumerations++ == 0 ? Mode.Standard : Mode.Future; }
            public static int Run() {
                IEnumerable<IEnumerable<Mode>> groups = new[] { Shifting() };
                {{walk}}
                return Enumerable.Count(Enumerable.SelectMany(groups, g => g), m => m == Mode.Future) * 10 + Enumerations;
            }
            """);

        Assert.Equal(1, probe.GetMethod("Run")!.Invoke(null, null));
    }

    /// <summary>
    /// Nothing is written for a type with no guarded enum in a container: a bare enum is refused
    /// where it is converted, and a container of an unguarded enum needs no walk.
    /// </summary>
    [Fact]
    public void NoContainedGuardedEnum_WritesNothing()
    {
        Assert.False(Emit(Mode, "mode", out _));
        Assert.False(Emit(Generic("Swift.Array", new NamedTypeSpec($"{Module}.Plain")), "plain", out _));
        Assert.False(Emit(Generic("Swift.Dictionary", SwiftString, Int32), "counts", out _));

        Assert.True(Emit(Generic("Swift.Array", Mode), "modes", out var byValueWalk));
        Assert.Contains("global::System.Linq.Enumerable.ToList(modesFloorSnapshot)", byValueWalk);
        Assert.DoesNotContain("modes is not null", byValueWalk);
    }

    /// <summary>
    /// A lazily computed sequence anywhere the walk reads one (a dictionary's values or keys, a
    /// set's elements, the arrays in an array of dictionaries, an <c>inout</c> array) is read once:
    /// the sequence yields an available case first and a floored one on every later read, so a
    /// second read by the container would hand Swift the floored case unrefused.
    /// </summary>
    [Theory]
    [InlineData("Values", "IDictionary<string, IEnumerable<Mode>>", "new Dictionary<string, IEnumerable<Mode>> { [\"a\"] = Shifting() }",
        "Enumerable.SelectMany(groups.Values, g => g)")]
    [InlineData("Keys", "IDictionary<IEnumerable<Mode>, int>", "new Dictionary<IEnumerable<Mode>, int> { [Shifting()] = 1 }",
        "Enumerable.SelectMany(groups.Keys, g => g)")]
    [InlineData("Set", "IReadOnlySet<IEnumerable<Mode>>", "new HashSet<IEnumerable<Mode>> { Shifting() }",
        "Enumerable.SelectMany(groups, g => g)")]
    [InlineData("Nested", "IEnumerable<IDictionary<string, IEnumerable<Mode>>>", "new[] { (IDictionary<string, IEnumerable<Mode>>)new Dictionary<string, IEnumerable<Mode>> { [\"a\"] = Shifting() } }",
        "Enumerable.SelectMany(groups, d => Enumerable.SelectMany(d.Values, g => g))")]
    [InlineData("InOut", "IEnumerable<Mode>", "Shifting()", "groups")]
    public void LazySequenceInsideAContainer_IsReadOnce_SoTheWalkSeesWhatTheContainerReads(
        string shape, string csharpType, string argument, string containerRead)
    {
        var spec = shape switch
        {
            "Values" => Generic("Swift.Dictionary", SwiftString, Generic("Swift.Array", Mode)),
            "Keys" => Generic("Swift.Dictionary", Generic("Swift.Array", Mode), Int32),
            "Set" => Generic("Swift.Set", Generic("Swift.Array", Mode)),
            "Nested" => Generic("Swift.Array", Generic("Swift.Dictionary", SwiftString, Generic("Swift.Array", Mode))),
            _ => (TypeSpec)Generic("Swift.Array", Mode),
        };
        var isInOut = shape == "InOut";
        Assert.True(Emit(spec, "groups", out var walk));
        var byRef = isInOut ? "ref " : "";
        var probe = Compile($$"""
            public static int Enumerations;
            // Standard on the first enumeration, Future on every later one.
            private static IEnumerable<Mode> Shifting() { yield return Enumerations++ == 0 ? Mode.Standard : Mode.Future; }
            private static int Read({{byRef}}{{csharpType}} groups) {
                {{walk}}
                return Enumerable.Count({{containerRead}}, m => m == Mode.Future) * 10 + Enumerations;
            }
            public static int Run() { {{csharpType}} groups = {{argument}}; return Read({{byRef}}groups); }
            """);

        Assert.Equal(1, probe.GetMethod("Run")!.Invoke(null, null));
    }

    /// <summary>
    /// A refused call leaves an <c>inout</c> caller's variable as it was: the walk reads the snapshot,
    /// and the parameter is pointed at it only once the walk has let the call through.
    /// </summary>
    [Fact]
    public void RefusedInOutSequence_LeavesTheCallersVariableUnchanged()
    {
        Assert.True(Emit(Generic("Swift.Array", Mode), "modes", out var walk));
        var probe = Compile($$"""
            private static IEnumerable<Mode> Lazy() { yield return Mode.Future; }
            private static void Call(ref IEnumerable<Mode> modes) {
                {{walk}}
            }
            public static bool Run() {
                var modes = Lazy();
                var original = modes;
                try { Call(ref modes); } catch (System.PlatformNotSupportedException) { return ReferenceEquals(modes, original); }
                return false;
            }
            """);

        Assert.True((bool)probe.GetMethod("Run")!.Invoke(null, null)!);
    }

    /// <summary>
    /// With several parameters, the snapshots are committed only once every parameter has been
    /// checked: a floored case in a later parameter leaves an earlier <c>inout</c> one as it was.
    /// </summary>
    [Fact]
    public void LaterParameterRefusal_LeavesAnEarlierInOutSequenceUnchanged()
    {
        var commits = new List<string>();
        var output = new StringWriter();
        var writer = new CSharpWriter(output);
        Assert.True(SimpleEnumCaseFloors.EmitContainedValueRefusal(writer, Generic("Swift.Array", Mode), "modes", "modes", Database(), commits));
        Assert.True(SimpleEnumCaseFloors.EmitContainedValueRefusal(writer, Generic("Swift.Array", Mode), "other", "other", Database(), commits));
        foreach (var commit in commits)
            writer.WriteLine(commit);
        var probe = Compile($$"""
            private static IEnumerable<Mode> Lazy(Mode mode) { yield return mode; }
            private static void Call(ref IEnumerable<Mode> modes, IEnumerable<Mode> other) {
                {{output}}
            }
            public static bool Run() {
                var modes = Lazy(Mode.Standard);
                var original = modes;
                try { Call(ref modes, Lazy(Mode.Future)); } catch (System.PlatformNotSupportedException) { return ReferenceEquals(modes, original); }
                return false;
            }
            """);

        Assert.True((bool)probe.GetMethod("Run")!.Invoke(null, null)!);
    }

    /// <summary>
    /// Copying keeps distinct keys and elements distinct: two empty lazy sequences are two keys of the
    /// caller's dictionary and two elements of its set, and stay two in the snapshot.
    /// </summary>
    [Fact]
    public void EmptyLazySequences_StayDistinctKeysAndElements()
    {
        Assert.True(Emit(Generic("Swift.Dictionary", Generic("Swift.Array", Mode), Int32), "keyed", out var keyedWalk));
        Assert.True(Emit(Generic("Swift.Set", Generic("Swift.Array", Mode)), "grouped", out var groupedWalk));
        var probe = Compile($$"""
            private static IEnumerable<Mode> Empty() { yield break; }
            private static int Keyed(IDictionary<IEnumerable<Mode>, int> keyed) {
                {{keyedWalk}}
                return keyed.Count;
            }
            private static int Grouped(IReadOnlySet<IEnumerable<Mode>> grouped) {
                {{groupedWalk}}
                return grouped.Count;
            }
            public static int Run() =>
                Keyed(new Dictionary<IEnumerable<Mode>, int> { [Empty()] = 1, [Empty()] = 2 }) * 10
                + Grouped(new HashSet<IEnumerable<Mode>> { Empty(), Empty() });
            """);

        Assert.Equal(22, probe.GetMethod("Run")!.Invoke(null, null));
    }

    /// <summary>
    /// A level is copied only when something in it is a sequence that is not a collection: a
    /// dictionary of arrays, passed <c>inout</c> or not, reaches the container as the same object.
    /// </summary>
    [Fact]
    public void ContainerOfCollections_IsNotCopied()
    {
        var spec = Generic("Swift.Dictionary", SwiftString, Generic("Swift.Array", Mode));
        Assert.True(Emit(spec, "groups", out var inOutWalk));
        Assert.True(Emit(spec, "byValue", out var byValueWalk));
        var probe = Compile($$"""
            private static bool InOut(ref IDictionary<string, IEnumerable<Mode>> groups, object original) {
                {{inOutWalk}}
                return ReferenceEquals(groups, original);
            }
            private static bool ByValue(IDictionary<string, IEnumerable<Mode>> byValue, object original) {
                {{byValueWalk}}
                return ReferenceEquals(byValue, original);
            }
            public static bool Run() {
                IDictionary<string, IEnumerable<Mode>> groups = new Dictionary<string, IEnumerable<Mode>> { ["a"] = new[] { Mode.Standard }, ["b"] = new List<Mode>() };
                var original = groups;
                return InOut(ref groups, original) && ByValue(groups, original) && ReferenceEquals(groups, original);
            }
            """);

        Assert.True((bool)probe.GetMethod("Run")!.Invoke(null, null)!);
    }

    /// <summary>
    /// The walk's locals cannot take a sibling parameter's name: named from the parameter's
    /// marshalling base name, they move aside with the other derived locals when a sibling is
    /// named like one of them (<c>modesFloorRoot</c> beside <c>modes</c>).
    /// </summary>
    [Fact]
    public void WalkLocals_DoNotCollideWithASiblingParameter()
    {
        var siblings = new[] { "modes", "modesFloorRoot", "modesFloorValue0", "modesFloorElement1" };
        var baseName = NameProvider.ResolveMarshallingBaseName("modes", siblings);
        var output = new StringWriter();
        Assert.True(SimpleEnumCaseFloors.EmitContainedValueRefusal(new CSharpWriter(output), Generic("Swift.Array", Mode),
            "modes", baseName, Database()));
        var walk = output.ToString();
        var probe = Compile($$"""
            public static int Run(IEnumerable<Mode> modes, int modesFloorRoot, int modesFloorValue0, int modesFloorElement1) {
                {{walk}}
                return modesFloorRoot + modesFloorValue0 + modesFloorElement1;
            }
            public static int Call() => Run(new[] { Mode.Standard }, 1, 2, 3);
            """);

        Assert.Equal(6, probe.GetMethod("Call")!.Invoke(null, null));
    }

    /// <summary>
    /// A subscript setter walks its <c>value</c> in the same scope as the index parameters, so an
    /// index parameter named like one of the walk's locals (<c>valueFloorRoot</c>) moves the walk's
    /// locals aside instead of redeclaring the parameter (CS0136).
    /// </summary>
    [Theory]
    [InlineData("valueFloorRoot")]
    [InlineData("@valueFloorRoot")]
    [InlineData("key")]
    public void SubscriptSetterWalkLocals_DoNotCollideWithAnIndexParameter(string indexName)
    {
        var baseName = SubscriptHandler.SetterValueLocalBaseName(new[] { indexName });
        var refusal = SimpleEnumCaseFloors.ContainedValueRefusalLine(Generic("Swift.Array", Mode), "value", baseName, Database());
        Assert.NotNull(refusal);
        var probe = Compile($$"""
            public sealed class Host {
                public int Stored;
                public IEnumerable<Mode> this[int {{indexName}}] {
                    get => Array.Empty<Mode>();
                    set { {{refusal}} Stored = {{indexName}} + Enumerable.Count(value); }
                }
            }
            public static int Call() { var host = new Host(); host[40] = new[] { Mode.Standard, Mode.Standard }; return host.Stored; }
            public static void Refused() { var host = new Host(); host[1] = new[] { Mode.Future }; }
            """);

        Assert.Equal(42, probe.GetMethod("Call")!.Invoke(null, null));
        var refused = Assert.Throws<TargetInvocationException>(() => probe.GetMethod("Refused")!.Invoke(null, null));
        Assert.IsType<PlatformNotSupportedException>(refused.InnerException);
    }

    // --- helpers ---

    private static NamedTypeSpec Optional(TypeSpec inner) => Generic("Swift.Optional", inner);

    private static NamedTypeSpec Generic(string name, params TypeSpec[] arguments) => new(name, arguments);

    private static bool Emit(TypeSpec spec, string parameter, out string walk)
    {
        var output = new StringWriter();
        var written = SimpleEnumCaseFloors.EmitContainedValueRefusal(new CSharpWriter(output), spec, parameter, parameter, Database());
        walk = output.ToString();
        return written;
    }

    private static TypeDatabase Database()
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase(Module, "/tmp/TestModule.dylib");
        foreach (var (name, guarded) in new[] { ("Mode", true), ("Plain", false) })
        {
            var swiftName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}");
            module.RegisterType(swiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(Module, name),
                SwiftTypeName = swiftName,
                MetadataAccessor = $"$s10TestModule{name.Length}{name}OMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                HasCaseFloorGuard = guarded,
            });
        }
        typeDatabase.AddModuleDatabase(module);
        return typeDatabase;
    }

    private static Type Compile(string members)
    {
        var source = $$"""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            namespace {{Module}};
            public enum Mode { Standard = 0, Future = 2 }
            public enum Plain { One = 0 }
            public static class __ModeCaseFloors
            {
                public static Mode Require(Mode value) =>
                    value == Mode.Future ? throw new PlatformNotSupportedException("Future needs iOS 99.0") : value;
            }
            public static class Probe
            {
            {{members}}
            }
            """;
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("ContainedRefusal" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        var problems = emitted.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
        Assert.True(emitted.Success && problems.Count == 0, string.Join("\n", problems) + "\n" + source);
        return Assembly.Load(stream.ToArray()).GetType($"{Module}.Probe")!;
    }
}
