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

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // Constructor is skipped — no output emitted
        Assert.Equal(string.Empty, csOutput);
    }

    [Fact]
    public void Emit_ThrowingConstructor_EmitsSwiftErrorPath()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl, throws: true);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("out SwiftError error", csOutput);
        Assert.Contains("if (error.Value != null)", csOutput);
        Assert.Contains("throw new SwiftRuntimeException(\"Call to Swift method init failed.\")", csOutput);
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

        Assert.Contains("SwiftClosureData", csOutput);
        Assert.Contains("GCHandle callbackHandle", csOutput);
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

        Assert.Equal(string.Empty, csOutput);
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
        Assert.Contains("public unsafe Animal(", csOutput);
        // Should NOT contain a return type (constructors don't have one)
        Assert.DoesNotContain("Swift.TestModule.Animal Init(", csOutput);
        Assert.DoesNotContain("return ", csOutput);
    }

    [Fact]
    public void Emit_ClassConstructor_UsesIndirectResult()
    {
        // Non-frozen class constructors require indirect result: allocate _payload and
        // pass it as SwiftIndirectResult to the P/Invoke.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass("init", parentDecl, moduleDecl);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("_payload = new SwiftSafeHandle<Animal>", csOutput);
        Assert.Contains("NativeMemory.Alloc(_payloadSize)", csOutput);
        Assert.Contains("new SwiftIndirectResult", csOutput);
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

        Assert.Contains("public unsafe Animal(", csOutput);
        // The extern P/Invoke should use IntPtr for the enum parameter
        var lines = csOutput.Split('\n');
        var externLine = Array.Find(lines, line => line.Contains("extern", StringComparison.Ordinal));
        Assert.NotNull(externLine);
        Assert.Contains("IntPtr", externLine);
        Assert.DoesNotContain("Swift.TestModule.Variant", externLine);
    }

    [Fact]
    public void Emit_FailableClassConstructor_Skipped()
    {
        // Failable initializers on classes are not yet supported in TryCreate() pattern.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass("init", parentDecl, moduleDecl, isFailable: true);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Equal(string.Empty, csOutput);
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$s10TestModule5PointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", name),
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Variant"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
                MetadataAccessor = "$s10TestModule7VariantOMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            })
        });
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
        handler.Emit(csWriter, swiftWriter, env, conductor);

        return (csOutput.ToString(), swiftOutput.ToString());
    }
}
