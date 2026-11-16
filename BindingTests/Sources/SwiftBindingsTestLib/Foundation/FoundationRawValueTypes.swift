// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Foundation raw-value types on every route
//
// RunLoop.Mode is an NSString typedef Swift imports as a struct over its string; the binding carries
// it as NSString. ObjCBool is one byte, a Bool on iOS, tvOS and arm64 macOS and an Int8 on Mac
// Catalyst and x86_64 macOS; the binding carries it as bool.
//
// The plain struct reaches Swift through @_cdecl wrappers. A wrapper cannot be generic over the
// box's parameters, so its members take three routes:
// - `echoMode` is a direct Swift-convention import that a native bridge carries.
// - `flipped` is a direct import with no bridge: ObjCBool has no layout every target shares, so the
//   bridge plan is refused and the member is guarded on the engines that need a bridge.
// - `name(ofMode:)` is bound through a wrapper specialized for the concrete arguments the fixture
//   exercises (KeyedToken, DirectTransitionFailure), as an extension method.

public struct FoundationRawValues {
    public init() {}

    /// The mode it was passed.
    public func echoMode(_ mode: RunLoop.Mode) -> RunLoop.Mode { mode }

    /// `.common`, built in Swift.
    public func commonMode() -> RunLoop.Mode { .common }

    /// The mode's raw value.
    public func name(ofMode mode: RunLoop.Mode) -> String { mode.rawValue }

    /// The flag negated.
    public func flipped(_ flag: ObjCBool) -> ObjCBool { ObjCBool(!flag.boolValue) }

    /// The flag as a Swift Bool.
    public func isSet(_ flag: ObjCBool) -> Bool { flag.boolValue }
}

/// The same members on a struct generic over a protocol-constrained parameter and an Error.
public struct FoundationRawValueBox<Element: KeyedValue, Failure: Error> {
    var element: Element

    public init(element: Element) { self.element = element }

    /// The mode it was passed.
    public func echoMode(_ mode: RunLoop.Mode) -> RunLoop.Mode { mode }

    /// The mode's raw value followed by the element's key.
    public func name(ofMode mode: RunLoop.Mode) -> String { "\(mode.rawValue)#\(element.key)" }

    /// The flag negated when the element's key is odd.
    public func flipped(_ flag: ObjCBool) -> ObjCBool { ObjCBool(element.key % 2 == 1 ? !flag.boolValue : flag.boolValue) }
}
