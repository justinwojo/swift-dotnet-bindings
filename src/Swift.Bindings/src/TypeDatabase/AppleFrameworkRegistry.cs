// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Centralized registry of Apple framework type knowledge.
/// Two separate concerns:
/// 1. Module-level classification (auto-bridge, optional fallback, unsupported)
/// 2. Type-level knowledge (value types, name remapping, ObjC prefix detection)
/// </summary>
internal static class AppleFrameworkRegistry
{
    // --- Module Sets ---

    private static readonly HashSet<string> AutoBridgeModules = new(StringComparer.Ordinal)
    {
        "Foundation",
        "UIKit", "AppKit", "CoreImage", "CoreData",
        "WebKit", "SceneKit", "SpriteKit", "ARKit", "RealityKit",
        "AVFoundation", "Photos", "PhotosUI", "Contacts", "ContactsUI",
        "EventKit", "EventKitUI", "HealthKit", "HomeKit", "CloudKit",
        "StoreKit", "PDFKit", "SafariServices",
        "AuthenticationServices", "CoreBluetooth", "CoreSpotlight",
        "CoreML", "Vision", "NaturalLanguage", "SoundAnalysis", "Speech",
        "MultipeerConnectivity", "UserNotifications", "NetworkExtension",
        "Intents", "IntentsUI",
        "QuartzCore",
        "AVFAudio",
        "CoreLocation", "MapKit",
    };

    private static readonly HashSet<string> OptionalFallbackModules = new(StringComparer.Ordinal)
    {
        "CoreAnimation", "CoreBluetooth", "CoreLocation", "CoreMedia", "CoreML",
        "CoreMotion", "MapKit", "Metal", "MetalKit", "PassKit", "PhotosUI",
        "QuartzCore", "SceneKit", "SpriteKit", "StoreKit", "WebKit",
        "ARKit", "RealityKit", "GameKit", "HealthKit", "HomeKit",
        "AuthenticationServices", "LocalAuthentication", "NaturalLanguage",
        "NetworkExtension", "UserNotifications", "Vision", "Intents",
        "EventKit", "Contacts", "MediaPlayer", "MultipeerConnectivity",
        "CoreNFC", "CarPlay", "ClassKit", "CloudKit", "CoreData",
        "CoreImage", "CoreSpotlight", "CoreTelephony", "FileProvider",
        "MessageUI", "SafariServices", "Social", "WatchConnectivity",
        "UIKit", "Foundation",
        // AutoBridge modules that were missing from the broader fallback set
        "AppKit", "AVFoundation", "AVFAudio", "Photos", "SoundAnalysis", "Speech",
        "PDFKit", "ContactsUI", "EventKitUI", "IntentsUI",
        "GameController", "Network",
    };

    private static readonly HashSet<string> UnsupportedModules = new(StringComparer.Ordinal)
    {
        "SwiftUI",
        "XCTest",
        "Combine",
        "_Concurrency",
        "Observation",
        "WidgetKit",
        "AppIntents",
        "Charts",
        "TipKit",
    };

    // --- Module Namespace Remaps ---

    private static readonly Dictionary<string, string> ModuleNamespaceRemaps = new(StringComparer.Ordinal)
    {
        { "ObjectiveC", "Foundation" },
        { "QuartzCore", "CoreAnimation" },
        { "Dispatch", "CoreFoundation" },
        { "AVFAudio", "AVFoundation" },
    };

    // --- Type Name Remaps (string→string) ---
    // Merged from AppleFrameworkClassRemappings, AppleFrameworkTypeRemappings,
    // AppleFrameworkSimpleEnumRemappings, and SwiftToNetTypeRemappings

