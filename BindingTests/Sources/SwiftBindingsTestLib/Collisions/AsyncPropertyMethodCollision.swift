// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async-overload collision under a property-forced rename
//
// Extends the property-rename concern (PropertyMethodCollision.swift) into the ASYNC dedup path. A
// stored property `data` forces the same-named methods to rename (`Data` → `DataMethod`). The type
// then declares TWO async-producing overloads of that method: a Swift-native `async` one and a
// completion-handler one (which the generator converts to an async C# method). Both project to the
// same `DataMethodAsync(...)` — so the async/overload dedup key MUST observe the property rename, or
// the converted completion-handler overload and the native-async overload emit under the same C#
// name (CS0111). The dedup disambiguates the second as `DataMethodAsync2`. Distinct return values
// pin which body ran.

public class AsyncPropertyMethodCollider {
    public var data: Int32
    public init(data: Int32) { self.data = data }

    /// Native async overload → `DataMethod...Async` after the property rename.
    public func data(times: Int32) async -> Int32 { return data * times }

    /// Completion-handler overload (converted to async) → collides with the native-async name.
    public func data(times: Int32, completion: @escaping (Int32) -> Void) {
        completion(data * times + 1)
    }
}
