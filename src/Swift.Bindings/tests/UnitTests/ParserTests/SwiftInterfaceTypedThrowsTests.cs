// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SwiftInterfaceAccessParser.GetTypedThrowsErrors().
/// Verifies extraction of typed throws error types from .swiftinterface files.
/// </summary>
public class SwiftInterfaceTypedThrowsTests
{
    [Fact]
    public void GetTypedThrowsErrors_FreeFunctionWithTypedThrows()
    {
        var swiftInterface = """
            public func parseNumber(_ input: Swift.String) throws(SwiftBindingsTestLib.ParseError) -> Swift.Int32
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetTypedThrowsErrors(path);
            Assert.True(result.ContainsKey("parseNumber(_:)"));
            Assert.Equal("SwiftBindingsTestLib.ParseError", result["parseNumber(_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetTypedThrowsErrors_InstanceMethodWithinType()
    {
        var swiftInterface = """
            @frozen public struct TypedThrowingParser {
              public func parse(_ input: Swift.String) throws(SwiftBindingsTestLib.ParseError) -> Swift.Int32
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetTypedThrowsErrors(path);
            Assert.True(result.ContainsKey("TypedThrowingParser.parse(_:)"));
            Assert.Equal("SwiftBindingsTestLib.ParseError", result["TypedThrowingParser.parse(_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetTypedThrowsErrors_ExtensionMethod()
    {
        var swiftInterface = """
            extension SwiftBindingsTestLib.TypedThrowingParser {
              public func asyncParse(_ input: Swift.String) async throws(SwiftBindingsTestLib.ParseError) -> Swift.Int32
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetTypedThrowsErrors(path);
            Assert.True(result.ContainsKey("TypedThrowingParser.asyncParse(_:)"));
            Assert.Equal("SwiftBindingsTestLib.ParseError", result["TypedThrowingParser.asyncParse(_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetTypedThrowsErrors_UntypedThrowsNotIncluded()
    {
        var swiftInterface = """
            public func divide(a: Swift.Int32, b: Swift.Int32) throws -> Swift.Int32
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetTypedThrowsErrors(path);
            Assert.Empty(result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetTypedThrowsErrors_NonThrowingFunctionNotIncluded()
    {
        var swiftInterface = """
            public func add(a: Swift.Int32, b: Swift.Int32) -> Swift.Int32
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetTypedThrowsErrors(path);
            Assert.Empty(result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetTypedThrowsErrors_MultipleTypedThrowsFunctions()
    {
        var swiftInterface = """
            public func parseNumber(_ input: Swift.String) throws(SwiftBindingsTestLib.ParseError) -> Swift.Int32
            public func validateRange(_ value: Swift.Int32, min: Swift.Int32, max: Swift.Int32) throws(SwiftBindingsTestLib.RangeError) -> Swift.Int32
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetTypedThrowsErrors(path);
            Assert.Equal(2, result.Count);
            Assert.Equal("SwiftBindingsTestLib.ParseError", result["parseNumber(_:)"]);
            Assert.Equal("SwiftBindingsTestLib.RangeError", result["validateRange(_:min:max:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetTypedThrowsErrors_MultiLineSignature()
    {
        var swiftInterface = """
            public func validateRange(_ value: Swift.Int32,
              min: Swift.Int32,
              max: Swift.Int32) throws(SwiftBindingsTestLib.RangeError) -> Swift.Int32
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetTypedThrowsErrors(path);
            Assert.True(result.ContainsKey("validateRange(_:min:max:)"));
            Assert.Equal("SwiftBindingsTestLib.RangeError", result["validateRange(_:min:max:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetTypedThrowsErrors_MixedThrowingAndTypedThrowing()
    {
        var swiftInterface = """
            @frozen public struct Parser {
              public func parse(_ input: Swift.String) throws(Module.ParseError) -> Swift.Int32
              public func validate(_ input: Swift.String) throws -> Swift.Bool
              public func transform(_ input: Swift.String) -> Swift.String
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetTypedThrowsErrors(path);
            Assert.Single(result);
            Assert.True(result.ContainsKey("Parser.parse(_:)"));
            Assert.Equal("Module.ParseError", result["Parser.parse(_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetTypedThrowsErrors_NonexistentFile_ReturnsEmpty()
    {
        var result = SwiftInterfaceAccessParser.GetTypedThrowsErrors("/nonexistent/path.swiftinterface");
        Assert.Empty(result);
    }

    [Fact]
    public void GetTypedThrowsErrors_PreservesFullyQualifiedErrorType()
    {
        var swiftInterface = """
            public func process(_ data: Swift.String) throws(Foundation.NSError) -> Swift.Bool
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetTypedThrowsErrors(path);
            Assert.True(result.ContainsKey("process(_:)"));
            Assert.Equal("Foundation.NSError", result["process(_:)"]);
        }
        finally { File.Delete(path); }
    }

    private static string WriteTempFile(string content)
    {
        var path = Path.GetTempFileName() + ".swiftinterface";
        File.WriteAllText(path, content);
        return path;
    }
}
