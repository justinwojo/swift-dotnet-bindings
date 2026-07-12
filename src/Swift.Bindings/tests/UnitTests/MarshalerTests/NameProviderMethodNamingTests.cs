// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for NameProvider.GetPublicMethodName() — verb prefix, async prefix stripping,
/// and double-async prevention.
/// </summary>
public class NameProviderMethodNamingTests
{
    #region Noun-only → Get prefix (sync and async, consistent shape)

    [Fact]
    public void NounOnly_Async_WithReturn_GetsGetPrefix()
    {
        // Async noun-only zero-arg getters get the same Get prefix as sync (GetDataAsync),
        // for a consistent Get*Async shape rather than a bare WeatherAsync/DataAsync.
        var result = NameProvider.GetPublicMethodName("data", isAsync: true, hasReturnValue: true);
        Assert.Equal("GetDataAsync", result);
    }

    [Fact]
    public void NounOnly_Image_Async_WithReturn_GetsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("image", isAsync: true, hasReturnValue: true);
        Assert.Equal("GetImageAsync", result);
    }

    [Fact]
    public void NounOnly_Response_Async_WithReturn_GetsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("response", isAsync: true, hasReturnValue: true);
        Assert.Equal("GetResponseAsync", result);
    }

    [Fact]
    public void NounOnly_Sync_WithReturn_GetsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("count", isAsync: false, hasReturnValue: true);
        Assert.Equal("GetCount", result);
    }

    #endregion

    #region Double async stripping

    [Fact]
    public void AsyncPrefix_CamelCase_Stripped()
    {
        var result = NameProvider.GetPublicMethodName("asyncGetString", isAsync: true, hasReturnValue: true);
        Assert.Equal("GetStringAsync", result);
    }

    [Fact]
    public void AsyncPrefix_PascalCase_Stripped_GetsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("AsyncStaticString", isAsync: true, hasReturnValue: true);
        Assert.Equal("GetStaticStringAsync", result);
    }

    [Fact]
    public void AsyncPrefix_WithReturnValue_NounGetsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("asyncData", isAsync: true, hasReturnValue: true);
        Assert.Equal("GetDataAsync", result);
    }

    [Fact]
    public void AsyncPrefix_NotStripped_WhenNotAsync()
    {
        // A sync property/method named "asyncInstance" should keep the prefix.
        // Without this gate, property getter naming breaks: asyncInstance_Get → Instance_Get (collision).
        var result = NameProvider.GetPublicMethodName("asyncInstance", isAsync: false, hasReturnValue: true);
        Assert.Equal("GetAsyncInstance", result);
    }

    [Fact]
    public void AsyncPrefix_StillStripped_WhenAsync_GetsGetPrefix()
    {
        // Async methods should still have the prefix stripped per .NET convention.
        // After stripping, a noun-only zero-arg getter gets the Get prefix just like sync.
        var result = NameProvider.GetPublicMethodName("asyncInstance", isAsync: true, hasReturnValue: true);
        Assert.Equal("GetInstanceAsync", result);
    }

    #endregion

    #region Verb already present (no change)

    [Fact]
    public void VerbPrefix_LoadImage_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("loadImage", isAsync: true, hasReturnValue: true);
        Assert.Equal("LoadImageAsync", result);
    }

    [Fact]
    public void VerbPrefix_RemoveAll_Sync_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("removeAll", isAsync: false, hasReturnValue: false);
        Assert.Equal("RemoveAll", result);
    }

    [Fact]
    public void VerbPrefix_CreateImage_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("createImage", isAsync: false, hasReturnValue: true);
        Assert.Equal("CreateImage", result);
    }

    [Fact]
    public void VerbPrefix_IsValid_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("isValid", isAsync: false, hasReturnValue: true);
        Assert.Equal("IsValid", result);
    }

    [Fact]
    public void VerbPrefix_HasData_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("hasData", isAsync: false, hasReturnValue: true);
        Assert.Equal("HasData", result);
    }

    [Fact]
    public void VerbPrefix_RefreshTitle_Async_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("refreshTitle", isAsync: true, hasReturnValue: true);
        Assert.Equal("RefreshTitleAsync", result);
    }

    [Fact]
    public void AcceptsPrefix_RecognizedAsVerb()
    {
        var result = NameProvider.GetPublicMethodName("acceptsParameters", isAsync: false, hasReturnValue: true);
        Assert.Equal("AcceptsParameters", result);
    }

    [Fact]
    public void SumPrefix_RecognizedAsVerb()
    {
        var result = NameProvider.GetPublicMethodName("sum", isAsync: false, hasReturnValue: true);
        Assert.Equal("Sum", result);
    }

    [Fact]
    public void PassPrefix_RecognizedAsVerb()
    {
        var result = NameProvider.GetPublicMethodName("passThrough", isAsync: false, hasReturnValue: true);
        Assert.Equal("PassThrough", result);
    }

    #endregion

    #region Void return (no Get)

    [Fact]
    public void VoidReturn_NounOnly_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("flush", isAsync: false, hasReturnValue: false);
        Assert.Equal("Flush", result);
    }

    [Fact]
    public void VoidReturn_Count_NoGetAdded()
    {
        var result = NameProvider.GetPublicMethodName("count", isAsync: false, hasReturnValue: false);
        Assert.Equal("Count", result);
    }

    #endregion

    #region Property collision + verb prefix

    [Fact]
    public void PropertyCollision_WithReturn_GetsGetPrefixAndMethodSuffix()
    {
        var props = new HashSet<string> { "Data" };
        // "data" with return → "GetData" which doesn't collide with "Data" property
        var result = NameProvider.GetPublicMethodName("data", isAsync: false, hasReturnValue: true, props);
        Assert.Equal("GetData", result);
    }

    [Fact]
    public void PropertyCollision_WithVerb_MethodSuffix()
    {
        var props = new HashSet<string> { "GetData" };
        // "getData" → "GetData" → collides with property → "GetDataMethod"
        var result = NameProvider.GetPublicMethodName("getData", isAsync: false, hasReturnValue: true, props);
        Assert.Equal("GetDataMethod", result);
    }

    [Fact]
    public void PropertyCollision_Async_GetsGetPrefix_NotMethodSuffix()
    {
        // A zero-arg async getter ("status() async") colliding with a "status" property
        // resolves via the Get prefix (GetStatusAsync), NOT the StatusMethodAsync infix —
        // the Get prefix is applied before the property-collision check.
        var props = new HashSet<string> { "Status" };
        var result = NameProvider.GetPublicMethodName("status", isAsync: true, hasReturnValue: true, props);
        Assert.Equal("GetStatusAsync", result);
    }

    #endregion

    #region Async getter Get*Async shape (consistency with sync getters)

    [Fact]
    public void Weather_ZeroArg_Async_GetsGetPrefix()
    {
        // The headline case: a zero-arg async getter reads GetWeatherAsync, not WeatherAsync.
        var result = NameProvider.GetPublicMethodName("weather", isAsync: true, hasReturnValue: true, parameterCount: 0);
        Assert.Equal("GetWeatherAsync", result);
    }

    [Fact]
    public void Weather_WithParam_Async_SkipsGetPrefix()
    {
        // A parameterized async call is not a getter — no Get prefix.
        var result = NameProvider.GetPublicMethodName("weather", isAsync: true, hasReturnValue: true, parameterCount: 1);
        Assert.Equal("WeatherAsync", result);
    }

    [Fact]
    public void Async_VerbName_Unchanged_NoDoubleGet()
    {
        // An async verb name keeps its verb and is not double-prefixed.
        var result = NameProvider.GetPublicMethodName("loadImage", isAsync: true, hasReturnValue: true, parameterCount: 0);
        Assert.Equal("LoadImageAsync", result);
    }

    #endregion

    #region hasReturnValue = false by default (backward compatibility)

    [Fact]
    public void DefaultHasReturnValue_IsFalse_NoGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("data", isAsync: false);
        Assert.Equal("Data", result);
    }

    #endregion

    #region Self-returning methods (suppress Get prefix)

    [Fact]
    public void SelfReturning_NounOnly_SkipsGetPrefix()
    {
        // "equalTo" returning Self → "EqualTo", not "GetEqualTo"
        var result = NameProvider.GetPublicMethodName("equalTo", isAsync: false, hasReturnValue: true, isSelfReturning: true);
        Assert.Equal("EqualTo", result);
    }

    [Fact]
    public void SelfReturning_Accessibility_SkipsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("accessibility", isAsync: false, hasReturnValue: true, isSelfReturning: true);
        Assert.Equal("Accessibility", result);
    }

    [Fact]
    public void SelfReturning_TargetCache_SkipsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("targetCache", isAsync: false, hasReturnValue: true, isSelfReturning: true);
        Assert.Equal("TargetCache", result);
    }

    [Fact]
    public void SelfReturning_WithVerb_KeepsVerb()
    {
        // Self-returning with existing verb: leave it alone
        var result = NameProvider.GetPublicMethodName("makeConstraints", isAsync: false, hasReturnValue: true, isSelfReturning: true);
        Assert.Equal("MakeConstraints", result);
    }

    [Fact]
    public void NotSelfReturning_NounOnly_GetsGetPrefix()
    {
        // Same method name but NOT self-returning → "Get" prefix applies
        var result = NameProvider.GetPublicMethodName("equalTo", isAsync: false, hasReturnValue: true, isSelfReturning: false);
        Assert.Equal("GetEqualTo", result);
    }

    #endregion

    #region IsSelfReturningMethod detection — fluent/builder chains (rule 2)

    [Fact]
    public void IsSelfReturningMethod_SameModuleSiblingReturn_WithFluentMember_IsFluent()
    {
        // SnapKit's `equalToSuperview()` on ConstraintMakerRelatable returns a *sibling* builder
        // type in the same module (ConstraintMakerEditable) that ITSELF chains further (its own
        // methods return same-module builder types). It's a builder continuation, not a getter —
        // detected as self-returning so the noun→Get policy does not fire.
        var editable = BuildTypeWithMemberReturns(
            "SnapKit", "ConstraintMakerEditable",
            new NamedTypeSpec("SnapKit.ConstraintMakerRelatable")); // chains onward → builder family
        var method = BuildInstanceMethodReturning(
            "SnapKit", "ConstraintMakerRelatable",
            new NamedTypeSpec("SnapKit.ConstraintMakerEditable"),
            registeredReturnTypes: new[] { editable });
        Assert.True(MethodEnvironment.IsSelfReturningMethod(method));
    }

    [Fact]
    public void IsSelfReturningMethod_SameModuleDomainObjectReturn_NoFluentMember_IsNotFluent()
    {
        // Regression: `currentCollidable() -> BoundCollidable` and `vendBoxable() -> any Boxable`
        // are vending/getter methods. Their return type is a same-module *domain object* whose
        // own members return primitives (or it has no methods) — NOT a builder continuation. It
        // must keep its Get prefix (GetCurrentCollidable / GetVendBoxable), not be treated as
        // fluent. A same-module return alone is insufficient: the return type must itself be part
        // of a builder family.
        var domainObject = BuildTypeWithMemberReturns(
            "AppMod", "Collidable",
            new NamedTypeSpec("Swift.Int")); // member returns a primitive → not a chain
        var method = BuildInstanceMethodReturning(
            "AppMod", "RecognizerVendor",
            new NamedTypeSpec("AppMod.Collidable"),
            registeredReturnTypes: new[] { domainObject });
        Assert.False(MethodEnvironment.IsSelfReturningMethod(method));
    }

    [Fact]
    public void IsSelfReturningMethod_SameModuleReturn_TypeWithNoMethods_IsNotFluent()
    {
        // A same-module return whose type has no methods at all (a plain data protocol like
        // BoundCollidable, only a `collisionLabel` property) is a getter target, not a builder.
        var dataProtocol = BuildTypeWithMemberReturns("AppMod", "BoundCollidable");
        var method = BuildInstanceMethodReturning(
            "AppMod", "RecognizerVendor",
            new NamedTypeSpec("AppMod.BoundCollidable"),
            registeredReturnTypes: new[] { dataProtocol });
        Assert.False(MethodEnvironment.IsSelfReturningMethod(method));
    }

    [Fact]
    public void IsSelfReturningMethod_SameModuleReturn_UnresolvableType_IsNotFluent()
    {
        // If the same-module return type cannot be resolved to a declaration, the builder-family
        // test cannot confirm a chain — default to NOT fluent (keep Get), the conservative choice.
        var method = BuildInstanceMethodReturning(
            "AppMod", "RecognizerVendor",
            new NamedTypeSpec("AppMod.Unregistered"));
        Assert.False(MethodEnvironment.IsSelfReturningMethod(method));
    }

    [Fact]
    public void IsSelfReturningMethod_ExactParentReturn_IsFluent()
    {
        // Regression: the pre-existing exact-parent self-return still counts as fluent.
        var method = BuildInstanceMethodReturning(
            "SnapKit", "ConstraintMaker",
            new NamedTypeSpec("SnapKit.ConstraintMaker"));
        Assert.True(MethodEnvironment.IsSelfReturningMethod(method));
    }

    [Fact]
    public void IsSelfReturningMethod_ForeignModuleValueReturn_IsNotFluent()
    {
        // `count() -> Swift.Int` returns a foreign-module value type → still a getter (keeps Get).
        var method = BuildInstanceMethodReturning(
            "SnapKit", "ConstraintMakerRelatable",
            new NamedTypeSpec("Swift.Int"));
        Assert.False(MethodEnvironment.IsSelfReturningMethod(method));
    }

    [Fact]
    public void IsSelfReturningMethod_UnqualifiedGenericParamReturn_IsNotFluent()
    {
        // A method returning its own generic parameter (`-> T`, unqualified) is not a
        // module-qualified nominal — FromTypeSpec throws and it is treated as non-fluent.
        var method = BuildInstanceMethodReturning(
            "SnapKit", "Builder",
            new NamedTypeSpec("T"));
        Assert.False(MethodEnvironment.IsSelfReturningMethod(method));
    }

    [Fact]
    public void IsSelfReturningMethod_SameModuleGenericContainerReturn_IsNotFluent()
    {
        // `boxedHandler() -> Box<Handler>` returns a same-module *generic* container — a
        // value-producing getter, not a builder continuation. Generic returns are excluded so
        // the noun→Get policy still fires (GetBoxedHandler), unlike SnapKit's non-generic sibling.
        var box = new NamedTypeSpec("TestModule.Box");
        box.GenericParameters.Add(new NamedTypeSpec("TestModule.Handler"));
        var method = BuildInstanceMethodReturning("TestModule", "Loader", box);
        Assert.False(MethodEnvironment.IsSelfReturningMethod(method));
    }

    [Fact]
    public void GetPublicMethodName_FluentSiblingReturn_CollidingWithProperty_UsesWithPrefix()
    {
        // Collision-safety fixture: a fluent zero-arg method whose bare name (EqualToSuperview)
        // collides with a same-type property of the same name. Self-returning routes the collision
        // to the builder `With…` prefix — never a `Get…` prefix and never a CS0102/CS0111 dup.
        var props = new HashSet<string> { "EqualToSuperview" };
        var result = NameProvider.GetPublicMethodName(
            "equalToSuperview", isAsync: false, hasReturnValue: true,
            propertyNames: props, isSelfReturning: true, parameterCount: 0);
        Assert.Equal("WithEqualToSuperview", result);
    }

    private static MethodDecl BuildInstanceMethodReturning(
        string parentModule, string parentName, TypeSpec returnType,
        IReadOnlyList<TypeDecl> registeredReturnTypes = null)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = parentModule,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = registeredReturnTypes is null ? new List<TypeDecl>() : new List<TypeDecl>(registeredReturnTypes),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        if (registeredReturnTypes is not null)
        {
            foreach (var t in registeredReturnTypes)
            {
                t.ModuleDecl = moduleDecl;
            }
        }
        var parentDecl = new StructDecl
        {
            Name = parentName,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{parentModule}.{parentName}"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            IsFrozen = true,
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
        };
        return new MethodDecl
        {
            Name = "equalToSuperview",
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            MangledName = "$sM",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = returnType,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl,
                }
            },
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
        };
    }

    /// <summary>
    /// Builds a same-module type declaration whose instance methods return the given specs.
    /// Register it via <see cref="BuildInstanceMethodReturning"/>'s <c>registeredReturnTypes</c>
    /// to control whether the builder-family test sees a chain continuation. Passing no member
    /// returns produces a type with no methods (a plain data type / getter target).
    /// </summary>
    private static TypeDecl BuildTypeWithMemberReturns(
        string module, string typeName, params TypeSpec[] memberReturns)
    {
        var typeDecl = new StructDecl
        {
            Name = typeName,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{typeName}"),
            MangledName = $"$s{typeName}",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            IsFrozen = true,
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
        };
        int i = 0;
        foreach (var memberReturn in memberReturns)
        {
            typeDecl.Methods.Add(new MethodDecl
            {
                Name = $"member{i++}",
                ParentDecl = typeDecl,
                ModuleDecl = null,
                MangledName = $"$sMember{i}",
                MethodType = MethodType.Instance,
                IsConstructor = false,
                CSSignature = new List<ArgumentDecl>
                {
                    new()
                    {
                        SwiftTypeSpec = memberReturn,
                        Name = "",
                        PrivateName = "",
                        IsInOut = false,
                        IsGeneric = false,
                        ParentDecl = typeDecl,
                        ModuleDecl = null,
                    }
                },
                Throws = false,
                IsAsync = false,
                GenericParameters = new List<GenericArgumentDecl>(),
                IsSynthesizedAccessor = false,
            });
        }
        return typeDecl;
    }

    #endregion

    #region parameterCount-aware Get prefix

    [Fact]
    public void NounOnly_WithParams_SkipsGetPrefix()
    {
        // "equalTo(view)" with 1 param → "EqualTo", not "GetEqualTo"
        var result = NameProvider.GetPublicMethodName("equalTo", isAsync: false, hasReturnValue: true, parameterCount: 1);
        Assert.Equal("EqualTo", result);
    }

    [Fact]
    public void NounOnly_ZeroParams_GetsGetPrefix()
    {
        // "count()" with 0 params → "GetCount"
        var result = NameProvider.GetPublicMethodName("count", isAsync: false, hasReturnValue: true, parameterCount: 0);
        Assert.Equal("GetCount", result);
    }

    [Fact]
    public void NounOnly_MultipleParams_SkipsGetPrefix()
    {
        // "offset(dx, dy)" with 2 params → "Offset", not "GetOffset"
        var result = NameProvider.GetPublicMethodName("offset", isAsync: false, hasReturnValue: true, parameterCount: 2);
        Assert.Equal("Offset", result);
    }

    #endregion

    #region Mutating methods are not getters (skip the Get prefix)

    [Fact]
    public void Mutating_Async_NounGetter_StaysBareAsync_NoGetPrefix()
    {
        // A mutating method advances/changes state — it is not a getter. AsyncIteratorProtocol.next()
        // must stay NextAsync (the async-sequence bridge dispatches the iterator advance via that fixed
        // name), not GetNextAsync.
        var result = NameProvider.GetPublicMethodName("next", isAsync: true, hasReturnValue: true, parameterCount: 0, isMutating: true);
        Assert.Equal("NextAsync", result);
    }

    [Fact]
    public void Mutating_Sync_NounGetter_SkipsGetPrefix()
    {
        var result = NameProvider.GetPublicMethodName("next", isAsync: false, hasReturnValue: true, parameterCount: 0, isMutating: true);
        Assert.Equal("Next", result);
    }

    [Fact]
    public void NonMutating_Async_NounGetter_StillGetsGetPrefix()
    {
        // Control: a non-mutating async getter still gets the Get prefix.
        var result = NameProvider.GetPublicMethodName("weather", isAsync: true, hasReturnValue: true, parameterCount: 0, isMutating: false);
        Assert.Equal("GetWeatherAsync", result);
    }

    #endregion
}
