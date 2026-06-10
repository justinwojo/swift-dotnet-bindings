// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - `@objc optional` protocol members
//
// Reproduces the shape where `@objc optional` protocol members were emitted
// as mandatory requirements. The protocol declares one mandatory method, two
// `@objc optional` methods (one void, one returning Int), and one `@objc
// optional` getter property. The lowered C# interface must:
//
//   1. Keep the mandatory member as a real interface requirement (no body).
//   2. Emit each optional member as a Default Interface Method (DIM) with a
//      no-op / `default` body, so a consumer that conforms with `class
//      MyDelegate : IOptionalCallbackDelegate { /* only the mandatory one */ }`
//      compiles cleanly — no CS0535 boilerplate stubs.
//
// The runtime test in `OptionalProtocolMembersTests.cs` exercises both: the
// pure-C# minimal conformer side, and a round-trip through a Swift consumer
// that calls into the C# conformer through the proxy.

@objc public protocol OptionalCallbackDelegate: NSObjectProtocol {
    /// Always required. Consumers MUST implement this.
    func didFireRequired(_ tag: Int32)

    /// Optional. Consumer may leave it unimplemented; default DIM body is `{ }`.
    @objc optional func didFireOptionalVoid(_ tag: Int32)

    /// Optional with a return value. DIM body is `=> default!;` (i.e. 0 for Int32).
    @objc optional func didReportProgress(_ tag: Int32) -> Int32

    /// Optional get-only property. DIM body is `=> default!;`.
    @objc optional var optionalLabel: Int32 { get }

    /// Optional async returning method. DIM body must be
    /// `=> Task.FromResult<long>(default!);` so unawaited calls don't NRE.
    @objc optional func fetchValue() async -> Int32
}

/// Lightweight Swift conformer used by the round-trip test. Implements only
/// the mandatory member so the optional defaults exercise the no-op path.
public class MinimalOptionalConformer: NSObject, OptionalCallbackDelegate {
    public var lastRequiredTag: Int32 = -1

    public override init() {
        super.init()
    }

    public func didFireRequired(_ tag: Int32) {
        lastRequiredTag = tag
    }
}

/// Helper that lets the runtime test ask Swift to invoke the mandatory
/// requirement on whatever value is plugged in (proxy or native).
public func invokeRequired(_ delegate: OptionalCallbackDelegate, tag: Int32) {
    delegate.didFireRequired(tag)
}

/// Factory exposing the minimal conformer through the optional-aware
/// existential so the C# side can round-trip through it.
public func makeMinimalOptionalConformer() -> MinimalOptionalConformer {
    return MinimalOptionalConformer()
}
