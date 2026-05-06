// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Frozen Structs

/// A simple frozen struct with stored properties and methods.
@frozen
public struct FrozenPoint {
    public var x: Double
    public var y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }

    /// Returns the distance from the origin.
    public func distanceFromOrigin() -> Double {
        return (x * x + y * y).squareRoot()
    }

    /// Returns a new point translated by the given offsets.
    public func translated(dx: Double, dy: Double) -> FrozenPoint {
        return FrozenPoint(x: x + dx, y: y + dy)
    }

    /// Returns the midpoint between this point and another.
    public func midpoint(to other: FrozenPoint) -> FrozenPoint {
        return FrozenPoint(x: (x + other.x) / 2.0, y: (y + other.y) / 2.0)
    }
}

/// A frozen struct with various property types for testing property emission.
@frozen
public struct FrozenStructWithProperties {
    public let constantValue: Int32
    public var mutableValue: Int32
    public var name: String

    public init(constantValue: Int32, mutableValue: Int32, name: String) {
        self.constantValue = constantValue
        self.mutableValue = mutableValue
        self.name = name
    }

    /// Computed property (read-only).
    public var displayName: String {
        return "\(name) (\(mutableValue))"
    }

    /// Static stored property.
    public static let defaultName: String = "Default"

    /// Static computed property.
    public static var typeName: String {
        return "FrozenStructWithProperties"
    }
}

// MARK: - Large Frozen Struct (@_cdecl wrapper gap test)

/// Frozen struct with 4 Double fields (32 bytes) — triggers SwiftIndirectResult on ARM64.
/// Tests that frozen struct constructors get @_cdecl wrappers. Previously, this pattern
/// used CallConvSwift + SwiftIndirectResult which crashed Mono JIT
/// (URLEncoding(destination:arrayEncoding:boolEncoding:) pattern from Alamofire).
@frozen
public struct FrozenRect {
    public var x: Double
    public var y: Double
    public var width: Double
    public var height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }

    /// Computed property for area.
    public var area: Double {
        return width * height
    }

    /// Method returning another frozen struct.
    public func offset(dx: Double, dy: Double) -> FrozenRect {
        return FrozenRect(x: x + dx, y: y + dy, width: width, height: height)
    }
}

/// Free function operating on FrozenRect.
public func describeFrozenRect(_ rect: FrozenRect) -> String {
    return "(\(rect.x), \(rect.y), \(rect.width), \(rect.height))"
}

// MARK: - Non-Frozen Structs

/// A non-frozen struct (default). ABI is opaque to consumers.
public struct NonFrozenPoint {
    public var x: Double
    public var y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }

    public func distanceFromOrigin() -> Double {
        return (x * x + y * y).squareRoot()
    }
}

/// Non-frozen struct with various property types.
public struct NonFrozenStructWithProperties {
    public let constantValue: Int32
    public var mutableValue: Int32

    public init(constantValue: Int32, mutableValue: Int32) {
        self.constantValue = constantValue
        self.mutableValue = mutableValue
    }

    public var doubled: Int32 {
        return mutableValue * 2
    }
}

// MARK: - Nested Structs

/// Outer struct containing an inner struct, testing nested type emission.
@frozen
public struct NestedOuter {
    @frozen
    public struct Inner {
        public var value: Int32

        public init(value: Int32) {
            self.value = value
        }
    }

    public var inner: Inner
    public var label: String

    public init(inner: Inner, label: String) {
        self.inner = inner
        self.label = label
    }

    public func innerValue() -> Int32 {
        return inner.value
    }
}

// MARK: - Factory Pattern

/// A struct with factory (static) methods.
public struct StructBuilder {
    public var value: Int32

    public init(value: Int32) {
        self.value = value
    }

    /// Factory method returning a new instance.
    public static func withValue(_ value: Int32) -> StructBuilder {
        return StructBuilder(value: value)
    }

    /// Factory method returning a default instance.
    public static func makeDefault() -> StructBuilder {
        return StructBuilder(value: 0)
    }
}

// MARK: - Free Functions

/// Free function accepting a frozen struct.
public func describePoint(_ point: FrozenPoint) -> String {
    return "(\(point.x), \(point.y))"
}

/// Free function returning a frozen struct.
public func makeOrigin() -> FrozenPoint {
    return FrozenPoint(x: 0.0, y: 0.0)
}

/// Free function accepting and returning a non-frozen struct.
public func scalePoint(_ point: NonFrozenPoint, by factor: Double) -> NonFrozenPoint {
    return NonFrozenPoint(x: point.x * factor, y: point.y * factor)
}

// MARK: - IEnumerable<NonFrozenStruct> sync round-trip
// Regression coverage for `bug-0.10.0-ienumerable-iswiftstruct-raw-intptr-…` Defect A.
// The wrapper expects `Array<NonFrozenPoint>` storage (contiguous payload bytes per slot,
// not 1-word IntPtr handles). The sync paths exercise SwiftArray<NonFrozenPoint>
// FromEnumerable / AsProjected via VWT InitializeWithCopy / NewFromPayload.

/// Sums the magnitude of a sequence of non-frozen-struct points.
/// Exercises SwiftArray<NonFrozenPoint> as a parameter on a sync wrapper.
public func sumPointMagnitudes(_ points: [NonFrozenPoint]) -> Double {
    return points.reduce(0.0) { acc, p in acc + p.distanceFromOrigin() }
}

