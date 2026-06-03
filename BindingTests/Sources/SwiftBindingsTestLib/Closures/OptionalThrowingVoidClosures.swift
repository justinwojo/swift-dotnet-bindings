// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Optional Throwing-Void Closure Parameters (Alamofire / YouTubePlayerKit shape)
//
// Regression guard for REMEDIATION-PLAN §6: an *optional* escaping throwing closure
// returning Void as a method/init parameter — `((T) throws -> Void)? = nil`. The C#
// binding emits a throwing-closure callback whose catch block mints a Swift error via
// the per-module helper `SBW_CreateError_{module}`. When that helper went unregistered
// by wrapper-emit — on the native `_optbuf`/default-parameter/non-optional-setter paths
// that forward the closure to Swift without funneling through the closure-adapter — the
// wrapper-symbol contract gate rejected the callback's P/Invoke, the co-gater stripped
// the `[UnmanagedCallersOnly]` callback method, and its one-line `s_<cb> = &<cb>` field
// plus the `new SwiftClosureData((IntPtr)s_<cb>, …)` call-site dangled → CS0103
// "name does not exist." These shapes broke Alamofire (`Session.upload(…requestModifier:)`)
// and YouTubePlayerKit (`init(htmlProvider:)` + the `HtmlProvider` setter) at the
// compile gate. This fixture is the durable in-repo reproduction across a free-function
// parameter, an initializer parameter, a default-valued non-optional parameter, and both
// optional and non-optional settable properties — every bypass site the handler-layer
// error-mint registration now covers.
//
// NOTE: the struct-argument shape `(RequestConfig) throws -> Void` is used only on the
// *parameter* direction (free function + initializer), where the C# delegate is marshalled
// TO Swift. Settable closure *properties* additionally generate a getter-invoker that reads
// the closure back FROM Swift; the throwing getter-invoker does not yet marshal by-value
// struct arguments (logged in REMEDIATION-PLAN §6 as a separate, different-shape defect), so
// the property shapes here use the arg-free `() throws -> Void` form to stay focused on the
// wrapper-symbol regression this fixture exists to gate.

/// Stand-in value type for the closure's input (URLRequest / HtmlProvider).
public struct RequestConfig {
    public var timeout: Int32
    public init(timeout: Int32) {
        self.timeout = timeout
    }
}

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
}
