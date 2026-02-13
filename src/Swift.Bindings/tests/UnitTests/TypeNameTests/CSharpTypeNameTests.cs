// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

public class CSharpTypeNameTests
{
    [Theory]
    [InlineData("MyApp", "Int32", "MyApp.Int32")]
    [InlineData("My.Namespace", "MyType", "My.Namespace.MyType")]
    public void FromNamespaceAndName_CreatesCorrectName(string @namespace, string name, string expectedFullName)
    {
        var typeName = CSharpTypeName.FromNamespaceAndName(@namespace, name);

        Assert.Equal(@namespace, typeName.Namespace);
        Assert.Equal(name, typeName.Name);
        Assert.Equal(expectedFullName, typeName.FullyQualifiedName);
        Assert.Equal(expectedFullName, typeName.ToString());
    }

    [Theory]
    [InlineData("System", "Single", "float", "")]
    [InlineData("System", "Int32", "int", "")]
    [InlineData("System", "Boolean", "bool", "")]
    [InlineData("System", "Double", "double", "")]
    [InlineData("System", "Byte", "byte", "")]
    [InlineData("System", "Int64", "long", "")]
    [InlineData("System", "UInt32", "uint", "")]
    [InlineData("System", "UInt64", "ulong", "")]
    [InlineData("MyApp", "Single", "MyApp.Single", "MyApp")]  // Non-System preserved
    public void FromNamespaceAndName_SystemPrimitives_NormalizedToKeywords(string ns, string name, string expectedFqn, string expectedNamespace)
    {
        var typeName = CSharpTypeName.FromNamespaceAndName(ns, name);
        Assert.Equal(expectedFqn, typeName.FullyQualifiedName);
        Assert.Equal(expectedNamespace, typeName.Namespace);
    }

    [Fact]
    public void VoidType_HasCorrectProperties()
    {
        Assert.Equal("", CSharpTypeName.VoidType.Namespace);
        Assert.Equal("", CSharpTypeName.VoidType.Name);
        Assert.Equal("void", CSharpTypeName.VoidType.FullyQualifiedName);
    }

    [Fact]
    public void AnyType_HasCorrectProperties()
    {
        Assert.Equal("Swift", CSharpTypeName.AnyType.Namespace);
        Assert.Equal("AnyType", CSharpTypeName.AnyType.Name);
        Assert.Equal("Swift.AnyType", CSharpTypeName.AnyType.FullyQualifiedName);
    }

    [Theory]
    [InlineData("Swift", "Array<Swift.String>")]
    [InlineData("Swift", "Dictionary<Swift.String, Swift.Int>")]
    public void ThrowsOnGenericTypes(string ns, string name)
    {
        Assert.Throws<ArgumentException>(() => CSharpTypeName.FromNamespaceAndName(ns, name));
    }

    [Theory]
    [InlineData(null, "Test")]
    [InlineData("Test", null)]
    public void ThrowsOnInvalidNullInput(string ns, string name)
    {
        Assert.Throws<ArgumentNullException>(() => CSharpTypeName.FromNamespaceAndName(ns, name));
    }

    [Theory]
    [InlineData("Test", "")]
    [InlineData("", "Test")]
    public void ThrowsOnInvalidEmptyInput(string ns, string name)
    {
        Assert.Throws<ArgumentException>(() => CSharpTypeName.FromNamespaceAndName(ns, name));
    }
}