/// Returns each point scaled by `factor`. Exercises SwiftArray<NonFrozenPoint> as a
/// parameter AND a return value, validating the entire round-trip both directions.
public func scalePoints(_ points: [NonFrozenPoint], by factor: Double) -> [NonFrozenPoint] {
    return points.map { NonFrozenPoint(x: $0.x * factor, y: $0.y * factor) }
}

// MARK: - Set parameter with empty-literal default (StoreKit Product.purchase pattern)
// Regression coverage for `gap-0.10.0-swift-set-parameter-becomes-ienumerable-default-lost.md`.
// Pre-fix the parameter projects as `IEnumerable<nint>` (fidelity loss — Set's uniqueness
// invariant is dropped at the API boundary) and the `= []` default is silently dropped
// (consumer must construct an empty enumerable explicitly). Post-fix the parameter
// projects as `IReadOnlySet<nint>` and the empty-literal default surfaces as either
// an inline default or a trim overload that calls Swift's defaulted function.
//
// Element type is `Int` (Swift native `Int`, projects to C# `nint`) rather than
// `Int32` because Swift `Set<Int32>` insertion takes the runtime's fallback
// CallConvSwift path on Mono Simulator, which is a documented pre-existing
// limitation (see `SwiftSet.InsertUnsafe`). `Set<Int>` has a working `@_cdecl`
// wrapper (`SBW_SetInt_Insert`) so the round-trip exercises the Set projection
// without tripping the unrelated runtime gap.

/// Returns the count of a `Set<Int>` with an empty-literal default. The signature
/// pattern mirrors StoreKit's `Product.purchase(options: Set<PurchaseOption> = [])`.
public func setMembershipCount(_ values: Set<Int> = []) -> Int {
    return values.count
}

/// Sums the elements of a `Set<Int>` parameter with empty-literal default.
/// Provides round-trip evidence the post-fix surface still routes the values correctly
/// when the caller does pass an explicit set.
public func setMembershipSum(_ values: Set<Int> = []) -> Int {
    return values.reduce(0, +)
}

/// Async variant — exercises the StoreKit `purchase(options: Set<…> = []) async`
/// shape directly. Confirms whether the default-trim overload generator handles
/// async methods on collection-defaulted parameters.
public func setMembershipCountAsync(_ values: Set<Int> = []) async -> Int {
    return values.count
}

// MARK: - V1: Method Overloading by Parameter Type (KeychainSwift Set pattern)

/// Struct with 4 overloaded methods differing only by parameter type.
/// Each overload has a different mangled name and marshalling path.
public struct Converter {
    public init() {}

    public func convert(_ value: Int32) -> String {
        return "int:\(value)"
    }

    public func convert(_ value: Double) -> String {
        return "double:\(value)"
    }

    public func convert(_ value: Bool) -> String {
        return "bool:\(value)"
    }

    public func convert(_ value: String) -> String {
        return "string:\(value)"
    }
}

// MARK: - V2: @available Annotations (Parchment SupportedOSPlatform pattern)

/// Type with @available annotation → C# [SupportedOSPlatform].
@available(iOS 14.0, *)
public struct ModernFeature {
    public let name: String

    public init(name: String) {
        self.name = name
    }

    @available(iOS 15.0, *)
    public func enhance() -> String {
        return "Enhanced: \(name)"
    }
}

// MARK: - W3: Swift Float (32-bit) Properties (AMPopTip ShadowRadius pattern)

/// Frozen struct with Float (32-bit) stored properties.
/// Distinct from Double/CGFloat — uses `Sf` suffix in mangled name.
@frozen
public struct FloatHolder {
    public var radius: Float
    public var opacity: Float

    public init(radius: Float, opacity: Float) {
        self.radius = radius
        self.opacity = opacity
    }

    public func describe() -> String {
        return "r=\(radius), o=\(opacity)"
    }
}

// MARK: - Generic Collection-with-Metadata (WeatherKit Forecast pattern)

/// Reproduces WeatherKit's `Forecast<Element>` shape: a generic struct that
/// conforms to `RandomAccessCollection` (via `Collection`) with an accompanying
/// metadata property. Exercises the collection projection — the generator
/// should emit `Count`, indexer, and `GetEnumerator` so consumers can iterate.
public struct IndexedSeries<Element>: RandomAccessCollection {
    public let items: [Element]
    public let metadata: String

    public init(items: [Element], metadata: String) {
        self.items = items
        self.metadata = metadata
    }

    public var startIndex: Int { 0 }
    public var endIndex: Int { items.count }
    public subscript(index: Int) -> Element { items[index] }
}

/// Factory returning a concrete `IndexedSeries<String>`. The direct C# ctor is
/// now emittable via the static-factory dispatch path (Array<T> where T is a
/// parent generic is accepted by `GenericDispatchEmitter.CanEmitStaticDispatch`),
/// and is covered directly by `IndexedSeriesTests`. This factory is kept as a
/// coverage point for the collection+metadata return-type projection.
public func makeIndexedSeriesString() -> IndexedSeries<String> {
    return IndexedSeries(items: ["alpha", "beta", "gamma", "delta"], metadata: "four-strings")
}
