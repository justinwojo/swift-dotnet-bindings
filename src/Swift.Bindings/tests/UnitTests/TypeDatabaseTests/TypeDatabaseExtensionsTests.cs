// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

public class TypeDatabaseExtensionsTests
{
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
    [InlineData("Foundation.PersonNameComponents")]
    [InlineData("Foundation.CharacterSet")]
    [InlineData("Foundation.Decimal")]
    [InlineData("Foundation.NSRange")]
    [InlineData("Foundation.Date")]
    [InlineData("Foundation.DateComponents")]
    [InlineData("Foundation.IndexSet")]
    [InlineData("Foundation.ComparisonResult")]
    [InlineData("Foundation.IndexPath")]
    [InlineData("Foundation.JSONEncoder")]
    [InlineData("Foundation.JSONDecoder")]
    [InlineData("Foundation.NSNotification.Name")]
    [InlineData("Foundation.objc_AssociationPolicy")]
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

    // --- Foundation remapped value types (Session 10 additions) ---

    [Theory]
    [InlineData("Foundation.URLSession.ResponseDisposition", "Foundation", "NSUrlSessionResponseDisposition")]
    [InlineData("Foundation.URLSession.AuthChallengeDisposition", "Foundation", "NSUrlSessionAuthChallengeDisposition")]
    [InlineData("Foundation.RunLoop.Mode", "Foundation", "NSRunLoopMode")]
    [InlineData("Foundation.FileAttributeKey", "Foundation", "NSString")]
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
    public void GetTypeRecordOrAnyType_FoundationRemappedValueType_ReturnsCorrectName(string swiftType, string expectedNamespace, string expectedName)
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal($"{expectedNamespace}.{expectedName}", record.CSharpTypeName.FullyQualifiedName);
        Assert.Equal(TypeRecordFlags.Frozen, record.Flags);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
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
    public void GetTypeRecordOrAnyType_NSRegularExpressionOptions_ReturnsRemappedRecord()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation.NSRegularExpression.Options"));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal("Foundation.NSRegularExpressionOptions", record.CSharpTypeName.FullyQualifiedName);
        Assert.Equal(TypeRecordFlags.Frozen, record.Flags);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
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
    public void IsObjCModuleType_NamespaceOverrideModules_ReturnsTrue(string swiftType)
    {
        var result = TypeDatabaseExtensions.IsObjCModuleType(new NamedTypeSpec(swiftType));

        Assert.True(result);
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
    public void GetTypeRecordOrAnyType_RemappedValueType_ReturnsCorrectDotNetType(string swiftType, string expectedNamespace, string expectedName)
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(swiftType));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal($"{expectedNamespace}.{expectedName}", record.CSharpTypeName.FullyQualifiedName);
        Assert.Equal(TypeRecordFlags.Frozen, record.Flags);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
        // Must NOT have ObjCBridged flag (these are value types, not classes)
        Assert.False((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
    }

    [Theory]
    [InlineData("Foundation._NSRange", "Foundation", "NSRange")]
    [InlineData("Foundation.JSONSerialization.ReadingOptions", "Foundation", "NSJsonReadingOptions")]
    [InlineData("Foundation.JSONSerialization.WritingOptions", "Foundation", "NSJsonWritingOptions")]
    public void TryGetTypeRecord_RemappedValueType_ReturnsCorrectDotNetType(string swiftType, string expectedNamespace, string expectedName)
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(swiftType), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal($"{expectedNamespace}.{expectedName}", record.CSharpTypeName.FullyQualifiedName);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
    }

