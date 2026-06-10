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
    public void GetSwiftTypeName_TupleWithLabels_PreservesLabels()
    {
        var elem0 = new NamedTypeSpec("Foundation.Data");
        elem0.TypeLabel = "data";
        var elem1 = new NamedTypeSpec("Swift.Optional");
        elem1.GenericParameters.Add(new NamedTypeSpec("Foundation.URLResponse"));
        elem1.TypeLabel = "response";

        var tuple = new TupleTypeSpec(new List<TypeSpec> { elem0, elem1 });

        var result = SwiftTypeNameHelper.GetSwiftTypeName(tuple);

        Assert.Equal("(data: Foundation.Data, response: (Foundation.URLResponse)?)", result);
    }

    [Fact]
    public void GetSwiftTypeName_TupleWithoutLabels_OmitsLabels()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });

        var result = SwiftTypeNameHelper.GetSwiftTypeName(tuple);

        Assert.Equal("(Swift.Int, Swift.String)", result);
    }

    [Fact]
    public void GetSwiftTypeName_TupleWithEmptyLabels_OmitsLabels()
    {
        var elem0 = new NamedTypeSpec("Swift.Int");
        elem0.TypeLabel = "";
        var elem1 = new NamedTypeSpec("Swift.String");
        elem1.TypeLabel = "";

        var tuple = new TupleTypeSpec(new List<TypeSpec> { elem0, elem1 });

        var result = SwiftTypeNameHelper.GetSwiftTypeName(tuple);

        // Empty labels must not render as ": Type" — that's invalid Swift
        Assert.Equal("(Swift.Int, Swift.String)", result);
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

    [Fact]
    public void GetSwiftTypeName_ClosureWithEmptyTupleArgs_RendersParentheses()
    {
        // P/SDK: Closures with no parameters like () -> Void were rendered as
        // "Void -> Void" which is invalid Swift. Must be "() -> Void".
        var closureType = new ClosureTypeSpec(
            arguments: TupleTypeSpec.Empty,
            returnType: TupleTypeSpec.Empty);

        var result = SwiftTypeNameHelper.GetSwiftTypeName(closureType);

        Assert.Contains("()", result);
        Assert.Contains("-> Void", result);
        // Must NOT start with "Void ->" (the old broken pattern)
        Assert.DoesNotMatch(result, @"^Void\s*->");
    }

    [Fact]
    public void GetSwiftTypeName_ClosureWithEmptyTupleArgs_IntReturn_RendersParentheses()
    {
        // () -> Int should render as "() -> Swift.Int", not "Void -> Swift.Int"
        var closureType = new ClosureTypeSpec(
            arguments: TupleTypeSpec.Empty,
            returnType: new NamedTypeSpec("Swift.Int"));

        var result = SwiftTypeNameHelper.GetSwiftTypeName(closureType);

        Assert.StartsWith("()", result);
        Assert.Contains("-> Swift.Int", result);
    }

    [Fact]
    public void GetSwiftTypeName_ClosureWithSingleArg_StillWrapsInParens()
    {
        // (Int) -> String — single non-tuple arg should be wrapped in parens
        var closureType = new ClosureTypeSpec(
            arguments: new NamedTypeSpec("Swift.Int"),
            returnType: new NamedTypeSpec("Swift.String"));

        var result = SwiftTypeNameHelper.GetSwiftTypeName(closureType);

        Assert.StartsWith("(Swift.Int)", result);
        Assert.Contains("-> Swift.String", result);
    }

    [Fact]
    public void GetSwiftTypeName_EscapingClosureWithEmptyArgs_ExcludesEscaping()
    {
        // @escaping is a calling convention attribute only valid on function parameters,
        // not on property types, return types, or metatype expressions.
        // GetSwiftTypeName strips it since this helper is used for property/metatype contexts.
        var closureType = new ClosureTypeSpec(
            arguments: TupleTypeSpec.Empty,
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var result = SwiftTypeNameHelper.GetSwiftTypeName(closureType);

        Assert.DoesNotContain("@escaping", result);
        Assert.Contains("()", result);
        Assert.DoesNotMatch(result, @"Void\s*->");
    }

    #endregion

    #region Optional Closure Type Tests (EC-15)

    [Fact]
    public void GetSwiftTypeName_OptionalClosure_RendersOptionalSyntax()
    {
        // Swift.Optional<(SomeClass) -> Void> should render as ((SomeClass) -> Void)?
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("TestModule.SomeClass") }),
            returnType: TupleTypeSpec.Empty);

        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(closureType);

        var result = SwiftTypeNameHelper.GetSwiftTypeName(optionalType);

        Assert.Contains("TestModule.SomeClass", result);
        Assert.Contains("-> Void", result);
        Assert.EndsWith(")?", result);
        Assert.DoesNotContain("@escaping", result);
    }

    [Fact]
    public void GetSwiftTypeName_MainActorClosure_PreservesMainActor()
    {
        // @MainActor (X) -> Void should preserve the @MainActor attribute
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("MainActor"));

        var result = SwiftTypeNameHelper.GetSwiftTypeName(closureType);

        Assert.Contains("@MainActor", result);
        Assert.StartsWith("@MainActor", result);
        Assert.Contains("-> Void", result);
    }

    [Fact]
    public void GetSwiftTypeName_SendableClosure_PreservesSendable()
    {
        // @Sendable (X) -> Void should preserve the @Sendable attribute
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("Sendable"));

        var result = SwiftTypeNameHelper.GetSwiftTypeName(closureType);

        Assert.Contains("@Sendable", result);
        Assert.StartsWith("@Sendable", result);
    }

    [Fact]
    public void GetSwiftTypeName_MainActorSendableClosure_PreservesBothAttributes()
    {
        // @MainActor @Sendable (X) -> Void should preserve both attributes
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("MainActor"));
        closureType.Attributes.Add(new TypeSpecAttribute("Sendable"));

        var result = SwiftTypeNameHelper.GetSwiftTypeName(closureType);

        Assert.Contains("@MainActor", result);
        Assert.Contains("@Sendable", result);
        Assert.DoesNotContain("@escaping", result);
    }

    [Fact]
    public void GetSwiftTypeName_EscapingMainActorClosure_ExcludesEscapingPreservesMainActor()
    {
        // @escaping @MainActor (X) -> Void should drop @escaping but keep @MainActor
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        closureType.Attributes.Add(new TypeSpecAttribute("MainActor"));

        var result = SwiftTypeNameHelper.GetSwiftTypeName(closureType);

        Assert.Contains("@MainActor", result);
        Assert.DoesNotContain("@escaping", result);
    }

    [Fact]
    public void GetSwiftTypeName_OptionalMainActorClosure_PreservesMainActor()
    {
        // Swift.Optional<@MainActor (T) -> ()> where T is a class parameter should render as
        // (@MainActor (T) -> Void)?
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("TestModule.SomeClass") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("MainActor"));

        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(closureType);

        var result = SwiftTypeNameHelper.GetSwiftTypeName(optionalType);

        Assert.Contains("@MainActor", result);
        Assert.Contains("TestModule.SomeClass", result);
        Assert.Contains("-> Void", result);
        Assert.EndsWith(")?", result);
    }

    [Fact]
    public void GetSwiftTypeNameForMetatype_OptionalClosure_UsesOptionalGenericSyntax()
    {
        // For metatype access, Optional<(X) -> Y> should use Optional<(X) -> Y>.self
        // instead of ((X) -> Y)?.self to avoid Swift parser ambiguity
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);

        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(closureType);

        var result = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(optionalType);

        // Should use Optional<...> syntax, not (...)? syntax
        Assert.StartsWith("Optional<", result);
        Assert.EndsWith(">", result);
        Assert.DoesNotContain("?", result);
    }

    [Fact]
    public void GetSwiftTypeNameForMetatype_OptionalMainActorClosure_UsesOptionalGenericSyntax()
    {
        // For metatype access, Optional<@MainActor (X) -> Y> should use
        // Optional<@MainActor (X) -> Y>.self
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("TestModule.SomeClass") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("MainActor"));

        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(closureType);

        var result = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(optionalType);

        Assert.StartsWith("Optional<", result);
        Assert.Contains("@MainActor", result);
        Assert.DoesNotContain("?", result);
    }

    [Fact]
    public void GetSwiftTypeNameForMetatype_OptionalNonClosure_PreservesQuestionMarkSyntax()
    {
        // Non-closure optionals should still use the regular (Type)? syntax for metatype
        var optionalType = new NamedTypeSpec("Swift.Optional");
        optionalType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var result = SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(optionalType);

        // Should use (Type)? syntax, not Optional<Type>
        Assert.Equal("(Swift.Int)?", result);
    }

    [Fact]
    public void GetSwiftTypeName_AutoclosureClosure_ExcludesAutoclosure()
    {
        // @autoclosure is a calling convention attribute, not a type attribute
        var closureType = new ClosureTypeSpec(
            arguments: TupleTypeSpec.Empty,
            returnType: new NamedTypeSpec("Swift.Bool"));
        closureType.Attributes.Add(new TypeSpecAttribute("autoclosure"));

        var result = SwiftTypeNameHelper.GetSwiftTypeName(closureType);

        Assert.DoesNotContain("@autoclosure", result);
        Assert.Contains("-> Swift.Bool", result);
    }

    #endregion
}
