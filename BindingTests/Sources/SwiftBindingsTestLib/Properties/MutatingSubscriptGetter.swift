// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A subscript whose getter is spelled `mutating get`. Swift passes `self` to that getter
// inout, exactly as it does for a `mutating func`, so the read can change the receiver: a
// memoizing or counting accessor depends on it. A binding that passes `self` borrowed, the
// way an ordinary getter is called, reads from a copy and loses the write, or, for a frozen
// struct whose getter takes `self` by value in registers, calls with the wrong ABI.
//
// Each type pairs the subscript with a `mutating get` computed property of the same
// behavior, so the two accessor kinds are checked against each other, and with a plain
// `reads` counter so a test can observe the receiver directly rather than through the
// accessor under test.

/// Resilient struct: `self` reaches every accessor through a pointer, so a getter that
/// mutates writes back only if that pointer is the caller's own storage.
public struct MutatingSubscriptCounter {
    public private(set) var reads: Int32

    public init(reads: Int32 = 0) {
        self.reads = reads
    }

    /// Advances the counter by `step` and returns the new total.
    public subscript(step: Int32) -> Int32 {
        mutating get {
            reads += step
            return reads
        }
    }

    /// Readable and writable, with a getter that counts its own reads. The setter replaces
    /// the counter outright, so a set followed by a get proves both directions. The index is
    /// `Int`, not `Int32`, so the C# indexer does not collide with the one above: C# indexers
    /// carry no labels.
    public subscript(slot slot: Int) -> Int {
        mutating get {
            reads += 1
            return Int(reads) * 100 + slot
        }
        set {
            reads = Int32(newValue)
        }
    }

    /// Advances the counter by one and returns the new total.
    public var tick: Int32 {
        mutating get {
            reads += 1
            return reads
        }
    }
}

/// Frozen trivially-copyable struct: an ordinary getter takes `self` by value in registers,
/// while a `mutating get` takes it by address. The two conventions are not interchangeable.
@frozen
public struct FrozenMutatingSubscriptCounter {
    public var reads: Int32
    public var limit: Int32

    public init(reads: Int32 = 0, limit: Int32 = 1_000) {
        self.reads = reads
        self.limit = limit
    }

    /// Advances the counter by `step`, capped at `limit`, and returns the new total.
    public subscript(step: Int32) -> Int32 {
        mutating get {
            reads = Swift.min(reads + step, limit)
            return reads
        }
    }

    /// Advances the counter by one and returns the new total.
    public var tick: Int32 {
        mutating get {
            reads += 1
            return reads
        }
    }
}

/// Frozen struct holding a reference: the binding keeps its value in a native buffer rather
/// than in the C# struct, so the getter has to be handed that buffer, not a copy of it. The
/// `String` subscript also takes the indirect-result path.
@frozen
public struct FrozenRefMutatingSubscriptCounter {
    public var name: String
    public var reads: Int32

    public init(name: String, reads: Int32 = 0) {
        self.name = name
        self.reads = reads
    }

    /// Advances the counter by `step` and returns the new total.
    public subscript(step: Int32) -> Int32 {
        mutating get {
            reads += step
            return reads
        }
    }

    /// Stores `label` as the name, counts the read, and returns the name with the count.
    public subscript(label: String) -> String {
        mutating get {
            reads += 1
            name = label
            return "\(name)#\(reads)"
        }
    }
}