    private static readonly Dictionary<string, string> TypeNameRemaps = new(StringComparer.Ordinal)
    {
        // === AppleFrameworkClassRemappings ===

        // Foundation: Swift drops NS prefix — common classes
        ["Foundation.Bundle"] = "Foundation.NSBundle",
        ["Foundation.NotificationCenter"] = "Foundation.NSNotificationCenter",
        ["Foundation.UserDefaults"] = "Foundation.NSUserDefaults",
        ["Foundation.Timer"] = "Foundation.NSTimer",
        ["Foundation.RunLoop"] = "Foundation.NSRunLoop",
        ["Foundation.Operation"] = "Foundation.NSOperation",
        ["Foundation.OperationQueue"] = "Foundation.NSOperationQueue",
        ["Foundation.BlockOperation"] = "Foundation.NSBlockOperation",
        ["Foundation.ProcessInfo"] = "Foundation.NSProcessInfo",
        ["Foundation.Thread"] = "Foundation.NSThread",
        ["Foundation.FileManager"] = "Foundation.NSFileManager",
        ["Foundation.FileHandle"] = "Foundation.NSFileHandle",
        ["Foundation.UndoManager"] = "Foundation.NSUndoManager",
        ["Foundation.Progress"] = "Foundation.NSProgress",
        ["Foundation.Scanner"] = "Foundation.NSScanner",
        ["Foundation.Formatter"] = "Foundation.NSFormatter",
        ["Foundation.NumberFormatter"] = "Foundation.NSNumberFormatter",
        ["Foundation.DateFormatter"] = "Foundation.NSDateFormatter",
        ["Foundation.InputStream"] = "Foundation.NSInputStream",
        ["Foundation.OutputStream"] = "Foundation.NSOutputStream",
        ["Foundation.Stream"] = "Foundation.NSStream",

        // Foundation: URL/HTTP/JSON acronym casing
        ["Foundation.URLSession"] = "Foundation.NSUrlSession",
        ["Foundation.URLSessionTask"] = "Foundation.NSUrlSessionTask",
        ["Foundation.URLSessionDataTask"] = "Foundation.NSUrlSessionDataTask",
        ["Foundation.URLSessionDownloadTask"] = "Foundation.NSUrlSessionDownloadTask",
        ["Foundation.URLSessionUploadTask"] = "Foundation.NSUrlSessionUploadTask",
        ["Foundation.URLSessionStreamTask"] = "Foundation.NSUrlSessionStreamTask",
        ["Foundation.URLSessionWebSocketTask"] = "Foundation.NSUrlSessionWebSocketTask",
        ["Foundation.URLSessionConfiguration"] = "Foundation.NSUrlSessionConfiguration",
        ["Foundation.URLSessionTaskMetrics"] = "Foundation.NSUrlSessionTaskMetrics",
        ["Foundation.URLSessionTaskTransactionMetrics"] = "Foundation.NSUrlSessionTaskTransactionMetrics",
        ["Foundation.URLResponse"] = "Foundation.NSUrlResponse",
        ["Foundation.HTTPURLResponse"] = "Foundation.NSHttpUrlResponse",
        ["Foundation.CachedURLResponse"] = "Foundation.NSCachedUrlResponse",
        ["Foundation.URLAuthenticationChallenge"] = "Foundation.NSUrlAuthenticationChallenge",
        ["Foundation.URLCredential"] = "Foundation.NSUrlCredential",
        ["Foundation.URLCredentialStorage"] = "Foundation.NSUrlCredentialStorage",
        ["Foundation.URLProtectionSpace"] = "Foundation.NSUrlProtectionSpace",
        ["Foundation.URLCache"] = "Foundation.NSUrlCache",
        ["Foundation.URLProtocol"] = "Foundation.NSUrlProtocol",
        ["Foundation.URLConnection"] = "Foundation.NSUrlConnection",
        ["Foundation.URLSessionWebSocketTask.Message"] = "Foundation.NSUrlSessionWebSocketMessage",
        ["Foundation.HTTPCookie"] = "Foundation.NSHttpCookie",
        ["Foundation.HTTPCookieStorage"] = "Foundation.NSHttpCookieStorage",
        ["Foundation.JSONSerialization"] = "Foundation.NSJsonSerialization",

        // Foundation: ObjC names with casing differences in .NET
        ["Foundation.NSURL"] = "Foundation.NSUrl",
        ["Foundation.NSUUID"] = "Foundation.NSUuid",

        // AVFoundation: acronym casing
        ["AVFoundation.AVURLAsset"] = "AVFoundation.AVUrlAsset",
        ["AVFoundation.AVMIDIPlayer"] = "AVFoundation.AVMidiPlayer",

        // QuartzCore NSString typedefs
        ["QuartzCore.CALayerContentsGravity"] = "Foundation.NSString",
        ["QuartzCore.CAMediaTimingFunctionName"] = "Foundation.NSString",
        ["QuartzCore.CATransitionType"] = "Foundation.NSString",
        ["QuartzCore.CATransitionSubtype"] = "Foundation.NSString",

        // QuartzCore: casing difference
        ["QuartzCore.CAKeyframeAnimation"] = "CoreAnimation.CAKeyFrameAnimation",

        // Foundation typedefs not bound as distinct types in .NET iOS
        ["Foundation.NSAttributedString.Key"] = "Foundation.NSString",

        // === AppleFrameworkTypeRemappings ===

        // C struct types
        ["Foundation._NSRange"] = "Foundation.NSRange",
        // ObjC NS_OPTIONS enums
        ["Foundation.JSONSerialization.ReadingOptions"] = "Foundation.NSJsonReadingOptions",
        ["Foundation.JSONSerialization.WritingOptions"] = "Foundation.NSJsonWritingOptions",
        // Foundation nested enums
        ["Foundation.URLSessionWebSocketTask.CloseCode"] = "Foundation.NSUrlSessionWebSocketCloseCode",
        ["Foundation.Stream.Event"] = "Foundation.NSStreamEvent",
        ["Foundation.Operation.QueuePriority"] = "Foundation.NSOperationQueuePriority",
        ["Foundation.URLCredential.Persistence"] = "Foundation.NSUrlCredentialPersistence",
        ["Foundation.URLSession.ResponseDisposition"] = "Foundation.NSUrlSessionResponseDisposition",
        ["Foundation.URLSession.AuthChallengeDisposition"] = "Foundation.NSUrlSessionAuthChallengeDisposition",
        ["Foundation.RunLoop.Mode"] = "Foundation.NSRunLoopMode",
        ["Foundation.FileAttributeKey"] = "Foundation.NSString",
        ["Foundation.NSRegularExpression.Options"] = "Foundation.NSRegularExpressionOptions",
        ["Foundation.NSData.WritingOptions"] = "Foundation.NSDataWritingOptions",
        // UIKit nested enums
        ["UIKit.UIImage.RenderingMode"] = "UIKit.UIImageRenderingMode",
        ["UIKit.UIView.AnimationOptions"] = "UIKit.UIViewAnimationOptions",
        // Photos enum
        ["Photos.PHImageContentMode"] = "Photos.PHImageContentMode",
        // QuartzCore value types
        ["QuartzCore.CATransform3D"] = "CoreAnimation.CATransform3D",
        ["QuartzCore.CACornerMask"] = "CoreAnimation.CACornerMask",
        ["QuartzCore.CAEdgeAntialiasingMask"] = "CoreAnimation.CAEdgeAntialiasingMask",
        ["QuartzCore.CAAutoresizingMask"] = "CoreAnimation.CAAutoresizingMask",
        ["QuartzCore.CAContentsFormat"] = "CoreAnimation.CAContentsFormat",
        ["QuartzCore.CACornerCurve"] = "CoreAnimation.CACornerCurve",
        ["QuartzCore.CAGradientLayerType"] = "CoreAnimation.CAGradientLayerType",
        ["QuartzCore.CATextLayerAlignmentMode"] = "CoreAnimation.CATextLayerAlignmentMode",
        ["QuartzCore.CATextLayerTruncationMode"] = "CoreAnimation.CATextLayerTruncationMode",
        ["QuartzCore.CAScroll"] = "CoreAnimation.CAScroll",
        ["QuartzCore.CADynamicRange"] = "CoreAnimation.CADynamicRange",
        ["QuartzCore.CAToneMapMode"] = "CoreAnimation.CAToneMapMode",
        // UIKit enum whose .NET namespace is Foundation
        ["UIKit.NSUnderlineStyle"] = "Foundation.NSUnderlineStyle",
        // UIKit nested enums/typedefs
        ["UIKit.NSLayoutConstraint.Relation"] = "UIKit.NSLayoutRelation",
        ["UIKit.NSParagraphStyle.LineBreakStrategy"] = "UIKit.NSLineBreakStrategy",
        ["UIKit.UIFont.TextStyle"] = "UIKit.UIFontTextStyle",

        // === AppleFrameworkSimpleEnumRemappings ===

        ["UIKit.UIView.ContentMode"] = "UIKit.UIViewContentMode",
        ["UIKit.UIBarStyle"] = "UIKit.UIBarStyle",
        ["UIKit.UIKeyboardAppearance"] = "UIKit.UIKeyboardAppearance",
        ["UIKit.UITextField.ViewMode"] = "UIKit.UITextFieldViewMode",
        ["UIKit.UIActivityIndicatorView.Style"] = "UIKit.UIActivityIndicatorViewStyle",
        ["UIKit.UIBlurEffect.Style"] = "UIKit.UIBlurEffectStyle",
        ["UIKit.UITableView.Style"] = "UIKit.UITableViewStyle",
        ["UIKit.UIModalPresentationStyle"] = "UIKit.UIModalPresentationStyle",
        ["UIKit.UIUserInterfaceStyle"] = "UIKit.UIUserInterfaceStyle",
        ["UIKit.UIControl.State"] = "UIKit.UIControlState",
        ["UIKit.UIControl.Event"] = "UIKit.UIControlEvent",
        ["AVFoundation.AVCaptureDevice.FocusMode"] = "AVFoundation.AVCaptureFocusMode",
    };

