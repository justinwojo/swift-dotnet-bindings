// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - KeyPath foundation fixtures
//
// Exercises the five-class KeyPath family (AnyKeyPath, PartialKeyPath, KeyPath,
// WritableKeyPath, ReferenceWritableKeyPath) at the @_cdecl boundary. Covers:
//   - OUT path: factory returns a +1 retained KeyPath; C# adopts via SafeHandle.
//   - IN path: C# passes a borrowed KeyPath; Swift consumes via `subscript(keyPath:)`.
//   - Optional<KeyPath>: nil and non-nil round-trip.
//   - [KeyPath]: array of class-typed elements.
//   - Round-trip: pass in, return same; value-equality (AnyKeyPath.==), not pointer
//     identity, is the contract.
//
// All five KeyPath classes share the single-pointer ABI; the test C# wrappers in
// Swift.Runtime/Swift/SwiftKeyPath.cs derive from a common SafeHandle and the
// generator's KeyPathProjection lowers parameters/returns to IntPtr.

// `@frozen` so PointKP projects to a blittable C# struct, which lets the
// `inout PointKP` parameter on `KeyPathConsumer.writeInt` flow through the
// generator's UnsafeMutableRawPointer write-back path. A non-frozen struct
// projects to a SafeHandle-backed C# class; the wrapper emitter rejects
// `inout` of class-projected types (WrapperValidation.HasInoutWithAbiMismatch),
// which would force the broken `tFZ` mangled-name fallback for the class method.
@frozen public struct PointKP {
    public var x: Int
    public var y: Int
    public init(x: Int, y: Int) { self.x = x; self.y = y }
}

public class BoxKP {
    public var n: Int = 0
    public var label: String = ""
    public init() {}
}

public class KeyPathFactory {
    // OUT — fully typed KeyPath
    public class func makePointXPath() -> KeyPath<PointKP, Int> { \PointKP.x }
    public class func makePointYPath() -> KeyPath<PointKP, Int> { \PointKP.y }

    // OUT — WritableKeyPath subclass (declared return type drives static C# type)
    public class func makeWritablePointXPath() -> WritableKeyPath<PointKP, Int> { \PointKP.x }

    // OUT — ReferenceWritableKeyPath
    public class func makeReferenceWritableBoxNPath() -> ReferenceWritableKeyPath<BoxKP, Int> { \BoxKP.n }

    // OUT — PartialKeyPath upcast
    public class func makePartialPointXPath() -> PartialKeyPath<PointKP> { \PointKP.x }

    // OUT — AnyKeyPath upcast
    public class func makeAnyPointXPath() -> AnyKeyPath { \PointKP.x }

    // OUT — Optional<KeyPath>
    public class func maybePath(_ make: Bool) -> KeyPath<PointKP, Int>? {
        make ? \PointKP.x : nil
    }

    // OUT — Array<KeyPath>
    public class func allPointPaths() -> [KeyPath<PointKP, Int>] {
        [\PointKP.x, \PointKP.y]
    }
}

public class KeyPathConsumer {
    // IN — read through a KeyPath
    public class func readInt(from p: PointKP, by kp: KeyPath<PointKP, Int>) -> Int {
        return p[keyPath: kp]
    }

    // IN — WritableKeyPath assigns into a value-type field through the KP subscript.
    //
    // Returns the mutated copy rather than using `inout`. This is a fixture-scope choice,
    // not a generator gap: the `inout`-of-blittable-frozen-struct path now round-trips
    // end-to-end on the cdecl boundary — the generated C# call site emits a `ref` param and
    // reads the mutation back after the P/Invoke (proven by `ParameterTests.TestIncrementPoint`
    // against `incrementPoint(_:)`). The returning-mutated-copy shape keeps this KeyPath fixture
    // focused on the WritableKeyPath contract (assignment through the KP subscript) without
    // also re-exercising inout writeback, which has its own dedicated coverage.
    public class func writeInt(into p: PointKP, by kp: WritableKeyPath<PointKP, Int>, value: Int) -> PointKP {
        var copy = p
        copy[keyPath: kp] = value
        return copy
    }

    // IN — ReferenceWritableKeyPath mutates a reference-type property in place
    public class func writeIntRef(into b: BoxKP, by kp: ReferenceWritableKeyPath<BoxKP, Int>, value: Int) {
        b[keyPath: kp] = value
    }

    // Round-trip: identity pass-through. Constraint #21 — when a method returns a
    // reference *to* its argument, the wrapper must Arc.Retain before return so the
    // caller receives a +1.
    public class func roundTrip(_ kp: KeyPath<PointKP, Int>) -> KeyPath<PointKP, Int> { kp }

    // Equality via AnyKeyPath.==
    public class func samePath(_ a: AnyKeyPath, _ b: AnyKeyPath) -> Bool { a == b }

    // Optional<KeyPath> in — read with a default of -1
    public class func readOrDefault(from p: PointKP, by kp: KeyPath<PointKP, Int>?, defaultValue: Int) -> Int {
        guard let kp else { return defaultValue }
        return p[keyPath: kp]
    }
}
