// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
#if canImport(UIKit)
import UIKit
#endif

// MARK: - Ownership of NSString-typedef results on every call route
//
// RunLoop.Mode, UIFont.TextStyle and UIContentSizeCategory are structs over one NSString, and the
// binding carries them as NSString. Swift returns them @owned: a native call hands back the string
// at +1 (SIL `-> @owned RunLoop.Mode`, IR `ptr`), a @_cdecl wrapper hands it back retained, and a
// wrapper that returns through a result buffer leaves the +1 in the buffer. Parameters are
// @guaranteed, so the callee takes no reference from the caller.
//
// The managed side must adopt that +1 exactly once on every route: a missing adoption leaks one
// retain per call, and a second one over-releases. Each echo returns the very object it was given,
// so a caller can count the object's retains before and after.

/// A resilient struct: its methods reach Swift through native thunks.
public struct StringTypedefOwnershipHolder {
    public init() {}

    public func echoMode(_ mode: RunLoop.Mode) -> RunLoop.Mode { mode }

    /// A String parameter sends this member through a @_cdecl wrapper, which returns the mode
    /// through a result buffer.
    public func echoMode(_ mode: RunLoop.Mode, tag: String) -> RunLoop.Mode { tag.isEmpty ? .default : mode }

    /// A new string built in Swift, so its only owner after the call is the caller.
    public func freshMode(_ suffix: String) -> RunLoop.Mode { RunLoop.Mode("StringTypedefOwnership-\(suffix)-ü") }

    public static func echoModeStatic(_ mode: RunLoop.Mode) -> RunLoop.Mode { mode }

    #if canImport(UIKit)
    public func echoTextStyle(_ style: UIFont.TextStyle) -> UIFont.TextStyle { style }

    public func echoCategory(_ category: UIContentSizeCategory) -> UIContentSizeCategory { category }
    #endif
}

public final class StringTypedefOwnershipBox {
    public init() {}

    public func echoMode(_ mode: RunLoop.Mode) -> RunLoop.Mode { mode }
}

/// Generic over an unconstrained parameter: the wrapper returns the mode through a result buffer.
public struct StringTypedefOwnershipGenericBox<T> {
    var item: T

    public init(item: T) { self.item = item }

    public func echoMode(_ mode: RunLoop.Mode) -> RunLoop.Mode { mode }
}

public func stringTypedefOwnershipEchoMode(_ mode: RunLoop.Mode) -> RunLoop.Mode { mode }

#if canImport(UIKit)
public func stringTypedefOwnershipEchoTextStyle(_ style: UIFont.TextStyle) -> UIFont.TextStyle { style }
#endif
