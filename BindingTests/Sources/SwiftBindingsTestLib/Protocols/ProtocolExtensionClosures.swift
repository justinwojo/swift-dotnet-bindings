// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Fixtures for the ProtocolExtensionClosureBridge (PExtCB) emitter: protocol
// extension methods that take an `@escaping` closure parameter. These exercise
// the throw-window fix and `_SBClosureCtx` owner-token wiring on the protocol
// extension code path (separate from MethodClosureBridge / NestedClosureBridge).
//
// Only the void-returning variant is generated as a class member by PExtCB
// today; non-void return shapes still surface as NotSupportedException defaults
// on the interface.

public protocol PExtClosureProtocol {
    var seed: Int32 { get }
}

extension PExtClosureProtocol {
    public func runEscapingVoid(_ callback: @escaping () -> Void) {
        callback()
    }

    // Non-escaping sibling of `runEscapingVoid` for the PExtCB non-escaping GCHandle-free
    // regression (Theme C). The callback fires synchronously inside the call and Swift never
    // assumes ownership, so the wrapper must free the per-call GCHandle in `finally`. Pre-fix
    // the try/finally was gated on `IsEscaping`, so the non-escaping branch emitted no finally
    // and leaked the handle (rooting the managed delegate and its captured graph) for the
    // process lifetime. Freed in pure C# (no `_SBClosureCtx` deinit), so this verifies on the
    // simulator too — unlike the escaping/async leak probes which are device-only.
    public func runNonEscapingVoid(_ callback: () -> Void) {
        callback()
    }
}

public final class PExtClosureSeed: PExtClosureProtocol {
    public let seed: Int32
    public init(seed: Int32) { self.seed = seed }
}
