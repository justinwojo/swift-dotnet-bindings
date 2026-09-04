// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Requirement newer than its protocol, shadowed by an extension default
//
// The ABI shape here is a delegate protocol introduced at one OS version whose
// individual requirement was added in a LATER one, while a protocol extension
// supplies a same-signature default implementation available at the protocol's
// own (older) floor. Apple's SDKs use this pattern to add a requirement to an
// already-shipped delegate protocol without breaking existing conformers.
//
// It is a trap for generated dispatch code: inside a function whose availability
// context is only the PROTOCOL's floor, the newer requirement is not visible, so
// `existential.newerDidChange(value:)` silently resolves to the extension member
// — a static call to the default, never a witness-table dispatch. There is no
// error and no warning; the only symptom is that the conformer's implementation
// is never reached. The generated `@_cdecl` forwarder therefore has to be declared
// with the merged availability of the protocol AND the requirement.
//
// `staggeredDefaultImplementationReachCount()` is the tripwire: if it is non-zero after a
// call through the generated binding, the call was statically bound to the default.

nonisolated(unsafe) private var staggeredDefaultReachCount: Int32 = 0

/// How many times the protocol extension's default implementation ran. Must stay 0
/// whenever a conformer supplies its own implementation — a non-zero count proves the
/// call was resolved statically to the default instead of dispatched through the
/// witness table.
public func staggeredDefaultImplementationReachCount() -> Int32 {
    return staggeredDefaultReachCount
}

/// Resets the default-implementation tripwire so each test starts from a known state.
public func resetStaggeredAvailabilityCounters() {
    staggeredDefaultReachCount = 0
}

/// Delegate protocol whose `newerDidChange(value:)` requirement is introduced one OS
/// version after the protocol itself.
@available(iOS 16.0, *)
public protocol StaggeredAvailabilityDelegate: AnyObject {
    /// Requirement available at the protocol's own floor — the control.
    func olderDidChange(value: Int32)

    /// Requirement introduced after the protocol. A forwarder emitted at the
    /// protocol's floor cannot see this declaration.
    @available(iOS 17.0, *)
    func newerDidChange(value: Int32)

    /// Readable at the protocol's own floor, writable only one OS version later — the
    /// shape a get-only requirement takes once it is made settable in a later SDK.
    /// Both accessors get their own witness-table slot, so the generated getter and
    /// setter forwarders each have to reach the conformer's accessor rather than
    /// collapsing onto one.
    ///
    /// Note on what the binding can see here: the accessor-level `@available` below is
    /// accepted by the compiler and reaches the binary module, but the textual
    /// `.swiftinterface` this framework also emits prints the requirement as
    /// `{ get set }` — accessor-level availability is not part of that format. This
    /// fixture's ABI JSON is produced by recompiling that interface, which is what a
    /// binary framework distributed as a `.swiftinterface` gives a consumer, so the
    /// setter's stricter floor is simply absent from the input on this path. It is
    /// present when the ABI JSON comes from an SDK module dump instead, which is where
    /// the staggered-setter shape actually shows up in practice.
    var staggeredValue: Int32 {
        get
        @available(iOS 17.0, *) set
    }
}

@available(iOS 16.0, *)
extension StaggeredAvailabilityDelegate {
    /// Same-signature default at the protocol's floor. Deliberately unannotated: it is
    /// what an under-annotated caller resolves to instead of the requirement, so it
    /// records that it was reached rather than being a silent no-op.
    public func newerDidChange(value: Int32) {
        staggeredDefaultReachCount += 1
    }
}

/// Harness that stores a delegate and can dispatch to it from the Swift side.
@available(iOS 16.0, *)
public class StaggeredAvailabilityHarness {
    /// Plain strong storage — this fixture is about dispatch resolution, not lifetime.
    public var delegate: StaggeredAvailabilityDelegate?

    public init() {}

    /// Positive control for the newer requirement: the `if #available` check widens the
    /// availability context of the call, so the requirement is visible here and Swift
    /// emits a witness-table dispatch. If this reaches the conformer while the same call
    /// through the generated forwarder does not, the forwarder's own availability is wrong.
    public func invokeNewerFromSwift(value: Int32) {
        guard let delegate = delegate else { return }
        if #available(iOS 17.0, *) {
            delegate.newerDidChange(value: value)
        }
    }

    /// Control for the requirement that is as old as the protocol.
    public func invokeOlderFromSwift(value: Int32) {
        delegate?.olderDidChange(value: value)
    }

    /// Reads the staggered property through the witness table. The getter is available at
    /// the protocol's own floor, so no widening is needed here.
    public func readStaggeredValueFromSwift() -> Int32 {
        return delegate?.staggeredValue ?? -1
    }

    /// Writes the staggered property through the witness table. The setter is only visible
    /// inside a context widened to its own floor, which is exactly the constraint the
    /// generated setter forwarder has to reproduce.
    public func writeStaggeredValueFromSwift(value: Int32) {
        guard let delegate = delegate else { return }
        if #available(iOS 17.0, *) {
            delegate.staggeredValue = value
        }
    }
}
