// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

[Collection("ReportCollector")]
public class ExistentialBypassEmitterTests
{
    [Fact]
    public void TryEmit_ExistentialParamWithDefaultArg_EmitsBypass()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        // Create existential type argument: "any Equatable"
        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        // Constructor with bound generic param containing existential, with default
        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Bypass should be emitted — both C# factory and Swift wrapper
        Assert.NotEqual(string.Empty, csOutput);
        Assert.NotEqual(string.Empty, swiftOutput);
        // Single bypass factory on this type with a unique signature → hash-free `Create`.
        Assert.Contains("static unsafe Config Create(", csOutput);
        Assert.DoesNotContain("Create_", csOutput);
        Assert.Contains("SBSW_Config_init_", csOutput);
        Assert.Contains("@_silgen_name", swiftOutput);
        Assert.Contains("SBSW_Config_init_", swiftOutput);
        Assert.Contains("SBSW_Config_free_", swiftOutput);
    }

    [Fact]
    public void RenderModuleQualifiedSwiftTypeWithExistentialAny_OptionalProtocolComposition_NoDoubleAny()
    {
        var typeDatabase = CreateTypeDatabase();

        // Optional<P & Q>: the inner ProtocolListTypeSpec already renders WITH `any`,
        // so the Optional path must not prepend a second one (which produces the
        // unparseable `any any P & Q`).
        var composition = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.P"),
            new NamedTypeSpec("TestModule.Q")
        });
        var optionalComposition = new NamedTypeSpec("Swift.Optional", composition);

        var rendered = CdeclParamMapper.RenderModuleQualifiedSwiftTypeWithExistentialAny(
            optionalComposition, typeDatabase);

        Assert.DoesNotContain("any any", rendered);
        Assert.Equal("Swift.Optional<(any TestModule.P & TestModule.Q)>", rendered);
    }

    [Fact]
    public void RenderModuleQualifiedSwiftTypeWithExistentialAny_OptionalAny_NoDoubleAny()
    {
        var typeDatabase = CreateTypeDatabase();

        // Optional<Any>: an empty ProtocolListTypeSpec renders as the bare existential
        // "Any" (no `any ` prefix), so exactly one `any` is added → `any Any`, which is
        // valid Swift. Guards against the StartsWith check being case-confused.
        var emptyComposition = new ProtocolListTypeSpec(System.Array.Empty<NamedTypeSpec>());
        var optionalAny = new NamedTypeSpec("Swift.Optional", emptyComposition);

        var rendered = CdeclParamMapper.RenderModuleQualifiedSwiftTypeWithExistentialAny(
            optionalAny, typeDatabase);

        Assert.DoesNotContain("any any", rendered);
        Assert.Equal("Swift.Optional<(any Any)>", rendered);
    }

    [Fact]
    public void TryEmit_ExistentialParamWithoutDefaultArg_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: false)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Bypass not possible — falls back to skip (empty output)
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_ExistentialPlusUnsupportedNonExistentialParam_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true),
                CreateArgument("unknown", new NamedTypeSpec("Missing.Type"), moduleDecl)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Bypass not possible because non-existential param is not marshallable
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_ExistentialPlusPrimitivePassthroughParam_EmitsBypassWithPassthrough()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("count", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Bypass emitted with count as passthrough
        Assert.NotEqual(string.Empty, csOutput);
        Assert.NotEqual(string.Empty, swiftOutput);
        // Unique signature → hash-free `Create(nint count)`.
        Assert.Contains("static unsafe Config Create(", csOutput);
        Assert.DoesNotContain("Create_", csOutput);
        Assert.Contains("count", csOutput);
        Assert.Contains("count", swiftOutput);
    }

    [Fact]
    public void TryEmit_GeneratedSwiftWrapper_UsesMangledHashBasedName_ButCSharpFactoryIsHashFree()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var mangledHash = ArraySliceNormalizationEmitter.DeterministicHash8(constructor.MangledName);

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // The Swift @_silgen_name wrapper symbols keep the mangled hash (ABI identity, must be
        // globally unique). The C#-facing factory, being the sole unique-signature `Create` on
        // this type, drops it for readability.
        Assert.Contains($"SBSW_Config_init_{mangledHash}", swiftOutput);
        Assert.Contains($"SBSW_Config_free_{mangledHash}", swiftOutput);
        Assert.Contains("static unsafe Config Create(", csOutput);
        Assert.DoesNotContain($"Create_{mangledHash}", csOutput);
    }

    [Fact]
    public void TryEmit_TwoBypassConstructors_SameSignature_SecondKeepsHash_NoDuplicateCreate()
    {
        // Collision-safety fixture: two existential-bypass constructors on the SAME struct that
        // reduce to the SAME C# overload signature — `Create(nint)` — differing only in the
        // (irrelevant-to-overloading) parameter name. Emitting both bare would be CS0111. Sharing
        // one ModuleEmissionContext (as a real module emit does), the first factory claims the
        // hash-free `Create` and the second keeps its deterministic `Create_{hash}`. Assert exactly
        // one hash-free `Create(` survives and the loser carries a hash — no duplicate, no CS0111.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);
        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        // Distinct init names → distinct mangled names → distinct Swift SBSW_ symbols (both claims
        // win) → both factories emit. Distinct passthrough param names, identical param TYPE (nint).
        var ctorA = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("count", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });
        var ctorB = CreateConstructorDecl("initAlt", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("amount", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("extras", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);
        var conductor = new Conductor(new NullLoggerFactory());
        var context = TypeHandlerContext.Empty with { EmissionContext = new ModuleEmissionContext() };

        foreach (var ctor in new[] { ctorA, ctorB })
        {
            var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
            var env = new MethodEnvironment(ctor, typeDatabase);
            handler.Emit(csWriter, swiftWriter, env, conductor, context);
        }

        var cs = csOutput.ToString();
        // Exactly one hash-free `Create(` (the substring cannot match `Create_<hash>(`).
        var bareCreateCount = cs.Split("static unsafe Config Create(").Length - 1;
        Assert.Equal(1, bareCreateCount);
        // The second factory kept its deterministic hash disambiguator.
        Assert.Contains("Create_", cs);
    }

    [Fact]
    public void TryEmit_CSharpFactory_UsesTryFinallyCleanup()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("IntPtr swiftPtr = IntPtr.Zero;", csOutput);
        Assert.Contains("try", csOutput);
        Assert.Contains("finally", csOutput);
        Assert.Contains("if (swiftPtr != IntPtr.Zero)", csOutput);
    }

    [Fact]
    public void TryEmit_PInvoke_UsesCallConvSwift()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // Bypass wrappers use SBSW_ prefix + @_silgen_name (Swift CC) because passthrough
        // args may carry non-@objc class types that can't be expressed under @_cdecl.
        Assert.Contains("CallConvSwift", csOutput);
        Assert.Contains("LibraryImport", csOutput);
    }

    [Fact]
    public void TryEmit_PInvoke_UsesCorrectWrapperLibraryPath()
    {
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "SwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("SwiftBindings", csOutput);
    }

    [Fact]
    public void TryEmit_BindingReport_RecordsWrappedItem()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        ReportCollector.Start(moduleDecl);
        EmitConstructor(constructor, typeDatabase);
        var report = ReportCollector.Complete();

        Assert.NotNull(report);
        Assert.Single(report.WrappedItems);
        Assert.Equal("ExistentialBypass", report.WrappedItems[0].WrapperKind);
        Assert.NotNull(report.WrappedItems[0].MangledName);

        ReportCollector.Reset();
    }

    // --- Fix validation tests ---

    [Fact]
    public void TryEmit_FailableConstructor_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            isFailable: true,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Failable constructors are not supported — bypass should not be attempted
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_ThrowingConstructor_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            throws: true,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Throwing constructors are not supported — bypass should not be attempted
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_UnlabeledArgParam_OmitsLabelInSwiftCall()
    {
        // Auto-generated arg names (arg0, arg1) are unlabeled in Swift.
        // Real names like "argIndex" or "arguments" should keep their labels.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgumentWithNames("arg0", "arg0", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        Assert.NotEqual(string.Empty, swiftOutput);
        // The init call should NOT use "arg0:" label — auto-generated names use bare value.
        Assert.Contains("Config(arg0)", swiftOutput);
    }

    [Fact]
    public void TryEmit_UnderscorePrefixParam_StripsUnderscoreForLabel()
    {
        // Parameters starting with "_" use the stripped name as the label
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgumentWithNames("_value", "_value", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        Assert.NotEqual(string.Empty, swiftOutput);
        // Should contain "value:" (stripped underscore) as the label
        Assert.Contains("value: _value", swiftOutput);
    }

    [Fact]
    public void RenderSwiftTypeSpec_NamedTypeWithGenericArgs_IncludesGenericParams()
    {
        var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));

        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);

        Assert.Equal("Array<Int>", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_NestedGenerics_RendersRecursively()
    {
        var inner = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.String"));
        var outer = new NamedTypeSpec("Swift.Array", inner);

        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(outer);

        Assert.Equal("Array<Optional<String>>", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_SimpleType_StripsModule()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int");

        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);

        Assert.Equal("Int", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_EmptyTuple_ReturnsVoid()
    {
        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(TupleTypeSpec.Empty);

        Assert.Equal("Void", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_NestedType_AppendsInnerName()
    {
        // StreamOf<E>.Iterator — outer has generic params, inner is bare.
        // Each nesting level carries only its own generics; we must not
        // re-emit the outer's params on the inner (StreamOf<E>.Iterator,
        // never StreamOf<E>.Iterator<E>).
        var outer = new NamedTypeSpec("TestModule.StreamOf", new NamedTypeSpec("E"));
        outer.InnerType = new NamedTypeSpec("Iterator");

        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(outer);

        Assert.Equal("StreamOf<E>.Iterator", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_NestedType_InnerWithOwnGenerics()
    {
        // Outer.Inner<T> — only inner has generics.
        var outer = new NamedTypeSpec("TestModule.Outer");
        outer.InnerType = new NamedTypeSpec("Inner", new NamedTypeSpec("T"));

        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(outer);

        Assert.Equal("Outer.Inner<T>", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_NestedType_ModuleQualifiedOuterOnly()
    {
        // Module-qualified rendering keeps the module on the outer and leaves
        // inner segments unqualified (nested types aren't module-prefixed in Swift).
        var outer = new NamedTypeSpec("TestModule.StreamOf", new NamedTypeSpec("Swift.Int"));
        outer.InnerType = new NamedTypeSpec("Iterator");

        var result = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(outer);

        Assert.Equal("TestModule.StreamOf<Swift.Int>.Iterator", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_TripleNested_RendersAllLevels()
    {
        // Outer.Middle.Leaf — three levels deep.
        var outer = new NamedTypeSpec("TestModule.Outer");
        var middle = new NamedTypeSpec("Middle");
        var leaf = new NamedTypeSpec("Leaf");
        middle.InnerType = leaf;
        outer.InnerType = middle;

        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(outer);

        Assert.Equal("Outer.Middle.Leaf", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_NestedType_InnerGenericArgs_KeepModuleQualification()
    {
        // Outer.Inner<Swift.Int>: the inner segment's NAME is unqualified (outer has the
        // module prefix), but its generic arguments must still carry module qualification
        // when caller requests it, otherwise Swift.Int would flatten to Int and resolve
        // incorrectly when multiple modules define `Int`.
        var outer = new NamedTypeSpec("TestModule.Outer");
        outer.InnerType = new NamedTypeSpec("Inner", new NamedTypeSpec("Swift.Int"));

        var result = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(outer);

        Assert.Equal("TestModule.Outer.Inner<Swift.Int>", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_TripleNested_InnerGenericArgs_KeepModuleQualification()
    {
        // Outer.Middle<Swift.Int>.Leaf<Swift.String>: every nested level's generic args
        // must keep qualification when the caller requested it.
        var outer = new NamedTypeSpec("TestModule.Outer");
        var middle = new NamedTypeSpec("Middle", new NamedTypeSpec("Swift.Int"));
        var leaf = new NamedTypeSpec("Leaf", new NamedTypeSpec("Swift.String"));
        middle.InnerType = leaf;
        outer.InnerType = middle;

        var result = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(outer);

        Assert.Equal("TestModule.Outer.Middle<Swift.Int>.Leaf<Swift.String>", result);
    }

    [Fact]
    public void TryEmit_BoundGenericPassthroughNeedingMarshalling_ReturnsFalse()
    {
        // Passthrough param of a bound generic type that needs marshalling (Array<Int> is
        // non-frozen with memory management) should be rejected because the factory can't
        // set up the required marshalling locals.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        // Passthrough param: Array<Int> (bound generic but no existential)
        var arrayOfInt = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                // This is a bound generic but NOT existential, so it's a passthrough.
                // However, SwiftArray needs marshalling → wrapper/P/Invoke sigs differ → rejected.
                CreateArgument("items", arrayOfInt, moduleDecl),
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Bypass rejected because passthrough arg requires marshalling
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void TryEmit_GenericTypeParameterPassthrough_ReturnsFalse()
    {
        // Passthrough param that is a generic type parameter (IsGeneric=true) should be
        // rejected because the reduced method has no GenericTypeMapping entries.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Config", moduleDecl, typeDatabase);

        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateGenericArgument("value", new NamedTypeSpec("T"), moduleDecl),
                CreateArgument("options", new NamedTypeSpec("Swift.Array", existentialArg), moduleDecl, hasDefault: true)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Bypass rejected because passthrough arg is a generic type parameter
        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    // --- Helper methods ---

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static StructDecl CreateFrozenStructDecl(string name, ModuleDecl moduleDecl, TypeDatabase typeDatabase)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VMa"
        };
        moduleDecl.Types.Add(structDecl);

        // Register the type in the database so marshalling can find it
        var moduleName = moduleDecl.Name;
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
                MetadataAccessor = structDecl.MetadataAccessor,
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });

        return structDecl;
    }

    private static MethodDecl CreateConstructorDecl(
        string name,
        StructDecl parentDecl,
        ModuleDecl moduleDecl,
        bool throws = false,
        bool isFailable = false,
        List<ArgumentDecl>? parameters = null)
    {
        var signature = new List<ArgumentDecl>
        {
            CreateArgument(string.Empty, new NamedTypeSpec($"{moduleDecl.Name}.{parentDecl.Name}"), moduleDecl)
        };
        if (parameters != null)
        {
            signature.AddRange(parameters);
        }

        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule6ConfigV{name}yACyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            IsFailable = isFailable,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl, bool hasDefault = false)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            HasDefaultArg = hasDefault,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static ArgumentDecl CreateGenericArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = true,
            HasDefaultArg = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static ArgumentDecl CreateArgumentWithNames(string name, string privateName, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = privateName,
            IsInOut = false,
            IsGeneric = false,
            HasDefaultArg = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static (string csOutput, string swiftOutput) EmitConstructor(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        // Per-test ModuleEmissionContext so the structural-claim guard in ExistentialBypassEmitter
        // does not see prior tests' wrapper-symbol claims via the shared Default singleton.
        var context = TypeHandlerContext.Empty with { EmissionContext = new ModuleEmissionContext() };
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EC-7: RenderModuleQualifiedSwiftTypeSpec tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void RenderModuleQualifiedSwiftTypeSpec_SimpleType_KeepsModule()
    {
        var typeSpec = new NamedTypeSpec("AttributedTextKit.StringStyle");
        var result = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(typeSpec);
        Assert.Equal("AttributedTextKit.StringStyle", result);
    }

    [Fact]
    public void RenderModuleQualifiedSwiftTypeSpec_GenericType_KeepsModuleOnAll()
    {
        var inner = new NamedTypeSpec("Swift.String");
        var outer = new NamedTypeSpec("Swift.Optional", inner);
        var result = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(outer);
        Assert.Equal("Swift.Optional<Swift.String>", result);
    }

    [Fact]
    public void RenderModuleQualifiedSwiftTypeSpec_UnqualifiedType_ReturnsAsIs()
    {
        // Types without module prefix (e.g., raw generic params) pass through unchanged
        var typeSpec = new NamedTypeSpec("Int");
        var result = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(typeSpec);
        Assert.Equal("Int", result);
    }

    [Fact]
    public void RenderSwiftTypeSpec_SimpleType_StripsModule_StillWorks()
    {
        // Verify unqualified rendering still works (backward compat)
        var typeSpec = new NamedTypeSpec("AttributedTextKit.StringStyle");
        var result = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
        Assert.Equal("StringStyle", result);
    }

    [Fact]
    public void RenderModuleQualifiedSwiftTypeSpec_Tuple_QualifiesElements()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });
        var result = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(tuple);
        Assert.Equal("(Swift.Int, Swift.String)", result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EC-16: IsAnyObjectType + AnyObject return mapping tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void IsAnyObjectType_ProtocolList_ReturnsTrue()
    {
        var anyObject = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("AnyObject") });
        Assert.True(CdeclParamMapper.IsAnyObjectType(anyObject));
    }

    [Fact]
    public void IsAnyObjectType_QualifiedProtocolList_ReturnsTrue()
    {
        var anyObject = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.AnyObject") });
        Assert.True(CdeclParamMapper.IsAnyObjectType(anyObject));
    }

    [Fact]
    public void IsAnyObjectType_NamedType_ReturnsTrue()
    {
        var anyObject = new NamedTypeSpec("AnyObject");
        Assert.True(CdeclParamMapper.IsAnyObjectType(anyObject));
    }

    [Fact]
    public void IsAnyObjectType_RegularProtocol_ReturnsFalse()
    {
        var proto = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Equatable") });
        Assert.False(CdeclParamMapper.IsAnyObjectType(proto));
    }

    [Fact]
    public void GetCdeclReturnMapping_AnyObject_ReturnsClassPointer()
    {
        var typeDb = new TypeDatabase();
        var anyObject = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("AnyObject") });
        var (mapping, needsResultPtr) = CdeclReturnMapping.Classify(anyObject, typeDb);

        Assert.Equal(CdeclReturnKind.ClassPointer, mapping.Kind);
        Assert.False(needsResultPtr);
    }
}
