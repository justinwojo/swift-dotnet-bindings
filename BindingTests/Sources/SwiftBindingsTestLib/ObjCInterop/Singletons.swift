// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Singleton patterns on @objc NSObject-derived classes
//
// Static getters that return the enclosing class type — the canonical Cocoa
// "shared instance" pattern. Covers three NSObject-bridging variants and one
// pure-Swift control so we have permanent coverage of the full matrix.

/// `static let` stored singleton on an `@objcMembers` NSObject subclass.
@objcMembers
public class StoredSingleton: NSObject {
    public static let shared = StoredSingleton(label: "stored")

    public let label: String

    private init(label: String) {
        self.label = label
        super.init()
    }
}

/// `static var { get }` computed singleton on an `@objcMembers` NSObject subclass.
@objcMembers
public class ComputedSingleton: NSObject {
    public static var shared: ComputedSingleton {
        return ComputedSingleton(label: "computed")
    }

    public let label: String

    private init(label: String) {
        self.label = label
        super.init()
    }
}

/// Plain Swift class (no @objc, not NSObject-derived) for control comparison.
public class PlainSwiftSingleton {
    public static let shared = PlainSwiftSingleton(label: "plain")

    public let label: String

    private init(label: String) {
        self.label = label
    }
}

/// `@objc public class` (NOT @objcMembers — each member explicitly @objc-annotated)
/// with `@objc public static let` singleton plus a sibling `@objc public static let`
/// of a different type, plus instance properties initialised in `init`. Exercises
/// the lazy `swift_once` initialiser path on a class whose ObjC bridging is
/// declared per-member rather than wholesale.
@objc public class ExplicitObjcSingleton: NSObject {
    @objc public static let errorDomain: String = "com.example.test.errordomain"
    @objc public static let shared: ExplicitObjcSingleton = ExplicitObjcSingleton()

    @objc public var apiClient: NSObject
    @objc public var simulateRedirect: Bool

    public override init() {
        self.apiClient = NSObject()
        self.simulateRedirect = false
        super.init()
    }
}
