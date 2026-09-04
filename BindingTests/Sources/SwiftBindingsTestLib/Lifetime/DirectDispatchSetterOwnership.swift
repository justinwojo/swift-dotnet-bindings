// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A class-valued setter reached by DIRECT CallConvSwift dispatch — no native assembly
// thunk, no @_cdecl wrapper, no wrapper-library entry between C# and Swift's own accessor.
//
// Swift lowers a subscript setter as `(@owned Value, @guaranteed Index…, self) -> ()`, so
// the new value arrives at +1 exactly as on a stored property. The thunked arm of that
// hand-over is fixtured next door; this file exists for the arm where the P/Invoke names
// the accessor's own `$s…` symbol, which until now had no first-party coverage and was
// observed only in a shipped Apple binding (a subscript setter on a nested collection
// struct, whose element type is nested and whose parent struct is resilient).
//
// Both halves of that shape are load-bearing and neither is incidental:
//
//   * the parent is a NON-frozen struct, so the accessor cannot be thunked — a resilient
//     struct's opaque accessors move value-typed operands through indirect buffers, which
//     the register-shifting thunk does not bridge;
//   * the element type is NESTED, which is the one shape the @_cdecl subscript wrapper
//     declines outright.
//
// With both wrapper paths declined the accessor falls through to the direct call, which is
// what these declarations are for. Change either half and the fixture silently starts
// measuring one of the already-covered arms instead.

/// The object whose retain count an assignment through the direct arm is measured on.
/// Nested inside the collection so its projected Swift type name is nested — the shape the
/// @_cdecl subscript wrapper refuses.
public struct DirectSetterSlots {
    public final class Slot {
        public let tag: Int

        public init(tag: Int) {
            self.tag = tag
        }
    }

    private var storage: [Slot]

    public init(first: Slot, second: Slot) {
        self.storage = [first, second]
    }

    /// The subscript under test. Deliberately non-optional and non-generic: an
    /// `Optional<class>` value would route through the carrier arm and a generic one
    /// would not reach direct dispatch at all.
    public subscript(index: Int) -> Slot {
        get { return storage[index] }
        set { storage[index] = newValue }
    }

    /// Reads a slot back without going through the subscript accessor, so a test can prove
    /// the stored value is the object it assigned and not a dangling pointer.
    public func tagAt(index: Int) -> Int {
        return storage[index].tag
    }
}
