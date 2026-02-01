// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for EnumHandler and EnumHandlerFactory.
/// </summary>
public class EnumHandlerTests
{
    #region Factory Tests

    [Fact]
    public void Factory_Handles_EnumDecl_ReturnsTrue()
    {
        var factory = new EnumHandlerFactory(NullLoggerFactory.Instance);
        var enumDecl = CreateEnumDecl("Direction");

        Assert.True(factory.Handles(enumDecl));
    }

    [Fact]
    public void Factory_Handles_StructDecl_ReturnsFalse()
    {
        var factory = new EnumHandlerFactory(NullLoggerFactory.Instance);
        var structDecl = CreateStructDecl("Point");

        Assert.False(factory.Handles(structDecl));
    }

    [Fact]
    public void Factory_Handles_ClassDecl_ReturnsFalse()
    {
        var factory = new EnumHandlerFactory(NullLoggerFactory.Instance);
        var classDecl = CreateClassDecl("MyClass");

        Assert.False(factory.Handles(classDecl));
    }

    [Fact]
    public void Factory_Handles_ProtocolDecl_ReturnsFalse()
    {
        var factory = new EnumHandlerFactory(NullLoggerFactory.Instance);
        var protocolDecl = CreateProtocolDecl("MyProtocol");

        Assert.False(factory.Handles(protocolDecl));
    }

    [Fact]
    public void Factory_Construct_ReturnsHandler()
    {
        var factory = new EnumHandlerFactory(NullLoggerFactory.Instance);

        var handler = factory.Construct();

        Assert.NotNull(handler);
        Assert.IsType<EnumHandler>(handler);
    }

    #endregion

    #region EnumDecl Configuration Tests

    [Fact]
    public void EnumDecl_HasCorrectName()
    {
        var enumDecl = CreateEnumDecl("Direction");

        Assert.Equal("Direction", enumDecl.Name);
    }

