// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MethodWrapperEmitter: per-method @_cdecl wrappers that route
/// instance/static method P/Invokes through C calling convention to avoid CallConvSwift crashes.
/// </summary>
public class MethodWrapperEmitterTests
{
    #region ShouldEmitWrapper Guard Tests

    [Fact]
    public void ShouldEmitWrapper_Constructor_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("init", parentDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_Accessor_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("getter:name", parentDecl, moduleDecl);
        method.IsAccessor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_NoAsyncLibraryName_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        // AsyncLibraryName is null — not in xcframework mode

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_ConcreteMethod_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_ConcreteMethod_UnresolvableConformance_ReturnsTrue()
    {
        // Codex P1 regression: a concrete-signature instance method on a generic
        // class with a Self-requirement constraint must NOT be rejected by the
        // wrapper-helper gates. Path 1 (concrete-signature instance dispatch) goes
        // through SelfReconstructionEmitter.EmitProtocolCast and never calls
        // EmitMetadataAccessorHelperIfNeeded — there's nothing for the gate to protect.
        // The previous (over-broad) gate placement at the top of CanEmitGenericDispatch
        // would have rejected this incorrectly.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "GenericBox",
            ("TestModule.AnyInterpolatable", TypeRecordFlags.HasSelfRequirement, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T",
                new List<GenericParameterConformance>
                {
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.AnyInterpolatable"),
                        ConformanceKind.Protocol)
                },
                new List<GenericParameterConformance>())
        };
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_ConcreteMethod_ExceedsRegisterThreshold_ReturnsTrue()
    {
        // Codex P1 regression: a concrete-signature instance method on a generic
        // class with enough conformances to trip the register threshold (1 metadata
        // + 3 PWTs > 3) must NOT be rejected. The instance protocol-cast path doesn't
        // touch the dlsym'd Ma symbol, so buffer-mode mismatch can't fire here.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "GenericBox",
            ("TestModule.Alpha", TypeRecordFlags.None, TypeRecordKind.Protocol),
            ("TestModule.Beta",  TypeRecordFlags.None, TypeRecordKind.Protocol),
            ("TestModule.Gamma", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T",
                new List<GenericParameterConformance>
                {
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Alpha"),
                        ConformanceKind.Protocol),
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Beta"),
                        ConformanceKind.Protocol),
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Gamma"),
                        ConformanceKind.Protocol),
                },
                new List<GenericParameterConformance>())
        };
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_TTypedReturn_UnresolvableConformance_ReturnsFalse()
    {
        // Method return references τ_0_0 → routes through EmitGenericStaticDispatchMethod →
        // calls EmitMetadataAccessorHelperIfNeeded. The Self-requirement protocol makes the
        // wrapper helper undercount PWTs, so the gate must reject.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "GenericBox",
            ("TestModule.AnyInterpolatable", TypeRecordFlags.HasSelfRequirement, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T",
                new List<GenericParameterConformance>
                {
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.AnyInterpolatable"),
                        ConformanceKind.Protocol)
                },
                new List<GenericParameterConformance>())
        };
        var method = CreateMethod("getValue", parentDecl, moduleDecl);
        method.CSSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
                Name = "_result",
                PrivateName = "_result",
                IsInOut = false,
                IsGeneric = true,
                ParentDecl = null,
                ModuleDecl = null
            }
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericStructParent_ConcreteSignature_ReturnsFalse()
    {
        // Generic struct parent with concrete-only method signature — blocked because
        // the method may come from a constrained extension. Only T-referencing methods
        // are supported for generic struct static dispatch.
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_MethodReferencingT_ReturnsTrue()
    {
        // Method return type references parent's generic param — now supported via static dispatch
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("getValue", parentDecl, moduleDecl);
        method.CSSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
                Name = "_result",
                PrivateName = "_result",
                IsInOut = false,
                IsGeneric = true,
                ParentDecl = null,
                ModuleDecl = null
            }
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void NeedsGenericStaticDispatch_GenericStructMethod_ReturnsTrue()
    {
        // Generic struct instance methods always need static dispatch
        var (moduleDecl, typeDb) = CreateTestEnvironment("Wrapper");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Wrapper", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.NeedsGenericStaticDispatch(env, parentDecl));
    }

    [Fact]
    public void NeedsGenericStaticDispatch_GenericClassConcreteMethod_ReturnsFalse()
    {
        // Generic class with concrete-signature method uses existing instance dispatch
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.NeedsGenericStaticDispatch(env, parentDecl));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_AssociatedTypeParam_ReturnsFalse()
    {
        // Associated type reference τ_0_0.Element references parent's generic param → can't erase
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("getValue", parentDecl, moduleDecl);
        method.CSSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = "_result",
                PrivateName = "_result",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            },
            new ArgumentDecl
            {
                SwiftTypeSpec = new AssociatedTypeReferenceSpec("τ_0_0", "Element"),
                Name = "element",
                PrivateName = "element",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            }
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_StaticMethod_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("create", parentDecl, moduleDecl);
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericMethod_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_AsyncMethod_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.IsAsync = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_AlreadyUsesWrapperLibrary_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.UsesWrapperLibrary = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_HasCdeclPropertyWrapper_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.UsesCdeclPropertyWrapper = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_CdeclCompatibleClosureParameter_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodWithParam("doWork", closureType, "callback", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_FrozenStructClosureParameter_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        // String (frozen struct) is now Cdecl-compatible via heap allocation in closure adapter
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.String") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = CreateMethodWithParam("doWork", closureType, "callback", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ProtocolExistentialParam_ReturnsTrue()
    {
        // Existential params are now supported in @_cdecl wrappers via UnsafeRawPointer + .load(as:)
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Cacheable", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Cacheable") });
        var method = CreateMethodWithParam("doWork", protocolSpec, "cache", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ProtocolExistentialReturn_ReturnsTrue()
    {
        // Existential returns are now supported in @_cdecl wrappers via indirect result pointer
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Cacheable", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Cacheable") });
        var method = CreateMethodWithReturn("doWork", protocolSpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ResultParam_NowSupported()
    {
        // Result<T,E> now supported — passes through via UnsafeRawPointer transport
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var resultSpec = new NamedTypeSpec("Swift.Result");
        resultSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        resultSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));
        var method = CreateMethodWithParam("doWork", resultSpec, "result", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ResultReturn_NowSupported()
    {
        // Result<T,E> return now supported via IndirectResult
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var resultSpec = new NamedTypeSpec("Swift.Result");
        resultSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        resultSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));
        var method = CreateMethodWithReturn("doWork", resultSpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OpaqueReturn_ReturnsTrue()
    {
        // Opaque returns (some Protocol) are now supported — @_cdecl wrapper boxes into existential.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var opaqueReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Equatable") })
        {
            IsOpaque = true
        };
        var method = CreateMethodWithReturn("doWork", opaqueReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ActorType_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Counter");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Counter", moduleDecl);
        parentDecl.IsActor = true;
        var method = CreateMethod("increment", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_SimpleInstanceMethod_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_StaticMethod_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ThrowingMethod_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.Throws = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_MutatingMethod_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyType", moduleDecl);
        var method = CreateMethod("mutate", parentDecl, moduleDecl);
        method.IsMutating = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_FreeFunction_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Dummy");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var method = CreateFreeFunction("globalHelper", moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_FreeFunction_NoAsyncLibrary_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Dummy");
        // AsyncLibraryName is null — not in xcframework mode

        var method = CreateFreeFunction("globalHelper", moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_FreeFunction_GenericMethod_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Dummy");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var method = CreateFreeFunction("globalHelper", moduleDecl);
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_PerMemberMainActor_ReturnsTrue()
    {
        // @MainActor on individual methods is now allowed — synchronous gate lift
        var (moduleDecl, typeDb) = CreateTestEnvironment("Session");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Session", moduleDecl);
        var method = CreateMethod("process", parentDecl, moduleDecl);
        method.IsActorIsolated = true;
        method.IsMainActorIsolated = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_PerMemberCustomActor_ReturnsFalse()
    {
        // Custom global actor (e.g., @ProcessingActor) on individual methods is still blocked
        var (moduleDecl, typeDb) = CreateTestEnvironment("Session");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Session", moduleDecl);
        var method = CreateMethod("process", parentDecl, moduleDecl);
        method.IsActorIsolated = true;
        // IsMainActorIsolated stays false — this is a custom actor
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_MainActorParent_ReturnsTrue()
    {
        // @MainActor parent types are now allowed — synchronous gate lift
        var (moduleDecl, typeDb) = CreateTestEnvironment("ViewModel");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("ViewModel", moduleDecl);
        parentDecl.IsMainActorIsolated = true;
        var method = CreateMethod("refresh", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_SpiProtectedMethod_ReturnsFalse()
    {
        // Regression test (Issue K): @_spi protected methods — wrapper can't access
        // them without @_spi import
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("errorCode", parentDecl, moduleDecl);
        method.IsSpiProtected = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_MetatypeParameter_ReturnsFalse()
    {
        // S2: Methods with Any.Type parameters generate bare "Type" in Swift which
        // doesn't exist as a type name, causing compilation errors.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("doWork", new NamedTypeSpec("Any.Type"), "metaType", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_QualifiedMetatypeParameter_ReturnsFalse()
    {
        // Module.SomeType.Type pattern
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("doWork", new NamedTypeSpec("TestModule.Config.Type"), "configType", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_MetatypeReturn_ReturnsFalse()
    {
        // S2: Metatype return types also not C-representable
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getType", new NamedTypeSpec("Any.Type"), parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_NonMetatypeParameter_ReturnsTrue()
    {
        // Normal types should still pass
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("doWork", new NamedTypeSpec("Swift.Int"), "count", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void IsMetatypeType_BareType_ReturnsTrue()
    {
        Assert.True(MethodWrapperEmitter.IsMetatypeType(new NamedTypeSpec("Type")));
    }

    [Fact]
    public void IsMetatypeType_AnyType_ReturnsTrue()
    {
        Assert.True(MethodWrapperEmitter.IsMetatypeType(new NamedTypeSpec("Any.Type")));
    }

    [Fact]
    public void IsMetatypeType_QualifiedType_ReturnsTrue()
    {
        Assert.True(MethodWrapperEmitter.IsMetatypeType(new NamedTypeSpec("TestModule.Foo.Type")));
    }

    [Fact]
    public void IsMetatypeType_NormalType_ReturnsFalse()
    {
        Assert.False(MethodWrapperEmitter.IsMetatypeType(new NamedTypeSpec("Swift.Int")));
    }

    [Fact]
    public void ShouldEmitWrapper_InoutStringParam_ReturnsFalse()
    {
        // Inout String blocked by Guard 5d: C# decomposes String to 2 nint words but
        // MapInout produces a single UnsafeMutableRawPointer, creating ABI mismatch.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var method = new MethodDecl
        {
            Name = "mutate",
            MangledName = "$s10TestModule_mutate",
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
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    Name = "value",
                    PrivateName = "value",
                    IsInOut = true,
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
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_InoutPrimitiveParam_ReturnsTrue()
    {
        // Primitive inout uses UnsafeMutableRawPointer + var binding + &ref + write-back.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var method = new MethodDecl
        {
            Name = "increment",
            MangledName = "$s10TestModule_increment",
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
                    Name = "count",
                    PrivateName = "count",
                    IsInOut = true,
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
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_VariadicParameter_ReturnsFalse()
    {
        // Swift variadic params (T...) appear as Array<T> in ABI JSON. The @_cdecl wrapper
        // would pass [T] where T... is expected, causing compilation error:
        // "cannot pass array of type '[String]' as variadic arguments of type 'String'"
        // E.g., SwiftyBeaver.FunctionFilterFactory.startsWith(_ prefixes: String..., ...)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var method = new MethodDecl
        {
            Name = "startsWith",
            MangledName = "$s10TestModule_startsWith",
            MethodType = MethodType.Static,
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
                    // ABI JSON represents String... as Array<String>
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Array") { GenericParameters = { new NamedTypeSpec("Swift.String") } },
                    Name = "prefixes",
                    PrivateName = "prefixes",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"),
                    Name = "caseSensitive",
                    PrivateName = "caseSensitive",
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
            Visibility = Visibility.Public,
            HasVariadicParameter = true // Set by demangler during parsing
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_NonVariadicArrayParameter_ReturnsTrue()
    {
        // Regular Array<T> parameter (not variadic) should NOT be blocked.
        // Only variadic params (T...) that demangler marks with IsVariadic should be blocked.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule_process",
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
                    // Regular Array<String> parameter — not variadic
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Array") { GenericParameters = { new NamedTypeSpec("Swift.String") } },
                    Name = "items",
                    PrivateName = "items",
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
            Visibility = Visibility.Public,
            HasVariadicParameter = false // Regular array, NOT variadic
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void HasCdeclCompatibleFunctionShape_InoutParam_ReturnsTrue()
    {
        // Inout params have a cdecl-compatible shape (UnsafeMutableRawPointer + write-back).
        // Async is blocked separately by CanEmitMember, not by shape check.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var method = new MethodDecl
        {
            Name = "mutateSync",
            MangledName = "$s10TestModule_mutateSync",
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
                    Name = "value",
                    PrivateName = "value",
                    IsInOut = true,
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
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));
    }

    [Fact]
    public void HasCdeclCompatibleFunctionShape_InoutOnGenericParent_ReturnsFalse()
    {
        // Guard 5c: inout params on generic parent types can't use the protocol dispatch
        // pattern for write-back, so they must fall back to CallConvSwift.
        var (moduleDecl, typeDb) = CreateTestEnvironment("Container");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Container", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        var method = new MethodDecl
        {
            Name = "mutate",
            MangledName = "$s10TestModule_mutate",
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
                    Name = "value",
                    PrivateName = "value",
                    IsInOut = true,
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
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));
    }

    [Fact]
    public void HasCdeclCompatibleFunctionShape_InoutString_ReturnsFalse()
    {
        // Guard 5d: inout String creates ABI mismatch — C# decomposes String to 2 nint words
        // but MapInout produces a single UnsafeMutableRawPointer.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var method = new MethodDecl
        {
            Name = "updateName",
            MangledName = "$s10TestModule_updateName",
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
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    Name = "name",
                    PrivateName = "name",
                    IsInOut = true,
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
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));
    }

    #endregion

    #region Symbol Name Tests

    [Fact]
    public void GetMethodSymbolName_Format()
    {
        var symbol = MethodWrapperEmitter.GetMethodSymbolName("Nuke", "ImagePipeline", "loadImage", "$s4Nuke13ImagePipelineC9loadImageyyF");
        Assert.StartsWith("SBW_Nuke_ImagePipeline_loadImage_", symbol);
    }

    [Fact]
    public void GetMethodSymbolName_NestedTypeDotReplaced()
    {
        var symbol = MethodWrapperEmitter.GetMethodSymbolName("Nuke", "Outer.Inner", "doWork", "$s_mangled");
        Assert.Contains("Outer_Inner", symbol);
        Assert.DoesNotContain("Outer.Inner", symbol);
    }

    [Fact]
    public void GetMethodSymbolName_UniqueAcrossOverloads()
    {
        var sym1 = MethodWrapperEmitter.GetMethodSymbolName("Mod", "Type", "method", "$s_mangled1");
        var sym2 = MethodWrapperEmitter.GetMethodSymbolName("Mod", "Type", "method", "$s_mangled2");
        Assert.NotEqual(sym1, sym2);
    }

    [Fact]
    public void GetMethodSymbolName_FreeFunction_UsesFreeSegment()
    {
        var symbol = MethodWrapperEmitter.GetMethodSymbolName("TestModule", "Free", "globalHelper", "$s_mangled_free");
        Assert.StartsWith("SBW_TestModule_Free_globalHelper_", symbol);
    }

    [Fact]
    public void ShouldEmitWrapper_TupleReturn_ReturnsTrue()
    {
        // Tuple returns are now routed through IndirectResult (resultPtr buffer)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var tupleReturn = new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Int") });
        var method = CreateMethodWithReturn("getPair", tupleReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_DynamicSelfReturn_ClassParent_ReturnsTrue()
    {
        // DynamicSelf (Self) on class parents resolves to class type — returned as class pointer
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var selfReturn = new NamedTypeSpec("Self");
        var method = CreateMethodWithReturn("configure", selfReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_DynamicSelfReturn_StructParent_ReturnsFalse()
    {
        // DynamicSelf (Self) on struct parents blocked — Unmanaged requires class type
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyStruct");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        var selfReturn = new NamedTypeSpec("Self");
        var method = CreateMethodWithReturn("create", selfReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    #endregion

    #region Emission Tests

    [Fact]
    public void EmitSwiftMethodWrapper_TupleReturn_UsesResultPtr()
    {
        // Tuple returns use resultPtr.initializeMemory(as: (T1, T2).self)
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "getPair",
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Int") }),
            isClass: true);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Must have resultPtr parameter
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        // Must use initializeMemory for tuple
        Assert.Contains("initializeMemory(as: (Swift.Int, Swift.Int).self", output);
        // Function signature must not have a return clause (indirect result — Void return)
        Assert.Matches(@"public func _sbw_.*\) \{", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_DynamicSelfReturn_ReturnsClassPointer()
    {
        // DynamicSelf returns use Unmanaged.passRetained().toOpaque()
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "configure",
            new NamedTypeSpec("Self"),
            isClass: true);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Must have UnsafeMutableRawPointer return type
        Assert.Contains("-> UnsafeMutableRawPointer", output);
        // Must use Unmanaged.passRetained for class pointer return
        Assert.Contains("Unmanaged.passRetained(", output);
        Assert.Contains(".toOpaque()", output);
        // Must NOT have resultPtr parameter
        Assert.DoesNotContain("resultPtr", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_ClassInstance_UnmanagedSelf()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "doWork", TupleTypeSpec.Empty, isClass: true);

        method.MangledName = "SBW_TestModule_MyType_doWork_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"SBW_TestModule_MyType_doWork_abc12345\")", output);
        Assert.Contains("_ self_: UnsafeMutableRawPointer", output);
        Assert.Contains("Unmanaged<TestModule.MyType>.fromOpaque(self_).takeUnretainedValue()", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_StructNonMutating_LoadSelf()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "getValue", new NamedTypeSpec("Swift.Int"), isClass: false);

        method.MangledName = "SBW_TestModule_MyType_getValue_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ self_: UnsafeRawPointer", output);
        Assert.Contains("self_.assumingMemoryBound(to: TestModule.MyType.self).pointee", output);
        Assert.Contains("-> Int", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_StructMutating_PointerAccess()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "mutate", TupleTypeSpec.Empty, isClass: false);

        method.IsMutating = true;
        method.MangledName = "SBW_TestModule_MyType_mutate_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ self_: UnsafeMutableRawPointer", output);
        Assert.Contains("self_.assumingMemoryBound(to: TestModule.MyType.self).pointee", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_StaticMethod_NoSelfParam()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "create", new NamedTypeSpec("Swift.Int"), isClass: true);
        method.MethodType = MethodType.Static;

        method.MangledName = "SBW_TestModule_MyType_create_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.DoesNotContain("self_", output);
        Assert.Contains("TestModule.MyType.create", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_ThrowingMethod_ErrorOut()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "doWork", TupleTypeSpec.Empty, isClass: true);
        method.Throws = true;

        method.MangledName = "SBW_TestModule_MyType_doWork_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>", output);
        Assert.Contains("do {", output);
        Assert.Contains("try obj.doWork()", output);
        Assert.Contains("errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_StringReturn_Utf8SlicePattern()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "getName", new NamedTypeSpec("Swift.String"), isClass: true);

        method.MangledName = "SBW_TestModule_MyType_getName_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("SBW_Utf8Slice", output);
        Assert.Contains("utf8", output);
        Assert.Contains("UnsafeMutablePointer<UInt8>.allocate", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_ClassReturn_PassRetained()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.OtherClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getOther", new NamedTypeSpec("TestModule.OtherClass"), parentDecl, moduleDecl);

        method.MangledName = "SBW_TestModule_MyType_getOther_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> UnsafeMutableRawPointer", output);
        Assert.Contains("Unmanaged.passRetained(", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_BoolReturn_Int8Conversion()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "isValid", new NamedTypeSpec("Swift.Bool"), isClass: true);

        method.MangledName = "SBW_TestModule_MyType_isValid_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> Int8", output);
        Assert.Contains("? 1 : 0", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_SimpleEnumReturn_RawValue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Status", TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, TypeRecordKind.Enum, "Swift.Int"));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getStatus", new NamedTypeSpec("TestModule.Status"), parentDecl, moduleDecl);

        method.MangledName = "SBW_TestModule_MyType_getStatus_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> Int", output);
        Assert.Contains(".rawValue)", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_IndirectResult_WritesToResultPtr()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.BigStruct", TypeRecordFlags.None, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getBigStruct", new NamedTypeSpec("TestModule.BigStruct"), parentDecl, moduleDecl);

        method.MangledName = "SBW_TestModule_MyType_getBigStruct_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("initializeMemory", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_MainActor_PropagatedToCdecl()
    {
        // @MainActor IS propagated to @_cdecl wrappers — Swift 6 requires the caller
        // to share isolation context. @MainActor on @_cdecl is compile-time only (no ABI change).
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "doWork", TupleTypeSpec.Empty, isClass: true, isMainActorIsolated: true);

        method.MangledName = "SBW_TestModule_MyType_doWork_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@MainActor", output);
        Assert.Contains("@_cdecl", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_Dedup_PreventsDuplicate()
    {
        var (swiftWriter, sw, method, env, ctx) = CreateMethodTestSetup(
            "doWork", TupleTypeSpec.Empty, isClass: true);

        method.MangledName = "SBW_TestModule_MyType_doWork_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        // First emission
        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);
        var firstOutput = sw.ToString();

        // Second emission with same symbol — should be no-op
        var sw2 = new StringWriter();
        var swiftWriter2 = new SwiftWriter(sw2);
        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter2, env, ctx);
        var secondOutput = sw2.ToString();

        Assert.Contains("@_cdecl", firstOutput);
        Assert.DoesNotContain("@_cdecl", secondOutput);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_WithIntParam()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("setCount", new NamedTypeSpec("Swift.Int"), "count", parentDecl, moduleDecl);

        method.MangledName = "SBW_TestModule_MyType_setCount_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ count: Int", output);
        Assert.Contains("count: count", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_ThrowingWithReturn_HasSentinel()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.OtherClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getOther", new NamedTypeSpec("TestModule.OtherClass"), parentDecl, moduleDecl);
        method.Throws = true;

        method.MangledName = "SBW_TestModule_MyType_getOther_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("errorOut", output);
        Assert.Contains("do {", output);
        Assert.Contains("} catch {", output);
        // Should have sentinel return in catch block for class pointer return
        Assert.Contains("UnsafeMutableRawPointer(bitPattern: 1)!", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_FreeFunction_NoSelfParam()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Dummy");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var method = CreateFreeFunction("globalHelper", moduleDecl);
        method.MangledName = "SBW_TestModule_Free_globalHelper_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"SBW_TestModule_Free_globalHelper_abc12345\")", output);
        Assert.DoesNotContain("self_", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_FreeFunction_CallExpression_NoTypePrefix()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Dummy");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var method = CreateFreeFunctionWithReturn("computeValue", new NamedTypeSpec("Swift.Int"), moduleDecl);
        method.MangledName = "SBW_TestModule_Free_computeValue_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Free function call should NOT have a type prefix like "TestModule.MyType.computeValue"
        Assert.DoesNotContain("TestModule.", output.Split("@_cdecl")[1]); // After @_cdecl line, no module prefix in call
        Assert.Contains("computeValue()", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_GenericClassParent_EmitsProtocolErasure()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("reset", parentDecl, moduleDecl);
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;
        method.UsesFreeFunctionWrapper = true;
        method.MangledName = "SBW_TestModule_GenericBox_reset";
        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Protocol-based type erasure
        Assert.Contains("private protocol _SBW_P_", output);
        Assert.Contains("func reset()", output);
        Assert.Contains("extension TestModule.GenericBox:", output);
        // Metadata parameter
        Assert.Contains("_ _metadata0: UnsafeRawPointer", output);
        // Self reconstruction via AnyObject
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any _SBW_P_", output);
        // Should NOT use concrete type
        Assert.DoesNotContain("Unmanaged<TestModule.GenericBox>", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_GenericClassParent_TReturnType_UsesMetadataAccessorHelper()
    {
        // Method with T return type on generic class — triggers generic static dispatch path.
        // Verifies _sbw_meta_ helper is emitted and used for metatype reconstruction.
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        // Method returning T (generic param) — needs static dispatch
        var method = CreateMethodWithReturn("getValue", new NamedTypeSpec("τ_0_0"), parentDecl, moduleDecl);
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;
        method.UsesFreeFunctionWrapper = true;
        method.MangledName = "SBW_TestModule_GenericBox_getValue";
        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Metadata accessor helper emitted at module scope
        Assert.Contains("_sbw_meta_", output);
        Assert.Contains("dlsym(dlopen(nil, RTLD_LAZY)", output);

        // Metatype dispatch uses helper result, not raw _metadata0
        Assert.Contains("unsafeBitCast(parentMeta, to: Any.Type.self)", output);
        Assert.Contains("as! any _SBW_GSM_", output);

        // Should NOT have the old pattern of directly casting _metadata0
        Assert.DoesNotContain("unsafeBitCast(_metadata0, to: Any.Type.self)", output);
    }

    #endregion

    #region UsesCdeclWrapper Computed Property

    [Fact]
    public void UsesCdeclWrapper_MethodWrapper_ReturnsTrue()
    {
        var method = new MethodDecl
        {
            Name = "test",
            MangledName = "test",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            UsesCdeclMethodWrapper = true
        };

        Assert.True(method.UsesCdeclWrapper);
    }

    [Fact]
    public void UsesCdeclWrapper_NoWrapper_ReturnsFalse()
    {
        var method = new MethodDecl
        {
            Name = "test",
            MangledName = "test",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        Assert.False(method.UsesCdeclWrapper);
    }

    #endregion

    #region BuildProtocolMethodDeclaration Tests

    [Fact]
    public void BuildProtocolMethodDeclaration_UnlabeledParam_EmitsUnderscore()
    {
        // Swift `append(_ element: Int)` — external label is "_", internal name is "element".
        // The parser converts "_" to "arg0" for Name, but if Name is literally "_" (e.g., from
        // a non-ABI-JSON path), it must not produce an empty external label.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var method = new MethodDecl
        {
            Name = "append",
            MangledName = "$s10TestModule_append",
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
                    Name = "_",
                    PrivateName = "element",
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
            Visibility = Visibility.Public
        };
        var env = new MethodEnvironment(method, typeDb);

        var result = MethodWrapperEmitter.BuildProtocolMethodDeclaration(method, env);

        // Must produce valid Swift: `func append(_ element: Int)`, NOT `func append( element: Int)`
        Assert.Contains("_ element: Int", result);
        Assert.DoesNotContain("( ", result); // no empty external label
    }

    #endregion

    #region Helper Methods

    private static (SwiftWriter swiftWriter, StringWriter sw, MethodDecl method, MethodEnvironment env, ModuleEmissionContext ctx) CreateMethodTestSetup(
        string methodName, TypeSpec returnType, bool isClass, bool isMainActorIsolated = false)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        TypeDecl parentDecl = isClass
            ? CreateClassDecl("MyType", moduleDecl)
            : CreateStructDecl("MyType", moduleDecl);
        if (isMainActorIsolated)
            parentDecl.IsMainActorIsolated = true;

        var method = CreateMethodWithReturn(methodName, returnType, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        return (swiftWriter, sw, method, env, ctx);
    }

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
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodWithReturn(string name, TypeSpec returnType, TypeDecl parentDecl, ModuleDecl moduleDecl)
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
                    SwiftTypeSpec = returnType,
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
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodWithParam(string name, TypeSpec paramType, string paramName, TypeDecl parentDecl, ModuleDecl moduleDecl)
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
                    SwiftTypeSpec = paramType,
                    Name = paramName,
                    PrivateName = paramName,
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
            Visibility = Visibility.Public
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
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

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithExtraTypes(
        string typeName,
        params (string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind, string? rawValueTypeName)[] extraTypes)
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

        foreach (var (qualifiedName, flags, kind, rawValue) in extraTypes)
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
            testModule.RegisterType(
                swiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", swiftTypeName.Name),
                    SwiftTypeName = swiftTypeName,
                    MetadataAccessor = $"$s{swiftTypeName.Name}Ma",
                    Flags = flags,
                    Kind = kind,
                    RawValueTypeName = rawValue
                });
        }

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

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithExtraTypes(
        string typeName,
        params (string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind)[] extraTypes)
    {
        return CreateTestEnvironmentWithExtraTypes(typeName,
            extraTypes.Select(t => (t.qualifiedName, t.flags, t.kind, (string?)null)).ToArray());
    }

    private static MethodDecl CreateFreeFunction(string name, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Static,
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
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateFreeFunctionWithReturn(string name, TypeSpec returnType, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = returnType,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    #endregion

    #region Optional<reference-type> Guard Tests

    [Fact]
    public void ShouldEmitWrapper_OptionalClassParam_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.MyClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyClass"));
        var method = CreateMethodWithParam("doWork", optionalSpec, "obj", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalObjCBridgedParam_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.UIImage", TypeRecordFlags.ObjCBridged, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.UIImage"));
        var method = CreateMethodWithParam("doWork", optionalSpec, "image", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalUnresolvedAppleObjCParam_ReturnsTrue()
    {
        // UIKit.UITableView: no TypeRecord, classified via fallback heuristic
        // (IsOptionalFallbackModule + HasObjCClassPrefix)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("UIKit.UITableView"));
        var method = CreateMethodWithParam("doWork", optionalSpec, "table", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalValueTypeParam_ReturnsTrue()
    {
        // Optional<value-type> params are now handled via @_cdecl UnsafeRawPointer + .load(as:)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithParam("doWork", optionalSpec, "value", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalStringParam_ReturnsTrue()
    {
        // Optional<String> params are now handled via @_cdecl UnsafeRawPointer + .load(as:)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var method = CreateMethodWithParam("doWork", optionalSpec, "name", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ArrayParam_ReturnsTrue()
    {
        // Array params now handled via @_cdecl UnsafeRawPointer + .load(as:) (Session 9A)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithParam("doWork", arraySpec, "items", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_MixedOptionalRefAndValue_ReturnsTrue()
    {
        // Method with both Optional<Class> and Optional<Int> → now allowed
        // Optional<Class> uses nullable pointer ABI, Optional<Int> uses IndirectResult
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.MyClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var optionalClassSpec = new NamedTypeSpec("Swift.Optional");
        optionalClassSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyClass"));

        var optionalIntSpec = new NamedTypeSpec("Swift.Optional");
        optionalIntSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule_doWork",
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
                    SwiftTypeSpec = optionalClassSpec,
                    Name = "obj",
                    PrivateName = "obj",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = optionalIntSpec,
                    Name = "count",
                    PrivateName = "count",
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
            Visibility = Visibility.Public
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalDoubleReturn_ReturnsTrue()
    {
        // Optional<Double> return now handled via @_cdecl IndirectResult (resultPtr)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Double"));
        var method = CreateMethodWithReturn("getRate", optionalSpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalBoolReturn_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Bool"));
        var method = CreateMethodWithReturn("isEnabled", optionalSpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_DictionaryParam_ReturnsTrue()
    {
        // Dictionary params now handled via @_cdecl UnsafeRawPointer + .load(as:) (Session 9A)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var dictSpec = new NamedTypeSpec("Swift.Dictionary");
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithParam("doWork", dictSpec, "dict", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ClosureReturn_ReturnsTrue()
    {
        // Closure returns now handled via @_cdecl IndirectResult (resultPtr buffer)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureReturn = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));
        closureReturn.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = CreateMethodWithReturn("getHandler", closureReturn, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalExistentialReturn_ReturnsFalse()
    {
        // Optional<protocol existential> still blocked — property/method marshalling
        // doesn't convert ExistentialContainer1 to protocol proxy
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolList = new ProtocolListTypeSpec(new List<NamedTypeSpec> { new NamedTypeSpec("Swift.Error") });
        var optionalExistential = new NamedTypeSpec("Swift.Optional");
        optionalExistential.GenericParameters.Add(protocolList);
        var method = CreateMethodWithReturn("getError", optionalExistential, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    #endregion

    #region Session 9B: Protocol Existential Param Guard Tests

    [Fact]
    public void ShouldEmitWrapper_BareExistentialParam_ReturnsTrue()
    {
        // Bare protocol existential params (any Protocol) already handled by
        // GetCdeclParamMapping existential path — no guard blocks them
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var existentialSpec = new ProtocolListTypeSpec(new List<NamedTypeSpec> { new NamedTypeSpec("Swift.Hashable") });
        var method = CreateMethodWithParam("doWork", existentialSpec, "thing", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalExistentialParam_ReturnsFalse()
    {
        // Optional<any Protocol> params still blocked — marshalling doesn't handle
        // ExistentialContainer1 → protocol proxy conversion
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolList = new ProtocolListTypeSpec(new List<NamedTypeSpec> { new NamedTypeSpec("Swift.Hashable") });
        var optionalExistential = new NamedTypeSpec("Swift.Optional");
        optionalExistential.GenericParameters.Add(protocolList);
        var method = CreateMethodWithParam("doWork", optionalExistential, "thing", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_BareExistentialReturn_ReturnsTrue()
    {
        // Bare protocol existential returns handled via @_cdecl IndirectResult
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var existentialSpec = new ProtocolListTypeSpec(new List<NamedTypeSpec> { new NamedTypeSpec("Swift.Hashable") });
        var method = CreateMethodWithReturn("getThing", existentialSpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalAnyParam_ReturnsTrue()
    {
        // Optional<Any> (Any?) — empty protocol list. Used for NSObject.isEqual(_ object: Any?).
        // CdeclParamMapper handles as nullable pointer (UnsafeMutableRawPointer?).
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var emptyProtocolList = new ProtocolListTypeSpec();  // Any = empty protocol list
        var optionalAny = new NamedTypeSpec("Swift.Optional");
        optionalAny.GenericParameters.Add(emptyProtocolList);
        var method = CreateMethodWithParam("isEqual", optionalAny, "_", parentDecl, moduleDecl);
        method.CSSignature[0] = new ArgumentDecl
        {
            SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"),
            Name = "",
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalAnyReturn_ReturnsTrue()
    {
        // Optional<Any> (Any?) return type — allowed via nullable pointer ABI
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var emptyProtocolList = new ProtocolListTypeSpec();
        var optionalAny = new NamedTypeSpec("Swift.Optional");
        optionalAny.GenericParameters.Add(emptyProtocolList);
        var method = CreateMethodWithReturn("getAny", optionalAny, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void CdeclParamMapper_OptionalAny_MapsToNullablePointer()
    {
        // Verify CdeclParamMapper.Map produces UnsafeMutableRawPointer? for Any?
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var emptyProtocolList = new ProtocolListTypeSpec();
        var optionalAny = new NamedTypeSpec("Swift.Optional");
        optionalAny.GenericParameters.Add(emptyProtocolList);

        var arg = new ArgumentDecl
        {
            SwiftTypeSpec = optionalAny,
            Name = "other",
            PrivateName = "other",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("isEqual", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var (cdeclParam, reconstruction, callArg) = CdeclParamMapper.Map(arg, "other", env);

        Assert.Contains("UnsafeMutableRawPointer?", cdeclParam);
        Assert.Contains("Unmanaged<AnyObject>", reconstruction!);
        Assert.Contains("other: otherVal", callArg);
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalSelfReturn_ClassParent_ReturnsTrue()
    {
        // Optional<Self> return on class parents — Self resolves to concrete class,
        // return emitted as nullable class pointer (UnsafeMutableRawPointer?).
        // Used for STPAPIResponseDecodable.decodedObject(fromAPIResponse:) -> Self?.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var selfSpec = new NamedTypeSpec("Self");
        var optionalSelf = new NamedTypeSpec("Swift.Optional");
        optionalSelf.GenericParameters.Add(selfSpec);
        var method = CreateMethodWithReturn("decodedObject", optionalSelf, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalSelfReturn_StructParent_ReturnsFalse()
    {
        // Optional<Self> on struct parents blocked — Unmanaged.passRetained requires class type.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyStruct");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        var selfSpec = new NamedTypeSpec("Self");
        var optionalSelf = new NamedTypeSpec("Swift.Optional");
        optionalSelf.GenericParameters.Add(selfSpec);
        var method = CreateMethodWithReturn("tryCreate", optionalSelf, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void CdeclReturnMapping_OptionalSelf_MapsToOptionalClassPointer()
    {
        // Verify CdeclReturnMapping.Classify produces OptionalClassPointer for Self?.
        var (_, typeDb) = CreateTestEnvironment("MyType");

        var selfSpec = new NamedTypeSpec("Self");
        var optionalSelf = new NamedTypeSpec("Swift.Optional");
        optionalSelf.GenericParameters.Add(selfSpec);

        var (mapping, needsResultPtr) = CdeclReturnMapping.Classify(optionalSelf, typeDb);

        Assert.Equal(CdeclReturnKind.OptionalClassPointer, mapping.Kind);
        Assert.Equal("UnsafeMutableRawPointer?", mapping.CdeclReturnType);
        Assert.False(needsResultPtr);
    }

    #endregion

    #region Session 9A: Collection Container Guard Tests

    [Fact]
    public void ShouldEmitWrapper_SetParam_ReturnsTrue()
    {
        // Set params now handled via @_cdecl UnsafeRawPointer + .load(as:) (Session 9A)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var setSpec = new NamedTypeSpec("Swift.Set");
        setSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var method = CreateMethodWithParam("doWork", setSpec, "items", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ArrayReturn_ReturnsTrue()
    {
        // Array returns now handled via @_cdecl IndirectResult (Session 9A)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var method = CreateMethodWithReturn("getItems", arraySpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_DictionaryReturn_ReturnsTrue()
    {
        // Dictionary returns now handled via @_cdecl IndirectResult (Session 9A)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var dictSpec = new NamedTypeSpec("Swift.Dictionary");
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithReturn("getDict", dictSpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void ShouldEmitWrapper_ResultParam_ReturnsTrue()
    {
        // Result<T,E> now supported — passes through via UnsafeRawPointer transport
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var resultSpec = new NamedTypeSpec("Swift.Result");
        resultSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        resultSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));
        var method = CreateMethodWithParam("doWork", resultSpec, "result", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Fact]
    public void IsSupportedCollectionType_Array_ReturnsTrue()
    {
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        Assert.True(MethodWrapperEmitter.IsSupportedCollectionType(arraySpec));
    }

    [Fact]
    public void IsSupportedCollectionType_Dictionary_ReturnsTrue()
    {
        var dictSpec = new NamedTypeSpec("Swift.Dictionary");
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        Assert.True(MethodWrapperEmitter.IsSupportedCollectionType(dictSpec));
    }

    [Fact]
    public void IsSupportedCollectionType_Set_ReturnsTrue()
    {
        var setSpec = new NamedTypeSpec("Swift.Set");
        setSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        Assert.True(MethodWrapperEmitter.IsSupportedCollectionType(setSpec));
    }

    [Fact]
    public void IsSupportedCollectionType_Result_ReturnsFalse()
    {
        var resultSpec = new NamedTypeSpec("Swift.Result");
        resultSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        resultSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));
        Assert.False(MethodWrapperEmitter.IsSupportedCollectionType(resultSpec));
    }

    [Fact]
    public void IsSupportedCollectionType_Optional_ReturnsFalse()
    {
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        Assert.False(MethodWrapperEmitter.IsSupportedCollectionType(optionalSpec));
    }

    #endregion

    #region IsOptionalWithReferenceInner Helper Tests

    [Fact]
    public void IsOptionalWithReferenceInner_UnresolvedAppleFrameworkType_ReturnsTrue()
    {
        // UIKit.UITableView: no TypeRecord but matching fallback heuristic
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("UIKit.UITableView"));

        Assert.True(CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb));
    }

    [Fact]
    public void IsOptionalWithReferenceInner_UnresolvedAppleValueType_ReturnsFalse()
    {
        // UIKit.UIEdgeInsets: known Apple value type in ValueTypes exclusion set,
        // excluded by IsKnownValueType in both IsObjCModuleType and IsOptionalObjCBridged
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("UIKit.UIEdgeInsets"));

        Assert.False(CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb));
    }

    [Fact]
    public void IsOptionalWithReferenceInner_UnresolvedApplePointerType_ReturnsFalse()
    {
        // Swift.UnsafeMutablePointer: rejected by IsPointerType guard
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.UnsafeMutablePointer"));

        Assert.False(CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb));
    }

    [Fact]
    public void IsOptionalWithReferenceInner_NonOptionalType_ReturnsFalse()
    {
        var typeDb = new TypeDatabase();
        Assert.False(CdeclParamMapper.IsOptionalWithReferenceInner(new NamedTypeSpec("Swift.Int"), typeDb));
    }

    [Fact]
    public void IsOptionalWithReferenceInner_NSStringTypedefStruct_ReturnsFalse()
    {
        // NSString typedef structs (e.g., CALayerContentsGravity) are ObjC-bridged in the type
        // database but are Swift structs wrapping NSString — not class instances.
        // Unmanaged<T>.passRetained() requires a class, so these must NOT be classified as
        // reference types. Regression test for NSString typedef carveout.
        var typeDb = new TypeDatabase();
        var quartzModule = new ModuleTypeDatabase("QuartzCore", "/usr/lib/libQuartzCore.dylib");
        quartzModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayerContentsGravity"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreAnimation", "CALayerContentsGravity"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayerContentsGravity"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(quartzModule);

        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("QuartzCore.CALayerContentsGravity"));

        Assert.False(CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb));
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ObjCBridgedStruct_ReturnsTrue()
    {
        // ObjC-bridged structs use nullable pointer ABI via Unmanaged<AnyObject> bridge.
        // Getter: `as AnyObject` boxes the struct. Setter: `takeUnretainedValue() as! T` unboxes.
        var typeDb = new TypeDatabase();
        var uikitModule = new ModuleTypeDatabase("UIKit", "/usr/lib/libUIKit.dylib");
        uikitModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("UIKit.UIFont.Weight"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIFontWeight"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIFont.Weight"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(uikitModule);

        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("UIKit.UIFont.Weight"));

        Assert.True(CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb));
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ObjCRootedStruct_ReturnsFalse()
    {
        // EC-4: ObjC-rooted structs (e.g., PHPickerResult) should NOT be treated as reference types.
        var typeDb = new TypeDatabase();
        var photosModule = new ModuleTypeDatabase("PhotosUI", "/usr/lib/libPhotosUI.dylib");
        photosModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("PhotosUI.PHPickerResult"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("PhotosUI", "PHPickerResult"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("PhotosUI.PHPickerResult"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.ObjCRooted,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(photosModule);

        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("PhotosUI.PHPickerResult"));

        Assert.False(CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb));
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ObjCBridgedClass_ReturnsTrue()
    {
        // EC-4: ObjC-bridged classes (e.g., UIImage) SHOULD still be treated as reference types.
        var typeDb = new TypeDatabase();
        var uikitModule = new ModuleTypeDatabase("UIKit", "/usr/lib/libUIKit.dylib");
        uikitModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("UIKit.UIImage"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIImage"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIImage"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Class
            });
        typeDb.AddModuleDatabase(uikitModule);

        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("UIKit.UIImage"));

        Assert.True(CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb));
    }

    [Fact]
    public void EmitSwiftMethodWrapper_ProtocolCompositionReturn_ParenthesizesMetatype()
    {
        // EC-8: protocol compositions need parentheses before .self to prevent
        // .self from binding to only the last protocol in the composition.
        // "any P1 & P2.self" is wrong; "(any P1 & P2).self" is correct.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            Array.Empty<(string, TypeRecordFlags, TypeRecordKind)>());
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolComposition = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.Cryptor"),
            new NamedTypeSpec("TestModule.Updatable")
        });
        var method = CreateMethodWithReturn("makeEncryptor", protocolComposition, parentDecl, moduleDecl);

        method.MangledName = "SBW_TestModule_MyType_makeEncryptor_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Must use parenthesized form for protocol composition metatype
        Assert.Contains("(any TestModule.Cryptor & TestModule.Updatable).self", output);
        // Must NOT have the unparenthesized form
        Assert.DoesNotContain("any TestModule.Cryptor & TestModule.Updatable.self", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_SingleProtocolReturn_ParenthesizesMetatype()
    {
        // Single protocol existentials also need parenthesization for correctness.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            Array.Empty<(string, TypeRecordFlags, TypeRecordKind)>());
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolReturn = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.Updatable")
        });
        var method = CreateMethodWithReturn("getUpdatable", protocolReturn, parentDecl, moduleDecl);

        method.MangledName = "SBW_TestModule_MyType_getUpdatable_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Single protocol existentials also use parenthesized form
        Assert.Contains("(any TestModule.Updatable).self", output);
    }

    [Fact]
    public void EmitSwiftMethodWrapper_ConcreteReturn_NoParenthesization()
    {
        // Concrete (non-existential) return types should NOT be parenthesized.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.BigStruct", TypeRecordFlags.None, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getBigStruct", new NamedTypeSpec("TestModule.BigStruct"), parentDecl, moduleDecl);

        method.MangledName = "SBW_TestModule_MyType_getBigStruct_abc12345";
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();
        // Concrete types use direct .self without parentheses
        Assert.Contains("TestModule.BigStruct.self", output);
        Assert.DoesNotContain("(TestModule.BigStruct).self", output);
    }

    /// <summary>
    /// Verifies that @_cdecl method wrappers emit two-Int-word parameters for Foundation.Data,
    /// not bare Foundation.Data which gets ObjC-bridged to NSData* at the ABI level.
    /// Regression test for Nuke DataCache.storeData SIGSEGV on NativeAOT device.
    /// </summary>
    [Fact]
    public void EmitSwiftMethodWrapper_FoundationDataParam_UsesTwoIntWords()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("DataCache");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("DataCache", moduleDecl);

        // Method with Foundation.Data parameter
        var method = new MethodDecl
        {
            Name = "storeData",
            MangledName = "SBW_TestModule_DataCache_storeData_abc12345",
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
                    SwiftTypeSpec = new NamedTypeSpec("Foundation.Data"),
                    Name = "data",
                    PrivateName = "data",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            UsesCdeclMethodWrapper = true,
            UsesWrapperLibrary = true
        };

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();

        // Data parameter must be received as two Int words (matching String pattern),
        // not Foundation.Data (which triggers ObjC bridging to NSData*)
        Assert.Contains("_dW0_data: Int", output);
        Assert.Contains("_dW1_data: Int", output);
        // Must reconstruct via unsafeBitCast to Foundation.Data.self
        Assert.Contains("unsafeBitCast", output);
        Assert.Contains("Foundation.Data.self", output);
        // Must NOT have bare "Foundation.Data" as a @_cdecl parameter type
        Assert.DoesNotContain("_ data: Data,", output);
        Assert.DoesNotContain("_ data: Foundation.Data,", output);
    }

    /// <summary>
    /// Verifies that @_cdecl method wrappers emit two-Int-word parameters for Foundation.Data
    /// on static methods (like LottieAnimation.from(data:)).
    /// Regression test for Lottie LottieAnimation.from(byte[]) SIGSEGV.
    /// </summary>
    [Fact]
    public void EmitSwiftMethodWrapper_StaticFoundationDataParam_UsesTwoIntWords()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Animation");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Animation", moduleDecl);

        // Static method with Foundation.Data parameter
        var method = new MethodDecl
        {
            Name = "from",
            MangledName = "SBW_TestModule_Animation_from_abc12345",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Animation"),
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Foundation.Data"),
                    Name = "data",
                    PrivateName = "data",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            UsesCdeclMethodWrapper = true,
            UsesWrapperLibrary = true
        };

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);

        var output = sw.ToString();

        // Data parameter must be received as two Int words
        Assert.Contains("_dW0_data: Int", output);
        Assert.Contains("_dW1_data: Int", output);
        Assert.Contains("unsafeBitCast", output);
        Assert.Contains("Foundation.Data.self", output);
    }

    #endregion

    #region Generic Static Dispatch — Return Conversion Tests

    [Fact]
    public void GenericStaticDispatch_BoolReturn_EmitsTernaryConversion()
    {
        // A generic struct method returning Bool must emit `result ? 1 : 0`
        // not a bare `return obj.method(...)` which would return Swift Bool
        // instead of the Int8 that the @_cdecl signature advertises.
        var (moduleDecl, typeDb) = CreateTestEnvironment("Container");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Container", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        // Method: func isEmpty() -> Bool
        var method = CreateMethodWithReturn("isEmpty", TypeSpecParser.Parse("Swift.Bool")!, parentDecl, moduleDecl);

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);
        var output = sw.ToString();

        // Must contain ternary Bool→Int8 conversion, not bare return
        Assert.Contains("result ? 1 : 0", output);
    }

    [Fact]
    public void GenericStaticDispatch_MutatingWithDirectReturn_WriteBackBeforeReturn()
    {
        // A mutating generic struct method that returns a direct value must
        // write back `selfPtr.assumingMemoryBound(to: Self.self).pointee = obj`
        // BEFORE the `return` statement, not after (where it would be unreachable).
        var (moduleDecl, typeDb) = CreateTestEnvironment("Stack");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Stack", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        // Method: mutating func pop() -> Int32
        var method = CreateMethodWithReturn("pop", TypeSpecParser.Parse("Swift.Int32")!, parentDecl, moduleDecl);
        method.IsMutating = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);
        var output = sw.ToString();

        // Write-back must appear in the output
        Assert.Contains("selfPtr.assumingMemoryBound(to: Self.self).pointee = obj", output);

        // Write-back must come BEFORE the return statement
        var writeBackIndex = output.IndexOf("selfPtr.assumingMemoryBound(to: Self.self).pointee = obj");
        var returnIndex = output.IndexOf("return ", writeBackIndex > 0 ? writeBackIndex : 0);
        Assert.True(writeBackIndex < returnIndex,
            "Mutating write-back must come before return statement to avoid being unreachable dead code");
    }

    [Fact]
    public void GenericStaticDispatch_TagOnlySimpleEnum_EmitsSafeCopyMemory()
    {
        // Tag-only simple enums (no RawValueTypeName) must use safe copyMemory
        // widening, not load(as: Int.self) which reads 8 bytes from a 1-byte value.
        var (moduleDecl, typeDb) = CreateTestEnvironment("Container");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Container", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        // Register a tag-only enum (no RawValueTypeName) in the existing TestModule
        typeDb.UpdateTypeRecord(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Status"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Status"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Status"),
                MetadataAccessor = "$s10TestModule_StatusMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum
                // No RawValueTypeName — tag-only enum
            });
        var method = CreateMethodWithReturn("getStatus", TypeSpecParser.Parse("TestModule.Status")!, parentDecl, moduleDecl);

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);
        var output = sw.ToString();

        // Tag-only enum must use safe copyMemory widening, NOT .rawValue or load(as: Int.self)
        Assert.Contains("let resultSize = MemoryLayout.size(ofValue: result)", output);
        Assert.Contains("var tag: Int = 0", output);
        Assert.Contains("copyMemory", output);
        Assert.Contains("byteCount: resultSize", output);
        Assert.DoesNotContain(".rawValue", output);
        Assert.DoesNotContain("load(as: Int.self)", output);
    }

    [Fact]
    public void GenericStaticDispatch_MutatingStringReturn_WriteBackBeforeEarlyReturn()
    {
        // A mutating generic struct method returning String has an early `return`
        // in the `if utf8.isEmpty` branch. The mutating write-back must happen
        // BEFORE that early return to avoid losing the mutation on empty strings.
        var (moduleDecl, typeDb) = CreateTestEnvironment("Buffer");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Buffer", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethodWithReturn("drain", TypeSpecParser.Parse("Swift.String")!, parentDecl, moduleDecl);
        method.IsMutating = true;

        var env = new MethodEnvironment(method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        MethodWrapperEmitter.EmitSwiftMethodWrapper(swiftWriter, env, ctx);
        var output = sw.ToString();

        // Write-back must appear in the output
        Assert.Contains("selfPtr.assumingMemoryBound(to: Self.self).pointee = obj", output);

        // Write-back must come BEFORE the early return in the empty string branch
        var writeBackIndex = output.IndexOf("selfPtr.assumingMemoryBound(to: Self.self).pointee = obj");
        var earlyReturnIndex = output.IndexOf("    return", writeBackIndex > 0 ? writeBackIndex : 0);
        Assert.True(writeBackIndex < earlyReturnIndex,
            "Mutating write-back must come before the early return in the empty string branch");
    }

    #endregion

    #region HasMethodOwnGenericParameters Tests

    [Fact]
    public void HasMethodOwnGenericParameters_NonGenericMethod_ReturnsFalse()
    {
        var (moduleDecl, _) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);

        Assert.False(WrapperValidation.HasMethodOwnGenericParameters(method));
    }

    [Fact]
    public void HasMethodOwnGenericParameters_MethodOnGenericType_InheritedOnly_ReturnsFalse()
    {
        // Method on generic type inherits parent's τ_0_0/T but has no own generic params.
        // ABI JSON includes parent's GenericSig on every method, making IsGeneric true.
        // HasMethodOwnGenericParameters should still return false.
        var (moduleDecl, _) = CreateTestEnvironment("Wrapper");
        var parentDecl = CreateStructDecl("Wrapper", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("unwrap", parentDecl, moduleDecl);
        // Simulate ABI JSON including parent's generic signature
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        Assert.True(method.IsGeneric, "IsGeneric should be true (inherited from parent)");
        Assert.False(WrapperValidation.HasMethodOwnGenericParameters(method));
    }

    [Fact]
    public void HasMethodOwnGenericParameters_MethodWithOwnGenericParam_ReturnsTrue()
    {
        // Method func pair<U>(...) on a non-generic type — U is method's own generic param.
        var (moduleDecl, _) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("pair", parentDecl, moduleDecl);
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new("U", "U", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        Assert.True(WrapperValidation.HasMethodOwnGenericParameters(method));
    }

    [Fact]
    public void HasMethodOwnGenericParameters_MethodWithOwnAndInherited_ReturnsTrue()
    {
        // Method func pair<U>(...) on GenericType<T> — has both inherited T and own U.
        var (moduleDecl, _) = CreateTestEnvironment("GenericBox");
        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("pair", parentDecl, moduleDecl);
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()),
            new("U", "U", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        Assert.True(WrapperValidation.HasMethodOwnGenericParameters(method));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericTypeMethod_InheritedGenericsOnly_NotBlockedByGuard6()
    {
        // Regression test: methods on generic types were incorrectly blocked by guard 6
        // because MethodDecl.IsGeneric includes parent-inherited generic params.
        // Guard 6 now uses HasMethodOwnGenericParameters to only block methods with
        // their own generic params (e.g., func pair<U>(...)).
        var (moduleDecl, typeDb) = CreateTestEnvironment("ConstrainedBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("ConstrainedBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("getDescription", parentDecl, moduleDecl);
        // Simulate ABI JSON including parent's GenericSig
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        // Concrete return type (String) — uses instance dispatch path
        method.CSSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                Name = "",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            }
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env),
            "Methods on generic types with only inherited generic params should not be blocked by guard 6");
    }

    #endregion
}
