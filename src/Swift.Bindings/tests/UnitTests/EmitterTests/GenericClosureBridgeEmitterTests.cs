// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for GenericClosureBridgeEmitter — monomorphized Swift wrapper bridges
/// for methods with generic closure parameters.
/// </summary>
public class GenericClosureBridgeEmitterTests
{
    #region TryEmit gate: constructor returns false

    [Fact]
    public void TryEmit_Constructor_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("init", isConstructor: true);
        var env = CreateMethodEnvironment(method);

        var result = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null);

        Assert.False(result);
    }

    #endregion

    #region TryEmit gate: method already using wrapper library returns false

    [Fact]
    public void TryEmit_AlreadyUsesWrapperLib_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("doWork");
        method.UsesWrapperLibrary = true;
        var env = CreateMethodEnvironment(method);

        var result = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null);

        Assert.False(result);
    }

    #endregion

    #region TryEmit gate: no generic closure returns false

    [Fact]
    public void TryEmit_NoClosureParam_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("doWork");
        var env = CreateMethodEnvironment(method);

        var result = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null);

        Assert.False(result);
    }

    #endregion

    #region AreNonClosureParamsCompatible

    [Fact]
    public void AreNonClosureParamsCompatible_NoOtherParams_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var closureArg = CreateArg("block", new ClosureTypeSpec(
            new NamedTypeSpec("τ_0_0"), new NamedTypeSpec("τ_0_0")), moduleDecl);

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s4GRDB8Database4readyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                closureArg
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = CreateClassDecl("Database"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var result = GenericClosureBridgeEmitter.AreNonClosureParamsCompatible(
            method, closureArg, typeDatabase);

        Assert.True(result);
    }

    [Fact]
    public void AreNonClosureParamsCompatible_WithNonClosureParam_ReturnsFalse()
    {
        // IsIntPtrCompatibleParam returns false for all params currently
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var closureArg = CreateArg("block", new ClosureTypeSpec(
            new NamedTypeSpec("τ_0_0"), new NamedTypeSpec("τ_0_0")), moduleDecl);
        var extraArg = CreateArg("config", new NamedTypeSpec("TestModule.Config"), moduleDecl);

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s4GRDB8Database4readyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                closureArg,
                extraArg
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = CreateClassDecl("Database"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var result = GenericClosureBridgeEmitter.AreNonClosureParamsCompatible(
            method, closureArg, typeDatabase);

        Assert.False(result);
    }

    #endregion

    #region TryEmit: eligible method emits bridge

    [Fact]
    public void TryEmit_EligibleGenericClosure_EmitsBridge()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        // Closure: (Database) throws -> τ_0_0 — concrete class input, generic return
        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0"))
        {
            Throws = true
        };

        var closureArg = CreateArg("block", closureSpec, moduleDecl);

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s4GRDB8Database4readyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type = τ_0_0 (identity-forwarding)
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                closureArg
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        var handled = GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, ctx);

        Assert.True(handled);
        Assert.True(method.WasEmitted);
        Assert.True(method.HasGenericClosureBridge);
        Assert.True(method.UsesWrapperLibrary);

        var csResult = csOutput.ToString();
        var swResult = swiftOutput.ToString();

        // C# output should contain callbacks, P/Invokes, and public methods
        Assert.Contains("[UnmanagedCallersOnly", csResult);
        Assert.Contains("LibraryImport", csResult);
        Assert.Contains("SBW_CreateError", csResult);

        // Swift output should contain wrapper functions
        Assert.Contains("@_silgen_name", swResult);
        Assert.Contains("@_cdecl", swResult);
        Assert.Contains("SBW_CreateError", swResult);
    }

    #endregion

    #region TryEmit: error handling in throwing methods

    [Fact]
    public void TryEmit_ThrowingMethod_EmitsErrorHandling()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        // Closure: (Database) throws -> τ_0_0 — concrete class input, generic return
        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0"))
        {
            Throws = true
        };

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s4GRDB8Database4readyyF_v2",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type = τ_0_0 (identity-forwarding)
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                CreateArg("block", closureSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDatabase);
        GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var csResult = csOutput.ToString();

        // Error handling should be present in throwing methods
        Assert.Contains("swiftError", csResult);
        Assert.Contains("SwiftRuntimeException", csResult);
        Assert.Contains("SBW_GetErrorDescription", csResult);
        Assert.Contains("SBW_ReleaseError", csResult);
    }

    #endregion

    #region @MainActor annotation on @_silgen_name

    [Fact]
    public void TryEmit_MainActorParent_EmitsMainActorAnnotation()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("ViewModel");
        parentDecl.IsMainActorIsolated = true;

        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0"))
        {
            Throws = true
        };

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s4GRDB9ViewModel4readyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                CreateArg("block", closureSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDatabase);
        GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var swift = swiftOutput.ToString();
        // Both returning and void variants should have @MainActor before @_silgen_name
        Assert.Contains("@MainActor", swift);
        Assert.Contains("@_silgen_name", swift);
        // Count: exactly 2 @MainActor annotations (returning + void variants)
        var mainActorCount = System.Text.RegularExpressions.Regex.Matches(swift, "@MainActor").Count;
        Assert.Equal(2, mainActorCount);
    }

    [Fact]
    public void TryEmit_NonActorParent_DoesNotEmitMainActorAnnotation()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0"))
        {
            Throws = true
        };

        var method = new MethodDecl
        {
            Name = "read",
            MangledName = "$s4GRDB8Database4readyyF_v3",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                CreateArg("block", closureSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "T", new(), new())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDatabase);
        GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var swift = swiftOutput.ToString();
        Assert.DoesNotContain("@MainActor", swift);
        Assert.Contains("@_silgen_name", swift);
    }

    #endregion

    #region Helpers

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter) CreateWriters()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        return (csWriter, swiftWriter);
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
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
    }

    private static ClassDecl CreateClassDecl(string name)
    {
        var moduleDecl = CreateModuleDecl();
        return new ClassDecl
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
    }

    private static MethodDecl CreateMethodDecl(string name, bool isConstructor = false)
    {
        var moduleDecl = CreateModuleDecl();
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = isConstructor,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateClassDecl("TestType"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };
    }

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Database"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Database"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Database"),
                MetadataAccessor = "$s10TestModule8DatabaseCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static MethodEnvironment CreateMethodEnvironment(MethodDecl method)
    {
        return new MethodEnvironment(method, CreateTypeDatabase());
    }

    #endregion
}
