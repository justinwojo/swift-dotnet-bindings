// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

public class AppleFrameworkRegistryTests
{
    // --- IsAutoBridgeModule ---

    [Theory]
    [InlineData("Foundation", true)]
    [InlineData("UIKit", true)]
    [InlineData("AppKit", true)]
    [InlineData("AVFoundation", true)]
    [InlineData("QuartzCore", true)]
    [InlineData("AVFAudio", true)]
    [InlineData("CoreImage", true)]
    [InlineData("CoreData", true)]
    [InlineData("WebKit", true)]
    [InlineData("Photos", true)]
    [InlineData("CoreLocation", true)]
    [InlineData("MapKit", true)]
    [InlineData("CoreAnimation", false)]  // NOT in auto-bridge (it's a .NET namespace, not a Swift module)
    [InlineData("Metal", false)]          // NOT in auto-bridge
    [InlineData("GameController", false)] // OptionalFallback only — auto-bridge too risky without full ValueTypes coverage
    [InlineData("Network", false)]        // OptionalFallback only — too many nested value types
    [InlineData("SwiftUI", false)]        // Unsupported, not auto-bridge
    [InlineData("MyCustomLib", false)]
    [InlineData("", false)]
    public void IsAutoBridgeModule_ReturnsExpected(string module, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.IsAutoBridgeModule(module));
    }

    // --- IsOptionalFallbackModule ---

    [Theory]
    [InlineData("Foundation", true)]
    [InlineData("UIKit", true)]
    [InlineData("CoreAnimation", true)]   // In optional fallback but NOT auto-bridge
    [InlineData("CoreLocation", true)]
    [InlineData("MapKit", true)]
    [InlineData("Metal", true)]
    [InlineData("QuartzCore", true)]
    [InlineData("AVFoundation", true)]
    [InlineData("Photos", true)]
    [InlineData("GameController", true)]
    [InlineData("Network", true)]
    [InlineData("SwiftUI", false)]        // Unsupported
    [InlineData("MyCustomLib", false)]
    [InlineData("", false)]
    public void IsOptionalFallbackModule_ReturnsExpected(string module, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.IsOptionalFallbackModule(module));
    }

    [Fact]
    public void OptionalFallbackModules_IsSupersetOfAutoBridge()
    {
        // Every auto-bridge module must also be in the broader optional fallback set.
        var autoBridgeModules = new[]
        {
            "Foundation", "UIKit", "AppKit", "CoreImage", "CoreData",
            "WebKit", "SceneKit", "SpriteKit", "ARKit", "RealityKit",
            "AVFoundation", "Photos", "PhotosUI", "Contacts", "ContactsUI",
            "EventKit", "EventKitUI", "HealthKit", "HomeKit", "CloudKit",
            "StoreKit", "PDFKit", "SafariServices",
            "AuthenticationServices", "CoreBluetooth", "CoreSpotlight",
            "CoreML", "Vision", "NaturalLanguage", "SoundAnalysis", "Speech",
            "MultipeerConnectivity", "UserNotifications", "NetworkExtension",
            "Intents", "IntentsUI", "QuartzCore", "AVFAudio",
            "CoreLocation", "MapKit",
        };

        foreach (var module in autoBridgeModules)
        {
            Assert.True(AppleFrameworkRegistry.IsOptionalFallbackModule(module),
                $"AutoBridge module '{module}' must also be in OptionalFallbackModules");
        }
    }

    // --- IsUnsupportedModule ---

    [Theory]
    [InlineData("SwiftUI", true)]
    [InlineData("XCTest", true)]
    [InlineData("Combine", true)]
    [InlineData("_Concurrency", true)]
    [InlineData("Observation", true)]
    [InlineData("WidgetKit", true)]
    [InlineData("AppIntents", true)]
    [InlineData("Charts", true)]
    [InlineData("TipKit", true)]
    [InlineData("Foundation", false)]
    [InlineData("UIKit", false)]
    [InlineData("", false)]
    public void IsUnsupportedModule_ReturnsExpected(string module, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.IsUnsupportedModule(module));
    }

    // --- IsKnownValueType ---

    [Theory]
    [InlineData("UIKit.UIEdgeInsets", true)]
    [InlineData("Foundation.Data", true)]
    [InlineData("Foundation.URL", true)]
    [InlineData("QuartzCore.CATransform3D", true)]
    [InlineData("QuartzCore.CAAutoresizingMask", true)]
    [InlineData("UIKit.NSUnderlineStyle", true)]
    [InlineData("Photos.PHImageContentMode", true)]
    [InlineData("Foundation.URLSessionWebSocketTask.CloseCode", true)]
    // Newly added value types for expanded modules
    [InlineData("CoreLocation.CLLocationCoordinate2D", true)]
    [InlineData("CoreLocation.CLAuthorizationStatus", true)]
    [InlineData("NaturalLanguage.NLLanguage", true)]
    [InlineData("CoreML.MLComputeUnits", true)]
    [InlineData("Photos.PHAuthorizationStatus", true)]
    [InlineData("Metal.MTLPixelFormat", true)]
    [InlineData("Network.NWEndpoint.Port", true)]
    [InlineData("UIKit.UIImage", false)]       // Class, not value type
    [InlineData("Foundation.NSObject", false)]  // Class
    [InlineData("Unknown.Type", false)]
    [InlineData("", false)]
    public void IsKnownValueType_ReturnsExpected(string name, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.IsKnownValueType(name));
    }

    // --- MapModuleToNetNamespace ---

    [Theory]
    [InlineData("ObjectiveC", "Foundation")]
    [InlineData("QuartzCore", "CoreAnimation")]
    [InlineData("Dispatch", "CoreFoundation")]
    [InlineData("AVFAudio", "AVFoundation")]
    [InlineData("Foundation", "Foundation")]     // No remap — pass through
    [InlineData("UIKit", "UIKit")]               // No remap — pass through
    [InlineData("MyModule", "MyModule")]          // Unknown — pass through
    [InlineData("", "")]
    public void MapModuleToNetNamespace_ReturnsExpected(string input, string expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.MapModuleToNetNamespace(input));
    }

    // --- MapModulesInString ---

    [Theory]
    [InlineData("QuartzCore.CATransform3D", "CoreAnimation.CATransform3D")]
    [InlineData("ObjectiveC.NSObject", "Foundation.NSObject")]
    [InlineData("Dispatch.DispatchQueue", "CoreFoundation.DispatchQueue")]
    [InlineData("AVFAudio.AVAudioSession", "AVFoundation.AVAudioSession")]
    [InlineData("Foundation.NSString", "Foundation.NSString")]  // No change
    [InlineData("UIKit.UIView", "UIKit.UIView")]                // No change
    [InlineData("", "")]
    public void MapModulesInString_ReturnsExpected(string input, string expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.MapModulesInString(input));
    }

    [Fact]
    public void MapModulesInString_MultipleReplacements()
    {
        var input = "QuartzCore.CALayer, ObjectiveC.NSObject";
        var result = AppleFrameworkRegistry.MapModulesInString(input);
        Assert.Contains("CoreAnimation.CALayer", result);
        Assert.Contains("Foundation.NSObject", result);
        Assert.DoesNotContain("QuartzCore.", result);
        Assert.DoesNotContain("ObjectiveC.", result);
    }

    // --- TryGetNetTypeName ---

    [Theory]
    // Foundation NS prefix drops
    [InlineData("Foundation.Bundle", "Foundation.NSBundle")]
    [InlineData("Foundation.NotificationCenter", "Foundation.NSNotificationCenter")]
    [InlineData("Foundation.UserDefaults", "Foundation.NSUserDefaults")]
    [InlineData("Foundation.Timer", "Foundation.NSTimer")]
    [InlineData("Foundation.FileManager", "Foundation.NSFileManager")]
    [InlineData("Foundation.Formatter", "Foundation.NSFormatter")]
    // Foundation URL/HTTP casing
    [InlineData("Foundation.URLSession", "Foundation.NSUrlSession")]
    [InlineData("Foundation.HTTPURLResponse", "Foundation.NSHttpUrlResponse")]
    [InlineData("Foundation.HTTPCookie", "Foundation.NSHttpCookie")]
    [InlineData("Foundation.JSONSerialization", "Foundation.NSJsonSerialization")]
    [InlineData("Foundation.NSURL", "Foundation.NSUrl")]
    [InlineData("Foundation.NSUUID", "Foundation.NSUuid")]
    // Foundation nested types
    [InlineData("Foundation.URLSessionWebSocketTask.Message", "Foundation.NSUrlSessionWebSocketMessage")]
    [InlineData("Foundation.URLSessionWebSocketTask.CloseCode", "Foundation.NSUrlSessionWebSocketCloseCode")]
    [InlineData("Foundation._NSRange", "Foundation.NSRange")]
    // QuartzCore module remaps
    [InlineData("QuartzCore.CATransform3D", "CoreAnimation.CATransform3D")]
    [InlineData("QuartzCore.CAAutoresizingMask", "CoreAnimation.CAAutoresizingMask")]
    [InlineData("QuartzCore.CAKeyframeAnimation", "CoreAnimation.CAKeyFrameAnimation")]
    // QuartzCore NSString typedefs
    [InlineData("QuartzCore.CALayerContentsGravity", "Foundation.NSString")]
    [InlineData("QuartzCore.CAMediaTimingFunctionName", "Foundation.NSString")]
    // UIKit simple enums
    [InlineData("UIKit.UIView.ContentMode", "UIKit.UIViewContentMode")]
    [InlineData("UIKit.UIControl.State", "UIKit.UIControlState")]
    [InlineData("UIKit.UIControl.Event", "UIKit.UIControlEvent")]
    // UIKit value type remaps
    [InlineData("UIKit.UIImage.RenderingMode", "UIKit.UIImageRenderingMode")]
    [InlineData("UIKit.UIView.AnimationOptions", "UIKit.UIViewAnimationOptions")]
    [InlineData("UIKit.NSUnderlineStyle", "Foundation.NSUnderlineStyle")]
    [InlineData("UIKit.NSLayoutConstraint.Relation", "UIKit.NSLayoutRelation")]
    // AVFoundation
    [InlineData("AVFoundation.AVCaptureDevice.FocusMode", "AVFoundation.AVCaptureFocusMode")]
    [InlineData("AVFoundation.AVURLAsset", "AVFoundation.AVUrlAsset")]
    // Photos
    [InlineData("Photos.PHImageContentMode", "Photos.PHImageContentMode")]
    public void TryGetNetTypeName_KnownType_ReturnsTrue(string swiftName, string expectedNetName)
    {
        Assert.True(AppleFrameworkRegistry.TryGetNetTypeName(swiftName, out var netName));
        Assert.Equal(expectedNetName, netName);
    }

    [Theory]
    [InlineData("Foundation.NSObject")]
    [InlineData("UIKit.UIView")]
    [InlineData("Unknown.SomeType")]
    [InlineData("")]
    public void TryGetNetTypeName_UnknownType_ReturnsFalse(string swiftName)
    {
        Assert.False(AppleFrameworkRegistry.TryGetNetTypeName(swiftName, out _));
    }

    // --- HasObjCClassPrefix ---

    [Theory]
    [InlineData("UIKit.UIView", true)]
    [InlineData("UIKit.UIImage", true)]
    [InlineData("Foundation.NSObject", true)]
    [InlineData("Foundation.NSString", true)]
    [InlineData("CoreAnimation.CALayer", true)]
    [InlineData("AVFoundation.AVPlayer", true)]
    [InlineData("MapKit.MKMapView", true)]
    [InlineData("CoreLocation.CLLocation", true)]
    [InlineData("WebKit.WKWebView", true)]
    [InlineData("Vision.VNRequest", true)]
    [InlineData("Foundation.Bundle", false)]       // No ObjC prefix
    [InlineData("Foundation.Timer", false)]         // No ObjC prefix
    [InlineData("Foundation.Data", false)]          // No ObjC prefix
    [InlineData("MyModule.MyClass", false)]         // Unknown prefix
    [InlineData("NoModule", false)]                 // No dot
    [InlineData("", false)]
    public void HasObjCClassPrefix_ReturnsExpected(string name, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.HasObjCClassPrefix(name));
    }

    [Theory]
    [InlineData("Foundation.N", false)]   // Too short — "N" doesn't match "NS" + uppercase
    [InlineData("Foundation.NS", false)]  // No letter after prefix
    public void HasObjCClassPrefix_EdgeCases(string name, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.HasObjCClassPrefix(name));
    }

    // --- IsPointerType ---

    [Theory]
    [InlineData("Swift.OpaquePointer", true)]
    [InlineData("Swift.UnsafePointer", true)]
    [InlineData("Swift.UnsafeMutablePointer", true)]
    [InlineData("Swift.UnsafeRawPointer", true)]
    [InlineData("Swift.UnsafeMutableRawPointer", true)]
    [InlineData("Builtin.RawPointer", true)]
    [InlineData("Swift.Int", false)]
    [InlineData("Foundation.NSObject", false)]
    [InlineData("", false)]
    public void IsPointerType_ReturnsExpected(string name, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.IsPointerType(name));
    }

    // --- IsNestedType ---

    [Theory]
    [InlineData("Foundation.URLSessionWebSocketTask.CloseCode", true)]
    [InlineData("Foundation.NSAttributedString.Key", true)]
    [InlineData("UIKit.UIView.ContentMode", true)]
    [InlineData("UIKit.UIControl.State", true)]
    [InlineData("Foundation.NSObject", false)]   // One dot — not nested
    [InlineData("UIKit.UIView", false)]
    [InlineData("NoModule", false)]               // No dots
    [InlineData("", false)]
    public void IsNestedType_ReturnsExpected(string name, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.IsNestedType(name));
    }

    // --- IsKnownObjCRootClass ---

    [Theory]
    [InlineData("NSObject", true)]
    [InlineData("NSProxy", true)]
    [InlineData("NSString", false)]
    [InlineData("UIView", false)]
    [InlineData("", false)]
    public void IsKnownObjCRootClass_ReturnsExpected(string name, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.IsKnownObjCRootClass(name));
    }

    // --- IsKnownModuleForElements ---

    [Theory]
    [InlineData("UIKit", true)]
    [InlineData("Foundation", true)]
    [InlineData("AVFoundation", false)]
    [InlineData("QuartzCore", false)]
    [InlineData("", false)]
    public void IsKnownModuleForElements_ReturnsExpected(string module, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.IsKnownModuleForElements(module));
    }

    // --- IsModuleAvailableOnPlatform ---

    [Theory]
    // tvOS: UI-interactive and hardware frameworks unavailable
    [InlineData("ContactsUI", ApplePlatform.tvOS, false)]
    [InlineData("EventKitUI", ApplePlatform.tvOS, false)]
    [InlineData("MessageUI", ApplePlatform.tvOS, false)]
    [InlineData("SafariServices", ApplePlatform.tvOS, false)]
    [InlineData("IntentsUI", ApplePlatform.tvOS, false)]
    [InlineData("CoreNFC", ApplePlatform.tvOS, false)]
    [InlineData("ARKit", ApplePlatform.tvOS, false)]
    // tvOS: core frameworks still available
    [InlineData("UIKit", ApplePlatform.tvOS, true)]
    [InlineData("Foundation", ApplePlatform.tvOS, true)]
    [InlineData("AVFoundation", ApplePlatform.tvOS, true)]
    // macOS: UIKit and mobile-only frameworks unavailable
    [InlineData("UIKit", ApplePlatform.macOS, false)]
    [InlineData("HealthKit", ApplePlatform.macOS, false)]
    [InlineData("HomeKit", ApplePlatform.macOS, false)]
    [InlineData("ARKit", ApplePlatform.macOS, false)]
    // macOS: AppKit and core frameworks available
    [InlineData("AppKit", ApplePlatform.macOS, true)]
    [InlineData("Foundation", ApplePlatform.macOS, true)]
    [InlineData("CoreML", ApplePlatform.macOS, true)]
    // iOS: everything available
    [InlineData("UIKit", ApplePlatform.iOS, true)]
    [InlineData("AppKit", ApplePlatform.iOS, true)]
    [InlineData("ARKit", ApplePlatform.iOS, true)]
    // MacCatalyst: both UIKit and AppKit available
    [InlineData("UIKit", ApplePlatform.MacCatalyst, true)]
    [InlineData("AppKit", ApplePlatform.MacCatalyst, true)]
    // Unknown modules default to available (conservative)
    [InlineData("MyCustomLib", ApplePlatform.tvOS, true)]
    [InlineData("MyCustomLib", ApplePlatform.macOS, true)]
    public void IsModuleAvailableOnPlatform_ReturnsExpected(string module, ApplePlatform platform, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.IsModuleAvailableOnPlatform(module, platform));
    }

    [Fact]
    public void IsModuleAvailableOnPlatform_NullPlatform_AlwaysReturnsTrue()
    {
        // Null platform = iOS default assumption, all modules available
        Assert.True(AppleFrameworkRegistry.IsModuleAvailableOnPlatform("UIKit", null));
        Assert.True(AppleFrameworkRegistry.IsModuleAvailableOnPlatform("AppKit", null));
        Assert.True(AppleFrameworkRegistry.IsModuleAvailableOnPlatform("ContactsUI", null));
    }

    [Fact]
    public void PlatformUnavailableModules_AreKnownFrameworks()
    {
        // Every module in the unavailability table should be a recognized Apple framework
        // (either AutoBridge or OptionalFallback), not a random unknown module.
        var unavailableModules = new[]
        {
            "ContactsUI", "EventKitUI", "MessageUI", "SafariServices", "IntentsUI",
            "CoreNFC", "CarPlay", "ClassKit", "ARKit",
            "UIKit", "HealthKit", "HomeKit",
        };

        foreach (var module in unavailableModules)
        {
            Assert.True(
                AppleFrameworkRegistry.IsAutoBridgeModule(module) ||
                AppleFrameworkRegistry.IsOptionalFallbackModule(module),
                $"Unavailable module '{module}' should be in AutoBridge or OptionalFallback sets");
        }
    }

    // --- Cross-cutting consistency checks ---

    [Fact]
    public void AllTypeNameRemaps_HaveConsistentModuleQualification()
    {
        // Every key in TypeNameRemaps should be module-qualified (contain at least one dot)
        // This test uses TryGetNetTypeName to probe known entries
        var knownKeys = new[]
        {
            "Foundation.Bundle", "Foundation.URLSession", "QuartzCore.CATransform3D",
            "UIKit.UIView.ContentMode", "AVFoundation.AVCaptureDevice.FocusMode",
        };

        foreach (var key in knownKeys)
        {
            Assert.True(key.Contains('.'), $"Key '{key}' should be module-qualified");
            Assert.True(AppleFrameworkRegistry.TryGetNetTypeName(key, out var netName),
                $"Key '{key}' should have a net type name mapping");
            Assert.True(netName.Contains('.'), $"Net name '{netName}' for '{key}' should be module-qualified");
        }
    }

    [Fact]
    public void QuartzCore_ValueTypes_MatchXmlEntries()
    {
        // All QuartzCore value types in the registry should map to CoreAnimation namespace
        var quartzCoreTypes = new[]
        {
            "QuartzCore.CATransform3D", "QuartzCore.CACornerMask",
            "QuartzCore.CAEdgeAntialiasingMask", "QuartzCore.CAAutoresizingMask",
            "QuartzCore.CAContentsFormat", "QuartzCore.CACornerCurve",
            "QuartzCore.CAGradientLayerType", "QuartzCore.CATextLayerAlignmentMode",
            "QuartzCore.CATextLayerTruncationMode", "QuartzCore.CAScroll",
            "QuartzCore.CADynamicRange", "QuartzCore.CAToneMapMode",
        };

        foreach (var typeName in quartzCoreTypes)
        {
            Assert.True(AppleFrameworkRegistry.IsKnownValueType(typeName),
                $"'{typeName}' should be a known value type");
            Assert.True(AppleFrameworkRegistry.TryGetNetTypeName(typeName, out var netName),
                $"'{typeName}' should have a net type name mapping");
            Assert.StartsWith("CoreAnimation.", netName);
        }
    }

    [Fact]
    public void ModuleNamespaceRemaps_AreReflectedInMapModulesInString()
    {
        // Verify that MapModulesInString applies all known module remaps
        Assert.Equal("CoreAnimation.X", AppleFrameworkRegistry.MapModulesInString("QuartzCore.X"));
        Assert.Equal("Foundation.X", AppleFrameworkRegistry.MapModulesInString("ObjectiveC.X"));
        Assert.Equal("CoreFoundation.X", AppleFrameworkRegistry.MapModulesInString("Dispatch.X"));
        Assert.Equal("AVFoundation.X", AppleFrameworkRegistry.MapModulesInString("AVFAudio.X"));
    }
}
