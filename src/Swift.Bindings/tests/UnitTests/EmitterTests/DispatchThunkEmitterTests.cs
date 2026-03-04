// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for dispatch thunk (Tj suffix) emission on class instance methods.
/// With library evolution, non-final class instance methods use vtable dispatch
/// and only the Tj thunk symbol is globally exported. Final classes and final
/// members use direct dispatch (bare symbols exported, no Tj).
/// </summary>
public class DispatchThunkEmitterTests
{
    #region Non-Final Class + Non-Final Method => Tj

    [Fact]
    public void NonFinalClass_NonFinalInstanceMethod_GetsTjSuffix()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Animal", moduleDecl, isFinal: false);
        var method = CreateMethodDecl("speak", classDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            methodType: MethodType.Instance, isFinal: false);
        method.MangledName = "$s10TestModule6AnimalC5speakSiyF";

        var (csOutput, _) = EmitMethod(method, CreateTypeDatabase());

        Assert.Contains("$s10TestModule6AnimalC5speakSiyFTj", csOutput);
    }

    [Fact]
    public void NonFinalClass_NonFinalPropertyGetter_GetsTjSuffix()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Animal", moduleDecl, isFinal: false);
        var method = CreatePropertyGetMethod("name", classDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isFinal: false);
        method.MangledName = "$s10TestModule6AnimalC4nameSivg";

        var (csOutput, _) = EmitMethod(method, CreateTypeDatabase());

        Assert.Contains("$s10TestModule6AnimalC4nameSivgTj", csOutput);
    }

    [Fact]
    public void NonFinalClass_NonFinalPropertySetter_GetsTjSuffix()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Animal", moduleDecl, isFinal: false);
        var method = CreatePropertySetMethod("name", classDecl, moduleDecl,
            valueType: new NamedTypeSpec("Swift.Int"), isFinal: false);
        method.MangledName = "$s10TestModule6AnimalC4nameSivs";

        var (csOutput, _) = EmitMethod(method, CreateTypeDatabase());

        Assert.Contains("$s10TestModule6AnimalC4nameSivsTj", csOutput);
    }

    #endregion

    #region Non-Final Class + Final Method => No Tj

    [Fact]
    public void NonFinalClass_FinalInstanceMethod_NoTjSuffix()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Service", moduleDecl, isFinal: false);
        var method = CreateMethodDecl("getKey", classDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            methodType: MethodType.Instance, isFinal: true);
        method.MangledName = "$s10TestModule7ServiceC6getKeySiyF";

        var (csOutput, _) = EmitMethod(method, CreateTypeDatabase());

        Assert.Contains("$s10TestModule7ServiceC6getKeySiyF\"", csOutput);
        Assert.DoesNotContain("Tj", csOutput);
    }

    [Fact]
    public void NonFinalClass_FinalPropertyGetter_NoTjSuffix()
    {
        // Stored let properties in non-final classes are final
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Service", moduleDecl, isFinal: false);
        var method = CreatePropertyGetMethod("key", classDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isFinal: true);
        method.MangledName = "$s10TestModule7ServiceC3keySivg";

        var (csOutput, _) = EmitMethod(method, CreateTypeDatabase());

        Assert.Contains("$s10TestModule7ServiceC3keySivg\"", csOutput);
        Assert.DoesNotContain("Tj", csOutput);
    }

    #endregion

    #region Final Class => No Tj

    [Fact]
    public void FinalClass_InstanceMethod_NoTjSuffix()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Handler", moduleDecl, isFinal: true);
        var method = CreateMethodDecl("fire", classDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            methodType: MethodType.Instance, isFinal: false);
        method.MangledName = "$s10TestModule7HandlerC4fireSiyF";

        var (csOutput, _) = EmitMethod(method, CreateTypeDatabase());

        Assert.Contains("$s10TestModule7HandlerC4fireSiyF\"", csOutput);
        Assert.DoesNotContain("Tj", csOutput);
    }

    [Fact]
    public void FinalClass_PropertyGetter_NoTjSuffix()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Handler", moduleDecl, isFinal: true);
        var method = CreatePropertyGetMethod("label", classDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"), isFinal: false);
        method.MangledName = "$s10TestModule7HandlerC5labelSivg";

        var (csOutput, _) = EmitMethod(method, CreateTypeDatabase());

        Assert.Contains("$s10TestModule7HandlerC5labelSivg\"", csOutput);
        Assert.DoesNotContain("Tj", csOutput);
    }

    #endregion

    #region Constructor and Static => No Tj

    [Fact]
    public void NonFinalClass_StaticMethod_NoTjSuffix()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Animal", moduleDecl, isFinal: false);
        var method = CreateMethodDecl("createDefault", classDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            methodType: MethodType.Static, isFinal: false);
        method.MangledName = "$s10TestModule6AnimalC13createDefaultSiyFZ";

        var (csOutput, _) = EmitMethod(method, CreateTypeDatabase());

        Assert.DoesNotContain("Tj", csOutput);
    }

    [Fact]
    public void NonFinalClass_Constructor_NoTjSuffix()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Animal", moduleDecl, isFinal: false);
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule6AnimalCACycfC",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.Animal"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        classDecl.Methods.Add(method);

        var (csOutput, _) = EmitConstructor(method, CreateTypeDatabase());

        Assert.DoesNotContain("Tj", csOutput);
    }

    #endregion

    #region Struct => No Tj (not a ClassDecl)

    [Fact]
    public void Struct_InstanceMethod_NoTjSuffix()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("Point", moduleDecl, isFrozen: true);
        var method = CreateMethodDecl("getX", structDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            methodType: MethodType.Instance, isFinal: false);
        method.MangledName = "$s10TestModule5PointV4getXSiyF";

        var (csOutput, _) = EmitMethod(method, CreateTypeDatabase());

        Assert.DoesNotContain("Tj", csOutput);
    }

    #endregion

    #region Wrapper Library => No Tj

    [Fact]
    public void NonFinalClass_WrapperLibMethod_NoTjSuffix()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Animal", moduleDecl, isFinal: false);
        var method = CreateMethodDecl("speak", classDecl, moduleDecl,
            returnType: new NamedTypeSpec("Swift.Int"),
            methodType: MethodType.Instance, isFinal: false);
        method.MangledName = "$s10TestModule6AnimalC5speakSiyF";
        method.UsesWrapperLibrary = true;

        var typeDb = CreateTypeDatabase();
        typeDb.AsyncLibraryName = "SwiftBindings";
        var (csOutput, _) = EmitMethod(method, typeDb);

        Assert.DoesNotContain("Tj", csOutput);
    }

    #endregion

    #region Helper Methods

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
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Animal"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Animal"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Animal"),
                MetadataAccessor = "$s10TestModule6AnimalCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Service"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Service"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Service"),
                MetadataAccessor = "$s10TestModule7ServiceCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Handler"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Handler"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Handler"),
                MetadataAccessor = "$s10TestModule7HandlerCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
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

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl, bool isFinal)
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
            ModuleDecl = moduleDecl,
            IsFinal = isFinal,
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl, bool isFrozen)
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
            IsFrozen = isFrozen,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static MethodDecl CreateMethodDecl(
        string name,
        TypeDecl parentDecl,
        ModuleDecl moduleDecl,
        TypeSpec returnType,
        MethodType methodType,
        bool isFinal)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule6LoaderC{name}SiyF",
            MethodType = methodType,
            IsConstructor = false,
            IsFinal = isFinal,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        if (parentDecl is ClassDecl classDecl)
            classDecl.Methods.Add(method);
        else if (parentDecl is StructDecl structDecl)
            structDecl.Methods.Add(method);
        return method;
    }

    private static MethodDecl CreatePropertyGetMethod(
        string fieldName,
        TypeDecl parentDecl,
        ModuleDecl moduleDecl,
        TypeSpec returnType,
        bool isFinal)
    {
        var method = new MethodDecl
        {
            Name = $"{fieldName}_Get",
            MangledName = $"$s10TestModule6LoaderC{fieldName}Sivg",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            IsFinal = isFinal,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Private
        };
        if (parentDecl is ClassDecl classDecl)
            classDecl.Methods.Add(method);
        return method;
    }

    private static MethodDecl CreatePropertySetMethod(
        string fieldName,
        TypeDecl parentDecl,
        ModuleDecl moduleDecl,
        TypeSpec valueType,
        bool isFinal)
    {
        var method = new MethodDecl
        {
            Name = $"{fieldName}_Set",
            MangledName = $"$s10TestModule6LoaderC{fieldName}Sivs",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            IsFinal = isFinal,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument("value", valueType, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Private
        };
        if (parentDecl is ClassDecl classDecl)
            classDecl.Methods.Add(method);
        return method;
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
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static (string csOutput, string swiftOutput) EmitConstructor(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    #endregion
}
