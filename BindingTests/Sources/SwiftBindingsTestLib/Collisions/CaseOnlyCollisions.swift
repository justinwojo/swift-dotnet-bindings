// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Case-only collisions
// Swift identifiers are case-sensitive and its libraries use that freely; a C# binding
// has to stay unambiguous without silently dropping the second declaration.
//
// Two distinct shapes live here:
//   1. A member-free container with nested types emits as a C# NAMESPACE, and a sibling
//      type whose name differs from it only by case reads as the same identifier.
//      (Shape observed in a document-scanning SDK, where the vendor ships both an
//      all-caps-acronym enum container and a mixed-case entry-point class.)
//   2. Two sibling properties whose Swift names differ only by case collapse onto one
//      C# identifier — a hard CS0102, which previously cost the later property its
//      binding entirely.

/// Member-free container with nested types → emits as a C# namespace.
public enum ScanKit {
    public struct Region {
        public let width: Int32
        public let height: Int32
        public init(width: Int32, height: Int32) {
            self.width = width
            self.height = height
        }
        public func area() -> Int32 { return width * height }
    }
}

/// Sibling whose name differs from the `ScanKit` namespace only by case. As a real type
/// with members it cannot become a namespace, so this one is the side that gets renamed.
public final class SCANKit {
    public init() {}
    public func describe() -> String { return "SCANKit" }
}

// MARK: - Case-only sibling properties

/// Two properties whose Swift names differ only by case both project onto `Url`.
/// The declaration-order first keeps the natural name; the second is disambiguated.
public struct EndpointSettings {
    public let url: String
    public let URL: String

    public init(url: String, URL: String) {
        self.url = url
        self.URL = URL
    }
}

// MARK: - Case-only requirements across a protocol and its conformer
// The conformer declares the SAME two requirements in the OPPOSITE order. If each side
// picked its own winner by declaration order, the interface would name `Url` for Swift
// `url` while the implementation named `Url` for Swift `URL` — and because C# matches an
// implicit interface implementation by name, that compiles and silently reads the wrong
// storage through the interface. The conformer has to adopt the requirement's name.

public protocol EndpointDescribing {
    var url: String { get }
    var URL: String { get }
}

public struct ReversedEndpoint: EndpointDescribing {
    public let URL: String
    public let url: String

    public init(url: String, URL: String) {
        self.url = url
        self.URL = URL
    }
}

// MARK: - Nested enum renamed with the kind-aware `Kind` suffix
// The enum arm of the nested-type rename scheme: a property whose type IS the sibling
// nested enum renames the ENUM (→ `Kind`, not `Info`), keeping the property name clean.

public struct TransferRecord {
    public enum Status {
        case pending
        case settled
    }

    public let status: Status
    public let amount: Int32

    public init(status: Status, amount: Int32) {
        self.status = status
        self.amount = amount
    }

    public func isSettled() -> Bool { return status == .settled }
}

// MARK: - Method colliding with its enclosing type name
// C# forbids a member named identically to its enclosing type (CS0542), so the method
// takes a `Get` prefix. `checksum` is a noun with no arguments, which ALSO makes it a
// getter-shaped candidate — the two arms must agree on one name rather than stack.

public struct Checksum {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    /// PascalCases to `Checksum`, which is the enclosing type's name.
    public func checksum() -> Int32 {
        return value
    }
}
