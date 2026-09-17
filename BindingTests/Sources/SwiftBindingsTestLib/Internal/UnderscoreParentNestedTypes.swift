// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nested types under an underscore-suppressed parent
//
// An underscore-prefixed `@usableFromInline internal` type is suppressed from the C#
// surface, and nested types are only ever emitted from inside their parent's handler, so
// nothing nested in it is emitted either. The nested helpers below have no public members
// and only internal ones, which is exactly the shape the silent-tombstone pre-pass counts
// as an opaque tombstone. Before the pre-pass learned to stop at a suppressed parent it
// registered these children even though no handler emitted them, and generation failed
// with the "silent tombstone invariant violated" error.
//
// `UnderscoreParentHost` is the public surface that reaches the internal table through
// `@inlinable` code, the way a hash-based collection reaches its internal storage.

@usableFromInline
internal struct _UnderscoreParentTable {
    @usableFromInline
    internal var header: Header

    @usableFromInline
    internal init(capacity: Int) {
        header = Header(capacity: capacity)
    }

    @usableFromInline
    internal struct Header {
        @usableFromInline
        internal var capacity: Int

        @usableFromInline
        internal init(capacity: Int) {
            self.capacity = capacity
        }
    }

    @usableFromInline
    internal final class Storage {
        @usableFromInline
        internal var header: Header

        @usableFromInline
        internal init(header: Header) {
            self.header = header
        }
    }

    @usableFromInline
    internal enum Slot {
        case empty
        case occupied(Int)

        @usableFromInline
        internal var isEmpty: Bool {
            if case .empty = self { return true }
            return false
        }
    }
}

public struct UnderscoreParentHost {
    @usableFromInline
    internal var table: _UnderscoreParentTable

    public init(capacity: Int) {
        table = _UnderscoreParentTable(capacity: capacity)
    }

    @inlinable
    public var capacity: Int {
        table.header.capacity
    }
}
