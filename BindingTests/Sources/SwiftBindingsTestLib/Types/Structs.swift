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

// MARK: - Mixed Int/Float Register-File Returns

/// An 8-byte frozen struct whose `Int32` and `Float` fields share one eightbyte. swiftcc returns
/// it field-wise (the Int32 in a general-purpose register, the Float in a vector register) while
/// the C ABI packs the eightbyte into a single integer register — so a register-return thunk would
/// place the Float in the wrong register. The generator must route this through the @_cdecl wrapper
/// instead. Round-tripped by `MixedRegisterReturnTests`.
@frozen
public struct MixedSmall {
    public var tag: Int32
    public var value: Float

    public init(tag: Int32, value: Float) {
        self.tag = tag
        self.value = value
    }

    /// Returns a derived value by value — exercises the same return path on an instance method.
    public func scaled(by factor: Float) -> MixedSmall {
        return MixedSmall(tag: tag &+ 1, value: value * factor)
    }
}

/// A 24-byte frozen struct mixing int and float fields at non-uniform offsets. Returned by value it
/// exceeds 16 bytes, so the thunk uses the field-wise return bridge — each register stored to its
/// natural buffer offset. Guards against an 8-byte-stride store corrupting the 4-byte `Float` or the
/// `Int64`. Round-tripped by `MixedRegisterReturnTests`.
@frozen
public struct MixedWide {
    public var tag: Int32
    public var scale: Float
    public var count: Int64
    public var weight: Double

    public init(tag: Int32, scale: Float, count: Int64, weight: Double) {
        self.tag = tag
        self.scale = scale
        self.count = count
        self.weight = weight
    }
}

/// A 16-byte frozen struct whose `Int64` and `Double` fields each own a separate eightbyte. On
/// x86_64 SysV the conventions agree (Int64 in a GPR, Double in an SSE register), but arm64 AAPCS64
/// returns this non-HFA aggregate entirely in general-purpose registers (Int64 in x0, Double bits in
/// x1) while swiftcc returns the Double in d0. A register-return thunk — chosen once for both arches —
/// would read the Double from the wrong register on arm64, so the generator must route this through
/// the @_cdecl wrapper. Round-tripped by `MixedRegisterReturnTests`.
@frozen
public struct WidePair {
    public var count: Int64
    public var weight: Double

    public init(count: Int64, weight: Double) {
        self.count = count
        self.weight = weight
    }

    /// Returns a derived value by value — exercises the same return path on an instance method.
    public func scaled(by factor: Double) -> WidePair {
        return WidePair(count: count &+ 1, weight: weight * factor)
    }
}

/// A 16-byte frozen struct whose `Float` and `Double` fields each own a separate eightbyte but are
/// different floating-point types. On x86_64 SysV each owns an SSE eightbyte and the conventions agree,
/// but the mixed widths mean this is NOT a homogeneous floating-point aggregate, so arm64 AAPCS64
/// returns it in the general-purpose registers (Float bits in w0, Double bits in x1) while swiftcc
/// returns it field-wise in s0/d1. The each-owns-an-eightbyte test alone would wrongly tail-call-thunk
/// this; only requiring a homogeneous FP type routes it to the @_cdecl wrapper. Round-tripped by
/// `MixedRegisterReturnTests`.
@frozen
public struct WideFloatDouble {
    public var scale: Float
    public var weight: Double

    public init(scale: Float, weight: Double) {
        self.scale = scale
        self.weight = weight
    }

    /// Returns a derived value by value — exercises the same return path on an instance method.
    public func scaled(by factor: Double) -> WideFloatDouble {
        return WideFloatDouble(scale: scale + 1.0, weight: weight * factor)
    }
}

/// Free-function factory returning a ≤16-byte mixed int/float struct by value.
public func makeMixedSmall(tag: Int32, value: Float) -> MixedSmall {
    return MixedSmall(tag: tag, value: value)
}

/// Free-function factory returning a 16-byte {Float, Double} struct by value (non-HFA, arm64-divergent).
public func makeWideFloatDouble(scale: Float, weight: Double) -> WideFloatDouble {
    return WideFloatDouble(scale: scale, weight: weight)
}

/// Free-function factory returning a 16-byte {Int64, Double} struct by value (arm64-divergent shape).
public func makeWidePair(count: Int64, weight: Double) -> WidePair {
    return WidePair(count: count, weight: weight)
}

/// Free-function factory returning a >16-byte mixed-width struct by value.
public func makeMixedWide(tag: Int32, scale: Float, count: Int64, weight: Double) -> MixedWide {
    return MixedWide(tag: tag, scale: scale, count: count, weight: weight)
}

/// A 40-byte (5 × Int64) frozen struct that exceeds the four-register direct-return budget, so
/// swiftcc returns it INDIRECTLY through a caller-allocated result buffer. This is the shape that
/// drives the x86_64 thunk's indirect-result paths: the SysV thunk must move the cdecl sret pointer
/// (a hidden first argument in %rdi) into swiftcc's indirect-result register (%rax). All-integer
/// fields keep self and the return thunk-eligible — a float/Double field would route the method to
/// the @_cdecl wrapper instead. Readable scalar fields let a round-trip catch a wrong buffer pointer,
/// which would otherwise return garbage silently. Round-tripped by `MixedRegisterReturnTests`.
@frozen
public struct LargeScalarStruct {
    public var a: Int64
    public var b: Int64
    public var c: Int64
    public var d: Int64
    public var e: Int64

    public init(a: Int64, b: Int64, c: Int64, d: Int64, e: Int64) {
        self.a = a
        self.b = b
        self.c = c
        self.d = d
        self.e = e
    }
}

