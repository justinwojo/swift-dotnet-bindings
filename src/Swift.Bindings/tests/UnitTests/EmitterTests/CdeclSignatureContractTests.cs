// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="CdeclSignatureContract.DetermineParameterOrder"/>:
/// verifies the phase ordering rules for @_cdecl wrapper parameter layouts.
/// </summary>
public class CdeclSignatureContractTests
{
    #region Struct Constructor Tests

    [Fact]
    public void StructConstructor_HasResultPtrFirst()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyStruct");
        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        var method = CreateMethod("init", parentDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env);

        Assert.True(result.NeedsResultPtr);
        Assert.Equal(new[] { CdeclPhase.ResultPtr, CdeclPhase.Metadata }, result.Phases);
    }

    [Fact]
    public void ThrowingStructConstructor_ErrorBeforeArgs()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyStruct");
        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        var method = CreateMethodWithArgs("init", parentDecl, moduleDecl);
        method.IsConstructor = true;
        method.Throws = true;
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env);

        Assert.True(result.NeedsResultPtr);
        Assert.Equal(
            new[] { CdeclPhase.ResultPtr, CdeclPhase.ErrorOut, CdeclPhase.Arguments, CdeclPhase.Metadata },
            result.Phases);
    }

    #endregion

    #region Class Constructor Tests

    [Fact]
    public void ClassConstructor_NoResultPtr()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyClass");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);
        var method = CreateMethod("init", parentDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env);

        Assert.False(result.NeedsResultPtr);
        Assert.Equal(new[] { CdeclPhase.Metadata }, result.Phases);
    }

    [Fact]
    public void ThrowingClassConstructor_ErrorBeforeArgs()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyClass");
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);
        var method = CreateMethodWithArgs("init", parentDecl, moduleDecl);
        method.IsConstructor = true;
        method.Throws = true;
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env);

        Assert.False(result.NeedsResultPtr);
        Assert.Equal(
            new[] { CdeclPhase.ErrorOut, CdeclPhase.Arguments, CdeclPhase.Metadata },
            result.Phases);
    }

    #endregion

    #region Regular Method Tests

    [Fact]
    public void InstanceMethod_SelfAfterMetadata()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithArgs("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: false);

        Assert.Equal(
            new[] { CdeclPhase.Arguments, CdeclPhase.Metadata, CdeclPhase.Self },
            result.Phases);
    }

    [Fact]
    public void ThrowingMethod_ErrorLast()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithArgs("doWork", parentDecl, moduleDecl);
        method.Throws = true;
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: false);

        Assert.Equal(
            new[] { CdeclPhase.Arguments, CdeclPhase.Metadata, CdeclPhase.Self, CdeclPhase.ErrorOut },
            result.Phases);
    }

    [Fact]
    public void MethodWithIndirectResult_ResultPtrFirst()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithArgs("getStruct", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: true);

        Assert.True(result.NeedsResultPtr);
        Assert.Equal(
            new[] { CdeclPhase.ResultPtr, CdeclPhase.Arguments, CdeclPhase.Metadata, CdeclPhase.Self },
            result.Phases);
    }

    [Fact]
    public void StaticMethod_NoSelf()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithArgs("create", parentDecl, moduleDecl);
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: false);

        Assert.Equal(
            new[] { CdeclPhase.Arguments, CdeclPhase.Metadata },
            result.Phases);
        Assert.DoesNotContain(CdeclPhase.Self, result.Phases);
    }

    [Fact]
    public void MethodWithOnlyEmptyTupleParam_IncludesArguments()
    {
        // Regression: a method whose only non-debug parameter is
        // Void/empty-tuple — the shape of result-builder overloads like
        // `buildPartialBlock(first: Void)` (TipKit's Tips.GroupBuilder) — must still
        // run the Arguments phase. A Void parameter contributes no @_cdecl ABI
        // parameter, but Swift requires the argument at the call site, so the wrapper
        // must emit `make(first: ())`. Excluding empty tuples from HasArguments dropped
        // the phase and produced an invalid nullary call that fails to compile.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithEmptyTupleArg("make", parentDecl, moduleDecl);
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: false);

        Assert.Contains(CdeclPhase.Arguments, result.Phases);
    }

    #endregion

    #region Protocol Extension Tests

    [Fact]
    public void ProtocolExtension_SelfBeforeArgs()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithArgs("extMethod", parentDecl, moduleDecl);
        method.IsProtocolExtensionMethod = true;
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: false);

        Assert.Equal(
            new[] { CdeclPhase.Self, CdeclPhase.Arguments, CdeclPhase.Metadata },
            result.Phases);
    }

    #endregion

    #region Override Parameter Tests

    [Fact]
    public void OverrideNeedsResultPtr_PassedThrough()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithArgs("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: true);

        Assert.True(result.NeedsResultPtr);
        Assert.Contains(CdeclPhase.ResultPtr, result.Phases);
    }

    [Fact]
    public void OverrideHasArguments_True_IncludesArguments()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        // Method with no args in CSSignature
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env,
            overrideNeedsResultPtr: false, overrideHasArguments: true);

        Assert.Contains(CdeclPhase.Arguments, result.Phases);
    }

    [Fact]
    public void OverrideNeedsSelf_False_ExcludesSelf()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithArgs("doWork", parentDecl, moduleDecl);
        // Instance method would normally have Self, but override excludes it
        var env = new MethodEnvironment(method, typeDb);

        var result = CdeclSignatureContract.DetermineParameterOrder(env,
            overrideNeedsResultPtr: false, overrideNeedsSelf: false);

        Assert.DoesNotContain(CdeclPhase.Self, result.Phases);
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Creates a method with no arguments (only the return type entry in CSSignature).
    /// </summary>
    private static MethodDecl CreateMethod(string name, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    /// <summary>
    /// Creates a method with one argument (return type + one Swift.Int parameter).
    /// HasArguments will return true for this method.
    /// </summary>
    private static MethodDecl CreateMethodWithArgs(string name, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = "arg0",
                    PrivateName = "arg0",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    /// <summary>
    /// Creates a method whose single parameter is Void (empty tuple) — the shape of
    /// result-builder overloads like `buildPartialBlock(first: Void)`. HasArguments must
    /// return true so the Arguments phase forwards the call-site `first: ()`.
    /// </summary>
    private static MethodDecl CreateMethodWithEmptyTupleArg(string name, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty, // return slot
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty, // Void parameter `first: ()`
                    Name = "first",
                    PrivateName = "first",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
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
            MetadataAccessor = "$sMa"
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
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
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment(string typeName)
    {
        var typeDb = new TypeDatabase();

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
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
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
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", typeName),
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

    #endregion
}
