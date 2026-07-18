// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the assumption every regenerate-from-plan mechanism rests on: emission is a pure function
/// of (frozen TypeDatabase, decl tree). Re-running it over the same inputs must produce a
/// byte-identical output set — same file names, same bytes — or a diagnostic attributed against
/// one render would be applied to a different one, and a "discard the attempt and re-emit"
/// recovery would silently change unrelated output.
///
/// <para>These run the whole <see cref="StringEmitter.EmitModule"/> path (pre-passes, handler tree,
/// namespace qualification, the file-per-type split, the manifest/surface writers and the Swift
/// wrapper file) into a scratch directory, twice, <b>in one process</b>. In-process is the mode
/// that matters: a two-process CLI double-run cannot see leftover static or <c>AsyncLocal</c>
/// state bleeding from one emission into the next, which is exactly what a re-emission loop
/// living inside a single generator run would hit.</para>
/// </summary>
public class EmissionDeterminismTests : IDisposable
{
    private readonly List<string> _scratchDirs = new();

    public void Dispose()
    {
        foreach (var dir in _scratchDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void EmitModule_RunTwiceInProcess_ProducesByteIdenticalOutputSet()
    {
        var first = EmitFixtureModule();
        var second = EmitFixtureModule();

        AssertOutputSetsIdentical(first, second);
    }

    /// <summary>
    /// The harsher shape: a different module is emitted between the two runs, so any static or
    /// <c>AsyncLocal</c> registry that accumulates across emissions (rather than being rebuilt per
    /// module) carries foreign state into the second run. A plain back-to-back double-emit can miss
    /// that when the leaked state happens to be a no-op for the same inputs.
    /// </summary>
    [Fact]
    public void EmitModule_RunTwiceAcrossAnInterveningModule_ProducesByteIdenticalOutputSet()
    {
        var first = EmitFixtureModule();
        EmitFixtureModule(moduleName: "InterveningModule");
        var second = EmitFixtureModule();

        AssertOutputSetsIdentical(first, second);
    }

    /// <summary>
    /// Guards the two tests above from passing vacuously. Byte-identity over an empty or trivial
    /// output set proves nothing, so assert the fixture really did drive the machinery whose
    /// ordering and name allocation is the plausible nondeterminism source: a protocol (proxy +
    /// witness dispatch), generics, closures, async, and enough members to make collision-suffix
    /// allocation and dedup registries matter.
    /// </summary>
    [Fact]
    public void FixtureModule_ExercisesTheMachineryTheDeterminismGateDependsOn()
    {
        var output = EmitFixtureModule();

        var combinedCSharp = string.Concat(output
            .Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => f.Value));
        var wrapper = output.Single(f => f.Key.EndsWith(".Wrapper.swift", StringComparison.Ordinal)).Value;

        Assert.True(output.Count >= 3, $"expected a multi-file output set, got {output.Count}");
        Assert.True(combinedCSharp.Length > 4000, $"C# output is too small to be meaningful ({combinedCSharp.Length} chars)");
        Assert.True(wrapper.Length > 500, $"Swift wrapper is too small to be meaningful ({wrapper.Length} chars)");

        // The fixture's shapes must have survived to emission, not been skipped wholesale.
        Assert.Contains("interface IShapeSink", combinedCSharp, StringComparison.Ordinal);      // protocol interface + proxy
        Assert.Contains("LibraryImport", combinedCSharp, StringComparison.Ordinal);             // native call surface
        Assert.Contains("Task<", combinedCSharp, StringComparison.Ordinal);                     // async bridge
        Assert.Contains("@_cdecl", wrapper, StringComparison.Ordinal);                          // Swift wrapper blocks

        // The collision-suffix allocator is the name-allocation machinery most likely to be
        // order-sensitive, so the gate is only meaningful if the fixture actually forced a rename.
        Assert.Contains("Register2", combinedCSharp, StringComparison.Ordinal);
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits the fixture module through the real <see cref="StringEmitter"/> into a fresh scratch
    /// directory and returns every file it wrote as name → content. Each call rebuilds the decl
    /// tree, the TypeDatabase, the <see cref="ModuleEmissionContext"/> and the report session —
    /// the same "fresh everything, same inputs" shape a re-emission attempt would use. Anything
    /// that survives across calls is ambient process state, which is what these tests hunt.
    /// </summary>
    private Dictionary<string, string> EmitFixtureModule(string moduleName = "DeterminismFixture")
    {
        var scratch = Path.Combine(Path.GetTempPath(), "swiftbind-determinism-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        _scratchDirs.Add(scratch);

        var moduleDecl = BuildFixtureModule(moduleName);
        var typeDatabase = BuildTypeDatabase(moduleDecl);

        // Mirror the module-boundary resets the CLI performs immediately around emission. Only
        // resets production already does belong here — adding one it does not would hide a real
        // leak rather than expose it.
        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        AppleSupplementReferences.Reset();
        try
        {
            var emitter = new StringEmitter(scratch, typeDatabase, new NullLoggerFactory());
            emitter.EmitModule(moduleDecl, new ModuleEmissionContext());
        }
        finally
        {
            ReportCollector.Complete();
            ReportCollector.Reset();
        }

        return Directory.EnumerateFiles(scratch)
            .ToDictionary(Path.GetFileName!, File.ReadAllText, StringComparer.Ordinal);
    }

    private static void AssertOutputSetsIdentical(
        Dictionary<string, string> first, Dictionary<string, string> second)
    {
        Assert.Equal(
            first.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            second.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        foreach (var name in first.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (string.Equals(first[name], second[name], StringComparison.Ordinal))
                continue;

            Assert.Fail(
                $"'{name}' differs between two emissions of the same inputs.{Environment.NewLine}" +
                DescribeFirstDifference(first[name], second[name]));
        }
    }

    /// <summary>
    /// Renders the first differing region of two renders. A raw full-file diff of generated output
    /// is unreadable in a test failure; the offset plus a window around it is what actually points
    /// at the nondeterministic emitter.
    /// </summary>
    private static string DescribeFirstDifference(string a, string b)
    {
        var limit = Math.Min(a.Length, b.Length);
        var offset = 0;
        while (offset < limit && a[offset] == b[offset])
            offset++;

        const int window = 160;
        var start = Math.Max(0, offset - window / 2);
        string Window(string s) => s.Substring(start, Math.Min(window, s.Length - start)).Replace("\n", "\\n");

        return $"  first difference at char {offset} (lengths {a.Length} vs {b.Length}){Environment.NewLine}" +
               $"  run 1: …{Window(a)}…{Environment.NewLine}" +
               $"  run 2: …{Window(b)}…";
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A deliberately broad module: a protocol with method and property requirements plus an
    /// extension default, a frozen and a non-frozen struct, a class with inherited and overloaded
    /// members, an enum, closures, async and throwing members, and free functions. Breadth is the
    /// point — each additional shape brings another dedup registry, name allocator and emitter
    /// family into the render, and a nondeterminism gate only covers the machinery it runs.
    /// </summary>
    private static ModuleDecl BuildFixtureModule(string moduleName)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = moduleName,
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var intSpec = new NamedTypeSpec("Swift.Int");
        var stringSpec = new NamedTypeSpec("Swift.String");
        var boolSpec = new NamedTypeSpec("Swift.Bool");
        var doubleSpec = new NamedTypeSpec("Swift.Double");

        // Protocol: requirements (vtable slots) + an extension default (no slot), and a
        // label-only overload pair so the disambiguation/collision machinery participates.
        var sink = TestDecls.Protocol(
            "ShapeSink", moduleName,
            TestDecls.Method("accept", parameters: new[] { TestDecls.Param("value", intSpec) }, module: moduleName),
            TestDecls.Method("accept", parameters: new[] { TestDecls.Param("text", stringSpec) }, module: moduleName),
            TestDecls.Method("total", returnType: intSpec, module: moduleName),
            TestDecls.Property("label", stringSpec, module: moduleName),
            TestDecls.Property("isEmpty", boolSpec, module: moduleName),
            TestDecls.ExtensionDefault("describe", returnType: stringSpec, module: moduleName));
        moduleDecl.Protocols.Add(sink);
        moduleDecl.Types.Add(sink);

        // Frozen struct: value-type marshalling + a throwing and an async member.
        var point = Struct("Point", moduleName, moduleDecl, isFrozen: true);
        point.Properties.Add(TestDecls.Property("x", doubleSpec, module: moduleName));
        point.Properties.Add(TestDecls.Property("y", doubleSpec, hasSetter: true, module: moduleName));
        point.Methods.Add(TestDecls.Method("magnitude", returnType: doubleSpec, module: moduleName));
        point.Methods.Add(TestDecls.Method("scaled", parameters: new[] { TestDecls.Param("by", doubleSpec) }, returnType: doubleSpec, module: moduleName));
        point.Methods.Add(TestDecls.Method("validate", throws: true, module: moduleName));
        moduleDecl.Types.Add(point);

        // Non-frozen struct: opaque-payload class projection — a different marshalling family.
        var box = Struct("Box", moduleName, moduleDecl, isFrozen: false);
        box.Properties.Add(TestDecls.Property("contents", stringSpec, hasSetter: true, module: moduleName));
        box.Methods.Add(TestDecls.Method("clear", module: moduleName));
        box.Methods.Add(TestDecls.Method("load", isAsync: true, returnType: stringSpec, module: moduleName));
        moduleDecl.Types.Add(box);

        // Class: reference-type ARC path plus an overload set whose projected C# names collide,
        // which is what drives the collision-suffix allocator.
        var registry = Class("Registry", moduleName, moduleDecl);
        registry.Properties.Add(TestDecls.Property("count", intSpec, module: moduleName));
        registry.Properties.Add(TestDecls.Property("name", stringSpec, hasSetter: true, module: moduleName));
        registry.Methods.Add(TestDecls.Method("register", parameters: new[] { TestDecls.Param("first", intSpec) }, module: moduleName));
        registry.Methods.Add(TestDecls.Method("register", parameters: new[] { TestDecls.Param("second", intSpec) }, module: moduleName));
        registry.Methods.Add(TestDecls.Method("register", parameters: new[] { TestDecls.Param("third", stringSpec) }, module: moduleName));
        registry.Methods.Add(TestDecls.Method("reset", methodType: MethodType.Static, module: moduleName));
        registry.Methods.Add(TestDecls.Method("fetch", isAsync: true, returnType: intSpec, module: moduleName));
        moduleDecl.Types.Add(registry);

        // Free functions, including a closure parameter (callback thunk emission) and a throwing one.
        moduleDecl.Methods.Add(
            TestDecls.Method("makeDefaultPoint", methodType: MethodType.Static, returnType: doubleSpec, module: moduleName));
        moduleDecl.Methods.Add(
            TestDecls.Method(
                "transform",
                methodType: MethodType.Static,
                parameters: new[] { TestDecls.Param("using", ClosureSpec(intSpec, intSpec)) },
                returnType: intSpec,
                module: moduleName));
        moduleDecl.Methods.Add(
            TestDecls.Method("riskyOperation", methodType: MethodType.Static, throws: true, returnType: boolSpec, module: moduleName));

        // The decl factories leave ownership unset (they build free-standing decls), so stitch the
        // whole tree's ParentDecl/ModuleDecl in one pass — including each property accessor's
        // backing method, which the emitter dereferences to resolve the declaring type.
        foreach (var type in moduleDecl.Types)
            Reparent(type, moduleDecl);
        foreach (var method in moduleDecl.Methods)
            Own(method, moduleDecl, moduleDecl);
        foreach (var property in moduleDecl.Properties)
            OwnProperty(property, moduleDecl, moduleDecl);

        return moduleDecl;
    }

    /// <summary>An escaping <c>(In) -&gt; Out</c> closure parameter spec.</summary>
    private static ClosureTypeSpec ClosureSpec(TypeSpec argument, TypeSpec returnType)
    {
        var spec = new ClosureTypeSpec(argument, returnType);
        spec.Attributes.Add(new TypeSpecAttribute("escaping"));
        return spec;
    }

    private static StructDecl Struct(string name, string moduleName, ModuleDecl moduleDecl, bool isFrozen) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
        MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}VN",
        MetadataAccessor = $"$s{moduleName.Length}{moduleName}{name.Length}{name}VMa",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        IsFrozen = isFrozen,
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
    };

    private static ClassDecl Class(string name, string moduleName, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
        MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}CN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        IsFinal = true,
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
    };

    private static void Reparent(TypeDecl type, ModuleDecl moduleDecl)
    {
        Own(type, moduleDecl, moduleDecl);
        foreach (var method in type.Methods)
            Own(method, type, moduleDecl);
        foreach (var property in type.Properties)
            OwnProperty(property, type, moduleDecl);
        foreach (var nested in type.Types)
            Reparent(nested, moduleDecl);
    }

    private static void OwnProperty(PropertyDecl property, BaseDecl parent, ModuleDecl moduleDecl)
    {
        Own(property, parent, moduleDecl);
        foreach (var accessor in property.Accessors)
            Own(accessor.Method, parent, moduleDecl);
    }

    private static void Own(BaseDecl decl, BaseDecl parent, ModuleDecl moduleDecl)
    {
        decl.ParentDecl = parent;
        decl.ModuleDecl = moduleDecl;
    }

    private static TypeDatabase BuildTypeDatabase(ModuleDecl moduleDecl)
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterStdlibType(swiftModule, "Swift.Int", CSharpTypeName.NIntType, "$sSiMa");
        RegisterStdlibType(swiftModule, "Swift.Bool", CSharpTypeName.FromNamespaceAndName("System", "Boolean"), "$sSbMa");
        RegisterStdlibType(swiftModule, "Swift.Double", CSharpTypeName.FromNamespaceAndName("System", "Double"), "$sSdMa");
        RegisterStdlibType(swiftModule, "Swift.String", CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"), "$sSSMa");
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase(moduleDecl.Name, $"/fake/{moduleDecl.Name}.dylib");
        foreach (var type in moduleDecl.Types)
        {
            var kind = type switch
            {
                ProtocolDecl => TypeRecordKind.Protocol,
                ClassDecl => TypeRecordKind.Class,
                _ => TypeRecordKind.Struct,
            };
            var flags = type is StructDecl { IsFrozen: true } ? TypeRecordFlags.Frozen : TypeRecordFlags.None;

            module.RegisterType(
                type.SwiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleDecl.Name, type.Name),
                    SwiftTypeName = type.SwiftTypeName,
                    MetadataAccessor = (type as StructDecl)?.MetadataAccessor ?? string.Empty,
                    Flags = flags,
                    Kind = kind,
                });
        }
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static void RegisterStdlibType(
        ModuleTypeDatabase module, string qualifiedName, CSharpTypeName csharpName, string metadataAccessor)
    {
        var swiftName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
        module.RegisterType(
            swiftName,
            new TypeRecord
            {
                CSharpTypeName = csharpName,
                SwiftTypeName = swiftName,
                MetadataAccessor = metadataAccessor,
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
    }
}
