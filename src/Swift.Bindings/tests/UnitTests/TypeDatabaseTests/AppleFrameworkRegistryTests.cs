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
    [InlineData("PassKit", true)]
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
            "StoreKit", "PDFKit", "SafariServices", "PassKit",
            "AuthenticationServices", "CoreBluetooth", "CoreSpotlight",
            "CoreML", "Vision", "NaturalLanguage", "SoundAnalysis", "Speech",
            "MultipeerConnectivity", "UserNotifications", "NetworkExtension",
            "Intents", "IntentsUI", "QuartzCore", "AVFAudio",
            "CoreLocation", "MapKit", "Matter",
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
    [InlineData("SwiftUICore", true)]
    [InlineData("XCTest", true)]
    [InlineData("Combine", true)]
    [InlineData("_Concurrency", true)]
    [InlineData("Observation", true)]
    [InlineData("WidgetKit", true)]
    [InlineData("AppIntents", false)]
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
    // Foundation Swift-only nested format styles. Without these entries
    // the synthetic ObjC bridge path flattens them into bogus
    // Foundation.DateComponentsFormatStyle / Foundation.DecimalFormatStyleCurrency
    // names that don't exist in Microsoft.iOS (StoreKit 2 follow-up bug #4).
    [InlineData("Foundation.Date.ComponentsFormatStyle", true)]
    [InlineData("Foundation.Decimal.FormatStyle.Currency", true)]
    // Foundation.Locale.Currency is a Swift-only nested struct with no
    // ObjC equivalent in Microsoft.iOS. Without this entry the synthetic
    // bridge produces a non-existent Foundation.LocaleCurrency, BoundGenericsHandler
    // collapses it to IntPtr, and StoreKit ends up with public
    // Swift.SwiftOptional<IntPtr> Currency leaks (Bug #4 follow-up).
    [InlineData("Foundation.Locale.Currency", true)]
    // Newly added value types for expanded modules
    [InlineData("CoreLocation.CLLocationCoordinate2D", true)]
    [InlineData("CoreLocation.CLAuthorizationStatus", true)]
    [InlineData("NaturalLanguage.NLLanguage", true)]
    [InlineData("CoreML.MLComputeUnits", true)]
    [InlineData("Photos.PHAuthorizationStatus", true)]
    [InlineData("Metal.MTLPixelFormat", true)]
    [InlineData("Network.NWEndpoint.Port", true)]
    [InlineData("PassKit.PKPaymentButtonType", true)]
    [InlineData("PassKit.PKPaymentNetwork", true)]
    // ARKit nested ObjC enums. Microsoft.iOS exposes these as flat enums
    // (ARRaycastTarget / ARRaycastTargetAlignment) rather than nested under
    // ARRaycastQuery. Without these entries the synthetic ObjC bridge path
    // overlap-dedups the parts into a non-existent ARRaycastQueryTarget
    // identifier — a CS0234 in any RealityKit binding that surfaces ARView.Raycast.
    // typeRemaps below pins the .NET name; valueTypes here keeps them off the
    // Class-bridged path so no Handle extraction is generated.
    [InlineData("ARKit.ARRaycastQuery.Target", true)]
    [InlineData("ARKit.ARRaycastQuery.TargetAlignment", true)]
    // ARHitTestResult.ResultType / UIAccessibilityCustomRotor.Direction —
    // same shape as ARRaycastQuery.Target: nested ObjC enum that
    // Microsoft.iOS hoists to a flat name (ARHitTestResultType /
    // UIAccessibilityCustomRotorDirection). Surfaced via RealityKit
    // dep-gate as CS0023 / CS0315 / CS1061 because the bridge path
    // synthesised a non-existent flattened identifier.
    [InlineData("ARKit.ARHitTestResult.ResultType", true)]
    [InlineData("UIKit.UIAccessibilityCustomRotor.Direction", true)]
    // Matter — the two cross-module ObjC enums referenced by MatterSupport's
    // WiFiScanResult. Must stay off the Class-bridged path so the resolver
    // picks them up from MatterDatabase.xml as proper enum/struct records.
    [InlineData("Matter.MTRNetworkCommissioningWiFiBand", true)]
    [InlineData("Matter.MTRNetworkCommissioningWiFiSecurity", true)]
    [InlineData("PassKit.PKPayment", false)]   // Class (NSObject subclass)
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

    // --- MapModuleToCompileImport ---

    [Theory]
    [InlineData("RealityFoundation", "RealityKit")]  // @_implementationOnly umbrella remap
    [InlineData("RealityKit", "RealityKit")]         // No remap — pass through
    [InlineData("UIKit", "UIKit")]                   // No remap — pass through
    [InlineData("Foundation", "Foundation")]         // No remap — pass through
    [InlineData("MyCustomLib", "MyCustomLib")]       // Unknown — pass through
    [InlineData("", "")]
    public void MapModuleToCompileImport_ReturnsExpected(string input, string expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.MapModuleToCompileImport(input));
    }

    [Fact]
    public void MapModuleToCompileImport_DoesNotAffectNetNamespace()
    {
        // The compile-import remap must NOT bleed into namespace remapping —
        // RealityFoundation's .NET namespace stays "RealityFoundation".
        Assert.Equal("RealityFoundation", AppleFrameworkRegistry.MapModuleToNetNamespace("RealityFoundation"));
    }

    // --- GetCompileImportSourceModules ---

    [Fact]
    public void GetCompileImportSourceModules_ReturnsRegisteredSources()
    {
        // Reverse direction of MapModuleToCompileImport: "RealityKit" is the umbrella;
        // RealityFoundation declares it as compileImportModule, so the source list must
        // contain RealityFoundation (and only that, for the current data file).
        var sources = AppleFrameworkRegistry.GetCompileImportSourceModules("RealityKit");
        Assert.Contains("RealityFoundation", sources);
    }

    [Theory]
    [InlineData("RealityFoundation")]   // Source side, not the umbrella
    [InlineData("UIKit")]               // No compile-import relationship
    [InlineData("MyCustomLib")]         // Unknown module
    [InlineData("")]                    // Empty input
    public void GetCompileImportSourceModules_ReturnsEmpty_ForNonUmbrellaModule(string module)
    {
        var sources = AppleFrameworkRegistry.GetCompileImportSourceModules(module);
        Assert.Empty(sources);
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
    // ARKit nested ObjC enums — see IsKnownValueType_ReturnsExpected for the
    // full why. Microsoft.iOS exposes these as ARKit.ARRaycastTarget /
    // ARKit.ARRaycastTargetAlignment, not flattened ARRaycastQueryTarget.
    [InlineData("ARKit.ARRaycastQuery.Target", "ARKit.ARRaycastTarget")]
    [InlineData("ARKit.ARRaycastQuery.TargetAlignment", "ARKit.ARRaycastTargetAlignment")]
    // ARHitTestResult.ResultType / UIAccessibilityCustomRotor.Direction —
    // same flat-hoist asymmetry; pin the Microsoft.iOS .NET name.
    [InlineData("ARKit.ARHitTestResult.ResultType", "ARKit.ARHitTestResultType")]
    [InlineData("UIKit.UIAccessibilityCustomRotor.Direction", "UIKit.UIAccessibilityCustomRotorDirection")]
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

    // --- JSON Data Completeness Tests ---
    // These tests verify that all data from the original hardcoded implementation
    // is correctly loaded from JSON embedded resources.

    [Fact]
    public void JsonLoaded_AllAutoBridgeModules_ArePresent()
    {
        // All auto-bridge modules from apple-frameworks.json
        var expectedModules = new[]
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
            "CoreLocation", "MapKit", "Matter", "PassKit",
        };

        foreach (var module in expectedModules)
        {
            Assert.True(AppleFrameworkRegistry.IsAutoBridgeModule(module),
                $"AutoBridge module '{module}' should be present");
        }
    }

    [Fact]
    public void JsonLoaded_AllOptionalFallbackModules_ArePresent()
    {
        // All optional fallback modules from the original hardcoded set
        var expectedModules = new[]
        {
            "CoreAnimation", "CoreBluetooth", "CoreLocation", "CoreMedia", "CoreML",
            "CoreMotion", "MapKit", "Matter", "Metal", "MetalKit", "PassKit", "PhotosUI",
            "QuartzCore", "SceneKit", "SpriteKit", "StoreKit", "WebKit",
            "ARKit", "RealityKit", "GameKit", "HealthKit", "HomeKit",
            "AuthenticationServices", "LocalAuthentication", "NaturalLanguage",
            "NetworkExtension", "UserNotifications", "Vision", "Intents",
            "EventKit", "Contacts", "MediaPlayer", "MultipeerConnectivity",
            "CoreNFC", "CarPlay", "ClassKit", "CloudKit", "CoreData",
            "CoreImage", "CoreSpotlight", "CoreTelephony", "FileProvider",
            "MessageUI", "SafariServices", "Social", "WatchConnectivity",
            "UIKit", "Foundation",
            "AppKit", "AVFoundation", "AVFAudio", "Photos", "SoundAnalysis", "Speech",
            "PDFKit", "ContactsUI", "EventKitUI", "IntentsUI",
            "GameController", "Network",
        };

        foreach (var module in expectedModules)
        {
            Assert.True(AppleFrameworkRegistry.IsOptionalFallbackModule(module),
                $"OptionalFallback module '{module}' should be present");
        }
    }

    [Fact]
    public void JsonLoaded_AllUnsupportedModules_ArePresent()
    {
        var expectedModules = new[]
        {
            "SwiftUI", "SwiftUICore", "XCTest", "Combine", "_Concurrency",
            "Observation", "WidgetKit", "Charts", "TipKit",
        };

        foreach (var module in expectedModules)
        {
            Assert.True(AppleFrameworkRegistry.IsUnsupportedModule(module),
                $"Unsupported module '{module}' should be present");
        }
    }

    [Fact]
    public void JsonLoaded_AllValueTypes_ArePresent()
    {
        // Complete set of value types from the original hardcoded data
        var expectedValueTypes = new[]
        {
            // UIKit
            "UIKit.UIEdgeInsets", "UIKit.UIOffset", "UIKit.UIFloatRange",
            "UIKit.NSDirectionalEdgeInsets",
            "UIKit.UIView.ContentMode", "UIKit.UIControl.State", "UIKit.UIControl.Event",
            "UIKit.UIAccessibilityCustomRotor.Direction",
            "UIKit.UIAccessibilityTraits",
            "UIKit.UIBarStyle", "UIKit.UIKeyboardAppearance", "UIKit.UITextField.ViewMode",
            "UIKit.UIControl.ContentVerticalAlignment", "UIKit.UIActivityIndicatorView.Style",
            "UIKit.UIBlurEffect.Style", "UIKit.UILayoutPriority",
            "UIKit.NSTextAlignment", "UIKit.NSWritingDirection",
            "UIKit.UIKeyboardType", "UIKit.UITextLayoutDirection",
            "UIKit.UIUserInterfaceLayoutDirection",
            "UIKit.UIImage.RenderingMode", "UIKit.UIImage.ResizingMode",
            "UIKit.UIImage.SymbolScale", "UIKit.UIImage.SymbolWeight",
            "UIKit.UIView.AnimationOptions", "UIKit.UIView.AutoresizingMask",
            "UIKit.UIView.AnimationCurve",
            "UIKit.UIRectEdge", "UIKit.UIRectCorner",
            "UIKit.UIInterfaceOrientation", "UIKit.UIInterfaceOrientationMask",
            "UIKit.UIUserInterfaceIdiom", "UIKit.UIUserInterfaceStyle",
            "UIKit.UISemanticContentAttribute",
            "UIKit.UIControl.ContentHorizontalAlignment",
            "UIKit.NSLineBreakMode",
            "UIKit.UITextAutocapitalizationType", "UIKit.UITextAutocorrectionType",
            "UIKit.UITextSpellCheckingType", "UIKit.UIReturnKeyType",
            "UIKit.UIDataDetectorTypes",
            "UIKit.UITableView.Style", "UIKit.UITextField.DidEndEditingReason",
            "UIKit.UISwipeGestureRecognizer.Direction",
            "UIKit.UICollectionView.ScrollDirection", "UIKit.UICollectionView.ScrollPosition",
            "UIKit.UITableViewCell.CellStyle", "UIKit.UITableViewCell.SelectionStyle",
            "UIKit.UITableViewCell.AccessoryType", "UIKit.UITableViewCell.EditingStyle",
            "UIKit.UITableView.RowAnimation", "UIKit.UITableView.ScrollPosition",
            "UIKit.UIScrollView.IndicatorStyle", "UIKit.UIScrollView.KeyboardDismissMode",
            "UIKit.UIScrollView.ContentInsetAdjustmentBehavior",
            "UIKit.UIStackView.Alignment", "UIKit.UIStackView.Distribution",
            "UIKit.UINavigationController.Operation",
            "UIKit.UINavigationItem.LargeTitleDisplayMode",
            "UIKit.UIPageViewController.NavigationDirection",
            "UIKit.UIPageViewController.NavigationOrientation",
            "UIKit.UIPageViewController.SpineLocation",
            "UIKit.UIPageViewController.TransitionStyle",
            "UIKit.UIGestureRecognizer.State",
            "UIKit.UIModalPresentationStyle", "UIKit.UIModalTransitionStyle",
            "UIKit.UIStatusBarStyle", "UIKit.UIStatusBarAnimation",
            "UIKit.UIBarPosition",
            "UIKit.UITabBarItem.SystemItem", "UIKit.UIBarButtonItem.SystemItem",
            "UIKit.UIBarButtonItem.Style",
            "UIKit.UIDatePicker.Mode",
            "UIKit.UIAlertController.Style", "UIKit.UIAlertAction.Style",
            "UIKit.NSUnderlineStyle",
            "UIKit.UIFont.TextStyle", "UIKit.UIFont.Weight",
            "UIKit.UIContentSizeCategory",
            "UIKit.NSLayoutConstraint.Relation", "UIKit.NSParagraphStyle.LineBreakStrategy",
            // AVFoundation
            "AVFoundation.AVAudioFramePosition", "AVFoundation.AVAudioFrameCount",
            "AVFoundation.AVAudioPacketCount", "AVFoundation.AVAudioChannelCount",
            "AVFoundation.AVCaptureVideoOrientation",
            "AVFoundation.AVCaptureSession.Preset",
            "AVFoundation.AVCaptureDevice.AutoFocusRangeRestriction",
            "AVFoundation.AVCaptureDevice.DeviceType",
            "AVFoundation.AVCaptureDevice.FocusMode",
            "AVFoundation.AVMediaType", "AVFoundation.AVFileType",
            "AVFoundation.AVLayerVideoGravity",
            "AVFoundation.AVCaptureDevice.Position",
            "AVFoundation.AVCaptureDevice.FlashMode", "AVFoundation.AVCaptureDevice.TorchMode",
            "AVFoundation.AVPlayer.TimeControlStatus", "AVFoundation.AVPlayer.Status",
            "AVFoundation.AVPlayerItem.Status",
            // CoreData
            "CoreData.NSFetchRequestResultType",
            // SceneKit
            "SceneKit.SCNVector3", "SceneKit.SCNVector4", "SceneKit.SCNMatrix4",
            // MapKit
            "MapKit.MKCoordinateRegion", "MapKit.MKCoordinateSpan",
            "MapKit.MKMapRect", "MapKit.MKMapPoint", "MapKit.MKMapSize",
            "MapKit.MKDirectionsTransportType",
            // ARKit
            "ARKit.ARRaycastQuery",
            "ARKit.ARRaycastQuery.Target",
            "ARKit.ARRaycastQuery.TargetAlignment",
            "ARKit.ARHitTestResult.ResultType",
            // Foundation
            "Foundation.Data", "Foundation.URL", "Foundation.UUID", "Foundation.IndexPath",
            "Foundation.URLError", "Foundation.URLError.Code",
            "Foundation.URLComponents", "Foundation.URLQueryItem", "Foundation.URLRequest",
            "Foundation.DateInterval", "Foundation.Calendar", "Foundation.Locale",
            "Foundation.TimeZone", "Foundation.Notification", "Foundation.Notification.Name",
            "Foundation.Measurement", "Foundation.PersonNameComponents",
            "Foundation.CharacterSet", "Foundation.Decimal", "Foundation.NSRange",
            "Foundation.Date", "Foundation.DateComponents", "Foundation.IndexSet",
            "Foundation.Selector", "Foundation.ComparisonResult",
            "Foundation._NSRange",
            "Foundation.JSONEncoder", "Foundation.JSONDecoder",
            "Foundation.PropertyListEncoder", "Foundation.PropertyListDecoder",
            // Foundation Swift-overlay classes routed to NSObject (no managed Foundation.NS* binding).
            // Each must be in valueTypes so AppleFrameworkRegistry.IsObjCBridgedTypeName returns false
            // for them; the actual NSObject TypeRecord comes from FoundationDatabase.xml.
            "Foundation.ByteCountFormatter", "Foundation.DateComponentsFormatter",
            "Foundation.DateIntervalFormatter", "Foundation.DistributedNotificationCenter",
            "Foundation.EnergyFormatter", "Foundation.FileWrapper",
            "Foundation.Host", "Foundation.ISO8601DateFormatter",
            "Foundation.LengthFormatter", "Foundation.ListFormatter",
            "Foundation.MassFormatter", "Foundation.MeasurementFormatter",
            "Foundation.MessagePort", "Foundation.NetService", "Foundation.NetServiceBrowser",
            "Foundation.NotificationQueue", "Foundation.PersonNameComponentsFormatter",
            "Foundation.Pipe", "Foundation.Port", "Foundation.PortMessage",
            "Foundation.Process", "Foundation.PropertyListSerialization",
            "Foundation.RelativeDateTimeFormatter", "Foundation.SocketPort",
            "Foundation.UnitConverter", "Foundation.UnitConverterLinear",
            "Foundation.ValueTransformer",
            "Foundation.XMLDocument", "Foundation.XMLDTD", "Foundation.XMLDTDNode",
            "Foundation.XMLElement", "Foundation.XMLNode",
            "Foundation.NSNotification.Name", "Foundation.objc_AssociationPolicy",
            "Foundation.XMLParser",
            "Foundation.URLSessionWebSocketTask.CloseCode",
            "Foundation.JSONSerialization.ReadingOptions",
            "Foundation.JSONSerialization.WritingOptions",
            "Foundation.Stream.Event",
            "Foundation.Operation.QueuePriority",
            "Foundation.URLCredential.Persistence",
            "Foundation.URLSession.ResponseDisposition",
            "Foundation.URLSession.AuthChallengeDisposition",
            "Foundation.RunLoop.Mode",
            "Foundation.FileAttributeKey",
            "Foundation.NSData.WritingOptions",
            "Foundation.NSRegularExpression.Options",
            "Foundation.NSAttributedString.Key",
            // StoreKit
            "StoreKit.SKPaymentTransactionState", "StoreKit.SKError.Code",
            "StoreKit.SKProduct.PeriodUnit", "StoreKit.SKProductDiscount.PaymentMode",
            // CoreBluetooth
            "CoreBluetooth.CBManagerState", "CoreBluetooth.CBManagerAuthorization",
            "CoreBluetooth.CBPeripheralState", "CoreBluetooth.CBCharacteristicProperties",
            "CoreBluetooth.CBAttributePermissions", "CoreBluetooth.CBCharacteristicWriteType",
            // Matter — MatterSupport's WiFiScanResult.security/.band reference these enums
            // from Apple's pure-ObjC Matter framework. valueTypes-listing keeps them off
            // the Class-bridged path; MatterDatabase.xml supplies the actual TypeRecord
            // (enum for Band/uint8_t, struct for Security/NS_OPTIONS).
            "Matter.MTRNetworkCommissioningWiFiBand",
            "Matter.MTRNetworkCommissioningWiFiSecurity",
            // Photos
            "Photos.PHImageContentMode",
            "Photos.PHAuthorizationStatus", "Photos.PHAccessLevel",
            "Photos.PHAssetMediaType", "Photos.PHAssetMediaSubtype",
            "Photos.PHAssetCollectionType", "Photos.PHAssetCollectionSubtype",
            "Photos.PHAssetResourceType",
            // PhotosUI
            "PhotosUI.PHPickerResult", "PhotosUI.PHPickerFilter",
            // QuartzCore
            "QuartzCore.CATransform3D", "QuartzCore.CACornerMask",
            "QuartzCore.CAEdgeAntialiasingMask", "QuartzCore.CAAutoresizingMask",
            "QuartzCore.CAContentsFormat", "QuartzCore.CACornerCurve",
            "QuartzCore.CAGradientLayerType", "QuartzCore.CATextLayerAlignmentMode",
            "QuartzCore.CATextLayerTruncationMode", "QuartzCore.CAScroll",
            "QuartzCore.CADynamicRange", "QuartzCore.CAToneMapMode",
            // CoreLocation
            "CoreLocation.CLLocationCoordinate2D", "CoreLocation.CLAuthorizationStatus",
            "CoreLocation.CLAccuracyAuthorization", "CoreLocation.CLLocationDirection",
            "CoreLocation.CLLocationDistance", "CoreLocation.CLLocationDegrees",
            // NaturalLanguage
            "NaturalLanguage.NLLanguage", "NaturalLanguage.NLTag",
            "NaturalLanguage.NLTagScheme", "NaturalLanguage.NLTokenUnit",
            // CoreML
            "CoreML.MLComputeUnits", "CoreML.MLFeatureType", "CoreML.MLMultiArrayDataType",
            // Metal
            "Metal.MTLSize", "Metal.MTLOrigin", "Metal.MTLRegion",
            "Metal.MTLClearColor", "Metal.MTLViewport", "Metal.MTLScissorRect",
            "Metal.MTLPixelFormat", "Metal.MTLPrimitiveType",
            "Metal.MTLStorageMode", "Metal.MTLResourceOptions",
            // Network
            "Network.NWEndpoint.Port",
            // PassKit
            "PassKit.PKPaymentButtonType", "PassKit.PKPaymentButtonStyle",
            "PassKit.PKPaymentNetwork", "PassKit.PKPaymentAuthorizationStatus",
            "PassKit.PKPaymentMethodType", "PassKit.PKAddPassButtonStyle",
            "PassKit.PKMerchantCapability", "PassKit.PKShippingType",
        };

        foreach (var vt in expectedValueTypes)
        {
            Assert.True(AppleFrameworkRegistry.IsKnownValueType(vt),
                $"Value type '{vt}' should be present");
        }
    }

    [Fact]
    public void JsonLoaded_AllTypeNameRemaps_ArePresent()
    {
        // Complete set of type name remaps from the original hardcoded data
        var expectedRemaps = new Dictionary<string, string>
        {
            // Foundation class remaps
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
            ["Foundation.NSURL"] = "Foundation.NSUrl",
            ["Foundation.NSUUID"] = "Foundation.NSUuid",
            ["Foundation._NSRange"] = "Foundation.NSRange",
            ["Foundation.JSONSerialization.ReadingOptions"] = "Foundation.NSJsonReadingOptions",
            ["Foundation.JSONSerialization.WritingOptions"] = "Foundation.NSJsonWritingOptions",
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
            ["Foundation.NSAttributedString.Key"] = "Foundation.NSString",
            // AVFoundation
            ["AVFoundation.AVURLAsset"] = "AVFoundation.AVUrlAsset",
            ["AVFoundation.AVMIDIPlayer"] = "AVFoundation.AVMidiPlayer",
            ["AVFoundation.AVCaptureDevice.FocusMode"] = "AVFoundation.AVCaptureFocusMode",
            // QuartzCore
            ["QuartzCore.CALayerContentsGravity"] = "Foundation.NSString",
            ["QuartzCore.CAMediaTimingFunctionName"] = "Foundation.NSString",
            ["QuartzCore.CATransitionType"] = "Foundation.NSString",
            ["QuartzCore.CATransitionSubtype"] = "Foundation.NSString",
            ["QuartzCore.CAKeyframeAnimation"] = "CoreAnimation.CAKeyFrameAnimation",
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
            // UIKit
            ["UIKit.UIImage.RenderingMode"] = "UIKit.UIImageRenderingMode",
            ["UIKit.UIView.AnimationOptions"] = "UIKit.UIViewAnimationOptions",
            ["UIKit.NSUnderlineStyle"] = "Foundation.NSUnderlineStyle",
            ["UIKit.NSLayoutConstraint.Relation"] = "UIKit.NSLayoutRelation",
            ["UIKit.NSParagraphStyle.LineBreakStrategy"] = "UIKit.NSLineBreakStrategy",
            ["UIKit.UIFont.TextStyle"] = "UIKit.UIFontTextStyle",
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
            ["UIKit.UIAccessibilityCustomRotor.Direction"] = "UIKit.UIAccessibilityCustomRotorDirection",
            // Photos
            ["Photos.PHImageContentMode"] = "Photos.PHImageContentMode",
            // ARKit
            ["ARKit.ARRaycastQuery.Target"] = "ARKit.ARRaycastTarget",
            ["ARKit.ARRaycastQuery.TargetAlignment"] = "ARKit.ARRaycastTargetAlignment",
            ["ARKit.ARHitTestResult.ResultType"] = "ARKit.ARHitTestResultType",
        };

        foreach (var (swiftName, expectedNetName) in expectedRemaps)
        {
            Assert.True(AppleFrameworkRegistry.TryGetNetTypeName(swiftName, out var netName),
                $"Type remap for '{swiftName}' should be present");
            Assert.Equal(expectedNetName, netName);
        }
    }

    [Fact]
    public void JsonLoaded_AllObjCPrefixes_AreDetected()
    {
        // All ObjC prefixes from the original hardcoded set, tested via HasObjCClassPrefix
        var prefixTests = new[]
        {
            ("Module.UIView", true),      // UI
            ("Module.NSObject", true),     // NS
            ("Module.MKMapView", true),    // MK
            ("Module.CLLocation", true),   // CL
            ("Module.CBManager", true),    // CB
            ("Module.CKRecord", true),     // CK
            ("Module.CNContact", true),    // CN
            ("Module.EKEvent", true),      // EK
            ("Module.GKSession", true),    // GK
            ("Module.HKStore", true),      // HK
            ("Module.HMHome", true),       // HM
            ("Module.MFMailer", true),     // MF
            ("Module.MCSession", true),    // MC
            ("Module.MPPlayer", true),     // MP
            ("Module.MTLDevice", true),    // MT (not MTK — MT matches first)
            ("Module.MTKView", true),      // MTK
            ("Module.PKPass", true),       // PK
            ("Module.SCNNode", true),      // SC
            ("Module.SKProduct", true),    // SK
            ("Module.WKWebView", true),    // WK
            ("Module.VNRequest", true),    // VN
            ("Module.ARSession", true),    // AR
            ("Module.ASAuth", true),       // AS
            ("Module.LAContext", true),    // LA
            ("Module.NEProvider", true),   // NE
            ("Module.UNNotification", true), // UN
            ("Module.INIntent", true),     // IN
            ("Module.CALayer", true),      // CA
            ("Module.CIFilter", true),     // CI
            ("Module.SFSafari", true),     // SF
            ("Module.SLRequest", true),    // SL
            ("Module.NKIssue", true),      // NK
            ("Module.CPTemplate", true),   // CP
            ("Module.FPManager", true),    // FP
            ("Module.REEntity", true),     // RE
            ("Module.AVPlayer", true),     // AV
            ("Module.PHAsset", true),      // PH
            ("Module.NWEndpoint", true),   // NW
            ("Module.MLModel", true),      // ML
            ("Module.GCController", true), // GC
            ("Module.NLTagger", true),     // NL
            ("Module.SNRequest", true),    // SN
            ("Module.MTRSetupPayload", true), // MTR (Matter framework)
        };

        foreach (var (name, expected) in prefixTests)
        {
            Assert.True(AppleFrameworkRegistry.HasObjCClassPrefix(name) == expected,
                $"HasObjCClassPrefix('{name}') should be {expected}");
        }
    }

    [Fact]
    public void JsonLoaded_AllModuleNamespaceRemaps_ArePresent()
    {
        // All 4 namespace remaps from the original hardcoded data
        Assert.Equal("Foundation", AppleFrameworkRegistry.MapModuleToNetNamespace("ObjectiveC"));
        Assert.Equal("CoreAnimation", AppleFrameworkRegistry.MapModuleToNetNamespace("QuartzCore"));
        Assert.Equal("CoreFoundation", AppleFrameworkRegistry.MapModuleToNetNamespace("Dispatch"));
        Assert.Equal("AVFoundation", AppleFrameworkRegistry.MapModuleToNetNamespace("AVFAudio"));
    }

    [Fact]
    public void JsonLoaded_AllPlatformUnavailableModules_ArePresent()
    {
        // tvOS unavailable modules
        var tvOSUnavailable = new[] { "ContactsUI", "EventKitUI", "MessageUI", "SafariServices", "IntentsUI", "CoreNFC", "CarPlay", "ClassKit", "ARKit" };
        foreach (var module in tvOSUnavailable)
        {
            Assert.False(AppleFrameworkRegistry.IsModuleAvailableOnPlatform(module, ApplePlatform.tvOS),
                $"'{module}' should be unavailable on tvOS");
        }

        // macOS unavailable modules
        var macOSUnavailable = new[] { "UIKit", "HealthKit", "HomeKit", "ARKit", "CoreNFC", "CarPlay", "ClassKit" };
        foreach (var module in macOSUnavailable)
        {
            Assert.False(AppleFrameworkRegistry.IsModuleAvailableOnPlatform(module, ApplePlatform.macOS),
                $"'{module}' should be unavailable on macOS");
        }
    }

    [Fact]
    public void JsonLoaded_KnownModulesForElements_AreCorrect()
    {
        Assert.True(AppleFrameworkRegistry.IsKnownModuleForElements("UIKit"));
        Assert.True(AppleFrameworkRegistry.IsKnownModuleForElements("Foundation"));
        Assert.False(AppleFrameworkRegistry.IsKnownModuleForElements("AVFoundation"));
        Assert.False(AppleFrameworkRegistry.IsKnownModuleForElements("CoreData"));
    }

    [Fact]
    public void JsonLoaded_NoFalsePositives_AutoBridge()
    {
        // Modules that must NOT be in auto-bridge
        var notAutoBridge = new[]
        {
            "CoreAnimation", "CoreMedia", "CoreMotion", "Metal", "MetalKit",
            "GameKit", "LocalAuthentication", "CoreNFC", "CarPlay",
            "ClassKit", "CoreTelephony", "FileProvider", "MessageUI", "Social",
            "WatchConnectivity", "MediaPlayer", "GameController", "Network",
        };

        foreach (var module in notAutoBridge)
        {
            Assert.False(AppleFrameworkRegistry.IsAutoBridgeModule(module),
                $"'{module}' should NOT be in AutoBridge");
        }
    }

    [Fact]
    public void JsonLoaded_NoFalsePositives_Unsupported()
    {
        // Regular frameworks must not be unsupported
        var notUnsupported = new[]
        {
            "Foundation", "UIKit", "AVFoundation", "CoreData", "Photos",
        };

        foreach (var module in notUnsupported)
        {
            Assert.False(AppleFrameworkRegistry.IsUnsupportedModule(module),
                $"'{module}' should NOT be unsupported");
        }
    }

    // --- CoreGraphics / Apple Framework Type Edge Cases ---

    [Fact]
    public void CoreGraphics_IsNotAutoBridgeModule()
    {
        // CoreGraphics types are handled via XML database, not auto-bridge.
        // Auto-bridge is for ObjC-class-heavy frameworks; CG is pure C/value types.
        Assert.False(AppleFrameworkRegistry.IsAutoBridgeModule("CoreGraphics"));
    }

    [Theory]
    [InlineData("CoreGraphics.CGPoint", false)]  // CG types are value types, but not in registry's known set
    [InlineData("CoreGraphics.CGSize", false)]
    [InlineData("CoreGraphics.CGRect", false)]
    [InlineData("CoreFoundation.CGFloat", false)]  // CGFloat is a primitive alias, not a struct
    public void CoreGraphicsTypes_ValueTypeClassification(string name, bool expected)
    {
        // CoreGraphics types are NOT in the registry's IsKnownValueType set because
        // they're handled via CoreGraphicsDatabase.xml at the TypeDatabase level.
        // This test documents that the registry defers to XML for CG type classification.
        Assert.Equal(expected, AppleFrameworkRegistry.IsKnownValueType(name));
    }

    // --- ShouldSuppressDeclaredWrapperImport ---
    //
    // Broad gate. True for every Apple framework registered in apple-frameworks.json
    // (autoBridge, optionalFallback, concreteClassFallback, unsupported, plus
    // wrapperImportable surface-coverage entries) AND the Swift stdlib / ObjC runtime
    // modules that never appear in the JSON. The wrapper Swift source skips re-emitting
    // declared `import X` lines for these because the umbrella chain (or
    // surface-driven imports) already cover them.

    [Theory]
    [InlineData("Swift", true)]
    [InlineData("Foundation", true)]
    [InlineData("UIKit", true)]
    [InlineData("Security", true)]
    [InlineData("_Concurrency", true)]
    [InlineData("ObjectiveC", true)]
    [InlineData("Dispatch", true)]
    [InlineData("CoreFoundation", true)]
    [InlineData("CoreGraphics", false)]              // CG is handled via XML database, not registry module set
    [InlineData("SwiftUI", true)]                    // Unsupported but still a known Apple module
    [InlineData("SwiftUICore", true)]                // SwiftUICore is the internal split-out
    [InlineData("RealityFoundation", true)]          // concreteClassFallback — wrapper should still suppress
    [InlineData("StripePayments", false)]            // Third-party
    [InlineData("Alamofire", false)]                 // Third-party
    [InlineData("MyCustomLib", false)]               // Unknown
    [InlineData("", false)]
    public void ShouldSuppressDeclaredWrapperImport_ReturnsExpected(string module, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.ShouldSuppressDeclaredWrapperImport(module));
    }

    // --- IsSystemReexportAllowedModule ---
    //
    // Narrow gate. True for Swift stdlib / ObjC runtime modules and for the
    // "common Apple" sets (autoBridge / optionalFallback / unsupported) — the
    // modules whose types may appear in another module's ABI as bare references
    // that should be kept (with a moduleName override). Deliberately FALSE for
    // concreteClassFallback-only modules (RealityFoundation): those need to flow
    // through the parser's children-first cross-module extension branch so their
    // extension types (e.g. RealityKit's nested RotorType inside an
    // `extension RealityFoundation.AccessibilityComponent`) are routed to
    // CrossModuleExtensionEmitter and emitted under the canonical namespace.

    [Theory]
    [InlineData("Swift", true)]
    [InlineData("Foundation", true)]
    [InlineData("UIKit", true)]
    [InlineData("Security", true)]
    [InlineData("_Concurrency", true)]
    [InlineData("ObjectiveC", true)]
    [InlineData("Dispatch", true)]
    [InlineData("CoreFoundation", true)]
    [InlineData("CoreGraphics", false)]              // CG handled via XML database
    [InlineData("SwiftUI", true)]                    // unsupported set — still kept on parser path
    [InlineData("SwiftUICore", true)]
    [InlineData("RealityFoundation", false)]         // concreteClassFallback ONLY — routes via children-first
    [InlineData("SceneKit", true)]                   // concreteClassFallback BUT also autoBridge/optionalFallback
    [InlineData("StripePayments", false)]            // Third-party
    [InlineData("Alamofire", false)]                 // Third-party
    [InlineData("MyCustomLib", false)]               // Unknown
    [InlineData("", false)]
    public void IsSystemReexportAllowedModule_ReturnsExpected(string module, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.IsSystemReexportAllowedModule(module));
    }

    // --- IsObjCBridgedTypeName ---
    //
    // Per-module ObjC prefix gate. Modules that declare an `objcPrefixes` entry in
    // apple-frameworks.json get a strict per-module check (Swift-only types whose
    // names don't match return false); modules without a declared prefix list
    // preserve the conservative "all autoBridge class types are ObjC" behavior.

    [Theory]
    // RealityKit declares ["RE"]: Swift-only protocols/classes whose names don't
    // start with RE must NOT be classified as ObjC. This is the Session 5 P1 bug —
    // SynchronizationPeerID was wrongly filtered out of effective protocols.
    [InlineData("RealityKit", "RealityKit.SynchronizationPeerID", false)]
    [InlineData("RealityKit", "RealityKit.MultipeerConnectivityService", false)]
    [InlineData("RealityKit", "RealityKit.Entity", false)]
    [InlineData("RealityKit", "RealityKit.REEntity", true)]   // hypothetical RE-prefixed name still classifies as ObjC
    // MultipeerConnectivity declares ["MC"]: MCSession is real ObjC; SessionState is Swift-only.
    [InlineData("MultipeerConnectivity", "MultipeerConnectivity.MCSession", true)]
    [InlineData("MultipeerConnectivity", "MultipeerConnectivity.SessionState", false)]
    // UIKit declares ["UI"]: UIView passes; a Swift-only protocol like
    // UICoordinateSpace is still UI-prefixed and stays ObjC. Genuinely Swift-only
    // names (no UI prefix) get correctly re-classified.
    [InlineData("UIKit", "UIKit.UIView", true)]
    [InlineData("UIKit", "UIKit.UICoordinateSpace", true)]
    [InlineData("UIKit", "UIKit.MyHypotheticalSwiftType", false)]
    // AVFoundation declares ["AV"]: AVPlayer is ObjC; a Swift-only protocol with no
    // AV prefix correctly returns false.
    [InlineData("AVFoundation", "AVFoundation.AVPlayer", true)]
    [InlineData("AVFoundation", "AVFoundation.SomeSwiftProtocol", false)]
    // ARKit declares ["AR"]: ARSession is ObjC; valueTypes-listed entries stay false.
    [InlineData("ARKit", "ARKit.ARSession", true)]
    [InlineData("ARKit", "ARKit.ARRaycastQuery.Target", false)]   // listed in valueTypes
    // Matter declares ["MTR"]: MTRSetupPayload is genuine ObjC; the two WiFi value
    // types are excluded so MatterDatabase.xml supplies their TypeRecords instead
    // of synthesizing bogus Class records.
    [InlineData("Matter", "Matter.MTRSetupPayload", true)]
    [InlineData("Matter", "Matter.MTRNetworkCommissioningWiFiBand", false)]
    [InlineData("Matter", "Matter.MTRNetworkCommissioningWiFiSecurity", false)]
    // Foundation declares ["NS"]: NSString is ObjC; non-NS Foundation types
    // (Bundle, Timer, etc. — handled separately by TryGetNetTypeName).
    [InlineData("Foundation", "Foundation.NSString", true)]
    [InlineData("Foundation", "Foundation.LocalizedError", false)]
    // AppKit declares no prefix list — preserves the "all autoBridge class types
    // are ObjC" backstop so existing AppKit consumers don't regress while the
    // prefix list waits to be backfilled.
    [InlineData("AppKit", "AppKit.NSWindow", true)]
    [InlineData("AppKit", "AppKit.SomeSwiftType", true)]
    // Non-autoBridge module — never classified as ObjC.
    [InlineData("RealityFoundation", "RealityFoundation.SynchronizationPeerID", false)]
    [InlineData("MyCustomLib", "MyCustomLib.MyType", false)]
    [InlineData("", "", false)]
    public void IsObjCBridgedTypeName_ReturnsExpected(string module, string moduleQualifiedName, bool expected)
    {
        Assert.Equal(expected, AppleFrameworkRegistry.IsObjCBridgedTypeName(module, moduleQualifiedName));
    }

    // --- TryGetPackageId ---
    // Cross-framework dep-edge auto-injection lives or dies on this lookup. Modules with
    // a registered packageId resolve to a SwiftBindings.Apple.<Module> NuGet ID; modules
    // without one (markers like Swift / _Concurrency, or Apple SDK modules that don't ship
    // as a standalone binding package) MUST return false so the MSBuild target skips them.

    [Theory]
    [InlineData("RealityKit", "SwiftBindings.Apple.RealityKit")]
    [InlineData("RealityFoundation", "SwiftBindings.Apple.RealityFoundation")]
    [InlineData("Matter", "SwiftBindings.Apple.Matter")]
    public void TryGetPackageId_ReturnsRegisteredPackageId(string module, string expectedPackageId)
    {
        Assert.True(AppleFrameworkRegistry.TryGetPackageId(module, out var packageId));
        Assert.Equal(expectedPackageId, packageId);
    }

    // --- IsWrapperImportableModule ---
    // Source of truth for "should the generated wrapper Swift emit `import X`?" — the
    // predicate that ModuleHandler.CollectFrameworkImports / CheckTypeNameForFrameworkImport
    // route through. Pinned positive cases cover representative buckets (UI framework,
    // newer SDK framework, ObjC sibling that triggered the MatterSupport gap, system
    // C-bridge module). Pinned negative cases cover the intentional exclusions:
    // unconditional imports (Foundation), ambient collision (Network), umbrella-source
    // modules whose wrapper import is rewritten via compileImportModule
    // (RealityFoundation → RealityKit), SPI modules (_LocationEssentials), and Swift
    // markers (_Concurrency, Dispatch, ObjectiveC).

    [Theory]
    [InlineData("UIKit")]
    [InlineData("AppKit")]
    [InlineData("CoreGraphics")]
    [InlineData("Matter")]
    [InlineData("RealityKit")]
    [InlineData("WeatherKit")]
    [InlineData("OSLog")]
    [InlineData("AppIntents")]
    public void IsWrapperImportableModule_ReturnsTrueForImportableModules(string module)
    {
        Assert.True(AppleFrameworkRegistry.IsWrapperImportableModule(module));
    }

    [Theory]
    [InlineData("Foundation")]
    [InlineData("Network")]
    [InlineData("RealityFoundation")]
    [InlineData("_LocationEssentials")]
    [InlineData("_Concurrency")]
    [InlineData("Dispatch")]
    [InlineData("ObjectiveC")]
    [InlineData("MyCustomLib")]
    [InlineData("")]
    public void IsWrapperImportableModule_ReturnsFalseForExcludedOrUnknownModules(string module)
    {
        Assert.False(AppleFrameworkRegistry.IsWrapperImportableModule(module));
    }

    [Theory]
    // Marker imports — never have a packageId
    [InlineData("Swift")]
    [InlineData("_Concurrency")]
    [InlineData("_StringProcessing")]
    [InlineData("simd")]
    [InlineData("__ObjC")]
    [InlineData("Builtin")]
    // Apple SDK modules in the registry but without a standalone binding package
    [InlineData("Foundation")]
    [InlineData("UIKit")]
    [InlineData("ARKit")]
    [InlineData("Combine")]
    // Unknown / non-Apple modules
    [InlineData("MyCustomLib")]
    [InlineData("")]
    public void TryGetPackageId_ReturnsFalseForUnregisteredModules(string module)
    {
        Assert.False(AppleFrameworkRegistry.TryGetPackageId(module, out var packageId));
        Assert.True(string.IsNullOrEmpty(packageId));
    }
}
