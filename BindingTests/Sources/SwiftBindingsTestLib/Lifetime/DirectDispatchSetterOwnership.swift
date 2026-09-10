// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A CLASS-VALUED subscript setter, and what happens to the assigned object's reference on
// the way across.
//
// Swift lowers a subscript setter as `(@owned Value, @owned Index…, self) -> ()`, so
// the new value arrives at +1 exactly as on a stored property. The indices arrive at +1
// too — the matching GETTER borrows the very same indices, so the convention follows the
// accessor rather than the parameter position. That half is measured next door on
// `OwnedArgKeyedHost`, whose index is a String and so carries a refcount to get wrong;
// the `Int` index below is POD and can show nothing about index ownership either way.
//
// The value half is what this file is for. A class value is the case with nothing to hide
// behind: it is not marshalled, so the call site hands the object's own payload handle
// straight to the accessor and the +1 has to be established around that handle rather than
// by the marshalling of a value. The shape was drawn from a subscript setter in a shipped
// Apple binding — a nested collection struct with a nested element type on a resilient
// parent — and both halves of that spelling are kept:
//
//   * the parent is a NON-frozen struct, so the accessor moves value-typed operands through
//     indirect buffers rather than in registers;
//   * the element type is NESTED, so the generated wrapper has to name it inside its own
//     body rather than in the C signature.
//
// The accessor reaches Swift through the generated `@_cdecl` wrapper. That frame borrows
// what C# hands it and mints the +1 itself when it forwards to the consuming accessor, so
// what is measured here is that the wrapper neither drops the assigned object nor leaves an
// extra reference on it.

/// The object whose retain count an assignment is measured on. Nested inside the collection
/// so its projected Swift type name is nested, which is the spelling the generated wrapper has
/// to rebuild in its own body.
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
    /// would not be bound at all.
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
