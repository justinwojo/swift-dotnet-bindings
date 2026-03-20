// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for StringReturnEmitter — shared SBW_Utf8Slice string return pattern for @_cdecl wrappers.
/// </summary>
public class StringReturnEmitterTests
{
    [Fact]
    public void EmitGetterBody_ContainsSBWUtf8SlicePattern()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        StringReturnEmitter.EmitGetterBody(swiftWriter, "obj.name");

        var result = output.ToString();
        Assert.Contains("let result = obj.name", result);
        Assert.Contains("SBW_Utf8Slice", result);
        Assert.Contains("utf8.isEmpty", result);
        Assert.Contains("_sbw_emptyBuffer", result);
        Assert.Contains("UnsafeMutablePointer<UInt8>.allocate(capacity: utf8.count)", result);
    }

    [Fact]
    public void EmitGetterBody_UsesPlainLetBinding()
    {
        // Getter body should NOT have `: String` annotation (unlike method returns)
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        StringReturnEmitter.EmitGetterBody(swiftWriter, "obj.title");

        var result = output.ToString();
        Assert.Contains("let result = obj.title", result);
        Assert.DoesNotContain("let result: String", result);
    }

    [Fact]
    public void EmitReturnBody_ContainsSBWUtf8SlicePattern()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        StringReturnEmitter.EmitReturnBody(swiftWriter, "obj.encode(value)");

        var result = output.ToString();
        Assert.Contains("let result: String = obj.encode(value)", result);
        Assert.Contains("SBW_Utf8Slice", result);
        Assert.Contains("utf8.isEmpty", result);
        Assert.Contains("_sbw_emptyBuffer", result);
    }

    [Fact]
    public void EmitReturnBody_HasExplicitStringTypeAnnotation()
    {
        // Method return body must include `: String` to disambiguate overloaded methods
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        StringReturnEmitter.EmitReturnBody(swiftWriter, "obj.describe()");

        var result = output.ToString();
        Assert.Contains("let result: String = obj.describe()", result);
    }

    [Fact]
    public void EmitGetterBody_EmptyReturnHandled()
    {
        // Verifies the empty string fast-path is present
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        StringReturnEmitter.EmitGetterBody(swiftWriter, "obj.emptyProp");

        var result = output.ToString();
        Assert.Contains("if utf8.isEmpty {", result);
        Assert.Contains("resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: &_sbw_emptyBuffer, len: 0), as: SBW_Utf8Slice.self)", result);
        Assert.Contains("return", result);
    }

    [Fact]
    public void EmitReturnBody_NonEmptyAllocatesAndInitializes()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        StringReturnEmitter.EmitReturnBody(swiftWriter, "obj.toString()");

        var result = output.ToString();
        Assert.Contains("let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: utf8.count)", result);
        Assert.Contains("ptr.initialize(from: utf8, count: utf8.count)", result);
        Assert.Contains("resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: ptr, len: utf8.count), as: SBW_Utf8Slice.self)", result);
    }

    [Fact]
    public void EmitGetterBody_StaticPropertyAccess()
    {
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        StringReturnEmitter.EmitGetterBody(swiftWriter, "TestModule.MyClass.staticName");

        var result = output.ToString();
        Assert.Contains("let result = TestModule.MyClass.staticName", result);
    }

    [Fact]
    public void EmitReturnBody_TryExpression()
    {
        // Throwing methods pass "try obj.method()" as the callExpr
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);

        StringReturnEmitter.EmitReturnBody(swiftWriter, "try obj.throwingMethod()");

        var result = output.ToString();
        Assert.Contains("let result: String = try obj.throwingMethod()", result);
    }
}
