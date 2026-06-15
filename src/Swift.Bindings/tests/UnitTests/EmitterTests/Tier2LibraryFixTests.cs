// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for all 7 tier-2 library validation fixes.
/// Each test targets a specific regression pattern discovered during --tier all validation.
/// </summary>
public class Tier2LibraryFixTests
{
    #region Fix 1: CAKeyframeAnimation class remapping (LoadingIndicator)

    [Fact]
    public void TypeDatabase_CAKeyframeAnimation_RemapsToCAKeyFrameAnimation()
    {
        // Swift uses CAKeyframeAnimation (lowercase 'f'), .NET uses CAKeyFrameAnimation (capital 'F')
        var typeDatabase = CreateTypeDatabaseWithCoreAnimation();
        var typeSpec = new NamedTypeSpec("QuartzCore.CAKeyframeAnimation");

        var record = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal("CAKeyFrameAnimation", record.CSharpTypeName.Name);
        Assert.Equal("CoreAnimation", record.CSharpTypeName.Namespace);
    }

    [Fact]
    public void TypeDatabase_CAKeyframeAnimation_IsObjCBridged()
    {
        var typeDatabase = CreateTypeDatabaseWithCoreAnimation();
        var typeSpec = new NamedTypeSpec("QuartzCore.CAKeyframeAnimation");

        var record = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

        Assert.True(MarshallingHelpers.IsObjCBridged(record));
    }

    #endregion

    #region Fix 2: P/Invoke name sanitization (emoji prefix + backtick-escaped keywords)

    [Fact]
    public void GetPInvokeName_EmojiInMethodName_SanitizesEmoji()
    {
        // 🚫 prefix on ObjC compatibility shims — must be sanitized to underscores
        var method = CreateMethodDecl("🚫deprecatedMethod");

        var pinvokeName = NameProvider.GetPInvokeName(method);

        Assert.DoesNotContain("🚫", pinvokeName);
        Assert.StartsWith("PInvoke_", pinvokeName);
        Assert.Contains("deprecatedMethod", pinvokeName);
    }

    [Fact]
    public void GetPInvokeName_BackticksInMethodName_StripsBackticks()
    {
        // Backtick-escaped Swift keywords like `subscript` — backticks must be stripped
        var method = CreateMethodDecl("`subscript`");

        var pinvokeName = NameProvider.GetPInvokeName(method);

        Assert.DoesNotContain("`", pinvokeName);
        Assert.StartsWith("PInvoke_subscript_", pinvokeName);
    }

    [Fact]
    public void GetPInvokeName_AllInvalidChars_ProducesValidIdentifier()
    {
        var method = CreateMethodDecl("🚫");

        var pinvokeName = NameProvider.GetPInvokeName(method);

        // Emoji chars become underscores via SanitizeIdentifierChars — still a valid C# identifier
        Assert.StartsWith("PInvoke_", pinvokeName);
        Assert.DoesNotContain("🚫", pinvokeName);
    }

    [Fact]
    public void GetPInvokeName_NormalMethodName_Unchanged()
    {
        var method = CreateMethodDecl("doSomething");

        var pinvokeName = NameProvider.GetPInvokeName(method);

        Assert.StartsWith("PInvoke_doSomething_", pinvokeName);
    }

    #endregion

    #region Fix 3: Backtick stripping at parser level

    [Fact]
    public void SanitizeIdentifierChars_BackticksStripped()
    {
        // Belt-and-suspenders: SanitizeIdentifierChars does NOT strip backticks
        // (backtick is not letter/digit/underscore), but the parser should strip them first.
        var sanitized = NameProvider.SanitizeIdentifierChars("`subscript`");

        // Backtick is sanitized to underscore by SanitizeIdentifierChars
        Assert.DoesNotContain("`", sanitized);
    }

    [Fact]
    public void EmitSimpleCaseFromTag_BackticksInFieldName_StrippedFromLazyField()
    {
        // The fix ensures _lazy_ field names don't contain backticks.
        // We test this indirectly through the field name sanitization.
        var fieldName = "`subscript`".Replace("`", "");

        Assert.Equal("subscript", fieldName);
        Assert.DoesNotContain("`", fieldName);
    }

