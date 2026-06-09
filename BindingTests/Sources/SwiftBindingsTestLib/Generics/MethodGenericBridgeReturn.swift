// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Compile gate for the SYNCHRONOUS MethodGenericBridge (MGB) indirect-result
// path — the bridge that emits a C# P/Invoke whose Swift @_cdecl symbol ends in `_XM` (not `_XMA`,
// the async variant). MGB fires for a method with a single class-bound, protocol-constrained,
// method-own generic parameter; it is shadowed by CSM whenever CSM has conformers for the
// constraint protocol. To route EXCLUSIVELY through MGB this fixture uses a plain class-bound
// protocol with:
//   - no associated type / Self requirement (so it is not a PAT — CSM's other trigger),
//   - no entry in specialization-hints.json, and
//   - deliberately NO conformer declared in this module
// so CSM finds zero conformers and does not emit a concrete overload. The host class is
// non-generic and the methods are non-throwing / non-async, satisfying the remaining MGB gates.
//
// MGB-sync is compile-gated, NOT runtime-reachable from BindingTests (its only runtime entry is
// the documented-fragile bridge dispatch), so this fixture's job is to prove the indirect-result
// ownership EMISSION COMPILES. The runtime ownership contract (sized alloc, allocator match,
// wire-retain destroy, no double-free) is asserted directly on the emitted shape by the unit
// tests in MethodGenericBridgeEmitterTests.cs.
//
// Indirect-result buffer fix: the buffer was a fixed `Marshal.AllocHGlobal(256)` — a heap overflow
// for any return whose Swift stride exceeds 256 bytes. The fix sizes via `GetSwiftTypeSize<T>()`,
// so the emitted size is correct regardless of stride (the overflow was a runtime fault; a small
// struct exercises the same emission shape the large one would).

/// Plain class-bound protocol. `: AnyObject` makes the MGB existential-opening bridge sound
/// (it uses `Unmanaged<AnyObject>.fromOpaque`). No associated types, no Self requirement.
public protocol BridgeProvider: AnyObject {
    var bridgeTag: Int32 { get }
}

/// Non-frozen struct (no `@frozen`): resilient layout → always an indirect result, projected as
/// a SafeHandle-backed class. Routes to the MGB ownership-transfer branch (the returned SafeHandle
/// adopts the `NativeMemory.Alloc`'d buffer; no finally-free).
public struct BridgeBox {
    public let value: Int32
    public init(value: Int32) { self.value = value }
}

/// `@frozen` struct carrying a class (reference) field → projected as ClassWithBufferStruct
/// (RequiresMemoryManagement). Routes to the MGB wire-destroy branch: the wire buffer is copied
/// out, its +1 ref-field retains are VWT-destroyed, then the C#-owned buffer is freed. Without
/// the VWT destroy, every call leaked +1 on `holder`.
@frozen
public struct BridgeRefBox {
    public let holder: BridgeRefHolder
    public init(holder: BridgeRefHolder) { self.holder = holder }
}

public final class BridgeRefHolder {
    public let tag: Int32
    public init(tag: Int32) { self.tag = tag }
}

/// Non-generic host so the MGB "skip generic parent" gate passes. Each method has exactly one
/// method-own generic param constrained to the plain class-bound `BridgeProvider`.
public final class BridgeHost {
    public init() {}

    /// Ownership-transfer branch: non-frozen struct return.
    public func makeBox<T: BridgeProvider>(_ provider: T) -> BridgeBox {
        return BridgeBox(value: provider.bridgeTag)
    }

    /// Wire-destroy branch: frozen-with-ref struct return.
    public func makeRefBox<T: BridgeProvider>(_ provider: T) -> BridgeRefBox {
        return BridgeRefBox(holder: BridgeRefHolder(tag: provider.bridgeTag))
    }

    // ── Synthetic-name collisions on the MGB path ──────────────────────
    //
    // The MGB Swift wrapper hand-emits the generic-parameter pointer binding as `_{label}`
    // (NOT through the keyword/reserved escape that Map applies to non-generic params), and
    // its instance-method body declares the receiver as `let __self`. Its C# public method
    // hardcodes the indirect-result body local as `resultPtr`. These two methods pin the
    // collisions those hardcodes allow; both are compile-gated (MGB-sync is not runtime
    // round-trippable from BindingTests — see the header note).

    /// B — a generic parameter internally named `_self` (no external label, so forwarding is
    /// positional and the `_self`→`self:` label artifact is not exercised). The MGB wrapper emits
    /// the generic-pointer binding as `_{label}` = `__self`, which duplicates the receiver body
    /// local `let __self`. `swiftc` rejects the duplicate and silently drops the wrapper unless the
    /// generic-pointer binding is escaped / the body local is minted collision-free.
    public func makeBoxSelf<T: BridgeProvider>(_ _self: T) -> BridgeBox {
        return BridgeBox(value: _self.bridgeTag)
    }

    /// C — a user parameter projecting to the C# synthetic body local `resultPtr`, on a method
    /// with an indirect (non-frozen-struct) return. The emitted C# public method declares
    /// `IntPtr resultPtr` in the same scope as the `resultPtr` parameter → CS0136 unless the
    /// body local is seeded against the public parameter names.
    public func makeBoxAt<T: BridgeProvider>(resultPtr: Int32, _ provider: T) -> BridgeBox {
        return BridgeBox(value: provider.bridgeTag + resultPtr)
    }
}