/// Free-function factory returning the 40-byte indirect struct by value — the indirect-result
/// TAIL-CALL path (no self, so the thunk moves %rdi→%rax, shifts the explicit arguments, and jumps).
public func makeLargeScalarStruct(seed: Int64) -> LargeScalarStruct {
    return LargeScalarStruct(a: seed, b: seed &+ 1, c: seed &+ 2, d: seed &+ 3, e: seed &+ 4)
}

/// Final class whose instance method returns the 40-byte indirect struct by value — the
/// indirect-result FULL-FRAME path: self in the swiftself register AND the sret pointer bridged
/// into %rax across the call. The free-function factory above cannot reach this path because with
/// no self it stays a tail call. self is a single class pointer (8 bytes, blittable), so the method
/// stays thunk-eligible rather than being forced to the @_cdecl wrapper the way a >8-byte struct
/// self would be. Round-tripped by `MixedRegisterReturnTests`.
public final class LargeScalarStructFactory {
    private let seed: Int64

    public init(seed: Int64) {
        self.seed = seed
    }

    public func make() -> LargeScalarStruct {
        return LargeScalarStruct(a: seed, b: seed &+ 1, c: seed &+ 2, d: seed &+ 3, e: seed &+ 4)
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
/// (large frozen struct with multiple encoding parameters).
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
// Regression coverage for IEnumerable<NonFrozenStruct> raw-IntPtr packing defect A.
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
// Regression coverage for the Set-parameter projection and empty-literal default loss bug.
// Pre-fix the parameter projects as `IEnumerable<nint>` (fidelity loss — Set's uniqueness
// invariant is dropped at the API boundary) and the `= []` default is silently dropped
// (consumer must construct an empty enumerable explicitly). Post-fix the parameter
// projects as `IReadOnlySet<nint>` and the empty-literal default surfaces as either
// an inline default or a trim overload that calls Swift's defaulted function.
//
// Element type is `Int` (Swift native `Int`, projects to C# `nint`), which the
// runtime inserts through its typed `@_cdecl` wrapper `SBW_SetInt_Insert`. That
// keeps these fixtures focused on the Set *projection* — `IReadOnlySet<T>` and
// the empty-literal default — rather than on insert dispatch. The general insert
// path, taken by any element type without a typed wrapper, has its own fixtures
// in `Collections/SetStructElement.swift`.

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

// MARK: - V1: Method Overloading by Parameter Type

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

// MARK: - V2: @available Annotations

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

// MARK: - W3: Swift Float (32-bit) Properties

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

// MARK: - Eightbyte Grouping: {Int8×5, Int64, Int64} by-value return

/// A 24-byte frozen struct whose five leading `Int8` fields share the first eightbyte while the two
/// `Int64` fields each own a full eightbyte — laid out `[b0..b4 + 3 pad][first][second]`. swiftcc
/// returns it directly in three general-purpose registers (x0/x1/x2), so the generator must group the
/// ABI field layout into exactly three integer eightbytes. A naïve per-field count (7 slots) would
/// wrongly mark it indirect or mis-store the packed first eightbyte, silently corrupting the bytes.
/// Because it exceeds 16 bytes, the C ABI returns it via the x8 sret buffer, so the thunk bridges the
/// three return registers to their natural buffer offsets (0, 8, 16). Round-tripped by
/// `MixedRegisterReturnTests`.
@frozen
public struct ByteQuintWide {
    public var b0: Int8
    public var b1: Int8
    public var b2: Int8
    public var b3: Int8
    public var b4: Int8
    public var first: Int64
    public var second: Int64

    public init(b0: Int8, b1: Int8, b2: Int8, b3: Int8, b4: Int8, first: Int64, second: Int64) {
        self.b0 = b0
        self.b1 = b1
        self.b2 = b2
        self.b3 = b3
        self.b4 = b4
        self.first = first
        self.second = second
    }
}

/// Returns a `ByteQuintWide` by value — the three-integer-eightbyte return path under test.
public func makeByteQuintWide(b0: Int8, b1: Int8, b2: Int8, b3: Int8, b4: Int8, first: Int64, second: Int64) -> ByteQuintWide {
    return ByteQuintWide(b0: b0, b1: b1, b2: b2, b3: b3, b4: b4, first: first, second: second)
}

// MARK: - Indirect-return static factory: sret pointer survives the metatype accessor

/// A 40-byte frozen struct (five `Int64` fields) returned by value. Exceeding 32 bytes, it is returned
/// indirectly through the x8 sret buffer pointer under both conventions. Paired with the static factory
/// below, it exercises the ARM64 thunk's x8 preservation: the metatype accessor `bl` clobbers x8
/// (caller-saved), so the thunk must spill and reload it around the accessor — otherwise Swift writes
/// the result through a corrupted pointer (heap corruption / crash). Round-tripped by
/// `MixedRegisterReturnTests`.
@frozen
public struct WideQuintet {
    public var a: Int64
    public var b: Int64
    public var c: Int64
    public var d: Int64
    public var e: Int64

    public init(a: Int64, b: Int64, c: Int64, d: Int64, e: Int64) {
        self.a = a
        self.b = b
        self.c = c
        self.d = d
        self.e = e
    }

    /// Static factory returning the 40-byte struct by value. A static method needs the metatype
    /// (`Self.Type`) via the metadata accessor, whose call would clobber the live x8 sret pointer
    /// unless the thunk preserves it.
    public static func make(seed: Int64) -> WideQuintet {
        return WideQuintet(a: seed, b: seed &+ 1, c: seed &+ 2, d: seed &+ 3, e: seed &+ 4)
    }
}
