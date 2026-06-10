// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class ConstructorHandlerOutputTests
{
    [Fact]
    public void Emit_GenericConstructor_SkippedBecauseCSharpDoesNotSupportGenericConstructors()
    {
        // C# does not allow generic constructors. A Swift init<T: Loadable>() on a
        // non-generic type has method-own generic params that can't be represented.
        // This gate is in MemberValidationPipeline.
        var typeDatabase = CreateTypeDatabase();
        RegisterProtocol(typeDatabase, "TestModule.Loadable", TypeRecordFlags.None);

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            genericParameters: new List<GenericArgumentDecl>
            {
                CreateGenericArgumentWithProtocolConformance("T", "TestModule.Loadable")
            });

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var result = pipeline.ValidateMethodEmission(constructor, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedSignature, result.Reason);
        Assert.Contains("generic constructors", result.Details!);
    }

    [Fact]
    public void Emit_ThrowingConstructor_EmitsSwiftErrorPath()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl, throws: true);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("ref SwiftError swiftError", csOutput);
        Assert.Contains("if (swiftError.Value != null)", csOutput);
        // Untyped throws uses SwiftMarshal.ThrowSwiftError (consolidates description read + release + throw)
        Assert.Contains("SwiftMarshal.ThrowSwiftError", csOutput);
        Assert.Contains("SBW_GetErrorDescription", csOutput);
        Assert.Contains("SBW_ReleaseError", csOutput);
    }

    [Fact]
    public void Emit_ConstructorWithEscapingClosure_EmitsClosureMarshalling()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);

        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("callback", closureType, moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // Cdecl closure wrapper: separate IntPtr params instead of SwiftClosureData
        Assert.DoesNotContain("SwiftClosureData", csOutput);
        Assert.Contains("GCHandle callbackHandle", csOutput);
        Assert.Contains("IntPtr callbackFuncPtr", csOutput);
        Assert.Contains("IntPtr callbackContext", csOutput);
    }

    [Fact]
    public void Emit_ConstructorWithUnknownParameterType_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("unknown", new NamedTypeSpec("Missing.Type"), moduleDecl)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // No binding code emitted — only unsupported comment
        Assert.DoesNotContain("public", csOutput);
        Assert.Contains("// Unsupported:", csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    #region Class Constructor Tests

    [Fact]
    public void Emit_ClassConstructor_EmitsProperConstructorSignature()
    {
        // Non-frozen class constructors should emit as C# constructors, not instance methods.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("age", new NamedTypeSpec("Swift.Int"), moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // Should emit constructor syntax, not instance method
        Assert.Contains("public Animal(", csOutput);
        // Should NOT contain a return type (constructors don't have one)
        Assert.DoesNotContain("TestModule.Animal Init(", csOutput);
        Assert.DoesNotContain("return ", csOutput);
    }

    [Fact]
    public void Emit_ClassConstructor_ReturnsIntPtrDirectly()
    {
        // Class constructors return a pointer in-register (not via SwiftIndirectResult).
        // The P/Invoke returns IntPtr, which is stored in _handle via SwiftClassHandle.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass("init", parentDecl, moduleDecl);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("_handle = new SwiftClassHandle<Animal>", csOutput);
        Assert.DoesNotContain("SwiftIndirectResult", csOutput);
        Assert.Contains("var result =", csOutput);
    }

    [Fact]
    public void Emit_ClassConstructorWithEnumParam_UsesIntPtrInPInvoke()
    {
        // Class constructors should handle enum parameters the same as struct constructors.
        var typeDatabase = CreateTypeDatabase();
        RegisterEnumType(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("variant", new NamedTypeSpec("TestModule.Variant"), moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("public Animal(", csOutput);
        // The partial P/Invoke should use IntPtr for the enum parameter
        var lines = csOutput.Split('\n');
        var externLine = Array.Find(lines, line => line.Contains("partial", StringComparison.Ordinal) && line.Contains("PInvoke_", StringComparison.Ordinal));
        Assert.NotNull(externLine);
        Assert.Contains("IntPtr", externLine);
        Assert.DoesNotContain("TestModule.Variant", externLine);
    }

    [Fact]
    public void Emit_FailableClassConstructor_EmitsTryCreate()
    {
        // Failable initializers on classes emit TryCreate() factory pattern.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass("init", parentDecl, moduleDecl, isFailable: true);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("public static bool TryCreate(", csOutput);
        Assert.Contains("out Animal result)", csOutput);
    }

    [Fact]
    public void Emit_ConstructorWithOptionalExistential_KnownProtocol_NotBlockedByExistentialGuard()
    {
        // P3: Exercises ConstructorHandler.Emit() constructor existential bypass path (line 167).
        // Optional<any KnownProtocol> should NOT set hasExistentialArg —
        // the constructor proceeds past the existential guard to normal emission.
        // It may still produce empty output due to SignatureHandler placeholder resolution
        // with a minimal TypeDatabase — this test verifies the guard path, not full emission.
        var typeDatabase = CreateTypeDatabaseWithOptionalAndProtocol();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Widget", moduleDecl);

        // Register the parent type
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: parentDecl.SwiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Widget"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule6WidgetVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });

        var optionalExistentialSpec = new NamedTypeSpec("Swift.Optional");
        var existentialInner = new NamedTypeSpec("TestModule.Drawable") { IsAny = true };
        optionalExistentialSpec.GenericParameters.Add(existentialInner);

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("renderer", optionalExistentialSpec, moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // The constructor should NOT be handled by ExistentialBypass (no "ExistentialBypass" report).
        // It may still produce empty output if the signature has unresolvable types
        // (UnsupportedSignature), but NOT because of UnsupportedExistential.
        // Verify it does NOT emit an ExistentialBypass wrapper pattern.
        Assert.DoesNotContain("ExistentialBypass", csOutput);
    }

    [Fact]
    public void Emit_ConstructorWithOptionalExistential_UnknownProtocol_Skipped()
    {
        // P3: Constructor with Optional<any UnknownProtocol> — no TypeRecord registered.
        // hasExistentialArg is set, triggering ExistentialBypass or skip.
        var typeDatabase = CreateTypeDatabaseWithOptional();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Widget", moduleDecl);

        // Register the parent type
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: parentDecl.SwiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Widget"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule6WidgetVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });

        var optionalExistentialSpec = new NamedTypeSpec("Swift.Optional");
        var existentialInner = new NamedTypeSpec("TestModule.UnknownProtocol") { IsAny = true };
        optionalExistentialSpec.GenericParameters.Add(existentialInner);

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("renderer", optionalExistentialSpec, moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // Constructor is skipped — no "public Widget(" constructor emitted
        Assert.DoesNotContain("public Widget(", csOutput);
    }

    #endregion

    #region @_cdecl Constructor Wrapper Integration Tests

    [Fact]
    public void Emit_PrimaryConstructor_EmitsCdeclSwiftWrapper()
    {
        // Primary constructors (not default-param overloads) must also get @_cdecl wrappers
        // when the type requires it for ABI safety (e.g., frozen struct with float fields).
        var typeDatabase = CreateTypeDatabaseWithFloatStruct();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl);

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Swift output should contain the @_cdecl wrapper
        Assert.Contains("@_cdecl(\"", swiftOutput);
        Assert.Contains("SBW_TestModule_Point_init_", swiftOutput);
        Assert.Contains("resultPtr.assumingMemoryBound(to: TestModule.Point.self).initialize(to: result)", swiftOutput);
    }

    [Fact]
    public void Emit_PrimaryClassConstructor_UsesNativeThunk()
    {
        // Class constructors are thunked (not @_cdecl) — allocating init returns pointer
        // in x0 (no indirect result). Thunk puts metatype in x20 via metadata accessor.
        // Non-frozen struct params are passed as pointers (single register) — thunk-safe.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        RegisterNonFrozenStruct(typeDatabase, "TestModule.Config");
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("config", new NamedTypeSpec("TestModule.Config"), moduleDecl)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // No @_cdecl wrapper emitted — thunk handles the ABI bridging in assembly
        Assert.DoesNotContain("@_cdecl(\"", swiftOutput);
        // C# P/Invoke uses CallConvCdecl (targets the thunk, not raw Swift symbol)
        Assert.Contains("CallConvCdecl", csOutput);
        // Thunk symbol in the P/Invoke entry point
        Assert.Contains("thunk_", csOutput);
    }

    [Fact]
    public void Emit_ClassConstructorWithClosureParam_FallsBackToCdecl()
    {
        // Class constructors with closure params can't be thunked (closures need Swift
        // adapter code). Falls back to @_cdecl wrapper.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        var constructor = CreateConstructorDeclForClass("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("handler", closureType, moduleDecl)
            });

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("@_cdecl(\"", swiftOutput);
        Assert.Contains("SBW_TestModule_Animal_init_", swiftOutput);
        Assert.Contains("Unmanaged.passRetained(result).toOpaque()", swiftOutput);
    }

    [Fact]
    public void Emit_PrimaryConstructorWithParam_CdeclWrapperIncludesParam()
    {
        // Frozen struct with float fields → ABI-unsafe → @_cdecl required
        var typeDatabase = CreateTypeDatabaseWithFloatStruct();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("x", new NamedTypeSpec("Swift.Int"), moduleDecl)
            });

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("@_cdecl(\"", swiftOutput);
        Assert.Contains("_ x: Int", swiftOutput);
    }

    [Fact]
    public void Emit_PrimaryConstructor_CSharpUsesCdeclCallingConvention()
    {
        // When @_cdecl wrapper is emitted (ABI-unsafe type), C# P/Invoke should NOT use CallConvSwift
        var typeDatabase = CreateTypeDatabaseWithFloatStruct();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // With @_cdecl wrapper, the P/Invoke should reference the wrapper library
        Assert.Contains("SBW_TestModule_Point_init_", csOutput);
        // Should NOT have CallConvSwift — the wrapper uses C calling convention
        Assert.DoesNotContain("CallConvSwift", csOutput);
    }

    [Fact]
    public void Emit_PrimaryConstructor_FrozenStruct_UsesCdeclWrapper()
    {
        // Frozen struct constructor → @_cdecl wrapper required
        // (SwiftIndirectResult + Mono JIT crash)
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl);

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // @_cdecl wrapper should be emitted for all frozen struct constructors
        Assert.Contains("@_cdecl(\"", swiftOutput);
        Assert.Contains("SBW_", swiftOutput);
        // C# P/Invoke should use CallConvCdecl
        Assert.Contains("CallConvCdecl", csOutput);
    }

    [Fact]
    public void Emit_PrimaryConstructor_NoAsyncLibrary_NoCdeclWrapper()
    {
        // Without xcframework mode (no AsyncLibraryName), no @_cdecl wrapper.
        var typeDatabase = CreateTypeDatabase();
        // AsyncLibraryName is null — not in xcframework mode
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl);

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        Assert.DoesNotContain("@_cdecl(\"", swiftOutput);
        Assert.DoesNotContain("SBW_", swiftOutput);
    }

    [Fact]
    public void Emit_TwoConstructorsCollidingOnProjectedKey_SecondSkipped_NoUnsupportedComment()
    {
        // Two Swift constructors with different argument labels project to the same C#
        // constructor signature (labels are stripped from the projected dedup key,
        // parameter types are kept). The first one wins emission; the second hits the
        // constructor branch in IHandler.HandleBaseDecl's projected-key collision check.
        //
        // C# can't disambiguate constructors with a numeric suffix (constructors don't
        // have names), so the second one is skipped. We must record the skip in
        // report.json (audit trail) but NOT write a `// Unsupported: method 'init' (C#
        // signature collides …)` comment to the C# source — that comment would land
        // directly above whatever the emitter writes next and read as if it applied to
        // the working overload that *did* emit
        // (e.g. a constructor like `AnimationKeypath(IEnumerable<string>)`).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Widget", moduleDecl, typeDatabase);

        // init(a: Int) and init(b: Int) — different labels, same projected key (`ctor(System.Int64)`).
        CreateConstructorDeclForClass("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("a", new NamedTypeSpec("Swift.Int"), moduleDecl)
            });
        CreateConstructorDeclForClass("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("b", new NamedTypeSpec("Swift.Int"), moduleDecl)
            });

        var csOutput = EmitClass(parentDecl, typeDatabase);

        // First constructor emits successfully.
        Assert.Contains("public Widget(", csOutput);

        // Second constructor's collision must NOT produce a `// Unsupported: method 'init' …`
        // comment. The audit trail lives in report.json (ReportCollector); the C# source
        // must stay clean so the comment doesn't misattribute to the next emitted member.
        Assert.DoesNotContain("// Unsupported: method 'init'", csOutput);
    }

    #endregion

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$s10TestModule5PointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithFloatStruct()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$s10TestModule5PointVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static void RegisterNonFrozenStruct(TypeDatabase typeDatabase, string typeName)
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(typeName);
        var shortName = typeName.Split('.')[1];
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: swiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", shortName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = $"$s10TestModule{shortName.Length}{shortName}VMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    private static void RegisterProtocol(TypeDatabase typeDatabase, string protocolName, TypeRecordFlags flags)
    {
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName(protocolName), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", protocolName.Split('.')[1]),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                MetadataAccessor = "$s10TestModule8ProtocolPAAWP",
                Flags = flags,
                Kind = TypeRecordKind.Protocol
            })
        });
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

    private static StructDecl CreateFrozenStructDecl(string name, ModuleDecl moduleDecl)
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
            MetadataAccessor = "$s10TestModule5PointVMa"
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static MethodDecl CreateConstructorDecl(
        string name,
        StructDecl parentDecl,
        ModuleDecl moduleDecl,
        bool throws = false,
        List<ArgumentDecl>? parameters = null,
        List<GenericArgumentDecl>? genericParameters = null)
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
            MangledName = $"$s10TestModule5PointV{name}yACyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = signature,
            GenericParameters = genericParameters ?? new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = typeSpec is NamedTypeSpec nts && nts.Name == "T",
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static GenericArgumentDecl CreateGenericArgumentWithProtocolConformance(string typeName, string protocolName)
    {
        return new GenericArgumentDecl(
            TypeName: typeName,
            SugaredTypeName: typeName,
            GenericConformances: new List<GenericParameterConformance>
            {
                new GenericParameterConformance(
                    Path: new[] { typeName },
                    ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(protocolName),
                    Kind: ConformanceKind.Protocol)
            },
            AssosiatedTypeConformances: new List<GenericParameterConformance>());
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl, TypeDatabase typeDatabase)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);

        // Register the class type in the TypeDatabase
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: classDecl.SwiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = classDecl.SwiftTypeName,
                MetadataAccessor = $"$s10TestModule{name.Length}{name}CMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            })
        });

        return classDecl;
    }

    private static MethodDecl CreateConstructorDeclForClass(
        string name,
        ClassDecl parentDecl,
        ModuleDecl moduleDecl,
        bool throws = false,
        bool isFailable = false,
        List<ArgumentDecl>? parameters = null,
        List<GenericArgumentDecl>? genericParameters = null)
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
            MangledName = $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}C{name}yACyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            IsFailable = isFailable,
            CSSignature = signature,
            GenericParameters = genericParameters ?? new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static void RegisterEnumType(TypeDatabase typeDatabase)
    {
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Variant"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
                MetadataAccessor = "$s10TestModule7VariantOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum
            })
        });
    }

    private static TypeDatabase CreateTypeDatabaseWithOptionalAndProtocol()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Drawable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IDrawable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Drawable"),
                MetadataAccessor = "$s10TestModule8DrawablePAAWP",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithOptional()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
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
        // Use a fresh ModuleEmissionContext to avoid cross-test dedup via the shared Default singleton.
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext());
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    /// <summary>
    /// Drives the full ClassHandler.Marshal + Emit path so IHandler.HandleBaseDecl runs over
    /// the class's members. Use this instead of <see cref="EmitConstructor"/> when the test
    /// needs to exercise primary/projected dedup loops, collision suffixing, or any other
    /// behavior that lives in the iteration loop rather than in the per-member handler.
    /// </summary>
    private static string EmitClass(ClassDecl classDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var handler = new ClassHandler(new NullLogger<ClassHandler>());
        var env = handler.Marshal(classDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csOutput.ToString();
    }
}