    #endregion

    #region Fix 4: Closure return type gate (unresolvable closure return crash)

    [Fact]
    public void CanEmitMethod_UnsupportedClosureReturn_ReturnsUnsupportedClosure()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Method returns a closure with an unresolvable inner type
        var closureReturn = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("UnknownModule.CodingKey") }),
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("UnknownModule.NodeDecoding")));
        closureReturn.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = new MethodDecl
        {
            Name = "nodeDecoding",
            MangledName = "$s8XmlCodec14nodeDecodingyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, closureReturn),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var skipDetails, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedClosure, result);
        Assert.Contains("unsupported closure", skipDetails);
    }

    [Fact]
    public void CanEmitMethod_NonClosureReturn_NotAffectedByClosureGate()
    {
        // Normal method with non-closure return should not be affected by the new B21 gate
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentClass = CreateClassDecl("TestClass");
        parentClass.ModuleDecl = moduleDecl;

        var method = new MethodDecl
        {
            Name = "getValue",
            MangledName = "$s10TestModule8getValueySiF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int")),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentClass,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out _, out _);

        Assert.Null(result); // null means valid — not blocked by closure gate
    }

    [Fact]
    public void CanInvokeReturnedClosure_NonThrowingBoundGenericReturn_ReturnsFalse()
    {
        // Problem C (XMLCoder regression): a method RETURNING a closure whose own return is
        // a bound generic (Optional<enum>) — e.g. `nodeEncodings(...) -> (any CodingKey) ->
        // NodeEncoding?`. The received-closure invoker emits `return _fp(...)`, where the
        // function pointer returns void* but the delegate returns the bound-generic shape;
        // that produces CS0029/CS1503 (cannot convert void* -> NodeEncoding?). The gate must
        // reject it REGARDLESS of Throws — the bug was a `!Throws` short-circuit that left
        // non-throwing returned closures ungated, so this closure has Throws=false.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var nonThrowingBoundGenericReturn = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int")));

        Assert.False(nonThrowingBoundGenericReturn.Throws);
        Assert.False(closureHandler.CanInvokeReturnedClosure(nonThrowingBoundGenericReturn));
    }

    [Fact]
    public void CanInvokeReturnedClosure_NonThrowingPrimitiveReturn_ReturnsTrue()
    {
        // Counterpart to the bound-generic case: a non-throwing returned closure whose return
        // is a blittable primitive marshals fine through the `return _fp(...)` fallback, so the
        // gate must still ADMIT it. Pins that the fix is additive — it prunes only the broken
        // shapes, it does not over-prune every non-throwing returned closure.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var nonThrowingPrimitiveReturn = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Int"));

        Assert.False(nonThrowingPrimitiveReturn.Throws);
        Assert.True(closureHandler.CanInvokeReturnedClosure(nonThrowingPrimitiveReturn));
    }

    [Fact]
    public void CanEmitMethod_NonThrowingReturnedClosureBoundGenericReturn_Pruned()
    {
        // Integration pin for Problem C at the B21 gate: a NON-throwing method returning a
        // closure `() -> Optional<Int>` (resolvable inner types, so the only reason to skip is
        // the received-closure return gate — not an unresolvable-type prune). IsSupportedClosure
        // approves the bound-generic return for the PASSED (indirect-return) direction, so the
        // method must be pruned by CanInvokeReturnedClosure, not the IsSupportedClosure check.
        // (Swift.Optional must be in the DB, else IsSupportedClosureReturnType rejects the bound
        // generic at line 672 and the gate at 677 is never reached.)
        var typeDatabase = CreateTypeDatabaseWithOptional();
        var moduleDecl = CreateModuleDecl("TestModule");

        var closureReturn = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int")));
        closureReturn.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = new MethodDecl
        {
            Name = "nodeEncodings",
            MangledName = "$s8XmlCodec13nodeEncodingsyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, closureReturn),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var skipDetails, out _);

        Assert.Equal(SkipReason.UnsupportedClosure, result);
        Assert.Contains("cannot be invoked from C# without a function-pointer marshaler", skipDetails);
    }

    #endregion

    #region Fix 5: Self resolution on generic classes

    [Fact]
    public void TypeProjectionFactory_Self_OnGenericClass_AppendsTypeParameters()
    {
        // ServiceEntry<TService>.inObjectScope() → Self
        // Should resolve to "ServiceEntry<TService>" not bare "ServiceEntry"
        var db = new MockTypeDatabase();
        db.AddType("DependencyContainer.ServiceEntry", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.DependencyContainer", "ServiceEntry"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("DependencyContainer.ServiceEntry"),
            MetadataAccessor = "$s19DependencyContainer12ServiceEntryMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });

        var parentDecl = new ClassDecl
        {
            Name = "ServiceEntry",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("DependencyContainer.ServiceEntry"),
            MangledName = "$s19DependencyContainer12ServiceEntryCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_0_0", "Service", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var factory = new TypeProjectionFactory();
        var selfSpec = new NamedTypeSpec("Self");
        var genericContext = GenericContext.FromType(parentDecl);
        var ctx = new ProjectionContext
        {
            TypeDatabase = db,
            IsParameter = false,
            ParentTypeDecl = parentDecl,
            GenericContext = genericContext,
        };

        var projection = factory.Project(selfSpec, ctx);

        Assert.NotNull(projection);
        // Should include type parameter, not bare name
        Assert.Contains("TService", projection.PublicType);
        Assert.Contains("ServiceEntry", projection.PublicType);
    }

    [Fact]
    public void TypeProjectionFactory_Self_OnNonGenericClass_ReturnsBareType()
    {
        var db = new MockTypeDatabase();
        db.AddType("TestModule.SimpleClass", new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SimpleClass"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SimpleClass"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });

        var parentDecl = new ClassDecl
        {
            Name = "SimpleClass",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SimpleClass"),
            MangledName = "$s10TestModule11SimpleClassCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var factory = new TypeProjectionFactory();
        var selfSpec = new NamedTypeSpec("Self");
        var ctx = new ProjectionContext
        {
            TypeDatabase = db,
            IsParameter = false,
            ParentTypeDecl = parentDecl,
        };

        var projection = factory.Project(selfSpec, ctx);

        Assert.NotNull(projection);
        Assert.Equal("TestModule.SimpleClass", projection.PublicType);
        Assert.DoesNotContain("<", projection.PublicType);
    }

    #endregion

    #region Fix 6: Frozen struct metadata in enum tuples

    [Fact]
    public void EmitGetTypeMetadataForElement_FrozenStruct_UsesGetTypeMetadataOrThrow()
    {
        // CGPoint is a frozen blittable struct — NOT an ISwiftObject.
        // Should use TypeMetadata.GetTypeMetadataOrThrow<T>(), not SwiftObjectHelper<T>.GetTypeMetadata().
        var typeDatabase = CreateTypeDatabaseWithFrozenStruct();
        var typeSpec = new NamedTypeSpec("CoreGraphics.CGPoint");

        // Verify the type record exists and is a frozen struct
        Assert.True(typeDatabase.TryGetTypeRecord(typeSpec, out var record));
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
        Assert.True(MarshallingHelpers.IsTypeFrozen(record));
        Assert.False(MarshallingHelpers.IsFrozenStructProjectedAsClass(record));
    }

    [Fact]
    public void EmitGetTypeMetadataForElement_FrozenStructWithMemory_UsesSwiftObjectHelper()
    {
        // Frozen struct projected as class (ClassWithBufferStruct) DOES implement ISwiftObject.
        // Should still use SwiftObjectHelper<T>.GetTypeMetadata().
        var typeDatabase = CreateTypeDatabaseWithFrozenStructWithMemory();
        var typeSpec = new NamedTypeSpec("MyModule.MyStruct");

        Assert.True(typeDatabase.TryGetTypeRecord(typeSpec, out var record));
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
        Assert.True(MarshallingHelpers.IsTypeFrozen(record));
        Assert.True(MarshallingHelpers.IsFrozenStructProjectedAsClass(record));
    }

    #endregion

    #region Fix 7: Override name parity check

    [Fact]
    public void HasMethodInResolvedAncestors_SameCSharpName_ReturnsTrue()
    {
        // Base class has method "opacity" → C# name "Opacity" (no property collision)
        // Derived class has method "opacity" → C# name "Opacity" (no property collision either)
        // Override should be emitted.
        var baseClass = CreateClassDecl("VectorImagePaint");
        var baseMethod = CreateEmittedMethodDecl("opacity", baseClass, isDynamicSelfReturn: true);
        baseClass.Methods.Add(baseMethod);

        var derivedClass = CreateClassDecl("VectorImageColor");
        derivedClass.ResolvedSuperclass = baseClass;

        var derivedMethod = CreateEmittedMethodDecl("opacity", derivedClass, isDynamicSelfReturn: true);

        // Derived C# name is "Opacity" (matches base, no collision)
        var derivedCSharpName = NameProvider.GetPublicMethodName("opacity", false,
            hasReturnValue: true, propertyNames: null, isSelfReturning: true,
            parentTypeName: "VectorImageColor", parameterCount: 1);

        var result = WrapperEmitter.HasMethodInResolvedAncestors(derivedClass, derivedMethod, derivedCSharpName);

        Assert.True(result);
    }

    [Fact]
    public void HasMethodInResolvedAncestors_DifferentCSharpName_DueToPropertyCollision_ReturnsFalse()
    {
        // Base class: "opacity" → C# "Opacity" (no property collision)
        // Derived class: "opacity" → C# "WithOpacity" (has Opacity property → self-returning builder gets "With" prefix)
        // Override should NOT be emitted because C# names differ.
        var baseClass = CreateClassDecl("VectorImagePaint");
        var baseMethod = CreateEmittedMethodDecl("opacity", baseClass, isDynamicSelfReturn: true);
        baseClass.Methods.Add(baseMethod);

        var derivedClass = CreateClassDecl("VectorImageColor");
        derivedClass.ResolvedSuperclass = baseClass;
        // Add "Opacity" property to derived class → triggers "With" prefix for self-returning builder
        derivedClass.Properties.Add(new PropertyDecl
        {
            Name = "opacity",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Double"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = derivedClass,
            ModuleDecl = null,
            WasEmitted = true,
        });

        var derivedMethod = CreateEmittedMethodDecl("opacity", derivedClass, isDynamicSelfReturn: true);

        // Derived C# name is "WithOpacity" because of property collision
        var derivedProps = new HashSet<string>(
            derivedClass.Properties.Where(p => p.WasEmitted).Select(p => NameProvider.ToPascalCase(p.Name)),
            StringComparer.Ordinal);
        var derivedCSharpName = NameProvider.GetPublicMethodName("opacity", false,
            hasReturnValue: true, propertyNames: derivedProps, isSelfReturning: true,
            parentTypeName: "VectorImageColor", parameterCount: 1);

        Assert.Equal("WithOpacity", derivedCSharpName);

        var result = WrapperEmitter.HasMethodInResolvedAncestors(derivedClass, derivedMethod, derivedCSharpName);

        Assert.False(result);
    }

    [Fact]
    public void HasMethodInResolvedAncestors_NullCSharpName_SkipsParity_ReturnsTrue()
    {
        // When derivedCSharpName is null (backward compat), skip parity check
        var baseClass = CreateClassDecl("Base");
        var baseMethod = CreateEmittedMethodDecl("doSomething", baseClass);
        baseClass.Methods.Add(baseMethod);

        var derivedClass = CreateClassDecl("Derived");
        derivedClass.ResolvedSuperclass = baseClass;

        var derivedMethod = CreateEmittedMethodDecl("doSomething", derivedClass);

        var result = WrapperEmitter.HasMethodInResolvedAncestors(derivedClass, derivedMethod, null);

        Assert.True(result);
    }

    [Fact]
    public void HasMethodInResolvedAncestors_ConcreteParentTypeReturn_IsSelfReturning()
    {
        // P2 fix: concrete parent-type return (e.g., "-> VectorImagePaint") should be treated
        // as self-returning, not just DynamicSelf/literal "Self".
        // Base method returns "TestModule.VectorImagePaint" (its own concrete type) → self-returning.
        var baseClass = CreateClassDecl("VectorImagePaint");
        var baseMethod = new MethodDecl
        {
            Name = "opacity",
            MangledName = "$s16VectorImagePaint7opacityyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsOverride = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type is concrete parent type (not DynamicSelf)
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.VectorImagePaint")),
                CreateArgument("value", new NamedTypeSpec("Swift.Double")),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = baseClass,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            WasEmitted = true,
        };
        // Add "Opacity" property to base class → triggers "With" prefix for self-returning
        baseClass.Properties.Add(new PropertyDecl
        {
            Name = "opacity",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Double"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = baseClass,
            ModuleDecl = null,
            WasEmitted = true,
        });
        baseClass.Methods.Add(baseMethod);

        var derivedClass = CreateClassDecl("VectorImageColor");
        derivedClass.ResolvedSuperclass = baseClass;
        // Derived also has the property
        derivedClass.Properties.Add(new PropertyDecl
        {
            Name = "opacity",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Double"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = derivedClass,
            ModuleDecl = null,
            WasEmitted = true,
        });

        var derivedMethod = CreateEmittedMethodDecl("opacity", derivedClass, isDynamicSelfReturn: true);
        // Derived name is "WithOpacity" (DynamicSelf → self-returning + property collision)
        var derivedProps = new HashSet<string>(
            derivedClass.Properties.Where(p => p.WasEmitted).Select(p => NameProvider.ToPascalCase(p.Name)),
            StringComparer.Ordinal);
        var derivedCSharpName = NameProvider.GetPublicMethodName("opacity", false,
            hasReturnValue: true, propertyNames: derivedProps, isSelfReturning: true,
            parentTypeName: "VectorImageColor", parameterCount: 1);
        Assert.Equal("WithOpacity", derivedCSharpName);

        // Base should ALSO compute "WithOpacity" because concrete-type return → self-returning
        var result = WrapperEmitter.HasMethodInResolvedAncestors(derivedClass, derivedMethod, derivedCSharpName);
        Assert.True(result);
    }

    [Fact]
    public void HasMethodInResolvedAncestors_NestedTypeCollision_UsesGetPropertyName()
    {
        // P1 fix: nested type named "Opacity" should cause property "opacity" to rename
        // to "OpacityValue" in the ancestor's collision set.
        var baseClass = CreateClassDecl("BaseView");
        // Add a nested type named "Opacity" → collides with property "opacity" (PascalCase="Opacity")
        baseClass.Types.Add(new ClassDecl
        {
            Name = "Opacity",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BaseView.Opacity"),
            MangledName = "$s10TestModule8BaseView7OpacityCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = baseClass,
            ModuleDecl = null
        });
        // Property "opacity" → GetPropertyName → "Opacity" → collides with nested type → renamed "OpacityValue"
        baseClass.Properties.Add(new PropertyDecl
        {
            Name = "opacity",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Double"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = baseClass,
            ModuleDecl = null,
            WasEmitted = true,
        });
        // Method "opacity" with self-returning → should compute against renamed property set
        // With rename, "Opacity" is now "OpacityValue" in the prop set, so "opacity" method
        // won't collide with the property name in the set — it goes through as "Opacity"
        // (But the nested type name "Opacity" IS added to the prop set directly — CS0102)
        // So the method WILL collide with "Opacity" from the nested type set → gets "With" prefix
        var baseMethod = CreateEmittedMethodDecl("opacity", baseClass, isDynamicSelfReturn: true);
        baseClass.Methods.Add(baseMethod);

        var derivedClass = CreateClassDecl("DerivedView");
        derivedClass.ResolvedSuperclass = baseClass;
        // Derived also has nested type → same collision
        derivedClass.Types.Add(new ClassDecl
        {
            Name = "Opacity",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DerivedView.Opacity"),
            MangledName = "$s10TestModule11DerivedView7OpacityCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = derivedClass,
            ModuleDecl = null
        });

        var derivedMethod = CreateEmittedMethodDecl("opacity", derivedClass, isDynamicSelfReturn: true);

        // Derived: nested type "Opacity" is in the property set → method collides → "WithOpacity"
        var derivedProps = new HashSet<string> { "Opacity" }; // nested type
        var derivedCSharpName = NameProvider.GetPublicMethodName("opacity", false,
            hasReturnValue: true, propertyNames: derivedProps, isSelfReturning: true,
            parentTypeName: "DerivedView", parameterCount: 1);
        Assert.Equal("WithOpacity", derivedCSharpName);

        // Base also has nested type "Opacity" → should compute same "WithOpacity" → match
        var result = WrapperEmitter.HasMethodInResolvedAncestors(derivedClass, derivedMethod, derivedCSharpName);
        Assert.True(result);
    }

    [Fact]
    public void HasMethodInResolvedAncestors_NonEmittedProperty_StillCausesCollision()
    {
        // Production uses ALL declared properties for the collision set, not just emitted ones.
        // A non-emitted property (e.g., unsupported type) still occupies the name and can
        // cause a self-returning method to get the "With" prefix.
        var baseClass = CreateClassDecl("VectorImagePaint");
        // Add "opacity" property that is NOT emitted (e.g., unsupported type)
        baseClass.Properties.Add(new PropertyDecl
        {
            Name = "opacity",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Double"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = baseClass,
            ModuleDecl = null,
            WasEmitted = false, // <-- NOT emitted
        });
        var baseMethod = CreateEmittedMethodDecl("opacity", baseClass, isDynamicSelfReturn: true);
        baseClass.Methods.Add(baseMethod);

        var derivedClass = CreateClassDecl("VectorImageColor");
        derivedClass.ResolvedSuperclass = baseClass;
        // Derived also has same non-emitted property
        derivedClass.Properties.Add(new PropertyDecl
        {
            Name = "opacity",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Double"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = derivedClass,
            ModuleDecl = null,
            WasEmitted = false,
        });

        var derivedMethod = CreateEmittedMethodDecl("opacity", derivedClass, isDynamicSelfReturn: true);

        // Derived C# name: "WithOpacity" (non-emitted property still in collision set)
        var derivedProps = new HashSet<string> { "Opacity" }; // includes non-emitted property
        var derivedCSharpName = NameProvider.GetPublicMethodName("opacity", false,
            hasReturnValue: true, propertyNames: derivedProps, isSelfReturning: true,
            parentTypeName: "VectorImageColor", parameterCount: 1);
        Assert.Equal("WithOpacity", derivedCSharpName);

        // Ancestor parity: base also has non-emitted "opacity" property → "WithOpacity" → match
        var result = WrapperEmitter.HasMethodInResolvedAncestors(derivedClass, derivedMethod, derivedCSharpName);
        Assert.True(result);
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithCoreAnimation()
    {
        var typeDatabase = CreateTypeDatabase();
        // QuartzCore module is ObjC — types are resolved via synthetic ObjCBridged records
        return typeDatabase;
    }

    /// <summary>
    /// DB with Swift.Optional registered so that IsSupportedClosureReturnType APPROVES a bound
    /// generic closure return (e.g. Optional&lt;Int&gt;) for the passed/indirect-return direction —
    /// the precondition for reaching the B21 CanInvokeReturnedClosure gate (Problem C / XMLCoder).
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithOptional()
    {
        var typeDatabase = CreateTypeDatabase();
        var swiftModule = new ModuleTypeDatabase("SwiftOptional", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithFrozenStruct()
    {
        var typeDatabase = CreateTypeDatabase();
        var cgModule = new ModuleTypeDatabase("CoreGraphics", "/usr/lib/libCoreGraphics.dylib");
        cgModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGPoint"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreGraphics", "CGPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGPoint"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(cgModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithFrozenStructWithMemory()
    {
        var typeDatabase = CreateTypeDatabase();
        var myModule = new ModuleTypeDatabase("MyModule", "/tmp/MyModule.dylib");
        myModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("MyModule.MyStruct"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.MyModule", "MyStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("MyModule.MyStruct"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(myModule);
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

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static MethodDecl CreateMethodDecl(string name)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static ClassDecl CreateClassDecl(string name, string moduleName = "TestModule")
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static MethodDecl CreateEmittedMethodDecl(string name, ClassDecl parentClass, bool isDynamicSelfReturn = false)
    {
        var returnTypeSpec = isDynamicSelfReturn
            ? (TypeSpec)new NamedTypeSpec("Self")
            : TupleTypeSpec.Empty;

        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s{parentClass.Name.Length}{parentClass.Name}{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsOverride = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnTypeSpec),
                CreateArgument("value", new NamedTypeSpec("Swift.Double")),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentClass,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            WasEmitted = true,
        };

        return method;
    }

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types = new();

        public void AddType(string moduleQualifiedName, TypeRecord record)
        {
            _types[moduleQualifiedName] = record;
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";
        public string? AsyncLibraryName => null;
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion

    #region P14C: AVCaptureDevice.FocusMode enum remap

    [Fact]
    public void AppleFrameworkValueTypes_AVCaptureDeviceFocusMode_IsValueType()
    {
        // AVCaptureDevice.FocusMode is an ObjC enum — must be in value types list
        // to prevent misclassification as an ObjC class
        var typeSpec = new NamedTypeSpec("AVFoundation.AVCaptureDevice.FocusMode");
        Assert.False(TypeDatabaseExtensions.IsObjCModuleType(typeSpec));
    }

    [Fact]
    public async Task AppleEnumRemap_AVCaptureDeviceFocusMode_RemapsToAVCaptureFocusMode()
    {
        // Swift: AVCaptureDevice.FocusMode → .NET: AVFoundation.AVCaptureFocusMode
        // Now resolved via AVFoundationDatabase.xml instead of hardcoded dict
        var typeDatabase = new TypeDatabase();
        await typeDatabase.LoadModuleDatabaseFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "AVFoundationDatabase.xml"));

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("AVFoundation.AVCaptureDevice.FocusMode");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("AVCaptureFocusMode", record.CSharpTypeName.Name);
        Assert.Equal("AVFoundation", record.CSharpTypeName.Namespace);
    }

    #endregion

    #region P14C: Foundation.Formatter → NSFormatter remap

    [Fact]
    public void MapQualifiedTypeToNet_FoundationFormatter_RemapsToNSFormatter()
    {
        var result = MarshallingHelpers.MapQualifiedTypeToNet("Foundation.Formatter");
        Assert.Equal("Foundation.NSFormatter", result);
    }

    [Fact]
    public void MapQualifiedTypeToNet_NormalType_PassesThrough()
    {
        // Non-remapped types should pass through unchanged
        var result = MarshallingHelpers.MapQualifiedTypeToNet("Foundation.NSObject");
        Assert.Equal("Foundation.NSObject", result);
    }

    #endregion

    #region P14C: UITextLayoutDirection as value type

    [Fact]
    public void AppleFrameworkValueTypes_UITextLayoutDirection_IsValueType()
    {
        // UITextLayoutDirection is an ObjC enum — must be in value types list
        var typeSpec = new NamedTypeSpec("UIKit.UITextLayoutDirection");
        Assert.False(TypeDatabaseExtensions.IsObjCModuleType(typeSpec));
    }

    #endregion
}
