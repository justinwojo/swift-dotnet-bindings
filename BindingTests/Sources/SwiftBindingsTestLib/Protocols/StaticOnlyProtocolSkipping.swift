// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Bug #5 fixture: protocol with only `static var` requirements

/// Reproduces the RealityFoundation `RealityCoordinateSpace` / `MaterialFunction`
/// pattern: a protocol whose entire requirement surface is `static var` properties
/// (no `Self` mention, no instance members).
///
/// Earlier the generator emitted an `extension EveryProtocol: P { ... }` block with
/// `fatalError()` stub bodies for each static var. Swift's type-checker rejected
/// those conformances when the requirement was constrained (e.g.
/// `static var scene: SceneRealityCoordinateSpace` where the witness type carried
/// inherited-protocol constraints EveryProtocol couldn't satisfy), so the wrapper
/// failed to compile. The fix skips the EveryProtocol conformance for these
/// protocols (`StaticPropertyRequirements` skip reason). The C# interface is still
/// emitted; only the proxy class (which would need the witness table) is suppressed.
///
/// The fixture below should round-trip cleanly: the protocol interface, the
/// conforming class, and a consumer that returns its static value via an instance
/// method should all build and execute on simulator and device.
public protocol Bug5StaticOnlyProtocol {
    static var defaultIdentifier: String { get }
    static var defaultRank: Int32 { get }
}

/// Concrete conformer used to feed the consumer below.
public final class Bug5StaticOnlyConformer: Bug5StaticOnlyProtocol {
    public static var defaultIdentifier: String { "static-only-default" }
    public static var defaultRank: Int32 { 7 }
    public init() {}
}

/// Consumer class: holds a metatype-typed reference to a conformer and exposes
/// the static values via instance methods. The presence of this class proves the
/// wrapper compiled (it would have failed at the EveryProtocol extension before
/// the fix). Instance methods give us a stable surface to call from C# without
/// needing a proxy.
public final class Bug5StaticOnlyConsumer {
    public init() {}

    public func identifier() -> String {
        Bug5StaticOnlyConformer.defaultIdentifier
    }

    public func rank() -> Int32 {
        Bug5StaticOnlyConformer.defaultRank
    }
}
