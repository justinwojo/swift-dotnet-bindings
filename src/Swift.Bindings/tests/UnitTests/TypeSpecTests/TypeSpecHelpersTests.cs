// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for TypeSpecHelpers.
/// </summary>
public class TypeSpecHelpersTests
{
    #region IsGenericTypeParameter Tests

    [Theory]
    [InlineData("τ_0_0")]
    [InlineData("τ_0_1")]
    [InlineData("τ_1_0")]
    [InlineData("τ_2_3")]
    public void IsGenericTypeParameter_SwiftInternalNotation_ReturnsTrue(string typeName)
    {
        Assert.True(TypeSpecHelpers.IsGenericTypeParameter(typeName));
    }

    [Theory]
    [InlineData("T")]
    [InlineData("U")]
    [InlineData("V")]
    [InlineData("W")]
    [InlineData("E")]
    [InlineData("K")]
    [InlineData("R")]
    [InlineData("S")]
    public void IsGenericTypeParameter_SingleLetterParams_ReturnsTrue(string typeName)
    {
        Assert.True(TypeSpecHelpers.IsGenericTypeParameter(typeName));
    }

    [Theory]
    [InlineData("Element")]
    [InlineData("Key")]
    [InlineData("Value")]
    [InlineData("Index")]
    [InlineData("Result")]
    [InlineData("Failure")]
    [InlineData("Success")]
    public void IsGenericTypeParameter_CommonNamedParams_ReturnsTrue(string typeName)
    {
        Assert.True(TypeSpecHelpers.IsGenericTypeParameter(typeName));
    }

    [Theory]
    [InlineData("T0")]
    [InlineData("T1")]
    [InlineData("T2")]
    [InlineData("U0")]
    public void IsGenericTypeParameter_NumberedParams_ReturnsTrue(string typeName)
    {
        Assert.True(TypeSpecHelpers.IsGenericTypeParameter(typeName));
    }

    [Theory]
    [InlineData("Swift.Int")]
    [InlineData("Swift.String")]
    [InlineData("Foundation.URL")]
    [InlineData("MyModule.MyClass")]
    public void IsGenericTypeParameter_ModuleQualifiedTypes_ReturnsFalse(string typeName)
    {
        Assert.False(TypeSpecHelpers.IsGenericTypeParameter(typeName));
    }

    [Theory]
    [InlineData("SomeVeryLongTypeName")]
    [InlineData("CustomType")]
    [InlineData("MyProtocol")]
    public void IsGenericTypeParameter_LongNonQualifiedTypes_ReturnsFalse(string typeName)
    {
        Assert.False(TypeSpecHelpers.IsGenericTypeParameter(typeName));
    }

    [Fact]
    public void IsGenericTypeParameter_NamedTypeSpec_DelegatesToStringMethod()
    {
        var genericParam = new NamedTypeSpec("τ_0_0");
        var regularType = new NamedTypeSpec("Swift.Int");

        Assert.True(TypeSpecHelpers.IsGenericTypeParameter(genericParam));
        Assert.False(TypeSpecHelpers.IsGenericTypeParameter(regularType));
    }

    [Fact]
    public void IsGenericTypeParameter_NonNamedTypeSpec_ReturnsFalse()
    {
        var tupleType = new TupleTypeSpec();
        Assert.False(TypeSpecHelpers.IsGenericTypeParameter(tupleType));
    }

    #endregion
}
