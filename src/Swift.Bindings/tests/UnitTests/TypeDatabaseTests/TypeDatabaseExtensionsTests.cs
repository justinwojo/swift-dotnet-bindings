// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

public class TypeDatabaseExtensionsTests
{
    private static readonly string s_dbDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift");

    private static async Task<TypeDatabase> CreateDbWithXmlAsync(params string[] xmlFileNames)
    {
        var typeDatabase = new TypeDatabase();
        foreach (var fileName in xmlFileNames)
            await typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, fileName));
        return typeDatabase;
    }
    [Fact]
    public void TryGetAnyTypeFallbackInfo_MissingType_ReturnsFallbackInfo()
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("KnownModule", "/tmp/KnownModule.dylib");
        typeDatabase.AddModuleDatabase(module);

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec("UnknownModule.MissingType"), out var fallbackInfo);

        Assert.True(found);
        Assert.NotNull(fallbackInfo);
        Assert.Equal("Type is missing from the type database", fallbackInfo.Value.Reason);
        Assert.Equal("UnknownModule.MissingType", fallbackInfo.Value.SwiftType);
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_GenericParameter_ReturnsFalse()
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec("T"), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_ExistentialNamedType_ReturnsFallbackInfo()
    {
        var typeDatabase = new TypeDatabase();
        var existential = new NamedTypeSpec("Swift.Encoder")
        {
            IsAny = true
        };

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(existential, out var fallbackInfo);

        Assert.True(found);
        Assert.NotNull(fallbackInfo);
        Assert.Equal("Existential type fallback", fallbackInfo.Value.Reason);
        Assert.Equal("any Swift.Encoder", fallbackInfo.Value.SwiftType);
    }

    // --- DynamicSelf hardening tests (A1) ---
    // These tests verify that DynamicSelf ("Self") is handled by explicit IsDynamicSelf guards,
    // NOT by the IsExistentialTypeName fallback. The key behavioral difference:
    // TryGetAnyTypeFallbackInfo returns false for DynamicSelf (intentionally handled),
    // but would return true with reason "Existential type fallback" if the heuristic path were used.

    [Fact]
    public void GetTypeRecordOrThrow_DynamicSelf_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();
        var selfSpec = new NamedTypeSpec("Self");

        var record = typeDatabase.GetTypeRecordOrThrow(selfSpec);

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void GetTypeRecordOrAnyType_DynamicSelf_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();
        var selfSpec = new NamedTypeSpec("Self");

        var record = typeDatabase.GetTypeRecordOrAnyType(selfSpec);

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void TryGetTypeRecord_DynamicSelf_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();
        var selfSpec = new NamedTypeSpec("Self");

        var found = typeDatabase.TryGetTypeRecord(selfSpec, out var record);

        Assert.True(found);
        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void IsTypeProcessed_DynamicSelf_ReturnsTrue()
    {
        var typeDatabase = new TypeDatabase();
        var selfSpec = new NamedTypeSpec("Self");

        Assert.True(typeDatabase.IsTypeProcessed(selfSpec));
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_DynamicSelf_IsNotFallback()
    {
        // This is the key hardening test: DynamicSelf must NOT be reported as an
        // existential fallback. Without the explicit IsDynamicSelf guard, "Self"
        // would match IsExistentialTypeName (no module qualifier) and return
        // fallbackInfo with reason "Existential type fallback".
        // The guard ensures DynamicSelf is a known, intentionally-handled case.
        var typeDatabase = new TypeDatabase();
        var selfSpec = new NamedTypeSpec("Self");

        var isFallback = typeDatabase.TryGetAnyTypeFallbackInfo(selfSpec, out var fallbackInfo);

        Assert.False(isFallback);
        Assert.Null(fallbackInfo);
    }

    // --- Pointer type tests ---

    [Theory]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("Swift.UnsafePointer")]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Builtin.RawPointer")]
    public void GetTypeRecordOrAnyType_PointerType_ReturnsIntPtrType(string pointerTypeName)
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(pointerTypeName));

        Assert.Equal(TypeDatabaseExtensions.IntPtrType, record);
        Assert.Equal("System.IntPtr", record.CSharpTypeName.FullyQualifiedName);
        Assert.Equal(TypeRecordFlags.Frozen, record.Flags);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
    }

    [Theory]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("Swift.UnsafePointer")]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Builtin.RawPointer")]
    public void GetTypeRecordOrThrow_PointerType_ReturnsIntPtrType(string pointerTypeName)
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec(pointerTypeName));

        Assert.Equal(TypeDatabaseExtensions.IntPtrType, record);
    }

    [Theory]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("Swift.UnsafePointer")]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Builtin.RawPointer")]
    public void IsTypeProcessed_PointerType_ReturnsTrue(string pointerTypeName)
    {
        var typeDatabase = new TypeDatabase();

        var result = typeDatabase.IsTypeProcessed(new NamedTypeSpec(pointerTypeName));

        Assert.True(result);
    }

    [Theory]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("Swift.UnsafePointer")]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Builtin.RawPointer")]
    public void TryGetAnyTypeFallbackInfo_PointerType_ReturnsFalse(string pointerTypeName)
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec(pointerTypeName), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    [Theory]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("Swift.UnsafePointer")]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Builtin.RawPointer")]
    public void TryGetTypeRecord_PointerType_ReturnsIntPtrType(string pointerTypeName)
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(pointerTypeName), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal(TypeDatabaseExtensions.IntPtrType, record);
    }

    // --- ObjC module type tests (ObjectiveC module only) ---

    [Fact]
    public void GetTypeRecordOrAnyType_ObjCNSObject_ReturnsObjCBridgedRecord()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("ObjectiveC.NSObject"));

        Assert.Equal("Foundation.NSObject", record.CSharpTypeName.FullyQualifiedName);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
        Assert.True((record.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
    }

    [Fact]
    public void GetTypeRecordOrThrow_ObjCNSObject_ReturnsObjCBridgedRecord()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec("ObjectiveC.NSObject"));

        Assert.Equal("Foundation.NSObject", record.CSharpTypeName.FullyQualifiedName);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_ObjCType_ReturnsFalse()
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec("ObjectiveC.NSObject"), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    // --- Foundation.NSObject (remapped from ObjectiveC.NSObject by TypeSpecParser) ---

    [Fact]
    public void GetTypeRecordOrAnyType_FoundationNSObject_ReturnsObjCBridgedRecord()
    {
        var typeDatabase = new TypeDatabase();

        // TypeSpecParser remaps ObjectiveC.NSObject → Foundation.NSObject
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation.NSObject"));

        Assert.Equal("Foundation.NSObject", record.CSharpTypeName.FullyQualifiedName);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
        Assert.True((record.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
    }

    [Fact]
    public void GetTypeRecordOrThrow_FoundationNSObject_ReturnsObjCBridgedRecord()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec("Foundation.NSObject"));

        Assert.Equal("Foundation.NSObject", record.CSharpTypeName.FullyQualifiedName);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_FoundationNSObject_ReturnsFalse()
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec("Foundation.NSObject"), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    [Fact]
    public void IsTypeProcessed_FoundationNSObject_ReturnsTrue()
    {
        var typeDatabase = new TypeDatabase();

        var result = typeDatabase.IsTypeProcessed(new NamedTypeSpec("Foundation.NSObject"));

        Assert.True(result);
    }

    [Fact]
    public void TryGetTypeRecord_FoundationNSObject_ReturnsObjCBridgedRecord()
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec("Foundation.NSObject"), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
    }

    // --- Non-class ObjectiveC module types are NOT auto-bridged ---

    [Fact]
    public void GetTypeRecordOrAnyType_ObjCSelector_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();

        // ObjectiveC.Selector is a struct, not an NSObject subclass.
        // TypeSpecParser remaps it to Foundation.Selector.
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation.Selector"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void GetTypeRecordOrAnyType_ObjCSelectorDirect_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();

        // Direct ObjectiveC.Selector (bypassing TypeSpecParser) should also not be bridged
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("ObjectiveC.Selector"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void GetTypeRecordOrThrow_ObjCSelector_Throws()
    {
        var typeDatabase = new TypeDatabase();

        // Selector is not a root class, so it should throw (not return synthetic ObjCBridged)
        Assert.Throws<Exception>(() =>
            typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec("ObjectiveC.Selector")));
    }

    // --- ObjectiveC module types resolved via FoundationDatabase ---

    [Fact]
    public async Task TryGetTypeRecord_SelectorFromFoundationDb_ReturnsNint()
    {
        // TypeSpecParser rewrites ObjectiveC.Selector → Foundation.Selector.
        // FoundationDatabase.xml contains Selector mapped to nint.
        var db = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var found = db.TryGetTypeRecord(
            SwiftTypeName.FromModuleQualifiedName("Foundation.Selector"), out var record);

        Assert.True(found, "Foundation.Selector should be found after loading FoundationDatabase");
        Assert.Equal("nint", record!.CSharpTypeName.ToString());
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
        Assert.False(record.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement));
    }

    [Fact]
    public async Task TryGetTypeRecord_ObjCBoolFromFoundationDb_ReturnsNint()
    {
        var db = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var found = db.TryGetTypeRecord(
            SwiftTypeName.FromModuleQualifiedName("Foundation.ObjCBool"), out var record);

        Assert.True(found, "Foundation.ObjCBool should be found after loading FoundationDatabase");
        Assert.Equal("nint", record!.CSharpTypeName.ToString());
    }

    // --- Apple framework ObjC types are auto-bridged (Phase I1b) ---

    [Theory]
    [InlineData("UIKit.UIImage", "UIKit", "UIImage")]
    [InlineData("UIKit.UIViewController", "UIKit", "UIViewController")]
    [InlineData("UIKit.UIView", "UIKit", "UIView")]
    [InlineData("AppKit.NSImage", "AppKit", "NSImage")]
    [InlineData("AppKit.NSViewController", "AppKit", "NSViewController")]
    [InlineData("CoreImage.CIImage", "CoreImage", "CIImage")]
    [InlineData("AVFoundation.AVPlayer", "AVFoundation", "AVPlayer")]
    [InlineData("WebKit.WKWebView", "WebKit", "WKWebView")]
    [InlineData("PassKit.PKPayment", "PassKit", "PKPayment")]
    [InlineData("PassKit.PKShippingMethod", "PassKit", "PKShippingMethod")]
    [InlineData("PassKit.PKPaymentRequestShippingMethodUpdate", "PassKit", "PKPaymentRequestShippingMethodUpdate")]
    [InlineData("PassKit.PKPaymentRequestCouponCodeUpdate", "PassKit", "PKPaymentRequestCouponCodeUpdate")]
    public void GetTypeRecordOrAnyType_AppleFrameworkType_ReturnsObjCBridgedRecord(string swiftType, string expectedNamespace, string expectedName)
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.Equal($"{expectedNamespace}.{expectedName}", record.CSharpTypeName.FullyQualifiedName);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
        Assert.True((record.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
    }

    [Theory]
    [InlineData("UIKit.UIImage")]
    [InlineData("AppKit.NSImage")]
    [InlineData("CoreImage.CIImage")]
    public void GetTypeRecordOrThrow_AppleFrameworkType_ReturnsObjCBridgedRecord(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec(swiftType));

        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
    }

    [Theory]
    [InlineData("UIKit.UIImage")]
    [InlineData("AppKit.NSImage")]
    [InlineData("CoreImage.CIImage")]
    [InlineData("PassKit.PKPaymentAuthorizationResult")]
    public void TryGetTypeRecord_AppleFrameworkType_ReturnsObjCBridgedRecord(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(swiftType), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
    }

    [Theory]
    [InlineData("UIKit.UIView")]
    [InlineData("AppKit.NSImage")]
    [InlineData("AVFoundation.AVPlayer")]
    public void IsTypeProcessed_AppleFrameworkType_ReturnsTrue(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        var result = typeDatabase.IsTypeProcessed(new NamedTypeSpec(swiftType));

        Assert.True(result);
    }

    [Theory]
    [InlineData("UIKit.UIImage")]
    [InlineData("AppKit.NSImage")]
    [InlineData("CoreImage.CIImage")]
    public void TryGetAnyTypeFallbackInfo_AppleFrameworkType_ReturnsFalse(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        // Apple framework types are handled via synthetic records, not a fallback
        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec(swiftType), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    // --- Foundation class types are auto-bridged as ObjC ---

    [Theory]
    // Foundation: Swift drops NS prefix — common classes
    [InlineData("Foundation.Bundle", "Foundation", "NSBundle")]
    [InlineData("Foundation.NotificationCenter", "Foundation", "NSNotificationCenter")]
    [InlineData("Foundation.UserDefaults", "Foundation", "NSUserDefaults")]
    [InlineData("Foundation.Timer", "Foundation", "NSTimer")]
    [InlineData("Foundation.RunLoop", "Foundation", "NSRunLoop")]
    [InlineData("Foundation.Operation", "Foundation", "NSOperation")]
    [InlineData("Foundation.OperationQueue", "Foundation", "NSOperationQueue")]
    [InlineData("Foundation.BlockOperation", "Foundation", "NSBlockOperation")]
    [InlineData("Foundation.ProcessInfo", "Foundation", "NSProcessInfo")]
    [InlineData("Foundation.Thread", "Foundation", "NSThread")]
    [InlineData("Foundation.FileManager", "Foundation", "NSFileManager")]
    [InlineData("Foundation.FileHandle", "Foundation", "NSFileHandle")]
    [InlineData("Foundation.UndoManager", "Foundation", "NSUndoManager")]
    [InlineData("Foundation.Progress", "Foundation", "NSProgress")]
    [InlineData("Foundation.Scanner", "Foundation", "NSScanner")]
    [InlineData("Foundation.NumberFormatter", "Foundation", "NSNumberFormatter")]
    [InlineData("Foundation.DateFormatter", "Foundation", "NSDateFormatter")]
    [InlineData("Foundation.InputStream", "Foundation", "NSInputStream")]
    [InlineData("Foundation.OutputStream", "Foundation", "NSOutputStream")]
    [InlineData("Foundation.Stream", "Foundation", "NSStream")]
    [InlineData("Foundation.NSData", "Foundation", "NSData")]
    // Foundation: URL/HTTP/JSON acronym casing
    [InlineData("Foundation.URLSession", "Foundation", "NSUrlSession")]
    [InlineData("Foundation.URLSessionTask", "Foundation", "NSUrlSessionTask")]
    [InlineData("Foundation.URLSessionDataTask", "Foundation", "NSUrlSessionDataTask")]
    [InlineData("Foundation.URLSessionDownloadTask", "Foundation", "NSUrlSessionDownloadTask")]
    [InlineData("Foundation.URLSessionUploadTask", "Foundation", "NSUrlSessionUploadTask")]
    [InlineData("Foundation.URLSessionStreamTask", "Foundation", "NSUrlSessionStreamTask")]
    [InlineData("Foundation.URLSessionWebSocketTask", "Foundation", "NSUrlSessionWebSocketTask")]
    [InlineData("Foundation.URLSessionConfiguration", "Foundation", "NSUrlSessionConfiguration")]
    [InlineData("Foundation.URLSessionTaskMetrics", "Foundation", "NSUrlSessionTaskMetrics")]
    [InlineData("Foundation.URLSessionTaskTransactionMetrics", "Foundation", "NSUrlSessionTaskTransactionMetrics")]
    [InlineData("Foundation.URLResponse", "Foundation", "NSUrlResponse")]
    [InlineData("Foundation.HTTPURLResponse", "Foundation", "NSHttpUrlResponse")]
    [InlineData("Foundation.CachedURLResponse", "Foundation", "NSCachedUrlResponse")]
    [InlineData("Foundation.URLAuthenticationChallenge", "Foundation", "NSUrlAuthenticationChallenge")]
    [InlineData("Foundation.URLCredential", "Foundation", "NSUrlCredential")]
    [InlineData("Foundation.URLCredentialStorage", "Foundation", "NSUrlCredentialStorage")]
    [InlineData("Foundation.URLProtectionSpace", "Foundation", "NSUrlProtectionSpace")]
    [InlineData("Foundation.URLCache", "Foundation", "NSUrlCache")]
    [InlineData("Foundation.URLProtocol", "Foundation", "NSUrlProtocol")]
    [InlineData("Foundation.URLConnection", "Foundation", "NSUrlConnection")]
    [InlineData("Foundation.URLSessionWebSocketTask.Message", "Foundation", "NSUrlSessionWebSocketMessage")]
    [InlineData("Foundation.HTTPCookie", "Foundation", "NSHttpCookie")]
    [InlineData("Foundation.HTTPCookieStorage", "Foundation", "NSHttpCookieStorage")]
    [InlineData("Foundation.JSONSerialization", "Foundation", "NSJsonSerialization")]
    // Foundation: ObjC names with casing differences
    [InlineData("Foundation.NSURL", "Foundation", "NSUrl")]
    [InlineData("Foundation.NSUUID", "Foundation", "NSUuid")]
    // AVFoundation: acronym casing
    [InlineData("AVFoundation.AVURLAsset", "AVFoundation", "AVUrlAsset")]
    [InlineData("AVFoundation.AVMIDIPlayer", "AVFoundation", "AVMidiPlayer")]
    // QuartzCore: NSString typedefs (namespace remapped to CoreAnimation)
    [InlineData("QuartzCore.CALayerContentsGravity", "Foundation", "NSString")]
    [InlineData("QuartzCore.CAMediaTimingFunctionName", "Foundation", "NSString")]
    [InlineData("QuartzCore.CATransitionType", "Foundation", "NSString")]
    [InlineData("QuartzCore.CATransitionSubtype", "Foundation", "NSString")]
    public void GetTypeRecordOrAnyType_FoundationClass_ReturnsObjCBridgedRecord(string swiftType, string expectedNamespace, string expectedName)
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.Equal($"{expectedNamespace}.{expectedName}", record.CSharpTypeName.FullyQualifiedName);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
        Assert.True((record.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
    }

    // --- Foundation value types are NOT auto-bridged ---

    [Theory]
    [InlineData("Foundation.Data")]
    [InlineData("Foundation.URL")]
    [InlineData("Foundation.UUID")]
    [InlineData("Foundation.URLError")]
    [InlineData("Foundation.URLError.Code")]
    [InlineData("Foundation.URLComponents")]
    [InlineData("Foundation.URLQueryItem")]
    [InlineData("Foundation.URLRequest")]
    [InlineData("Foundation.DateInterval")]
    [InlineData("Foundation.Calendar")]
    [InlineData("Foundation.Locale")]
    [InlineData("Foundation.TimeZone")]
    [InlineData("Foundation.Notification")]
    [InlineData("Foundation.Notification.Name")]
    [InlineData("Foundation.Measurement")]
    [InlineData("Foundation.CharacterSet")]
    [InlineData("Foundation.Decimal")]
    [InlineData("Foundation.NSRange")]
    [InlineData("Foundation.Date")]
    [InlineData("Foundation.IndexSet")]
    [InlineData("Foundation.ComparisonResult")]
    [InlineData("Foundation.IndexPath")]
    [InlineData("Foundation.JSONEncoder")]
    [InlineData("Foundation.JSONDecoder")]
    [InlineData("Foundation.NSNotification.Name")]
    [InlineData("Foundation.objc_AssociationPolicy")]
    [InlineData("Foundation.Date.ComponentsFormatStyle")]
    [InlineData("Foundation.Decimal.FormatStyle.Currency")]
    public void GetTypeRecordOrAnyType_FoundationValueType_ReturnsAnyType(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        // Known Foundation value types must NOT be auto-bridged as ObjC classes
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Theory]
    [InlineData("Foundation.URLResponse")]
    [InlineData("Foundation.HTTPURLResponse")]
    [InlineData("Foundation.URLSession")]
    public void TryGetTypeRecord_FoundationClass_ReturnsObjCBridgedRecord(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(swiftType), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
    }

    [Theory]
    [InlineData("Foundation.URLResponse")]
    [InlineData("Foundation.HTTPURLResponse")]
    [InlineData("Foundation.URLSession")]
    public void TryGetAnyTypeFallbackInfo_FoundationClass_ReturnsFalse(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        // Foundation class types are handled via synthetic records, not a fallback
        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec(swiftType), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    // --- Explicit DB registration overrides synthetic records ---

    [Fact]
    public void TryGetTypeRecord_ExplicitDbOverridesSynthetic()
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("UIKit", "/System/Library/Frameworks/UIKit.framework/UIKit");
        // Register an explicit type record for UIKit.UIImage
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIImage");
        module.RegisterType(swiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "UIImage"),
            SwiftTypeName = swiftTypeName,
            MetadataAccessor = "$sSo7UIImageCMa",
            Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        typeDatabase.AddModuleDatabase(module);

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec("UIKit.UIImage"), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        // Explicit registration should be used (namespace "Swift", not "UIKit")
        Assert.Equal("Swift.UIImage", record.CSharpTypeName.FullyQualifiedName);
    }

    // --- Known Apple framework value types are NOT auto-bridged ---

    [Theory]
    // UIKit structs
    [InlineData("UIKit.UIEdgeInsets")]
    [InlineData("UIKit.UIOffset")]
    [InlineData("UIKit.UIFloatRange")]
    [InlineData("UIKit.NSDirectionalEdgeInsets")]
    // SceneKit structs
    [InlineData("SceneKit.SCNVector3")]
    [InlineData("SceneKit.SCNVector4")]
    [InlineData("SceneKit.SCNMatrix4")]
    // MapKit structs
    [InlineData("MapKit.MKCoordinateRegion")]
    [InlineData("MapKit.MKCoordinateSpan")]
    [InlineData("MapKit.MKMapRect")]
    [InlineData("MapKit.MKMapPoint")]
    [InlineData("MapKit.MKMapSize")]
    // ARKit structs
    [InlineData("ARKit.ARRaycastQuery")]
    // AVFoundation structs
    [InlineData("AVFoundation.AVAudioFramePosition")]
    [InlineData("AVFoundation.AVAudioFrameCount")]
    [InlineData("AVFoundation.AVAudioPacketCount")]
    [InlineData("AVFoundation.AVAudioChannelCount")]
    // CoreData structs
    [InlineData("CoreData.NSFetchRequestResultType")]
    public void GetTypeRecordOrAnyType_AppleFrameworkValueType_ReturnsAnyType(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        // Known value types from Apple frameworks must NOT be auto-bridged
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Theory]
    [InlineData("UIKit.UIEdgeInsets")]
    [InlineData("SceneKit.SCNVector3")]
    public void TryGetTypeRecord_AppleFrameworkValueType_ReturnsFalse(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(swiftType), out var record);

        Assert.False(found);
    }

    [Theory]
    [InlineData("UIKit.UIEdgeInsets")]
    [InlineData("SceneKit.SCNVector3")]
    public void TryGetAnyTypeFallbackInfo_AppleFrameworkValueType_ReturnsMissing(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        // Known value types should report as missing (not silently suppressed)
        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec(swiftType), out var fallbackInfo);

        Assert.True(found);
        Assert.NotNull(fallbackInfo);
        Assert.Equal("Type is missing from the type database", fallbackInfo.Value.Reason);
    }

    // --- ObjC enum types from Apple frameworks are NOT auto-bridged ---

    [Theory]
    // UIKit top-level enums
    [InlineData("UIKit.UILayoutPriority")]
    [InlineData("UIKit.NSTextAlignment")]
    [InlineData("UIKit.UIUserInterfaceLayoutDirection")]
    [InlineData("UIKit.UIRectEdge")]
    [InlineData("UIKit.UIRectCorner")]
    [InlineData("UIKit.UIInterfaceOrientation")]
    [InlineData("UIKit.UIInterfaceOrientationMask")]
    [InlineData("UIKit.UIUserInterfaceIdiom")]
    [InlineData("UIKit.UISemanticContentAttribute")]
    [InlineData("UIKit.NSLineBreakMode")]
    [InlineData("UIKit.UITextAutocapitalizationType")]
    [InlineData("UIKit.UITextAutocorrectionType")]
    [InlineData("UIKit.UITextSpellCheckingType")]
    [InlineData("UIKit.UIReturnKeyType")]
    [InlineData("UIKit.UIDataDetectorTypes")]
    // UIKit nested enums
    [InlineData("UIKit.UIControl.ContentVerticalAlignment")]
    [InlineData("UIKit.UIControl.ContentHorizontalAlignment")]
    [InlineData("UIKit.UIAccessibilityTraits")]
    // AVFoundation enums
    [InlineData("AVFoundation.AVMediaType")]
    [InlineData("AVFoundation.AVFileType")]
    [InlineData("AVFoundation.AVLayerVideoGravity")]
    [InlineData("AVFoundation.AVCaptureDevice.Position")]
    [InlineData("AVFoundation.AVCaptureDevice.FlashMode")]
    [InlineData("AVFoundation.AVCaptureDevice.TorchMode")]
    [InlineData("AVFoundation.AVPlayer.TimeControlStatus")]
    [InlineData("AVFoundation.AVPlayer.Status")]
    [InlineData("AVFoundation.AVPlayerItem.Status")]
    // StoreKit enums
    [InlineData("StoreKit.SKPaymentTransactionState")]
    [InlineData("StoreKit.SKError.Code")]
    [InlineData("StoreKit.SKProduct.PeriodUnit")]
    [InlineData("StoreKit.SKProductDiscount.PaymentMode")]
    // CoreBluetooth enums
    [InlineData("CoreBluetooth.CBManagerState")]
    [InlineData("CoreBluetooth.CBManagerAuthorization")]
    [InlineData("CoreBluetooth.CBPeripheralState")]
    [InlineData("CoreBluetooth.CBCharacteristicProperties")]
    [InlineData("CoreBluetooth.CBAttributePermissions")]
    [InlineData("CoreBluetooth.CBCharacteristicWriteType")]
    public void GetTypeRecordOrAnyType_ObjCEnumType_ReturnsAnyType(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Theory]
    [InlineData("UIKit.NSTextAlignment")]
    public void TryGetTypeRecord_ObjCEnumType_ReturnsFalse(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(swiftType), out var record);

        Assert.False(found);
    }

    [Theory]
    [InlineData("UIKit.NSTextAlignment")]
    public void TryGetAnyTypeFallbackInfo_ObjCEnumType_ReturnsMissing(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec(swiftType), out var fallbackInfo);

        Assert.True(found);
        Assert.NotNull(fallbackInfo);
        Assert.Equal("Type is missing from the type database", fallbackInfo.Value.Reason);
    }

    // --- Foundation remapped value types ---

    [Theory]
    [InlineData("Foundation.URLSession.ResponseDisposition", "Foundation", "NSUrlSessionResponseDisposition")]
    [InlineData("Foundation.URLSession.AuthChallengeDisposition", "Foundation", "NSUrlSessionAuthChallengeDisposition")]
    [InlineData("Foundation.RunLoop.Mode", "Foundation", "NSRunLoopMode")]
    [InlineData("Foundation.NSData.WritingOptions", "Foundation", "NSDataWritingOptions")]
    [InlineData("Foundation.Operation.QueuePriority", "Foundation", "NSOperationQueuePriority")]
    [InlineData("Foundation.URLCredential.Persistence", "Foundation", "NSUrlCredentialPersistence")]
    [InlineData("UIKit.UIImage.RenderingMode", "UIKit", "UIImageRenderingMode")]
    [InlineData("UIKit.UIView.AnimationOptions", "UIKit", "UIViewAnimationOptions")]
    [InlineData("Photos.PHImageContentMode", "Photos", "PHImageContentMode")]
    // QuartzCore value types (namespace remapped to CoreAnimation)
    [InlineData("QuartzCore.CATransform3D", "CoreAnimation", "CATransform3D")]
    [InlineData("QuartzCore.CACornerMask", "CoreAnimation", "CACornerMask")]
    [InlineData("QuartzCore.CAEdgeAntialiasingMask", "CoreAnimation", "CAEdgeAntialiasingMask")]
    [InlineData("QuartzCore.CAAutoresizingMask", "CoreAnimation", "CAAutoresizingMask")]
    [InlineData("QuartzCore.CAContentsFormat", "CoreAnimation", "CAContentsFormat")]
    [InlineData("QuartzCore.CACornerCurve", "CoreAnimation", "CACornerCurve")]
    [InlineData("QuartzCore.CAGradientLayerType", "CoreAnimation", "CAGradientLayerType")]
    [InlineData("QuartzCore.CATextLayerAlignmentMode", "CoreAnimation", "CATextLayerAlignmentMode")]
    [InlineData("QuartzCore.CATextLayerTruncationMode", "CoreAnimation", "CATextLayerTruncationMode")]
    [InlineData("QuartzCore.CAScroll", "CoreAnimation", "CAScroll")]
    [InlineData("QuartzCore.CADynamicRange", "CoreAnimation", "CADynamicRange")]
    [InlineData("QuartzCore.CAToneMapMode", "CoreAnimation", "CAToneMapMode")]
    public async Task GetTypeRecordOrAnyType_FoundationRemappedValueType_ReturnsCorrectName(string swiftType, string expectedNamespace, string expectedName)
    {
        var typeDatabase = await CreateDbWithXmlAsync(
            "FoundationDatabase.xml", "UIKitDatabase.xml", "QuartzCoreDatabase.xml", "PhotosDatabase.xml");

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal($"{expectedNamespace}.{expectedName}", record.CSharpTypeName.FullyQualifiedName);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
        Assert.False((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
    }

    [Theory]
    [InlineData("Foundation.URLSession.ResponseDisposition")]
    [InlineData("Foundation.URLSession.AuthChallengeDisposition")]
    [InlineData("Foundation.RunLoop.Mode")]
    [InlineData("Foundation.FileAttributeKey")]
    public void IsObjCModuleType_FoundationValueType_ReturnsFalse(string swiftType)
    {
        var result = TypeDatabaseExtensions.IsObjCModuleType(new NamedTypeSpec(swiftType));

        Assert.False(result);
    }

    // --- Nested Apple enum value types excluded from ObjC bridging ---

    [Fact]
    public async Task GetTypeRecordOrAnyType_NSRegularExpressionOptions_ReturnsRemappedRecord()
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation.NSRegularExpression.Options"));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal("Foundation.NSRegularExpressionOptions", record.CSharpTypeName.FullyQualifiedName);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
    }

    [Fact]
    public void IsObjCModuleType_NSRegularExpressionOptions_ReturnsFalse()
    {
        var result = TypeDatabaseExtensions.IsObjCModuleType(new NamedTypeSpec("Foundation.NSRegularExpression.Options"));

        Assert.False(result);
    }

    [Theory]
    [InlineData("UIKit.UIImage.RenderingMode")]
    [InlineData("UIKit.UIView.AnimationOptions")]
    [InlineData("Foundation.NSData.WritingOptions")]
    [InlineData("Photos.PHImageContentMode")]
    // Bug 4+5: UIKit value-type enums that were misclassified as ObjC classes
    [InlineData("UIKit.UITableView.Style")]
    [InlineData("UIKit.UITextField.DidEndEditingReason")]
    [InlineData("UIKit.UISwipeGestureRecognizer.Direction")]
    [InlineData("UIKit.UICollectionView.ScrollDirection")]
    // Additional UIKit nested enums for coverage
    [InlineData("UIKit.UITableViewCell.CellStyle")]
    [InlineData("UIKit.UIGestureRecognizer.State")]
    [InlineData("UIKit.UIAlertController.Style")]
    [InlineData("UIKit.UIAlertAction.Style")]
    [InlineData("UIKit.UIStackView.Alignment")]
    [InlineData("UIKit.UIBarButtonItem.SystemItem")]
    public void IsObjCModuleType_NestedAppleEnumValueType_ReturnsFalse(string swiftType)
    {
        var result = TypeDatabaseExtensions.IsObjCModuleType(new NamedTypeSpec(swiftType));

        Assert.False(result);
    }

    // --- UIKeyboardType is a simple enum in UIKitDatabase.xml, NOT an ObjC class ---
    // UIKeyboardType is a C enum (NS_ENUM) in UIKit, not an NSObject subclass.
    // Registered in UIKitDatabase.xml with simpleEnum="true" and in AppleFrameworkValueTypes
    // to prevent ObjC auto-bridging. PInvokeEmitter uses integer cast path.

    [Fact]
    public void GetTypeRecordOrAnyType_UIKeyboardType_ReturnsAnyType()
    {
        // UIKeyboardType is in AppleFrameworkValueTypes but not in the in-memory TypeDatabase
        // (it's only in UIKitDatabase.xml loaded at runtime). Without the XML loaded,
        // it's excluded from ObjC bridging → AnyType.
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("UIKit.UIKeyboardType"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void IsObjCModuleType_UIKeyboardType_ReturnsFalse()
    {
        // UIKeyboardType is in AppleFrameworkValueTypes → not treated as ObjC class
        var result = TypeDatabaseExtensions.IsObjCModuleType(new NamedTypeSpec("UIKit.UIKeyboardType"));

        Assert.False(result);
    }

    // --- Module namespace overrides ---

    [Fact]
    public void GetTypeRecordOrAnyType_QuartzCoreClass_UsesRemappedNamespace()
    {
        var typeDatabase = new TypeDatabase();

        // QuartzCore types that aren't in AppleFrameworkValueTypes or ClassRemappings
        // should auto-bridge with CoreAnimation namespace
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("QuartzCore.CALayer"));

        Assert.Equal("CoreAnimation.CALayer", record.CSharpTypeName.FullyQualifiedName);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
    }

    [Fact]
    public void GetTypeRecordOrAnyType_AVFAudioClass_UsesAVFoundationNamespace()
    {
        var typeDatabase = new TypeDatabase();

        // AVFAudio module maps to AVFoundation namespace
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("AVFAudio.AVAudioSession"));

        Assert.Equal("AVFoundation.AVAudioSession", record.CSharpTypeName.FullyQualifiedName);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
    }

    [Theory]
    [InlineData("QuartzCore.CALayer")]
    [InlineData("AVFAudio.AVAudioSession")]
    [InlineData("PassKit.PKPayment")]
    [InlineData("PassKit.PKShippingMethod")]
    public void IsObjCModuleType_NamespaceOverrideModules_ReturnsTrue(string swiftType)
    {
        var result = TypeDatabaseExtensions.IsObjCModuleType(new NamedTypeSpec(swiftType));

        Assert.True(result);
    }

    // --- PassKit value types must NOT be treated as ObjC classes ---

    [Theory]
    [InlineData("PassKit.PKPaymentButtonType")]
    [InlineData("PassKit.PKPaymentNetwork")]
    public void IsObjCModuleType_PassKitValueType_ReturnsFalse(string swiftType)
    {
        var result = TypeDatabaseExtensions.IsObjCModuleType(new NamedTypeSpec(swiftType));

        Assert.False(result, $"{swiftType} is a value type, not an ObjC class");
    }

    // --- Modules NOT in auto-bridge set (removed for safety) ---

    [Theory]
    [InlineData("Metal.MTLOrigin")]
    [InlineData("CoreMotion.CMAcceleration")]
    [InlineData("CoreLocation.CLLocationCoordinate2D")]
    [InlineData("MapKit.MKCoordinateRegion")]
    public void GetTypeRecordOrAnyType_ExcludedModuleType_ReturnsAnyType(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        // Types from modules with many value types are NOT auto-bridged
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    // --- Negative / boundary tests ---

    [Fact]
    public void GetTypeRecordOrAnyType_UnknownModuleType_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("SomeUnknownModule.SomeType"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void GetTypeRecordOrThrow_UnknownModuleType_Throws()
    {
        var typeDatabase = new TypeDatabase();

        Assert.Throws<Exception>(() =>
            typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec("SomeUnknownModule.SomeType")));
    }

    // --- Bare generic type guard tests (WU5) ---

    [Fact]
    public void GetTypeRecordOrAnyType_BareDictionary_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Swift.Dictionary"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void GetTypeRecordOrAnyType_BoundDictionary_ReturnsRecord()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftDictionary"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
                MetadataAccessor = "$sSDMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var boundDict = new NamedTypeSpec("Swift.Dictionary");
        boundDict.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        boundDict.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var record = typeDatabase.GetTypeRecordOrAnyType(boundDict);

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal("Swift.SwiftDictionary", record.CSharpTypeName.FullyQualifiedName);
    }

    [Fact]
    public void GetTypeRecordOrAnyType_BareArray_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Swift.Array"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void GetTypeRecordOrAnyType_NonGenericType_ReturnsRecord()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Swift.Int32"));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal("int", record.CSharpTypeName.FullyQualifiedName);
    }

    // --- Apple framework value type remapping tests ---

    [Theory]
    [InlineData("Foundation._NSRange", "Foundation", "NSRange")]
    [InlineData("Foundation.JSONSerialization.ReadingOptions", "Foundation", "NSJsonReadingOptions")]
    [InlineData("Foundation.JSONSerialization.WritingOptions", "Foundation", "NSJsonWritingOptions")]
    [InlineData("Foundation.Stream.Event", "Foundation", "NSStreamEvent")]
    public async Task GetTypeRecordOrAnyType_RemappedValueType_ReturnsCorrectDotNetType(string swiftType, string expectedNamespace, string expectedName)
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal($"{expectedNamespace}.{expectedName}", record.CSharpTypeName.FullyQualifiedName);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
        // Must NOT have ObjCBridged flag (these are value types, not classes)
        Assert.False((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
    }

    [Theory]
    [InlineData("Foundation._NSRange", "Foundation", "NSRange")]
    [InlineData("Foundation.JSONSerialization.ReadingOptions", "Foundation", "NSJsonReadingOptions")]
    [InlineData("Foundation.JSONSerialization.WritingOptions", "Foundation", "NSJsonWritingOptions")]
    public async Task TryGetTypeRecord_RemappedValueType_ReturnsCorrectDotNetType(string swiftType, string expectedNamespace, string expectedName)
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(swiftType), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal($"{expectedNamespace}.{expectedName}", record.CSharpTypeName.FullyQualifiedName);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
    }

    [Theory]
    [InlineData("Foundation._NSRange")]
    [InlineData("Foundation.JSONSerialization.ReadingOptions")]
    [InlineData("Foundation.JSONSerialization.WritingOptions")]
    public async Task GetTypeRecordOrThrow_RemappedValueType_ReturnsCorrectDotNetType(string swiftType)
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        // Should NOT throw — these types have remapped records
        var record = typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec(swiftType));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
    }

    [Theory]
    [InlineData("Foundation._NSRange")]
    [InlineData("Foundation.JSONSerialization.ReadingOptions")]
    [InlineData("Foundation.JSONSerialization.WritingOptions")]
    public async Task IsTypeProcessed_RemappedValueType_ReturnsTrue(string swiftType)
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var result = typeDatabase.IsTypeProcessed(new NamedTypeSpec(swiftType));

        Assert.True(result);
    }

    [Theory]
    [InlineData("Foundation._NSRange")]
    [InlineData("Foundation.JSONSerialization.ReadingOptions")]
    [InlineData("Foundation.JSONSerialization.WritingOptions")]
    public async Task TryGetAnyTypeFallbackInfo_RemappedValueType_ReturnsFalse(string swiftType)
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        // Remapped types should not report as missing
        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec(swiftType), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    [Fact]
    public async Task GetTypeRecordOrAnyType_NSRange_ReturnsRemappedNotObjCBridged()
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        // Foundation.NSRange (without underscore) is in AppleFrameworkValueTypes → AnyType
        // Foundation._NSRange (with underscore) should remap to Foundation.NSRange
        var rangeRecord = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation.NSRange"));
        var underscoreRangeRecord = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation._NSRange"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, rangeRecord); // NSRange without underscore → AnyType (no DB entry)
        Assert.NotEqual(TypeDatabaseExtensions.AnyType, underscoreRangeRecord); // _NSRange → remapped
        Assert.Equal("Foundation.NSRange", underscoreRangeRecord.CSharpTypeName.FullyQualifiedName);
    }

    // --- CloseCode value-type remapping test ---
    // Without XML loaded, CloseCode resolves via AppleFrameworkTypeRemappings (Struct fallback).
    // At runtime, FoundationDatabase.xml provides the correct kind="enum" simpleEnum="true" record,
    // which takes priority over the remapping and ensures the emitter uses the integer cast path
    // instead of SwiftObjectHelper<T> (which requires ISwiftObject — invalid for .NET enums).

    [Fact]
    public async Task GetTypeRecordOrAnyType_FoundationRemappedValueType_ReturnsRemappedRecord()
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        // CloseCode is an enum in .NET iOS (NSUrlSessionWebSocketCloseCode), not a class
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation.URLSessionWebSocketTask.CloseCode"));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal("Foundation.NSUrlSessionWebSocketCloseCode", record.CSharpTypeName.FullyQualifiedName);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
        // Must NOT have ObjCBridged flag (this is a value type, not a class)
        Assert.False((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
    }

    [Fact]
    public void IsObjCModuleType_FoundationCloseCode_ReturnsFalse()
    {
        // CloseCode is in AppleFrameworkValueTypes → not treated as ObjC class
        var result = TypeDatabaseExtensions.IsObjCModuleType(new NamedTypeSpec("Foundation.URLSessionWebSocketTask.CloseCode"));

        Assert.False(result);
    }

    // --- Foundation types with no .NET equivalent → AnyType ---

    [Theory]
    [InlineData("Foundation.JSONEncoder")]
    [InlineData("Foundation.NSNotification.Name")]
    [InlineData("Foundation.objc_AssociationPolicy")]
    public void GetTypeRecordOrAnyType_FoundationNonExistentType_ReturnsAnyType(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    // --- AVFoundation/UIKit value-type exclusions → AnyType ---

    [Theory]
    [InlineData("AVFoundation.AVCaptureSession.Preset")]
    [InlineData("AVFoundation.AVCaptureDevice.AutoFocusRangeRestriction")]
    [InlineData("AVFoundation.AVCaptureDevice.DeviceType")]
    [InlineData("AVFoundation.AVCaptureVideoOrientation")]
    [InlineData("UIKit.NSWritingDirection")]
    [InlineData("UIKit.UIKeyboardType")]
    // UIKit nested ObjC enums (value type remappings)
    [InlineData("UIKit.UIImage.ResizingMode")]
    [InlineData("UIKit.UIImage.SymbolScale")]
    [InlineData("UIKit.UIImage.SymbolWeight")]
    [InlineData("UIKit.UIView.AutoresizingMask")]
    [InlineData("UIKit.UIView.AnimationCurve")]
    public void GetTypeRecordOrAnyType_NewAppleFrameworkValueType_ReturnsAnyType(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    // --- Security opaque CF types → IntPtr (loaded from SecurityDatabase.xml) ---

    [Fact]
    public async Task LoadSecurityDatabase_SecTrust_ResolvesToIntPtr()
    {
        var typeDatabase = new TypeDatabase();
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SecurityDatabase.xml");
        await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Security.SecTrust");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("System", record!.CSharpTypeName.Namespace);
        Assert.Equal("IntPtr", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordFlags.Frozen, record.Flags);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
    }

    [Theory]
    [InlineData("Security.SecCertificate")]
    [InlineData("Security.SecKey")]
    [InlineData("Security.SecIdentity")]
    public async Task LoadSecurityDatabase_OpaqueCFTypes_ResolveToIntPtr(string swiftType)
    {
        var typeDatabase = new TypeDatabase();
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SecurityDatabase.xml");
        await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftType);
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("System", record!.CSharpTypeName.Namespace);
        Assert.Equal("IntPtr", record.CSharpTypeName.Name);
    }

    // --- IndexPath → NSIndexPath ObjC-bridged (loaded from FoundationDatabase.xml) ---

    [Fact]
    public async Task LoadFoundationDatabase_IndexPath_ResolvesToNSIndexPath()
    {
        var typeDatabase = new TypeDatabase();
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "FoundationDatabase.xml");
        await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.IndexPath");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("Foundation", record!.CSharpTypeName.Namespace);
        Assert.Equal("NSIndexPath", record.CSharpTypeName.Name);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
    }

    // --- JSONDecoder → AnyType (AppleFrameworkValueTypes exclusion) ---

    [Fact]
    public void GetTypeRecordOrAnyType_JSONDecoder_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation.JSONDecoder"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void IsObjCModuleType_JSONDecoder_ReturnsFalse()
    {
        // JSONDecoder is in AppleFrameworkValueTypes → not treated as ObjC class
        var result = TypeDatabaseExtensions.IsObjCModuleType(new NamedTypeSpec("Foundation.JSONDecoder"));

        Assert.False(result);
    }

    // --- XMLParser → AnyType (AppleFrameworkValueTypes exclusion) ---
    // NSXMLParser is not bound in .NET iOS — excluded from ObjC bridging

    [Fact]
    public void GetTypeRecordOrAnyType_XMLParser_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation.XMLParser"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void IsObjCModuleType_XMLParser_ReturnsFalse()
    {
        // XMLParser is in AppleFrameworkValueTypes → not treated as ObjC class
        var result = TypeDatabaseExtensions.IsObjCModuleType(new NamedTypeSpec("Foundation.XMLParser"));

        Assert.False(result);
    }

    #region ConcatWithOverlapDedup

    [Theory]
    [InlineData("UITableViewCell", "CellStyle", "UITableViewCellStyle")]
    [InlineData("UIView", "ContentMode", "UIViewContentMode")]
    [InlineData("UIScrollView", "IndicatorStyle", "UIScrollViewIndicatorStyle")]
    [InlineData("UIStackView", "Alignment", "UIStackViewAlignment")]
    [InlineData("ABC", "CDE", "ABCDE")]
    [InlineData("Hello", "World", "HelloWorld")]
    public void ConcatWithOverlapDedup_VariousCases_ProducesCorrectResult(string first, string second, string expected)
    {
        var result = TypeDatabaseExtensions.ConcatWithOverlapDedup(first, second);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConcatWithOverlapDedup_FullOverlap_DeduplicatesCompletely()
    {
        // If second is entirely a suffix of first, second is consumed
        var result = TypeDatabaseExtensions.ConcatWithOverlapDedup("UITableViewCell", "Cell");

        Assert.Equal("UITableViewCell", result);
    }

    [Fact]
    public void ConcatWithOverlapDedup_NoOverlap_SimpleConcatenation()
    {
        var result = TypeDatabaseExtensions.ConcatWithOverlapDedup("UIImage", "RenderingMode");

        Assert.Equal("UIImageRenderingMode", result);
    }

    #endregion

    #region AppleFrameworkSimpleEnumRemappings

    [Fact]
    public async Task TryGetTypeRecord_UIViewContentMode_ReturnsSimpleEnumRecord()
    {
        var typeDatabase = await CreateDbWithXmlAsync("UIKitDatabase.xml");

        var found = typeDatabase.TryGetTypeRecord(
            new NamedTypeSpec("UIKit.UIView.ContentMode"), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal(TypeRecordKind.Enum, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
        Assert.Equal("UIKit.UIViewContentMode", record.CSharpTypeName.FullyQualifiedName);
    }

    [Fact]
    public async Task TryGetTypeRecord_UIControlState_ReturnsStruct()
    {
        // UIControl.State is an OptionSet (struct), not an enum.
        // Changed from kind="enum" to kind="struct" to fix guard-let compilation error.
        var typeDatabase = await CreateDbWithXmlAsync("UIKitDatabase.xml");

        var found = typeDatabase.TryGetTypeRecord(
            new NamedTypeSpec("UIKit.UIControl.State"), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
    }

    [Fact]
    public async Task TryGetTypeRecord_FlatUIKitEnum_PreservesName()
    {
        var typeDatabase = await CreateDbWithXmlAsync("UIKitDatabase.xml");

        var found = typeDatabase.TryGetTypeRecord(
            new NamedTypeSpec("UIKit.UIBarStyle"), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal("UIKit.UIBarStyle", record.CSharpTypeName.FullyQualifiedName);
    }

    [Fact]
    public async Task GetTypeRecordOrThrow_UIViewContentMode_ReturnsEnumRecord()
    {
        var typeDatabase = await CreateDbWithXmlAsync("UIKitDatabase.xml");
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView.ContentMode");

        var record = typeDatabase.GetTypeRecordOrThrow(swiftTypeName);

        Assert.Equal(TypeRecordKind.Enum, record.Kind);
        Assert.Equal("UIKit.UIViewContentMode", record.CSharpTypeName.FullyQualifiedName);
    }

    [Fact]
    public async Task GetTypeRecordOrAnyType_UIViewContentMode_ReturnsEnumRecord()
    {
        var typeDatabase = await CreateDbWithXmlAsync("UIKitDatabase.xml");
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView.ContentMode");

        var record = typeDatabase.GetTypeRecordOrAnyType(swiftTypeName);

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal(TypeRecordKind.Enum, record.Kind);
    }

    [Fact]
    public async Task TryGetTypeRecord_UIViewContentMode_PInvokeUnderlyingType_IsLong()
    {
        var typeDatabase = await CreateDbWithXmlAsync("UIKitDatabase.xml");

        typeDatabase.TryGetTypeRecord(
            new NamedTypeSpec("UIKit.UIView.ContentMode"), out var record);

        Assert.NotNull(record);
        Assert.Equal("Int", record!.RawValueTypeName);
        // Int maps to C# "long" in P/Invoke (platform-width integer)
        Assert.Equal("long", EnumHandler.GetCSharpEnumUnderlyingType(record.RawValueTypeName!));
    }

    [Fact]
    public async Task CoreTryGetTypeRecord_UIViewContentMode_ReturnsEnumRecord()
    {
        // Test via TypeDatabase directly (not extension), verifying ClosureHandler path works
        var typeDatabase = await CreateDbWithXmlAsync("UIKitDatabase.xml");
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView.ContentMode");

        var found = typeDatabase.TryGetTypeRecord(swiftTypeName, out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal(TypeRecordKind.Enum, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
    }

    [Fact]
    public async Task IsTypeProcessed_UIViewContentMode_NamedTypeSpec_ReturnsTrue()
    {
        var typeDatabase = await CreateDbWithXmlAsync("UIKitDatabase.xml");

        var processed = typeDatabase.IsTypeProcessed(
            new NamedTypeSpec("UIKit.UIView.ContentMode"));

        Assert.True(processed);
    }

    [Fact]
    public async Task IsTypeProcessed_UIViewContentMode_SwiftTypeName_ReturnsTrue()
    {
        // Exercises TypeDatabase.IsTypeProcessed(SwiftTypeName) directly,
        // ensuring the core path resolves remapped Apple enums independently
        // of the extension-level IsRemappedAppleEnum(NamedTypeSpec) check.
        var typeDatabase = await CreateDbWithXmlAsync("UIKitDatabase.xml");
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView.ContentMode");

        var processed = typeDatabase.IsTypeProcessed(swiftTypeName);

        Assert.True(processed);
    }

    #endregion

    #region GetTypeRecordOrAnyType — Unsupported Apple Module Suppression

    [Theory]
    [InlineData("Combine.Publisher")]
    [InlineData("XCTest.XCTestCase")]
    public async Task GetTypeRecordOrAnyType_UnsupportedAppleModule_ReturnsAnyType(string typeName)
    {
        // Types from unsupported Apple modules (Combine, XCTest) without C# stubs
        // must resolve to AnyType so that members referencing them are suppressed.
        var typeDatabase = new TypeDatabase();
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftUIDatabase.xml");
        if (File.Exists(dbPath))
            await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

        var typeSpec = new NamedTypeSpec(typeName);
        var record = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public async Task GetTypeRecordOrAnyType_RegisteredSwiftUIType_ReturnsDatabaseRecord()
    {
        // CQ-6: Registered non-generic SwiftUI types (with C# ISwiftObject stubs)
        // resolve to the database record, NOT AnyType.
        var typeDatabase = new TypeDatabase();
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftUIDatabase.xml");
        if (File.Exists(dbPath))
            await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

        var typeSpec = new NamedTypeSpec("SwiftUI.AnyView");
        var record = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal("SwiftUI.AnyView", record.SwiftTypeName.ModuleQualifiedName);
    }

    [Theory]
    [InlineData("UIKit.UIImage")]
    [InlineData("Foundation.NSData")]
    public void GetTypeRecordOrAnyType_SupportedAppleModule_DoesNotReturnAnyType(string typeName)
    {
        // Types from supported Apple modules (UIKit, Foundation) should NOT be
        // suppressed — they have C# equivalents via .NET iOS bindings.
        var typeDatabase = new TypeDatabase();
        var typeSpec = new NamedTypeSpec(typeName);
        var record = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
    }

    #endregion

    #region NewAppleFrameworkDatabaseEntries

    // --- UIKit new enum entries resolve correctly when XML is loaded ---

    [Theory]
    [InlineData("UIKit.NSTextAlignment", "UIKit.UITextAlignment", TypeRecordKind.Enum, "Int")]
    [InlineData("UIKit.NSLineBreakMode", "UIKit.UILineBreakMode", TypeRecordKind.Enum, "Int")]
    [InlineData("UIKit.NSWritingDirection", "Foundation.NSWritingDirection", TypeRecordKind.Enum, "Int")]
    [InlineData("UIKit.UICollectionView.ScrollDirection", "UIKit.UICollectionViewScrollDirection", TypeRecordKind.Enum, "Int")]
    [InlineData("UIKit.UIGestureRecognizer.State", "UIKit.UIGestureRecognizerState", TypeRecordKind.Enum, "Int")]
    [InlineData("UIKit.UITableView.RowAnimation", "UIKit.UITableViewRowAnimation", TypeRecordKind.Enum, "Int")]
    [InlineData("UIKit.UITableViewCell.CellStyle", "UIKit.UITableViewCellStyle", TypeRecordKind.Enum, "Int")]
    [InlineData("UIKit.UIAlertAction.Style", "UIKit.UIAlertActionStyle", TypeRecordKind.Enum, "Int")]
    [InlineData("UIKit.UIBarButtonItem.SystemItem", "UIKit.UIBarButtonSystemItem", TypeRecordKind.Enum, "UInt")]
    [InlineData("UIKit.UITabBarItem.SystemItem", "UIKit.UITabBarSystemItem", TypeRecordKind.Enum, "UInt")]
    [InlineData("UIKit.UIImage.ResizingMode", "UIKit.UIImageResizingMode", TypeRecordKind.Enum, "Int")]
    [InlineData("UIKit.UIView.AnimationCurve", "UIKit.UIViewAnimationCurve", TypeRecordKind.Enum, "Int")]
    public async Task TryGetTypeRecord_NewUIKitEnum_ResolvesCorrectly(
        string swiftType, string expectedCSharp, TypeRecordKind expectedKind, string expectedRawType)
    {
        var typeDatabase = await CreateDbWithXmlAsync("UIKitDatabase.xml");

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(swiftType), out var record);

        Assert.True(found, $"{swiftType} should resolve from UIKitDatabase.xml");
        Assert.NotNull(record);
        Assert.Equal(expectedKind, record.Kind);
        Assert.Equal(expectedCSharp, record.CSharpTypeName.FullyQualifiedName);
        Assert.Equal(expectedRawType, record.RawValueTypeName);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
        Assert.False(record.Flags.HasFlag(TypeRecordFlags.ObjCBridged));
    }

    // --- UIKit new struct/options entries resolve correctly ---

    [Theory]
    [InlineData("UIKit.NSDirectionalEdgeInsets", "UIKit.NSDirectionalEdgeInsets")]
    [InlineData("UIKit.UIAccessibilityTraits", "UIKit.UIAccessibilityTraits")]
    [InlineData("UIKit.UIRectCorner", "UIKit.UIRectCorner")]
    [InlineData("UIKit.UIRectEdge", "UIKit.UIRectEdge")]
    [InlineData("UIKit.UIDataDetectorTypes", "UIKit.UIDataDetectorType")]
    [InlineData("UIKit.UISwipeGestureRecognizer.Direction", "UIKit.UISwipeGestureRecognizerDirection")]
    [InlineData("UIKit.UIStackView.Alignment", "UIKit.UIStackViewAlignment")]
    [InlineData("UIKit.UIView.AutoresizingMask", "UIKit.UIViewAutoresizing")]
    [InlineData("UIKit.UIFont.Weight", "UIKit.UIFontWeight")]
    [InlineData("UIKit.UIContentSizeCategory", "UIKit.UIContentSizeCategory")]
    [InlineData("UIKit.UIOffset", "UIKit.UIOffset")]
    public async Task TryGetTypeRecord_NewUIKitStruct_ResolvesCorrectly(
        string swiftType, string expectedCSharp)
    {
        var typeDatabase = await CreateDbWithXmlAsync("UIKitDatabase.xml");

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(swiftType), out var record);

        Assert.True(found, $"{swiftType} should resolve from UIKitDatabase.xml");
        Assert.NotNull(record);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
        Assert.Equal(expectedCSharp, record.CSharpTypeName.FullyQualifiedName);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
        Assert.False(record.Flags.HasFlag(TypeRecordFlags.ObjCBridged));
    }

    // --- AVFoundation new entries resolve correctly ---

    [Theory]
    [InlineData("AVFoundation.AVCaptureDevice.FlashMode", "AVFoundation.AVCaptureFlashMode")]
    [InlineData("AVFoundation.AVPlayer.Status", "AVFoundation.AVPlayerStatus")]
    [InlineData("AVFoundation.AVMediaType", "AVFoundation.AVMediaTypes")]
    public async Task TryGetTypeRecord_NewAVFoundation_ResolvesCorrectly(
        string swiftType, string expectedCSharp)
    {
        var typeDatabase = await CreateDbWithXmlAsync("AVFoundationDatabase.xml");

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(swiftType), out var record);

        Assert.True(found, $"{swiftType} should resolve from AVFoundationDatabase.xml");
        Assert.NotNull(record);
        Assert.Equal(expectedCSharp, record.CSharpTypeName.FullyQualifiedName);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
    }

    // --- Photos new entries resolve correctly ---

    [Theory]
    [InlineData("Photos.PHAccessLevel", "Photos.PHAccessLevel")]
    [InlineData("Photos.PHAssetMediaType", "Photos.PHAssetMediaType")]
    [InlineData("Photos.PHAssetCollectionType", "Photos.PHAssetCollectionType")]
    public async Task TryGetTypeRecord_NewPhotos_ResolvesCorrectly(
        string swiftType, string expectedCSharp)
    {
        var typeDatabase = await CreateDbWithXmlAsync("PhotosDatabase.xml");

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(swiftType), out var record);

        Assert.True(found, $"{swiftType} should resolve from PhotosDatabase.xml");
        Assert.NotNull(record);
        Assert.Equal(TypeRecordKind.Enum, record.Kind);
        Assert.Equal(expectedCSharp, record.CSharpTypeName.FullyQualifiedName);
    }

    // --- New framework databases load and resolve correctly ---

    [Theory]
    [InlineData("CoreBluetoothDatabase.xml", "CoreBluetooth.CBManagerState", "CoreBluetooth.CBManagerState", TypeRecordKind.Enum)]
    [InlineData("CoreBluetoothDatabase.xml", "CoreBluetooth.CBCharacteristicProperties", "CoreBluetooth.CBCharacteristicProperties", TypeRecordKind.Struct)]
    [InlineData("CoreLocationDatabase.xml", "CoreLocation.CLLocationCoordinate2D", "CoreLocation.CLLocationCoordinate2D", TypeRecordKind.Struct)]    [InlineData("CoreLocationDatabase.xml", "CoreLocation.CLAuthorizationStatus", "CoreLocation.CLAuthorizationStatus", TypeRecordKind.Enum)]
    [InlineData("CoreLocationDatabase.xml", "CoreLocation.CLAccuracyAuthorization", "CoreLocation.CLAccuracyAuthorization", TypeRecordKind.Enum)]
    [InlineData("MapKitDatabase.xml", "MapKit.MKCoordinateRegion", "MapKit.MKCoordinateRegion", TypeRecordKind.Struct)]
    [InlineData("MapKitDatabase.xml", "MapKit.MKDirectionsTransportType", "MapKit.MKDirectionsTransportType", TypeRecordKind.Struct)]
    [InlineData("MetalDatabase.xml", "Metal.MTLPixelFormat", "Metal.MTLPixelFormat", TypeRecordKind.Enum)]
    [InlineData("MetalDatabase.xml", "Metal.MTLSize", "Metal.MTLSize", TypeRecordKind.Struct)]
    [InlineData("CoreMLDatabase.xml", "CoreML.MLComputeUnits", "CoreML.MLComputeUnits", TypeRecordKind.Enum)]
    [InlineData("StoreKitDatabase.xml", "StoreKit.SKPaymentTransactionState", "StoreKit.SKPaymentTransactionState", TypeRecordKind.Enum)]
    [InlineData("StoreKitDatabase.xml", "StoreKit.SKError.Code", "StoreKit.SKError", TypeRecordKind.Enum)]
    [InlineData("SceneKitDatabase.xml", "SceneKit.SCNVector3", "SceneKit.SCNVector3", TypeRecordKind.Struct)]
    [InlineData("NaturalLanguageDatabase.xml", "NaturalLanguage.NLLanguage", "NaturalLanguage.NLLanguage", TypeRecordKind.Struct)]
    [InlineData("NaturalLanguageDatabase.xml", "NaturalLanguage.NLTagScheme", "NaturalLanguage.NLTagScheme", TypeRecordKind.Struct)]
    [InlineData("NaturalLanguageDatabase.xml", "NaturalLanguage.NLTokenUnit", "NaturalLanguage.NLTokenUnit", TypeRecordKind.Struct)]
    public async Task TryGetTypeRecord_NewFrameworkDatabase_ResolvesCorrectly(
        string xmlFile, string swiftType, string expectedCSharp, TypeRecordKind expectedKind)
    {
        var typeDatabase = await CreateDbWithXmlAsync(xmlFile);

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(swiftType), out var record);

        Assert.True(found, $"{swiftType} should resolve from {xmlFile}");
        Assert.NotNull(record);
        Assert.Equal(expectedKind, record.Kind);
        Assert.Equal(expectedCSharp, record.CSharpTypeName.FullyQualifiedName);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
        Assert.False(record.Flags.HasFlag(TypeRecordFlags.ObjCBridged));
    }

    // --- Types that previously returned AnyType now resolve when XML is loaded ---

    [Theory]
    [InlineData("Metal.MTLOrigin")]
    [InlineData("CoreLocation.CLLocationCoordinate2D")]
    [InlineData("MapKit.MKCoordinateRegion")]
    public async Task GetTypeRecordOrAnyType_PreviouslyExcludedModuleType_ResolvesWithXml(string swiftType)
    {
        // These types used to return AnyType because their modules had no XML databases.
        // With the new databases loaded, they should resolve to proper type records.
        var typeDatabase = await CreateDbWithXmlAsync(
            "MetalDatabase.xml", "CoreLocationDatabase.xml", "MapKitDatabase.xml");

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
    }

    // --- Intentional AnyType types still return AnyType even with XML loaded ---

    [Theory]
    [InlineData("Foundation.XMLParser")]
    [InlineData("Foundation.objc_AssociationPolicy")]
    public async Task GetTypeRecordOrAnyType_IntentionalAnyType_StillReturnsAnyType(string swiftType)
    {
        // These types are in valueTypes to prevent ObjC auto-bridging but have no
        // .NET equivalent. They should STILL return AnyType even with Foundation XML loaded.
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    #endregion

    #region Session1_FoundationTypeDatabaseExpansion

    // --- NSNotification.Name resolves from FoundationDatabase.xml ---

    [Fact]
    public async Task LoadFoundationDatabase_NSNotificationName_ResolvesToNSString()
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.NSNotification.Name");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("Foundation", record!.CSharpTypeName.Namespace);
        Assert.Equal("NSString", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.ObjCBridged));
    }

    // --- CharacterSet, Calendar, Decimal resolve from FoundationDatabase.xml ---

    [Fact]
    public async Task LoadFoundationDatabase_CharacterSet_ResolvesToObjCBridgedClass()
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.CharacterSet");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("Foundation", record!.CSharpTypeName.Namespace);
        Assert.Equal("NSCharacterSet", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.ObjCBridged));
    }

    [Fact]
    public async Task LoadFoundationDatabase_Calendar_ResolvesToObjCBridgedClass()
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Calendar");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("Foundation", record!.CSharpTypeName.Namespace);
        Assert.Equal("NSCalendar", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.ObjCBridged));
    }

    // --- CGBlendMode resolves as simple enum from CoreGraphicsDatabase.xml ---

    [Fact]
    public async Task LoadCoreGraphicsDatabase_CGBlendMode_ResolvesToSimpleEnum()
    {
        var typeDatabase = await CreateDbWithXmlAsync("CoreGraphicsDatabase.xml");

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGBlendMode");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("CoreGraphics", record!.CSharpTypeName.Namespace);
        Assert.Equal("CGBlendMode", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Enum, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
        Assert.Equal("Int32", record.RawValueTypeName);
    }

    // --- CMTime resolves from CoreMediaDatabase.xml ---

    [Fact]
    public async Task LoadCoreMediaDatabase_CMTime_ResolvesToIntPtr()
    {
        var typeDatabase = await CreateDbWithXmlAsync("CoreMediaDatabase.xml");

        Assert.True(typeDatabase.IsModuleLoaded("CoreMedia"));
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreMedia.CMTime");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("System", record!.CSharpTypeName.Namespace);
        Assert.Equal("IntPtr", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
    }

    // --- SecTrustResultType resolves as uint (System.UInt32 keyword alias) from SecurityDatabase.xml ---

    [Fact]
    public async Task LoadSecurityDatabase_SecTrustResultType_ResolvesToUInt32()
    {
        var typeDatabase = await CreateDbWithXmlAsync("SecurityDatabase.xml");

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Security.SecTrustResultType");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        // System.UInt32 is normalized to C# keyword "uint" (empty namespace)
        Assert.Equal("", record!.CSharpTypeName.Namespace);
        Assert.Equal("uint", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
    }

    // --- New XML entries don't conflict with existing synthetic ObjC bridge records ---

    [Theory]
    [InlineData("Foundation.NSNotification.Name", "Foundation", "NSString")]
    [InlineData("Foundation.CharacterSet", "Foundation", "NSCharacterSet")]
    [InlineData("Foundation.Calendar", "Foundation", "NSCalendar")]
    [InlineData("Foundation.JSONEncoder", "Foundation", "NSObject")]
    [InlineData("Foundation.JSONDecoder", "Foundation", "NSObject")]
    [InlineData("Foundation.Locale", "Foundation", "NSLocale")]
    [InlineData("Foundation.Decimal", "Foundation", "NSDecimalNumber")]
    public async Task LoadFoundationDatabase_NewEntries_ResolveViaGetTypeRecordOrAnyType(
        string swiftType, string expectedNamespace, string expectedName)
    {
        // Verify GetTypeRecordOrAnyType returns the XML record, not AnyType or synthetic
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal(expectedNamespace, record.CSharpTypeName.Namespace);
        Assert.Equal(expectedName, record.CSharpTypeName.Name);
    }

    #endregion

    #region Session5_FoundationTypeDatabaseSweep

    // --- JSONEncoder resolves from FoundationDatabase.xml ---

    [Fact]
    public async Task LoadFoundationDatabase_JSONEncoder_ResolvesToObjCBridgedClass()
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.JSONEncoder");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("Foundation", record!.CSharpTypeName.Namespace);
        Assert.Equal("NSObject", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.ObjCBridged));
    }

    [Fact]
    public async Task LoadFoundationDatabase_JSONDecoder_ResolvesToObjCBridgedClass()
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.JSONDecoder");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("Foundation", record!.CSharpTypeName.Namespace);
        Assert.Equal("NSObject", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.ObjCBridged));
    }

    // --- Locale resolves as ObjC-bridged class (bridges to NSLocale) ---

    [Fact]
    public async Task LoadFoundationDatabase_Locale_ResolvesToNSLocale()
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Locale");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("Foundation", record!.CSharpTypeName.Namespace);
        Assert.Equal("NSLocale", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.ObjCBridged));
    }

    [Fact]
    public async Task LoadFoundationDatabase_Decimal_ResolvesToObjCBridgeableStruct()
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Decimal");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("Foundation", record!.CSharpTypeName.Namespace);
        Assert.Equal("NSDecimalNumber", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
        Assert.False(record.Flags.HasFlag(TypeRecordFlags.Frozen));
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement));
        Assert.False(record.Flags.HasFlag(TypeRecordFlags.ObjCBridged));
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.ObjCBridgeable));
        Assert.Equal("Foundation.NSDecimalNumber", record.NativeTypeName!.FullyQualifiedName);
    }

    // --- Metatype types map to AnyType across all resolution entry points ---

    [Fact]
    public void GetTypeRecordOrAnyType_MetatypeType_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();
        // Foundation.Decimal.Type → Foundation(InnerType: Decimal(InnerType: Type))
        var metatypeSpec = new NamedTypeSpec("Foundation")
        {
            InnerType = new NamedTypeSpec("Decimal") { InnerType = new NamedTypeSpec("Type") }
        };

        var record = typeDatabase.GetTypeRecordOrAnyType(metatypeSpec);

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void GetTypeRecordOrThrow_MetatypeType_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();
        var metatypeSpec = new NamedTypeSpec("Foundation")
        {
            InnerType = new NamedTypeSpec("Decimal") { InnerType = new NamedTypeSpec("Type") }
        };

        var record = typeDatabase.GetTypeRecordOrThrow(metatypeSpec);

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void TryGetTypeRecord_MetatypeType_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();
        var metatypeSpec = new NamedTypeSpec("Foundation")
        {
            InnerType = new NamedTypeSpec("Decimal") { InnerType = new NamedTypeSpec("Type") }
        };

        var found = typeDatabase.TryGetTypeRecord(metatypeSpec, out var record);

        Assert.True(found);
        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_MetatypeType_ReturnsFalse()
    {
        var typeDatabase = new TypeDatabase();
        var metatypeSpec = new NamedTypeSpec("Foundation")
        {
            InnerType = new NamedTypeSpec("Decimal") { InnerType = new NamedTypeSpec("Type") }
        };

        var isFallback = typeDatabase.TryGetAnyTypeFallbackInfo(metatypeSpec, out var fallbackInfo);

        Assert.False(isFallback);
        Assert.Null(fallbackInfo);
    }

    // --- ComparisonResult resolves as simple enum ---

    [Fact]
    public async Task LoadFoundationDatabase_ComparisonResult_ResolvesToSimpleEnum()
    {
        var typeDatabase = await CreateDbWithXmlAsync("FoundationDatabase.xml");

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.ComparisonResult");
        Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("Foundation", record!.CSharpTypeName.Namespace);
        Assert.Equal("NSComparisonResult", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Enum, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
        Assert.Equal("Int", record.RawValueTypeName);
    }

    #endregion
}
