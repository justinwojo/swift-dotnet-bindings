// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests that shared guard predicates produce consistent decisions across all 4 wrapper types.
/// Validates WrapperValidation shared predicates and WrapperStrategy enum semantics.
/// </summary>
public class WrapperConsistencyTests
{
    #region Non-Copyable Struct — Wrappers Must Accept (borrowing pointer semantics)

    [Fact]
    public void NonCopyableStructParent_AllWrappersAccept()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("NonCopyableToken");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        // Create a non-copyable struct: Escapable WITHOUT Copyable
        // Noncopyable types now get @_cdecl wrappers with borrowing pointer semantics
        var parentDecl = CreateStructDecl("NonCopyableToken", moduleDecl);
        parentDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.NonCopyableToken"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"),
                "$s10TestModule15NonCopyableTokenVACSWAAMc")
        };

        // Method — accepted (borrowing pointer semantics)
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var methodEnv = new MethodEnvironment(method, typeDb);
        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(methodEnv),
            "MethodWrapperEmitter should accept non-copyable struct parent");

        // Constructor — accepted (initialize(to:) is consuming, works for ~Copyable)
        var ctor = CreateConstructor("init", parentDecl, moduleDecl);
        var ctorEnv = new MethodEnvironment(ctor, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(ctorEnv),
            "ConstructorWrapperEmitter should accept non-copyable struct parent");

        // Property — accepted (borrowing pointer semantics)
        var (propertyDecl, propEnv) = CreatePropertyAndEnv("value", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);
        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, propEnv),
            "PropertyWrapperEmitter should accept non-copyable struct parent");

        // Subscript — accepted (borrowing pointer semantics)
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);
        var subEnv = new MethodEnvironment(accessor.Method, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, subEnv),
            "SubscriptWrapperEmitter should accept non-copyable struct parent");
    }

    [Fact]
    public void CopyableStructParent_AllWrappersAccept()
    {
        // Normal copyable struct (pre-Swift 6.2: empty conformances, Swift 6.2+: both Copyable+Escapable)
        var (moduleDecl, typeDb) = CreateTestEnvironment("NormalStruct");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("NormalStruct", moduleDecl);
        parentDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.NormalStruct"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"),
                "$s10TestModule12NormalStructVACSWAAMc"),
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.NormalStruct"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Copyable"),
                "$s10TestModule12NormalStructVACsSYAAMc")
        };

        // Method
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var methodEnv = new MethodEnvironment(method, typeDb);
        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(methodEnv),
            "MethodWrapperEmitter should accept copyable struct parent");

        // Constructor
        var ctor = CreateConstructor("init", parentDecl, moduleDecl);
        var ctorEnv = new MethodEnvironment(ctor, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(ctorEnv),
            "ConstructorWrapperEmitter should accept copyable struct parent");

        // Property
        var (propertyDecl, propEnv) = CreatePropertyAndEnv("value", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);
        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, propEnv),
            "PropertyWrapperEmitter should accept copyable struct parent");

        // Subscript
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);
        var subEnv = new MethodEnvironment(accessor.Method, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, subEnv),
            "SubscriptWrapperEmitter should accept copyable struct parent");
    }

    #endregion

    #region XCFramework Mode — All Wrappers Must Reject Without It

    [Fact]
    public void NoXCFrameworkMode_AllWrappersReject()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        // AsyncLibraryName is null — not in xcframework mode

        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        // Method
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var methodEnv = new MethodEnvironment(method, typeDb);
        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(methodEnv),
            "MethodWrapperEmitter should reject without xcframework mode");

        // Constructor
        var ctor = CreateConstructor("init", parentDecl, moduleDecl);
        var ctorEnv = new MethodEnvironment(ctor, typeDb);
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(ctorEnv),
            "ConstructorWrapperEmitter should reject without xcframework mode");

        // Property
        var (propertyDecl, propEnv) = CreatePropertyAndEnv("name", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);
        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, propEnv),
            "PropertyWrapperEmitter should reject without xcframework mode");

        // Subscript
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);
        var subEnv = new MethodEnvironment(accessor.Method, typeDb);
        Assert.False(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, subEnv),
            "SubscriptWrapperEmitter should reject without xcframework mode");
    }

    #endregion

    #region Generic Struct Parent — Constructor/Method/Property Accept, Subscript Rejects

    [Fact]
    public void GenericStructParent_ConstructorMethodPropertyAccept_SubscriptRejects()
    {
        // Generic struct parents now supported for constructor, method, and property
        // via protocol-based static dispatch. Subscript still blocked (separate emitter).
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        // Method with concrete signature on generic struct — blocked (may be from constrained extension)
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var methodEnv = new MethodEnvironment(method, typeDb);
        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(methodEnv),
            "MethodWrapperEmitter should reject generic struct parent with concrete signature");

        // Constructor — now accepted
        var ctor = CreateConstructor("init", parentDecl, moduleDecl);
        var ctorEnv = new MethodEnvironment(ctor, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(ctorEnv),
            "ConstructorWrapperEmitter should accept generic struct parent");

        // Property with concrete type on generic struct — blocked (may be from constrained extension)
        var (propertyDecl, propEnv) = CreatePropertyAndEnv("value", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);
        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, propEnv),
            "PropertyWrapperEmitter should reject generic struct parent with concrete property type");

        // Subscript — still blocked (not yet implemented)
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);
        var subEnv = new MethodEnvironment(accessor.Method, typeDb);
        Assert.False(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, subEnv),
            "SubscriptWrapperEmitter should reject generic struct parent");
    }

    [Fact]
    public void GenericClassParent_ConcreteSignature_AllWrappersAccept()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.IsFinal = true; // Non-final classes can't satisfy protocol init() requirement
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        // Method — concrete signature (no τ_0_0 in params/return)
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var methodEnv = new MethodEnvironment(method, typeDb);
        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(methodEnv),
            "MethodWrapperEmitter should accept generic class parent with concrete method");

        // Constructor — concrete signature (only final classes can use _SBW_CI_ protocol)
        var ctor = CreateConstructor("init", parentDecl, moduleDecl);
        var ctorEnv = new MethodEnvironment(ctor, typeDb);
        Assert.True(ConstructorWrapperEmitter.ShouldEmitWrapper(ctorEnv),
            "ConstructorWrapperEmitter should accept generic class parent with concrete constructor");

        // Property — concrete type
        var (propertyDecl, propEnv) = CreatePropertyAndEnv("count", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);
        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, propEnv),
            "PropertyWrapperEmitter should accept generic class parent with concrete property");

        // Subscript — concrete types
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);
        var subEnv = new MethodEnvironment(accessor.Method, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, subEnv),
            "SubscriptWrapperEmitter should accept generic class parent with concrete subscript");
    }

    #endregion

    #region WrapperValidation Shared Predicate Tests

    [Fact]
    public void IsXCFrameworkMode_NullAsyncLibraryName_ReturnsFalse()
    {
        var typeDb = new TypeDatabase();
        Assert.False(WrapperValidation.IsXCFrameworkMode(typeDb));
    }

    [Fact]
    public void IsXCFrameworkMode_EmptyAsyncLibraryName_ReturnsFalse()
    {
        var typeDb = new TypeDatabase();
        typeDb.AsyncLibraryName = "";
        Assert.False(WrapperValidation.IsXCFrameworkMode(typeDb));
    }

    [Fact]
    public void IsXCFrameworkMode_SetAsyncLibraryName_ReturnsTrue()
    {
        var typeDb = new TypeDatabase();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        Assert.True(WrapperValidation.IsXCFrameworkMode(typeDb));
    }

    [Fact]
    public void IsNonCopyableStructParent_NonCopyableStruct_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl();
        var structDecl = CreateStructDecl("Token", moduleDecl);
        structDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.Token"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"),
                "$s10TestModule5TokenVACSWAAMc")
        };

        Assert.True(WrapperValidation.IsNonCopyableStructParent(structDecl));
    }

    [Fact]
    public void IsNonCopyableStructParent_CopyableStruct_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl();
        var structDecl = CreateStructDecl("Point", moduleDecl);
        structDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"),
                "$s10TestModule5PointVACSWAAMc"),
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Copyable"),
                "$s10TestModule5PointVACsSYAAMc")
        };

        Assert.False(WrapperValidation.IsNonCopyableStructParent(structDecl));
    }

    [Fact]
    public void IsNonCopyableStructParent_ClassDecl_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("MyClass", moduleDecl);
        Assert.False(WrapperValidation.IsNonCopyableStructParent(classDecl));
    }

    [Fact]
    public void IsNonCopyableStructParent_Null_ReturnsFalse()
    {
        Assert.False(WrapperValidation.IsNonCopyableStructParent(null));
    }

    [Fact]
    public void IsActorIsolatedMember_ActorType_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl();
        var actorDecl = CreateClassDecl("MyActor", moduleDecl);
        actorDecl.IsActor = true;

        Assert.True(WrapperValidation.IsActorIsolatedMember(actorDecl, memberIsActorIsolated: false, memberIsMainActorIsolated: false));
    }

    [Fact]
    public void IsActorIsolatedMember_MainActorIsolatedParent_ReturnsFalse()
    {
        // @MainActor parent types are now allowed — synchronous gate lift
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("MyClass", moduleDecl);
        classDecl.IsMainActorIsolated = true;

        Assert.False(WrapperValidation.IsActorIsolatedMember(classDecl, memberIsActorIsolated: false, memberIsMainActorIsolated: false));
    }

    [Fact]
    public void IsActorIsolatedMember_PerMemberMainActor_ReturnsFalse()
    {
        // Per-member @MainActor on non-actor class is now allowed
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("MyClass", moduleDecl);

        Assert.False(WrapperValidation.IsActorIsolatedMember(classDecl, memberIsActorIsolated: true, memberIsMainActorIsolated: true));
    }

    [Fact]
    public void IsActorIsolatedMember_PerMemberCustomActor_ReturnsTrue()
    {
        // Per-member custom actor (e.g., @ProcessingActor) on non-actor class is still blocked
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("MyClass", moduleDecl);

        Assert.True(WrapperValidation.IsActorIsolatedMember(classDecl, memberIsActorIsolated: true, memberIsMainActorIsolated: false));
    }

    [Fact]
    public void IsActorIsolatedMember_NormalClass_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("MyClass", moduleDecl);

        Assert.False(WrapperValidation.IsActorIsolatedMember(classDecl, memberIsActorIsolated: false, memberIsMainActorIsolated: false));
    }

    [Fact]
    public void IsActorIsolatedMember_ActorTypeWithNonisolatedMember_ReturnsFalse()
    {
        // nonisolated members on an actor type opt out of actor isolation and are
        // safe to dispatch synchronously through a @_cdecl wrapper.
        var moduleDecl = CreateModuleDecl();
        var actorDecl = CreateClassDecl("MyActor", moduleDecl);
        actorDecl.IsActor = true;

        Assert.False(WrapperValidation.IsActorIsolatedMember(
            actorDecl,
            memberIsActorIsolated: false,
            memberIsMainActorIsolated: false,
            memberIsNonisolated: true));
    }

    [Fact]
    public void IsActorIsolatedMember_PerMemberCustomActorWithNonisolated_ReturnsFalse()
    {
        // nonisolated overrides any per-member isolation flag.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("MyClass", moduleDecl);

        Assert.False(WrapperValidation.IsActorIsolatedMember(
            classDecl,
            memberIsActorIsolated: true,
            memberIsMainActorIsolated: false,
            memberIsNonisolated: true));
    }

    [Fact]
    public void IsActorIsolatedMember_CustomGlobalActorIsolatedParent_NonIsolatedMethod_ReturnsFalse()
    {
        // SWIFTBIND022 narrows blocking to constructors via CanEmitMember; this helper
        // must remain permissive for plain instance members on a custom-global-actor type.
        // A nonisolated method on @ImagePipelineActor class X compiles fine in a synchronous
        // @_cdecl wrapper because the call doesn't cross the actor boundary.
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("ImagePrefetcher", moduleDecl);
        classDecl.IsCustomActorIsolated = true;

        // No isolation, no nonisolated marker — IsActorIsolatedMember should NOT block.
        Assert.False(WrapperValidation.IsActorIsolatedMember(
            classDecl,
            memberIsActorIsolated: false,
            memberIsMainActorIsolated: false));

        // Even an actor-isolated member that is also marked nonisolated stays unblocked.
        Assert.False(WrapperValidation.IsActorIsolatedMember(
            classDecl,
            memberIsActorIsolated: true,
            memberIsMainActorIsolated: false,
            memberIsNonisolated: true));
    }

    [Fact]
    public void ContainsParameterizedProtocol_ProtocolBaseWithGenericArg_ReturnsTrue()
    {
        // Parameterized-protocol pattern: `EventStream<UIEvent>` where EventStream is a protocol
        // with a primary associated type. Requires iOS 16+ runtime; must be blocked.
        var (_, typeDb) = CreateTestEnvironment("BlinkIDUX");
        var testModule = new ModuleTypeDatabase("BlinkIDUX", "/tmp/BlinkIDUX.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("BlinkIDUX.EventStream"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("BlinkIDUX", "EventStream"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("BlinkIDUX.EventStream"),
                MetadataAccessor = "$sMp",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
            });
        typeDb.AddModuleDatabase(testModule);

        var typeSpec = new NamedTypeSpec("BlinkIDUX.EventStream");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        Assert.True(WrapperValidation.ContainsParameterizedProtocol(typeSpec, typeDb));
    }

    [Fact]
    public void ContainsParameterizedProtocol_NonProtocolGeneric_ReturnsFalse()
    {
        // `Array<Int>` — concrete generic, no protocol anywhere. Must NOT be flagged.
        var (_, typeDb) = CreateTestEnvironment("TestModule");

        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        Assert.False(WrapperValidation.ContainsParameterizedProtocol(typeSpec, typeDb));
    }

    [Fact]
    public void ContainsParameterizedProtocol_ProtocolAsGenericArg_ReturnsTrue()
    {
        // `Array<MyProtocol>` — protocol appears as the generic argument (legacy case).
        var (_, typeDb) = CreateTestEnvironment("TestModule");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyProtocol"),
                MetadataAccessor = "$sMp",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
            });
        typeDb.AddModuleDatabase(testModule);

        var typeSpec = new NamedTypeSpec("Swift.Array");
        typeSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyProtocol"));

        Assert.True(WrapperValidation.ContainsParameterizedProtocol(typeSpec, typeDb));
    }

    [Fact]
    public void IsMetatypeType_BareType_ReturnsTrue()
    {
        Assert.True(WrapperValidation.IsMetatypeType(new NamedTypeSpec("Type")));
    }

    [Fact]
    public void IsMetatypeType_QualifiedType_ReturnsTrue()
    {
        Assert.True(WrapperValidation.IsMetatypeType(new NamedTypeSpec("Any.Type")));
        Assert.True(WrapperValidation.IsMetatypeType(new NamedTypeSpec("MyModule.MyClass.Type")));
    }

    [Fact]
    public void IsMetatypeType_NonMetatype_ReturnsFalse()
    {
        Assert.False(WrapperValidation.IsMetatypeType(new NamedTypeSpec("Swift.Int")));
        Assert.False(WrapperValidation.IsMetatypeType(new NamedTypeSpec("TypeAlias")));
    }

    [Fact]
    public void IsMetatypeType_NestedInnerType_ReturnsTrue()
    {
        // TypeSpecParser produces nested NamedTypeSpec chains for module-qualified metatypes:
        // "Foundation.Decimal.Type" → Foundation(InnerType: Decimal(InnerType: Type))
        var outerSpec = new NamedTypeSpec("Foundation")
        {
            InnerType = new NamedTypeSpec("Decimal")
            {
                InnerType = new NamedTypeSpec("Type")
            }
        };
        Assert.True(WrapperValidation.IsMetatypeType(outerSpec));
    }

    [Fact]
    public void IsMetatypeType_NestedNonMetatype_ReturnsFalse()
    {
        // "Foundation.Decimal" → Foundation(InnerType: Decimal) — NOT a metatype
        var outerSpec = new NamedTypeSpec("Foundation")
        {
            InnerType = new NamedTypeSpec("Decimal")
        };
        Assert.False(WrapperValidation.IsMetatypeType(outerSpec));
    }

    [Fact]
    public void IsNestedType_NestedSwiftType_ReturnsTrue()
    {
        // Uses AppleFrameworkRegistry.IsNestedType which checks for double dots after module
        var spec = new NamedTypeSpec("UIKit.UIView.ContentMode");
        Assert.True(WrapperValidation.IsNestedType(spec));
    }

    [Fact]
    public void IsNestedType_TopLevelType_ReturnsFalse()
    {
        var spec = new NamedTypeSpec("UIKit.UIView");
        Assert.False(WrapperValidation.IsNestedType(spec));
    }

    [Fact]
    public void IsOptionalType_Optional_ReturnsTrue()
    {
        var spec = new NamedTypeSpec("Swift.Optional");
        spec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        Assert.True(WrapperValidation.IsOptionalType(spec));
    }

    [Fact]
    public void IsOptionalType_NonOptional_ReturnsFalse()
    {
        Assert.False(WrapperValidation.IsOptionalType(new NamedTypeSpec("Swift.Int")));
    }

    [Fact]
    public void IsSupportedCollectionType_Array_ReturnsTrue()
    {
        Assert.True(WrapperValidation.IsSupportedCollectionType(new NamedTypeSpec("Swift.Array")));
        Assert.True(WrapperValidation.IsSupportedCollectionType(new NamedTypeSpec("Swift.Dictionary")));
        Assert.True(WrapperValidation.IsSupportedCollectionType(new NamedTypeSpec("Swift.Set")));
    }

    [Fact]
    public void IsSupportedCollectionType_Result_ReturnsFalse()
    {
        Assert.False(WrapperValidation.IsSupportedCollectionType(new NamedTypeSpec("Swift.Result")));
    }

    #endregion

    #region WrapperStrategy Enum Tests

    [Fact]
    public void WrapperStrategy_MutualExclusivity_LastWriteWins()
    {
        var method = CreateSimpleMethod();

        method.UsesCdeclMethodWrapper = true;
        Assert.True(method.UsesCdeclMethodWrapper);
        Assert.False(method.UsesCdeclConstructorWrapper);

        // Setting constructor clears method
        method.UsesCdeclConstructorWrapper = true;
        Assert.True(method.UsesCdeclConstructorWrapper);
        Assert.False(method.UsesCdeclMethodWrapper);
        Assert.False(method.UsesCdeclPropertyWrapper);
    }

    [Fact]
    public void WrapperStrategy_UsesCdeclWrapper_ComputedCorrectly()
    {
        var method = CreateSimpleMethod();

        Assert.False(method.UsesCdeclWrapper);

        method.UsesCdeclConstructorWrapper = true;
        Assert.True(method.UsesCdeclWrapper);

        method.WrapperStrategy = WrapperStrategy.None;
        Assert.False(method.UsesCdeclWrapper);

        method.UsesCdeclPropertyWrapper = true;
        Assert.True(method.UsesCdeclWrapper);

        method.WrapperStrategy = WrapperStrategy.None;
        method.UsesCdeclMethodWrapper = true;
        Assert.True(method.UsesCdeclWrapper);
    }

    [Fact]
    public void WrapperStrategy_None_IsNotCdeclWrapper()
    {
        var method = CreateSimpleMethod();
        method.WrapperStrategy = WrapperStrategy.None;
        Assert.False(method.UsesCdeclWrapper);
        Assert.False(method.UsesCdeclConstructorWrapper);
        Assert.False(method.UsesCdeclPropertyWrapper);
        Assert.False(method.UsesCdeclMethodWrapper);
    }

    [Fact]
    public void WrapperStrategy_Default_IsNone()
    {
        var method = CreateSimpleMethod();
        Assert.Equal(WrapperStrategy.None, method.WrapperStrategy);
    }

    [Fact]
    public void WrapperStrategy_FreeFunctionWrapperIsOrthogonal()
    {
        // UsesFreeFunctionWrapper is NOT part of the enum — it's an independent modifier
        var method = CreateSimpleMethod();
        method.UsesCdeclMethodWrapper = true;
        method.UsesFreeFunctionWrapper = true;

        Assert.True(method.UsesCdeclMethodWrapper);
        Assert.True(method.UsesFreeFunctionWrapper);
        Assert.True(method.UsesCdeclWrapper);
        Assert.Equal(WrapperStrategy.CdeclMethod, method.WrapperStrategy);
    }

    [Fact]
    public void WrapperStrategy_HasCdeclClosureMarshalling_WorksWithEnum()
    {
        var method = CreateSimpleMethod();

        // Standalone closure cdecl wrapper
        method.HasClosureCdeclWrapper = true;
        Assert.True(method.HasCdeclClosureMarshalling);

        // @_cdecl method wrapper with closure params
        method.HasClosureCdeclWrapper = false;
        method.UsesCdeclMethodWrapper = true;
        method.HasClosureParams = true;
        Assert.True(method.HasCdeclClosureMarshalling);

        // Neither
        method.WrapperStrategy = WrapperStrategy.None;
        method.HasClosureParams = false;
        Assert.False(method.HasCdeclClosureMarshalling);
    }

    [Fact]
    public void WrapperStrategy_ClearingBooleanFlag_ResetsToNone()
    {
        var method = CreateSimpleMethod();

        // Set a wrapper strategy via boolean
        method.UsesCdeclMethodWrapper = true;
        Assert.Equal(WrapperStrategy.CdeclMethod, method.WrapperStrategy);

        // Clear it — must reset to None, not leave stale strategy
        method.UsesCdeclMethodWrapper = false;
        Assert.Equal(WrapperStrategy.None, method.WrapperStrategy);
        Assert.False(method.UsesCdeclWrapper);
    }

    [Fact]
    public void WrapperStrategy_ClearingConstructorFlag_ResetsToNone()
    {
        var method = CreateSimpleMethod();

        method.UsesCdeclConstructorWrapper = true;
        Assert.True(method.UsesCdeclConstructorWrapper);

        method.UsesCdeclConstructorWrapper = false;
        Assert.Equal(WrapperStrategy.None, method.WrapperStrategy);
        Assert.False(method.UsesCdeclWrapper);
    }

    [Fact]
    public void WrapperStrategy_ClearingPropertyFlag_ResetsToNone()
    {
        var method = CreateSimpleMethod();

        method.UsesCdeclPropertyWrapper = true;
        Assert.True(method.UsesCdeclPropertyWrapper);

        method.UsesCdeclPropertyWrapper = false;
        Assert.Equal(WrapperStrategy.None, method.WrapperStrategy);
        Assert.False(method.UsesCdeclWrapper);
    }

    [Fact]
    public void WrapperStrategy_ClearingUnrelatedFlag_PreservesActiveStrategy()
    {
        var method = CreateSimpleMethod();

        // Set to CdeclMethod, then clear CdeclConstructor — must NOT affect CdeclMethod
        method.UsesCdeclMethodWrapper = true;
        method.UsesCdeclConstructorWrapper = false;
        Assert.Equal(WrapperStrategy.CdeclMethod, method.WrapperStrategy);
        Assert.True(method.UsesCdeclMethodWrapper);
    }

    [Fact]
    public void WrapperStrategy_ClearingActiveFlag_ResetsToNone()
    {
        var method = CreateSimpleMethod();

        // Set to CdeclMethod, then clear CdeclMethod — must reset
        method.UsesCdeclMethodWrapper = true;
        method.UsesCdeclMethodWrapper = false;
        Assert.Equal(WrapperStrategy.None, method.WrapperStrategy);
    }

    #endregion

    #region Actor Isolation — Custom Actors Reject, @MainActor Accepts

    [Fact]
    public void CustomActorParent_SubscriptWrapperRejects()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("ActorParent");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("ActorParent", moduleDecl);
        parentDecl.IsActor = true;

        var accessorMethod = CreateAccessorMethod("getter:item", isGetter: true, parentDecl, moduleDecl);
        var accessor = new GetAccessorDecl { Method = accessorMethod };
        var subscript = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("index", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessorMethod, typeDb);
        Assert.False(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscript, accessor, env));
    }

    [Fact]
    public void MainActorIsolatedParent_SubscriptWrapperAccepts()
    {
        // @MainActor parent types are now allowed — synchronous gate lift
        var (moduleDecl, typeDb) = CreateTestEnvironment("MainActorParent");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MainActorParent", moduleDecl);
        parentDecl.IsMainActorIsolated = true;

        var accessorMethod = CreateAccessorMethod("getter:item", isGetter: true, parentDecl, moduleDecl);
        var accessor = new GetAccessorDecl { Method = accessorMethod };
        var subscript = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("index", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessorMethod, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscript, accessor, env));
    }

    [Fact]
    public void MainActorIsolatedAccessor_SubscriptWrapperAccepts()
    {
        // Per-member @MainActor on non-actor class is now allowed
        var (moduleDecl, typeDb) = CreateTestEnvironment("NormalParent");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("NormalParent", moduleDecl);

        var accessorMethod = CreateAccessorMethod("getter:item", isGetter: true, parentDecl, moduleDecl);
        accessorMethod.IsActorIsolated = true;
        accessorMethod.IsMainActorIsolated = true;
        var accessor = new GetAccessorDecl { Method = accessorMethod };
        var subscript = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("index", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessorMethod, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscript, accessor, env));
    }

    #endregion

    #region WrapperValidation.GetRejectionReason Tests

    [Fact]
    public void GetRejectionReason_ValidMethod_ReturnsNull()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.Null(WrapperValidation.GetRejectionReason(env));
    }

    [Fact]
    public void GetRejectionReason_AsyncMethod_ReturnsAsyncReason()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        method.IsAsync = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.Equal("async_method", WrapperValidation.GetRejectionReason(env));
    }

    [Fact]
    public void GetRejectionReason_NonCopyableStruct_ReturnsNull()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Token");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Token", moduleDecl);
        parentDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.Token"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"),
                "$s10TestModule5TokenVACSWAAMc")
        };
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.Null(WrapperValidation.GetRejectionReason(env));
    }

    [Fact]
    public void GetRejectionReason_GenericMethod_ReturnsReason()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("transform", parentDecl, moduleDecl);
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.Equal("method_level_generics", WrapperValidation.GetRejectionReason(env));
    }

    [Fact]
    public void GetRejectionReason_CustomActor_ReturnsActorType()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Counter");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Counter", moduleDecl);
        parentDecl.IsActor = true;
        var method = CreateMethod("increment", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.Equal("actor_type", WrapperValidation.GetRejectionReason(env));
    }

    [Fact]
    public void GetRejectionReason_MainActorParent_ReturnsNull()
    {
        // @MainActor parent types are now allowed — no rejection
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        parentDecl.IsMainActorIsolated = true;
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.Null(WrapperValidation.GetRejectionReason(env));
    }

    #endregion

    #region Raw Generic Type Params — Wrapper Rejection

    [Fact]
    public void HasRawGenericTypeParams_ParamWithTau_ReturnsTrue()
    {
        var method = CreateMethodWithGenericParam();
        Assert.True(WrapperValidation.HasRawGenericTypeParams(method));
    }

    [Fact]
    public void HasRawGenericTypeParams_NormalParams_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl();
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        Assert.False(WrapperValidation.HasRawGenericTypeParams(method));
    }

    [Fact]
    public void HasRawGenericTypeParams_ReturnTypeWithTau_ReturnsTrue()
    {
        var method = CreateMethodWithGenericReturn();
        Assert.True(WrapperValidation.HasRawGenericTypeParams(method));
    }

    [Fact]
    public void GetRejectionReason_RawGenericParam_ReturnsReason()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithGenericParam();
        method.ParentDecl = parentDecl;
        method.ModuleDecl = moduleDecl;
        var env = new MethodEnvironment(method, typeDb);
        Assert.Equal("raw_generic_type_params", WrapperValidation.GetRejectionReason(env));
    }

    [Fact]
    public void ConstructorWrapper_RawGenericParam_Rejected()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        // Constructor with τ_0_0 parameter
        var ctor = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule_init_generic",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { SwiftTypeSpec = TupleTypeSpec.Empty, Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new ArgumentDecl { SwiftTypeSpec = new NamedTypeSpec("\u03c4_0_0"), Name = "value", PrivateName = "value", IsInOut = false, IsGeneric = true, ParentDecl = null, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(ctor, typeDb);
        Assert.False(ConstructorWrapperEmitter.ShouldEmitWrapper(env),
            "Constructor with raw generic param should be rejected");
    }

    [Fact]
    public void PropertyWrapper_RawGenericTypeSpec_Rejected()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        // Property with τ_0_0 type
        var (propertyDecl, env) = CreatePropertyAndEnv(
            "value", new NamedTypeSpec("\u03c4_0_0"), parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env),
            "Property with raw generic type param should be rejected");
    }

    [Fact]
    public void SubscriptWrapper_RawGenericReturn_Rejected()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var getterMethod = CreateAccessorMethod("getter:subscript", isGetter: true, parentDecl, moduleDecl);
        var accessor = new GetAccessorDecl { Method = getterMethod };
        var subscript = CreateSubscriptDecl(
            new NamedTypeSpec("\u03c4_0_0"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(getterMethod, typeDb);
        Assert.False(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscript, accessor, env),
            "Subscript with raw generic return type should be rejected");
    }

    [Fact]
    public void SubscriptWrapper_RawGenericIndexParam_Rejected()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var getterMethod = CreateAccessorMethod("getter:subscript", isGetter: true, parentDecl, moduleDecl);
        var accessor = new GetAccessorDecl { Method = getterMethod };
        var subscript = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("\u03c4_0_0"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(getterMethod, typeDb);
        Assert.False(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscript, accessor, env),
            "Subscript with raw generic index param should be rejected");
    }

    #endregion

    #region Property/Subscript Rejection Reasons

    [Fact]
    public void PropertyGetRejectionReason_ClosureProperty_ReturnsNull()
    {
        // Direct closure properties are now supported via resultPtr + invoke thunk
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var closureType = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var (propertyDecl, env) = CreatePropertyAndEnv("handler", closureType, parentDecl, moduleDecl, typeDb);

        Assert.Null(PropertyWrapperEmitter.GetRejectionReason(propertyDecl, env));
    }

    [Fact]
    public void PropertyGetRejectionReason_RawGenericTypeParam_ReturnsReason()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var (propertyDecl, env) = CreatePropertyAndEnv("value", new NamedTypeSpec("\u03c4_0_0"), parentDecl, moduleDecl, typeDb);

        Assert.Equal("raw_generic_type_params", PropertyWrapperEmitter.GetRejectionReason(propertyDecl, env));
    }

    [Fact]
    public void PropertyGetRejectionReason_NormalProperty_ReturnsNull()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var (propertyDecl, env) = CreatePropertyAndEnv("count", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.Null(PropertyWrapperEmitter.GetRejectionReason(propertyDecl, env));
    }

    [Fact]
    public void SubscriptGetRejectionReason_StaticSubscript_ReturnsReason()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var getterMethod = CreateAccessorMethod("getter:subscript", isGetter: true, parentDecl, moduleDecl);
        var accessor = new GetAccessorDecl { Method = getterMethod };
        var subscript = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl, isStatic: true);

        var env = new MethodEnvironment(getterMethod, typeDb);
        Assert.Equal("static_subscript", SubscriptWrapperEmitter.GetRejectionReason(subscript, accessor, env));
    }

    [Fact]
    public void SubscriptGetRejectionReason_RawGenericReturn_ReturnsReason()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var getterMethod = CreateAccessorMethod("getter:subscript", isGetter: true, parentDecl, moduleDecl);
        var accessor = new GetAccessorDecl { Method = getterMethod };
        var subscript = CreateSubscriptDecl(
            new NamedTypeSpec("\u03c4_0_0"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(getterMethod, typeDb);
        Assert.Equal("raw_generic_type_params", SubscriptWrapperEmitter.GetRejectionReason(subscript, accessor, env));
    }

    [Fact]
    public void SubscriptGetRejectionReason_NormalSubscript_ReturnsNull()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);

        var getterMethod = CreateAccessorMethod("getter:subscript", isGetter: true, parentDecl, moduleDecl);
        var accessor = new GetAccessorDecl { Method = getterMethod };
        var subscript = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(getterMethod, typeDb);
        Assert.Null(SubscriptWrapperEmitter.GetRejectionReason(subscript, accessor, env));
    }

    #endregion

    #region CanEmitMember — Unified Gate Tests

    [Fact]
    public void CanEmitMember_NoXCFrameworkMode_AllKindsReject()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestModule");
        // Don't set AsyncLibraryName — xcframework mode is off
        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        parentDecl.IsFrozen = true;
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Method));
        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Constructor));
        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Property));
        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Subscript));
    }

    [Fact]
    public void CanEmitMember_ModuleInternal_MethodConstructorPropertyReject()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestModule");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        parentDecl.IsFrozen = true;
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        // Method and Constructor should reject when isModuleInternal is true
        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Method, isModuleInternal: true));
        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Constructor, isModuleInternal: true));
        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Property, isModuleInternal: true));
        // Subscript does NOT check module internal
        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Subscript, isModuleInternal: true));
    }

    [Fact]
    public void CanEmitMember_SpiProtected_MethodPropertyReject()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestModule");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        parentDecl.IsFrozen = true;
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        // Method and Property should reject when isSpiProtected is true
        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Method, isSpiProtected: true));
        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Property, isSpiProtected: true));
        // Constructor and Subscript do NOT check SPI
        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Constructor, isSpiProtected: true));
        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Subscript, isSpiProtected: true));
    }

    [Fact]
    public void CanEmitMember_NonCopyableStruct_AllAccepted()
    {
        // Noncopyable types now get @_cdecl wrappers with borrowing pointer semantics
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestModule");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateStructDecl("NonCopyableToken", moduleDecl);
        parentDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.NonCopyableToken"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"),
                "$s10TestModule15NonCopyableTokenVACSWAAMc")
        };
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Method));
        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Constructor));
        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Property));
        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Subscript));
        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Operator));
    }

    [Fact]
    public void CanEmitMember_Async_MethodConstructorReject()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestModule");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        parentDecl.IsFrozen = true;
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        // Method and Constructor should reject when isAsync is true
        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Method, isAsync: true));
        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Constructor, isAsync: true));
        // Property and Subscript check async differently (via accessor), not via CanEmitMember
        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Property, isAsync: true));
        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Subscript, isAsync: true));
    }

    [Fact]
    public void CanEmitMember_NormalStruct_AllPass()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestModule");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        parentDecl.IsFrozen = true;
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Method));
        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Constructor));
        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Property));
        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Subscript));
    }

    #endregion

    #region SWIFTBIND022 — Custom Global Actor Resolution

    [Fact]
    public void TryResolveCustomActorExecutor_SameModuleActor_Resolves()
    {
        // Standard @globalActor pattern: a sibling actor in the same module with a
        // `static var shared` singleton. When the resolver succeeds, the SWIFTBIND022
        // narrowing keeps the constructor in the C# binding (calling Swift's native
        // init via CallConvSwift) instead of triggering the wholesale skip.
        var moduleDecl = CreateModuleDecl();
        var actorDecl = CreateActorDecl("BindingsTestGlobalActor", moduleDecl);
        var isolatedClass = CreateClassDecl("GlobalActorIsolatedClass", moduleDecl);
        isolatedClass.IsCustomActorIsolated = true;
        isolatedClass.CustomActorIsolatorName = "BindingsTestGlobalActor";

        Assert.True(WrapperValidation.TryResolveCustomActorExecutor(isolatedClass, out var actorRef));
        Assert.Equal("TestModule.BindingsTestGlobalActor", actorRef);
    }

    [Fact]
    public void TryResolveCustomActorExecutor_MissingActor_FallsBack()
    {
        // Imported global actor whose TypeDecl isn't in the bound module. The resolver
        // must return false so MethodHandler / WrapperValidation skip the constructor
        // wholesale via SWIFTBIND022 instead of emitting a constructor that calls into
        // an unreachable actor's executor.
        var moduleDecl = CreateModuleDecl();
        var isolatedClass = CreateClassDecl("ImagePrefetcher", moduleDecl);
        isolatedClass.IsCustomActorIsolated = true;
        isolatedClass.CustomActorIsolatorName = "ImagePipelineActor";

        Assert.False(WrapperValidation.TryResolveCustomActorExecutor(isolatedClass, out var actorRef));
        Assert.Equal(string.Empty, actorRef);
    }

    [Fact]
    public void TryResolveCustomActorExecutor_ActorWithoutSharedSingleton_FallsBack()
    {
        // An actor type that lacks the `static var shared` accessor isn't a usable
        // @globalActor — there's no canonical singleton to call into. Fall back to the
        // SWIFTBIND022 wholesale skip.
        var moduleDecl = CreateModuleDecl();
        var actorDecl = CreateClassDecl("NotAGlobalActor", moduleDecl);
        actorDecl.IsCustomActor = true;
        // Deliberately no `shared` property: HasSingletonPattern is false.

        var isolatedClass = CreateClassDecl("Isolated", moduleDecl);
        isolatedClass.IsCustomActorIsolated = true;
        isolatedClass.CustomActorIsolatorName = "NotAGlobalActor";

        Assert.False(WrapperValidation.TryResolveCustomActorExecutor(isolatedClass, out _));
    }

    [Fact]
    public void TryResolveCustomActorExecutor_ResolverNameMissing_FallsBack()
    {
        // Belt-and-suspenders: a TypeDecl flagged IsCustomActorIsolated but with no
        // CustomActorIsolatorName recorded (e.g., from a code path that pre-dates the
        // map plumbing) must still fall through to the SWIFTBIND022 skip.
        var moduleDecl = CreateModuleDecl();
        var isolatedClass = CreateClassDecl("Isolated", moduleDecl);
        isolatedClass.IsCustomActorIsolated = true;
        // CustomActorIsolatorName is null.

        Assert.False(WrapperValidation.TryResolveCustomActorExecutor(isolatedClass, out _));
    }

    [Fact]
    public void CanEmitMember_CustomGlobalActor_ConstructorAcceptsWhenActorResolves()
    {
        // The SWIFTBIND022 narrowing must let CanEmitMember return true for a
        // custom-global-actor-isolated constructor when the actor TypeDecl is reachable
        // in the bound module — the constructor stays in the C# binding (calling Swift's
        // native init via CallConvSwift) instead of being skipped wholesale. The
        // complementary "actor not resolvable → fallback skip" case is covered by
        // CanEmitMember_CustomGlobalActor_ConstructorRejectsWhenActorMissing below.
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestModule");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var actorDecl = CreateActorDecl("BindingsTestGlobalActor", moduleDecl);
        var parentDecl = CreateClassDecl("GlobalActorIsolatedClass", moduleDecl);
        parentDecl.IsCustomActorIsolated = true;
        parentDecl.CustomActorIsolatorName = "BindingsTestGlobalActor";

        var ctor = CreateConstructor("init", parentDecl, moduleDecl);
        var env = new MethodEnvironment(ctor, typeDb);

        Assert.True(WrapperValidation.CanEmitMember(env, MemberKind.Constructor),
            "CanEmitMember must accept a custom-global-actor-isolated constructor when the actor is resolvable.");
    }

    [Fact]
    public void CanEmitMember_CustomGlobalActor_ConstructorRejectsWhenActorMissing()
    {
        // Fallback path: the actor isolating this type isn't in the bound module(s).
        // The constructor would call into an unreachable actor's executor at runtime,
        // so SWIFTBIND022 skips it wholesale.
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestModule");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("ImagePrefetcher", moduleDecl);
        parentDecl.IsCustomActorIsolated = true;
        parentDecl.CustomActorIsolatorName = "ImagePipelineActor"; // not a TypeDecl in this module

        var ctor = CreateConstructor("init", parentDecl, moduleDecl);
        var env = new MethodEnvironment(ctor, typeDb);

        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Constructor),
            "CanEmitMember must still skip the constructor (SWIFTBIND022 fallback) when the actor isn't reachable.");
    }

    [Fact]
    public void TryResolveCustomActorExecutor_NestedActor_Resolves()
    {
        // Nested @globalActor declarations (e.g., `extension Outer { @globalActor public
        // actor Inner { … } }`) live inside another type's `Types` list, not the module's
        // top-level type list. The resolver's recursive lookup must find them.
        var (moduleDecl, _) = CreateTestEnvironment("OuterContainer");

        var outerDecl = CreateClassDecl("OuterContainer", moduleDecl);
        var nestedActor = CreateActorDecl("InnerActor", moduleDecl);
        // Move the actor from module-level into the outer's nested types so the
        // module's `Types` no longer reaches it directly — only the recursive walk does.
        moduleDecl.Types.Remove(nestedActor);
        outerDecl.Types.Add(nestedActor);
        nestedActor.ParentDecl = outerDecl;

        var isolatedClass = CreateClassDecl("IsolatedClass", moduleDecl);
        isolatedClass.IsCustomActorIsolated = true;
        isolatedClass.CustomActorIsolatorName = "InnerActor";

        Assert.True(WrapperValidation.TryResolveCustomActorExecutor(isolatedClass, out var actorRef),
            "Resolver must walk nested Types to find a global actor declared inside another type.");
        Assert.Equal("TestModule.InnerActor", actorRef);
    }

    /// <summary>
    /// Helper: create a custom actor TypeDecl with the standard `static var shared` singleton.
    /// Mirrors the @globalActor pattern (`@globalActor public actor Foo { public static let
    /// shared = Foo() }`) so the resolver's `IsCustomActor` + `HasSingletonPattern` checks
    /// both succeed.
    /// </summary>
    private static ClassDecl CreateActorDecl(string name, ModuleDecl moduleDecl)
    {
        var actorDecl = CreateClassDecl(name, moduleDecl);
        actorDecl.IsCustomActor = true;
        // HasSingletonPattern requires a static `shared` property whose SwiftTypeSpec
        // name ends with the actor's Name (matches `static let shared: Self`).
        actorDecl.Properties.Add(new PropertyDecl
        {
            Name = "shared",
            SwiftTypeSpec = new NamedTypeSpec($"TestModule.{name}"),
            HasStorage = true,
            IsStatic = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = actorDecl,
            ModuleDecl = moduleDecl
        });
        return actorDecl;
    }

    #endregion

    #region Test Helpers

    private static MethodDecl CreateMethodWithGenericParam()
    {
        return new MethodDecl
        {
            Name = "process",
            MangledName = "$s_process",
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
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("\u03c4_0_0"),
                    Name = "value",
                    PrivateName = "value",
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodWithGenericReturn()
    {
        return new MethodDecl
        {
            Name = "getValue",
            MangledName = "$s_getValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("\u03c4_0_0"),
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateSimpleMethod()
    {
        return new MethodDecl
        {
            Name = "test",
            MangledName = "$s_test",
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
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Types = new List<TypeDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
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

    private static MethodDecl CreateConstructor(string name, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = true,
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

    private static (PropertyDecl propertyDecl, MethodEnvironment env) CreatePropertyAndEnv(
        string propertyName, TypeSpec typeSpec, TypeDecl parentDecl, ModuleDecl moduleDecl, TypeDatabase typeDb)
    {
        var getterMethod = CreateAccessorMethod($"getter:{propertyName}", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = propertyName,
            SwiftTypeSpec = typeSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        return (propertyDecl, env);
    }

    private static SubscriptDecl CreateSubscriptDecl(
        TypeSpec returnType, ArgumentDecl[] indexParams, AccessorDecl[] accessors,
        TypeDecl parentDecl, ModuleDecl moduleDecl, bool isStatic = false)
    {
        return new SubscriptDecl
        {
            Name = "subscript",
            ReturnTypeSpec = returnType,
            IndexParameters = indexParams.ToList(),
            IsStatic = isStatic,
            Accessors = accessors.ToList(),
            MangledName = "$s10TestModule_subscript",
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static ArgumentDecl CreateIndexParam(string name, TypeSpec typeSpec, ModuleDecl? moduleDecl)
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

    private static MethodDecl CreateAccessorMethod(string name, bool isGetter, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_accessor_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            CSSignature = new List<ArgumentDecl>(),
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
        typeDb.AddModuleDatabase(swiftModule);

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Types = new List<TypeDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        return (moduleDecl, typeDb);
    }

    #endregion

    #region HasIncompatibleFields Tests

    [Fact]
    public void HasIncompatibleFields_FloatFields_ReturnsTrue()
    {
        var record = new TypeRecord
        {
            Kind = TypeRecordKind.Struct,
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FloatStruct"),
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FloatStruct"),
            MetadataAccessor = "$sMa",
        };
        Assert.True(WrapperValidation.HasIncompatibleFields(record));
    }

    [Fact]
    public void HasIncompatibleFields_BoolFields_ReturnsTrue()
    {
        var record = new TypeRecord
        {
            Kind = TypeRecordKind.Struct,
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasBoolFields,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BoolStruct"),
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BoolStruct"),
            MetadataAccessor = "$sMa",
        };
        Assert.True(WrapperValidation.HasIncompatibleFields(record));
    }

    [Fact]
    public void HasIncompatibleFields_IntegerOnly_ReturnsFalse()
    {
        var record = new TypeRecord
        {
            Kind = TypeRecordKind.Struct,
            Flags = TypeRecordFlags.Frozen,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.IntStruct"),
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IntStruct"),
            MetadataAccessor = "$sMa",
        };
        Assert.False(WrapperValidation.HasIncompatibleFields(record));
    }

    [Fact]
    public void HasIncompatibleFields_NoFlags_ReturnsFalse()
    {
        var record = new TypeRecord
        {
            Kind = TypeRecordKind.Struct,
            Flags = TypeRecordFlags.None,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.EmptyStruct"),
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "EmptyStruct"),
            MetadataAccessor = "$sMa",
        };
        Assert.False(WrapperValidation.HasIncompatibleFields(record));
    }

    #endregion

    #region AbiSizeLimits Tests

    [Fact]
    public void AbiSizeLimits_MaxSelfSize_Is8()
    {
        Assert.Equal(8, WrapperValidation.AbiSizeLimits.MaxSelfSize);
    }

    [Fact]
    public void AbiSizeLimits_MaxParamSize_Is16()
    {
        Assert.Equal(16, WrapperValidation.AbiSizeLimits.MaxParamSize);
    }

    #endregion

    #region WrapperDecision Tests

    [Fact]
    public void DetermineMethodWrapperDecision_NoXCFramework_CannotWrap()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        // No AsyncLibraryName → no xcframework mode
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.Equal(WrapperDecision.CannotWrap, WrapperValidation.DetermineMethodWrapperDecision(env));
    }

    [Fact]
    public void DetermineMethodWrapperDecision_Constructor_CannotWrap()
    {
        // MethodWrapperEmitter.ShouldEmitWrapper rejects constructors
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var ctor = CreateConstructor("init", parentDecl, moduleDecl);
        var env = new MethodEnvironment(ctor, typeDb);

        Assert.Equal(WrapperDecision.CannotWrap, WrapperValidation.DetermineMethodWrapperDecision(env));
    }

    [Fact]
    public void DetermineConstructorWrapperDecision_NoXCFramework_CannotWrap()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var ctor = CreateConstructor("init", parentDecl, moduleDecl);
        var env = new MethodEnvironment(ctor, typeDb);

        Assert.Equal(WrapperDecision.CannotWrap, WrapperValidation.DetermineConstructorWrapperDecision(env));
    }

    [Fact]
    public void DetermineConstructorWrapperDecision_NonFinalClass_WrapperRequired()
    {
        // Non-final class constructors require @_cdecl (Tj dispatch thunks)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyClass");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);
        parentDecl.IsFinal = false;
        var ctor = CreateConstructor("init", parentDecl, moduleDecl);
        var env = new MethodEnvironment(ctor, typeDb);

        Assert.Equal(WrapperDecision.WrapperRequired, WrapperValidation.DetermineConstructorWrapperDecision(env));
    }

    [Fact]
    public void DetermineConstructorWrapperDecision_NonFrozenStruct_WrapperRequired()
    {
        // Non-frozen struct constructors require @_cdecl (SwiftIndirectResult + Mono JIT crash).
        // E.g., LottieColor(r:g:b:a:denominator:) — all primitive params, but non-frozen struct
        // constructor uses SwiftIndirectResult which Mono JIT can't handle with CallConvSwift.
        var (moduleDecl, typeDb) = CreateTestEnvironment("OpaquePoint");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateStructDecl("OpaquePoint", moduleDecl);
        parentDecl.IsFrozen = false;
        var ctor = CreateConstructor("init", parentDecl, moduleDecl);
        var env = new MethodEnvironment(ctor, typeDb);

        Assert.Equal(WrapperDecision.WrapperRequired, WrapperValidation.DetermineConstructorWrapperDecision(env));
    }

    [Fact]
    public void DetermineConstructorWrapperDecision_FailableNonFrozenStruct_CannotWrap()
    {
        // Failable non-frozen struct constructors can't be wrapped (VWT incompatibility).
        // ShouldEmitWrapper blocks them, so the decision is CannotWrap even though
        // RequiresCdeclForAbiSafety would return true.
        var (moduleDecl, typeDb) = CreateTestEnvironment("OpaquePoint");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateStructDecl("OpaquePoint", moduleDecl);
        parentDecl.IsFrozen = false;
        var ctor = CreateConstructor("init", parentDecl, moduleDecl);
        ctor.IsFailable = true;
        var env = new MethodEnvironment(ctor, typeDb);

        Assert.Equal(WrapperDecision.CannotWrap, WrapperValidation.DetermineConstructorWrapperDecision(env));
    }

    [Fact]
    public void DeterminePropertyWrapperDecision_NoXCFramework_CannotWrap()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var (propertyDecl, propEnv) = CreatePropertyAndEnv("value", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.Equal(WrapperDecision.CannotWrap, WrapperValidation.DeterminePropertyWrapperDecision(propertyDecl, propEnv));
    }

    [Fact]
    public void DeterminePropertyWrapperDecision_NonFinalClass_WrapperRequired()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyClass");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);
        parentDecl.IsFinal = false;
        var (propertyDecl, propEnv) = CreatePropertyAndEnv("value", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.Equal(WrapperDecision.WrapperRequired, WrapperValidation.DeterminePropertyWrapperDecision(propertyDecl, propEnv));
    }

    #endregion

    #region HasUnsupportedTypeSignature Tests

    [Fact]
    public void HasUnsupportedTypeSignature_MetatypeParam_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateStructDecl("MyType", moduleDecl);

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
                    Name = "", PrivateName = "",
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Any.Type"),
                    Name = "metatype", PrivateName = "metatype",
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = moduleDecl
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
        Assert.True(MethodWrapperEmitter.HasUnsupportedTypeSignature(env));
    }

    [Fact]
    public void HasUnsupportedTypeSignature_MetatypeReturn_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateStructDecl("MyType", moduleDecl);

        var method = new MethodDecl
        {
            Name = "getType",
            MangledName = "$s10TestModule_getType",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Any.Type"),
                    Name = "", PrivateName = "",
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = moduleDecl
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
        Assert.True(MethodWrapperEmitter.HasUnsupportedTypeSignature(env));
    }

    [Fact]
    public void HasUnsupportedTypeSignature_OpaqueReturn_ReturnsFalse()
    {
        // Opaque returns (some Protocol) are now supported — @_cdecl wrapper boxes into existential.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateStructDecl("MyType", moduleDecl);

        var opaqueReturn = new ProtocolListTypeSpec(
            new[] { new NamedTypeSpec("Hashable") })
        {
            IsOpaque = true
        };

        var method = new MethodDecl
        {
            Name = "getSome",
            MangledName = "$s10TestModule_getSome",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = opaqueReturn,
                    Name = "", PrivateName = "",
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = moduleDecl
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
        Assert.False(MethodWrapperEmitter.HasUnsupportedTypeSignature(env));
    }

    [Fact]
    public void HasUnsupportedTypeSignature_SimpleIntMethod_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateStructDecl("MyType", moduleDecl);

        // Simple method: void return, no params → no unsupported type signatures
        var method = CreateMethod("doWork", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);
        Assert.False(MethodWrapperEmitter.HasUnsupportedTypeSignature(env));
    }

    [Fact]
    public void HasUnsupportedTypeSignature_DynamicSelfOnStruct_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyStruct");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);

        var method = new MethodDecl
        {
            Name = "copy",
            MangledName = "$s10TestModule_copy",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Self"),
                    Name = "", PrivateName = "",
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = moduleDecl
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
        Assert.True(MethodWrapperEmitter.HasUnsupportedTypeSignature(env));
    }

    [Fact]
    public void HasUnsupportedTypeSignature_DynamicSelfOnClass_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyClass");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyClass", moduleDecl);

        var method = new MethodDecl
        {
            Name = "copy",
            MangledName = "$s10TestModule_copy",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Self"),
                    Name = "", PrivateName = "",
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = moduleDecl
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
        Assert.False(MethodWrapperEmitter.HasUnsupportedTypeSignature(env));
    }

    #endregion
}
