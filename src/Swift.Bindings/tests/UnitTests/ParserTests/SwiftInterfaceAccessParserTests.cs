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
            Assert.True(result.ContainsKey("MyCollection.subscript(index:)"));
            var annotations = result["MyCollection.subscript(index:)"];
            Assert.Contains(annotations, a => a.Platform == "iOS" && a.IntroducedVersion == "14.0");
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
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(path, customActors);

            // Should detect 3 custom-actor-isolated methods
            Assert.Contains("BlinkIDSession.process(inputImage:)", result);
            Assert.Contains("BlinkIDSession.reset()", result);
            Assert.Contains("BlinkIDSession.getResult()", result);

            // Non-isolated method should NOT be included
            Assert.DoesNotContain("BlinkIDSession.resumeActiveProcessing()", result);
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
            var resultWithCustom = SwiftInterfaceAccessParser.GetActorIsolatedMembers(path, customActors);
            Assert.Contains("SomeClass.mainActorMethod()", resultWithCustom);
            Assert.Contains("SomeClass.actorMethod()", resultWithCustom);
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
            var result = SwiftInterfaceAccessParser.GetActorIsolatedMembers(path, customActors);
            Assert.Contains("BlinkIDSdk.terminateBlinkIDSdk()", result);
            Assert.DoesNotContain("BlinkIDSdk.refreshLicenseLease()", result);
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
