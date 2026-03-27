// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Struct Property Access Tests (R1 regression)

/// Non-frozen struct with properties — tests repeated access patterns.
/// Exercises the Nuke crash where accessing struct-typed properties on
/// cached objects causes silent memory corruption or leaks.
public struct InnerData {
    public var value: Int32
    public var name: String

    public init(value: Int32, name: String) {
        self.value = value
        self.name = name
    }
}

/// Class holding a non-frozen struct — tests repeated property access.
public class DataContainer {
    private var _data: InnerData

    public init(value: Int32, name: String) {
        self._data = InnerData(value: value, name: name)
    }

    /// Repeated access to this property should not leak or crash.
    public var data: InnerData { _data }

    /// Second access exercises the cache-hit path (Nuke pattern).
    private var _cached: InnerData?
    public var cachedData: InnerData {
        if let c = _cached { return c }
        _cached = _data
        return _data
    }
}
