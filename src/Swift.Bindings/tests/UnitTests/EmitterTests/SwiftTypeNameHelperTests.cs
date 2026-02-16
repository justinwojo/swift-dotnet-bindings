// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SwiftTypeNameHelper — Swift type name rendering and metatype parenthesization.
/// </summary>
public class SwiftTypeNameHelperTests
{
    #region GetSwiftTypeNameForMetatype Tests

    [Fact]
    public void GetSwiftTypeNameForMetatype_FunctionType_WrapsInParentheses()
    {
        // (Int) -> String should become ((Int) -> String) for .self usage
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: new NamedTypeSpec("Swift.String"));

        var result = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(closureType);

        Assert.StartsWith("(", result);
        Assert.EndsWith(")", result);
        Assert.Contains("->", result);
    }

    [Fact]
    public void GetSwiftTypeNameForMetatype_FunctionTypeWithOptionalReturn_WrapsInParentheses()
    {
        // (ArraySlice<UInt8>) -> (Array<UInt8>)? should become ((...) -> (...)?) for .self
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec>
            {
                new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.UInt8"))
            }),
            returnType: new NamedTypeSpec("Swift.Optional",
                new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.UInt8"))
            ));

        var result = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(closureType);

        // Must be wrapped: ((...) -> (...)?)
        Assert.StartsWith("(", result);
        Assert.EndsWith(")", result);
        Assert.Contains("->", result);
    }

    [Fact]
    public void GetSwiftTypeNameForMetatype_SimpleNamedType_NoExtraParentheses()
    {
        var namedType = new NamedTypeSpec("Swift.Int");

        var result = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(namedType);

        Assert.Equal("Swift.Int", result);
    }

    [Fact]
    public void GetSwiftTypeNameForMetatype_ExistentialType_WrapsInParentheses()
    {
        // "any Protocol" needs (any Protocol).self
        var existentialType = new NamedTypeSpec("TestModule.MyProtocol");
        existentialType.IsAny = true;

        var result = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(existentialType);

        Assert.Equal("(any TestModule.MyProtocol)", result);
    }

    [Fact]
    public void GetSwiftTypeNameForMetatype_ThrowingFunctionType_WrapsInParentheses()
    {
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: new NamedTypeSpec("Swift.String"));
        closureType.Throws = true;

        var result = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(closureType);

        Assert.StartsWith("(", result);
        Assert.EndsWith(")", result);
        Assert.Contains("throws ->", result);
    }

    #endregion

    #region GetSwiftTypeName Tests

    [Fact]
    public void GetSwiftTypeName_NullTypeSpec_ReturnsAny()
    {
        var result = SwiftTypeNameHelper.GetSwiftTypeName(null);
        Assert.Equal("Any", result);
    }

    [Fact]
    public void GetSwiftTypeName_SimpleNamed_ReturnsName()
    {
        var result = SwiftTypeNameHelper.GetSwiftTypeName(new NamedTypeSpec("Swift.Int"));
        Assert.Equal("Swift.Int", result);
    }

    [Fact]
    public void GetSwiftTypeName_GenericNamed_ReturnsWithAngleBrackets()
    {
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var result = SwiftTypeNameHelper.GetSwiftTypeName(arrayType);

        Assert.Equal("Swift.Array<Swift.Int>", result);
    }

    [Fact]
    public void GetSwiftTypeName_Optional_ReturnsQuestionMark()
    {
        var optType = new NamedTypeSpec("Swift.Optional");
        optType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var result = SwiftTypeNameHelper.GetSwiftTypeName(optType);

        Assert.Equal("(Swift.String)?", result);
    }

    [Fact]
    public void GetSwiftTypeName_EmptyTuple_ReturnsVoid()
    {
        var result = SwiftTypeNameHelper.GetSwiftTypeName(TupleTypeSpec.Empty);
        Assert.Equal("Void", result);
    }

    [Fact]
    public void GetSwiftTypeName_Closure_ReturnsArrowSyntax()
    {
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            new NamedTypeSpec("Swift.String"));

        var result = SwiftTypeNameHelper.GetSwiftTypeName(closureType);

        Assert.Contains("->", result);
        Assert.Contains("Swift.Int", result);
        Assert.Contains("Swift.String", result);
    }

    [Fact]
    public void GetSwiftTypeName_ProtocolList_ReturnsComposition()
    {
        var protocols = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.P1"),
            new NamedTypeSpec("TestModule.P2")
        });

        var result = SwiftTypeNameHelper.GetSwiftTypeName(protocols);

        Assert.Contains("any", result);
        Assert.Contains("&", result);
    }

    #endregion
}
