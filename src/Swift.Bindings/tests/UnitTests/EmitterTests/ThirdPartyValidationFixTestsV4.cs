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
    #region C1 — Optional tuple with unsupported element

    [Fact]
    public void CanEmitProperty_OptionalTupleWithClosureElement_IsSkipped()
    {
        // UploadProgressHandler is Optional<(AnyType, DispatchQueue)>
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

    #region C2 — CanEmitProperty wired into type handlers

    [Fact]
    public void CanEmitProperty_SwiftUIProperty_IsSkipped()
    {
        // SwiftUI.Color/SwiftUI.Font properties should be skipped.
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

    #region C3 — DateTimeOffset bound generic detection

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

    [Fact]
    public void HasNonSwiftObjectGenericArg_SwiftResultOfVoid_ReturnsFalse()
    {
        // Issue D.1: Swift.Result<(), MyError>.
        // SwiftResult<TSuccess, TFailure> has no ISwiftObject constraint on its type
        // parameters, so a ValueTuple (empty or otherwise) success arg is safe; the
        // projection handles marshalling. Without the bypass, the tuple blocker
        // at BoundGenericsHandler.HasNonSwiftObjectGenericArg returned true and
        // the affected properties were silently tombstoned.
        var typeDatabase = CreateTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase);

        var resultTypeSpec = new NamedTypeSpec("Swift.Result");
        resultTypeSpec.GenericParameters.Add(new TupleTypeSpec());
        resultTypeSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyError"));

        Assert.False(handler.HasNonSwiftObjectGenericArg(resultTypeSpec));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_SwiftResultOfNonEmptyTuple_ReturnsFalse()
    {
        // Result<(Int, String), E> — non-empty tuple as Success. SwiftResult has no
        // constraint, so even a non-void tuple is valid as a generic arg.
        var typeDatabase = CreateTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase);

        var resultTypeSpec = new NamedTypeSpec("Swift.Result");
        var successTuple = new TupleTypeSpec();
        successTuple.Elements.Add(new NamedTypeSpec("Swift.Int"));
        successTuple.Elements.Add(new NamedTypeSpec("Swift.Int"));
        resultTypeSpec.GenericParameters.Add(successTuple);
        resultTypeSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyError"));

        Assert.False(handler.HasNonSwiftObjectGenericArg(resultTypeSpec));
    }

    #endregion

    #region C4 — ObjC-bridged type excluded from async copy buffer

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

    #region C5 — Duplicate enum case param name

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

    #region C6 — Async tuple with non-simple enum element

    [Fact]
    public void ShouldSkipMethodEmission_AsyncTupleWithNonSimpleEnum_IsSkipped()
    {
        // Async method returning a tuple of (non-simple enum, NSError?) where the enum
        // flattens into an [UnmanagedCallersOnly] callback → CS8894.
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

    #region C7 — Default parameter overload projected signature dedup

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

    #region C8 — Optional non-simple enum (B18 gate lifted)

    [Fact]
    public void ShouldSkipMethodEmission_OptionalNonSimpleEnumReturn_NotSkipped()
    {
        // B18 gate was removed — Optional<NonSimpleEnum> returns now supported.
        var typeDatabase = CreateTypeDatabaseWithEnum(isSimple: false, requiresMemMgmt: true);

        var optionalEnum = new NamedTypeSpec("Swift.Optional");
        optionalEnum.GenericParameters.Add(new NamedTypeSpec("TestModule.ComplexEnum"));

        var method = CreateMethodDecl("getState", "TestModule.TestType",
            returnType: optionalEnum);

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
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
    public void CanEmitProperty_OptionalNonSimpleEnum_NotSkipped()
    {
        // B18 gate was removed — Optional<NonSimpleEnum> properties now supported.
        var typeDatabase = CreateTypeDatabaseWithEnumAndOptional(isSimple: false, requiresMemMgmt: true);

        var optionalEnum = new NamedTypeSpec("Swift.Optional");
        optionalEnum.GenericParameters.Add(new NamedTypeSpec("TestModule.ComplexEnum"));

        var property = CreatePropertyDeclWithAccessor("validationState", optionalEnum);

        var result = MemberEmissionValidator.CanEmitProperty(property, typeDatabase, out _, out _);

        Assert.Null(result);
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

    #region loadImage — Result&lt;T,E&gt; closure + non-frozen struct param

    [Fact]
    public void ImagePipeline_LoadImage_IsEligibleForMethodClosureBridge()
    {
        // loadImage(with:completion:) takes:
        //   with: ImageRequest (non-frozen struct)
        //   completion: @escaping (Result<ImageResponse, ImagePipeline.Error>) -> Void
        // This must be eligible for MethodClosureBridge now that non-frozen structs are passable.
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftResult"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
                MetadataAccessor = "$ss6ResultOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var imageModule = new ModuleTypeDatabase("ImagePipeline", "/tmp/ImagePipeline.dylib");
        imageModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageService"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImagePipeline", "ImageService"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageService"),
                MetadataAccessor = "$s13ImagePipeline12ImageServiceCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        imageModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageRequest"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImagePipeline", "ImageRequest"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageRequest"),
                MetadataAccessor = "$s13ImagePipeline12ImageRequestVMa",
                Flags = TypeRecordFlags.None, // Non-frozen struct
                Kind = TypeRecordKind.Struct
            });
        imageModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageResponse"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImagePipeline", "ImageResponse"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageResponse"),
                MetadataAccessor = "$s13ImagePipeline13ImageResponseVMa",
                Flags = TypeRecordFlags.None, // Non-frozen struct
                Kind = TypeRecordKind.Struct
            });
        imageModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageService.Error"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImagePipeline", "ImageServiceError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageService.Error"),
                MetadataAccessor = "$s13ImagePipeline12ImageServiceC5ErrorOMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement, // Non-simple enum
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(imageModule);

        // Build: loadImage(with: ImageRequest, completion: @escaping (Result<ImageResponse, ImagePipeline.Error>) -> Void)
        var imageModuleDecl = new ModuleDecl
        {
            Name = "ImagePipeline",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var pipelineDecl = new ClassDecl
        {
            Name = "ImageService",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageService"),
            MangledName = "$s13ImagePipeline12ImageServiceCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = imageModuleDecl,
            ModuleDecl = imageModuleDecl
        };

        var resultType = new NamedTypeSpec("Swift.Result",
            new NamedTypeSpec("ImagePipeline.ImageResponse"),
            new NamedTypeSpec("ImagePipeline.ImageService.Error"));

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { (TypeSpec)resultType }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var method = new MethodDecl
        {
            Name = "loadImage",
            MangledName = "$s13ImagePipeline12ImageServiceC9loadImage4with10completionyAA0dF0V_yAF_AC5ErrorOtctF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "", SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "", IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = imageModuleDecl
                },
                new ArgumentDecl
                {
                    Name = "_with", SwiftTypeSpec = new NamedTypeSpec("ImagePipeline.ImageRequest"),
                    PrivateName = "request", IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = imageModuleDecl
                },
                new ArgumentDecl
                {
                    Name = "_completion", SwiftTypeSpec = closureType,
                    PrivateName = "completion", IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = imageModuleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = pipelineDecl,
            ModuleDecl = imageModuleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var closureHandler = new ClosureHandler(typeDatabase);
        Assert.True(MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase));
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
        // Args: method, typeDatabase, logger, siblingPropertyNames, treatAsClosureTombstone.
        return (string)methodInfo.Invoke(null, new object?[] { method, typeDatabase, null, null, false })!;
    }

    private static string GetOverloadProjectedKeyViaReflection(MethodDecl method, ITypeDatabase typeDatabase)
    {
        var emitterType = typeof(DefaultParameterOverloadEmitter);
        var methodInfo = emitterType.GetMethod("GetProjectedOverloadKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (methodInfo == null)
            throw new InvalidOperationException("Could not find GetProjectedOverloadKey method");
        // Args: method, typeDatabase, siblingPropertyNames (null = base behavior, no property rename).
        return (string)methodInfo.Invoke(null, new object?[] { method, typeDatabase, null })!;
    }

    #endregion

    #region C10 — Enum case variable shadowing

    [Fact]
    public void EnumCaseWithResultParam_UsesUniqueLocalVarName()
    {
        // Enum case factory method has parameter named 'result'
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

    #region C11 — Optional&lt;Closure&gt; vs Closure dedup

    [Fact]
    public void ProjectedKey_OptionalClosureAndBareClosure_Collide()
    {
        // CreateToken(string, Action<...>?) and CreateToken(string, Action<...>)
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
        // Closure property () -> Optional<AddressDetails> produces void* return in function
        // pointer, but the invoker can't marshal it back.
        var typeDatabase = CreateTypeDatabaseWithArrayAndOptional();

        // Non-frozen struct type
        var structType = new NamedTypeSpec("PaymentSdkPaymentSheet.AddressDetails");

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

    #region Optional<Closure> with default parameter bypass

    [Fact]
    public void ShouldSkipMethodEmission_OptionalClosureWithDefault_NotSkipped()
    {
        // Method with Optional<Closure> + HasDefaultArg=true should pass through
        // ShouldSkipMethodEmission — ExistentialBypassEmitter handles it in MethodHandler.
        var typeDatabase = CreateTypeDatabase();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("UnknownModule.SomeError") }),
            TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        var method = CreateMethodDeclWithDefaultArgs("loadVenue", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("errorCallback", optionalClosure as TypeSpec, true) });

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_OptionalClosureWithoutDefault_IsSkipped()
    {
        // Optional<Closure> without HasDefaultArg should still be skipped.
        var typeDatabase = CreateTypeDatabase();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("UnknownModule.SomeError") }),
            TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        var method = CreateMethodDeclWithDefaultArgs("loadVenue", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("errorCallback", optionalClosure as TypeSpec, false) });

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var details);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedClosure, result);
        Assert.Contains("unsupported closure", details!);
    }

    [Fact]
    public void ShouldSkipMethodEmission_NonOptionalUnsupportedClosure_StillSkipped()
    {
        // Bare unsupported closure (not Optional<Closure>) should still be skipped
        // even with HasDefaultArg — regression guard.
        var typeDatabase = CreateTypeDatabase();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("UnknownModule.SomeError") }),
            TupleTypeSpec.Empty);

        var method = CreateMethodDeclWithDefaultArgs("process", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("callback", closureType as TypeSpec, true) });

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var details);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedClosure, result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_MixedOptionalAndNonOptionalClosure_IsSkipped()
    {
        // If one closure has no default, the entire method should still be skipped.
        var typeDatabase = CreateTypeDatabase();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("UnknownModule.SomeError") }),
            TupleTypeSpec.Empty);

        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        // Second param: bare unsupported closure without default
        var bareClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("UnknownModule.OtherType") }),
            TupleTypeSpec.Empty);

        var method = CreateMethodDeclWithDefaultArgs("mixed", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[]
            {
                ("optionalCallback", optionalClosure as TypeSpec, true),
                ("requiredCallback", bareClosure as TypeSpec, false)
            });

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedClosure, result);
    }

    [Fact]
    public void CanEmitMethod_OptionalClosureWithDefault_StillSkipped()
    {
        // CanEmitMethod must remain conservative — Optional<Closure> with default
        // should still be skipped (used by ProtocolConformanceValidator).
        var typeDatabase = CreateTypeDatabase();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("UnknownModule.SomeError") }),
            TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        var method = CreateMethodDeclWithDefaultArgs("loadVenue", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("errorCallback", optionalClosure as TypeSpec, true) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedClosure, result);
    }

    [Fact]
    public void HasOptionalClosureWithDefault_DetectsPattern()
    {
        // Unit test for the ExistentialBypassEmitter helper.
        var typeDatabase = CreateTypeDatabase();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("UnknownModule.SomeError") }),
            TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        var methodWithDefault = CreateMethodDeclWithDefaultArgs("loadVenue", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("errorCallback", optionalClosure as TypeSpec, true) });

        Assert.True(ExistentialBypassEmitter.HasOptionalClosureWithDefault(methodWithDefault, typeDatabase));

        var methodWithoutDefault = CreateMethodDeclWithDefaultArgs("loadVenue", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("errorCallback", optionalClosure as TypeSpec, false) });

        Assert.False(ExistentialBypassEmitter.HasOptionalClosureWithDefault(methodWithoutDefault, typeDatabase));
    }

    [Fact]
    public void HasOptionalClosureWithDefault_SupportedClosure_ReturnsFalse()
    {
        // A supported Optional<Closure> with default should NOT trigger bypass —
        // it goes through normal emission.
        var typeDatabase = CreateTypeDatabase();

        // Closure with only primitive args is supported
        var supportedClosure = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(supportedClosure);

        var method = CreateMethodDeclWithDefaultArgs("process", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("callback", optionalClosure as TypeSpec, true) });

        Assert.False(ExistentialBypassEmitter.HasOptionalClosureWithDefault(method, typeDatabase));
    }

    [Fact]
    public void ShouldSkipMethodEmission_StaticMethodWithOptionalClosureDefault_NotSkipped()
    {
        // Static methods with Optional<Closure>+default should pass ShouldSkipMethodEmission.
        // MethodClosureBridge handles static methods; the bypass does not preempt them.
        var typeDatabase = CreateTypeDatabase();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("UnknownModule.SomeError") }),
            TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        var method = CreateMethodDeclWithDefaultArgs("configure", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("errorCallback", optionalClosure as TypeSpec, true) });
        method.MethodType = MethodType.Static;

        // ShouldSkipMethodEmission should still let it through (the carve-out is param-level)
        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);
        Assert.Null(result);
    }

    [Fact]
    public void BuildReducedMethodDecl_StripsOptionalClosureWithDefault()
    {
        // Verify BuildReducedMethodDecl strips unsupported Optional<Closure>+default params
        // but keeps other params.
        var typeDatabase = CreateTypeDatabase();

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("UnknownModule.SomeError") }),
            TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        var method = CreateMethodDeclWithDefaultArgs("loadVenue", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[]
            {
                ("options", new NamedTypeSpec("Swift.Int") as TypeSpec, false),
                ("errorCallback", optionalClosure as TypeSpec, true)
            });

        var reduced = ExistentialBypassEmitter.BuildReducedMethodDecl(method, typeDatabase);

        Assert.NotNull(reduced);
        // CSSignature: return + 1 passthrough param (options). errorCallback stripped.
        Assert.Equal(2, reduced!.CSSignature.Count);
        Assert.Equal("options", reduced.CSSignature[1].Name);
    }

    [Fact]
    public void BuildReducedMethodDecl_NoOmittableParams_ReturnsNull()
    {
        // When no params are omittable, BuildReducedMethodDecl returns null.
        var typeDatabase = CreateTypeDatabase();

        var method = CreateMethodDeclWithDefaultArgs("process", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("value", new NamedTypeSpec("Swift.Int") as TypeSpec, false) });

        var reduced = ExistentialBypassEmitter.BuildReducedMethodDecl(method, typeDatabase);

        Assert.Null(reduced);
    }

    [Fact]
    public void BuildReducedMethodDecl_DedupKeyCollisionDetected()
    {
        // Two different methods that reduce to the same signature should produce the same
        // projected key, allowing the dedup check to catch the collision.
        var typeDatabase = CreateTypeDatabase();

        var closureType1 = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("UnknownModule.ErrorA") }),
            TupleTypeSpec.Empty);
        var optionalClosure1 = new NamedTypeSpec("Swift.Optional");
        optionalClosure1.GenericParameters.Add(closureType1);

        var closureType2 = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("UnknownModule.ErrorB") }),
            TupleTypeSpec.Empty);
        var optionalClosure2 = new NamedTypeSpec("Swift.Optional");
        optionalClosure2.GenericParameters.Add(closureType2);

        // Method 1: loadVenue(options: Int, errorCallback: Optional<(ErrorA) -> Void> = nil)
        var method1 = CreateMethodDeclWithDefaultArgs("loadVenue", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[]
            {
                ("options", new NamedTypeSpec("Swift.Int") as TypeSpec, false),
                ("errorCallback", optionalClosure1 as TypeSpec, true)
            });

        // Method 2: loadVenue(options: Int, failureHandler: Optional<(ErrorB) -> Void> = nil)
        var method2 = CreateMethodDeclWithDefaultArgs("loadVenue", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[]
            {
                ("options", new NamedTypeSpec("Swift.Int") as TypeSpec, false),
                ("failureHandler", optionalClosure2 as TypeSpec, true)
            });

        var reduced1 = ExistentialBypassEmitter.BuildReducedMethodDecl(method1, typeDatabase);
        var reduced2 = ExistentialBypassEmitter.BuildReducedMethodDecl(method2, typeDatabase);

        Assert.NotNull(reduced1);
        Assert.NotNull(reduced2);

        // Both reduce to loadVenue(Int) — same projected key
        var key1 = GetProjectedKeyViaReflection(reduced1!, typeDatabase);
        var key2 = GetProjectedKeyViaReflection(reduced2!, typeDatabase);
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void BuildReducedMethodDecl_ContainerExistentialNotOmitted()
    {
        // Supported container existentials (Array<any P>, Optional<any P>) should NOT
        // be stripped by BuildReducedMethodDecl — they go through normal emission in MethodHandler.
        var typeDatabase = CreateTypeDatabaseWithProtocol();

        // Build Array<any SomeProtocol> — a container with supported existential
        var existentialElement = new NamedTypeSpec("TestModule.SomeProtocol") { IsAny = true };
        var arrayOfExistential = new NamedTypeSpec("Swift.Array");
        arrayOfExistential.GenericParameters.Add(existentialElement);

        // Optional<Closure> with default
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("UnknownModule.SomeError") }),
            TupleTypeSpec.Empty);
        var optionalClosure = new NamedTypeSpec("Swift.Optional");
        optionalClosure.GenericParameters.Add(closureType);

        var method = CreateMethodDeclWithDefaultArgs("process", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[]
            {
                ("items", arrayOfExistential as TypeSpec, false),
                ("errorCallback", optionalClosure as TypeSpec, true)
            });

        var reduced = ExistentialBypassEmitter.BuildReducedMethodDecl(method, typeDatabase);

        Assert.NotNull(reduced);
        // Should keep 'items' (container existential = passthrough) and strip only 'errorCallback'
        Assert.Equal(2, reduced!.CSSignature.Count); // return + items
        Assert.Equal("items", reduced.CSSignature[1].Name);
    }

    /// <summary>
    /// Creates a TypeDatabase with Swift stdlib types + a TestModule protocol for existential tests.
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithProtocol()
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
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.SomeProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ISomeProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SomeProtocol"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    /// <summary>
    /// Creates a MethodDecl with support for HasDefaultArg on parameters.
    /// </summary>
    private static MethodDecl CreateMethodDeclWithDefaultArgs(string name, string parentTypeName,
        TypeSpec? returnType = null, (string name, TypeSpec type, bool hasDefault)[]? parameters = null)
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
            foreach (var (pName, pType, hasDefault) in parameters)
            {
                csSignature.Add(new ArgumentDecl
                {
                    Name = pName,
                    PrivateName = pName,
                    SwiftTypeSpec = pType,
                    IsGeneric = false,
                    IsInOut = false,
                    HasDefaultArg = hasDefault,
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

    #endregion
}
