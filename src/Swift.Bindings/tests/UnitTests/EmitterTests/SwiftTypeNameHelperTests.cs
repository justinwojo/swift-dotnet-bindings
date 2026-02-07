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
}
