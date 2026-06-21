// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Reverse-path error carriage (C# exception -> Swift NSError userInfo)
//
// When a C#-implemented throwing closure throws, the generated C# callback must not let
// the managed exception unwind into native Swift (that aborts the process). Instead it
// mints a Swift Error via SBW_CreateError_{module}(message, managedTypeName): an NSError
// in domain "SwiftBindings" whose userInfo carries the originating .NET exception's CLR
// type name under "SwiftBindingsManagedExceptionType", and its message under
// NSLocalizedDescriptionKey. These fixtures call such a closure, catch the minted error
// on the Swift side, and surface the recovered identity back to C# so a round-trip test
// can assert the managed exception's type AND message survived the boundary — not just a
// flattened, identity-less description.

/// Invokes a throwing C# closure. On error, returns the recovered .NET exception type name
/// from the minted NSError's userInfo; "<no-managed-type>" if the error carried none (or was
/// not an NSError), and "<no-throw>" if the closure did not throw.
public func recoverManagedExceptionType(_ callback: @escaping () throws -> Void) -> String {
    do {
        try callback()
        return "<no-throw>"
    } catch {
        let nsError = error as NSError
        if let managedType = nsError.userInfo["SwiftBindingsManagedExceptionType"] as? String {
            return managedType
        }
        return "<no-managed-type>"
    }
}

/// Sibling that returns the minted error's localizedDescription — the C# exception's
/// `.Message` round-tripped through NSLocalizedDescriptionKey — or "<no-throw>".
public func recoverManagedExceptionMessage(_ callback: @escaping () throws -> Void) -> String {
    do {
        try callback()
        return "<no-throw>"
    } catch {
        return (error as NSError).localizedDescription
    }
}
