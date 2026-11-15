// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Foundation raw-value types on every route
//
// RunLoop.Mode is an NSString typedef Swift imports as a struct over its string, and ObjCBool one
// byte. The platform binding's NSRunLoopMode is an int enum of the constants, and the old ObjCBool
// carrier an eight-byte nint, so a binding passed an int where Swift reads an object and read seven
// bytes past a one-byte result. They project onto NSString and bool.
//
// The plain struct reaches Swift through @_cdecl wrappers; the generic box through direct
// Swift-convention transitions that a native bridge carries.

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
