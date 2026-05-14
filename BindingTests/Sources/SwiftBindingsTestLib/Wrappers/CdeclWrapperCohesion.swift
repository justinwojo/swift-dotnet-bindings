// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Maximum-case fixture for the `@_cdecl` wrapper cohesion gates that broke
// downstream in 0.11.0 across RealityFoundation, RealityKit, Kingfisher,
// Alamofire, RxSwift, Starscream, and Swinject. Two intertwined symptoms:
//
//  1. Inherited instance method takes / returns an Optional<Class> and the
//     `@_cdecl` wrapper must render it as `UnsafeMutableRawPointer?` rather
//     than the bare `Optional<Base>`. Reproduces RealityFoundation's
//     `Entity.setParent(_:preservingWorldTransform:)` siblings on the
//     subclass-iteration path.
//
//  2. A protocol extension default method is reachable through both
//     `ProtocolExtensionEmitter` (synthesised onto the conforming type) and
//     the standard `MethodHandler -> MethodWrapperEmitter` pipeline. Without
//     cross-emitter symbol canonicalisation, both fire and the wrapper file
//     ends up with duplicate `@_cdecl` blocks for the same underlying Swift
//     method. The Bool-bearing extension exercises the Int8 bridge variant
//     and a sibling non-Bool extension confirms the dedup doesn't over-fire
//     across distinct overloads.

public class WrapperCohesionBase {
    public var lastSeenChildId: Int32 = -1
    public var stashedSibling: WrapperCohesionBase? = nil
    public let nodeId: Int32

    public init(nodeId: Int32) {
        self.nodeId = nodeId
    }

    // Optional<Class> parameter — sibling-iteration trigger on subclasses.
    public func attach(_ other: WrapperCohesionBase?) -> Bool {
        if let other = other {
            self.lastSeenChildId = other.nodeId
            return true
        }
        return false
    }

    // Optional<Class> return — round-tripped through the same wrapper path.
    public func detach() -> WrapperCohesionBase? {
        let prior = stashedSibling
        stashedSibling = nil
        return prior
    }

    // Setter sibling for the optional return: lets the runtime test stage a
    // sibling so `detach()` can return non-nil on demand.
    public func stash(_ other: WrapperCohesionBase?) {
        self.stashedSibling = other
    }
}

public final class WrapperCohesionLeft: WrapperCohesionBase {
    public var leftMark: Int32 = 0
}

public final class WrapperCohesionRight: WrapperCohesionBase {
    public var rightMark: Int32 = 0
}

// Class-only protocol so the conforming type is a class (mirrors Kingfisher's
// Builder shape) and the protocol-extension methods can mutate the receiver
// without a `mutating` keyword.
public protocol WrapperCohesionBuildable: AnyObject {
    var stepCounter: Int32 { get set }
    var strideCounter: Int32 { get set }
}

extension WrapperCohesionBuildable {
    // Bool-bearing extension method — exercises the @_cdecl Int8 bridge.
    // Reachable through `ProtocolExtensionEmitter` (synthesised onto the
    // conforming class) AND the standard `MethodHandler -> MethodWrapperEmitter`
    // pipeline. Without cross-emitter dedup the wrapper file ends up with two
    // `@_cdecl` blocks for the same underlying Swift method.
    public func step(_ enabled: Bool) -> Bool {
        if enabled {
            stepCounter += 1
        }
        return enabled
    }

    // True overload of `step(_:)` — same external label, distinct parameter
    // type. Its `PrintedName` collides with the Bool variant (`step(_:)`), so
    // a labels-only dedup key would silently drop one of the two wrappers.
    // Reaching this method at runtime confirms the canonical source key
    // distinguishes overloads by parameter type, not just by label.
    public func step(_ count: Int32) -> Int32 {
        strideCounter += count
        return strideCounter
    }
}

public final class WrapperCohesionBuilder: WrapperCohesionBuildable {
    public var stepCounter: Int32 = 0
    public var strideCounter: Int32 = 0

    public init() {}
}
