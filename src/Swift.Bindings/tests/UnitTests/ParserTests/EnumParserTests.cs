// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for EnumDecl and enum-related parsing functionality.
/// </summary>
public class EnumParserTests
{
    #region EnumDecl Tests

    [Fact]
    public void EnumDecl_DefaultValues_AreCorrect()
    {
        var enumDecl = CreateEnumDecl("TestEnum");

        Assert.Equal("TestEnum", enumDecl.Name);
        Assert.Empty(enumDecl.Cases);
        Assert.Empty(enumDecl.Properties);
        Assert.Empty(enumDecl.Methods);
        Assert.False(enumDecl.HasAssociatedValueCases);
    }

    [Fact]
    public void EnumDecl_WithSimpleCases_HasNoCases()
    {
        var enumDecl = CreateEnumDecl("SimpleEnum");
        enumDecl.Cases.Add(CreateEnumCaseDecl("none"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("some"));

        Assert.Equal(2, enumDecl.Cases.Count);
        Assert.False(enumDecl.HasAssociatedValueCases);
    }

    [Fact]
    public void EnumDecl_WithAssociatedValues_HasAssociatedValueCases()
    {
        var enumDecl = CreateEnumDecl("ResultEnum");
        enumDecl.Cases.Add(CreateEnumCaseDecl("success"));

        var failureCase = CreateEnumCaseDecl("failure");
        failureCase.AssociatedValues.Add(new NamedTypeSpec("Swift.String"));
        enumDecl.Cases.Add(failureCase);

        Assert.True(enumDecl.HasAssociatedValueCases);
    }

    [Fact]
    public void EnumDecl_CanHaveProperties()
    {
        var enumDecl = CreateEnumDecl("EnumWithProperty");
        enumDecl.Properties.Add(CreatePropertyDecl("description", "Swift.String"));

        Assert.Single(enumDecl.Properties);
    }

    [Fact]
    public void EnumDecl_CanHaveMethods()
    {
        var enumDecl = CreateEnumDecl("EnumWithMethod");
        enumDecl.Methods.Add(CreateMethodDecl("process"));

        Assert.Single(enumDecl.Methods);
    }

    #endregion

    #region EnumCaseDecl Tests

    [Fact]
    public void EnumCaseDecl_DefaultValues_AreCorrect()
    {
        var caseDecl = CreateEnumCaseDecl("testCase");

        Assert.Equal("testCase", caseDecl.Name);
        Assert.NotEmpty(caseDecl.MangledName);
        Assert.Empty(caseDecl.AssociatedValues);
        Assert.False(caseDecl.HasAssociatedValues);
    }

    [Fact]
    public void EnumCaseDecl_WithSingleAssociatedValue()
    {
        var caseDecl = CreateEnumCaseDecl("error");
        caseDecl.AssociatedValues.Add(new NamedTypeSpec("Swift.String"));

        Assert.True(caseDecl.HasAssociatedValues);
        Assert.Single(caseDecl.AssociatedValues);
    }

    [Fact]
    public void EnumCaseDecl_WithMultipleAssociatedValues()
    {
        var caseDecl = CreateEnumCaseDecl("pair");
        caseDecl.AssociatedValues.Add(new NamedTypeSpec("Swift.Int"));
        caseDecl.AssociatedValues.Add(new NamedTypeSpec("Swift.String"));

        Assert.True(caseDecl.HasAssociatedValues);
        Assert.Equal(2, caseDecl.AssociatedValues.Count);
    }

    #endregion

    #region RawRepresentable Tests

    [Fact]
    public void EnumDecl_WithoutRawValueType_IsNotRawRepresentable()
    {
        var enumDecl = CreateEnumDecl("Direction");

        Assert.Null(enumDecl.RawValueTypeName);
        Assert.False(enumDecl.IsRawRepresentable);
    }

    [Fact]
    public void EnumDecl_WithIntRawValueType_IsRawRepresentable()
    {
        var enumDecl = CreateEnumDecl("Priority");
        enumDecl.RawValueTypeName = "Int";

        Assert.Equal("Int", enumDecl.RawValueTypeName);
        Assert.True(enumDecl.IsRawRepresentable);
    }

    [Fact]
    public void EnumDecl_WithStringRawValueType_IsRawRepresentable()
    {
        var enumDecl = CreateEnumDecl("HTTPMethod");
        enumDecl.RawValueTypeName = "String";

        Assert.Equal("String", enumDecl.RawValueTypeName);
        Assert.True(enumDecl.IsRawRepresentable);
    }

    [Fact]
    public void EnumDecl_WithEmptyRawValueType_IsNotRawRepresentable()
    {
        var enumDecl = CreateEnumDecl("TestEnum");
        enumDecl.RawValueTypeName = "";

        Assert.False(enumDecl.IsRawRepresentable);
    }

    [Fact]
    public void EnumDecl_RawRepresentable_WithSimpleCases()
    {
        // Simulates Priority enum: Int-backed with simple cases
        var enumDecl = CreateEnumDecl("Priority");
        enumDecl.RawValueTypeName = "Int";
        enumDecl.Cases.Add(CreateEnumCaseDecl("veryLow"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("low"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("normal"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("high"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("veryHigh"));

        Assert.True(enumDecl.IsRawRepresentable);
        Assert.Equal(5, enumDecl.Cases.Count);
        Assert.False(enumDecl.HasAssociatedValueCases);
    }

    #endregion

    #region Complete Enum Tests

    [Fact]
    public void EnumDecl_CompleteEnum_AllPropertiesSet()
    {
        var enumDecl = CreateEnumDecl("ImageProcessingError");

        // Add cases
        enumDecl.Cases.Add(CreateEnumCaseDecl("unknown"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("invalidInput"));

        var customErrorCase = CreateEnumCaseDecl("custom");
        customErrorCase.AssociatedValues.Add(new NamedTypeSpec("Swift.String"));
        enumDecl.Cases.Add(customErrorCase);

        // Add property
        enumDecl.Properties.Add(CreatePropertyDecl("description", "Swift.String"));

        // Add method
        enumDecl.Methods.Add(CreateMethodDecl("isRecoverable"));

        Assert.Equal("ImageProcessingError", enumDecl.Name);
        Assert.Equal(3, enumDecl.Cases.Count);
        Assert.Single(enumDecl.Properties);
        Assert.Single(enumDecl.Methods);
        Assert.True(enumDecl.HasAssociatedValueCases);
    }

    [Fact]
    public void EnumDecl_SimpleEnumWithoutAssociatedValues()
    {
        var enumDecl = CreateEnumDecl("Direction");
        enumDecl.Cases.Add(CreateEnumCaseDecl("north"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("south"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("east"));
        enumDecl.Cases.Add(CreateEnumCaseDecl("west"));

        Assert.Equal(4, enumDecl.Cases.Count);
        Assert.False(enumDecl.HasAssociatedValueCases);
        Assert.All(enumDecl.Cases, c => Assert.False(c.HasAssociatedValues));
    }

    #endregion

    #region Single-Tuple-Payload Detection

    [Theory]
    // Single UNLABELED tuple-typed associated value → double paren around the param clause.
    [InlineData("((Swift.Int32, SwiftBindingsTestLib.BoxedCounter)) -> SwiftBindingsTestLib.TaggedDelivery", true)]
    [InlineData("((Swift.Int, Swift.Bool)) -> Module.Pair", true)]
    // N separate values → single paren.
    [InlineData("(Swift.Int32, Swift.String) -> Module.NetworkResponse", false)]
    // Single non-tuple value → single paren, single element.
    [InlineData("(Swift.Int32) -> Module.Boxed", false)]
    // Labeled single tuple → handled by OuterTupleLabel, not this detector (label breaks the
    // leading-paren shape, so this returns false by design).
    [InlineData("(label: (a: Swift.Int, b: Swift.Int)) -> Module.Labeled", false)]
    // Two values where the FIRST is a tuple → not a single tuple payload.
    [InlineData("((Swift.Int, Swift.Int), Swift.Bool) -> Module.Mixed", false)]
    // No associated values.
    [InlineData("Module.Plain.Type -> Module.Plain", false)]
    [InlineData("", false)]
    public void IsSingleTupleAssociatedValueParam_ClassifiesByParenNesting(string funcPrintedName, bool expected)
    {
        Assert.Equal(expected, SwiftABIParser.IsSingleTupleAssociatedValueParam(funcPrintedName));
    }

    #endregion

    #region Helper Methods

    private static EnumDecl CreateEnumDecl(string name)
    {
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}O",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Cases = new List<EnumCaseDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = string.Empty
        };
    }

    private static EnumCaseDecl CreateEnumCaseDecl(string name)
    {
        return new EnumCaseDecl
        {
            Name = name,
            MangledName = $"$s10TestModule9TestEnumO{name.Length}{name}yA2CmF",
            AssociatedValues = new List<TypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static PropertyDecl CreatePropertyDecl(string name, string typeName)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(typeName),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = $"{name}_Get",
                        MangledName = "",
                        MethodType = MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>(),
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = null,
                        ModuleDecl = null,
                        Throws = false,
                        IsAsync = false,
                        Visibility = Visibility.Public
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

    #endregion
}