    // --- Value Type Exclusion Set ---

    internal static readonly HashSet<string> ValueTypes = new(StringComparer.Ordinal)
    {
        // UIKit structs
        "UIKit.UIEdgeInsets", "UIKit.UIOffset", "UIKit.UIFloatRange",
        "UIKit.NSDirectionalEdgeInsets",
        // UIKit nested enums/structs
        "UIKit.UIView.ContentMode", "UIKit.UIControl.State", "UIKit.UIControl.Event",
        "UIKit.UIAccessibilityTraits",
        // UIKit enums
        "UIKit.UIBarStyle", "UIKit.UIKeyboardAppearance", "UIKit.UITextField.ViewMode",
        "UIKit.UIControl.ContentVerticalAlignment", "UIKit.UIActivityIndicatorView.Style",
        "UIKit.UIBlurEffect.Style", "UIKit.UILayoutPriority",
        "UIKit.NSTextAlignment",
        "UIKit.NSWritingDirection",
        "UIKit.UIKeyboardType",
        "UIKit.UITextLayoutDirection",
        "UIKit.UIUserInterfaceLayoutDirection",
        // AVFoundation structs
        "AVFoundation.AVAudioFramePosition", "AVFoundation.AVAudioFrameCount",
        "AVFoundation.AVAudioPacketCount", "AVFoundation.AVAudioChannelCount",
        "AVFoundation.AVCaptureVideoOrientation",
        "AVFoundation.AVCaptureSession.Preset",
        "AVFoundation.AVCaptureDevice.AutoFocusRangeRestriction",
        "AVFoundation.AVCaptureDevice.DeviceType",
        "AVFoundation.AVCaptureDevice.FocusMode",
        // CoreData structs
        "CoreData.NSFetchRequestResultType",
        // SceneKit structs
        "SceneKit.SCNVector3", "SceneKit.SCNVector4", "SceneKit.SCNMatrix4",
        // MapKit structs
        "MapKit.MKCoordinateRegion", "MapKit.MKCoordinateSpan",
        "MapKit.MKMapRect", "MapKit.MKMapPoint", "MapKit.MKMapSize",
        // ARKit structs
        "ARKit.ARRaycastQuery",
        // Foundation value types
        "Foundation.Data", "Foundation.URL", "Foundation.UUID", "Foundation.IndexPath",
        "Foundation.URLError", "Foundation.URLError.Code",
        "Foundation.URLComponents", "Foundation.URLQueryItem", "Foundation.URLRequest",
        "Foundation.DateInterval", "Foundation.Calendar", "Foundation.Locale",
        "Foundation.TimeZone", "Foundation.Notification", "Foundation.Notification.Name",
        "Foundation.Measurement", "Foundation.PersonNameComponents",
        "Foundation.CharacterSet", "Foundation.Decimal", "Foundation.NSRange",
        "Foundation.Date", "Foundation.DateComponents", "Foundation.IndexSet",
        "Foundation.Selector", "Foundation.ComparisonResult",
        // Foundation types with underscore prefix
        "Foundation._NSRange",
        // Foundation types with no .NET equivalent
        "Foundation.JSONEncoder",
        "Foundation.JSONDecoder",
        "Foundation.NSNotification.Name",
        "Foundation.objc_AssociationPolicy",
        "Foundation.XMLParser",
        // Foundation nested enums with remapped .NET names
        "Foundation.URLSessionWebSocketTask.CloseCode",
        // Foundation nested ObjC enums (NS_OPTIONS)
        "Foundation.JSONSerialization.ReadingOptions",
        "Foundation.JSONSerialization.WritingOptions",
        // Foundation stream types
        "Foundation.Stream.Event",
        // Foundation nested ObjC enum
        "Foundation.Operation.QueuePriority",
        // Foundation nested ObjC enum
        "Foundation.URLCredential.Persistence",
        // Foundation nested ObjC enums from URLSession delegate protocols
        "Foundation.URLSession.ResponseDisposition",
        "Foundation.URLSession.AuthChallengeDisposition",
        // Foundation nested struct
        "Foundation.RunLoop.Mode",
        // Foundation ObjC typedef
        "Foundation.FileAttributeKey",
        // Foundation nested ObjC NS_OPTIONS
        "Foundation.NSData.WritingOptions",
        // UIKit nested ObjC enums/options
        "UIKit.UIImage.RenderingMode",
        "UIKit.UIImage.ResizingMode",
        "UIKit.UIImage.SymbolScale",
        "UIKit.UIImage.SymbolWeight",
        "UIKit.UIView.AnimationOptions",
        "UIKit.UIView.AutoresizingMask",
        "UIKit.UIView.AnimationCurve",
        "UIKit.UIRectEdge",
        "UIKit.UIRectCorner",
        "UIKit.UIInterfaceOrientation",
        "UIKit.UIInterfaceOrientationMask",
        "UIKit.UIUserInterfaceIdiom",
        "UIKit.UIUserInterfaceStyle",
        "UIKit.UISemanticContentAttribute",
        "UIKit.UIControl.ContentHorizontalAlignment",
        "UIKit.NSLineBreakMode",
        "UIKit.UITextAutocapitalizationType",
        "UIKit.UITextAutocorrectionType",
        "UIKit.UITextSpellCheckingType",
        "UIKit.UIReturnKeyType",
        "UIKit.UIDataDetectorTypes",
        "UIKit.UITableView.Style",
        "UIKit.UITextField.DidEndEditingReason",
        "UIKit.UISwipeGestureRecognizer.Direction",
        "UIKit.UICollectionView.ScrollDirection",
        "UIKit.UICollectionView.ScrollPosition",
        "UIKit.UITableViewCell.CellStyle",
        "UIKit.UITableViewCell.SelectionStyle",
        "UIKit.UITableViewCell.AccessoryType",
        "UIKit.UITableViewCell.EditingStyle",
        "UIKit.UITableView.RowAnimation",
        "UIKit.UITableView.ScrollPosition",
        "UIKit.UIScrollView.IndicatorStyle",
        "UIKit.UIScrollView.KeyboardDismissMode",
        "UIKit.UIScrollView.ContentInsetAdjustmentBehavior",
        "UIKit.UIStackView.Alignment",
        "UIKit.UIStackView.Distribution",
        "UIKit.UINavigationController.Operation",
        "UIKit.UINavigationItem.LargeTitleDisplayMode",
        "UIKit.UIPageViewController.NavigationDirection",
        "UIKit.UIPageViewController.NavigationOrientation",
        "UIKit.UIPageViewController.SpineLocation",
        "UIKit.UIPageViewController.TransitionStyle",
        "UIKit.UIGestureRecognizer.State",
        "UIKit.UIModalPresentationStyle",
        "UIKit.UIModalTransitionStyle",
        "UIKit.UIStatusBarStyle",
        "UIKit.UIStatusBarAnimation",
        "UIKit.UIBarPosition",
        "UIKit.UITabBarItem.SystemItem",
        "UIKit.UIBarButtonItem.SystemItem",
        "UIKit.UIBarButtonItem.Style",
        "UIKit.UIDatePicker.Mode",
        "UIKit.UIAlertController.Style",
        "UIKit.UIAlertAction.Style",
        // AVFoundation enums
        "AVFoundation.AVMediaType",
        "AVFoundation.AVFileType",
        "AVFoundation.AVLayerVideoGravity",
        "AVFoundation.AVCaptureDevice.Position",
        "AVFoundation.AVCaptureDevice.FlashMode",
        "AVFoundation.AVCaptureDevice.TorchMode",
        "AVFoundation.AVPlayer.TimeControlStatus",
        "AVFoundation.AVPlayer.Status",
        "AVFoundation.AVPlayerItem.Status",
        // StoreKit enums
        "StoreKit.SKPaymentTransactionState",
        "StoreKit.SKError.Code",
        "StoreKit.SKProduct.PeriodUnit",
        "StoreKit.SKProductDiscount.PaymentMode",
        // CoreBluetooth enums
        "CoreBluetooth.CBManagerState",
        "CoreBluetooth.CBManagerAuthorization",
        "CoreBluetooth.CBPeripheralState",
        "CoreBluetooth.CBCharacteristicProperties",
        "CoreBluetooth.CBAttributePermissions",
        "CoreBluetooth.CBCharacteristicWriteType",
        // Photos ObjC enum
        "Photos.PHImageContentMode",
        // Foundation NS_OPTIONS
        "Foundation.NSRegularExpression.Options",
        // QuartzCore structs/enums
        "QuartzCore.CATransform3D",
        "QuartzCore.CACornerMask",
        "QuartzCore.CAEdgeAntialiasingMask",
        "QuartzCore.CAAutoresizingMask",
        "QuartzCore.CAContentsFormat",
        "QuartzCore.CACornerCurve",
        "QuartzCore.CAGradientLayerType",
        "QuartzCore.CATextLayerAlignmentMode",
        "QuartzCore.CATextLayerTruncationMode",
        "QuartzCore.CAScroll",
        "QuartzCore.CADynamicRange",
        "QuartzCore.CAToneMapMode",
        // UIKit enums whose .NET namespace differs
        "UIKit.NSUnderlineStyle",
        // UIKit ObjC typedefs
        "UIKit.UIFont.TextStyle",
        "UIKit.UIContentSizeCategory",
        // UIKit nested ObjC enums
        "UIKit.NSLayoutConstraint.Relation",
        "UIKit.NSParagraphStyle.LineBreakStrategy",
        // Foundation nested ObjC typedefs
        "Foundation.NSAttributedString.Key",
        // CoreLocation value types
        "CoreLocation.CLLocationCoordinate2D",
        "CoreLocation.CLAuthorizationStatus",
        "CoreLocation.CLAccuracyAuthorization",
        "CoreLocation.CLLocationDirection",
        "CoreLocation.CLLocationDistance",
        "CoreLocation.CLLocationDegrees",
        // MapKit value types
        "MapKit.MKDirectionsTransportType",
        // NaturalLanguage value types
        "NaturalLanguage.NLLanguage",
        "NaturalLanguage.NLTag",
        "NaturalLanguage.NLTagScheme",
        "NaturalLanguage.NLTokenUnit",
        // CoreML value types
        "CoreML.MLComputeUnits",
        "CoreML.MLFeatureType",
        "CoreML.MLMultiArrayDataType",
        // Photos value types
        "Photos.PHAuthorizationStatus",
        "Photos.PHAccessLevel",
        "Photos.PHAssetMediaType",
        "Photos.PHAssetMediaSubtype",
        "Photos.PHAssetCollectionType",
        "Photos.PHAssetCollectionSubtype",
        "Photos.PHAssetResourceType",
        // Metal value types
        "Metal.MTLSize",
        "Metal.MTLOrigin",
        "Metal.MTLRegion",
        "Metal.MTLClearColor",
        "Metal.MTLViewport",
        "Metal.MTLScissorRect",
        "Metal.MTLPixelFormat",
        "Metal.MTLPrimitiveType",
        "Metal.MTLStorageMode",
        "Metal.MTLResourceOptions",
        // Network value types
        "Network.NWEndpoint.Port",
    };

