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

    #region P1-22 (C1): synthetic-name guard wiring

    // The GenericClosureBridge @_silgen_name wrapper hardcodes synthetic Swift identifiers in the
    // same scope as the user's non-closure params: the `cdecl` func-ptr rebind local, the self
    // pointer param + its reconstruction local, the result-buffer param, and the thrown-error
    // locals. A user non-closure param spelled the same identifier used to produce an "invalid
    // redeclaration" emitted at swiftc time (silently stripped at exit 0). The emitter now seeds a
    // SyntheticNameScope with the user param names (and the closure's FuncPtr/Context params) and
    // reserves each synthetic through it, renaming a collision to its `__`-prefixed form. These
    // assert the wiring at the emitter layer — the layer where the guard's behavior is observable
    // independent of the runtime path (the runtime round-trip is separately blocked by a pre-existing
    // GenericClosureBridge self-register ABI defect; see GenericClosureBridgeTests + REMEDIATION-PLAN §6).

    [Fact]
    public void TryEmit_UserParamNamedCdecl_RenamesFuncPtrSynthetic()
    {
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0")) { Throws = true };
        var closureArg = CreateArg("block", closureSpec, moduleDecl);
        // User non-closure class param spelled `cdecl` — collides with the synthetic func-ptr local.
        var cdeclArg = CreateArg("cdecl", new NamedTypeSpec("TestModule.Database"), moduleDecl);

        var method = new MethodDecl
        {
            Name = "readWithCdecl",
            MangledName = "$s4GRDB8Database13readWithCdeclyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                closureArg,
                cdeclArg
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
        GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, new ModuleEmissionContext());

        var swift = swiftOutput.ToString();
        // The synthetic func-ptr rebind escaped to `__cdecl`; the user param `cdecl` survives as-is.
        Assert.Contains("let __cdecl = unsafeBitCast", swift);
        Assert.Contains("__cdecl(", swift); // invoked under the renamed identifier
        // No bare-`cdecl` redeclaration (the "invalid redeclaration" the guard exists to prevent).
        Assert.DoesNotContain("let cdecl = unsafeBitCast", swift);
    }

    [Fact]
    public void TryEmit_UserParamNamedUnderscoreSelf_RenamesSelfPointerTransitively()
    {
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();
        var parentDecl = CreateClassDecl("Database");

        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Database"), new NamedTypeSpec("τ_0_0")) { Throws = true };
        var closureArg = CreateArg("block", closureSpec, moduleDecl);
        // User non-closure class param spelled `_self` — collides with the synthetic self-pointer
        // param, forcing a transitive rename (`_self`→`___self`; the `__self` reconstruction local
        // is then free).
        var selfArg = CreateArg("_self", new NamedTypeSpec("TestModule.Database"), moduleDecl);

        var method = new MethodDecl
        {
            Name = "readWithSelf",
            MangledName = "$s4GRDB8Database12readWithSelfyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("τ_0_0"), moduleDecl),
                closureArg,
                selfArg
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
        GenericClosureBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parentDecl, new ModuleEmissionContext());

        var swift = swiftOutput.ToString();
        // The self-pointer param escaped to `___self`; the reconstruction local reads from it.
        Assert.Contains("___self: UnsafeMutableRawPointer", swift);
        Assert.Contains("unsafeBitCast(OpaquePointer(___self)", swift);
        // The user param `_self` survives as a distinct, label-less wrapper parameter (`_ _self:`),
        // so the synthetic (`___self`) and the user identifier never collide into an "invalid
        // redeclaration". `_ _self:` is not a substring of `_ ___self:`, so this uniquely matches
        // the user param regardless of how its type renders in this fixture.
        Assert.Contains("_ _self:", swift);
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
