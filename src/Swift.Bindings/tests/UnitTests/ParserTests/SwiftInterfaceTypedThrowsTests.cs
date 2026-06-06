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

    // --- P1-27 B1: depth-aware, string-literal-safe extraction ---
    // The old extractor took the LAST `throws(` match on the line, which misfired on a
    // function that returns a throwing closure (the closure's throws lives after the
    // function's own depth-0 `->`) and on a `throws(` appearing inside a string literal.

    [Fact]
    public void GetTypedThrowsErrors_ReturnedThrowingClosure_NotMisattributed()
    {
        // makeHandler itself does NOT throw; it returns a closure that does. The closure's
        // typed throws is after the function's depth-0 return arrow and must not be recorded.
        var swiftInterface = """
            public func makeHandler() -> (Swift.Int) throws(Module.HandlerError) -> Swift.Void
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
    public void GetTypedThrowsErrors_ReturnedThrowingClosure_Parenthesized_NotMisattributed()
    {
        // Same, with the returned closure fully parenthesized.
        var swiftInterface = """
            public func makeHandler() -> ((Swift.Int) throws(Module.HandlerError) -> Swift.Void)
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
    public void GetTypedThrowsErrors_FunctionThrowsAndReturnsClosure_ExtractsOwnError()
    {
        // The function's own typed throws comes BEFORE the depth-0 arrow and must still be
        // extracted even though the return type is itself a closure.
        var swiftInterface = """
            public func parse(_ input: Swift.String) throws(Module.ParseError) -> (Swift.Int) -> Swift.Void
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetTypedThrowsErrors(path);
            Assert.True(result.ContainsKey("parse(_:)"));
            Assert.Equal("Module.ParseError", result["parse(_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetTypedThrowsErrors_ThrowsInsideStringLiteral_NotMatched()
    {
        // A non-throwing function whose default-value string contains the text "throws(...)"
        // must not be treated as typed-throwing.
        var swiftInterface = """
            public func describe(_ label: Swift.String = "throws(NotReal)") -> Swift.String
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetTypedThrowsErrors(path);
            Assert.Empty(result);
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
