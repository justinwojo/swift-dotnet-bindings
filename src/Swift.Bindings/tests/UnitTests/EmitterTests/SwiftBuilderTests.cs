// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SwiftBuilder — type maps, identifier sanitization, and scope blocks.
/// </summary>
public class SwiftBuilderTests
{
    #region Type Map Tests

    [Theory]
    [InlineData("Swift.Int", "nint")]
    [InlineData("Swift.UInt", "nuint")]
    [InlineData("Swift.Int8", "sbyte")]
    [InlineData("Swift.UInt8", "byte")]
    [InlineData("Swift.Int16", "short")]
    [InlineData("Swift.UInt16", "ushort")]
    [InlineData("Swift.Int32", "int")]
    [InlineData("Swift.UInt32", "uint")]
    [InlineData("Swift.Int64", "long")]
    [InlineData("Swift.UInt64", "ulong")]
    [InlineData("Swift.Float", "float")]
    [InlineData("Swift.Double", "double")]
    [InlineData("Swift.Bool", "bool")]
    public void SwiftToCSharpType_MapsCorrectly(string swiftType, string expectedCSharp)
    {
        Assert.True(SwiftBuilder.SwiftToCSharpType.TryGetValue(swiftType, out var result));
        Assert.Equal(expectedCSharp, result);
    }

    [Theory]
    [InlineData("bool", "Bool")]
    [InlineData("System.Boolean", "Bool")]
    [InlineData("nint", "Int")]
    [InlineData("System.IntPtr", "Int")]
    [InlineData("nuint", "UInt")]
    [InlineData("float", "Float")]
    [InlineData("double", "Double")]
    [InlineData("int", "Int32")]
    [InlineData("long", "Int64")]
    public void CSharpToSwiftType_MapsCorrectly(string csharpType, string expectedSwift)
    {
        Assert.True(SwiftBuilder.CSharpToSwiftType.TryGetValue(csharpType, out var result));
        Assert.Equal(expectedSwift, result);
    }

    [Fact]
    public void SwiftToCSharpType_UnknownKey_ReturnsFalse()
    {
        Assert.False(SwiftBuilder.SwiftToCSharpType.TryGetValue("Swift.Unknown", out _));
    }

    [Fact]
    public void CSharpToSwiftType_UnknownKey_ReturnsFalse()
    {
        Assert.False(SwiftBuilder.CSharpToSwiftType.TryGetValue("decimal", out _));
    }

    #endregion

    #region GetSwiftCdeclParamType Tests

    [Theory]
    [InlineData("Swift.Bool", "UInt8")]
    [InlineData("Swift.Int", "Int")]
    [InlineData("Swift.UInt", "UInt")]
    [InlineData("Swift.Float", "Float")]
    [InlineData("Swift.Double", "Double")]
    [InlineData("Swift.Int32", "Int32")]
    [InlineData("Swift.UnsafeRawPointer", "UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer", "UnsafeMutableRawPointer")]
    [InlineData("Swift.OpaquePointer", "OpaquePointer")]
    [InlineData("MyModule.MyStruct", "UnsafeMutableRawPointer")]
    public void GetSwiftCdeclParamType_MapsCorrectly(string swiftTypeName, string expectedCdecl)
    {
        var named = new NamedTypeSpec(swiftTypeName);
        Assert.Equal(expectedCdecl, SwiftBuilder.GetSwiftCdeclParamType(named));
    }

    [Theory]
    [InlineData("Swift.Int", "Int")]
    [InlineData("Swift.Bool", "UInt8")]
    [InlineData("Swift.Double", "Double")]
    [InlineData("MyModule.MyClass", "UnsafeMutableRawPointer")]
    public void GetSwiftCdeclParamType_TypeSpec_NamedDelegatesToOverload(string name, string expected)
    {
        TypeSpec spec = new NamedTypeSpec(name);
        Assert.Equal(expected, SwiftBuilder.GetSwiftCdeclParamType(spec));
    }

    [Fact]
    public void GetSwiftCdeclParamType_TypeSpec_EmptyTuple_ReturnsVoid()
    {
        Assert.Equal("Void", SwiftBuilder.GetSwiftCdeclParamType(TupleTypeSpec.Empty));
    }

