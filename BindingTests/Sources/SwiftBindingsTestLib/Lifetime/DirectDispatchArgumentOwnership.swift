// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Ownership of an ARGUMENT handed to Swift, across both arms that can carry one.
//
// SILGen lowers an initializer as `(@owned A, …, @thin Self.Type) -> @owned Self` and a
// property setter as `(@owned Value, @inout self) -> ()`: the callee RELEASES what it was
// handed. A plain `func` is the control — `(@guaranteed A, @guaranteed self) -> …`, a borrow
// the caller still owns afterwards.
//
// Which arm a member lands on is a routing decision rather than a property of its shape. A
// generated `@_cdecl` wrapper is itself a borrowing Swift frame, and SILGen mints the transfer
// inside it when that frame forwards to a consuming callee; a P/Invoke naming Swift's own `$s…`
// symbol has to mint the same transfer on the C# side. Most members below now reach Swift
// through the wrapper, so what they measure is that the wrapper frame neither drops nor
// duplicates a reference on the way through — the same counters, one frame further out.
//
// One arm still names Swift's own symbol, and is kept deliberately:
//
//   * the String-taking initializer is FAILABLE. A failable initializer has no wrapper form, so
//     `init?(text:)` is this fixture's remaining first-party measurement of a consuming call
//     made straight from C#. Make it non-failable and that coverage moves to the wrapper with
//     everything else, silently.
//
// The frozen carrier stays NESTED. Nesting no longer decides the route — a nested frozen struct
// travels to the wrapper as a raw pointer and is rebuilt in the wrapper body, where a nested
// name is ordinary Swift — but it keeps the carrier exercising that rebuild rather than the
// simpler file-scope spelling.
//
// Every string is deliberately built past 15 UTF-8 bytes at the call sites. At or below 15 a
// Swift String is the inline small form — the bytes live in the value itself and there is no
// refcount to get wrong — so a fixture written with short strings measures nothing and stays
// green through the exact defect it exists for.
//
// The class payload is what makes the probe deterministic rather than a crash: it feeds the
// same allocation counters the C# lifetime probe reads, so an unbalanced hand-over shows up as
// the payload dying while a live C# wrapper still owns it, instead of as a heap corruption
// that surfaces later in an unrelated type on an unrelated thread.

/// The observable payload. Its allocation and deallocation feed the shared counters behind
/// `SwiftBindingsTestLib_GetLiveObjectCount`, so a test can pin the exact moment the carrier's
/// reference is released rather than inferring it from a crash.
public final class OwnedArgWitness {
    public let tag: Int32

    public init(tag: Int32) {
        self.tag = tag
        recordTrackedAllocation()
    }

    deinit {
        recordTrackedDeallocation()
    }

    public func isAlive() -> Bool {
        return true
    }
}

/// Host for the frozen-carrier arms. The carrier is nested here rather than declared at file
/// scope so the wrapper has to rebuild it from a raw pointer; nothing else about the enclosing
/// type matters.
public struct OwnedArgInitHost {
    /// The reference-bearing frozen struct under test. Frozen plus reference-typed fields is
    /// the `ClassWithBufferStruct` shape: it lowers to a by-value Buffer whose words carry the
    /// very references a consuming callee releases. Both a class reference and a String are
    /// present so one lowered value covers both reference-managed carriers.
    @frozen
    public struct Carrier {
        public let witness: OwnedArgWitness
        public let note: String

        public init(witness: OwnedArgWitness, note: String) {
            self.witness = witness
            self.note = note
        }

        /// Reads the payload back without going through an accessor, so a test can prove the
        /// storage is still the value it was given rather than a reused allocation.
        public func readNote() -> String {
            return note
        }
    }

    /// Named `carried` rather than `payload` because a public Swift property called `payload`
    /// projects onto the same C# name as the emitted SafeHandle accessor.
    public let carried: Carrier

    /// Consuming arm: an initializer taking the frozen carrier.
    public init(payload: Carrier) {
        self.carried = payload
    }

    /// Control arm on the same carrier type and the same direct route: a plain method borrows
    /// its argument, so nothing may be handed over here. Emitting a transfer for this call
    /// leaks the argument — the opposite failure, and just as silent — which is why this is a
    /// negative control rather than an assumption.
    public func matchesNote(_ other: Carrier) -> Bool {
        return carried.note == other.note
    }

