// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit tests for DefaultParameterOverloadEmitter.
/// Validates CountTrailingDefaults, BuildOverloadDecl, and TryEmitOverloads skip guards.
/// </summary>
public class DefaultParameterOverloadEmitterTests
{
    #region CountTrailingDefaults Tests

    [Fact]
    public void CountTrailingDefaults_ZeroParams_ReturnsZero()
    {
        var method = CreateMethodWithArgs();
        Assert.Equal(0, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    [Fact]
    public void CountTrailingDefaults_AllDefaults_ReturnsAll()
    {
        var method = CreateMethodWithArgs(
            CreateArg("limit", hasDefault: true),
            CreateArg("offset", hasDefault: true),
            CreateArg("page", hasDefault: true));
        Assert.Equal(3, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    [Fact]
    public void CountTrailingDefaults_NonTrailingOnly_ReturnsZero()
    {
        // (query: String = "", page: Int) — default is NOT trailing
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: true),
            CreateArg("page", hasDefault: false));
        Assert.Equal(0, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    [Fact]
    public void CountTrailingDefaults_Mixed_ReturnsTrailingCount()
    {
        // (query: String, limit: Int = 10, offset: Int = 0) — 2 trailing defaults
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: false),
            CreateArg("limit", hasDefault: true),
            CreateArg("offset", hasDefault: true));
        Assert.Equal(2, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    [Fact]
    public void CountTrailingDefaults_OneTrailing_ReturnsOne()
    {
        var method = CreateMethodWithArgs(
            CreateArg("name", hasDefault: false),
            CreateArg("verbose", hasDefault: true));
        Assert.Equal(1, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    #endregion

    #region BuildOverloadDecl Tests

    [Fact]
    public void BuildOverloadDecl_SetsUsesWrapperLibrary()
    {
        var method = CreateMethodWithArgs(
            CreateArg("a", hasDefault: false),
            CreateArg("b", hasDefault: true));

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method, trimCount: 1);

        Assert.True(overload.UsesWrapperLibrary);
    }

    [Fact]
    public void BuildOverloadDecl_CorrectParamCount()
    {
        // Original: return + 3 params, trim 2 → return + 1 param
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: false),
            CreateArg("limit", hasDefault: true),
            CreateArg("offset", hasDefault: true));

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method, trimCount: 2);

        // CSSignature[0] is return type, rest are params
        Assert.Equal(2, overload.CSSignature.Count); // return + 1 kept param
        Assert.Equal("query", overload.CSSignature[1].Name);
    }

    #endregion

    #region TryEmitOverloads Skip Guard Tests

    [Fact]
    public void TryEmitOverloads_GenericParentType_SkipsOverloads()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericContainer");

        var parentDecl = new StructDecl
        {
            Name = "GenericContainer",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.GenericContainer"),
            MangledName = "$s10TestModule16GenericContainerVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule16GenericContainerVMa"
        };

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule16GenericContainerV7processSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("value", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var (csOutput, swiftOutput) = EmitOverloads(method, typeDb);

        // Generic parent type → no overloads emitted
        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    [Fact]
    public void TryEmitOverloads_SiblingCollision_SkipsOverload()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Fetcher");

        var parentDecl = new StructDecl
        {
            Name = "Fetcher",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Fetcher"),
            MangledName = "$s10TestModule7FetcherVN",
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
            MetadataAccessor = "$s10TestModule7FetcherVMa"
        };

        // Existing sibling: fetch(query: Int) — 1 param
        var existingSibling = new MethodDecl
        {
            Name = "fetch",
            MangledName = "$s10TestModule7FetcherV5fetchySiF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("query", hasDefault: false)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(existingSibling);

        // Method with default: fetch(query: Int, limit: Int = 10) — trim=1 would produce fetch(query:)
        // which collides with the existing sibling
        var method = new MethodDecl
        {
            Name = "fetch",
            MangledName = "$s10TestModule7FetcherV5fetchySi_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("query", hasDefault: false),
                CreateArg("limit", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);

        var (csOutput, swiftOutput) = EmitOverloads(method, typeDb);

        // Sibling collision → overload skipped
        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    #endregion

    #region Helpers

    private static ArgumentDecl CreateArg(string name, bool hasDefault)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasDefaultArg = hasDefault,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ArgumentDecl CreateReturnArg(ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = "",
            PrivateName = "",
            SwiftTypeSpec = TupleTypeSpec.Empty,
            HasDefaultArg = false,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    /// <summary>
    /// Creates a MethodDecl with the given args as parameters (return type auto-added as void).
    /// </summary>
    private static MethodDecl CreateMethodWithArgs(params ArgumentDecl[] args)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var csSignature = new List<ArgumentDecl>
        {
            CreateReturnArg(moduleDecl)
        };
        csSignature.AddRange(args);

        return new MethodDecl
        {
            Name = "testMethod",
            MangledName = "$s10TestModule10testMethodyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment(string typeName)
    {
        var typeDb = new TypeDatabase();

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
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", typeName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
                MetadataAccessor = $"$s10TestModule{typeName.Length}{typeName}VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(testModule);

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        return (moduleDecl, typeDb);
    }

    private static (string csOutput, string swiftOutput) EmitOverloads(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var logger = NullLogger.Instance;

        DefaultParameterOverloadEmitter.TryEmitOverloads(csWriter, swiftWriter, env, logger);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    #endregion
}
