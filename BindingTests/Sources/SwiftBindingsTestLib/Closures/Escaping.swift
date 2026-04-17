// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Escaping Closure Free Functions

/// Calls an escaping closure with an Int32 value.
public func callWithInt32(_ callback: @escaping (Int32) -> Int32) -> Int32 {
    return callback(42)
}

/// Calls an escaping void closure.
public func callVoidCallback(_ callback: @escaping () -> Void) {
    callback()
}

/// Calls an escaping closure with multiple arguments.
public func callMultiArg(_ callback: @escaping (Int32, Int32) -> Int32) -> Int32 {
    return callback(10, 20)
}

/// Calls an escaping closure with a Bool argument.
public func callBoolCallback(_ callback: @escaping (Bool) -> Bool) -> Bool {
    return callback(true)
}

/// Calls an escaping closure with a FrozenPoint argument.
public func callWithFrozenStruct(_ callback: @escaping (FrozenPoint) -> Double) -> Double {
    let point = FrozenPoint(x: 3.0, y: 4.0)
    return callback(point)
}

/// Calls a closure with a Double argument.
public func callDoubleCallback(_ callback: @escaping (Double) -> Double) -> Double {
    return callback(3.14159)
}

// MARK: - Struct with Closure Methods

/// A frozen struct with instance and static methods accepting closures.
@frozen
public struct ClosureConsumer {
    public let multiplier: Int32

    public init(multiplier: Int32) {
        self.multiplier = multiplier
    }

    /// Instance method that accepts a closure.
    public func applyToValue(_ value: Int32, using transform: @escaping (Int32) -> Int32) -> Int32 {
        return transform(value * multiplier)
    }

    /// Static method that accepts a closure.
    public static func processWithClosure(_ value: Int32, closure: @escaping (Int32) -> Int32) -> Int32 {
        return closure(value)
    }
}

/// Calls the closure multiple times and sums the results.
public func callMultipleTimes(_ callback: @escaping (Int32) -> Int32, times: Int32) -> Int32 {
    var sum: Int32 = 0
    for i in 1...times {
        sum += callback(Int32(i))
    }
    return sum
}

// MARK: - B7 Closure Return Tests (Optional<String> and [String])

/// Calls an escaping closure that returns Optional<String>.
/// Used to test B7 gate lift for String in container return types.
public func callWithOptionalStringReturn(_ handler: @escaping (Int32) -> String?) -> String? {
    return handler(42)
}

/// Calls an escaping closure that returns [String].
/// Used to test B7 gate lift for String in array return types.
public func callWithStringArrayReturn(_ handler: @escaping (Int32) -> [String]) -> [String] {
    return handler(3)
}

// MARK: - P1: Nullable Closure Property (Lottie AnimationLoaded pattern)

/// Class with an optional closure stored property.
/// Tests nullable closure marshalling: setter handles both non-null and null.
public class ClosureHolder {
    public var onValueChanged: ((Int32) -> Void)?

    public init() {
        self.onValueChanged = nil
    }

    public func triggerChange(value: Int32) {
        onValueChanged?(value)
    }
}

// MARK: - P2: Static Closure Property (NVActivityIndicatorView pattern)

/// Class with a static optional closure property.
/// Combines static property access with closure marshalling.
public class LogRouter {
    public static var logHandler: ((String) -> Void)?

    public static func route(message: String) {
        logHandler?(message)
    }
}

// MARK: - P3: Optional Closure Parameter (Mixpanel Flush pattern)

/// Free function taking an optional closure parameter.
/// Tests Optional<Closure> parameter marshalling.
public func executeIfPresent(action: (() -> Void)?, fallbackValue: Int32) -> Int32 {
    if let action = action {
        action()
        return 1
    }
    return fallbackValue
}

// MARK: - X2: Method with Multiple Closure Parameters (StripePayments pattern)

/// Free function with two closure parameters (one void, one with arg).
public func executeWithCallbacks(
    onStart: @escaping () -> Void,
    onComplete: @escaping (Int32) -> Void) {
    onStart()
    onComplete(42)
}

// MARK: - Optional<Primitive/Enum> Closure Parameters

/// Calls a closure with an Optional<Int32> parameter (value present).
public func callWithOptionalInt(_ callback: @escaping (Int32?) -> Int32) -> Int32 {
    return callback(42)
}

/// Calls a closure with an Optional<Int32> parameter (nil).
public func callWithNilInt(_ callback: @escaping (Int32?) -> Int32) -> Int32 {
    return callback(nil)
}

/// Calls a closure with an Optional<Bool> parameter (value present).
public func callWithOptionalBool(_ callback: @escaping (Bool?) -> Bool) -> Bool {
    return callback(true)
}

/// Calls a closure with an Optional<Bool> parameter (nil).
public func callWithNilBool(_ callback: @escaping (Bool?) -> Bool) -> Bool {
    return callback(nil)
}

/// Calls a closure with an Optional<Color> (simple enum) parameter (value present).
public func callWithOptionalEnum(_ callback: @escaping (Color?) -> Int32) -> Int32 {
    return callback(.blue)
}

/// Calls a closure with an Optional<Color> (simple enum) parameter (nil).
public func callWithNilEnum(_ callback: @escaping (Color?) -> Int32) -> Int32 {
    return callback(nil)
}

/// Calls a closure with an Optional<Double> parameter.
public func callWithOptionalDouble(_ callback: @escaping (Double?) -> Double) -> Double {
    return callback(3.14)
}

