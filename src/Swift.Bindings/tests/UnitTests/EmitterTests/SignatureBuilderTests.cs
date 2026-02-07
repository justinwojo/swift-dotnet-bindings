// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MethodHandler signature building functionality.
/// These tests focus on WrapperSignatureBuilder and PInvokeSignatureBuilder behavior.
/// </summary>
public class SignatureBuilderTests
{
    #region MethodDecl Creation Tests

    [Fact]
    public void MethodDecl_InstanceMethod_HasCorrectMethodType()
    {
        var methodDecl = CreateMethodDecl("doSomething", isStatic: false);

        Assert.Equal(MethodType.Instance, methodDecl.MethodType);
    }

    [Fact]
    public void MethodDecl_StaticMethod_HasCorrectMethodType()
    {
        var methodDecl = CreateMethodDecl("doSomething", isStatic: true);

        Assert.Equal(MethodType.Static, methodDecl.MethodType);
    }

    [Fact]
    public void MethodDecl_Constructor_IsConstructorTrue()
    {
        var methodDecl = CreateMethodDecl("init", isConstructor: true);

        Assert.True(methodDecl.IsConstructor);
    }

    [Fact]
    public void MethodDecl_Throws_ThrowsTrue()
    {
        var methodDecl = CreateMethodDecl("riskyOperation", throws: true);

        Assert.True(methodDecl.Throws);
    }

    [Fact]
    public void MethodDecl_Async_IsAsyncTrue()
    {
        var methodDecl = CreateMethodDecl("fetchData", isAsync: true);

        Assert.True(methodDecl.IsAsync);
    }

    #endregion

    #region Return Type Handling Tests

    [Theory]
    [InlineData("Swift.Int", "System.Int64")]
    [InlineData("Swift.Bool", "System.Boolean")]
    [InlineData("Swift.Double", "System.Double")]
    [InlineData("Swift.Float", "System.Single")]
    [InlineData("Swift.UInt", "System.UInt64")]
    public void ReturnType_PrimitiveType_MapsCorrectly(string swiftType, string expectedCSharpType)
    {
        // Test that primitive type mappings are correct
        var typeRecord = CreatePrimitiveTypeRecord(swiftType, expectedCSharpType);

        Assert.Equal(expectedCSharpType, typeRecord.CSharpTypeName.FullyQualifiedName);
    }

    [Fact]
    public void ReturnType_SwiftString_MapsToSwiftString()
    {
        var typeRecord = CreateTypeRecord("Swift.String", "Swift.SwiftString");

        Assert.Equal("Swift.SwiftString", typeRecord.CSharpTypeName.FullyQualifiedName);
    }

    [Fact]
    public void ReturnType_Void_IsEmptyTuple()
    {
        var voidReturn = TupleTypeSpec.Empty;

        Assert.True(voidReturn.IsEmptyTuple);
    }

    #endregion

    #region Argument Handling Tests

    [Fact]
    public void Arguments_WithPrimitiveTypes_ParsesCorrectly()
    {
        var methodDecl = CreateMethodDecl("process");
        methodDecl.CSSignature.Add(CreateArgumentDecl("count", new NamedTypeSpec("Swift.Int")));
        methodDecl.CSSignature.Add(CreateArgumentDecl("flag", new NamedTypeSpec("Swift.Bool")));

        // Skip first argument (return type)
        var args = methodDecl.CSSignature.Skip(1).ToList();

        Assert.Equal(2, args.Count);
        Assert.Equal("count", args[0].Name);
        Assert.Equal("flag", args[1].Name);
    }

    [Fact]
    public void Arguments_WithBoundGeneric_HasGenericParameters()
    {
        var genericType = new NamedTypeSpec("Swift.Array");
        genericType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var methodDecl = CreateMethodDecl("process");
        methodDecl.CSSignature.Add(CreateArgumentDecl("items", genericType));

        var arg = methodDecl.CSSignature.Skip(1).First();

        Assert.IsType<NamedTypeSpec>(arg.SwiftTypeSpec);
        var namedSpec = (NamedTypeSpec)arg.SwiftTypeSpec;
        Assert.Single(namedSpec.GenericParameters);
    }

    [Fact]
    public void Arguments_WithClosure_HasClosureTypeSpec()
    {
        var closureType = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);

        var methodDecl = CreateMethodDecl("execute");
        methodDecl.CSSignature.Add(CreateArgumentDecl("callback", closureType));

