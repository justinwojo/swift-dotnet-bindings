// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Internal-conformer-to-public-protocol fixture (Step 8a: emission-time
//         parent-module-internal @_cdecl gate, CS0535-safety)
//
// The standalone `InternalHolder` fixture in InternalTypeReach.swift proves the
// internal-receiver wrapper compiles away, but it conforms to no protocol, so a
// dropped member is harmless. THIS fixture exercises the load-bearing case the
// emission-time gate exists for: an `@usableFromInline internal` type that
// conforms to a *public* protocol. The generator projects it as a public C#
// class implementing the public C# interface, so dropping any requirement
// member would break interface satisfaction (CS0535 in the consumer compile).
//
// Each requirement witness MUST be `public` (Swift requires a witness to be at
// least as visible as the protocol). A public member's own visibility is not
// internal, so it slips the member-keyed `module_internal` wrapper guard — but
// its @_cdecl wrapper body reconstructs `self` via the *parent's*
// module-qualified name (`...SwiftBindingsTestLib.InternalContractConformer...`),
// an internal type the separate wrapper module cannot name. Before the
// emission-time gate these wrappers emit-then-strip (StripSubCause.InternalType);
// with the gate the @_cdecl is rejected at emission and the member falls back to
// a direct CallConvSwift P/Invoke against the dylib silgen symbol, so the public
// interface stays satisfied AND the wrapper-strip count stays 0.
//
// SCOPE: requirements are the kinds with a clean CallConvSwift fallback that ALSO
// round-trip through a Swift-backed existential — sync method + read-only
// property. A subscript IS gated by arm 2b (MemberKind.Subscript, same clean
// fallback as a property), but it is deliberately NOT a requirement here: a
// protocol subscript requirement reached through a Swift-backed existential is
// universally unsupported (the proxy's `this[...]` throws NotSupportedException,
// marked [Obsolete] "subscript dispatch is not yet supported"), so it can't be
// exercised at runtime via this conformer shape. The subscript arm is instead
// covered end-to-end by the standalone `InternalHolder.subscript(offset:)` in
// InternalTypeReach.swift (a concrete subscript on an internal class), plus the
// WrapperValidationTests unit gate. Async / closure-param / operator requirements
// are also NOT modelled here: those have no clean fallback (async always needs a
// wrapper that still names the parent; closure degrades to a crashing legacy
// path; a frozen-struct operator's direct CallConvSwift P/Invoke segfaults ILC on
// NativeAOT), so they remain post-processor-scoped. See the S07b-followon design
// doc and the WrapperValidation.GetMemberRejectionReason `2b` comment.

/// Public protocol whose C# projection is a public interface. An internal type
/// conforming to it must implement every member in C# or the binding fails to
/// compile (CS0535).
public protocol InternalReceiverContract {
    /// Sync, value-returning — the dominant clean-fallback shape.
    func contractValue() -> Int32
    /// Sync with a blittable parameter — exercises argument marshalling on the
    /// CallConvSwift fallback path.
    func combined(with other: Int32) -> Int32
    /// Read-only property requirement — exercises the accessor (getter) gate.
    var contractTag: Int32 { get }
}

/// `@usableFromInline internal` (ABI-visible, off-limits by name to external
/// Swift) class conforming to the public protocol. Non-`final` on purpose: a
/// non-final class method dispatches through the `Tj` thunk, which
/// `@usableFromInline` keeps exported from the dylib, so the CallConvSwift
/// fallback resolves at runtime. The init is `@usableFromInline internal`, so no
/// public C# constructor is emitted (construction barrier) — consumers obtain an
/// instance through the public factory below, behind the public existential.
@usableFromInline
internal class InternalContractConformer: InternalReceiverContract {
    @usableFromInline
    internal let seed: Int32

    @usableFromInline
    internal init(seed: Int32) {
        self.seed = seed
    }

    public func contractValue() -> Int32 {
        return seed
    }

    public func combined(with other: Int32) -> Int32 {
        return seed &+ other
    }

    public var contractTag: Int32 {
        return seed &* 10
    }
}

/// Public factory: hands C# the internal conformer behind the public existential
/// so the conformance round-trips without ever exposing the internal type by
/// name in a public signature (which Swift would reject).
public func makeInternalContractConformer(seed: Int32) -> any InternalReceiverContract {
    return InternalContractConformer(seed: seed)
}

/// A second, *public* conformer of the SAME protocol. Negative direction: proves
/// the protocol/interface machinery is intact and the parent-internal gate does
/// not bleed onto public parents (its members keep their @_cdecl wrappers and
/// round-trip exactly as before).
public final class PublicContractConformer: InternalReceiverContract {
    public let seed: Int32

    public init(seed: Int32) {
        self.seed = seed
    }

    public func contractValue() -> Int32 {
        return seed
    }

    public func combined(with other: Int32) -> Int32 {
        return seed &+ other
    }

    public var contractTag: Int32 {
        return seed &* 10
    }
}
