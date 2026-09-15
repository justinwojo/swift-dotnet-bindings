// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Closure-backed value providers — does what the managed block RETURNS reach Swift?
//
// Shape observed in an animation SDK: a provider is constructed with a block the renderer
// calls once per frame, and the block's return value (an array of colour structs, or an
// array of doubles) is what the renderer draws. Callback delivery and value delivery are
// separate questions — a bridge can dispatch to the managed delegate correctly and still
// drop what it hands back, which surfaces to a consumer as "the callback ran but nothing
// changed" rather than as a crash or a skip.
//
// Every member below is a Swift-side observation of the returned value, so a C# test can
// distinguish "the block fired" from "Swift received what the block returned".

import Foundation

/// Value the provider block returns, mirroring the reported colour struct: four doubles,
/// no `@frozen` (so it is resilient, as the reported one is).
public struct ProviderColor: Hashable {
    public var r: Double
    public var g: Double
    public var b: Double
    public var a: Double

    public init(r: Double, g: Double, b: Double, a: Double) {
        self.r = r
        self.g = g
        self.b = b
        self.a = a
    }
}

/// Provider whose blocks are supplied at construction, as the reported one is.
public final class DynamicGradientProvider {
    public typealias ColorsValueBlock = (Double) -> [ProviderColor]
    public typealias LocationsValueBlock = (Double) -> [Double]

    private let colorsBlock: ColorsValueBlock
    private let locationsBlock: LocationsValueBlock?

    public init(block: @escaping ColorsValueBlock, locations: LocationsValueBlock? = nil) {
        self.colorsBlock = block
        self.locationsBlock = locations
    }

    /// How many colours the block handed back for this frame.
    public func colorCount(at frame: Double) -> Int32 {
        Int32(colorsBlock(frame).count)
    }

    /// The first colour's components, summed — a single scalar a C# test can assert on
    /// without any further marshalling of the array itself.
    public func firstColorChecksum(at frame: Double) -> Double {
        guard let first = colorsBlock(frame).first else { return -1 }
        return first.r * 1000 + first.g * 100 + first.b * 10 + first.a
    }

    /// The whole array back out, so the round trip is observable end to end.
    public func colors(at frame: Double) -> [ProviderColor] {
        colorsBlock(frame)
    }

    /// Same question for an array of primitives, which needs no struct marshalling.
    public func locationSum(at frame: Double) -> Double {
        guard let locations = locationsBlock?(frame) else { return -1 }
        return locations.reduce(0, +)
    }

    public func locationCount(at frame: Double) -> Int32 {
        Int32(locationsBlock?(frame).count ?? -1)
    }
}

/// Setter-installed variant: the block arrives after construction, the way a renderer
/// attaches a provider to an already-built animation.
public final class DynamicGradientTarget {
    private var provider: DynamicGradientProvider?

    public init() {}

    public func setProvider(_ provider: DynamicGradientProvider) {
        self.provider = provider
    }

    /// The consumer-visible output: what the renderer would draw for this frame.
    public func renderedChecksum(at frame: Double) -> Double {
        provider?.firstColorChecksum(at: frame) ?? -1
    }
}

/// A tiny deterministic renderer analogue. It turns the provider's first and last
/// colors into a one-dimensional RGBA gradient, quantizes every pixel to the same
/// 8-bit representation a bitmap renderer uses, and exposes both the complete render
/// checksum and its pixel delta from the built-in black-to-white gradient.
///
/// The callback counter alone is deliberately insufficient: if a bridge invokes the
/// block but loses its returned palette, `changedPixelCountFromDefault` remains zero.
public final class DeterministicGradientRenderTarget {
    private let defaultColors = [
        ProviderColor(r: 0, g: 0, b: 0, a: 1),
        ProviderColor(r: 1, g: 1, b: 1, a: 1),
    ]
    private var provider: DynamicGradientProvider?

    public init() {}

    public func setProvider(_ provider: DynamicGradientProvider) {
        self.provider = provider
    }

    public func renderedPixelChecksum(width: Int32, at frame: Double) -> Int64 {
        let pixels = render(colors: resolvedColors(at: frame), width: width)
        return pixels.enumerated().reduce(Int64(0)) { partial, entry in
            partial + Int64(entry.offset + 1) * Int64(entry.element)
        }
    }

    public func changedPixelCountFromDefault(width: Int32, at frame: Double) -> Int32 {
        let baseline = render(colors: defaultColors, width: width)
        let actual = render(colors: resolvedColors(at: frame), width: width)
        var changed: Int32 = 0
        for pixel in 0..<Int(max(0, width)) {
            let offset = pixel * 4
            if actual[offset..<(offset + 4)] != baseline[offset..<(offset + 4)] {
                changed += 1
            }
        }
        return changed
    }

    /// Separately observes collection-result integrity through the installed-provider
    /// hop. A fired callback whose return is discarded reports zero here even though
    /// callback delivery itself succeeded.
    public func providerPaletteColorCount(at frame: Double) -> Int32 {
        Int32(provider?.colors(at: frame).count ?? -1)
    }

    private func resolvedColors(at frame: Double) -> [ProviderColor] {
        guard let colors = provider?.colors(at: frame), colors.count >= 2 else {
            return defaultColors
        }
        return colors
    }

    private func render(colors: [ProviderColor], width: Int32) -> [UInt8] {
        guard width > 0, let first = colors.first, let last = colors.last else { return [] }
        let denominator = max(1, Int(width) - 1)
        var pixels: [UInt8] = []
        pixels.reserveCapacity(Int(width) * 4)
        for x in 0..<Int(width) {
            let t = Double(x) / Double(denominator)
            pixels.append(quantize(first.r + (last.r - first.r) * t))
            pixels.append(quantize(first.g + (last.g - first.g) * t))
            pixels.append(quantize(first.b + (last.b - first.b) * t))
            pixels.append(quantize(first.a + (last.a - first.a) * t))
        }
        return pixels
    }

    private func quantize(_ value: Double) -> UInt8 {
        UInt8((min(1, max(0, value)) * 255).rounded())
    }
}
