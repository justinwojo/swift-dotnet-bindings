// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Optional Throwing-Void Closure Parameters (Alamofire / YouTubePlayerKit shape)
//
// Regression guard for the optional escaping throwing closure returning Void as a
// method/init parameter — `((T) throws -> Void)? = nil`. The C# binding emits a
// throwing-closure callback whose catch block mints a Swift error via the per-module
// helper `SBW_CreateError_{module}`. When that helper went unregistered by wrapper-emit
// — on the native `_optbuf`/default-parameter/non-optional-setter paths that forward the
// closure to Swift without funneling through the closure-adapter — the wrapper-symbol
// contract gate rejected the callback's P/Invoke, the co-gater stripped the
// `[UnmanagedCallersOnly]` callback method, and its one-line `s_<cb> = &<cb>` field
// plus the `new SwiftClosureData((IntPtr)s_<cb>, …)` call-site dangled → CS0103
// "name does not exist." These shapes broke Alamofire (`Session.upload(…requestModifier:)`)
// and YouTubePlayerKit (`init(htmlProvider:)` + the `HtmlProvider` setter) at the
// compile gate. This fixture is the durable in-repo reproduction across a free-function
// parameter, an initializer parameter, a default-valued non-optional parameter, and both
// optional and non-optional settable properties — every bypass site the handler-layer
// error-mint registration now covers.
//
// NOTE: the struct-argument shape `(RequestConfig) throws -> Void` is exercised on BOTH the
// *parameter* direction (free function + initializer), where the C# delegate is marshalled
// TO Swift, AND the *return* direction (the `configValidator` gettable property below), where
// the closure is read back FROM Swift and its by-value struct argument is marshalled TO Swift
// through the func-ptr each time the returned delegate is invoked. The return direction was
// a throwing-closure RETURN defect: the invoker emitted a bare `_arg0` struct value into a
// `void*`/struct-pointer func-ptr slot (CS1503 at the compile gate). It now emits the same
// metadata + buffer + MarshalToSwift prologue the non-throwing struct-param closure paths
// use, so `configValidator` is the durable gate for that fix.

/// Stand-in value type for the closure's input (URLRequest / HtmlProvider).
public struct RequestConfig {
    public var timeout: Int32
    public init(timeout: Int32) {
        self.timeout = timeout
    }
}

// The `configValidator` closure throws `ConfigError.invalidTimeout` (declared in
// Initializers/Throwing.swift) for a non-positive timeout — driving the throwing branch of the
// throwing-closure RETURN invoker.

/// Free function with an OPTIONAL throwing-void closure parameter defaulting to nil —
/// the Alamofire `Session.upload(…requestModifier:)` shape (the `_optbuf` native-forward
/// path). Returns true iff the closure was supplied and ran without throwing.
public func runWithOptionalModifier(
    timeout: Int32,
    modifier: ((RequestConfig) throws -> Void)? = nil
) -> Bool {
    guard let modifier = modifier else { return false }
    do {
        try modifier(RequestConfig(timeout: timeout))
        return true
    } catch {
        return false
    }
}

/// Holds throwing-void closures provided at construction and via settable properties —
/// the YouTubePlayerKit `init(htmlProvider:)` + `HtmlProvider` setter shapes.
public final class OptionalThrowingModifierHolder {
    /// Settable OPTIONAL throwing-void closure property (Optional closure-setter branch).
    public var onComplete: (() throws -> Void)?

    /// Settable NON-OPTIONAL throwing-void closure property — the actual YouTubePlayerKit
    /// `HtmlProvider` setter bypass site (PropertyWrapperEmitter's non-optional
    /// closure-setter branch, outside the adapter funnel).
    public var validator: () throws -> Void

    /// Initializer taking an OPTIONAL throwing-void closure with a struct argument and a
    /// default of nil (YouTubePlayerKit `init(htmlProvider:)` shape, the `_optbuf` path),
    /// plus a default-valued NON-optional throwing-void closure exercising the
    /// default-parameter shim bypass.
    public init(
        validator: @escaping () throws -> Void = { },
        modifier: ((RequestConfig) throws -> Void)? = nil
    ) {
        self.validator = validator
        self.storedModifier = modifier
    }

    /// The init-supplied optional modifier, kept private so it is invoked from Swift rather
    /// than read back through the (separately-tracked) throwing getter-invoker.
    private var storedModifier: ((RequestConfig) throws -> Void)?

    /// Runs the init-supplied optional modifier against a fresh config; true iff present and
    /// non-throwing.
    public func runStoredModifier(timeout: Int32) -> Bool {
        guard let modifier = storedModifier else { return false }
        do {
            try modifier(RequestConfig(timeout: timeout))
            return true
        } catch {
            return false
        }
    }

    /// Runs the stored non-optional validator; true iff non-throwing.
    public func runValidator() -> Bool {
        do {
            try validator()
            return true
        } catch {
            return false
        }
    }

    /// Runs the stored optional onComplete closure; true iff present and non-throwing.
    public func runOnComplete() -> Bool {
        guard let onComplete = onComplete else { return false }
        do {
            try onComplete()
            return true
        } catch {
            return false
        }
    }

    /// Last `timeout` observed by a successful `configValidator` invocation — lets a C# caller
    /// confirm the by-value `RequestConfig` struct argument round-tripped TO Swift through the
    /// returned throwing closure's func-ptr.
    public var lastObservedTimeout: Int32 = 0

    /// GETTABLE throwing-closure property whose closure takes a by-value struct argument and throws.
    /// Reading this property hands C# a delegate backed by a Swift func-ptr; each C# invocation
    /// marshals the `RequestConfig` struct TO Swift through that func-ptr (the throwing-return
    /// invoker previously emitted a bare struct value into a `void*` func-ptr slot → CS1503). The
    /// closure throws for a non-positive timeout and otherwise records the timeout so the
    /// round-trip is observable.
    public var configValidator: (RequestConfig) throws -> Void {
        return { config in
            if config.timeout <= 0 {
                throw ConfigError.invalidTimeout
            }
            self.lastObservedTimeout = config.timeout
        }
    }

    /// NON-THROWING gettable closure with a by-value NON-FROZEN struct argument and a primitive
    /// return — the non-throwing twin of `configValidator`. The fix routes struct-arg closure RETURNS
    /// through the @_cdecl invoke thunk for BOTH throwing and non-throwing closures, so the
    /// non-throwing struct-arg return no longer takes the raw `delegate* unmanaged[Swift]` lambda
    /// struct path (an untested latent SIGSEGV on Mono JIT / NativeAOT). Echoes the timeout so the
    /// `RequestConfig` round-trip TO Swift is observable from the returned `Int32`.
    public var configEcho: (RequestConfig) -> Int32 {
        return { config in config.timeout }
    }

    /// NON-THROWING gettable closure with a by-value `@frozen` struct argument — exercises the
    /// invoke thunk's frozen-struct marshalling branch (C# stackalloc + MarshalToSwift) rather than
    /// the non-frozen InitializeWithCopy branch `configEcho` drives. Returns the sum of the point's
    /// coordinates so the `FrozenPoint` round-trip TO Swift is observable.
    public var pointEcho: (FrozenPoint) -> Double {
        return { point in point.x + point.y }
    }
}
