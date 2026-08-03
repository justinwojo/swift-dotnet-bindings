// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Reverse-dispatch honesty: hollow vs. partially-dispatchable protocols
//
// A C# type can declare `: ISomeDelegate` for any emitted protocol interface. That declaration is a
// promise that Swift will call the type back. The promise is only kept for requirements that own a
// reverse-dispatch vtable slot AND get a receiver trampoline wired into it. When EVERY requirement of a
// protocol falls out of that set, the emitted interface is still conformable and the proxy still
// registers an entirely null vtable with Swift — so a C# implementation compiles, links, runs, and is
// never once invoked, with nothing in the source or the report to say so.
//
// The two protocols below are the negative and positive control for that gate, deliberately kept
// adjacent so the contrast survives future edits: they share the same non-dispatchable requirement and
// differ only in whether ONE plain requirement joins it.

/// NEGATIVE CONTROL — no requirement is reverse-dispatchable.
///
/// Both requirements carry a closure the reverse-dispatch path cannot marshal:
///   * `beginUpload(onProgress:)` pairs a closure parameter with a non-`Void` return — the dispatch
///     path treats the closure as the method's only output channel, so a real return value has
///     nowhere to go alongside it.
///   * `finishUpload(onSuccess:onFailure:)` declares two closure parameters — the receiver can carry
///     exactly one `(fnPtr, ctx)` pair per slot.
///
/// Both stay on the C# interface (they are perfectly usable in the forward direction, where Swift
/// vends the conformer), but neither fills a callback slot. The binding must therefore register no
/// vtable at all and mark the interface, rather than hand Swift a table of nulls.
public protocol HollowUploadDelegate {
    func beginUpload(onProgress: @escaping (Double) -> Void) -> Int32
    func finishUpload(onSuccess: @escaping () -> Void, onFailure: @escaping () -> Void)
}

/// POSITIVE CONTROL — the partial case, which must be left alone.
///
/// Carries the identical non-dispatchable `beginUpload(onProgress:)` requirement, plus one plain
/// requirement that does dispatch. One filled slot is enough to keep the interface honest: a C#
/// implementation genuinely is called back for `uploadIdentifier()`, so the vtable stays registered
/// and the interface carries no marker. The gate is strictly zero-filled, never "some requirement was
/// dropped" — degrading a partially-working protocol would cost real, working reverse dispatch.
public protocol PartialUploadDelegate {
    func beginUpload(onProgress: @escaping (Double) -> Void) -> Int32
    func uploadIdentifier() -> String
}

/// Traffics in both protocols so their proxy classes are emitted (a protocol nothing accepts or vends
/// never reaches the proxy emitter), and drives the positive control's reverse dispatch so the
/// surviving slot is proven to fire rather than merely to exist.
public class UploadCoordinator {
    public var hollowDelegate: (any HollowUploadDelegate)?
    public var partialDelegate: (any PartialUploadDelegate)?

    public init() {
        self.hollowDelegate = nil
        self.partialDelegate = nil
    }

    /// Reverse dispatch through the one filled slot: with a C#-authored `partialDelegate`, this
    /// returns the string the C# implementation produced.
    public func partialIdentifier() -> String {
        return partialDelegate?.uploadIdentifier() ?? "none"
    }

    /// Forward direction for the hollow protocol — a Swift-vended conformer consumed through the C#
    /// interface. This path is unaffected by the missing vtable and must keep working.
    public func vendHollowDelegate() -> any HollowUploadDelegate {
        return HollowUploadDelegateImpl()
    }

    /// Round-trip probe for the vended value. Every requirement of `HollowUploadDelegate` carries a
    /// closure, so C# cannot invoke one through the existential to prove the value is live — but Swift
    /// can. Handing the projection back here and dispatching through the conformer's OWN witness table
    /// returns 7 for a real `HollowUploadDelegateImpl` and cannot return it for an empty shell, which is
    /// the assertion the C# side needs: suppressing the reverse-dispatch registration did not cost the
    /// produce path.
    public func probeHollowDelegate(_ delegate_: any HollowUploadDelegate) -> Int32 {
        var observed = 0.0
        let code = delegate_.beginUpload(onProgress: { observed = $0 })
        return observed > 0 ? code : -1
    }
}

/// Swift-side conformer for the forward direction.
public struct HollowUploadDelegateImpl: HollowUploadDelegate {
    public init() {}
    public func beginUpload(onProgress: @escaping (Double) -> Void) -> Int32 {
        onProgress(1.0)
        return 7
    }
    public func finishUpload(onSuccess: @escaping () -> Void, onFailure: @escaping () -> Void) {
        onSuccess()
    }
}
