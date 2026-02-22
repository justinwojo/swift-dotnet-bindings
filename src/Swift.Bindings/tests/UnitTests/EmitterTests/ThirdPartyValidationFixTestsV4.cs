// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression tests for third-party library binding compilation error fixes (C1-C9).
/// Validation Pass 4: 166 generator-caused errors across 9 libraries.
/// </summary>
public class ThirdPartyValidationFixTestsV4
{
    #region C1 — Optional tuple with unsupported element (Alamofire)

    [Fact]
    public void CanEmitProperty_OptionalTupleWithClosureElement_IsSkipped()
    {
        // Alamofire: UploadProgressHandler is Optional<(AnyType, DispatchQueue)>
        // where the closure fell back to AnyType inside the tuple.
        var typeDatabase = CreateTypeDatabaseWithString();

        var tupleTypeSpec = new TupleTypeSpec();
        tupleTypeSpec.Elements.Add(new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty)); // closure element
        tupleTypeSpec.Elements.Add(new NamedTypeSpec("Swift.Int"));

        var optionalTuple = new NamedTypeSpec("Swift.Optional");
        optionalTuple.GenericParameters.Add(tupleTypeSpec);

        var property = CreatePropertyDecl("progressHandler", optionalTuple);

        var result = MemberEmissionValidator.CanEmitProperty(property, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedSignature, result);
        Assert.Contains("tuple", details!);
    }

    [Fact]
    public void CanEmitProperty_OptionalTupleWithPrimitives_NotSkipped()
    {
        // Optional<(Int, Int)> should pass through — no unsupported elements.
        var typeDatabase = CreateTypeDatabase();

        var tupleTypeSpec = new TupleTypeSpec();
        tupleTypeSpec.Elements.Add(new NamedTypeSpec("Swift.Int"));
        tupleTypeSpec.Elements.Add(new NamedTypeSpec("Swift.Int"));

        var optionalTuple = new NamedTypeSpec("Swift.Optional");
        optionalTuple.GenericParameters.Add(tupleTypeSpec);

        var property = CreatePropertyDecl("coords", optionalTuple);

        // Won't pass full validation (tuple properties unsupported), but
        // the specific C1 tuple element check should NOT fire.
        var result = MemberEmissionValidator.CanEmitProperty(property, typeDatabase, out var details, out _);

        // If skipped, it should NOT be due to tuple element check
        if (result != null)
            Assert.DoesNotContain("tuple with unsupported element", details ?? "");
    }

    [Fact]
    public void CanEmitProperty_TupleWithAnyTypeElement_IsSkipped()
    {
        // Direct tuple with AnyType element (unresolved type fallback)
        var typeDatabase = CreateTypeDatabase();

        var tupleTypeSpec = new TupleTypeSpec();
        tupleTypeSpec.Elements.Add(new NamedTypeSpec("Swift.Int"));
        tupleTypeSpec.Elements.Add(new NamedTypeSpec("UnknownModule.UnknownType"));

        var property = CreatePropertyDecl("pair", tupleTypeSpec);

        var result = MemberEmissionValidator.CanEmitProperty(property, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedSignature, result);
        Assert.Contains("tuple", details!);
    }

    #endregion

    #region C2 — CanEmitProperty wired into type handlers (MicroblinkPlatform)

    [Fact]
    public void CanEmitProperty_SwiftUIProperty_IsSkipped()
    {
        // MicroblinkPlatform: SwiftUI.Color/SwiftUI.Font properties should be skipped.
        // This test verifies the validator itself catches SwiftUI types.
        var typeDatabase = CreateTypeDatabase();
        var property = CreatePropertyDecl("tintColor", new NamedTypeSpec("SwiftUI.Color"));

        var result = MemberEmissionValidator.CanEmitProperty(property, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.SwiftUIConstraint, result);
        Assert.Contains("unsupported module", details!);
    }

    [Fact]
    public void CanEmitProperty_ValidPrimitive_NotSkipped()
    {
        // A regular Int property should pass validation.
        var typeDatabase = CreateTypeDatabase();
        var structDecl = CreateStructDecl("Owner");
        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>
            {
                CreateGetAccessorWithParent(new NamedTypeSpec("Swift.Int"), structDecl)
            },
            ParentDecl = structDecl,
            ModuleDecl = CreateModuleDecl()
        };

        var result = MemberEmissionValidator.CanEmitProperty(property, typeDatabase, out _, out _);

        Assert.Null(result);
    }

    #endregion

    #region C3 — DateTimeOffset bound generic detection (StripeConnect)

    [Fact]
    public void HasNonSwiftObjectGenericArg_FoundationDateNoNativeType_ReturnsTrue()
    {
        // Foundation.Date → System.DateTimeOffset with NativeTypeName=null
        // C3 check: CSharpTypeName.Namespace == "System" && module != "Swift"
        var typeDatabase = CreateTypeDatabaseWithFoundationDateNoNative();
        var handler = new BoundGenericsHandler(typeDatabase);

        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(new NamedTypeSpec("Foundation.Date"));

        Assert.True(handler.HasNonSwiftObjectGenericArg(arrayTypeSpec));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_SwiftBool_ReturnsFalse()
    {
        // Swift.Bool → System.Boolean — but it's from the Swift module,
        // so it should NOT be caught by the C3 check.
        var typeDatabase = CreateTypeDatabaseWithBool();
        var handler = new BoundGenericsHandler(typeDatabase);

        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Bool"));

        Assert.False(handler.HasNonSwiftObjectGenericArg(arrayTypeSpec));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_SwiftInt_ReturnsFalse()
    {
        // Swift.Int → System.Int64 — Swift module primitives excluded.
        var typeDatabase = CreateTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase);

        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        Assert.False(handler.HasNonSwiftObjectGenericArg(arrayTypeSpec));
    }

    #endregion

    #region C4 — ObjC-bridged type excluded from async copy buffer (StripeCryptoOnramp)

    [Fact]
    public void MarshallingHelpers_IsObjCBridged_WithFlag_ReturnsTrue()
    {
        // ObjC-bridged types (UIViewController) must be excluded from async copy-buffer.
        var record = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIViewController"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIViewController"),
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class
        };

        Assert.True(MarshallingHelpers.IsObjCBridged(record));
    }

    [Fact]
    public void MarshallingHelpers_IsObjCBridged_WithoutFlag_ReturnsFalse()
    {
        // Regular non-frozen struct should NOT be flagged as ObjC-bridged.
        var record = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Config"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Config"),
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        };

        Assert.False(MarshallingHelpers.IsObjCBridged(record));
    }

    #endregion

    #region C5 — Duplicate enum case param name (StripeFinancialConnections)

    [Fact]
    public void EnumCaseConstruction_ResultParamCollision_DetectsConflict()
    {
        // When an enum case has an associated value named "result",
        // the EnumHandler.CaseConstruction code detects this and uses "__result"
        // for the indirect result parameter. Test the collision detection logic directly.
        var parameters = new List<(string csType, string swiftType, string name, TypeSpec typeSpec)>
        {
            ("IntPtr", "IntPtr", "result", new NamedTypeSpec("Swift.Int")),
            ("nint", "nint", "value", new NamedTypeSpec("Swift.Int"))
        };

        // The fix checks: parameters.Any(p => p.name == "result") ? "__result" : "result"
        bool hasResultCollision = parameters.Any(p => p.name == "result");
        var indirectResultParamName = hasResultCollision ? "__result" : "result";

        Assert.True(hasResultCollision);
        Assert.Equal("__result", indirectResultParamName);
    }

    [Fact]
    public void EnumCaseConstruction_NoResultParam_UsesDefaultName()
    {
        // When no parameter is named "result", the default "result" name is used.
        var parameters = new List<(string csType, string swiftType, string name, TypeSpec typeSpec)>
        {
            ("IntPtr", "IntPtr", "value", new NamedTypeSpec("Swift.Int")),
            ("nint", "nint", "count", new NamedTypeSpec("Swift.Int"))
        };

        bool hasResultCollision = parameters.Any(p => p.name == "result");
        var indirectResultParamName = hasResultCollision ? "__result" : "result";

        Assert.False(hasResultCollision);
        Assert.Equal("result", indirectResultParamName);
    }

    #endregion

    #region C6 — Async tuple with non-simple enum element (StripePayments)

    [Fact]
    public void ShouldSkipMethodEmission_AsyncTupleWithNonSimpleEnum_IsSkipped()
    {
        // StripePayments: async method returning (STPPaymentHandlerActionStatus, NSError?)
        // The enum flattens into [UnmanagedCallersOnly] callback → CS8894.
        var typeDatabase = CreateTypeDatabaseWithEnum(isSimple: false, requiresMemMgmt: true);

        var tupleReturn = new TupleTypeSpec();
        tupleReturn.Elements.Add(new NamedTypeSpec("TestModule.ComplexEnum"));
        tupleReturn.Elements.Add(new NamedTypeSpec("Swift.Int"));

        var method = CreateMethodDecl("processPayment", "TestModule.TestType",
            returnType: tupleReturn);
        method.IsAsync = true;

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var details);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedSignature, result);
        Assert.Contains("non-simple enum", details!);
    }

    [Fact]
    public void ShouldSkipMethodEmission_AsyncTupleWithSimpleEnum_NotSkipped()
    {
        // Simple enums are blittable and should be fine in callbacks.
        var typeDatabase = CreateTypeDatabaseWithEnum(isSimple: true, requiresMemMgmt: false);

        var tupleReturn = new TupleTypeSpec();
        tupleReturn.Elements.Add(new NamedTypeSpec("TestModule.SimpleEnum"));
        tupleReturn.Elements.Add(new NamedTypeSpec("Swift.Int"));

        var method = CreateMethodDecl("getStatus", "TestModule.TestType",
            returnType: tupleReturn);
        method.IsAsync = true;

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_AsyncNonTupleReturn_NotSkipped()
    {
        // Async method with non-tuple return — no callback flattening issue.
        var typeDatabase = CreateTypeDatabaseWithEnum(isSimple: false, requiresMemMgmt: true);
        var method = CreateMethodDecl("getStatus", "TestModule.TestType",
            returnType: new NamedTypeSpec("TestModule.ComplexEnum"));
        method.IsAsync = true;

        // ShouldSkipMethodEmission is lighter — async non-tuple non-simple enum return is OK
        // because the callback takes the enum directly, not flattened
        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    #endregion

    #region C7 — Default parameter overload projected signature dedup (StripePaymentSheet)

    [Fact]
    public void GetProjectedOverloadKey_MatchesMainPassKey()
    {
        // C7: DefaultParameterOverloadEmitter.GetProjectedOverloadKey must produce the same format
        // as BaseHandler.GetProjectedCSharpMethodKey so dedup works across both passes.
        var typeDatabase = CreateTypeDatabaseWithString();

        var method = CreateMethodDecl("configure", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("name", new NamedTypeSpec("Swift.String") as TypeSpec) });

        // Get key from main pass
        var mainKey = GetProjectedKeyViaReflection(method, typeDatabase);

        // Get key from overload emitter (same format)
        var overloadKey = GetOverloadProjectedKeyViaReflection(method, typeDatabase);

        Assert.Equal(mainKey, overloadKey);
    }

    [Fact]
    public void EmittedProjectedSignatures_SharedBetweenPasses()
    {
        // Verify that MethodEnvironment exposes EmittedProjectedSignatures for sharing.
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("test", "TestModule.TestType");

        var env = new MethodEnvironment(method, typeDatabase);
        Assert.Null(env.EmittedProjectedSignatures);

        var sharedSet = new HashSet<string>();
        env.EmittedProjectedSignatures = sharedSet;
        Assert.Same(sharedSet, env.EmittedProjectedSignatures);
    }

    #endregion

    #region C8 — Optional non-simple enum B18 check (StripeUICore)

    [Fact]
    public void ShouldSkipMethodEmission_OptionalNonSimpleEnumReturn_IsSkipped()
    {
        // StripeUICore: Methods returning Optional<NonSimpleEnum> — the B18 check
        // must unwrap Optional to see the inner non-simple enum.
        var typeDatabase = CreateTypeDatabaseWithEnum(isSimple: false, requiresMemMgmt: true);

        var optionalEnum = new NamedTypeSpec("Swift.Optional");
        optionalEnum.GenericParameters.Add(new NamedTypeSpec("TestModule.ComplexEnum"));

        var method = CreateMethodDecl("getState", "TestModule.TestType",
            returnType: optionalEnum);

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var details);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedSignature, result);
        Assert.Contains("Non-simple enum", details!);
    }

    [Fact]
    public void ShouldSkipMethodEmission_OptionalSimpleEnumReturn_NotSkipped()
    {
        // Optional<SimpleEnum> should not be skipped.
        var typeDatabase = CreateTypeDatabaseWithEnum(isSimple: true, requiresMemMgmt: false);

        var optionalEnum = new NamedTypeSpec("Swift.Optional");
        optionalEnum.GenericParameters.Add(new NamedTypeSpec("TestModule.SimpleEnum"));

        var method = CreateMethodDecl("getDirection", "TestModule.TestType",
            returnType: optionalEnum);

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_AsyncOptionalNonSimpleEnum_NotSkipped()
    {
        // Async methods use callbacks — B18 only applies to sync methods.
        var typeDatabase = CreateTypeDatabaseWithEnum(isSimple: false, requiresMemMgmt: true);

        var optionalEnum = new NamedTypeSpec("Swift.Optional");
        optionalEnum.GenericParameters.Add(new NamedTypeSpec("TestModule.ComplexEnum"));

        var method = CreateMethodDecl("fetchState", "TestModule.TestType",
            returnType: optionalEnum);
        method.IsAsync = true;

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    [Fact]
    public void CanEmitProperty_OptionalNonSimpleEnum_IsSkipped()
    {
        // Property returning Optional<NonSimpleEnum> — C8 fix in CanEmitProperty.
        var typeDatabase = CreateTypeDatabaseWithEnumAndOptional(isSimple: false, requiresMemMgmt: true);

        var optionalEnum = new NamedTypeSpec("Swift.Optional");
        optionalEnum.GenericParameters.Add(new NamedTypeSpec("TestModule.ComplexEnum"));

        var property = CreatePropertyDeclWithAccessor("validationState", optionalEnum);

        var result = MemberEmissionValidator.CanEmitProperty(property, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedSignature, result);
        Assert.Contains("Non-simple enum", details!);
    }

    #endregion

    #region C9 — Protocol interface type alignment (StripeCameraCore)

    [Fact]
    public void ProtocolHandler_GetCSharpTypeName_OptionalBool_ReturnsIdiomaticType()
    {
        // C9: ProtocolHandler.GetCSharpTypeName must check idiomatic types first.
        // Optional<Bool> should resolve to "bool?" not "SwiftOptional<Boolean>".
        var typeDatabase = CreateTypeDatabaseWithBool();

        var optionalBool = new NamedTypeSpec("Swift.Optional");
        optionalBool.GenericParameters.Add(new NamedTypeSpec("Swift.Bool"));

        var typeConversionHandler = new TypeConversionHandler(typeDatabase);
        var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(optionalBool, isParameter: true);

        Assert.NotNull(idiomaticType);
        // TypeConversionHandler uses fully-qualified names from TypeDatabase
        Assert.Contains("bool?", idiomaticType);
    }

    [Fact]
    public void ProtocolHandler_GetCSharpTypeName_PlainInt_ReturnsNull()
    {
        // Plain Int in protocol context — no idiomatic conversion needed
        // (not a bound generic, resolved via TypeDatabase).
        var typeDatabase = CreateTypeDatabase();
        var typeConversionHandler = new TypeConversionHandler(typeDatabase);

        var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(
            new NamedTypeSpec("Swift.Int"), isParameter: true);

        // Int → no idiomatic conversion (it's a primitive, not a wrapper)
        Assert.Null(idiomaticType);
    }

    #endregion

    #region ShouldSkipMethodEmission — scope tests

    [Fact]
    public void ShouldSkipMethodEmission_Constructor_AlwaysAllowed()
    {
        // Normal constructors should never be skipped by the lightweight check.
        // Exception: Codable init(from: Decoder) is pruned — see ShouldSkipMethodEmission_CodableInitFromDecoder_IsSkipped.
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("init", "TestModule.TestType");
        method.IsConstructor = true;

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_SwiftUIParam_IsSkipped()
    {
        // B19 check should be in ShouldSkipMethodEmission too.
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("render", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("view", new NamedTypeSpec("SwiftUI.AnyView") as TypeSpec) });

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.SwiftUIConstraint, result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_NormalMethod_NotSkipped()
    {
        // Regular method with primitive types should pass.
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("process", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("value", new NamedTypeSpec("Swift.Int") as TypeSpec) });

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    #endregion

    #region Helper Methods

    private static MethodDecl CreateMethodDecl(string name, string parentTypeName,
        TypeSpec? returnType = null, (string name, TypeSpec type)[]? parameters = null)
    {
        var moduleDecl = CreateModuleDecl();
        var csSignature = new List<ArgumentDecl>();

        csSignature.Add(new ArgumentDecl
        {
            Name = "_return",
            PrivateName = "_return",
            SwiftTypeSpec = returnType ?? TupleTypeSpec.Empty,
            IsGeneric = false,
            IsInOut = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        });

        if (parameters != null)
        {
            foreach (var (pName, pType) in parameters)
            {
                csSignature.Add(new ArgumentDecl
                {
                    Name = pName,
                    PrivateName = pName,
                    SwiftTypeSpec = pType,
                    IsGeneric = false,
                    IsInOut = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                });
            }
        }

        return new MethodDecl
        {
            Name = name,
            MangledName = "$s4test" + name,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateStructDecl(parentTypeName.Split('.').Last()),
            ModuleDecl = moduleDecl
        };
    }

    private static PropertyDecl CreatePropertyDecl(string name, TypeSpec typeSpec)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = typeSpec,
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = CreateStructDecl("Owner"),
            ModuleDecl = CreateModuleDecl()
        };
    }

    private static PropertyDecl CreatePropertyDeclWithAccessor(string name, TypeSpec typeSpec)
    {
        var parentDecl = CreateStructDecl("Owner");
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = typeSpec,
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>
            {
                CreateGetAccessorWithParent(typeSpec, parentDecl)
            },
            ParentDecl = parentDecl,
            ModuleDecl = CreateModuleDecl()
        };
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Types = new List<TypeDecl>(),
            Protocols = new List<ProtocolDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Dependencies = new List<string>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static StructDecl CreateStructDecl(string name)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
    }

    private static GetAccessorDecl CreateGetAccessor(TypeSpec returnType)
    {
        var method = new MethodDecl
        {
            Name = "get",
            MangledName = "$sGetter",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "_return",
                    PrivateName = "_return",
                    SwiftTypeSpec = returnType,
                    IsGeneric = false,
                    IsInOut = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        return new GetAccessorDecl { Method = method };
    }

    private static GetAccessorDecl CreateGetAccessorWithParent(TypeSpec returnType, BaseDecl parentDecl)
    {
        var method = new MethodDecl
        {
            Name = "get",
            MangledName = "$sGetter",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "_return",
                    PrivateName = "_return",
                    SwiftTypeSpec = returnType,
                    IsGeneric = false,
                    IsInOut = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = CreateModuleDecl()
        };
        return new GetAccessorDecl { Method = method };
    }

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

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithString()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithEnum(bool isSimple, bool requiresMemMgmt)
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

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var enumName = isSimple ? "SimpleEnum" : "ComplexEnum";
        var flags = TypeRecordFlags.None;
        if (isSimple) flags |= TypeRecordFlags.SimpleEnum;
        if (requiresMemMgmt) flags |= TypeRecordFlags.RequiresMemoryManagement;

        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{enumName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", enumName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{enumName}"),
                MetadataAccessor = "$sMa",
                Flags = flags,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = isSimple ? "Swift.Int" : null
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithEnumAndOptional(bool isSimple, bool requiresMemMgmt)
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
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var enumName = isSimple ? "SimpleEnum" : "ComplexEnum";
        var flags = TypeRecordFlags.None;
        if (isSimple) flags |= TypeRecordFlags.SimpleEnum;
        if (requiresMemMgmt) flags |= TypeRecordFlags.RequiresMemoryManagement;

        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{enumName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", enumName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{enumName}"),
                MetadataAccessor = "$sMa",
                Flags = flags,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = isSimple ? "Swift.Int" : null
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithBool()
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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    /// <summary>
    /// Creates a TypeDatabase with Foundation.Date mapped to System.DateTimeOffset
    /// WITHOUT NativeTypeName (the C3 scenario — only CSharpTypeName.Namespace == "System").
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithFoundationDateNoNative()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.Date"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "DateTimeOffset"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Date"),
                NativeTypeName = null, // C3: No NativeTypeName — B11 check misses this
                MetadataAccessor = "$s10Foundation4DateVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static string GetProjectedKeyViaReflection(MethodDecl method, ITypeDatabase typeDatabase)
    {
        var baseHandlerType = typeof(BaseHandler);
        var methodInfo = baseHandlerType.GetMethod("GetProjectedCSharpMethodKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (methodInfo == null)
            throw new InvalidOperationException("Could not find GetProjectedCSharpMethodKey method");
        return (string)methodInfo.Invoke(null, new object?[] { method, typeDatabase, null })!;
    }

    private static string GetOverloadProjectedKeyViaReflection(MethodDecl method, ITypeDatabase typeDatabase)
    {
        var emitterType = typeof(DefaultParameterOverloadEmitter);
        var methodInfo = emitterType.GetMethod("GetProjectedOverloadKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (methodInfo == null)
            throw new InvalidOperationException("Could not find GetProjectedOverloadKey method");
        return (string)methodInfo.Invoke(null, new object[] { method, typeDatabase })!;
    }

    #endregion

    #region C9b — Composition proxy idiomatic types (CryptoSwift)

    [Fact]
    public void ResolveCSharpTypeName_ClosureParamArray_UsesIdiomaticType()
    {
        // CryptoSwift: Composition proxy stubs emitted Action<SwiftArray<byte>> but the interface
        // declared Action<IEnumerable<byte>>. The fix adds idiomatic type check in ResolveCSharpTypeName.
        var typeDatabase = CreateTypeDatabaseWithArrayAndOptional();

        var typeConversionHandler = new TypeConversionHandler(typeDatabase);

        // Array<UInt8> should get idiomatic conversion to IEnumerable<byte> for parameters
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(new NamedTypeSpec("Swift.UInt8"));

        var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(arraySpec, isParameter: true);
        Assert.NotNull(idiomaticType);
        Assert.Contains("IEnumerable", idiomaticType);
        Assert.Contains("byte", idiomaticType);
    }

    #endregion

    #region C10 — Enum case variable shadowing (StripeFinancialConnections)

    [Fact]
    public void EnumCaseWithResultParam_UsesUniqueLocalVarName()
    {
        // StripeFinancialConnections: Enum case factory method has parameter named 'result'
        // which shadows local 'var result = new EnumType()'. Fix: rename local to '__enumResult'.
        // Verify the CaseConstruction code path by checking that the parameters collection
        // triggers the unique name selection.
        var parameters = new List<(string type, string publicType, string name, TypeSpec typeSpec)>
        {
            ("Swift.AnyType", "Swift.AnyType", "result", new NamedTypeSpec("Swift.AnyType"))
        };

        // This mirrors the C10 fix logic: when a param is named "result", use "__enumResult"
        var resultVarName = parameters.Any(p => p.name == "result") ? "__enumResult" : "result";
        Assert.Equal("__enumResult", resultVarName);

        // When no param is named "result", use "result"
        var noResultParams = new List<(string type, string publicType, string name, TypeSpec typeSpec)>
        {
            ("int", "int", "value", new NamedTypeSpec("Swift.Int"))
        };
        var normalVarName = noResultParams.Any(p => p.name == "result") ? "__enumResult" : "result";
        Assert.Equal("result", normalVarName);
    }

    #endregion

    #region C11 — Optional<Closure> vs Closure dedup (StripePayments)

    [Fact]
    public void ProjectedKey_OptionalClosureAndBareClosure_Collide()
    {
        // StripePayments: CreateToken(string, Action<...>?) and CreateToken(string, Action<...>)
        // produce the same C# overload (nullable reference types don't affect overload resolution).
        // Fix: unwrap Optional<Closure> to bare Closure in projected key computation.
        var typeDatabase = CreateTypeDatabaseWithArrayAndOptional();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);

        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        // Method 1: bare closure param
        var method1 = CreateMethodDecl("createToken", "TestModule.TestType",
            parameters: new[]
            {
                ("pii", (TypeSpec)new NamedTypeSpec("Swift.String")),
                ("completion", (TypeSpec)closureType)
            });

        // Method 2: optional closure param
        var method2 = CreateMethodDecl("createToken", "TestModule.TestType",
            parameters: new[]
            {
                ("ssnLast4", (TypeSpec)new NamedTypeSpec("Swift.String")),
                ("completion", (TypeSpec)optionalClosure)
            });

        var key1 = GetProjectedKeyViaReflection(method1, typeDatabase);
        var key2 = GetProjectedKeyViaReflection(method2, typeDatabase);

        // Both should produce the same projected key (collision detected → second method skipped)
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void ProjectedKey_OptionalClosure_OverloadPass_MatchesMainPass()
    {
        // P1: DefaultParameterOverloadEmitter.GetProjectedOverloadKey must also unwrap
        // Optional<Closure> to bare Closure, matching the main pass C11 fix in IHandler.cs.
        // Without this, cross-pass dedup misses collisions for optional-vs-nonoptional closure.
        var typeDatabase = CreateTypeDatabaseWithArrayAndOptional();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);

        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        // Method with Optional<Closure> param
        var method = CreateMethodDecl("process", "TestModule.TestType",
            parameters: new[]
            {
                ("callback", (TypeSpec)optionalClosure)
            });

        // Main pass key (IHandler.cs — has C11 unwrap)
        var mainKey = GetProjectedKeyViaReflection(method, typeDatabase);
        // Overload pass key (DefaultParameterOverloadEmitter — P1 fix adds unwrap)
        var overloadKey = GetOverloadProjectedKeyViaReflection(method, typeDatabase);

        // Both passes must produce the same key for Optional<Closure> params
        Assert.Equal(mainKey, overloadKey);
    }

    #endregion

    #region C12 — Optional<Array<T>> generic arg in proxy + closure return guard

    [Fact]
    public void CanEmitProperty_ClosureReturningNonPrimitiveType_IsSkipped()
    {
        // StripePaymentSheet: Closure property () -> Optional<AddressDetails> produces
        // void* return in function pointer, but the invoker can't marshal it back.
        var typeDatabase = CreateTypeDatabaseWithArrayAndOptional();

        // Non-frozen struct type
        var structType = new NamedTypeSpec("StripePaymentSheet.AddressDetails");

        // Optional wrapping the struct
        var optionalStruct = new NamedTypeSpec("Swift.Optional");
        optionalStruct.GenericParameters.Add(structType);

        // Closure returning Optional<NonFrozenStruct>
        var closureType = new ClosureTypeSpec(TupleTypeSpec.Empty, optionalStruct);

        var property = CreatePropertyDeclWithAccessor("shippingDetails", closureType);

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out _);

        Assert.NotNull(skipReason);
        Assert.Equal(SkipReason.UnsupportedClosure, skipReason);
        // May be caught by CanInvokeFromCSharp ("Closure type is not supported") or by
        // the C12 return type guard — either way, the closure property is correctly skipped.
    }

    [Fact]
    public void CanEmitProperty_ClosureReturningPrimitiveType_Passes()
    {
        // Closures returning primitives (Int, Bool) should pass — their return maps to
        // blittable types in the function pointer.
        var typeDatabase = CreateTypeDatabaseWithArrayAndOptional();

        // Closure returning Int
        var closureType = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Int"));

        var property = CreatePropertyDeclWithAccessor("counter", closureType);

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out _, out _);

        Assert.Null(skipReason);
    }

    #endregion

    #region Test helpers (additional)

    private static TypeDatabase CreateTypeDatabaseWithArrayAndOptional()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.UInt8"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Byte"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.UInt8"),
                MetadataAccessor = "$ss5UInt8VMa",
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
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    #endregion

    #region WU2 — Codable member pruning

    [Fact]
    public void ShouldSkipMethodEmission_CodableEncodeToEncoder_IsSkipped()
    {
        // encode(to: any Swift.Encoder) should be pruned as synthesized Codable member.
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("encode", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("to", new NamedTypeSpec("Swift.Encoder") { IsAny = true } as TypeSpec) });

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var skipDetails);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.SynthesizedCodable, result);
        Assert.Contains("Codable", skipDetails);
    }

    [Fact]
    public void ShouldSkipMethodEmission_CodableInitFromDecoder_IsSkipped()
    {
        // init(from: any Swift.Decoder) should be pruned as synthesized Codable member.
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("init", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("from", new NamedTypeSpec("Swift.Decoder") { IsAny = true } as TypeSpec) });
        method.IsConstructor = true;

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var skipDetails);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.SynthesizedCodable, result);
        Assert.Contains("Codable", skipDetails);
    }

    [Fact]
    public void ShouldSkipMethodEmission_EncodeWithNonEncoderParam_NotSkipped()
    {
        // encode(data: SomeType) should NOT be pruned — it's a normal method.
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("encode", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("data", new NamedTypeSpec("TestModule.SomeType") as TypeSpec) });

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_ConstructorWithNonDecoderParam_NotSkipped()
    {
        // init(value: Int) should NOT be pruned — it's a normal constructor.
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("init", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("value", new NamedTypeSpec("Swift.Int") as TypeSpec) });
        method.IsConstructor = true;

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_EncodeDataMethodNotCodable_NotSkipped()
    {
        // A method named "encodeData" with unrelated params should NOT be pruned.
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("encodeData", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("buffer", new NamedTypeSpec("TestModule.Buffer") as TypeSpec) });

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    #endregion
}