    /// The same control for a bare String, kept on this host rather than the String host so one
    /// call carries a String and a frozen carrier together — the two reference-managed operands
    /// meet in one lowered signature, which is where a per-operand convention error shows up.
    public func noteMatches(_ note: String, other: Carrier) -> Bool {
        return carried.note == note && other.note == note
    }

    public func hostNote() -> String {
        return carried.note
    }

    public func witnessTag() -> Int32 {
        return carried.witness.tag
    }
}

/// Consuming arm: an assignment whose new value is the same nested frozen carrier. Deliberately
/// a separate host from the initializer arm so a fix that covers construction but not assignment
/// still leaves a red here.
///
/// It is a SUBSCRIPT rather than a stored property because the subscript setter carries its
/// indices alongside the new value, so the assignment arm and the index arm can be measured on
/// the same lowering rather than on two unrelated ones.
public struct OwnedArgSetterHost {
    private var slots: [OwnedArgInitHost.Carrier]

    public init(payload: OwnedArgInitHost.Carrier) {
        self.slots = [payload]
    }

    public subscript(index: Int) -> OwnedArgInitHost.Carrier {
        get { return slots[index] }
        set { slots[index] = newValue }
    }

    public func noteAt(index: Int) -> String {
        return slots[index].note
    }

    public func tagAt(index: Int) -> Int32 {
        return slots[index].witness.tag
    }
}

/// Consuming arm for a plain CLASS argument, which is its own case rather than a variation on the
/// frozen carrier: a class parameter is not marshalled at all — the call site passes the object's
/// own payload handle straight through — so it is the one carrier whose transfer has nowhere to be
/// spelled by the marshalling of the value. The frozen carrier beside it keeps a marshalled
/// operand in the same signature, so the two cannot be confused for each other.
public struct OwnedArgClassHost {
    public let witness: OwnedArgWitness
    public let carried: OwnedArgInitHost.Carrier

    public init(witness: OwnedArgWitness, carrier: OwnedArgInitHost.Carrier) {
        self.witness = witness
        self.carried = carrier
    }

    /// Control arm: the same class type in a borrowing position on the same route. A transfer
    /// minted here is a leak the live-object counter can see, which makes this the one negative
    /// control that goes red rather than merely staying green.
    public func borrowedTag(_ other: OwnedArgWitness, carrier: OwnedArgInitHost.Carrier) -> Int32 {
        return other.tag &+ carrier.witness.tag
    }

    public func storedTag() -> Int32 {
        return witness.tag
    }
}

/// Consuming arm for a subscript INDEX. Swift lowers a subscript setter as
/// `(@owned NewValue, @owned Index…, @inout self)`: the indices are consumed alongside the new
/// value, not borrowed. The key is a String so the index carries refcounted storage of its own —
/// with an integer index a wrong index convention has nothing to get wrong.
public struct OwnedArgKeyedHost {
    private var slots: [String: OwnedArgInitHost.Carrier]
    private var fallback: OwnedArgInitHost.Carrier

    public init(key: String, payload: OwnedArgInitHost.Carrier) {
        self.slots = [key: payload]
        self.fallback = payload
    }

    public subscript(key: String) -> OwnedArgInitHost.Carrier {
        get { return slots[key] ?? fallback }
        set { slots[key] = newValue }
    }

    public func noteFor(key: String) -> String {
        return slots[key]?.note ?? ""
    }

    public func slotCount() -> Int32 {
        return Int32(slots.count)
    }
}

/// Consuming arm for the bare-String carrier, kept apart from the frozen-struct arms because
/// the two reach the call through different marshalling: a String parameter builds a transient
/// Swift String that the emitted code disposes, so a consuming callee that takes the
/// transient's only count releases storage the transient will release again.
public struct OwnedArgStringHost {
    public let text: String

    /// Failable, which is what keeps this initializer on Swift's own symbol: there is no wrapper
    /// form for a failable init, so this is the fixture's one consuming call made straight from
    /// C#. The nil arm is real (an empty string), so the failure path is exercised too.
    public init?(text: String) {
        if text.isEmpty {
            return nil
        }
        self.text = text
    }

    /// The same String type in the borrowing position, as the control.
    public func concat(_ other: String) -> String {
        return text + "|" + other
    }

    public func read() -> String {
        return text
    }
}
