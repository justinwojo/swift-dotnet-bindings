// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// An <c>SBW_</c> entry point exists only in the generated companion wrapper library, so every
/// P/Invoke that names one must bind against the library the run was actually configured to emit
/// (<see cref="ITypeDatabase.AsyncLibraryName"/>). Binding it against any other name compiles fine
/// and then throws <c>EntryPointNotFoundException</c>/<c>DllNotFoundException</c> on first call —
/// which is exactly what <c>AbiContractChecker</c> CC-003 (SWIFTBIND093) exists to catch, and the
/// generator fails the whole module closed when it fires.
///
/// <para>The regression these pin: a couple of hand-rolled helper P/Invokes — the failable-init
/// Optional tag helper and the conformer KeyPath-init (EntityProperty) factory — wrote the literal
/// <c>"SwiftBindings"</c> as their library instead of consulting the configured wrapper name. That
/// literal happens to be the wrapper module name the BindingTests iOS lane passes via
/// <c>--async-library</c>, so the mistake was invisible there; every run that does NOT pass
/// <c>--async-library</c> (the macOS BindingTests lane, and every third-party binding, both of which
/// auto-default the wrapper to <c>"{Module}SwiftBindings"</c>) tripped CC-003 and emitted no C# at
/// all.</para>
/// </summary>
public class WrapperLibraryBindingTests
{
    private const string ConfiguredWrapper = "TestModuleSwiftBindings";

    // ─── The resolver every hand-rolled wrapper P/Invoke must route through ───

    [Fact]
    public void ResolveWrapperLibrary_ConfiguredWrapper_IsUsed()
    {
        var db = new TypeDatabase { AsyncLibraryName = ConfiguredWrapper };
        Assert.Equal(ConfiguredWrapper, PInvokeEmitHelper.ResolveWrapperLibrary(db));
    }

    [Fact]
    public void ResolveWrapperLibrary_NoWrapperConfigured_FallsBackToConventionalName()
    {
        // Direct mode with no companion wrapper: CC-003 is not checkable (there is no wrapper to be
        // wrong about), and the conventional name is what the generator has always emitted here.
        var db = new TypeDatabase();
        Assert.Equal(
            PInvokeEmitHelper.DefaultWrapperLibraryName,
            PInvokeEmitHelper.ResolveWrapperLibrary(db));
    }

    // ─── Optional tag helper (failable init on a frozen struct) ───

    [Fact]
    public void OptionalTagHelperPInvoke_BindsConfiguredWrapperLibrary()
    {
        var (emitter, _) = CreateFailableCtorEmitter(ConfiguredWrapper, withHelperContext: false);

        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        emitter.EmitOptionalTagHelperPInvoke(csWriter);
        var output = sw.ToString();

        Assert.Contains("SBW_GetOptionalTag_TestModule_SafeDiv", output);
        Assert.Contains($"\"{ConfiguredWrapper}\"", output);
        Assert.DoesNotContain("\"SwiftBindings\"", output);
    }

    [Fact]
    public void OptionalTagHelperPInvoke_BindsConfiguredWrapperLibrary_ViaHelperContext()
    {
        // The two branches of the helper are independent code paths (a PInvokeHelperContext
        // declaration vs. a directly written attribute); both must name the same library.
        var (emitter, helperContext) = CreateFailableCtorEmitter(ConfiguredWrapper, withHelperContext: true);

        var sw = new StringWriter();
        emitter.EmitOptionalTagHelperPInvoke(new CSharpWriter(sw));

        var decl = Assert.Single(
            helperContext!.Declarations, d => d.MethodName == "PInvoke_GetOptionalTag");
        Assert.Equal(ConfiguredWrapper, decl.LibraryPath);
    }

    [Fact]
    public void OptionalTagHelperPInvoke_IsCleanUnderCC003()
    {
        // The behavioral statement, asserted through the very gate that failed the macOS run:
        // the emitted declaration must not be reported as binding a wrapper symbol against a
        // non-wrapper library.
        var (emitter, _) = CreateFailableCtorEmitter(ConfiguredWrapper, withHelperContext: false);

        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        csWriter.WriteLine("public static partial class Probe");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        emitter.EmitOptionalTagHelperPInvoke(csWriter);
        csWriter.Indent--;
        csWriter.WriteLine("}");

        var result = AbiContractChecker.Validate(
            sw.ToString(), "TestModule", NullLogger.Instance, ConfiguredWrapper);

        Assert.Empty(result.Violations.Where(v => v.RuleId == "CC-003"));
    }

