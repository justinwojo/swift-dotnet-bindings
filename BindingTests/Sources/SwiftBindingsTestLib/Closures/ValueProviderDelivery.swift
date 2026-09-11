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