/// Calls a closure with an Optional<FrozenPoint> parameter (value present).
/// Exercises Fix 11B: nil-for-none pointer ABI for Optional<FrozenStruct>.
public func callWithOptionalFrozenStruct(_ callback: @escaping (FrozenPoint?) -> Double) -> Double {
    return callback(FrozenPoint(x: 3.0, y: 4.0))
}

/// Calls a closure with an Optional<FrozenPoint> parameter (nil).
/// Exercises Fix 11B: nil-for-none pointer ABI returning nil to C#.
public func callWithNilFrozenStruct(_ callback: @escaping (FrozenPoint?) -> Double) -> Double {
    return callback(nil)
}

// MARK: - Closure with Existential Array Parameter (Swinject Container pattern)
// Regression test: SwiftArray<ExistentialContainer1> type init must not throw
// TypeInitializationException when NativeAotInitialize() fails for existential types.

/// Class with init taking a closure alongside an existential array parameter.
/// Mirrors Swinject Container(behaviors: [any Behavior], registerClosure: ...) pattern.
public class ClosureWithExistentialArray {
    private let modes: [any ProcessingMode]
    private let transformResult: Int32

    public init(modes: [any ProcessingMode], transform: @escaping (Int32) -> Int32) {
        self.modes = modes
        self.transformResult = transform(Int32(modes.count))
    }

    public func getModeCount() -> Int32 { Int32(modes.count) }
    public func getTransformResult() -> Int32 { transformResult }
}

// MARK: - Setter-Only Closure Properties

/// Class with closure properties whose parameter types can't be marshalled for C# invocation.
/// The generator emits these as setter-only: C# can set callback handlers but can't get/invoke.
/// Mirrors Alamofire.ClosureEventMonitor pattern (closures with ObjC-bridgeable/complex params).
public class SetterOnlyCallbackHolder {
    /// Closure with a protocol existential parameter (non-invocable from C#).
    /// The generator emits this as set-only because CanInvokeFromCSharp rejects existential params
    /// (they aren't NamedTypeSpec, so they fall through all IsInvocableParameter checks).
    public var onConfigChanged: ((any ProcessingMode) -> Void)?

    /// Verification: trigger the callback from Swift side.
    public func notifyConfigChanged() {
        let mode = SimpleMode()
        onConfigChanged?(mode)
    }

    public init() {}
}

// MARK: - Throwing Closures (REMOVED)
// Throwing closures cause emission errors (SwiftString→void* return mismatch in thunks).
// Known generator limitation. ClosureError enum also removed to avoid orphan type.

// MARK: - Existential Closure Parameters (Fix 11A / Fix 11C)
// Exercises `(any Protocol) -> Void` closure parameters through the Cdecl wrapper path.
// The Swift adapter heap-allocates an ExistentialContainer for the existential arg and
// passes an UnsafeMutableRawPointer to the C# callback, which dereferences it back into
// a protocol proxy instance.

/// Calls an escaping closure that receives `any ProcessingMode` and invokes validate().
/// Single-protocol existential closure param (Fix 11A).
public func callWithExistentialCallback(_ callback: @escaping (any ProcessingMode) -> Bool) -> Bool {
    return callback(SimpleMode())
}

/// Calls an escaping closure that receives `any ProcessingMode` and invokes it multiple times.
/// Verifies the adapter properly reallocates the existential buffer per invocation.
public func callExistentialCallbackTwice(_ callback: @escaping (any ProcessingMode) -> Bool) -> Bool {
    let a = callback(SimpleMode())
    let b = callback(StrictMode())
    return a && b
}

/// Multi-closure method mixing an existential closure with a primitive closure (Fix 11C).
/// Both closures must be independently Cdecl-compatible for the `.All()` gate to accept.
public func callWithMixedCallbacks(
    onMode: @escaping (any ProcessingMode) -> Bool,
    onValue: @escaping (Int32) -> Int32) -> Int32 {
    let modeResult = onMode(SimpleMode()) ? Int32(1) : Int32(0)
    let valueResult = onValue(41)
    return modeResult + valueResult
}

// MARK: - Non-Frozen Struct Closure Parameter (StoreKit2 Storefront pattern)
// Non-frozen structs are opaque-payload class-backed types on the C# side. The
// Swift adapter heap-allocates the struct via initializeMemory (VWT copy) and
// transfers ownership of that buffer to C#. The C# callback wraps the pointer
// with MarshalFromSwift<T>; SwiftSafeHandle.ReleaseHandle pairs VWT.Destroy
// with NativeMemory.Free on dispose/finalize, so the wrapper is free to escape
// the callback.

/// A non-frozen struct carrying a String field (ARC-owning payload, non-trivial VWT).
public struct NonFrozenInfo {
    public let label: String
    public let value: Int32

    public init(label: String, value: Int32) {
        self.label = label
        self.value = value
    }
}

/// Calls an escaping closure with a non-frozen struct argument.
/// Exercises the cdecl adapter's heap-alloc + ownership-transfer path for
/// non-frozen structs (StoreKit2 `onStorefrontChange((Storefront) -> Bool)` pattern).
public func callWithNonFrozenStruct(_ callback: @escaping (NonFrozenInfo) -> Int32) -> Int32 {
    let info = NonFrozenInfo(label: "nonfrozen", value: 7)
    return callback(info)
}

