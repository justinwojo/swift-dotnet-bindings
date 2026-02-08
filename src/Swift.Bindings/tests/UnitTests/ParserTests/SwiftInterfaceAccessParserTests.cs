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

    private static string WriteTempFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }
}