    // --- ObjC Prefix Detection ---

    private static readonly string[] ObjCPrefixes = new[]
    {
        "UI", "NS", "MK", "CL", "CB", "CK", "CN", "EK", "GK", "HK", "HM",
        "MF", "MC", "MP", "MT", "MTK", "PK", "SC", "SK", "WK", "VN",
        "AR", "AS", "LA", "NE", "UN", "IN", "CA", "CI", "SF", "SL",
        "NK", "CP", "FP", "RE",
        "AV", "PH", "NW", "ML", "GC", "NL", "SN",
    };

    // --- Public API ---

    /// <summary>Narrower set used by IsObjCModuleType to gate auto-bridging.</summary>
    public static bool IsAutoBridgeModule(string moduleName) => AutoBridgeModules.Contains(moduleName);

    /// <summary>Broader set used by Optional/Array element fallback.</summary>
    public static bool IsOptionalFallbackModule(string moduleName) => OptionalFallbackModules.Contains(moduleName);

    public static bool IsUnsupportedModule(string moduleName) => UnsupportedModules.Contains(moduleName);

    public static bool IsKnownValueType(string moduleQualifiedName) => ValueTypes.Contains(moduleQualifiedName);

    /// <summary>Module-level only remapping (ObjectiveC→Foundation, QuartzCore→CoreAnimation, etc.)</summary>
    public static string MapModuleToNetNamespace(string swiftModule)
    {
        if (string.IsNullOrEmpty(swiftModule)) return swiftModule;
        return ModuleNamespaceRemaps.TryGetValue(swiftModule, out var mapped) ? mapped : swiftModule;
    }

