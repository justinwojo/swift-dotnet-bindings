// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftInterfaceAccessParserTests
{
    [Fact]
    public void GetInternalMembers_DetectsInlinableInternalFunc()
    {
        var swiftInterface = """
            public class AES {
              @inlinable final internal func encrypt(block: Swift.ArraySlice<Swift.UInt8>) -> Swift.Array<Swift.UInt8>? {
                return nil
              }
              @inlinable final public func encrypt(_ bytes: Swift.ArraySlice<Swift.UInt8>) throws -> Swift.Array<Swift.UInt8> {
                return []
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetInternalMembers(path);
            Assert.Contains("AES.encrypt(block:)", result);
            Assert.DoesNotContain("AES.encrypt(_:)", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetInternalMembers_DetectsUsableFromInlineInternalFunc()
    {
        var swiftInterface = """
            public class AES {
              @usableFromInline
              final internal func decrypt(block: Swift.ArraySlice<Swift.UInt8>) -> Swift.Array<Swift.UInt8>?
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetInternalMembers(path);
            Assert.Contains("AES.decrypt(block:)", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetInternalMembers_DetectsInternalVarAndLet()
    {
        var swiftInterface = """
            public class AES {
              @usableFromInline final internal let variantNr: Swift.Int
              @usableFromInline final internal var expandedKey: Swift.Array<Swift.UInt32>
              public var blockSize: Swift.Int { get }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetInternalMembers(path);
            Assert.Contains("AES.variantNr", result);
            Assert.Contains("AES.expandedKey", result);
            Assert.DoesNotContain("AES.blockSize", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetInternalMembers_HandlesNestedTypes()
    {
        var swiftInterface = """
            public class Outer {
              public class Inner {
                @inlinable internal func helper() -> Swift.Int {
                  return 0
                }
              }
              public func publicMethod() -> Swift.Int
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetInternalMembers(path);
            Assert.Contains("Inner.helper()", result);
            Assert.DoesNotContain("Outer.publicMethod()", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetInternalMembers_HandlesMultipleParameterLabels()
    {
        var swiftInterface = """
            public class Config {
              @inlinable internal func setup(blockMode mode: Swift.Int, padding pad: Swift.Int) -> Swift.Bool {
                return true
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetInternalMembers(path);
            Assert.Contains("Config.setup(blockMode:padding:)", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetInternalMembers_HandlesUnderscoreLabels()
    {
        var swiftInterface = """
            public struct Cipher {
              @inlinable internal func process(_ data: Swift.Array<Swift.UInt8>, _ key: Swift.Array<Swift.UInt8>) -> Swift.Bool {
                return false
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetInternalMembers(path);
            Assert.Contains("Cipher.process(_:_:)", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetInternalMembers_IgnoresPublicMembers()
    {
        var swiftInterface = """
            public class Foo {
              @inlinable public func bar() -> Swift.Int { return 0 }
              public func baz() -> Swift.Int
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetInternalMembers(path);
            Assert.DoesNotContain("Foo.bar()", result);
            Assert.DoesNotContain("Foo.baz()", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetInternalMembers_ReturnsEmptyForNonExistentFile()
    {
        var result = SwiftInterfaceAccessParser.GetInternalMembers("/nonexistent/path.swiftinterface");
        Assert.Empty(result);
    }

    [Fact]
    public void GetInternalMembers_DetectsInternalInit()
    {
        var swiftInterface = """
            public class AES {
              @usableFromInline
              internal init(key: Swift.Array<Swift.UInt8>, blockMode: Swift.Int) throws
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetInternalMembers(path);
            Assert.Contains("AES.init(key:blockMode:)", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetInternalMembers_HandlesNoParamFunc()
    {
        var swiftInterface = """
            public class Encryptor {
              @inlinable internal func reset() {
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetInternalMembers(path);
            Assert.Contains("Encryptor.reset()", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetInternalMembers_DetectsInternalInExtension()
    {
        // Internal members declared in extension blocks must also be detected.
        // This is the primary shape in real swiftinterface files.
        var swiftInterface = """
            @_hasMissingDesignatedInitializers public class AES {
              public var blockSize: Swift.Int { get }
            }
            extension CryptoSwift.AES {
              @inlinable final internal func encrypt(block: Swift.ArraySlice<Swift.UInt8>) -> Swift.Array<Swift.UInt8>? {
                return nil
              }
            }
            extension CryptoSwift.AES : CryptoSwift.Cipher {
              @inlinable final public func encrypt(_ bytes: Swift.ArraySlice<Swift.UInt8>) throws -> Swift.Array<Swift.UInt8> {
                return []
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetInternalMembers(path);
            Assert.Contains("AES.encrypt(block:)", result);
            Assert.DoesNotContain("AES.encrypt(_:)", result);
            Assert.DoesNotContain("AES.blockSize", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetInternalMembers_HandlesExtensionWithConformance()
    {
        var swiftInterface = """
            public protocol Cipher {}
            extension CryptoSwift.SHA2 : CryptoSwift.Cipher {
              @usableFromInline
              internal func process64(_ data: Swift.Array<Swift.UInt8>) -> Swift.Array<Swift.UInt8>
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetInternalMembers(path);
            Assert.Contains("SHA2.process64(_:)", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetInternalMembers_HandlesUnqualifiedExtension()
    {
        // Extension without module prefix (rare but valid in swiftinterface)
        var swiftInterface = """
            public struct Foo {
            }
            extension Foo {
              @inlinable internal func helper() -> Swift.Int {
                return 0
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetInternalMembers(path);
            Assert.Contains("Foo.helper()", result);
        }
        finally { File.Delete(path); }
    }

    // ===== GetParameterNames Tests =====

    [Fact]
    public void GetParameterNames_SingleUnderscoreParam()
    {
        var swiftInterface = """
            public struct Math {
              public func negate(_ value: Swift.Int) -> Swift.Int
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetParameterNames(path);
            Assert.True(result.ContainsKey("Math.negate(_:)"));
            Assert.Equal(new[] { "value" }, result["Math.negate(_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetParameterNames_MultipleUnderscoreParams()
    {
        var swiftInterface = """
            public struct Math {
              public func sumTwo(_ a: Swift.Int, _ b: Swift.Int) -> Swift.Int
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetParameterNames(path);
            Assert.True(result.ContainsKey("Math.sumTwo(_:_:)"));
            Assert.Equal(new[] { "a", "b" }, result["Math.sumTwo(_:_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetParameterNames_MixedLabels()
    {
        var swiftInterface = """
            public class Dog {
              public func fetch(from location: Swift.String, _ count: Swift.Int) -> Swift.Bool
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetParameterNames(path);
            Assert.True(result.ContainsKey("Dog.fetch(from:_:)"));
            Assert.Equal(new[] { "location", "count" }, result["Dog.fetch(from:_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetParameterNames_KeywordNames()
    {
        var swiftInterface = """
            public struct Ops {
              public func iterate(for range: Swift.Int, in context: Swift.String) -> Swift.Void
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetParameterNames(path);
            Assert.True(result.ContainsKey("Ops.iterate(for:in:)"));
            Assert.Equal(new[] { "range", "context" }, result["Ops.iterate(for:in:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetParameterNames_GenericFunc()
    {
        var swiftInterface = """
            public struct Container {
              public func wrap<T>(_ value: T) -> Container
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetParameterNames(path);
            Assert.True(result.ContainsKey("Container.wrap(_:)"));
            Assert.Equal(new[] { "value" }, result["Container.wrap(_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetParameterNames_FreeFunctions()
    {
        var swiftInterface = """
            public func globalSum(_ a: Swift.Int, _ b: Swift.Int) -> Swift.Int
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetParameterNames(path);
            // Free functions have no type prefix
            Assert.True(result.ContainsKey("globalSum(_:_:)"));
            Assert.Equal(new[] { "a", "b" }, result["globalSum(_:_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetParameterNames_InitParams()
    {
        var swiftInterface = """
            public class AES {
              public init(key: Swift.Array<Swift.UInt8>, blockMode mode: Swift.Int)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetParameterNames(path);
            Assert.True(result.ContainsKey("AES.init(key:blockMode:)"));
            Assert.Equal(new[] { "key", "mode" }, result["AES.init(key:blockMode:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetParameterNames_MultiLineSignature()
    {
        var swiftInterface = """
            public struct Config {
              public func setup(
                _ name: Swift.String,
                limit count: Swift.Int
              ) -> Swift.Bool
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetParameterNames(path);
            Assert.True(result.ContainsKey("Config.setup(_:limit:)"));
            Assert.Equal(new[] { "name", "count" }, result["Config.setup(_:limit:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetParameterNames_ExtensionScope()
    {
        var swiftInterface = """
            public struct Cipher {
            }
            extension MyModule.Cipher {
              public func process(_ data: Swift.Array<Swift.UInt8>) -> Swift.Array<Swift.UInt8>
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetParameterNames(path);
            Assert.True(result.ContainsKey("Cipher.process(_:)"));
            Assert.Equal(new[] { "data" }, result["Cipher.process(_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetParameterNames_SameNameExternalAndInternal()
    {
        // When external and internal name are the same: "name: Type"
        var swiftInterface = """
            public struct Point {
              public func move(x: Swift.Int, y: Swift.Int) -> Point
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetParameterNames(path);
            Assert.True(result.ContainsKey("Point.move(x:y:)"));
            Assert.Equal(new[] { "x", "y" }, result["Point.move(x:y:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetParameterNames_ReturnsEmptyForNonExistentFile()
    {
        var result = SwiftInterfaceAccessParser.GetParameterNames("/nonexistent/path.swiftinterface");
        Assert.Empty(result);
    }

    [Fact]
    public void GetParameterNames_NoParamFunc()
    {
        var swiftInterface = """
            public class Timer {
              public func reset()
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetParameterNames(path);
            // No params → no entry (empty internalNames list is not stored)
            Assert.False(result.ContainsKey("Timer.reset()"));
        }
        finally { File.Delete(path); }
    }

    private static string WriteTempFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }
}
