// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Functions Returning Closures

/// Returns an adder closure.
public func makeAdder(_ base: Int32) -> (Int32) -> Int32 {
    return { x in base + x }
}

/// Returns a multiplier closure.
public func makeMultiplier(_ factor: Int32) -> (Int32) -> Int32 {
    return { x in factor * x }
}

/// Returns a predicate closure.
public func makeGreaterThan(_ threshold: Int32) -> (Int32) -> Bool {
    return { x in x > threshold }
}

// MARK: - Closure Properties Returning Class Types

/// Holder with a closure property that returns a class.
/// Tests the C12 gate fix: closure properties returning class types were previously blocked
/// because the P/Invoke return type maps to void* but the C# delegate expects the class type.
/// The fix wraps the void* result in `new ClassName(new SwiftHandle((IntPtr)...))`.
public final class ClosureClassReturnHolder {
    private let _count: Int32

    public init(count: Int32) {
        self._count = count
    }

    /// Closure property returning a class type (non-optional).
    /// Exercises the fallback lambda class return wrapping path in EmitClosureReturnMarshalling.
    public var counterFactory: () -> FinalCounter {
        return { FinalCounter(count: self._count) }
    }

    /// Static closure property returning a class.
    /// Exercises the static-closure-property returning a class shape.
    public static var defaultCounter: () -> FinalCounter {
        return { FinalCounter(count: 0) }
    }
}

// MARK: - Returned closures whose args/returns are simple enums (invoke-thunk conversion)

/// Simple Int-raw-value enum (no payloads) — projects to a C# enum. When a returned closure
/// returns this type, the @_cdecl invoke thunk declares the underlying Int scalar, so the
/// thunk body must convert the enum case to `.rawValue`; when it is a closure *argument*, the
/// thunk must reconstruct the enum from the incoming scalar via `init(rawValue:)`.
public enum ProbeQuadrant: Int {
    case one
    case two
    case three
    case four
}

/// Payload-less enum with NO raw value (tag-only). The invoke thunk has no `.rawValue` to use,
/// so it copies the enum's tag bytes into a zero-initialised scalar (and back) — exercising the
/// tag-only branch of the invoke-thunk simple-enum conversion.
public enum ProbeMode {
    case fast
    case slow
    case idle
}

/// Frozen blittable struct used as a returned-closure argument.
@frozen
public struct ProbePoint {
    public let x: Int32
    public let y: Int32
    public init(x: Int32, y: Int32) {
        self.x = x
        self.y = y
    }
}

/// Returned closure with a by-value struct arg AND a simple-enum return. The closure parameter
/// (`log`) forces the method onto the cdecl wrapper + invoke-thunk path; the returned closure's
/// `ProbeQuadrant` return must be lowered to its Int raw value by the Swift invoke thunk
/// (`return Int64(_result.rawValue)`), and the C# invoker casts the scalar back to the enum.
/// Regression cover for the invoke-thunk simple-enum *return* conversion.
public func makeProbeClassifier(_ log: @escaping (Int32) -> Void) -> (ProbePoint) -> ProbeQuadrant {
    return { p in
        log(p.x)
        return p.x >= 0 ? .one : .two
    }
}

/// Returned closure that *takes* a simple enum and returns a primitive. The invoke thunk
/// receives the enum as its Int scalar and must reconstruct it via `init(rawValue:)` before
/// invoking the closure. Regression cover for the invoke-thunk simple-enum *argument* path.
public func makeProbeScorer(_ log: @escaping (Int32) -> Void) -> (ProbeQuadrant) -> Int32 {
    return { q in
        log(Int32(q.rawValue))
        return Int32(q.rawValue) * 10
    }
}

/// Returned closure whose argument and return are both the tag-only `ProbeMode` enum. Exercises
/// the byte-copy reconstruction/lowering of a simple enum that has no integer `.rawValue`.
public func makeProbeModeFlipper(_ log: @escaping (Int32) -> Void) -> (ProbeMode) -> ProbeMode {
    return { m in
        log(0)
        switch m {
        case .fast: return .slow
        case .slow: return .idle
        case .idle: return .fast
        }
    }
}

// MARK: - Returned closures with NO outer closure param (invoke-thunk selection parity)

/// Returned closure with a by-value struct arg AND a simple-enum return, on a method that takes
/// NO closure parameter. This is the adversarial shape for invoke-thunk *selection*: the returned
/// closure is invoke-thunk-compatible (struct arg + simple-enum return both accepted by
/// `CanUseInvokeThunk`), but there is no closure parameter forcing the outer method onto the cdecl
/// wrapper. If the method failed to route through the cdecl invoke thunk it would fall to the
/// struct-params emitter, whose return path has no simple-enum scalar→enum cast — producing
/// uncompilable C# (raw integer assigned to the enum delegate). Regression cover that a returned
/// closure of this shape selects the invoke thunk regardless of any closure parameter.
public func makeQuadrantClassifier() -> (ProbePoint) -> ProbeQuadrant {
    return { p in p.x >= 0 ? .one : .two }
}

/// Same selection probe for a tag-only enum that is both the argument and the return of the
/// returned closure, again with no outer closure parameter — exercises the byte-copy / byte-load
/// tag conversions through the invoke thunk on a method that has nothing else forcing cdecl.
public func makeModeFlipperNoLog() -> (ProbeMode) -> ProbeMode {
    return { m in
        switch m {
        case .fast: return .slow
        case .slow: return .idle
        case .idle: return .fast
        }
    }
}

// MARK: - Struct Returning Closures

/// A frozen struct with methods that return closures.
@frozen
public struct ClosureFactory {
    public let base: Int32

    public init(base: Int32) {
        self.base = base
    }

    /// Instance method returning a closure.
    public func makeTransform() -> (Int32) -> Int32 {
        return { x in self.base + x }
    }

    /// Static method returning a closure.
    public static func makeScaler(_ scale: Int32) -> (Int32) -> Int32 {
        return { x in x * scale }
    }
}
