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

    // A `throws ->` closure in a protocol extension. The declared closure projects as
    // `Func<int, SwiftResult<bool, SwiftError>>`, but the bridge writes a plain `Func<int, bool>` —
    // the throwing arm is carried by the shim, not by the delegate type — so the emitted parameter
    // list does not match the declared one. With TWO conformers the divergence is observable: the
    // interface declaration supplies only one member of the declared shape, so the second conformer's
    // entry has nothing left to reconcile against.
    //
    // Provenance: a 3D-scene framework's entity collection declares `removeAll(where:) rethrows`
    // over a `(Entity) throws -> Bool` predicate on two collection types, and its whole binding
    // failed to generate on exactly this divergence.
    public func dropMatching(where shouldDrop: (PExtClosureItem) throws -> Bool) rethrows {
        _ = try shouldDrop(PExtClosureItem(value: seed))
    }

    // Optional callback followed by a non-optional trailing parameter, on the protocol
    // extension path. An `Optional<Closure>` occupies TWO C ABI words wherever it is passed
    // (function pointer + context); this emitter's parameter renderer has a closure arm only
    // for a BARE closure, so an Optional one fell through to a single `UnsafeRawPointer` while
    // the C# P/Invoke — whose closure classifier does look through Optional — passed the
    // two-word carrier. Both sides compiled and every later argument, `trailing` and the
    // receiver included, shifted by a register.
    //
    // The shape has no carrier on this path, so the member is deliberately left unbound rather
    // than bound wrongly; this stays as the corpus-level negative control for that decision.
    public func sumWithOptionalCallback(_ callback: ((PExtClosureItem) -> Void)?, trailing: Int32) -> Int32 {
        callback?(PExtClosureItem(value: seed))
        return seed + trailing
    }
}

/// Closure argument for `dropMatching(where:)`. It has to be a class: the protocol-extension
/// closure bridge passes every callback argument through a pointer buffer, so a primitive
/// argument is rejected outright and the member never reaches the emitter under test.
public final class PExtClosureItem {
    public let value: Int32
    public init(value: Int32) { self.value = value }
}

public final class PExtClosureSeed: PExtClosureProtocol {
    public let seed: Int32
    public init(seed: Int32) { self.seed = seed }
}

/// The second conformer of the throwing-closure extension shape above — one conformer alone would
/// be reconciled by the interface declaration's own member.
public final class PExtClosureSecondSeed: PExtClosureProtocol {
    public let seed: Int32
    public init(seed: Int32) { self.seed = seed }
}
