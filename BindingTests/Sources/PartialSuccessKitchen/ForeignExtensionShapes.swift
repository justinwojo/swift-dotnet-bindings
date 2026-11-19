// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Members this module adds to other modules' types in shapes the foreign-extension path does not
// bind. Each public member below is bound or keeps a skip row of its own; none may vanish.

// A public overload the ABI omits (`@_alwaysEmitIntoClient`) whose only ABI sibling of the same
// name and labels is internal. The internal sibling is not public surface and gets no row, so it
// never stands for the public member.
extension Date {
    @_alwaysEmitIntoClient public func kitchenCarriedOverload(x: Int32) -> Int32 { x }
    @usableFromInline internal func kitchenCarriedOverload(x: (Int32, Int32)) -> Int32 { x.0 }

    // An initializer the ABI omits: no ABI path sees it, and the interface reader does not take
    // initializers as extension members.
    @_alwaysEmitIntoClient public init(kitchenSeconds: Int32) {
        self.init(timeIntervalSince1970: Double(kitchenSeconds))
    }
}

// Two public static overloads of one name and labels. The ABI lists only the `class func`; the
// other is emitted into its client. A tuple cannot be another spelling of `Int32`, so they are two
// members, and each keeps its own row.
extension NSObject {
    @_alwaysEmitIntoClient public static func kitchenCarriedOverload(x: Int32) -> Int32 { x }
    public class func kitchenCarriedOverload(x: (Int32, Int32)) -> Int32 { x.0 }

    // A subscript the ABI omits, on a receiver the parser drops.
    @_alwaysEmitIntoClient public subscript(kitchenIndex index: Int32) -> Int32 { index }
}

// Two overloads of one name on a receiver the parser drops: it parks the one the ABI lists, and
// the other, emitted into its client, is reported by the interface reader. Labels tell them apart.
@available(iOS 15, macOS 12, tvOS 15, *)
extension AttributeScopes {
    public static func kitchenParked(a: Int32) -> Int32 { a }
    @_alwaysEmitIntoClient public static func kitchenParked(b: Int32) -> Int32 { b }
}
