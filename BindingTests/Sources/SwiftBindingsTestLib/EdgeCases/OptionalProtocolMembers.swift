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

// MARK: - `@objc optional` member declared BEFORE a required member (Defect C)
//
// `OptionalCallbackDelegate` above declares its required method FIRST, so it
// cannot catch the reverse-dispatch slot-index skew: the index for the required
// member is 0 whether or not optionals are skipped. This protocol puts the
// optional FIRST.
//
// The reverse-dispatch vtable (Swift → C# callback) numbers its slots by walking
// the protocol's members. The producers (the Swift wrapper's vtable struct and
// the `SBW_…` witness accessors) skip `@objc optional` members BEFORE assigning a
// slot index, so `fireRequired` must land at slot 0. If a C# consumer walk
// increments the slot index BEFORE skipping the optional, the C#-side vtable
// struct gains a phantom slot for the optional and pushes `fireRequired`'s
// function pointer to offset 8 — while the Swift wrapper still reads offset 0.
// Swift then calls a null/garbage pointer → SIGSEGV (the same-module Finding-8
// shape). `OptionalFirstDelegate` exercises that exact ordering end-to-end.

@objc public protocol OptionalFirstDelegate: NSObjectProtocol {
    /// Optional, declared FIRST — must consume NO reverse-dispatch slot.
    @objc optional func willStartOptional(_ tag: Int32)

    /// Required, declared AFTER the optional — must occupy vtable slot 0.
    func fireRequired(_ tag: Int32)
}

/// Records the tag handed back by Swift so the runtime test can assert the
/// reverse call landed on the required member (and carried the right value).
public class RecordingFirstConformer: NSObject, OptionalFirstDelegate {
    public var lastFiredTag: Int32 = -1

    public override init() {
        super.init()
    }

    public func fireRequired(_ tag: Int32) {
        lastFiredTag = tag
    }
}

/// Reverse-dispatch entry point: Swift invokes the REQUIRED member on whatever
/// is plugged in. A C#-backed conformer routes the call through the generated
/// vtable slot 0; a native conformer dispatches directly.
public func invokeFireRequired(_ delegate: OptionalFirstDelegate, tag: Int32) {
    delegate.fireRequired(tag)
}