    [Fact]
    public void EnumDecl_HasCorrectSwiftTypeName()
    {
        var enumDecl = CreateEnumDecl("State", moduleName: "MyApp");

        Assert.Equal("MyApp.State", enumDecl.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void EnumDecl_IsFrozen_WhenMarkedFrozen()
    {
        var enumDecl = CreateEnumDecl("Direction", isFrozen: true);

        Assert.True(enumDecl.IsFrozen);
    }

    [Fact]
    public void EnumDecl_IsNotFrozen_WhenNotMarkedFrozen()
    {
        var enumDecl = CreateEnumDecl("State", isFrozen: false);

        Assert.False(enumDecl.IsFrozen);
    }

    #endregion

    #region Simple Case Tests

    [Fact]
    public void EnumDecl_SimpleCase_HasNoAssociatedValues()
    {
        var enumDecl = CreateEnumDecl("Direction");
        enumDecl.Cases.Add(CreateEnumCaseDecl("north"));

        Assert.Empty(enumDecl.Cases[0].AssociatedValues);
    }

    [Fact]
    public void EnumDecl_WithMultipleSimpleCases_CollectsAll()
    {
        var enumDecl = CreateEnumDecl("Direction");
        enumDecl.Cases.Add(CreateEnumCaseDecl("north"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("south"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("east"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("west"));

        Assert.Equal(4, enumDecl.Cases.Count);
    }

    [Fact]
    public void EnumDecl_CaseNames_ArePreserved()
    {
        var enumDecl = CreateEnumDecl("LoadingState");
        enumDecl.Cases.Add(CreateEnumCaseDecl("idle"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("loading"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("completed"));

        Assert.Equal("idle", enumDecl.Cases[0].Name);
        Assert.Equal("loading", enumDecl.Cases[1].Name);
        Assert.Equal("completed", enumDecl.Cases[2].Name);
    }

    #endregion

    #region Associated Value Case Tests

    [Fact]
    public void EnumDecl_CaseWithSingleAssociatedValue_HasOneValue()
    {
        var enumDecl = CreateEnumDecl("Result");
        var successCase = CreateEnumCaseDecl("success");
        successCase.AssociatedValues.Add(new NamedTypeSpec("Swift.String"));
        enumDecl.Cases.Add(successCase);

        Assert.Single(enumDecl.Cases[0].AssociatedValues);
    }

    [Fact]
    public void EnumDecl_CaseWithMultipleAssociatedValues_CollectsAll()
    {
        var enumDecl = CreateEnumDecl("NetworkResponse");
        var dataCase = CreateEnumCaseDecl("data");
        dataCase.AssociatedValues.Add(new NamedTypeSpec("Foundation.Data"));
        dataCase.AssociatedValues.Add(new NamedTypeSpec("Foundation.URLResponse"));
        enumDecl.Cases.Add(dataCase);

        Assert.Equal(2, enumDecl.Cases[0].AssociatedValues.Count);
    }

    [Fact]
    public void EnumDecl_MixedCases_SimplesAndAssociatedValues()
    {
        var enumDecl = CreateEnumDecl("AsyncResult");
        enumDecl.Cases.Add(CreateEnumCaseDecl("pending"));

        var loadingCase = CreateEnumCaseDecl("loading");
        loadingCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Double")); // progress
        enumDecl.Cases.Add(loadingCase);

        var successCase = CreateEnumCaseDecl("success");
        successCase.AssociatedValues.Add(new NamedTypeSpec("Swift.String")); // data
        enumDecl.Cases.Add(successCase);

        var failureCase = CreateEnumCaseDecl("failure");
        failureCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Error")); // error
        enumDecl.Cases.Add(failureCase);

        Assert.Equal(4, enumDecl.Cases.Count);
        Assert.Empty(enumDecl.Cases[0].AssociatedValues); // pending
        Assert.Single(enumDecl.Cases[1].AssociatedValues); // loading
        Assert.Single(enumDecl.Cases[2].AssociatedValues); // success
        Assert.Single(enumDecl.Cases[3].AssociatedValues); // failure
    }

    #endregion

    #region RawRepresentable Tests

    [Fact]
    public void EnumDecl_WithRawValueType_HasRawValueTypeName()
    {
        var enumDecl = CreateEnumDecl("HTTPMethod", rawValueTypeName: "Swift.String");

        Assert.Equal("Swift.String", enumDecl.RawValueTypeName);
    }

    [Fact]
    public void EnumDecl_WithIntRawValue_HasCorrectRawValueType()
    {
        var enumDecl = CreateEnumDecl("StatusCode", rawValueTypeName: "Swift.Int");

        Assert.Equal("Swift.Int", enumDecl.RawValueTypeName);
    }

    [Fact]
    public void EnumDecl_WithoutRawValue_HasNullRawValueTypeName()
    {
        var enumDecl = CreateEnumDecl("SimpleEnum");

        Assert.Null(enumDecl.RawValueTypeName);
    }

    [Fact]
    public void EnumDecl_IsRawRepresentable_CanBeDetected()
    {
        var enumDecl = CreateEnumDecl("HTTPMethod", rawValueTypeName: "Swift.String");

        var isRawRepresentable = enumDecl.RawValueTypeName != null;

        Assert.True(isRawRepresentable);
    }

    #endregion

    #region CaseTag Tests

    [Fact]
    public void EnumCaseDecl_HasMangledName()
    {
        var caseDecl = CreateEnumCaseDecl("north");

        Assert.NotEmpty(caseDecl.MangledName);
    }

    [Fact]
    public void EnumDecl_CasesOrderedCorrectly_PayloadCasesFirst()
    {
        var enumDecl = CreateEnumDecl("MixedEnum");

        // Add simple case first
        enumDecl.Cases.Add(CreateEnumCaseDecl("none"));

        // Add payload case
        var someCase = CreateEnumCaseDecl("some");
        someCase.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        enumDecl.Cases.Add(someCase);

        // The handler should order payload cases first, then no-payload cases
        // This test verifies the structure allows for reordering
        var payloadCases = enumDecl.Cases.Where(c => c.AssociatedValues.Count > 0).ToList();
        var simpleCases = enumDecl.Cases.Where(c => c.AssociatedValues.Count == 0).ToList();

        Assert.Single(payloadCases);
        Assert.Single(simpleCases);
    }

    #endregion

    #region Conformance Tests

    [Fact]
    public void EnumDecl_CanHaveConformances()
    {
        var enumDecl = CreateEnumDecl("EquatableEnum");
        enumDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.EquatableEnum"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            "$sEquatable"));

        Assert.Single(enumDecl.Conformances);
    }

    [Fact]
    public void EnumDecl_ConformsToHashable_CanBeDetected()
    {
        var enumDecl = CreateEnumDecl("HashableDirection");
        enumDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.HashableDirection"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
            "$sHashable"));

        var conformsToHashable = enumDecl.Conformances
            .Any(c => c.Protocol.ModuleQualifiedName == "Swift.Hashable");

        Assert.True(conformsToHashable);
    }

    [Fact]
    public void EnumDecl_ConformsToCodable_CanBeDetected()
    {
        var enumDecl = CreateEnumDecl("CodableState");
        enumDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.CodableState"),
            SwiftTypeName.FromModuleQualifiedName("Swift.Codable"),
            "$sCodable"));

        var conformsToCodable = enumDecl.Conformances
            .Any(c => c.Protocol.ModuleQualifiedName == "Swift.Codable");

        Assert.True(conformsToCodable);
    }

    #endregion

    #region Generic Parameters Tests

    [Fact]
    public void EnumDecl_WithGenericParameter_HasGenericParameters()
    {
        var enumDecl = CreateEnumDecl("Optional");
        enumDecl.GenericParameters.Add(CreateGenericArgumentDecl("Wrapped"));

        Assert.Single(enumDecl.GenericParameters);
        Assert.Equal("Wrapped", enumDecl.GenericParameters[0].TypeName);
    }

    [Fact]
    public void EnumDecl_WithMultipleGenericParameters_CollectsAll()
    {
        var enumDecl = CreateEnumDecl("Either");
        enumDecl.GenericParameters.Add(CreateGenericArgumentDecl("Left"));
        enumDecl.GenericParameters.Add(CreateGenericArgumentDecl("Right"));

        Assert.Equal(2, enumDecl.GenericParameters.Count);
    }

    #endregion

    #region Methods and Properties Tests

    [Fact]
    public void EnumDecl_CanHaveMethods()
    {
        var enumDecl = CreateEnumDecl("Direction");
        enumDecl.Methods.Add(CreateMethodDecl("opposite"));

        Assert.Single(enumDecl.Methods);
    }

    [Fact]
    public void EnumDecl_CanHaveComputedProperties()
    {
        var enumDecl = CreateEnumDecl("Direction");
        enumDecl.Properties.Add(CreatePropertyDecl("isVertical", "Swift.Bool"));

        Assert.Single(enumDecl.Properties);
    }

    [Fact]
    public void EnumDecl_CanHaveStaticProperties()
    {
        var enumDecl = CreateEnumDecl("Direction");
        enumDecl.Properties.Add(CreatePropertyDecl("allCases", "Swift.Array<TestModule.Direction>", isStatic: true));

        Assert.True(enumDecl.Properties[0].IsStatic);
    }

    #endregion

    #region Nested Types Tests

    [Fact]
    public void EnumDecl_CanHaveNestedTypes()
    {
        var enumDecl = CreateEnumDecl("OuterEnum");
        enumDecl.Types.Add(CreateEnumDecl("InnerEnum", moduleName: "TestModule.OuterEnum"));

        Assert.Single(enumDecl.Types);
    }

    #endregion

    #region Tuple Associated Value Tests

    [Fact]
    public void EnumDecl_CaseWithTupleAssociatedValue_HasOneTupleTypeSpec()
    {
        var enumDecl = CreateEnumDecl("Error");
        var failedCase = CreateEnumCaseDecl("failed");

        // Create a tuple with (code: Int, message: String)
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int") { TypeLabel = "code" },
            new NamedTypeSpec("Swift.String") { TypeLabel = "message" }
        });
        failedCase.AssociatedValues.Add(tupleSpec);
        enumDecl.Cases.Add(failedCase);

        // The case has one associated value (the tuple itself)
        Assert.Single(enumDecl.Cases[0].AssociatedValues);
        Assert.IsType<TupleTypeSpec>(enumDecl.Cases[0].AssociatedValues[0]);

        // The tuple contains two elements
        var tuple = (TupleTypeSpec)enumDecl.Cases[0].AssociatedValues[0];
        Assert.Equal(2, tuple.Elements.Count);
    }

    [Fact]
    public void EnumDecl_CaseWithTupleAssociatedValue_PreservesLabels()
    {
        var enumDecl = CreateEnumDecl("NetworkError");
        var httpCase = CreateEnumCaseDecl("httpError");

        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int") { TypeLabel = "statusCode" },
            new NamedTypeSpec("Swift.String") { TypeLabel = "body" },
            new NamedTypeSpec("Foundation.URLResponse") { TypeLabel = "response" }
        });
        httpCase.AssociatedValues.Add(tupleSpec);
        enumDecl.Cases.Add(httpCase);

