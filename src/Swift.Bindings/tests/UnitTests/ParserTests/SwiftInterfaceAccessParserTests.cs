// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    #region GetProtocolExtensionMethods — property setter detection

    [Fact]
    public void GetProtocolExtensionMethods_InlineGetSet_DetectsSetter()
    {
        var swiftInterface = """
            public protocol Configurable {
              var setting: Swift.Int { get set }
            }
            extension TestModule.Configurable {
              public var setting: Swift.Int { get set }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolExtensionMethods(
                path, new HashSet<string> { "Configurable" });
            Assert.True(result.ContainsKey("TestModule.Configurable"));
            var prop = Assert.Single(result["TestModule.Configurable"]);
            Assert.True(prop.IsProperty);
            Assert.True(prop.HasSetter);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolExtensionMethods_InlineGetOnly_NoSetter()
    {
        var swiftInterface = """
            public protocol Readable {
              var value: Swift.Int { get }
            }
            extension TestModule.Readable {
              public var value: Swift.Int { get }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolExtensionMethods(
                path, new HashSet<string> { "Readable" });
            Assert.True(result.ContainsKey("TestModule.Readable"));
            var prop = Assert.Single(result["TestModule.Readable"]);
            Assert.True(prop.IsProperty);
            Assert.False(prop.HasSetter);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolExtensionMethods_MultilineSetOnSeparateLine_DetectsSetter()
    {
        // Multiline property with "set" on its own line — exercises scope-based detection
        var swiftInterface = """
            public protocol Writable {
              var data: Swift.String { get set }
            }
            extension TestModule.Writable {
              public var data: Swift.String {
                get
                set
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolExtensionMethods(
                path, new HashSet<string> { "Writable" });
            Assert.True(result.ContainsKey("TestModule.Writable"));
            var prop = Assert.Single(result["TestModule.Writable"]);
            Assert.True(prop.IsProperty);
            Assert.True(prop.HasSetter);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolExtensionMethods_MultilineNonmutatingSetter_DetectsSetter()
    {
        // "nonmutating set" on its own line — exercises scope-based detection
        var swiftInterface = """
            public protocol Settings {
              var theme: Swift.Int { get set }
            }
            extension TestModule.Settings {
              public var theme: Swift.Int {
                get
                nonmutating set
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolExtensionMethods(
                path, new HashSet<string> { "Settings" });
            Assert.True(result.ContainsKey("TestModule.Settings"));
            var prop = Assert.Single(result["TestModule.Settings"]);
            Assert.True(prop.IsProperty);
            Assert.True(prop.HasSetter);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolExtensionMethods_MultilineGetOnly_NoSetter()
    {
        // Multiline property with only "get" — should NOT have setter
        var swiftInterface = """
            public protocol Info {
              var name: Swift.String { get }
            }
            extension TestModule.Info {
              public var name: Swift.String {
                get
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolExtensionMethods(
                path, new HashSet<string> { "Info" });
            Assert.True(result.ContainsKey("TestModule.Info"));
            var prop = Assert.Single(result["TestModule.Info"]);
            Assert.True(prop.IsProperty);
            Assert.False(prop.HasSetter);
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region Availability Annotations

    [Fact]
    public void GetAvailabilityAnnotations_PlatformVersionOnType()
    {
        var swiftInterface = """
            @available(iOS 16.0, *)
            public class NewFeature {
              public func doStuff()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("NewFeature"));
            var annotations = result["NewFeature"];
            Assert.Contains(annotations, a => a.Platform == "iOS" && a.IntroducedVersion == "16.0");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_PlatformVersionOnMember()
    {
        var swiftInterface = """
            public class MyClass {
              @available(iOS 13, *)
              public func newFunc() -> Swift.Int
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("MyClass.newFunc()"));
            var annotations = result["MyClass.newFunc()"];
            Assert.Contains(annotations, a => a.Platform == "iOS" && a.IntroducedVersion == "13");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_MultiPlatform()
    {
        var swiftInterface = """
            @available(macOS 10.15, iOS 13, tvOS 13, watchOS 6, *)
            public class CrossPlatform {
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("CrossPlatform"));
            var annotations = result["CrossPlatform"];
            Assert.Contains(annotations, a => a.Platform == "macOS" && a.IntroducedVersion == "10.15");
            Assert.Contains(annotations, a => a.Platform == "iOS" && a.IntroducedVersion == "13");
            Assert.Contains(annotations, a => a.Platform == "tvOS" && a.IntroducedVersion == "13");
            Assert.Contains(annotations, a => a.Platform == "watchOS" && a.IntroducedVersion == "6");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_DeprecatedWithMessage()
    {
        var swiftInterface = """
            public class MyClass {
              @available(*, deprecated, message: "Use newMethod instead")
              public func oldMethod()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("MyClass.oldMethod()"));
            var annotations = result["MyClass.oldMethod()"];
            Assert.Contains(annotations, a => a.IsUnconditionallyDeprecated && a.Message == "Use newMethod instead");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_DeprecatedWithRenamed()
    {
        var swiftInterface = """
            public class MyClass {
              @available(*, deprecated, renamed: "newName")
              public func oldName()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("MyClass.oldName()"));
            Assert.Contains(result["MyClass.oldName()"], a => a.IsUnconditionallyDeprecated && a.Renamed == "newName");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_Unavailable()
    {
        var swiftInterface = """
            public class MyClass {
              @available(*, unavailable)
              public func unavailableFunc()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("MyClass.unavailableFunc()"));
            Assert.Contains(result["MyClass.unavailableFunc()"], a => a.IsUnconditionallyUnavailable);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_SwiftObsoleted_Skipped()
    {
        var swiftInterface = """
            public class MyClass {
              @available(swift, obsoleted: 1.0)
              public func swiftOnly()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.False(result.ContainsKey("MyClass.swiftOnly()"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_StackedAvailable()
    {
        var swiftInterface = """
            public class MyClass {
              @available(iOS 13, *)
              @available(*, deprecated, message: "Old API")
              public func stackedFunc()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("MyClass.stackedFunc()"));
            var annotations = result["MyClass.stackedFunc()"];
            Assert.Contains(annotations, a => a.Platform == "iOS" && a.IntroducedVersion == "13");
            Assert.Contains(annotations, a => a.IsUnconditionallyDeprecated && a.Message == "Old API");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_ExtensionLevel_InheritedByMembers()
    {
        var swiftInterface = """
            @available(iOS 13, *)
            extension Module.MyType {
              public func extFunc() -> Swift.Int
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("MyType.extFunc()"));
            Assert.Contains(result["MyType.extFunc()"], a => a.Platform == "iOS" && a.IntroducedVersion == "13");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_NestedType()
    {
        var swiftInterface = """
            public class Outer {
              @available(iOS 16, *)
              public class Inner {
              }
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("Outer.Inner"));
            Assert.Contains(result["Outer.Inner"], a => a.Platform == "iOS" && a.IntroducedVersion == "16");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_PendingAnnotation()
    {
        var swiftInterface = """
            public class MyClass {
              @available(iOS 14, *)
              public func pendingFunc() -> Swift.String
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("MyClass.pendingFunc()"));
            Assert.Contains(result["MyClass.pendingFunc()"], a => a.Platform == "iOS" && a.IntroducedVersion == "14");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_PerPlatformLifecycle()
    {
        var swiftInterface = """
            public class MyClass {
              @available(iOS, introduced: 10, deprecated: 12)
              public func lifecycleFunc()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("MyClass.lifecycleFunc()"));
            var annotation = Assert.Single(result["MyClass.lifecycleFunc()"]);
            Assert.Equal("iOS", annotation.Platform);
            Assert.Equal("10", annotation.IntroducedVersion);
            Assert.Equal("12", annotation.DeprecatedVersion);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_PropertyLevel()
    {
        var swiftInterface = """
            public class MyClass {
              @available(iOS 14, *)
              public var newProp: Swift.Int { get }
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("MyClass.newProp"));
            Assert.Contains(result["MyClass.newProp"], a => a.Platform == "iOS" && a.IntroducedVersion == "14");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_MessageWithNestedParens()
    {
        var swiftInterface = """
            public class MyClass {
              @available(*, deprecated, message: "Use init(config:) instead")
              public func oldInit()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("MyClass.oldInit()"));
            Assert.Contains(result["MyClass.oldInit()"],
                a => a.IsUnconditionallyDeprecated && a.Message == "Use init(config:) instead");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_TypeLevelDeprecation()
    {
        var swiftInterface = """
            @available(*, deprecated, message: "Use NewClass instead")
            public class OldClass {
              public func foo()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("OldClass"));
            Assert.Contains(result["OldClass"],
                a => a.IsUnconditionallyDeprecated && a.Message == "Use NewClass instead");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseAvailableClause_ShorthandPlatformForm()
    {
        var annotations = SwiftInterfaceAccessParser.ParseAvailableClause("iOS 16.0, macOS 13, *");
        Assert.Equal(2, annotations.Count);
        Assert.Contains(annotations, a => a.Platform == "iOS" && a.IntroducedVersion == "16.0");
        Assert.Contains(annotations, a => a.Platform == "macOS" && a.IntroducedVersion == "13");
    }

    [Fact]
    public void ParseAvailableClause_UnconditionalDeprecated()
    {
        var annotations = SwiftInterfaceAccessParser.ParseAvailableClause("*, deprecated, message: \"old\"");
        var annotation = Assert.Single(annotations);
        Assert.True(annotation.IsUnconditionallyDeprecated);
        Assert.Equal("old", annotation.Message);
    }

    [Fact]
    public void ParseAvailableClause_Unavailable()
    {
        var annotations = SwiftInterfaceAccessParser.ParseAvailableClause("*, unavailable");
        var annotation = Assert.Single(annotations);
        Assert.True(annotation.IsUnconditionallyUnavailable);
    }

    [Fact]
    public void ParseAvailableClause_SwiftObsoleted_SkipsCompilerLevel()
    {
        var annotations = SwiftInterfaceAccessParser.ParseAvailableClause("swift, obsoleted: 1.0");
        Assert.Empty(annotations);
    }

    [Fact]
    public void ExtractAvailableClauses_BalancedParenHandling()
    {
        var clauses = SwiftInterfaceAccessParser.ExtractAvailableClauses(
            "@available(*, deprecated, message: \"Use init(config:) instead\")");
        var clause = Assert.Single(clauses);
        Assert.Equal("*, deprecated, message: \"Use init(config:) instead\"", clause);
    }

    [Fact]
    public void GetAvailabilityAnnotations_MultiLineMember()
    {
        var swiftInterface = """
            public class MyClass {
              @available(iOS 16.0, *)
              public func longSignature(_ x: Swift.Int,
                y: Swift.String) -> Swift.Bool
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("MyClass.longSignature(_:y:)"));
            var annotations = result["MyClass.longSignature(_:y:)"];
            Assert.Contains(annotations, a => a.Platform == "iOS" && a.IntroducedVersion == "16.0");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_NestedTypeExtension()
    {
        var swiftInterface = """
            @available(iOS 15.0, *)
            extension Module.Outer.Inner {
              public func nestedFunc()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            // Extension-scope annotations should be inherited by members
            // and the type path should be "Outer.Inner" (not just "Inner")
            Assert.True(result.ContainsKey("Outer.Inner.nestedFunc()"));
            var annotations = result["Outer.Inner.nestedFunc()"];
            Assert.Contains(annotations, a => a.Platform == "iOS" && a.IntroducedVersion == "15.0");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_SubscriptMember()
    {
        var swiftInterface = """
            public class MyCollection {
              @available(iOS 14.0, *)
              public subscript(index: Swift.Int) -> Swift.String { get }
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            // Single-name subscript params carry no call-site label, so the ABI key uses `_:`.
            Assert.True(result.ContainsKey("MyCollection.subscript(_:)"));
            var annotations = result["MyCollection.subscript(_:)"];
            Assert.Contains(annotations, a => a.Platform == "iOS" && a.IntroducedVersion == "14.0");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_FreeFunctionDoesNotBleedToNextType()
    {
        var swiftInterface = """
            @available(*, deprecated, message: "Use modernFunction instead")
            public func legacyFunction() -> Swift.String
            @available(iOS 16.0, *)
            public func modernFunction() -> Swift.String
            public class TrackedObject {
              public var objectId: Swift.Int { get }
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            // Free functions should get their own annotations
            Assert.True(result.ContainsKey("legacyFunction()"), "legacyFunction should have annotations");
            Assert.Contains(result["legacyFunction()"], a => a.IsUnconditionallyDeprecated);
            Assert.True(result.ContainsKey("modernFunction()"), "modernFunction should have annotations");
            Assert.Contains(result["modernFunction()"], a => a.Platform == "iOS" && a.IntroducedVersion == "16.0");
            // TrackedObject must NOT inherit free function annotations
            Assert.False(result.ContainsKey("TrackedObject"), "TrackedObject should NOT have annotations from free functions");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_InlineAvailableOnEnumCase()
    {
        // `@available(...) case foo` on a single line must be recognized as a member
        // declaration, not a pure annotation. Otherwise the case is silently dropped
        // and its annotation leaks onto the following declaration.
        var swiftInterface = """
            public enum Status {
              @available(iOS 16.0, *) case freshlyAdded
              public func describe() -> Swift.String
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("Status.freshlyAdded"),
                "freshlyAdded enum case should have its inline @available annotation");
            Assert.Contains(result["Status.freshlyAdded"],
                a => a.Platform == "iOS" && a.IntroducedVersion == "16.0");
            // The describe() method should NOT have inherited the case's annotation.
            Assert.False(result.ContainsKey("Status.describe()"),
                "describe() should not inherit the case's @available annotation");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_InlineAvailableOnEnumCaseWithStringPayload()
    {
        // Attribute payload contains a string literal with parens (`renamed: "foo(_:)"`).
        // The classifier must skip the entire attribute, not stop at the first `)` in the
        // string. Otherwise the case is dropped and the annotation leaks to the next decl.
        var swiftInterface = """
            public enum Status {
              @available(*, deprecated, renamed: "foo(_:)") case old
              public func describe() -> Swift.String
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("Status.old"),
                "old case should have its inline @available annotation");
            Assert.Contains(result["Status.old"],
                a => a.IsUnconditionallyDeprecated && a.Renamed == "foo(_:)");
            Assert.False(result.ContainsKey("Status.describe()"),
                "describe() should not inherit the case's @available annotation");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAvailabilityAnnotations_GroupedEnumCases()
    {
        // `case foo, bar(Int), baz` exposes each case as a separate Var node in the ABI
        // JSON. The pending @available must be applied to every case on the line, not
        // just the first.
        var swiftInterface = """
            public enum Status {
              @available(iOS 16.0, *)
              case freshlyAdded, anotherFresh, yetAnotherFresh(Swift.Int)
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            foreach (var name in new[] { "freshlyAdded", "anotherFresh", "yetAnotherFresh" })
            {
                var key = $"Status.{name}";
                Assert.True(result.ContainsKey(key), $"{key} should have annotations");
                Assert.Contains(result[key],
                    a => a.Platform == "iOS" && a.IntroducedVersion == "16.0");
            }
        }
        finally { File.Delete(path); }
    }

    #endregion

    // ===== GetDefaultParameterValues Tests =====

    [Fact]
    public void GetDefaultParameterValues_NumericDefaults()
    {
        var swiftInterface = """
            public class Config {
              public func setup(limit: Swift.Int = 10, offset: Swift.Int = 0)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Config.setup(limit:offset:)"));
            var defaults = result["Config.setup(limit:offset:)"];
            Assert.Equal(2, defaults.Count);
            Assert.Equal("10", defaults[0]);
            Assert.Equal("0", defaults[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_BoolDefaults()
    {
        var swiftInterface = """
            public class Scanner {
              public func scan(verbose: Swift.Bool = true, strict: Swift.Bool = false)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Scanner.scan(verbose:strict:)"));
            var defaults = result["Scanner.scan(verbose:strict:)"];
            Assert.Equal("true", defaults[0]);
            Assert.Equal("false", defaults[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_StringDefault()
    {
        var swiftInterface = """
            public class Greeter {
              public func greet(name: Swift.String = "World")
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Greeter.greet(name:)"));
            Assert.Equal("\"World\"", result["Greeter.greet(name:)"][0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_NilDefault()
    {
        var swiftInterface = """
            public class Cache {
              public func store(key: Swift.String, value: Swift.Optional<Swift.Int> = nil)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Cache.store(key:value:)"));
            var defaults = result["Cache.store(key:value:)"];
            Assert.Null(defaults[0]); // no default for 'key'
            Assert.Equal("nil", defaults[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_EnumDotDefault()
    {
        var swiftInterface = """
            public class Audio {
              public func setLevel(level: MyLib.Level = .mid)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Audio.setLevel(level:)"));
            Assert.Equal(".mid", result["Audio.setLevel(level:)"][0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_QualifiedEnumDefault()
    {
        var swiftInterface = """
            public class Painter {
              public func setColor(color: SVGView.SVGColor = SVGColor.black)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Painter.setColor(color:)"));
            Assert.Equal("SVGColor.black", result["Painter.setColor(color:)"][0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_FloatDefault()
    {
        var swiftInterface = """
            public class Animator {
              public func animate(duration: Swift.Double = 0.02, speed: Swift.Float = 0.8)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Animator.animate(duration:speed:)"));
            var defaults = result["Animator.animate(duration:speed:)"];
            Assert.Equal("0.02", defaults[0]);
            Assert.Equal("0.8", defaults[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_UnderscoreNumeric()
    {
        var swiftInterface = """
            public class Counter {
              public func setMax(max: Swift.Int = 1_000_000)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Counter.setMax(max:)"));
            Assert.Equal("1_000_000", result["Counter.setMax(max:)"][0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_MultiLineSignature()
    {
        var swiftInterface = """
            public class Builder {
              public func build(width: Swift.Int = 100,
                height: Swift.Int = 200,
                depth: Swift.Int = 50)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Builder.build(width:height:depth:)"));
            var defaults = result["Builder.build(width:height:depth:)"];
            Assert.Equal(3, defaults.Count);
            Assert.Equal("100", defaults[0]);
            Assert.Equal("200", defaults[1]);
            Assert.Equal("50", defaults[2]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_NestedType()
    {
        var swiftInterface = """
            public class Outer {
              public class Inner {
                public func configure(mode: Swift.Int = 1)
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Outer.Inner.configure(mode:)"));
            Assert.Equal("1", result["Outer.Inner.configure(mode:)"][0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_NoDefaults_NotInResult()
    {
        var swiftInterface = """
            public class Plain {
              public func doSomething(x: Swift.Int, y: Swift.Int)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.False(result.ContainsKey("Plain.doSomething(x:y:)"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_InitWithDefaults()
    {
        var swiftInterface = """
            public class Settings {
              public init(verbose: Swift.Bool = false, retries: Swift.Int = 3)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Settings.init(verbose:retries:)"));
            var defaults = result["Settings.init(verbose:retries:)"];
            Assert.Equal("false", defaults[0]);
            Assert.Equal("3", defaults[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_MixedDefaultAndNonDefault()
    {
        var swiftInterface = """
            public class Query {
              public func search(query: Swift.String, limit: Swift.Int = 25, offset: Swift.Int = 0)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Query.search(query:limit:offset:)"));
            var defaults = result["Query.search(query:limit:offset:)"];
            Assert.Equal(3, defaults.Count);
            Assert.Null(defaults[0]); // no default for 'query'
            Assert.Equal("25", defaults[1]);
            Assert.Equal("0", defaults[2]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_ComplexExpression_StillExtracted()
    {
        // Complex defaults are extracted as raw strings — the mapper will reject them later
        var swiftInterface = """
            public class Factory {
              public func create(config: MyLib.Config = Config())
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Factory.create(config:)"));
            Assert.Equal("Config()", result["Factory.create(config:)"][0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_NegativeNumber()
    {
        var swiftInterface = """
            public class Math {
              public func offset(x: Swift.Int = -1)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Math.offset(x:)"));
            Assert.Equal("-1", result["Math.offset(x:)"][0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_DotNoneOnOptional()
    {
        var swiftInterface = """
            public class Opt {
              public func configure(value: Swift.Optional<Swift.Int> = .none)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("Opt.configure(value:)"));
            Assert.Equal(".none", result["Opt.configure(value:)"][0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_EmptyFile()
    {
        var result = SwiftInterfaceAccessParser.GetDefaultParameterValues("/nonexistent/path.swiftinterface");
        Assert.Empty(result);
    }

    [Fact]
    public void GetDefaultParameterValues_StringWithComma()
    {
        // Regression: default value "," should not split the parameter list
        var swiftInterface = """
            public struct DumpFormat {
              public init(header: Swift.Bool = false, separator: Swift.String = ",")
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("DumpFormat.init(header:separator:)"));
            var defaults = result["DumpFormat.init(header:separator:)"];
            Assert.Equal(2, defaults.Count);
            Assert.Equal("false", defaults[0]);
            Assert.Equal("\",\"", defaults[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_FreeFunction()
    {
        // Free functions (not inside a type) should use bare printedName as key
        var swiftInterface = """
            public func greet(name: Swift.String, loud: Swift.Bool = false)
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("greet(name:loud:)"));
            var defaults = result["greet(name:loud:)"];
            Assert.Equal(2, defaults.Count);
            Assert.Null(defaults[0]); // name has no default
            Assert.Equal("false", defaults[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetDefaultParameterValues_MultiLineFreeFunction()
    {
        // Multi-line free functions should be reassembled via continuation and parsed correctly
        var swiftInterface =
            "public func configure(\n" +
            "  name: Swift.String,\n" +
            "  verbose: Swift.Bool = true\n" +
            ")\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetDefaultParameterValues(path);
            Assert.True(result.ContainsKey("configure(name:verbose:)"));
            var defaults = result["configure(name:verbose:)"];
            Assert.Equal(2, defaults.Count);
            Assert.Null(defaults[0]);
            Assert.Equal("true", defaults[1]);
        }
        finally { File.Delete(path); }
    }

    // ===== GetEnumCaseLabels Tests =====

    [Fact]
    public void GetEnumCaseLabels_LabeledParam_ExtractsLabel()
    {
        var swiftInterface = """
            public enum WebSocketEvent {
              case connected([Swift.String : Swift.String])
              case text(Swift.String)
              case error(reason: Swift.String)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetEnumCaseLabels(path);
            Assert.True(result.ContainsKey("WebSocketEvent.error"));
            Assert.Single(result["WebSocketEvent.error"]);
            Assert.Equal("reason", result["WebSocketEvent.error"][0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetEnumCaseLabels_DictionaryType_ColonNotMisparsedAsLabel()
    {
        // Regression: [String : String] contains a colon that was misinterpreted as a
        // label separator, producing label "_ Swift.String" instead of null (unlabeled).
        // The fix uses FindTopLevelColon which skips colons inside brackets.
        var swiftInterface = """
            public enum WebSocketEvent {
              case connected([Swift.String : Swift.String])
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetEnumCaseLabels(path);
            Assert.True(result.ContainsKey("WebSocketEvent.connected"));
            Assert.Single(result["WebSocketEvent.connected"]);
            // Should be unlabeled (null), not a garbled label from the dictionary colon
            Assert.Null(result["WebSocketEvent.connected"][0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetEnumCaseLabels_NestedGenericType_ColonNotMisparsedAsLabel()
    {
        // Dictionary inside Optional: Optional<[String : Int]> — colon must not be label separator
        var swiftInterface = """
            public enum Config {
              case headers(Swift.Optional<[Swift.String : Swift.Int]>)
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetEnumCaseLabels(path);
            Assert.True(result.ContainsKey("Config.headers"));
            Assert.Single(result["Config.headers"]);
            Assert.Null(result["Config.headers"][0]);
        }
        finally { File.Delete(path); }
    }

    // ===== ExtractParameterDefaults Tests =====

    [Fact]
    public void ExtractParameterDefaults_SimpleDefault()
    {
        var line = "public func foo(x: Swift.Int = 10)";
        var defaults = SwiftInterfaceAccessParser.ExtractParameterDefaults(line);
        Assert.NotNull(defaults);
        Assert.Single(defaults!);
        Assert.Equal("10", defaults![0]);
    }

    [Fact]
    public void ExtractParameterDefaults_NoDefault()
    {
        var line = "public func foo(x: Swift.Int)";
        var defaults = SwiftInterfaceAccessParser.ExtractParameterDefaults(line);
        Assert.Null(defaults); // No defaults at all
    }

    [Fact]
    public void ExtractParameterDefaults_GenericTypeDefault()
    {
        // Default on Optional<Array<Int>> — expression should be extracted
        var line = "public func foo(items: Swift.Optional<Swift.Array<Swift.Int>> = nil)";
        var defaults = SwiftInterfaceAccessParser.ExtractParameterDefaults(line);
        Assert.NotNull(defaults);
        Assert.Equal("nil", defaults![0]);
    }

    // ===== ExtractAutoclosureFlags Tests =====

    [Fact]
    public void ExtractAutoclosureFlags_SingleAutoclosure()
    {
        var line = "final public func warn(_ message: @autoclosure () -> Swift.String = String(), fileID: Swift.StaticString = #fileID, line: Swift.UInt = #line)";
        var flags = SwiftInterfaceAccessParser.ExtractAutoclosureFlags(line);
        Assert.NotNull(flags);
        Assert.Equal(3, flags!.Count);
        Assert.True(flags[0]);   // message is @autoclosure
        Assert.False(flags[1]);  // fileID is not
        Assert.False(flags[2]);  // line is not
    }

    [Fact]
    public void ExtractAutoclosureFlags_MultipleAutoclosures()
    {
        var line = "final public func assert(_ condition: @autoclosure () -> Swift.Bool, _ message: @autoclosure () -> Swift.String = String(), fileID: Swift.StaticString = #fileID, line: Swift.UInt = #line)";
        var flags = SwiftInterfaceAccessParser.ExtractAutoclosureFlags(line);
        Assert.NotNull(flags);
        Assert.Equal(4, flags!.Count);
        Assert.True(flags[0]);   // condition is @autoclosure
        Assert.True(flags[1]);   // message is @autoclosure
        Assert.False(flags[2]);  // fileID is not
        Assert.False(flags[3]);  // line is not
    }

    [Fact]
    public void ExtractAutoclosureFlags_NoAutoclosure_ReturnsNull()
    {
        var line = "public func foo(x: Swift.Int, callback: () -> Swift.Void)";
        var flags = SwiftInterfaceAccessParser.ExtractAutoclosureFlags(line);
        Assert.Null(flags);
    }

    [Fact]
    public void ExtractAutoclosureFlags_EscapingAutoclosure()
    {
        var line = "public func validate(contentType: @autoclosure @escaping @Sendable () -> Swift.Set<Swift.String>) -> Self";
        var flags = SwiftInterfaceAccessParser.ExtractAutoclosureFlags(line);
        Assert.NotNull(flags);
        Assert.Single(flags!);
        Assert.True(flags[0]);
    }

    [Fact]
    public void ExtractAutoclosureFlags_TruncatedMultiLine_ReturnsNull()
    {
        // Multi-line free function where only the first line is passed — unmatched paren
        var line = "public func foo(_ condition: @autoclosure () -> Swift.Bool,";
        var flags = SwiftInterfaceAccessParser.ExtractAutoclosureFlags(line);
        Assert.Null(flags);
    }

    [Fact]
    public void ExtractParameterDefaults_TruncatedMultiLine_ReturnsNull()
    {
        // Same scenario for ExtractParameterDefaults
        var line = "public func foo(x: Swift.Int = 10,";
        var defaults = SwiftInterfaceAccessParser.ExtractParameterDefaults(line);
        Assert.Null(defaults);
    }

    [Fact]
    public void GetAutoclosureParameters_MultilineFreeFuncDoesNotCrash()
    {
        // Multi-line free function at top level should not crash the scanner
        var swiftInterface = """
            public func longSignature(_ condition: @autoclosure () -> Swift.Bool,
                                      _ message: @autoclosure () -> Swift.String) -> Swift.Void {
            }
            final public class SomeLogger {
              final public func warn(_ message: @autoclosure () -> Swift.String)
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            // Should not throw — the multi-line free func is gracefully skipped
            var result = SwiftInterfaceAccessParser.GetAutoclosureParameters(path);
            // The class member should still be detected
            Assert.True(result.ContainsKey("SomeLogger.warn(_:)"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetAutoclosureParameters_ParsesFromSwiftInterface()
    {
        var swiftInterface = """
            final public class LottieLogger {
              final public func assert(_ condition: @autoclosure () -> Swift.Bool, _ message: @autoclosure () -> Swift.String = String(), fileID: Swift.StaticString = #fileID, line: Swift.UInt = #line)
              final public func warn(_ message: @autoclosure () -> Swift.String = String(), fileID: Swift.StaticString = #fileID, line: Swift.UInt = #line)
              final public func info(_ message: @autoclosure () -> Swift.String = String())
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAutoclosureParameters(path);
            Assert.Equal(3, result.Count);

            // assert has 2 @autoclosure params out of 4
            Assert.True(result.ContainsKey("LottieLogger.assert(_:_:fileID:line:)"));
            var assertFlags = result["LottieLogger.assert(_:_:fileID:line:)"];
            Assert.Equal(4, assertFlags.Count);
            Assert.True(assertFlags[0]);
            Assert.True(assertFlags[1]);
            Assert.False(assertFlags[2]);
            Assert.False(assertFlags[3]);

            // warn has 1 @autoclosure param out of 3
            Assert.True(result.ContainsKey("LottieLogger.warn(_:fileID:line:)"));
            var warnFlags = result["LottieLogger.warn(_:fileID:line:)"];
            Assert.True(warnFlags[0]);
            Assert.False(warnFlags[1]);

            // info has 1 @autoclosure param out of 1
            Assert.True(result.ContainsKey("LottieLogger.info(_:)"));
            var infoFlags = result["LottieLogger.info(_:)"];
            Assert.Single(infoFlags);
            Assert.True(infoFlags[0]);
        }
        finally { File.Delete(path); }
    }

    // ===== GetActorIsolatedMembers with Custom Actors Tests =====

    [Fact]
    public void GetActorIsolatedMembers_DetectsCustomActorAnnotation()
    {
        var swiftInterface = """
            @_hasMissingDesignatedInitializers @globalActor public actor ProcessingActor {
              public static let shared: BlinkID.ProcessingActor
            }
            @_hasMissingDesignatedInitializers final public class BlinkIDSession {
              final public func resumeActiveProcessing()
              @BlinkID.ProcessingActor final public func process(inputImage: BlinkID.InputImage) -> BlinkID.FrameProcessResult
              @BlinkID.ProcessingActor final public func reset() throws
              @BlinkID.ProcessingActor final public func getResult() -> BlinkID.BlinkIDScanningResult
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var customActors = new HashSet<string> { "ProcessingActor" };
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(path, customActors, out var mainActorMembers);

            // Should detect 3 custom-actor-isolated methods
            Assert.Contains("BlinkIDSession.process(inputImage:)", result);
            Assert.Contains("BlinkIDSession.reset()", result);
            Assert.Contains("BlinkIDSession.getResult()", result);

            // Non-isolated method should NOT be included
            Assert.DoesNotContain("BlinkIDSession.resumeActiveProcessing()", result);

            // Custom actor members should NOT be in mainActorMembers
            Assert.DoesNotContain("BlinkIDSession.process(inputImage:)", mainActorMembers);
            Assert.DoesNotContain("BlinkIDSession.reset()", mainActorMembers);
            Assert.DoesNotContain("BlinkIDSession.getResult()", mainActorMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetActorIsolatedMembers_WithoutCustomActors_OnlyDetectsMainActor()
    {
        var swiftInterface = """
            final public class SomeClass {
              @BlinkID.ProcessingActor final public func actorMethod() -> Swift.Int
              @MainActor final public func mainActorMethod() -> Swift.String
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            // Without custom actors, only @MainActor should be detected
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(path);
            Assert.Contains("SomeClass.mainActorMethod()", result);
            Assert.DoesNotContain("SomeClass.actorMethod()", result);

            // With custom actors, both should be detected
            var customActors = new HashSet<string> { "ProcessingActor" };
            var resultWithCustom = SwiftInterfaceAccessParser.GetActorIsolatedMembers(path, customActors, out var mainActorMembers);
            Assert.Contains("SomeClass.mainActorMethod()", resultWithCustom);
            Assert.Contains("SomeClass.actorMethod()", resultWithCustom);

            // mainActorMembers should only contain @MainActor, not custom actors
            Assert.Contains("SomeClass.mainActorMethod()", mainActorMembers);
            Assert.DoesNotContain("SomeClass.actorMethod()", mainActorMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetActorIsolatedMembers_CustomActorStaticMethods()
    {
        var swiftInterface = """
            @_hasMissingDesignatedInitializers final public class BlinkIDSdk : Swift.Sendable {
              @BlinkID.ProcessingActor public static func terminateBlinkIDSdk()
              public static func refreshLicenseLease() async throws
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var customActors = new HashSet<string> { "ProcessingActor" };
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(path, customActors, out var mainActorMembers);
            Assert.Contains("BlinkIDSdk.terminateBlinkIDSdk()", result);
            Assert.DoesNotContain("BlinkIDSdk.refreshLicenseLease()", result);
            // Custom actor should NOT be in mainActorMembers
            Assert.DoesNotContain("BlinkIDSdk.terminateBlinkIDSdk()", mainActorMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetActorIsolatedMembers_TopLevelMainActorFreeFunction_SingleLine()
    {
        var swiftInterface = """
            @_Concurrency.MainActor public func mainActorFreeFunction() -> Swift.String
            public func regularFreeFunction() -> Swift.Int
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(path);
            Assert.Contains("mainActorFreeFunction()", result);
            Assert.DoesNotContain("regularFreeFunction()", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetActorIsolatedMembers_TopLevelMainActorFreeFunction_Multiline()
    {
        // Multiline signature: opening paren on first line, closing paren on a later line
        var swiftInterface = """
            @_Concurrency.MainActor public func multilineActorFunc(
                _ value: Swift.Int,
                label: Swift.String) -> Swift.Bool
            public func regularFreeFunction() -> Swift.Int
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(path);
            Assert.Contains("multilineActorFunc(_:label:)", result);
            Assert.DoesNotContain("regularFreeFunction()", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetActorIsolatedMembers_TopLevelMainActorFreeFunction_MainActorMembersOut()
    {
        // Verify the mainActorMembers out-parameter includes top-level free functions
        var swiftInterface = """
            @_Concurrency.MainActor public func mainActorFreeFunc() -> Swift.String
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(
                path, customActorTypeNames: null, out var mainActorMembers);
            Assert.Contains("mainActorFreeFunc()", result);
            Assert.Contains("mainActorFreeFunc()", mainActorMembers);
        }
        finally { File.Delete(path); }
    }

    // === GetPublicMemberNames tests ===

    [Fact]
    public void GetPublicMemberNames_CollectsPublicFuncAndVar()
    {
        var swiftInterface = """
            public struct MyStruct {
              public var tintColor: UIKit.UIColor
              public func doWork(_ value: Swift.Int) -> Swift.Bool
              internal func secret() -> Swift.Int
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            SwiftInterfaceAccessParser.GetInternalMembers(path, out var publicMembers);
            Assert.Contains("MyStruct.tintColor", publicMembers);
            Assert.Contains("MyStruct.doWork(_:)", publicMembers);
            Assert.DoesNotContain("MyStruct.secret()", publicMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetPublicMemberNames_CollectsStaticMembers()
    {
        var swiftInterface = """
            public class Registry {
              public static let shared: MyModule.Registry
              public static var instanceCount: Swift.Int32
              public var name: Swift.String
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            SwiftInterfaceAccessParser.GetInternalMembers(path, out var publicMembers);
            Assert.Contains("Registry.shared", publicMembers);
            Assert.Contains("Registry.instanceCount", publicMembers);
            Assert.Contains("Registry.name", publicMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetPublicMemberNames_CollectsPublicInit()
    {
        var swiftInterface = """
            public struct Point {
              public init(x: Swift.Int, y: Swift.Int)
              public var x: Swift.Int
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            SwiftInterfaceAccessParser.GetInternalMembers(path, out var publicMembers);
            Assert.Contains("Point.init(x:y:)", publicMembers);
            Assert.Contains("Point.x", publicMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetPublicMemberNames_CollectsExtensionMembers()
    {
        var swiftInterface = """
            public class Foo {
              public var name: Swift.String
            }
            extension MyModule.Foo {
              public func extended() -> Swift.Bool
              public static var count: Swift.Int
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            SwiftInterfaceAccessParser.GetInternalMembers(path, out var publicMembers);
            Assert.Contains("Foo.name", publicMembers);
            Assert.Contains("Foo.extended()", publicMembers);
            Assert.Contains("Foo.count", publicMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetPublicMemberNames_CollectsBacktickEscapedIdentifiers()
    {
        var swiftInterface = """
            public struct KeywordTest {
              public var `operator`: Swift.String
              public var `class`: Swift.String
              public var normal: Swift.Int
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            SwiftInterfaceAccessParser.GetInternalMembers(path, out var publicMembers);
            Assert.Contains("KeywordTest.operator", publicMembers);
            Assert.Contains("KeywordTest.class", publicMembers);
            Assert.Contains("KeywordTest.normal", publicMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetPublicMemberNames_HandlesAnnotationPrefixes()
    {
        var swiftInterface = """
            public class ViewModel {
              @_Concurrency.MainActor public var title: Swift.String
              @objc @IBInspectable @_Concurrency.MainActor @preconcurrency dynamic public var count: Swift.Int
              nonisolated public var typeName: Swift.String
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            SwiftInterfaceAccessParser.GetInternalMembers(path, out var publicMembers);
            Assert.Contains("ViewModel.title", publicMembers);
            Assert.Contains("ViewModel.count", publicMembers);
            Assert.Contains("ViewModel.typeName", publicMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetPublicMemberNames_CollectsFreeFunctions()
    {
        var swiftInterface = """
            public func processKeywords(in input: Swift.String) -> Swift.String
            public var globalCount: Swift.Int
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            SwiftInterfaceAccessParser.GetInternalMembers(path, out var publicMembers);
            Assert.Contains("processKeywords(in:)", publicMembers);
            Assert.Contains("globalCount", publicMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetPublicMemberNames_MissingDesignatedInitializersTypeHasNoInit()
    {
        // @_hasMissingDesignatedInitializers means init is NOT public
        var swiftInterface = """
            @_hasMissingDesignatedInitializers public class AppearanceConfig {
              public var tintColor: UIKit.UIColor
              @objc deinit
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            SwiftInterfaceAccessParser.GetInternalMembers(path, out var publicMembers);
            Assert.Contains("AppearanceConfig.tintColor", publicMembers);
            // No init should be in the public set
            Assert.DoesNotContain("AppearanceConfig.init()", publicMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetPublicMemberNames_CollectsSetterRestrictedProperties()
    {
        var swiftInterface = """
            public class Config {
              public internal(set) var name: Swift.String
              public private(set) var version: Swift.Int
              public private(set) static var shared: MyModule.Config
              public var normal: Swift.Bool
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            SwiftInterfaceAccessParser.GetInternalMembers(path, out var publicMembers);
            Assert.Contains("Config.name", publicMembers);
            Assert.Contains("Config.version", publicMembers);
            Assert.Contains("Config.shared", publicMembers);
            Assert.Contains("Config.normal", publicMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetPublicMemberNames_HandlesMultilineSignatures()
    {
        var swiftInterface = """
            public class Encoder {
              public func encode(_ value: Swift.String,
                withRootKey rootKey: Swift.String?,
                header: MyModule.Header?) throws -> Foundation.Data
              public func simple() -> Swift.Int
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            SwiftInterfaceAccessParser.GetInternalMembers(path, out var publicMembers);
            Assert.Contains("Encoder.encode(_:withRootKey:header:)", publicMembers);
            Assert.Contains("Encoder.simple()", publicMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetPublicMemberNames_HandlesMultilineInit()
    {
        var swiftInterface = """
            public struct Point {
              public init(x: Swift.Int,
                y: Swift.Int,
                z: Swift.Int)
              public var x: Swift.Int
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            SwiftInterfaceAccessParser.GetInternalMembers(path, out var publicMembers);
            Assert.Contains("Point.init(x:y:z:)", publicMembers);
            Assert.Contains("Point.x", publicMembers);
        }
        finally { File.Delete(path); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GetSubscriptLabels tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void GetSubscriptLabels_SimpleUnlabeledSubscript()
    {
        var swiftInterface = """
            public class Container {
              public subscript(_ index: Swift.Int) -> Swift.String { get }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSubscriptLabels(path);
            Assert.Single(result);
            Assert.True(result.ContainsKey("Container.subscript(_:)"));
            Assert.Equal(new[] { "_" }, result["Container.subscript(_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSubscriptLabels_LabeledSubscript_BitAt()
    {
        // CryptoSwift pattern: subscript(bitAt index: Int) -> Bool
        var swiftInterface = """
            public class AES {
              public subscript(bitAt index: Swift.Int) -> Swift.Bool { get set }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSubscriptLabels(path);
            Assert.Single(result);
            Assert.True(result.ContainsKey("AES.subscript(bitAt:)"));
            Assert.Equal(new[] { "bitAt" }, result["AES.subscript(bitAt:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSubscriptLabels_MultipleLabels_ObjectMapper()
    {
        // ObjectMapper pattern: subscript(key:nested:delimiter:)
        // In Swift subscripts, single-name params have NO label (key: String → _),
        // while two-name params have explicit labels (nested nested: String → nested)
        var swiftInterface = """
            public class Map {
              public subscript(key: Swift.String, nested nested: Swift.String?, delimiter delimiter: Swift.String) -> Any? { get set }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSubscriptLabels(path);
            Assert.Single(result);
            Assert.True(result.ContainsKey("Map.subscript(_:nested:delimiter:)"));
            Assert.Equal(new[] { "_", "nested", "delimiter" }, result["Map.subscript(_:nested:delimiter:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSubscriptLabels_MultipleLabels_IgnoreNil()
    {
        // ObjectMapper pattern: subscript(key:delimiter:ignoreNil:)
        // First param (key: String) is single-name → no label (_)
        var swiftInterface = """
            public class Map {
              public subscript(key: Swift.String, delimiter delimiter: Swift.String, ignoreNil ignoreNil: Swift.Bool) -> Any? { get set }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSubscriptLabels(path);
            Assert.Single(result);
            Assert.True(result.ContainsKey("Map.subscript(_:delimiter:ignoreNil:)"));
            Assert.Equal(new[] { "_", "delimiter", "ignoreNil" }, result["Map.subscript(_:delimiter:ignoreNil:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSubscriptLabels_MultipleSubscripts()
    {
        // Both subscripts have single-name params → both get label "_".
        // subscript(_ index: Int) and subscript(key: String) have the same
        // calling convention in Swift (no argument label). The second overwrites
        // the first in the dictionary (same key pattern), which is harmless since
        // both map to "_" anyway.
        var swiftInterface = """
            public class Container {
              public subscript(_ index: Swift.Int) -> Swift.String { get }
              public subscript(key: Swift.String) -> Swift.Int? { get set }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSubscriptLabels(path);
            // Both produce key "Container.subscript(_:)" — second overwrites first
            Assert.Single(result);
            Assert.True(result.ContainsKey("Container.subscript(_:)"));
            Assert.Equal(new[] { "_" }, result["Container.subscript(_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSubscriptLabels_NestedType()
    {
        var swiftInterface = """
            public class Outer {
              public class Inner {
                public subscript(bitAt index: Swift.Int) -> Swift.Bool { get }
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSubscriptLabels(path);
            Assert.Single(result);
            Assert.True(result.ContainsKey("Outer.Inner.subscript(bitAt:)"));
            Assert.Equal(new[] { "bitAt" }, result["Outer.Inner.subscript(bitAt:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSubscriptLabels_Extension()
    {
        var swiftInterface = """
            public class AES {
              public var blockSize: Swift.Int { get }
            }
            extension CryptoSwift.AES {
              public subscript(bitAt index: Swift.Int) -> Swift.Bool { get set }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSubscriptLabels(path);
            Assert.Single(result);
            Assert.True(result.ContainsKey("AES.subscript(bitAt:)"));
            Assert.Equal(new[] { "bitAt" }, result["AES.subscript(bitAt:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSubscriptLabels_GenericSubscript()
    {
        // Single-name param (key: String) → no label
        var swiftInterface = """
            public class Cache {
              public subscript<T>(key: Swift.String) -> T? { get set }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSubscriptLabels(path);
            Assert.Single(result);
            Assert.True(result.ContainsKey("Cache.subscript(_:)"));
            Assert.Equal(new[] { "_" }, result["Cache.subscript(_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSubscriptLabels_StaticSubscript()
    {
        // Single-name param (key: String) → no label
        var swiftInterface = """
            public class Registry {
              public static subscript(key: Swift.String) -> Swift.Int { get }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSubscriptLabels(path);
            Assert.Single(result);
            Assert.True(result.ContainsKey("Registry.subscript(_:)"));
            Assert.Equal(new[] { "_" }, result["Registry.subscript(_:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSubscriptLabels_MultiLineSubscript()
    {
        // Multi-line: first param single-name → _, others two-name → labeled
        var swiftInterface = """
            public class Map {
              public subscript(key: Swift.String,
                nested nested: Swift.String?,
                delimiter delimiter: Swift.String) -> Any? { get set }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSubscriptLabels(path);
            Assert.Single(result);
            Assert.True(result.ContainsKey("Map.subscript(_:nested:delimiter:)"));
            Assert.Equal(new[] { "_", "nested", "delimiter" }, result["Map.subscript(_:nested:delimiter:)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSubscriptLabels_EmptyFileReturnsEmptyDictionary()
    {
        var path = WriteTempFile("");
        try
        {
            var result = SwiftInterfaceAccessParser.GetSubscriptLabels(path);
            Assert.Empty(result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSubscriptLabels_MissingFileReturnsEmptyDictionary()
    {
        var result = SwiftInterfaceAccessParser.GetSubscriptLabels("/tmp/nonexistent_file_12345.swiftinterface");
        Assert.Empty(result);
    }

    #region HasVariadicParameterInSignature Tests

    [Theory]
    [InlineData("  public static func startsWith(_ prefixes: Swift.String..., caseSensitive: Swift.Bool = false) -> any SwiftyBeaver.FilterType", true)]
    [InlineData("  public static func buildBlock(_ disposables: any RxSwift.Disposable...) -> [any RxSwift.Disposable]", true)]
    [InlineData("  public func insert(_ disposables: any RxSwift.Disposable...)", true)]
    [InlineData("  public func process(_ items: [Swift.String])", false)]
    [InlineData("  public func log(_ message: Swift.String, level: Swift.Int)", false)]
    [InlineData("  public init(items: [Swift.Int])", false)]
    public void HasVariadicParameterInSignature_DetectsVariadics(string line, bool expected)
    {
        Assert.Equal(expected, SwiftInterfaceAccessParser.HasVariadicParameterInSignature(line));
    }

    [Fact]
    public void GetVariadicMembers_DetectsVariadicFromFile()
    {
        var content = @"
public class FilterFactory {
  public static func startsWith(_ prefixes: Swift.String..., caseSensitive: Swift.Bool = false) -> any FilterType
  public static func process(_ items: [Swift.String]) -> Swift.Void
}
";
        var path = WriteTempFile(content);
        try
        {
            var result = SwiftInterfaceAccessParser.GetVariadicMembers(path);
            Assert.Contains("FilterFactory.startsWith(_:caseSensitive:)", result);
            Assert.DoesNotContain("FilterFactory.process(_:)", result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetVariadicMembers_NestedType_UsesQualifiedPath()
    {
        var content = @"
public class DisposeBag {
  public struct DisposableBuilder {
    public static func buildBlock(_ disposables: any Disposable...) -> [any Disposable]
  }
}
";
        var path = WriteTempFile(content);
        try
        {
            var result = SwiftInterfaceAccessParser.GetVariadicMembers(path);
            Assert.Contains("DisposeBag.DisposableBuilder.buildBlock(_:)", result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetVariadicMembers_MissingFile_ReturnsEmpty()
    {
        var result = SwiftInterfaceAccessParser.GetVariadicMembers("/tmp/nonexistent_file_12345.swiftinterface");
        Assert.Empty(result);
    }

    [Fact]
    public void GetVariadicMembers_MultiLineFreeFunction_Detected()
    {
        // Multi-line free functions at module level should be reassembled and checked for variadic
        var swiftInterface =
            "public func broadcast(\n" +
            "  _ values: Swift.Int...\n" +
            ")\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetVariadicMembers(path);
            Assert.Contains("broadcast(_:)", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetVariadicMembers_MultiLineFreeFunctionNonVariadic_NotDetected()
    {
        // Multi-line free function without variadic should NOT be detected
        var swiftInterface =
            "public func process(\n" +
            "  items: [Swift.String]\n" +
            ")\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetVariadicMembers(path);
            Assert.Empty(result);
        }
        finally { File.Delete(path); }
    }

    #endregion

    // === Protocol member @MainActor detection tests ===

    [Fact]
    public void GetActorIsolatedMembers_ProtocolMemberMainActor_DetectedWithoutAccessModifier()
    {
        // Protocol members in .swiftinterface have no access modifier (no public/open).
        // The parser must detect @MainActor on bare func/var/init declarations.
        var swiftInterface = """
            public protocol PagingMenuDelegate : AnyObject {
              @_Concurrency.MainActor func selectContent(pagingItem: any Parchment.PagingItem, direction: Parchment.PagingDirection, animated: Swift.Bool)
              @_Concurrency.MainActor func removeContent()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(
                path, customActorTypeNames: null, out var mainActorMembers);
            Assert.Contains("PagingMenuDelegate.removeContent()", result);
            Assert.Contains("PagingMenuDelegate.removeContent()", mainActorMembers);
            Assert.Contains("PagingMenuDelegate.selectContent(pagingItem:direction:animated:)", result);
            Assert.Contains("PagingMenuDelegate.selectContent(pagingItem:direction:animated:)", mainActorMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetActorIsolatedMembers_ProtocolMemberMainActor_BareVarDetected()
    {
        // Protocol property requirements have no access modifier.
        var swiftInterface = """
            public protocol MyDelegate {
              @_Concurrency.MainActor var title: Swift.String { get }
              @_Concurrency.MainActor var count: Swift.Int { get set }
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(
                path, customActorTypeNames: null, out var mainActorMembers);
            Assert.Contains("MyDelegate.title", result);
            Assert.Contains("MyDelegate.title", mainActorMembers);
            Assert.Contains("MyDelegate.count", result);
            Assert.Contains("MyDelegate.count", mainActorMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetActorIsolatedMembers_ProtocolMemberMainActor_BareInitDetected()
    {
        // Protocol init requirements have no access modifier.
        var swiftInterface = """
            public protocol MyFactory {
              @_Concurrency.MainActor init(value: Swift.Int)
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(
                path, customActorTypeNames: null, out var mainActorMembers);
            Assert.Contains("MyFactory.init(value:)", result);
            Assert.Contains("MyFactory.init(value:)", mainActorMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetActorIsolatedMembers_ProtocolMemberMainActor_NonAnnotatedNotDetected()
    {
        // Protocol members without @MainActor should NOT appear in results.
        var swiftInterface = """
            public protocol MyProtocol : AnyObject {
              @_Concurrency.MainActor func isolatedMethod()
              func normalMethod()
              var normalProp: Swift.Int { get }
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(
                path, customActorTypeNames: null, out var mainActorMembers);
            Assert.Contains("MyProtocol.isolatedMethod()", result);
            Assert.DoesNotContain("MyProtocol.normalMethod()", result);
            Assert.DoesNotContain("MyProtocol.normalProp", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetActorIsolatedMembers_ProtocolMemberMainActor_MultiLineSignature()
    {
        // Protocol members with multi-line signatures.
        var swiftInterface = """
            public protocol MyDelegate {
              @_Concurrency.MainActor func configure(title: Swift.String,
                                                     count: Swift.Int,
                                                     enabled: Swift.Bool)
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(
                path, customActorTypeNames: null, out var mainActorMembers);
            Assert.Contains("MyDelegate.configure(title:count:enabled:)", result);
            Assert.Contains("MyDelegate.configure(title:count:enabled:)", mainActorMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetActorIsolatedMembers_ProtocolMemberMainActor_AnnotationOnOwnLine()
    {
        // @MainActor annotation on its own line, followed by bare func on next line.
        var swiftInterface = """
            public protocol MyDelegate {
              @_Concurrency.MainActor
              func doWork(value: Swift.Int)
              @_Concurrency.MainActor
              var title: Swift.String { get }
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(
                path, customActorTypeNames: null, out var mainActorMembers);
            Assert.Contains("MyDelegate.doWork(value:)", result);
            Assert.Contains("MyDelegate.doWork(value:)", mainActorMembers);
            Assert.Contains("MyDelegate.title", result);
            Assert.Contains("MyDelegate.title", mainActorMembers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetActorIsolatedMembers_ProtocolMemberMainActor_MixedWithPublicMembers()
    {
        // Mix of protocol members (bare) and class members (public) — both should be detected.
        var swiftInterface = """
            public protocol MyDelegate {
              @_Concurrency.MainActor func protocolMethod()
            }
            public class MyClass {
              @_Concurrency.MainActor public func classMethod()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(
                path, customActorTypeNames: null, out var mainActorMembers);
            Assert.Contains("MyDelegate.protocolMethod()", result);
            Assert.Contains("MyDelegate.protocolMethod()", mainActorMembers);
            Assert.Contains("MyClass.classMethod()", result);
            Assert.Contains("MyClass.classMethod()", mainActorMembers);
        }
        finally { File.Delete(path); }
    }

    // ===== GetEnumRawValues Tests =====

    [Fact]
    public void GetEnumRawValues_ExtractsStringLiterals()
    {
        var swiftInterface = """
            public enum LogLevel : Swift.String {
              case debug = "[DEBUG]"
              case info = "[INFO]"
              case warning = "[WARNING]"
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetEnumRawValues(path);
            Assert.Equal("[DEBUG]", result["LogLevel.debug"]);
            Assert.Equal("[INFO]", result["LogLevel.info"]);
            Assert.Equal("[WARNING]", result["LogLevel.warning"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetEnumRawValues_SkipsCasesWithoutExplicitRawValue()
    {
        var swiftInterface = """
            public enum Status : Swift.String {
              case active
              case inactive = "not_active"
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetEnumRawValues(path);
            Assert.False(result.ContainsKey("Status.active"));
            Assert.Equal("not_active", result["Status.inactive"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetEnumRawValues_NestedEnum_UsesFullyQualifiedName()
    {
        var swiftInterface = """
            public struct Config {
              public enum Mode : Swift.String {
                case fast = "turbo"
                case slow = "crawl"
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetEnumRawValues(path);
            Assert.Equal("turbo", result["Config.Mode.fast"]);
            Assert.Equal("crawl", result["Config.Mode.slow"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetEnumRawValues_EscapedCharacters_PreservesEscapeSequences()
    {
        // Swift .swiftinterface escape sequences should be preserved as-is (not unescaped),
        // because they map 1:1 to C# escape sequences and are emitted directly into C# string literals.
        var swiftInterface = "public enum Delimiter : Swift.String {\n  case tab = \"\\t\"\n  case newline = \"\\n\"\n}\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetEnumRawValues(path);
            Assert.Equal("\\t", result["Delimiter.tab"]);
            Assert.Equal("\\n", result["Delimiter.newline"]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\\t", "\\t")]       // tab escape preserved
    [InlineData("\\n", "\\n")]       // newline escape preserved
    [InlineData("\\\\", "\\\\")]     // backslash escape preserved
    [InlineData("\\\"", "\\\"")]     // quote escape preserved
    [InlineData("hello\\tworld", "hello\\tworld")] // mixed content preserved
    public void GetEnumRawValues_EscapeSequences_RoundTripForCSharpEmission(string swiftEscaped, string expected)
    {
        // Verify that escape sequences from .swiftinterface are preserved verbatim,
        // so they can be safely interpolated into C# string literals like: $"\"{rawValue}\""
        var swiftInterface = $"public enum E : Swift.String {{\n  case x = \"{swiftEscaped}\"\n}}\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetEnumRawValues(path);
            Assert.Equal(expected, result["E.x"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetEnumRawValues_IntRawValue_NotExtracted()
    {
        var swiftInterface = """
            public enum Priority : Swift.Int {
              case low = 0
              case high = 10
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetEnumRawValues(path);
            // Int raw values are not string literals — should not be extracted
            Assert.Empty(result);
        }
        finally { File.Delete(path); }
    }

    private static string WriteTempFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }

    #region GetProtocolsWithConventionClosures Tests

    [Fact]
    public void GetProtocolsWithConventionClosures_TypealiasConventionC_DetectsProtocol()
    {
        var path = WriteTempFile("""
            public typealias FTS5TokenCallback = @convention(c) (_ context: Swift.UnsafeMutableRawPointer?, _ flags: Swift.CInt) -> Swift.CInt
            public protocol FTS5Tokenizer : AnyObject {
              func tokenize(context: Swift.UnsafeMutableRawPointer?, tokenCallback: GRDB.FTS5TokenCallback) -> Swift.CInt
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithConventionClosures(path);
            Assert.Contains("FTS5Tokenizer", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithConventionClosures_DirectConventionC_DetectsProtocol()
    {
        var path = WriteTempFile("""
            public protocol DirectConvention {
              func process(callback: @convention(c) (Swift.CInt) -> Swift.CInt)
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithConventionClosures(path);
            Assert.Contains("DirectConvention", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithConventionClosures_ConventionBlock_DetectsProtocol()
    {
        var path = WriteTempFile("""
            public typealias ObjCCallback = @convention(block) () -> Swift.Void
            public protocol ObjCDelegate {
              func handle(callback: ObjCCallback)
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithConventionClosures(path);
            Assert.Contains("ObjCDelegate", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithConventionClosures_NoConventionC_ReturnsEmpty()
    {
        var path = WriteTempFile("""
            public protocol NormalProtocol {
              func doWork(callback: @escaping () -> Swift.Void)
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithConventionClosures(path);
            Assert.Empty(result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithConventionClosures_ConventionCOutsideProtocol_NotDetected()
    {
        var path = WriteTempFile("""
            public typealias CCallback = @convention(c) (Swift.CInt) -> Swift.CInt
            public protocol CleanProtocol {
              func doWork() -> Swift.Int
            }
            public func usesCallback(cb: CCallback) {}
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithConventionClosures(path);
            Assert.Empty(result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithConventionClosures_TypealiasSubstringNoMatch_NotDetected()
    {
        // A typealias "CB" with @convention(c) should NOT match a parameter type "CBRoot"
        var path = WriteTempFile("""
            public typealias CB = @convention(c) (Swift.CInt) -> Swift.CInt
            public protocol SubstringProtocol {
              func process(handler: CBRoot)
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithConventionClosures(path);
            Assert.Empty(result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithConventionClosures_TypealiasOnContinuationLine_DetectsProtocol()
    {
        // swiftinterface signatures can wrap across lines — the convention-c typealias
        // reference may appear on a continuation line, not the func line itself.
        var path = WriteTempFile("""
            public typealias FTS5TokenCallback = @convention(c) (_ context: Swift.UnsafeMutableRawPointer?, _ flags: Swift.CInt) -> Swift.CInt
            public protocol FTS5Tokenizer : AnyObject {
              func tokenize(
                _ text: Swift.String,
                context: Swift.UnsafeMutableRawPointer?,
                tokenCallback: GRDB.FTS5TokenCallback
              ) -> Swift.CInt
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithConventionClosures(path);
            Assert.Contains("FTS5Tokenizer", result);
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region GetProtocolsWithUnsatisfiedHiddenRequirements Tests

    private const string TestModuleHeader =
        "// swift-interface-format-version: 1.0\n" +
        "// swift-module-flags: -target arm64-apple-ios15.0-simulator -module-name TestModule\n";

    [Fact]
    public void GetProtocolsWithUnsatisfiedHiddenRequirements_UnderscoredVarNoExtension_ReturnsRequirementName()
    {
        // Mirrors RealityFoundation.MaterialFunction: __linkSPI is a public protocol
        // requirement in the swiftinterface but stripped from the ABI JSON, with no
        // extension supplying a default. The conformance can't be satisfied.
        var path = WriteTempFile(TestModuleHeader + """
            public protocol MaterialFunction {
              var name: Swift.String { get set }
              var __linkSPI: Swift.Bool { get }
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithUnsatisfiedHiddenRequirements(path);
            Assert.True(result.TryGetValue("MaterialFunction", out var unsatisfied));
            Assert.Contains("__linkSPI", unsatisfied);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithUnsatisfiedHiddenRequirements_UnderscoredVarWithExtensionDefault_NotDetected()
    {
        // Same-module qualified extension supplies a default, so the protocol's
        // __-prefixed requirement is satisfied. Must NOT be flagged for skip.
        var path = WriteTempFile(TestModuleHeader + """
            public protocol Component {
              static var __typeName: Swift.String { get }
              static var __size: Swift.Int { get }
            }
            extension TestModule.Component {
              public static var __typeName: Swift.String {
                get
              }
              public static var __size: Swift.Int {
                get
              }
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithUnsatisfiedHiddenRequirements(path);
            Assert.DoesNotContain("Component", result.Keys);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithUnsatisfiedHiddenRequirements_ForeignModuleExtension_DoesNotSatisfy()
    {
        // P2: a public extension on a same-simple-name protocol declared in a foreign
        // module never satisfies the local protocol's hidden requirement. The local
        // Component must still be flagged because no LOCAL extension provides defaults.
        var path = WriteTempFile(TestModuleHeader + """
            public protocol Component {
              static var __typeName: Swift.String { get }
            }
            extension OtherModule.Component {
              public static var __typeName: Swift.String { get { "" } }
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithUnsatisfiedHiddenRequirements(path);
            Assert.True(result.TryGetValue("Component", out var unsatisfied));
            Assert.Contains("__typeName", unsatisfied);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithUnsatisfiedHiddenRequirements_UnqualifiedExtensionDefault_Satisfies()
    {
        // Unqualified `extension Foo` always refers to the same-module Foo (Swift
        // requires qualification for cross-module). Defaults declared this way satisfy.
        var path = WriteTempFile(TestModuleHeader + """
            public protocol Foo {
              var __token: Swift.Int { get }
            }
            extension Foo {
              public var __token: Swift.Int { get { 0 } }
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithUnsatisfiedHiddenRequirements(path);
            Assert.DoesNotContain("Foo", result.Keys);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithUnsatisfiedHiddenRequirements_UnderscoredFunc_ReturnsRequirementName()
    {
        var path = WriteTempFile(TestModuleHeader + """
            public protocol __SyncService {
              func __fromCore(peerID: Swift.OpaquePointer) -> Swift.Bool
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithUnsatisfiedHiddenRequirements(path);
            Assert.True(result.TryGetValue("__SyncService", out var unsatisfied));
            Assert.Contains("__fromCore", unsatisfied);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithUnsatisfiedHiddenRequirements_PartialDefaults_OnlyReturnsMissing()
    {
        // Extension provides a default for one of two underscored requirements — the
        // remaining unsatisfied requirement is reported, the satisfied one is not.
        var path = WriteTempFile(TestModuleHeader + """
            public protocol PartiallyDefaulted {
              var __coveredByDefault: Swift.Int { get }
              var __stillMissing: Swift.Bool { get }
            }
            extension TestModule.PartiallyDefaulted {
              public var __coveredByDefault: Swift.Int { get { 0 } }
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithUnsatisfiedHiddenRequirements(path);
            Assert.True(result.TryGetValue("PartiallyDefaulted", out var unsatisfied));
            Assert.Contains("__stillMissing", unsatisfied);
            Assert.DoesNotContain("__coveredByDefault", unsatisfied);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithUnsatisfiedHiddenRequirements_NoUnderscoredRequirements_ReturnsEmpty()
    {
        var path = WriteTempFile(TestModuleHeader + """
            public protocol PlainProto {
              var name: Swift.String { get }
              func doWork()
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithUnsatisfiedHiddenRequirements(path);
            Assert.Empty(result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetProtocolsWithUnsatisfiedHiddenRequirements_UnderscoredVarOutsideProtocol_NotDetected()
    {
        var path = WriteTempFile(TestModuleHeader + """
            public class Entity {
            }
            extension TestModule.Entity {
              public var __interactions: [Swift.String] { get { [] } }
            }
            public protocol PlainProto {
              var name: Swift.String { get }
            }
            """);
        try
        {
            var result = SwiftInterfaceAccessParser.GetProtocolsWithUnsatisfiedHiddenRequirements(path);
            Assert.Empty(result);
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region GetForeignTypeExtensionMembers Tests

    [Fact]
    public void GetForeignTypeExtensionMembers_SkipsMembersOfNestedTypeDeclaredInsideExtension()
    {
        // Regression: when a foreign extension declares a nested type, the nested type's
        // members must not be attributed to the outer foreign type. Previously, the parser
        // would see `public var hashValue` inside the nested enum and emit a foreign-extension
        // property on `Foundation.PredicateExpressions` itself, producing an invalid Swift
        // wrapper `Unmanaged<Foundation.PredicateExpressions>.fromOpaque(...)` (caseless enum
        // is not a class type, so Unmanaged rejects it).
        var swiftInterface = """
            extension Foundation.PredicateExpressions {
              public static func build_donated<Input>(_ input: Input) -> Foundation.PredicateExpressions.Donated<Input>
              public enum DonationFilterOperator : Swift.Codable, Swift.Sendable {
                case equal
                case notEqual
                public var hashValue: Swift.Int {
                  get
                }
                public func hash(into hasher: inout Swift.Hasher)
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var protocolNames = new HashSet<string>();
            var moduleTypeNames = new HashSet<string>();
            var result = SwiftInterfaceAccessParser.GetForeignTypeExtensionMembers(
                path, protocolNames, moduleTypeNames, "TipKit");

            // Extension on Foundation.PredicateExpressions is detected as a foreign extension.
            Assert.True(result.ContainsKey("Foundation.PredicateExpressions"));
            var members = result["Foundation.PredicateExpressions"];

            // The direct static method on PredicateExpressions must still be collected.
            Assert.Contains(members, m => m.MethodName == "build_donated");

            // The nested enum's `hashValue` property and `hash` method belong to
            // DonationFilterOperator, NOT to PredicateExpressions — they must NOT appear
            // as members of the outer extension.
            Assert.DoesNotContain(members, m => m.MethodName == "hashValue");
            Assert.DoesNotContain(members, m => m.MethodName == "hash");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetForeignTypeExtensionMembers_RealTipKitInterface_DoesNotEmitPredicateExpressionsHashValue()
    {
        // Run directly against the real TipKit swiftinterface as a spot-check. Only executes
        // when the SDK file is present; skipped otherwise.
        var path = "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS26.2.sdk/System/Library/Frameworks/TipKit.framework/Modules/TipKit.swiftmodule/arm64e-apple-ios.swiftinterface";
        if (!File.Exists(path))
            return;

        var result = SwiftInterfaceAccessParser.GetForeignTypeExtensionMembers(
            path, new HashSet<string>(), new HashSet<string>(), "TipKit");

        if (result.TryGetValue("Foundation.PredicateExpressions", out var members))
        {
            // hashValue on Foundation.PredicateExpressions must not exist — it belongs to
            // the nested DonationFilterOperator enum, not the namespace-style outer enum.
            var buggyMember = members.FirstOrDefault(m => m.MethodName == "hashValue");
            Assert.Null(buggyMember);
        }
    }

    [Fact]
    public void GetForeignTypeExtensionMembers_WithAvailabilityAndDocAttributes_StillSkipsNestedMembers()
    {
        // Same regression as above but with the real-world attribute-heavy syntax Foundation
        // uses: @available on the nested type + @_documentation(visibility: private) on each
        // line. Needs to work even when multi-line attributes and the var/func contain
        // whitespace/leading attributes.
        var swiftInterface = """
            @available(macOS 14.0, iOS 17.0, macCatalyst 17.0, tvOS 17.0, visionOS 1.0, watchOS 10.0, *)
            extension Foundation.PredicateExpressions {
              @available(macOS 14.0, iOS 17.0, macCatalyst 17.0, tvOS 17.0, visionOS 1.0, watchOS 10.0, *)
              @_documentation(visibility: private) public enum DonationFilterOperator : Swift.Codable, Swift.Sendable {
                case equal
                case notEqual
                public static func == (a: Foundation.PredicateExpressions.DonationFilterOperator, b: Foundation.PredicateExpressions.DonationFilterOperator) -> Swift.Bool
                public func encode(to encoder: any Swift.Encoder) throws
                public func hash(into hasher: inout Swift.Hasher)
                public var hashValue: Swift.Int {
                  get
                }
                public init(from decoder: any Swift.Decoder) throws
              }
            }
            """;

        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetForeignTypeExtensionMembers(
                path, new HashSet<string>(), new HashSet<string>(), "TipKit");

            if (result.TryGetValue("Foundation.PredicateExpressions", out var members))
            {
                Assert.DoesNotContain(members, m => m.MethodName == "hashValue");
                Assert.DoesNotContain(members, m => m.MethodName == "hash");
                Assert.DoesNotContain(members, m => m.MethodName == "encode");
            }
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region GetCustomActorIsolatedTypes (SWIFTBIND022)

    [Fact]
    public void GetCustomActorIsolatedTypes_DetectsAnnotationOnSameLine()
    {
        // Reproduces the Nuke 13.0.2 pattern: @<Lib>Actor on the same line as the type decl.
        var swiftInterface = """
            @globalActor public actor ImagePipelineActor {
              public static let shared: Nuke.ImagePipelineActor
            }
            @Nuke.ImagePipelineActor public class ImagePrefetcher {
              public init(pipeline: Nuke.ImagePipeline = Nuke.ImagePipeline.shared)
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var customActors = new HashSet<string> { "ImagePipelineActor" };
            var result = SwiftInterfaceAccessParser.GetCustomActorIsolatedTypes(path, customActors);
            Assert.Contains("ImagePrefetcher", result);
            // The actor declaration itself must NOT be in the set — that's GetCustomActorTypes' job.
            Assert.DoesNotContain("ImagePipelineActor", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetCustomActorIsolatedTypes_DetectsAnnotationOnPriorLine()
    {
        var swiftInterface = """
            @globalActor public actor ImagePipelineActor {
              public static let shared: Nuke.ImagePipelineActor
            }
            @Nuke.ImagePipelineActor
            public class ImageCache {
              public init()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var customActors = new HashSet<string> { "ImagePipelineActor" };
            var result = SwiftInterfaceAccessParser.GetCustomActorIsolatedTypes(path, customActors);
            Assert.Contains("ImageCache", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetCustomActorIsolatedTypes_IgnoresUnannotatedTypes()
    {
        var swiftInterface = """
            @globalActor public actor ImagePipelineActor {
              public static let shared: Nuke.ImagePipelineActor
            }
            public class PlainClass {
              public init()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var customActors = new HashSet<string> { "ImagePipelineActor" };
            var result = SwiftInterfaceAccessParser.GetCustomActorIsolatedTypes(path, customActors);
            Assert.DoesNotContain("PlainClass", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetCustomActorIsolatedTypes_AnnotatedMemberDoesNotTaintNextType()
    {
        // Regression: an actor-annotated member (init/func/var) on a line shared with the
        // annotation must NOT cause the next type declaration to be flagged as actor-isolated.
        // Previously the deferred-annotation logic carried the flag forward across complete
        // member decls, making downstream types like AsyncWorker erroneously match SWIFTBIND022.
        var swiftInterface = """
            @globalActor public actor BindingsTestGlobalActor {
              public static let shared: BindingsTestGlobalActor
            }
            @BindingsTestGlobalActor public class GlobalActorIsolatedClass {
              @BindingsTestGlobalActor public init(label: Swift.String = "default")
              @BindingsTestGlobalActor public func describe() -> Swift.String
              @objc deinit
            }
            public func plainFreeFunction()
            public struct AsyncWorker {
              public init(name: Swift.String)
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var customActors = new HashSet<string> { "BindingsTestGlobalActor" };
            var result = SwiftInterfaceAccessParser.GetCustomActorIsolatedTypes(path, customActors);
            Assert.Contains("GlobalActorIsolatedClass", result);
            Assert.DoesNotContain("AsyncWorker", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetCustomActorIsolatedTypes_ReturnsEmptyWhenNoCustomActorsKnown()
    {
        var swiftInterface = """
            @ImagePipelineActor public class ImagePrefetcher {
              public init()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            // Bare unqualified annotation with no local actors: the qualified-imported-actor
            // heuristic requires at least one module-prefix segment, so this returns empty.
            // Matching the bare form here would overlap with property wrappers / macros.
            var result = SwiftInterfaceAccessParser.GetCustomActorIsolatedTypes(path, customActorTypeNames: null);
            Assert.Empty(result);

            var emptyResult = SwiftInterfaceAccessParser.GetCustomActorIsolatedTypes(path, new HashSet<string>());
            Assert.Empty(emptyResult);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetCustomActorIsolatedTypes_DetectsImportedQualifiedActorAnnotation()
    {
        // Imported global actors slip past the local-actor list (nothing declares
        // `ImagePipelineActor` in this swiftinterface) but the consumer still applies
        // `@Dependency.ImagePipelineActor`. The qualified name + Actor suffix is the
        // signal we lean on to fire SWIFTBIND022 in this case.
        var swiftInterface = """
            @Dependency.ImagePipelineActor public class ImagePrefetcher {
              public init()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetCustomActorIsolatedTypes(
                path, customActorTypeNames: null);
            Assert.Contains("ImagePrefetcher", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetCustomActorIsolatedTypes_DetectsImportedQualifiedActorOnPriorLine()
    {
        // Multi-line annotation form for an imported actor: the annotation is on its
        // own line and the type declaration follows. The deferred-annotation path must
        // pick up the imported-actor regex match too.
        var swiftInterface = """
            @Dependency.ImagePipelineActor
            public class ImageCache {
              public init()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetCustomActorIsolatedTypes(
                path, customActorTypeNames: new HashSet<string>());
            Assert.Contains("ImageCache", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetCustomActorIsolatedTypes_DetectsNestedQualifiedActor()
    {
        // Imported actors can be nested under a parent type, producing qualifier paths
        // with more than one dot (e.g., `@Foo.Inner.SomeActor`). The regex must match
        // any number of qualifier segments.
        var swiftInterface = """
            @Foo.Inner.SomeActor public class IsolatedClass {
              public init()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetCustomActorIsolatedTypes(
                path, customActorTypeNames: null);
            Assert.Contains("IsolatedClass", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetCustomActorIsolatedTypes_IgnoresQualifiedMainActor()
    {
        // `@_Concurrency.MainActor` matches the qualified-name shape but is the
        // built-in @MainActor — which has its own IsMainActorIsolated path. Custom-
        // actor isolation would double-classify the type and mis-route SWIFTBIND022;
        // the negative lookahead in the regex must exclude it.
        var swiftInterface = """
            @_Concurrency.MainActor public class MainActorClass {
              public init()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetCustomActorIsolatedTypes(
                path, customActorTypeNames: null);
            Assert.DoesNotContain("MainActorClass", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetCustomActorIsolatedTypes_QualifiedNonActorAttribute_NotMatched()
    {
        // Property wrappers / generic attributes that happen to use a qualified shape
        // but do NOT end in `Actor` must not be flagged. The Actor-suffix gate keeps
        // false positives off arbitrary `@Module.Wrapper` annotations.
        var swiftInterface = """
            @Foo.Bar public class WrappedThing {
              public init()
            }
            @Module.SomeWrapper public class AnotherThing {
              public init()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetCustomActorIsolatedTypes(
                path, customActorTypeNames: null);
            Assert.DoesNotContain("WrappedThing", result);
            Assert.DoesNotContain("AnotherThing", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetCustomActorIsolatorMap_RecordsLocalActorShortName()
    {
        // Local-actor annotation must record the matched actor's leaf identifier so the
        // emitter can build `<Actor>.shared.assumeIsolated { … }` without re-scanning.
        var swiftInterface = """
            @globalActor public actor ImagePipelineActor {
              public static let shared: Nuke.ImagePipelineActor
            }
            @Nuke.ImagePipelineActor public class ImagePrefetcher {
              public init()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var customActors = new HashSet<string> { "ImagePipelineActor" };
            var map = SwiftInterfaceAccessParser.GetCustomActorIsolatorMap(path, customActors);
            Assert.True(map.TryGetValue("ImagePrefetcher", out var actorName));
            Assert.Equal("ImagePipelineActor", actorName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetCustomActorIsolatorMap_RecordsImportedQualifiedActorShortName()
    {
        // The qualified-imported regex captures the trailing `*Actor` segment as the
        // short name — exactly what the emitter needs to resolve the actor TypeDecl.
        var swiftInterface = """
            @Dependency.ImagePipelineActor public class ImagePrefetcher {
              public init()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var map = SwiftInterfaceAccessParser.GetCustomActorIsolatorMap(
                path, customActorTypeNames: null);
            Assert.True(map.TryGetValue("ImagePrefetcher", out var actorName));
            Assert.Equal("ImagePipelineActor", actorName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetCustomActorIsolatorMap_DeferredAnnotationCarriesActorName()
    {
        // Annotation on its own line: the deferred-flag path must also carry the matched
        // actor's short name forward — otherwise the lookup map ends up missing the value
        // and the emitter would silently fall back to SWIFTBIND022.
        var swiftInterface = """
            @globalActor public actor ImagePipelineActor {
              public static let shared: Nuke.ImagePipelineActor
            }
            @Nuke.ImagePipelineActor
            public class ImageCache {
              public init()
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var customActors = new HashSet<string> { "ImagePipelineActor" };
            var map = SwiftInterfaceAccessParser.GetCustomActorIsolatorMap(path, customActors);
            Assert.True(map.TryGetValue("ImageCache", out var actorName));
            Assert.Equal("ImagePipelineActor", actorName);
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region Family-F overload disambiguation + protocol requirement availability

    /// <summary>
    /// Family-F-1 (Nuke <c>data(for:)</c>): one overload deprecated, the recommended
    /// overload has no <c>@available</c>. The parser must scope the deprecation to
    /// the deprecated overload only — the bare <c>"X.data(for:)"</c> key must NOT
    /// resolve to the deprecation, otherwise <c>SwiftABIParser.ApplyMemberAvailability</c>
    /// broadcasts <c>[Obsolete]</c> across both C# overloads.
    /// </summary>
    [Fact]
    public void GetAvailabilityAnnotations_F1_DeprecatedOverloadDoesNotBroadcast()
    {
        var swiftInterface = """
            public class ImagePipeline {
              public func data(for request: ImageRequest) async throws -> Foundation.Data
              @available(*, deprecated, message: "Use the variant that accepts ImageRequest")
              public func data(for url: Foundation.URL) async throws -> Foundation.Data
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            // The bare key is poisoned (ambiguous overloads) and must NOT carry the
            // deprecation — otherwise the recommended overload's ABI lookup hits it
            // and gets a spurious [Obsolete].
            Assert.False(result.ContainsKey("ImagePipeline.data(for:)"));
            // The deprecated overload is keyed under its disamb suffix.
            Assert.True(result.ContainsKey("ImagePipeline.data(for:)|URL"));
            Assert.Contains(result["ImagePipeline.data(for:)|URL"],
                a => a.IsUnconditionallyDeprecated &&
                     a.Message == "Use the variant that accepts ImageRequest");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Family-F-4 (StoreKit2 <c>Product.purchase(confirmIn:options:)</c>): two
    /// overloads with DIFFERENT <c>@available</c> versions. Without disambiguation,
    /// <c>AddRange</c> concatenates them and both C# overloads emit a merged
    /// version set.
    /// </summary>
    [Fact]
    public void GetAvailabilityAnnotations_F4_OverloadsKeepDistinctVersions()
    {
        var swiftInterface = """
            public struct Product {
              @available(iOS 17.0, *)
              public func purchase(confirmIn scene: some UIScene, options: Set<Int>) async throws -> Int
              @available(iOS 18.2, *)
              public func purchase(confirmIn viewController: UIKit.UIViewController, options: Set<Int>) async throws -> Int
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            // Bare key is poisoned: both overloads carry @available, so concatenation
            // would surface both versions on each. Disamb keys keep them apart.
            Assert.False(result.ContainsKey("Product.purchase(confirmIn:options:)"));

            var sceneKey = "Product.purchase(confirmIn:options:)|UIScene,Set<Int>";
            var vcKey = "Product.purchase(confirmIn:options:)|UIViewController,Set<Int>";
            Assert.True(result.ContainsKey(sceneKey), $"missing: {sceneKey}");
            Assert.True(result.ContainsKey(vcKey), $"missing: {vcKey}");

            Assert.Single(result[sceneKey]);
            Assert.Single(result[vcKey]);
            Assert.Equal("17.0", result[sceneKey][0].IntroducedVersion);
            Assert.Equal("18.2", result[vcKey][0].IntroducedVersion);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Single-overload baseline: bare key is preserved (no disamb suffix) so any
    /// downstream consumer that can't compute a sig still hits.
    /// </summary>
    [Fact]
    public void GetAvailabilityAnnotations_SingleOverload_KeepsBareKey()
    {
        var swiftInterface = """
            public class Pipeline {
              @available(iOS 16.0, *)
              public func fetch(url: Foundation.URL) async throws -> Foundation.Data
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("Pipeline.fetch(url:)"));
            Assert.False(result.ContainsKey("Pipeline.fetch(url:)|URL"));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Family-F-2 (StripeApplePay <c>@objc optional func</c>): protocol requirements
    /// have NO access modifier in the swiftinterface. Without the bare-regex
    /// fallback inside protocol scope, the <c>@available</c> floor is silently
    /// dropped and the C# binding crashes on iOS &lt; floor.
    /// </summary>
    [Fact]
    public void GetAvailabilityAnnotations_F2_ProtocolRequirementWithoutAccessModifier()
    {
        var swiftInterface = """
            @objc public protocol PaymentDelegate {
              @available(iOS 15.0, *)
              @objc optional func paymentSheetWillPresent()

              @available(iOS 16.0, *)
              var supportedNetworks: [Int] { get }
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("PaymentDelegate.paymentSheetWillPresent()"),
                "protocol-method @available must be harvested even without `public` modifier");
            Assert.Contains(result["PaymentDelegate.paymentSheetWillPresent()"],
                a => a.Platform == "iOS" && a.IntroducedVersion == "15.0");

            Assert.True(result.ContainsKey("PaymentDelegate.supportedNetworks"));
            Assert.Contains(result["PaymentDelegate.supportedNetworks"],
                a => a.Platform == "iOS" && a.IntroducedVersion == "16.0");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Inline-attribute regression: when an <c>@available(...)</c> sits on the same
    /// line as the member decl (and another overload exists), the parameter-clause
    /// finder must skip the attribute's own argument list before locating the real
    /// parameter clause. Pre-fix, <c>ExtractParamTypesForSignature</c> would grab
    /// the FIRST <c>(</c> in the line — the attribute's <c>(iOS 17.0, *)</c> — and
    /// produce a bogus disamb signature. The bare key would then store the wrong
    /// annotation set, and the ABI-side lookup (using a real param-typed key like
    /// <c>|Int</c>) would miss, dropping availability for the inline-attributed
    /// overload entirely.
    /// </summary>
    [Fact]
    public void GetAvailabilityAnnotations_InlineAvailable_OverloadParamTypesParsedCorrectly()
    {
        var swiftInterface = """
            public struct S {
              @available(iOS 17.0, *) public func f(_ x: Swift.Int) -> Swift.Int
              public func f(_ x: Swift.String) -> Swift.Int
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            // Bare key must be poisoned (two overloads share the printedName).
            Assert.False(result.ContainsKey("S.f(_:)"));
            // The Int overload carries iOS 17.0 — keyed by its real param type, not
            // the attribute's argument list.
            var intKey = "S.f(_:)|Int";
            Assert.True(result.ContainsKey(intKey),
                $"missing '{intKey}'; parser likely captured the @available argument list as params");
            Assert.Contains(result[intKey],
                a => a.Platform == "iOS" && a.IntroducedVersion == "17.0");
            // No annotations expected for the String overload.
            Assert.False(result.ContainsKey("S.f(_:)|String"));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Direct unit test for the attribute-skipping helper. Mirrors the input shapes
    /// the regex parser sees in practice (single attribute, multiple attributes,
    /// attribute without args, mixed with modifiers).
    /// </summary>
    [Theory]
    [InlineData("@available(iOS 17.0, *) public func f(_ x: Int)", "Int")]
    [InlineData("@available(*, deprecated) @objc(something) public func f(_ x: Int)", "Int")]
    [InlineData("@objc public func f(_ x: Int)", "Int")]
    [InlineData("public func f(_ x: Int)", "Int")]
    [InlineData("@available(iOS 17.0, *)\n  public func f(_ x: Set<Int>)", "Set<Int>")]
    // Dot-qualified attribute names (e.g. `@_Concurrency.MainActor`,
    // `@Module.Actor`) must be consumed as a single attribute. If the helper
    // stopped at the dot, the next `@available(...)`'s argument list would be
    // misidentified as the parameter clause.
    [InlineData("@_Concurrency.MainActor @available(iOS 17.0, *) public func f(_ x: Int)", "Int")]
    [InlineData("@_Concurrency.MainActor public func f(_ x: Set<Int>)", "Set<Int>")]
    public void ExtractParamTypesForSignature_SkipsLeadingAttributes(string memberText, string expectedJoined)
    {
        var types = SwiftInterfaceAccessParser.ExtractParamTypesForSignature(memberText);
        Assert.Equal(expectedJoined, MemberSignatureNormalizer.BuildSignature(types));
    }

    /// <summary>
    /// Family-F-5 (visionOS): the <c>@available(visionOS 1.0, *)</c> shorthand must
    /// produce a normalized "visionOS" platform annotation.
    /// </summary>
    [Fact]
    public void GetAvailabilityAnnotations_F5_VisionOSShorthand()
    {
        var swiftInterface = """
            public struct Spatial {
              @available(visionOS 1.0, *)
              public func play() async
            }
            """;
        var path = WriteTempFile(swiftInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(path);
            Assert.True(result.ContainsKey("Spatial.play()"));
            Assert.Contains(result["Spatial.play()"],
                a => a.Platform == "visionOS" && a.IntroducedVersion == "1.0");
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region GetSpiOnlyConformances Tests

    [Fact]
    public void GetSpiOnlyConformances_PlainConformance_CapturesQualifiedTypeAndProtocol()
    {
        var privateInterface = """
            @_spi(Internal) extension StripeCore.StripeAPI.BankAccountToken : Swift.Equatable {
              public static func == (lhs: StripeCore.StripeAPI.BankAccountToken, rhs: StripeCore.StripeAPI.BankAccountToken) -> Swift.Bool
            }
            """;
        var path = WriteTempFile(privateInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSpiOnlyConformances(path);
            Assert.Contains("StripeCore.StripeAPI.BankAccountToken::Equatable", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSpiOnlyConformances_RetroactiveAttributedConformance_StripsAttrAndCapturesProtocol()
    {
        // Compiler sometimes requires `@retroactive` on conformances declared in a
        // different module than the protocol. Without the regex/loop tolerance the
        // whole extension is silently dropped from the filter set.
        var privateInterface = """
            @_spi(Internal) extension StripeCore.StripeAPI.BankAccountToken : @retroactive Swift.Equatable, @retroactive Swift.Hashable {
              public static func == (lhs: StripeCore.StripeAPI.BankAccountToken, rhs: StripeCore.StripeAPI.BankAccountToken) -> Swift.Bool
            }
            """;
        var path = WriteTempFile(privateInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSpiOnlyConformances(path);
            Assert.Contains("StripeCore.StripeAPI.BankAccountToken::Equatable", result);
            Assert.Contains("StripeCore.StripeAPI.BankAccountToken::Hashable", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSpiOnlyConformances_CodableTypealias_ExpandsToEncodableAndDecodable()
    {
        // `Codable` is a typealias for `Encodable & Decodable`; ABI JSON records the
        // expanded pair, so the filter must emit both keys to match.
        var privateInterface = """
            @_spi(Internal) extension StripeCore.StripeAPI.BankAccountToken : Swift.Codable {
              public func encode(to encoder: any Swift.Encoder) throws
            }
            """;
        var path = WriteTempFile(privateInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSpiOnlyConformances(path);
            Assert.Contains("StripeCore.StripeAPI.BankAccountToken::Encodable", result);
            Assert.Contains("StripeCore.StripeAPI.BankAccountToken::Decodable", result);
            Assert.DoesNotContain("StripeCore.StripeAPI.BankAccountToken::Codable", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSpiOnlyConformances_MissingPath_ReturnsEmpty()
    {
        var result = SwiftInterfaceAccessParser.GetSpiOnlyConformances("/nonexistent/path.private.swiftinterface");
        Assert.Empty(result);
    }

    [Fact]
    public void GetSpiOnlyConformances_ParenthesizedAttribute_StripsAndCapturesProtocol()
    {
        // Attributes with arguments (parentheses) must not cause the parser to drop the
        // entire extension. The previous regex stopped at the first '(', silently losing
        // the conformance and emitting unbuildable wrapper references.
        var privateInterface = """
            @_spi(Internal) extension StripeCore.StripeAPI.BankAccountToken : @available(*, deprecated) Swift.Equatable, @objc(SBKHashable) Swift.Hashable {
              public static func == (lhs: StripeCore.StripeAPI.BankAccountToken, rhs: StripeCore.StripeAPI.BankAccountToken) -> Swift.Bool
            }
            """;
        var path = WriteTempFile(privateInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSpiOnlyConformances(path);
            Assert.Contains("StripeCore.StripeAPI.BankAccountToken::Equatable", result);
            Assert.Contains("StripeCore.StripeAPI.BankAccountToken::Hashable", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetSpiOnlyConformances_GenericConformanceAndWhereClause_CapturesProtocol()
    {
        // The depth-aware splitter must not break on commas inside generic arguments and
        // must trim a trailing 'where ...' generic-requirements clause off the conformance
        // list. Otherwise the second protocol gets lost in the split.
        var privateInterface = """
            @_spi(Internal) extension StripeCore.Container : Swift.Collection, Foundation.NSCopying where Element : Swift.Hashable {
              public func copy(with zone: Foundation.NSZone? = nil) -> Any
            }
            """;
        var path = WriteTempFile(privateInterface);
        try
        {
            var result = SwiftInterfaceAccessParser.GetSpiOnlyConformances(path);
            Assert.Contains("StripeCore.Container::Collection", result);
            Assert.Contains("StripeCore.Container::NSCopying", result);
        }
        finally { File.Delete(path); }
    }

    #endregion
}
