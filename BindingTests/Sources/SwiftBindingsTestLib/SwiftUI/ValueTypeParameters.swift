// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// SwiftUI value types used as ORDINARY parameters — Color and Font crossing the ABI as
// arguments rather than as part of a View bridge. Shape observed in a document-scanning
// SDK's shared theme object, whose settable properties are typed SwiftUI.Color /
// SwiftUI.Font; a C# consumer could read them but had no way to construct one, so every
// setter on the theme was unusable.
//
// The probe deliberately does NOT just report "the handle arrived" — it reconstructs the
// expected value on the Swift side and compares, so a marshalling bug that delivers a
// live-but-wrong payload fails the test.

// SwiftUI types are not accessible in the Mac Catalyst compiler environment despite the
// module importing successfully.
#if !targetEnvironment(macCatalyst)
import SwiftUI
import CoreGraphics

#if canImport(UIKit)
import UIKit
#elseif canImport(AppKit)
import AppKit
#endif

/// Accepts constructed SwiftUI value types and reports what actually crossed the ABI.
public struct SwiftUIValueProbe {

    public init() {}

    /// Reads back one sRGB component of `color` via the platform color type.
    /// Index 0/1/2/3 selects red/green/blue/alpha; any other index returns -1.
    ///
    /// This is the definitive round-trip check: it does not compare against a rebuilt
    /// `Color`, it extracts the components the value is actually carrying.
    public func colorComponent(_ color: SwiftUI.Color, at index: Int32) -> Double {
        var red: CGFloat = 0
        var green: CGFloat = 0
        var blue: CGFloat = 0
        var alpha: CGFloat = 0

        #if canImport(UIKit)
        UIColor(color).getRed(&red, green: &green, blue: &blue, alpha: &alpha)
        #elseif canImport(AppKit)
        // NSColor(Color) can land in a non-RGB color space; getRed(...) traps there, so
        // convert first and fall back to a sentinel rather than crashing the test host.
        guard let rgb = NSColor(color).usingColorSpace(.sRGB) else { return -1 }
        rgb.getRed(&red, green: &green, blue: &blue, alpha: &alpha)
        #else
        return -1
        #endif

        switch index {
        case 0: return Double(red)
        case 1: return Double(green)
        case 2: return Double(blue)
        case 3: return Double(alpha)
        default: return -1
        }
    }

    /// True when `font` equals a system font rebuilt from the same size/weight/design.
    ///
    /// `Font` exposes no component readback, so equality against an independently
    /// reconstructed value is the available oracle. The weight/design codes are decoded
    /// here from the same numbering the managed enums use — an independent second
    /// implementation of that mapping, so a one-off in either side shows up as a mismatch.
    public func fontIsSystem(_ font: SwiftUI.Font, size: Double, weight: Int32, design: Int32) -> Bool {
        return font == SwiftUI.Font.system(
            size: CGFloat(size),
            weight: SwiftUIValueProbe.weight(forCode: weight),
            design: SwiftUIValueProbe.design(forCode: design))
    }

    /// True when `color` equals a Color rebuilt from the same sRGB components.
    /// Complements `colorComponent(_:at:)` by exercising SwiftUI's own equality rather
    /// than a platform-color conversion.
    public func colorEquals(_ color: SwiftUI.Color, red: Double, green: Double, blue: Double, opacity: Double) -> Bool {
        return color == SwiftUI.Color(red: red, green: green, blue: blue, opacity: opacity)
    }

    /// 0 ultraLight · 1 thin · 2 light · 3 regular · 4 medium
    /// 5 semibold   · 6 bold · 7 heavy · 8 black
    private static func weight(forCode code: Int32) -> SwiftUI.Font.Weight {
        switch code {
        case 0: return .ultraLight
        case 1: return .thin
        case 2: return .light
        case 3: return .regular
        case 4: return .medium
        case 5: return .semibold
        case 6: return .bold
        case 7: return .heavy
        case 8: return .black
        default: return .regular
        }
    }

    /// 0 default · 1 serif · 2 rounded · 3 monospaced
    private static func design(forCode code: Int32) -> SwiftUI.Font.Design {
        switch code {
        case 1: return .serif
        case 2: return .rounded
        case 3: return .monospaced
        default: return .default
        }
    }
}
#endif