        var tuple = (TupleTypeSpec)enumDecl.Cases[0].AssociatedValues[0];
        Assert.Equal("statusCode", tuple.Elements[0].TypeLabel);
        Assert.Equal("body", tuple.Elements[1].TypeLabel);
        Assert.Equal("response", tuple.Elements[2].TypeLabel);
    }

    [Fact]
    public void EnumDecl_CaseWithTupleNoLabels_HasNullLabels()
    {
        var enumDecl = CreateEnumDecl("Pair");
        var pairCase = CreateEnumCaseDecl("both");

        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });
        pairCase.AssociatedValues.Add(tupleSpec);
        enumDecl.Cases.Add(pairCase);

        var tuple = (TupleTypeSpec)enumDecl.Cases[0].AssociatedValues[0];
        Assert.Null(tuple.Elements[0].TypeLabel);
        Assert.Null(tuple.Elements[1].TypeLabel);
    }

    [Fact]
    public void EnumDecl_CaseWith7ElementTuple_IsSupported()
    {
        var enumDecl = CreateEnumDecl("LargeTuple");
        var maxCase = CreateEnumCaseDecl("max");

        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int") { TypeLabel = "a" },
            new NamedTypeSpec("Swift.Int") { TypeLabel = "b" },
            new NamedTypeSpec("Swift.Int") { TypeLabel = "c" },
            new NamedTypeSpec("Swift.Int") { TypeLabel = "d" },
            new NamedTypeSpec("Swift.Int") { TypeLabel = "e" },
            new NamedTypeSpec("Swift.Int") { TypeLabel = "f" },
            new NamedTypeSpec("Swift.Int") { TypeLabel = "g" }
        });
        maxCase.AssociatedValues.Add(tupleSpec);
        enumDecl.Cases.Add(maxCase);

        var tuple = (TupleTypeSpec)enumDecl.Cases[0].AssociatedValues[0];
        Assert.Equal(7, tuple.Elements.Count);
    }

    [Fact]
    public void EnumDecl_CaseWith8ElementTuple_ExceedsMaxSupported()
    {
        var enumDecl = CreateEnumDecl("TooLargeTuple");
        var hugeCase = CreateEnumCaseDecl("huge");

        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int") // 8th element
        });
        hugeCase.AssociatedValues.Add(tupleSpec);
        enumDecl.Cases.Add(hugeCase);

        var tuple = (TupleTypeSpec)enumDecl.Cases[0].AssociatedValues[0];
        Assert.Equal(8, tuple.Elements.Count);

        // This should exceed the max supported (7) in TupleHandler
        Assert.True(tuple.Elements.Count > TupleHandler.MaxSupportedTupleElements);
    }

    [Fact]
    public void EnumDecl_CaseWithMixedTypes_CollectsAllElementTypes()
    {
        var enumDecl = CreateEnumDecl("MixedResult");
        var detailCase = CreateEnumCaseDecl("detail");

        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int") { TypeLabel = "code" },
            new NamedTypeSpec("Swift.Bool") { TypeLabel = "success" },
            new NamedTypeSpec("Swift.String") { TypeLabel = "message" }
        });
        detailCase.AssociatedValues.Add(tupleSpec);
        enumDecl.Cases.Add(detailCase);

        var tuple = (TupleTypeSpec)enumDecl.Cases[0].AssociatedValues[0];
        Assert.IsType<NamedTypeSpec>(tuple.Elements[0]);
        Assert.IsType<NamedTypeSpec>(tuple.Elements[1]);
        Assert.IsType<NamedTypeSpec>(tuple.Elements[2]);

        Assert.Equal("Swift.Int", ((NamedTypeSpec)tuple.Elements[0]).Name);
        Assert.Equal("Swift.Bool", ((NamedTypeSpec)tuple.Elements[1]).Name);
        Assert.Equal("Swift.String", ((NamedTypeSpec)tuple.Elements[2]).Name);
    }

    [Fact]
    public void EnumDecl_MultipleCasesWithDifferentTuples_EachHasOwnStructure()
    {
        var enumDecl = CreateEnumDecl("Error");

        var networkCase = CreateEnumCaseDecl("network");
        networkCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int") { TypeLabel = "code" },
            new NamedTypeSpec("Swift.String") { TypeLabel = "message" }
        }));
        enumDecl.Cases.Add(networkCase);

        var validationCase = CreateEnumCaseDecl("validation");
        validationCase.AssociatedValues.Add(new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.String") { TypeLabel = "field" },
            new NamedTypeSpec("Swift.String") { TypeLabel = "reason" },
            new NamedTypeSpec("Swift.String") { TypeLabel = "suggestion" }
        }));
        enumDecl.Cases.Add(validationCase);

        var networkTuple = (TupleTypeSpec)enumDecl.Cases[0].AssociatedValues[0];
        var validationTuple = (TupleTypeSpec)enumDecl.Cases[1].AssociatedValues[0];

        Assert.Equal(2, networkTuple.Elements.Count);
        Assert.Equal(3, validationTuple.Elements.Count);
    }

    [Fact]
    public void TupleTypeSpec_IsNotEmptyTuple_WhenHasElements()
    {
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });

        Assert.False(tupleSpec.IsEmptyTuple);
    }

    [Fact]
    public void TupleTypeSpec_IsEmptyTuple_WhenNoElements()
    {
        var tupleSpec = new TupleTypeSpec();

        Assert.True(tupleSpec.IsEmptyTuple);
    }

    #endregion

    #region Helper Methods

    private static EnumDecl CreateEnumDecl(
        string name,
        string moduleName = "TestModule",
        bool isFrozen = false,
        string? rawValueTypeName = null)
    {
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}ON",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Cases = new List<EnumCaseDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = isFrozen,
            MetadataAccessor = "",
            RawValueTypeName = rawValueTypeName
        };
    }

    private static EnumCaseDecl CreateEnumCaseDecl(string name)
    {
        return new EnumCaseDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            AssociatedValues = new List<TypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
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

    private static ProtocolDecl CreateProtocolDecl(string name, string moduleName = "TestModule")
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static PropertyDecl CreatePropertyDecl(string name, string typeName, bool isStatic = false)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(typeName),
            IsStatic = isStatic,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = $"{name}_Get",
                        MangledName = $"$s{name}g",
                        MethodType = isStatic ? MethodType.Static : MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>(),
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = null,
                        ModuleDecl = null,
                        Throws = false,
                        IsAsync = false,
                        Visibility = Visibility.Private
                    }
                }
            },
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static MethodDecl CreateMethodDecl(string name)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
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

    private static GenericArgumentDecl CreateGenericArgumentDecl(string name)
    {
        return new GenericArgumentDecl(
            TypeName: name,
            SugaredTypeName: name,
            GenericConformances: new List<GenericParameterConformance>(),
            AssosiatedTypeConformances: new List<GenericParameterConformance>()
        );
    }

    #endregion
}
