// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Strong stored properties of class type on a plain Swift class.
//
// Swift lowers a stored-property setter as `(@owned Value, @guaranteed self) -> ()`:
// the callee consumes a +1 on the new value and releases whatever the slot held before.
// When such an accessor is reached through the generated native assembly thunk — which
// only shifts registers and tail-calls the real accessor, owning nothing itself — the
// caller still has to hand that +1 across. Passing the object borrowed instead costs it
// a retain it never received, and the over-release surfaces much later, on whatever
// thread next touches the object.
//
// These shapes exist purely so the retain count of the assigned object can be read
// before and after an assignment; they deliberately hold no other state.

/// The object whose refcount the assignment is measured on.
public class ThunkedPropertyPayload {
    public let tag: Int

    public init(tag: Int) {
        self.tag = tag
    }
}

/// Holds the payload both non-optionally and optionally, so the single-slot class
/// argument and the `Optional<class>` argument are both exercised as setter values.
public class ThunkedStrongPropertyHost {
    public var strongPayload: ThunkedPropertyPayload
    public var optionalPayload: ThunkedPropertyPayload?

    public init(strongPayload: ThunkedPropertyPayload) {
        self.strongPayload = strongPayload
        self.optionalPayload = nil
    }

    /// Reads the strong slot back without going through the property accessor, so a test
    /// can prove the stored value is the object it assigned and not a dangling pointer.
    public func strongPayloadTag() -> Int {
        return strongPayload.tag
    }
}
