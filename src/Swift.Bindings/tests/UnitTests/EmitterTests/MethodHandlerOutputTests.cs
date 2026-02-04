// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class MethodHandlerOutputTests
{
    [Fact]
    public void Emit_AsyncMethod_UsesAsyncLibraryAndReturnsTask()
    {
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "/tmp/AsyncWrapper.dylib";

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "fetch",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: true,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("[DllImport(\"/tmp/AsyncWrapper.dylib\"", csOutput);
        Assert.Contains("public unsafe Task<System.Int64> Fetch()", csOutput);
        Assert.Contains("return task.Task;", csOutput);
    }

    [Fact]
    public void Emit_ThrowingMethod_EmitsSwiftErrorHandling()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "load",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: true,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("out SwiftError error", csOutput);
        Assert.Contains("if (error.Value != null)", csOutput);
        Assert.Contains("throw new SwiftRuntimeException(\"Call to Swift method load failed.\")", csOutput);
    }

    [Fact]
    public void Emit_MethodNameCollidesWithProperty_AppendsMethodSuffix()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "fetch",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var siblingProperties = new HashSet<string> { "Fetch" };
        var (csOutput, _) = EmitMethod(method, typeDatabase, siblingProperties);

        Assert.Contains("public unsafe System.Int64 FetchMethod()", csOutput);
    }

    [Fact]
    public void Emit_StaticMethod_DoesNotRequireUnsafe()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "count",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Static);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("public static System.Int64 Count()", csOutput);
        var signatureLine = Array.Find(csOutput.Split('\n'), line => line.Contains("public static System.Int64 Count()", StringComparison.Ordinal));
        Assert.NotNull(signatureLine);
        Assert.DoesNotContain("unsafe", signatureLine!, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_GenericMethod_WithProtocolConstraint_EmitsWhereClause()
    {
        var typeDatabase = CreateTypeDatabase();
        RegisterProtocol(typeDatabase, "TestModule.Loadable", TypeRecordFlags.None);

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "decode",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance,
            genericParameters: new List<GenericArgumentDecl>
            {
                CreateGenericArgumentWithProtocolConformance("T", "TestModule.Loadable")
            });

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("public unsafe System.Int64 Decode<T0>()", csOutput);
        Assert.Contains("where T0 : ISwiftObject, ISwiftLoadable", csOutput);
    }

    [Fact]
    public void Emit_GenericMethod_WithAssociatedTypeProtocolConstraint_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabase();
        RegisterProtocol(typeDatabase, "TestModule.SequenceLike", TypeRecordFlags.HasAssociatedTypes);

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "decode",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance,
            genericParameters: new List<GenericArgumentDecl>
            {
                CreateGenericArgumentWithProtocolConformance("T", "TestModule.SequenceLike")
            });

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_ModuleLevelMethod_IsAlwaysStatic()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var method = new MethodDecl
        {
            Name = "version",
            MangledName = "$s10TestModule7versionSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("public static System.Int64 Version()", csOutput);
    }

    [Fact]
    public void Emit_MethodWithUnknownParameterType_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "decode",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        method.CSSignature.Add(CreateArgument("unknown", new NamedTypeSpec("Missing.Type"), moduleDecl));

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_MethodWithUnsupportedClosureFallback_EmitsUnsupportedSwiftTypeAttribute()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var unsupportedClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new NamedTypeSpec("T")),
            TupleTypeSpec.Empty);
        var method = CreateMethodDecl(
            name: "boxedHandler",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("TestModule.Box", unsupportedClosure),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("[global::Swift.UnsupportedSwiftType(\"Unsupported closure fallback\",", csOutput);
        Assert.Contains("public unsafe Swift.TestModule.Box<object> BoxedHandler()", csOutput);
    }

    [Fact]
    public void Emit_MethodWithNonFrozenEnumParameter_UsesIntPtrInPInvokeSignature()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var method = CreateMethodDecl(
            name: "setDenominator",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("denominator", new NamedTypeSpec("TestModule.ColorFormatDenominator"), moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("IntPtr denominator", csOutput);
        Assert.Contains("denominator.Payload.DangerousGetHandle()", csOutput);
    }

    [Fact]
    public void Emit_MethodWithEscapingClosureReturningFrozenStruct_EmitsTypedCallbackReturn()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Double") }),
            new NamedTypeSpec("TestModule.Box"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl(
            name: "handle",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("private static Swift.TestModule.Box handle_callback_", csOutput);
        Assert.DoesNotContain("private static void* handle_callback_", csOutput);
        Assert.Contains("return del(", csOutput);
        Assert.Contains("delegate* unmanaged[Swift]<double, SwiftSelf, Swift.TestModule.Box>", csOutput);
    }

    [Fact]
    public void Emit_MethodWithEscapingClosureReturningFrozenStructMappedToScalar_EmitsScalarReturnType()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Double") }),
            new NamedTypeSpec("TestModule.Scalar"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl(
            name: "sample",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("private static System.Double sample_callback_", csOutput);
        Assert.DoesNotContain("private static void* sample_callback_", csOutput);
        Assert.Contains("delegate* unmanaged[Swift]<double, SwiftSelf, System.Double>", csOutput);
    }

    [Fact]
    public void Emit_MethodWithEscapingClosureReturningNonFrozenStruct_UsesIndirectReturnCallback()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Double") }),
            new NamedTypeSpec("TestModule.LottieColor"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodDecl(
            name: "tint",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: TupleTypeSpec.Empty,
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);
        method.CSSignature.Add(CreateArgument("callback", closureType, moduleDecl));

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("private static void tint_callback_", csOutput);
        Assert.Contains("(void* indirectResult, double arg0, SwiftSelf context)", csOutput);
        Assert.Contains("delegate* unmanaged[Swift]<void*, double, SwiftSelf, void>", csOutput);
        Assert.DoesNotContain("private static void* tint_callback_", csOutput);
    }

    [Fact]
    public void Emit_MethodWithSupportedExistentialBoundGeneric_EmitsSuccessfully()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);
        var existentialArg = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") });
        var method = CreateMethodDecl(
            name: "existentialBox",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec("TestModule.Box", existentialArg),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        // Supported existentials (1 protocol) should now emit with ExistentialContainer1
        Assert.NotEqual(string.Empty, csOutput);
        Assert.Contains("ExistentialContainer1", csOutput);
    }

    [Fact]
    public void Emit_MethodWithUnsatisfiedBoundGenericConstraint_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var constrainedStorageDecl = CreateGenericStructDecl(
            "ValueProviderStorage",
            moduleDecl,
            "T",
            "TestModule.AnyInterpolatable");
        CreateStructDecl("LottieVector3D", moduleDecl);

        var method = CreateMethodDecl(
            name: "storage",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec(
                constrainedStorageDecl.SwiftTypeName.ModuleQualifiedName,
                new NamedTypeSpec("TestModule.LottieVector3D")),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    [Fact]
    public void Emit_MethodWithExternalTypeUnsatisfiedBoundGenericConstraint_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var constrainedStorageDecl = CreateGenericStructDecl(
            "ValueProviderStorage",
            moduleDecl,
            "T",
            "TestModule.AnyInterpolatable");

        var method = CreateMethodDecl(
            name: "storageExternal",
            parentDecl: parentDecl,
            moduleDecl: moduleDecl,
            returnType: new NamedTypeSpec(
                constrainedStorageDecl.SwiftTypeName.ModuleQualifiedName,
                new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Double"))),
            isAsync: false,
            throws: false,
            methodType: MethodType.Instance);

        var (csOutput, swiftOutput) = EmitMethod(method, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
                MetadataAccessor = "$sSdMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Box"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
                MetadataAccessor = "$s10TestModule3BoxVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Scalar"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Scalar"),
                MetadataAccessor = "$s10TestModule6ScalarVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.LottieColor"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "LottieColor"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.LottieColor"),
                MetadataAccessor = "$s10TestModule11LottieColorVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ColorFormatDenominator"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "ColorFormatDenominator"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ColorFormatDenominator"),
                MetadataAccessor = "$s10TestModule22ColorFormatDenominatorOMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Enum
            });
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

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
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
        return classDecl;
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
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
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VMa",
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static StructDecl CreateGenericStructDecl(string name, ModuleDecl moduleDecl, string typeParameterName, string constraintProtocolName)
    {
        var structDecl = CreateStructDecl(name, moduleDecl);
        structDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new(
                TypeName: $"τ_0_0",
                SugaredTypeName: typeParameterName,
                GenericConformances: new List<GenericParameterConformance>
                {
                    new(
                        Path: new[] { $"τ_0_0" },
                        ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(constraintProtocolName),
                        Kind: ConformanceKind.Protocol)
                },
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };
        return structDecl;
    }

    private static MethodDecl CreateMethodDecl(
        string name,
        ClassDecl parentDecl,
        ModuleDecl moduleDecl,
        TypeSpec returnType,
        bool isAsync,
        bool throws,
        MethodType methodType,
        List<GenericArgumentDecl>? genericParameters = null)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule6LoaderC{name}SiyF",
            MethodType = methodType,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl)
            },
            GenericParameters = genericParameters ?? new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);
        return method;
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

    private static void RegisterProtocol(TypeDatabase typeDatabase, string protocolName, TypeRecordFlags flags)
    {
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName(protocolName), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", protocolName.Split('.')[1]),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                MetadataAccessor = "$s10TestModule8ProtocolPAAWP",
                Flags = flags,
                Kind = TypeRecordKind.Protocol
            })
        });
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = string.Empty,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static (string csOutput, string swiftOutput) EmitMethod(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase,
        IReadOnlySet<string>? siblingPropertyNames = null)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(methodDecl, typeDatabase, siblingPropertyNames);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor);

        return (csOutput.ToString(), swiftOutput.ToString());
    }
}