    [Theory]
    [InlineData("Foundation._NSRange")]
    [InlineData("Foundation.JSONSerialization.ReadingOptions")]
    [InlineData("Foundation.JSONSerialization.WritingOptions")]
    public void GetTypeRecordOrThrow_RemappedValueType_ReturnsCorrectDotNetType(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        // Should NOT throw — these types have remapped records
        var record = typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec(swiftType));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
    }

    [Theory]
    [InlineData("Foundation._NSRange")]
    [InlineData("Foundation.JSONSerialization.ReadingOptions")]
    [InlineData("Foundation.JSONSerialization.WritingOptions")]
    public void IsTypeProcessed_RemappedValueType_ReturnsTrue(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        var result = typeDatabase.IsTypeProcessed(new NamedTypeSpec(swiftType));

        Assert.True(result);
    }

    [Theory]
    [InlineData("Foundation._NSRange")]
    [InlineData("Foundation.JSONSerialization.ReadingOptions")]
    [InlineData("Foundation.JSONSerialization.WritingOptions")]
    public void TryGetAnyTypeFallbackInfo_RemappedValueType_ReturnsFalse(string swiftType)
    {
        var typeDatabase = new TypeDatabase();

        // Remapped types should not report as missing
        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec(swiftType), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    [Fact]
    public void GetTypeRecordOrAnyType_NSRange_ReturnsRemappedNotObjCBridged()
    {
        var typeDatabase = new TypeDatabase();

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
    public void GetTypeRecordOrAnyType_FoundationRemappedValueType_ReturnsRemappedRecord()
    {
        var typeDatabase = new TypeDatabase();

        // CloseCode is an enum in .NET iOS (NSUrlSessionWebSocketCloseCode), not a class
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation.URLSessionWebSocketTask.CloseCode"));

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal("Foundation.NSUrlSessionWebSocketCloseCode", record.CSharpTypeName.FullyQualifiedName);
        Assert.Equal(TypeRecordFlags.Frozen, record.Flags);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
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
    public void TryGetTypeRecord_UIViewContentMode_ReturnsSimpleEnumRecord()
    {
        var typeDatabase = new TypeDatabase();

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
    public void TryGetTypeRecord_UIControlState_ReturnsUInt64RawValueType()
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetTypeRecord(
            new NamedTypeSpec("UIKit.UIControl.State"), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal(TypeRecordKind.Enum, record.Kind);
        Assert.Equal("UInt64", record.RawValueTypeName);
    }

    [Fact]
    public void TryGetTypeRecord_FlatUIKitEnum_PreservesName()
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetTypeRecord(
            new NamedTypeSpec("UIKit.UIBarStyle"), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal("UIKit.UIBarStyle", record.CSharpTypeName.FullyQualifiedName);
    }

    [Fact]
    public void GetTypeRecordOrThrow_UIViewContentMode_ReturnsEnumRecord()
    {
        var typeDatabase = new TypeDatabase();
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView.ContentMode");

        var record = typeDatabase.GetTypeRecordOrThrow(swiftTypeName);

        Assert.Equal(TypeRecordKind.Enum, record.Kind);
        Assert.Equal("UIKit.UIViewContentMode", record.CSharpTypeName.FullyQualifiedName);
    }

    [Fact]
    public void GetTypeRecordOrAnyType_UIViewContentMode_ReturnsEnumRecord()
    {
        var typeDatabase = new TypeDatabase();
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView.ContentMode");

        var record = typeDatabase.GetTypeRecordOrAnyType(swiftTypeName);

        Assert.NotEqual(TypeDatabaseExtensions.AnyType, record);
        Assert.Equal(TypeRecordKind.Enum, record.Kind);
    }

    [Fact]
    public void TryGetTypeRecord_UIViewContentMode_PInvokeUnderlyingType_IsLong()
    {
        var typeDatabase = new TypeDatabase();

        typeDatabase.TryGetTypeRecord(
            new NamedTypeSpec("UIKit.UIView.ContentMode"), out var record);

        Assert.NotNull(record);
        Assert.Equal("Int64", record!.RawValueTypeName);
        // Int64 maps to C# "long" in P/Invoke
        Assert.Equal("long", EnumHandler.GetCSharpEnumUnderlyingType(record.RawValueTypeName!));
    }

    [Fact]
    public void CoreTryGetTypeRecord_UIViewContentMode_ReturnsEnumRecord()
    {
        // Test via TypeDatabase directly (not extension), verifying ClosureHandler path works
        var typeDatabase = new TypeDatabase();
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView.ContentMode");

        var found = typeDatabase.TryGetTypeRecord(swiftTypeName, out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal(TypeRecordKind.Enum, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
    }

    [Fact]
    public void IsTypeProcessed_UIViewContentMode_NamedTypeSpec_ReturnsTrue()
    {
        var typeDatabase = new TypeDatabase();

        var processed = typeDatabase.IsTypeProcessed(
            new NamedTypeSpec("UIKit.UIView.ContentMode"));

        Assert.True(processed);
    }

    [Fact]
    public void IsTypeProcessed_UIViewContentMode_SwiftTypeName_ReturnsTrue()
    {
        // Exercises TypeDatabase.IsTypeProcessed(SwiftTypeName) directly,
        // ensuring the core path resolves remapped Apple enums independently
        // of the extension-level IsRemappedAppleEnum(NamedTypeSpec) check.
        var typeDatabase = new TypeDatabase();
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIView.ContentMode");

        var processed = typeDatabase.IsTypeProcessed(swiftTypeName);

        Assert.True(processed);
    }

    #endregion
}
