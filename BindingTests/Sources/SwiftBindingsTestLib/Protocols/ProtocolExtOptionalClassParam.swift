// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Reproduces the RealityFoundation `Entity.setParent(_:preservingWorldTransform:)`
// shape: a protocol-extension method that takes an Optional<Class> parameter.
// Before the ProtocolExtensionEmitter fix, the wrapper rendered the param as
// bare `Optional<Parent>`; swiftc rejected the `@_cdecl` because Optional<Class>
// isn't ObjC-representable. The fix maps it to `UnsafeMutableRawPointer?` and
// reconstructs via `Unmanaged<AnyObject>.fromOpaque(...).map`.
//
// The protocol-extension gate only emits void/primitive returns. Round-trip
// observability comes from the parent class capturing what attach() saw.

public final class PExtOptParent {
    public var lastAttachedChildId: Int32 = -1
    public init() { }
}

public protocol PExtOptChildProtocol {
    var nodeId: Int32 { get }
}

extension PExtOptChildProtocol {
    public func attachTo(_ parent: PExtOptParent?) -> Bool {
        if let parent = parent {
            parent.lastAttachedChildId = nodeId
            return true
        }
        return false
    }
}

public final class PExtOptChild: PExtOptChildProtocol {
    public let nodeId: Int32
    public init(nodeId: Int32) { self.nodeId = nodeId }
}
