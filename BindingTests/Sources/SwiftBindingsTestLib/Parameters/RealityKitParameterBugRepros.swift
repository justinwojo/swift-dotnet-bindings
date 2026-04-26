// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Bug 2: Param names containing `?` / `!` from optional/IUO types

/// Reproduces RealityKit ARView API methods where Swift parameter labels are
/// implicit and the generator falls back to typename-derived param names.
/// When the type is `Optional<X>` or `X!`, the synthesized name used to leak
/// the `?`/`!` characters into Swift identifier positions, breaking the
/// wrapper. The generator must strip those characters from synthesized names.
public class BugReproOptionalParamNames {
    public var lastBlittable: Int32 = 0
    public var lastString: String = ""

    public init() {}

    /// Unlabeled Optional param (`Int32?`) — used to synthesize `int32?`.
    public func setBlittable(_ value: Int32?) {
        lastBlittable = value ?? -1
    }

    /// Unlabeled IUO String param — used to synthesize `string!`.
    public func setString(_ value: String!) {
        lastString = value ?? ""
    }
}

// MARK: - Bug 6: Noncopyable parameter on a protocol-extension method

/// A `~Copyable` parameter type. Mirrors the shape of
/// RealityFoundation.PostProcessEffectContext, which exposes a noncopyable
/// payload referenced by an extension method on PostProcessEffect.
public struct BugReproNonCopyableContext: ~Copyable {
    public var token: Int32
    public init(token: Int32) { self.token = token }
}

/// Protocol with an extension method that takes a noncopyable parameter.
/// Swift 6 requires the parameter to declare ownership (`borrowing` or
/// `consuming`); the generator must propagate that through both the
/// EveryProtocol implementation and any @_silgen_name extension wrapper.
public protocol BugReproNonCopyableConsumer {
    func name() -> String
}

extension BugReproNonCopyableConsumer {
    public func process(context: consuming BugReproNonCopyableContext) -> Int32 {
        context.token
    }
}

public class BugReproNonCopyableConsumerDefault: BugReproNonCopyableConsumer {
    public init() {}
    public func name() -> String { "default" }
}