    // ─── Conformer KeyPath-init (EntityProperty) factory ───

    [Fact]
    public void KeyPathInitFactoryPInvoke_BindsConfiguredWrapperLibrary()
    {
        var output = EmitKeyPathInitFactories(ConfiguredWrapper);

        // The factory really was emitted — otherwise the library assertions below pass vacuously.
        Assert.Contains("SBW_EPF_", output);
        Assert.Contains($"LibraryImport(\"{ConfiguredWrapper}\"", output);
        Assert.DoesNotContain("\"SwiftBindings\"", output);
    }

    [Fact]
    public void KeyPathInitFactoryPInvoke_IsCleanUnderCC003()
    {
        // Same behavioral statement as the Optional-tag-helper case, asserted through the gate
        // that actually failed the macOS run: an SBW_ symbol bound against a non-wrapper library.
        var output = EmitKeyPathInitFactories(ConfiguredWrapper);

        var result = AbiContractChecker.Validate(
            output, "TestModule", NullLogger.Instance, ConfiguredWrapper);

        Assert.Empty(result.Violations.Where(v => v.RuleId == "CC-003"));
    }

    [Fact]
    public void KeyPathInitFactoryPInvoke_TracksTheConfiguredWrapper_NotAnyFixedName()
    {
        // The drift guard below can only see literals in the source. It cannot see a P/Invoke that
        // resolves from the wrong variable or field — that reads as "not a literal" and passes.
        // Emitting the same fixture under two different configured wrappers pins the actual
        // dependency: the library name must MOVE with ITypeDatabase.AsyncLibraryName.
        var first = EmitKeyPathInitFactories("AlphaSwiftBindings");
        var second = EmitKeyPathInitFactories("BetaSwiftBindings");

        Assert.Contains("LibraryImport(\"AlphaSwiftBindings\"", first);
        Assert.DoesNotContain("BetaSwiftBindings", first);
        Assert.Contains("LibraryImport(\"BetaSwiftBindings\"", second);
        Assert.DoesNotContain("AlphaSwiftBindings", second);
    }

    // ─── Drift guard over the whole emitter tree ───