        var arg = methodDecl.CSSignature.Skip(1).First();

        Assert.IsType<ClosureTypeSpec>(arg.SwiftTypeSpec);
    }

    [Fact]
    public void Arguments_WithTuple_HasTupleTypeSpec()
    {
        var tupleType = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });

        var methodDecl = CreateMethodDecl("process");
        methodDecl.CSSignature.Add(CreateArgumentDecl("pair", tupleType));

        var arg = methodDecl.CSSignature.Skip(1).First();

        Assert.IsType<TupleTypeSpec>(arg.SwiftTypeSpec);
    }

    [Fact]
    public void Arguments_InOutParameter_HasIsInOutTrue()
    {
        var methodDecl = CreateMethodDecl("modify");
        var inoutArg = new ArgumentDecl
        {
            Name = "value",
            PrivateName = "value",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsInOut = true,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
        methodDecl.CSSignature.Add(inoutArg);

        var arg = methodDecl.CSSignature.Skip(1).First();

        Assert.True(arg.IsInOut);
    }

    [Fact]
    public void Arguments_GenericParameter_HasIsGenericTrue()
    {
        var methodDecl = CreateMethodDecl("transform");
        var genericArg = new ArgumentDecl
        {
            Name = "value",
            PrivateName = "value",
            SwiftTypeSpec = new NamedTypeSpec("T"),
            IsInOut = false,
            IsGeneric = true,
            ParentDecl = null,
            ModuleDecl = null
        };
        methodDecl.CSSignature.Add(genericArg);

        var arg = methodDecl.CSSignature.Skip(1).First();

        Assert.True(arg.IsGeneric);
    }

    #endregion

    #region Generic Method Tests

    [Fact]
    public void GenericMethod_WithTypeParameter_HasGenericParameters()
    {
        var methodDecl = CreateMethodDecl("transform");
        methodDecl.GenericParameters.Add(CreateGenericArgumentDecl("T"));

        Assert.Single(methodDecl.GenericParameters);
    }

    [Fact]
    public void GenericMethod_WithMultipleTypeParameters_CollectsAll()
    {
        var methodDecl = CreateMethodDecl("convert");
        methodDecl.GenericParameters.Add(CreateGenericArgumentDecl("T"));
        methodDecl.GenericParameters.Add(CreateGenericArgumentDecl("U"));

        Assert.Equal(2, methodDecl.GenericParameters.Count);
    }

    [Fact]
    public void GenericMethod_WithConstrainedParameter_HasConformances()
    {
        var methodDecl = CreateMethodDecl("compare");
        methodDecl.GenericParameters.Add(CreateGenericArgumentDeclWithConformance("T", "Swift.Equatable"));

        Assert.Single(methodDecl.GenericParameters[0].GenericConformances);
    }

    [Fact]
    public void GenericMethod_WithProtocolConstraint_ConformanceHasTarget()
    {
        var methodDecl = CreateMethodDecl("sort");
        methodDecl.GenericParameters.Add(CreateGenericArgumentDeclWithConformance("T", "Swift.Comparable"));

        var conformance = methodDecl.GenericParameters[0].GenericConformances[0];

        Assert.Equal("Swift.Comparable", conformance.ConformanceTarget.ModuleQualifiedName);
    }

    #endregion

    #region Async Method Tests

    [Fact]
    public void AsyncMethod_HasIsAsyncTrue()
    {
        var methodDecl = CreateMethodDecl("fetchData", isAsync: true);

        Assert.True(methodDecl.IsAsync);
    }

    [Fact]
    public void AsyncMethod_InstanceMethod_NotStatic()
    {
        var methodDecl = CreateMethodDecl("fetchData", isAsync: true, isStatic: false);

        Assert.Equal(MethodType.Instance, methodDecl.MethodType);
    }

    [Fact]
    public void AsyncMethod_StaticMethod_IsStatic()
    {
        var methodDecl = CreateMethodDecl("fetchAll", isAsync: true, isStatic: true);

        Assert.Equal(MethodType.Static, methodDecl.MethodType);
    }

    [Fact]
    public void AsyncMethod_WithReturnType_HasReturnType()
    {
        var methodDecl = CreateMethodDecl("fetchData", isAsync: true);
        methodDecl.CSSignature[0] = CreateArgumentDecl("", new NamedTypeSpec("Swift.String"));

        var returnArg = methodDecl.CSSignature[0];

        Assert.IsType<NamedTypeSpec>(returnArg.SwiftTypeSpec);
    }

    [Fact]
    public void AsyncMethod_CanThrow_HasBothAsyncAndThrows()
    {
        var methodDecl = CreateMethodDecl("riskyFetch", isAsync: true, throws: true);

        Assert.True(methodDecl.IsAsync);
        Assert.True(methodDecl.Throws);
    }

    #endregion

    #region Throwing Method Tests

    [Fact]
    public void ThrowingMethod_HasThrowsTrue()
    {
        var methodDecl = CreateMethodDecl("parse", throws: true);

        Assert.True(methodDecl.Throws);
    }

    [Fact]
    public void ThrowingMethod_NonAsync_OnlyThrows()
    {
        var methodDecl = CreateMethodDecl("validate", throws: true, isAsync: false);

        Assert.True(methodDecl.Throws);
        Assert.False(methodDecl.IsAsync);
    }

    #endregion

    #region Constructor Handler Tests

    [Fact]
    public void Constructor_IsConstructorTrue()
    {
        var methodDecl = CreateMethodDecl("init", isConstructor: true);

        Assert.True(methodDecl.IsConstructor);
    }

    [Fact]
    public void Constructor_WithParameters_HasParameters()
    {
        var methodDecl = CreateMethodDecl("init", isConstructor: true);
        methodDecl.CSSignature.Add(CreateArgumentDecl("name", new NamedTypeSpec("Swift.String")));
        methodDecl.CSSignature.Add(CreateArgumentDecl("age", new NamedTypeSpec("Swift.Int")));

        // Skip return type
        var args = methodDecl.CSSignature.Skip(1).ToList();

        Assert.Equal(2, args.Count);
    }

    [Fact]
    public void Constructor_CanThrow()
    {
        var methodDecl = CreateMethodDecl("init", isConstructor: true, throws: true);

        Assert.True(methodDecl.IsConstructor);
        Assert.True(methodDecl.Throws);
    }

    #endregion

    #region Method Handler Factory Tests

    [Fact]
    public void MethodHandlerFactory_Handles_MethodDecl_ReturnsTrue()
    {
        var factory = new MethodHandlerFactory(NullLoggerFactory.Instance);
        var methodDecl = CreateMethodDecl("doSomething");

        Assert.True(factory.Handles(methodDecl));
    }

    [Fact]
    public void MethodHandlerFactory_Handles_Constructor_ReturnsTrue()
    {
        // MethodHandlerFactory handles all MethodDecl, including constructors
        var factory = new MethodHandlerFactory(NullLoggerFactory.Instance);
        var constructorDecl = CreateMethodDecl("init", isConstructor: true);

        Assert.True(factory.Handles(constructorDecl));
    }

    [Fact]
    public void MethodHandlerFactory_Construct_ReturnsHandler()
    {
        var factory = new MethodHandlerFactory(NullLoggerFactory.Instance);

        var handler = factory.Construct();

        Assert.NotNull(handler);
        Assert.IsType<MethodHandler>(handler);
    }

    [Fact]
    public void ConstructorHandlerFactory_Handles_StructConstructor_ReturnsTrue()
    {
        // ConstructorHandlerFactory only handles constructors on structs
        var factory = new ConstructorHandlerFactory(NullLoggerFactory.Instance);
        var structDecl = CreateStructDecl("Point");
        var constructorDecl = CreateMethodDecl("init", isConstructor: true);
        constructorDecl.ParentDecl = structDecl;

        Assert.True(factory.Handles(constructorDecl));
    }

    [Fact]
    public void ConstructorHandlerFactory_Handles_RegularMethod_ReturnsFalse()
    {
        var factory = new ConstructorHandlerFactory(NullLoggerFactory.Instance);
        var methodDecl = CreateMethodDecl("doSomething");

        Assert.False(factory.Handles(methodDecl));
    }

    [Fact]
    public void ConstructorHandlerFactory_Construct_ReturnsHandler()
    {
        var factory = new ConstructorHandlerFactory(NullLoggerFactory.Instance);

        var handler = factory.Construct();

        Assert.NotNull(handler);
        Assert.IsType<ConstructorHandler>(handler);
    }

    #endregion

    #region Closure Type Tests

    [Fact]
    public void ClosureTypeSpec_WithVoidReturn_HasEmptyReturnType()
    {
        // ClosureTypeSpec constructor takes (arguments, returnType)
        var closure = new ClosureTypeSpec(
            TupleTypeSpec.Empty, // arguments
            TupleTypeSpec.Empty  // return type
        );

        Assert.True(closure.ReturnType.IsEmptyTuple);
    }

    [Fact]
    public void ClosureTypeSpec_WithParameters_HasArguments()
    {
        var argsTuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });

        // ClosureTypeSpec constructor takes (arguments, returnType)
        var closure = new ClosureTypeSpec(
            argsTuple,
            new NamedTypeSpec("Swift.Bool")
        );

        var argsType = closure.Arguments as TupleTypeSpec;
        Assert.NotNull(argsType);
        Assert.Equal(2, argsType.Elements.Count);
    }

    [Fact]
    public void ClosureTypeSpec_IsEscaping_DetectedFromAttribute()
    {
        // IsEscaping is detected from attributes, not a settable property
        var closure = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            TupleTypeSpec.Empty);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));

        Assert.True(closure.IsEscaping);
    }

    [Fact]
    public void ClosureTypeSpec_HasReturn_WhenReturnTypeNotEmpty()
    {
        var closure = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.Int"));

        Assert.True(closure.HasReturn());
    }

    #endregion

    #region Tuple Type Tests

    [Fact]
    public void TupleTypeSpec_Empty_IsEmptyTuple()
    {
        var tuple = TupleTypeSpec.Empty;

        Assert.True(tuple.IsEmptyTuple);
    }

    [Fact]
    public void TupleTypeSpec_WithElements_IsNotEmptyTuple()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int")
        });

        Assert.False(tuple.IsEmptyTuple);
    }

    [Fact]
    public void TupleTypeSpec_WithLabels_PreservesLabels()
    {
        var intType = new NamedTypeSpec("Swift.Int") { TypeLabel = "x" };
        var boolType = new NamedTypeSpec("Swift.Bool") { TypeLabel = "y" };
        var tuple = new TupleTypeSpec(new List<TypeSpec> { intType, boolType });

        Assert.Equal("x", tuple.Elements[0].TypeLabel);
        Assert.Equal("y", tuple.Elements[1].TypeLabel);
    }

    [Fact]
    public void TupleTypeSpec_ElementCount_IsCorrect()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Bool")
        });

        Assert.Equal(3, tuple.Elements.Count);
    }

    #endregion

    #region Bound Generic Type Tests

    [Fact]
    public void BoundGeneric_SwiftArray_HasGenericParameter()
    {
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        Assert.Single(arrayType.GenericParameters);
    }

    [Fact]
    public void BoundGeneric_SwiftDictionary_HasTwoGenericParameters()
    {
        var dictType = new NamedTypeSpec("Swift.Dictionary");
        dictType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        Assert.Equal(2, dictType.GenericParameters.Count);
    }

    [Fact]
    public void BoundGeneric_SwiftOptional_HasGenericParameter()
    {
        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        Assert.Single(optionalType.GenericParameters);
    }

    [Fact]
    public void BoundGeneric_NestedGeneric_HasNestedParameters()
    {
        // Array<Optional<String>>
        var innerOptional = new NamedTypeSpec("Swift.Optional");
        innerOptional.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var outerArray = new NamedTypeSpec("Swift.Array");
        outerArray.GenericParameters.Add(innerOptional);

        Assert.Single(outerArray.GenericParameters);
        var innerType = outerArray.GenericParameters[0] as NamedTypeSpec;
        Assert.NotNull(innerType);
        Assert.Single(innerType.GenericParameters);
    }

    #endregion

    #region Visibility Tests

    [Fact]
    public void MethodDecl_PublicVisibility_IsPublic()
    {
        var methodDecl = CreateMethodDecl("publicMethod");

        Assert.Equal(Visibility.Public, methodDecl.Visibility);
    }

    [Fact]
    public void MethodDecl_PrivateVisibility_CanBeSet()
    {
        var methodDecl = CreateMethodDecl("privateMethod");
        methodDecl.Visibility = Visibility.Private;

        Assert.Equal(Visibility.Private, methodDecl.Visibility);
    }

    #endregion

    #region Mangled Name Tests

    [Fact]
    public void MethodDecl_HasMangledName()
    {
        var methodDecl = CreateMethodDecl("doSomething");

        Assert.NotEmpty(methodDecl.MangledName);
    }

    [Fact]
    public void MethodDecl_MangledNameContainsDollarSign()
    {
        var methodDecl = CreateMethodDecl("doSomething");

        Assert.Contains("$s", methodDecl.MangledName);
    }

    #endregion

    #region TypeRecord Tests

    [Fact]
    public void TypeRecord_FrozenType_HasFrozenFlag()
    {
        var typeRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FrozenStruct"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FrozenStruct"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        };

        Assert.True((typeRecord.Flags & TypeRecordFlags.Frozen) != 0);
    }

    [Fact]
    public void TypeRecord_NonFrozenType_NoFrozenFlag()
    {
        var typeRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "NonFrozenStruct"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.NonFrozenStruct"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Struct
        };

        Assert.False((typeRecord.Flags & TypeRecordFlags.Frozen) != 0);
    }

    [Fact]
    public void TypeRecord_ClassKind_IsClass()
    {
        var typeRecord = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Class
        };

        Assert.Equal(TypeRecordKind.Class, typeRecord.Kind);
    }

    #endregion

    #region PInvoke Enum Parameter Tests

    [Theory]
    [InlineData(TypeRecordFlags.Frozen, "EnumSafeHandle")]
    [InlineData(TypeRecordFlags.None, "EnumSafeHandle")]
    public void PInvokeSignature_EnumParameter_UsesEnumSafeHandle(TypeRecordFlags enumFlags, string expectedType)
    {
        var typeDatabase = CreateTypeDatabaseWithEnum(enumFlags);
        var moduleDecl = CreateModuleDeclFull();
        var parentDecl = CreateClassDeclFull("Loader", moduleDecl);
        var method = CreateMethodDeclFull("process", parentDecl, moduleDecl);
        method.CSSignature.Add(CreateArgumentDeclFull("variant", new NamedTypeSpec("TestModule.Variant"), moduleDecl));

        var env = new MethodEnvironment(method, typeDatabase);
        var builder = new PInvokeSignatureBuilder(env);
        builder.HandleReturnType();
        builder.HandleArguments();
        var signature = builder.Build();

        var variantParam = signature.Parameters.First(p => p.Name == "variant");
        Assert.Equal(expectedType, variantParam.Type);
        // EnumSafeHandle maps to IntPtr in the actual P/Invoke signature
        Assert.Contains("IntPtr", variantParam.SignatureString());
    }

    [Theory]
    [InlineData(TypeRecordFlags.Frozen)]
    [InlineData(TypeRecordFlags.None)]
    public void PInvokeSignature_EnumParameter_CallArgExtractsPayloadHandle(TypeRecordFlags enumFlags)
    {
        var typeDatabase = CreateTypeDatabaseWithEnum(enumFlags);
        var moduleDecl = CreateModuleDeclFull();
        var parentDecl = CreateClassDeclFull("Loader", moduleDecl);
        var method = CreateMethodDeclFull("process", parentDecl, moduleDecl);
        method.CSSignature.Add(CreateArgumentDeclFull("variant", new NamedTypeSpec("TestModule.Variant"), moduleDecl));

        var env = new MethodEnvironment(method, typeDatabase);
        var signatureHandler = new SignatureHandler(env);
        var pInvokeSignature = signatureHandler.GetPInvokeSignature();

        // The call argument should extract the handle from the payload
        Assert.Contains("variant.Payload.DangerousGetHandle()", pInvokeSignature.CallArgumentsString());
    }

    [Fact]
    public void PInvokeSignature_FrozenEnumParameter_NeverUsesManagedType()
    {
        var typeDatabase = CreateTypeDatabaseWithEnum(TypeRecordFlags.Frozen);
        var moduleDecl = CreateModuleDeclFull();
        var parentDecl = CreateClassDeclFull("Loader", moduleDecl);
        var method = CreateMethodDeclFull("process", parentDecl, moduleDecl);
        method.CSSignature.Add(CreateArgumentDeclFull("variant", new NamedTypeSpec("TestModule.Variant"), moduleDecl));

        var env = new MethodEnvironment(method, typeDatabase);
        var signatureHandler = new SignatureHandler(env);
        var pInvokeSignature = signatureHandler.GetPInvokeSignature();

        // Must not contain the managed enum type name in the P/Invoke signature
        Assert.DoesNotContain("Swift.TestModule.Variant", pInvokeSignature.ParametersString());
    }

    [Theory]
    [InlineData(TypeRecordFlags.Frozen)]
    [InlineData(TypeRecordFlags.None)]
    public void PInvokeSignature_AsyncEnumParameter_UsesIntPtrFromNonFrozen(TypeRecordFlags enumFlags)
    {
        var typeDatabase = CreateTypeDatabaseWithEnum(enumFlags);
        var moduleDecl = CreateModuleDeclFull();
        var parentDecl = CreateClassDeclFull("Loader", moduleDecl);
        var method = CreateMethodDeclFull("fetch", parentDecl, moduleDecl);
        method.IsAsync = true;
        method.CSSignature.Add(CreateArgumentDeclFull("variant", new NamedTypeSpec("TestModule.Variant"), moduleDecl));

        var env = new MethodEnvironment(method, typeDatabase);
        var builder = new PInvokeSignatureBuilder(env);
        builder.HandleReturnType();
        builder.HandleArguments();
        var signature = builder.Build();

        var variantParam = signature.Parameters.First(p => p.Name == "variant");
        // Async enum params use IntPtrFromNonFrozen for copy-buffer lifetime management
        Assert.Equal("IntPtrFromNonFrozen", variantParam.Type);
        Assert.Contains("IntPtr", variantParam.SignatureString());
    }

    #endregion

    #region Helper Methods

    private static MethodDecl CreateMethodDecl(
        string name,
        bool isStatic = false,
        bool isConstructor = false,
        bool throws = false,
        bool isAsync = false)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}",
            MethodType = isStatic ? MethodType.Static : MethodType.Instance,
            IsConstructor = isConstructor,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type placeholder (void)
                CreateArgumentDecl("", TupleTypeSpec.Empty)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = throws,
            IsAsync = isAsync,
            Visibility = Visibility.Public
        };
    }

    private static ArgumentDecl CreateArgumentDecl(string name, TypeSpec typeSpec)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static TypeRecord CreatePrimitiveTypeRecord(string swiftType, string csharpType)
    {
        var parts = csharpType.Split('.');
        var ns = parts.Length > 1 ? string.Join(".", parts.Take(parts.Length - 1)) : "";
        var typeName = parts.Last();

        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ns, typeName),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftType),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        };
    }

    private static TypeRecord CreateTypeRecord(string swiftType, string csharpType)
    {
        var parts = csharpType.Split('.');
        var ns = parts.Length > 1 ? string.Join(".", parts.Take(parts.Length - 1)) : "";
        var typeName = parts.Last();

        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ns, typeName),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftType),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        };
    }

    private static GenericArgumentDecl CreateGenericArgumentDecl(string name)
    {
        return new GenericArgumentDecl(
            TypeName: name,
            SugaredTypeName: name,
            GenericConformances: new List<GenericParameterConformance>(),
            AssosiatedTypeConformances: new List<GenericParameterConformance>()
        );
    }

    private static GenericArgumentDecl CreateGenericArgumentDeclWithConformance(string name, string conformance)
    {
        return new GenericArgumentDecl(
            TypeName: name,
            SugaredTypeName: name,
            GenericConformances: new List<GenericParameterConformance>
            {
                new GenericParameterConformance(
                    Path: new[] { name },
                    ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(conformance),
                    Kind: ConformanceKind.Protocol
                )
            },
            AssosiatedTypeConformances: new List<GenericParameterConformance>()
        );
    }

    private static StructDecl CreateStructDecl(string name, string moduleName = "TestModule")
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}VN",
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
            MetadataAccessor = ""
        };
    }

    private static TypeDatabase CreateTypeDatabaseWithEnum(TypeRecordFlags enumFlags)
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

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Variant"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
                MetadataAccessor = "$s10TestModule7VariantOMa",
                Flags = enumFlags,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDeclFull()
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

    private static ClassDecl CreateClassDeclFull(string name, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}CN",
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
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static MethodDecl CreateMethodDeclFull(string name, ClassDecl parentDecl, ModuleDecl moduleDecl)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule6LoaderC{name.Length}{name}SiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgumentDeclFull("", TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static ArgumentDecl CreateArgumentDeclFull(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
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

    #endregion
}