    /// <summary>
    /// Replaces all known Swift module name prefixes with their .NET namespace equivalents in a string.
    /// </summary>
    public static string MapModulesInString(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (swiftModule, netNamespace) in ModuleNamespaceRemaps)
        {
            var swiftPrefix = $"{swiftModule}.";
            if (text.Contains(swiftPrefix, StringComparison.Ordinal))
                text = text.Replace(swiftPrefix, $"{netNamespace}.", StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>
    /// Full type name remapping for string-only callers.
    /// Checks explicit type remappings.
    /// </summary>
    public static bool TryGetNetTypeName(string moduleQualifiedSwiftName, out string netName)
    {
        if (TypeNameRemaps.TryGetValue(moduleQualifiedSwiftName, out netName!))
            return true;
        netName = default!;
        return false;
    }

    /// <summary>
    /// Returns true if the type name portion of a module-qualified name starts with
    /// a known ObjC class prefix followed by an uppercase letter.
    /// </summary>
    public static bool HasObjCClassPrefix(string moduleQualifiedName)
    {
        var dotIndex = moduleQualifiedName.IndexOf('.');
        if (dotIndex < 0 || dotIndex >= moduleQualifiedName.Length - 1)
            return false;

        var typeName = moduleQualifiedName.AsSpan(dotIndex + 1);

        foreach (var prefix in ObjCPrefixes)
        {
            if (typeName.Length > prefix.Length &&
                typeName.StartsWith(prefix.AsSpan(), StringComparison.Ordinal) &&
                char.IsUpper(typeName[prefix.Length]))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsPointerType(string name) =>
        name is "Swift.OpaquePointer" or "Swift.UnsafePointer"
            or "Swift.UnsafeMutablePointer" or "Swift.UnsafeRawPointer"
            or "Swift.UnsafeMutableRawPointer" or "Builtin.RawPointer";

    /// <summary>
    /// Returns true if the module-qualified name represents a nested type
    /// (e.g., "Foundation.NSAttributedString.Key" has two dots).
    /// </summary>
    public static bool IsNestedType(string moduleQualifiedName)
    {
        var firstDot = moduleQualifiedName.IndexOf('.');
        if (firstDot < 0) return false;
        return moduleQualifiedName.IndexOf('.', firstDot + 1) >= 0;
    }

    public static bool IsKnownObjCRootClass(string name) => name is "NSObject" or "NSProxy";

    public static bool IsKnownModuleForElements(string moduleName) =>
        moduleName == "UIKit" || moduleName == "Foundation";
}