    [Fact]
    public void NoEmitterHardcodesAWrapperLibraryName()
    {
        // Both regressions had the same shape: a hand-rolled P/Invoke that wrote a library-name
        // literal instead of asking the TypeDatabase. This guard is over the source: an emitted
        // LibraryImport/LibraryPath may name a variable, never a bare wrapper-library literal. It
        // catches REINTRODUCTION of a literal anywhere in the tree — including emitters with no
        // behavioral test of their own — which is why it stays alongside the per-path tests above.
        var emitterDir = Path.Combine(LocateRepoRoot(), "src", "Swift.Bindings", "src", "Emitter");
        Assert.True(Directory.Exists(emitterDir), $"Emitter source directory not found: {emitterDir}");

        // Matches   LibraryImport(\"SwiftBindings\"   /   DllImport(\"…SwiftBindings\"
        //     and   LibraryPath = "SwiftBindings"     (the PInvokeEmissionInfo/Declaration form).
        var literalImport = new Regex(
            @"(?:LibraryImport|DllImport)\(\\?""[A-Za-z0-9_]*SwiftBindings\\?""",
            RegexOptions.Compiled);
        var literalPath = new Regex(
            @"LibraryPath\s*=\s*""[A-Za-z0-9_]*SwiftBindings""",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(emitterDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (literalImport.IsMatch(lines[i]) || literalPath.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(emitterDir, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A P/Invoke names a hardcoded wrapper library instead of resolving it from the " +
            "TypeDatabase (PInvokeEmitHelper.ResolveWrapperLibrary). Any run whose configured " +
            "--async-library differs from that literal fails CC-003/SWIFTBIND093 and emits no C#:\n  " +
            string.Join("\n  ", offenders));
    }

    // ─── Fixture: conformer KeyPath-init factory ───

    /// <summary>
    /// Drives <see cref="ConformerKeyPathInitFactoryEmitter.EmitForModule"/> over the smallest input
    /// that reaches its P/Invoke: a dependency module vending
    /// <c>MiniEntityProperty&lt;Value&gt;.init&lt;Entity: MiniAppEntity&gt;(keyPath:)</c>, and a local
    /// module whose public struct conforms to that protocol and carries one KeyPath-able property.
    /// Returns the emitted C#.
    /// </summary>
    private static string EmitKeyPathInitFactories(string asyncLibraryName)
    {
        // A non-empty AsyncLibraryName is also what puts the run in XCFramework mode, which is the
        // emitter's own gate — the factories are wrapper-trampoline-backed, so a Direct-mode run
        // has no wrapper to bind and emits nothing.
        var typeDatabase = new TypeDatabase { AsyncLibraryName = asyncLibraryName };

        var depClass = BuildMiniEntityPropertyClass();
        var depModule = BuildModule("DepModule", depClass);
        typeDatabase.AddDependencyModuleDecl(depModule);

        var conformer = BuildConformerStruct();
        var localModule = BuildModule("TestModule", conformer);

        RegisterTypes(typeDatabase, depClass, conformer);

        var engine = new ConcreteSpecializationEngine(typeDatabase, "TestModule");
        engine.IndexModuleConformances(localModule);

        var sw = new StringWriter();
        ConformerKeyPathInitFactoryEmitter.EmitForModule(
            new CSharpWriter(sw), new SwiftWriter(new StringWriter()),
            localModule, typeDatabase,
            // An explicit context: the dedup key this emitter claims is scoped to whatever context
            // it is handed, so sharing one across tests would make the second emit nothing.
            new ModuleEmissionContext(),
            engine, NullLogger.Instance);

        return sw.ToString();
    }

    private static ModuleDecl BuildModule(string name, TypeDecl type)
    {
        var module = new ModuleDecl
        {
            Name = name,
            Dependencies = new List<string>(),
            Types = new List<TypeDecl> { type },
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        return module;
    }

    /// <summary>
    /// The dependency shape the emitter recognizes: a final generic class with one class generic
    /// (the KeyPath Value slot) and a constructor whose own generic is protocol-constrained and
    /// roots a <c>KeyPath&lt;Entity, Value&gt;</c>.
    /// </summary>
    private static ClassDecl BuildMiniEntityPropertyClass()
    {
        var classGeneric = new GenericArgumentDecl(
            "τ_0_0", "Value",
            new List<GenericParameterConformance>(),
            new List<GenericParameterConformance>());

        var methodGeneric = new GenericArgumentDecl(
            "τ_1_0", "Entity",
            new List<GenericParameterConformance>
            {
                new GenericParameterConformance(
                    new[] { "τ_1_0" },
                    SwiftTypeName.FromModuleQualifiedName("DepModule.MiniAppEntity"),
                    ConformanceKind.Protocol),
            },
            new List<GenericParameterConformance>());

        var ctor = new MethodDecl
        {
            Name = "init",
            MangledName = "$s9DepModule17MiniEntityPropertyCyACyxGqd__7keyPathtcfc",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                // Index 0 is the constructor return type; recognition starts at index 1.
                KeyPathArg("$return", new NamedTypeSpec("DepModule.MiniEntityProperty")),
                KeyPathArg("keyPath", new NamedTypeSpec(
                    "Swift.KeyPath", new NamedTypeSpec("Entity"), new NamedTypeSpec("Value"))),
            },
            // Mirrors the real ABI shape: the class generic (depth-0) precedes the method-own one.
            GenericParameters = new List<GenericArgumentDecl> { classGeneric, methodGeneric },
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
        };

        var depClass = new ClassDecl
        {
            Name = "MiniEntityProperty",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("DepModule.MiniEntityProperty"),
            MangledName = "$s9DepModule17MiniEntityPropertyC",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { ctor },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl> { classGeneric },
            Conformances = new List<TypeConformance>(),
            IsFinal = true,
            ParentDecl = null,
            ModuleDecl = null,
        };
        return depClass;
    }

    /// <summary>
    /// A concrete, public, non-generic local conformer with one stored <c>String</c> property —
    /// the eligible shape, carrying exactly one KeyPath-able value type.
    /// </summary>
    private static StructDecl BuildConformerStruct()
    {
        var conformerName = SwiftTypeName.FromModuleQualifiedName("TestModule.MockBook");
        var titleType = new NamedTypeSpec("Swift.String");

        var title = new PropertyDecl
        {
            Name = "title",
            SwiftTypeSpec = titleType,
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = AccessorMethod("title_get") },
            },
            ParentDecl = null,
            ModuleDecl = null,
        };

        return new StructDecl
        {
            Name = "MockBook",
            SwiftTypeName = conformerName,
            MangledName = "$s10TestModule8MockBookV",
            Properties = new List<PropertyDecl> { title },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>
            {
                new TypeConformance(
                    conformerName,
                    SwiftTypeName.FromModuleQualifiedName("DepModule.MiniAppEntity"),
                    "$s10TestModule8MockBookV9DepModule12MiniAppEntityAAMc"),
            },
            IsFrozen = false,
            MetadataAccessor = "$s10TestModule8MockBookVMa",
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    /// <summary>
    /// The emitter resolves every name it emits through the TypeDatabase (the dependency class, the
    /// conformer, and the property's value type), so all three must be registered or emission
    /// short-circuits before reaching the P/Invoke.
    /// </summary>
    private static void RegisterTypes(TypeDatabase typeDatabase, ClassDecl depClass, StructDecl conformer)
    {
        var depDb = new ModuleTypeDatabase("DepModule", "/fake/DepModule.dylib");
        depDb.RegisterType(depClass.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.DepModule", "MiniEntityProperty"),
            SwiftTypeName = depClass.SwiftTypeName,
            MetadataAccessor = "$s9DepModule17MiniEntityPropertyCMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Class,
        });
        typeDatabase.AddModuleDatabase(depDb);

        var localDb = new ModuleTypeDatabase("TestModule", "/fake/TestModule.dylib");
        localDb.RegisterType(conformer.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MockBook"),
            SwiftTypeName = conformer.SwiftTypeName,
            MetadataAccessor = "$s10TestModule8MockBookVMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Struct,
        });
        typeDatabase.AddModuleDatabase(localDb);
    }

    private static MethodDecl AccessorMethod(string name) =>
        new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static ArgumentDecl KeyPathArg(string name, TypeSpec spec) =>
        new ArgumentDecl
        {
            Name = name,
            SwiftTypeSpec = spec,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null,
        };

    // ─── Fixture: failable-init Optional tag helper ───

    /// <summary>
    /// A minimal <c>WrapperEmitter</c> whose environment hosts a failable initializer on a frozen
    /// struct — the shape whose Optional tag helper is emitted once per parent type.
    /// </summary>
    private static (WrapperEmitter Emitter, PInvokeHelperContext? HelperContext) CreateFailableCtorEmitter(
        string? asyncLibraryName, bool withHelperContext)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var structDecl = new StructDecl
        {
            Name = "SafeDiv",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SafeDiv"),
            MangledName = "$s10TestModule7SafeDivV",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "$s10TestModule7SafeDivVMa",
            AvailabilityAnnotations = null,
        };
        moduleDecl.Types.Add(structDecl);

        var methodDecl = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule7SafeDivVyACSgSi_SitcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = "value",
                    PrivateName = "value",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = structDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = structDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            UsesCdeclConstructorWrapper = true,
        };
        structDecl.Methods.Add(methodDecl);

        var typeDatabase = new TypeDatabase { AsyncLibraryName = asyncLibraryName };
        var module = new ModuleTypeDatabase("TestModule", "/fake/TestModule.dylib");
        var safeDivName = SwiftTypeName.FromModuleQualifiedName("TestModule.SafeDiv");
        module.RegisterType(safeDivName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "SafeDiv"),
            SwiftTypeName = safeDivName,
            MetadataAccessor = "$s10TestModule7SafeDivVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(module);

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        var intTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        swiftModule.RegisterType(intTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = intTypeName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);

        var helperContext = withHelperContext
            ? new PInvokeHelperContext("SafeDiv", Array.Empty<string>())
            : null;

        var env = new MethodEnvironment(methodDecl, typeDatabase, pinvokeHelperContext: helperContext);
        return (new WrapperEmitter(env, new SignatureHandler(env)), helperContext);
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