    [Fact]
    public void GetSwiftCdeclParamType_TypeSpec_NonNamedNonTuple_ReturnsPointer()
    {
        // A closure type spec is neither NamedTypeSpec nor empty tuple
        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));
        Assert.Equal("UnsafeMutableRawPointer", SwiftBuilder.GetSwiftCdeclParamType(closureSpec));
    }

    #endregion

    #region SanitizeIdentifier Tests

    [Fact]
    public void SanitizeIdentifier_CleanName_ReturnsUnchanged()
    {
        Assert.Equal("myParam", SwiftBuilder.SanitizeIdentifier("myParam"));
    }

    [Fact]
    public void SanitizeIdentifier_Brackets_Stripped()
    {
        Assert.Equal("foobar", SwiftBuilder.SanitizeIdentifier("foo[bar]"));
    }

    [Fact]
    public void SanitizeIdentifier_Parens_Stripped()
    {
        Assert.Equal("foobar", SwiftBuilder.SanitizeIdentifier("foo(bar)"));
    }

    [Fact]
    public void SanitizeIdentifier_AngleBrackets_Stripped()
    {
        Assert.Equal("ArrayInt", SwiftBuilder.SanitizeIdentifier("Array<Int>"));
    }

    [Fact]
    public void SanitizeIdentifier_MixedSyntaxChars_AllStripped()
    {
        Assert.Equal("abcdef", SwiftBuilder.SanitizeIdentifier("a<b>c[d]e(f)"));
    }

    [Fact]
    public void SanitizeIdentifier_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", SwiftBuilder.SanitizeIdentifier(""));
    }

    [Fact]
    public void SanitizeIdentifier_Null_ReturnsNull()
    {
        Assert.Null(SwiftBuilder.SanitizeIdentifier(null!));
    }

    [Fact]
    public void SanitizeIdentifier_UnderscoresPreserved()
    {
        Assert.Equal("my_param_name", SwiftBuilder.SanitizeIdentifier("my_param_name"));
    }

    #endregion

    #region Scope Block Tests

    [Fact]
    public void FunctionBlock_WritesSignatureAndBraces()
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);

        using (SwiftBuilder.FunctionBlock(writer, "public func test()"))
        {
            writer.WriteLine("// body");
        }

        var output = stringWriter.ToString();
        Assert.Contains("public func test() {", output);
        Assert.Contains("// body", output);
        Assert.Contains("}", output);
    }

    [Fact]
    public void FunctionBlock_WithAttribute_WritesAttributeFirst()
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);

        using (SwiftBuilder.FunctionBlock(writer, "public func test()", "@_cdecl(\"sym\")"))
        {
            writer.WriteLine("return 0");
        }

        var output = stringWriter.ToString();
        var attrIndex = output.IndexOf("@_cdecl");
        var funcIndex = output.IndexOf("public func test()");
        Assert.True(attrIndex < funcIndex, "Attribute should appear before function signature");
    }

    [Fact]
    public void ExtensionBlock_WritesExtensionAndBraces()
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);

        using (SwiftBuilder.ExtensionBlock(writer, "MyModule.MyType"))
        {
            writer.WriteLine("var x: Int { 0 }");
        }

        var output = stringWriter.ToString();
        Assert.Contains("extension MyModule.MyType {", output);
        Assert.Contains("}", output);
    }

    [Fact]
    public void IfBlock_WritesConditionAndBraces()
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);

        using (SwiftBuilder.IfBlock(writer, "x > 0"))
        {
            writer.WriteLine("print(x)");
        }

        var output = stringWriter.ToString();
        Assert.Contains("if x > 0 {", output);
        Assert.Contains("}", output);
    }

    [Fact]
    public void FunctionBlock_IndentIncrementsAndDecrements()
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        Assert.Equal(0, writer.Indent);

        using (SwiftBuilder.FunctionBlock(writer, "func f()"))
        {
            Assert.Equal(1, writer.Indent);
        }

        Assert.Equal(0, writer.Indent);
    }

    #endregion
}
