// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Parameter Named "result" Collision
//
// Regression coverage: when a Swift method has a parameter named `result` and a
// non-void direct return, the generator used to emit `var result = PInvoke_*(... result ...)`
// in the C# wrapper, which caused CS0841/CS0136 self-referential shadowing and the
// generated bindings would not compile. The fix renames the P/Invoke return local to
// `__result` whenever a method parameter is also named `result`.

/// Class with methods whose parameters are named `result` to exercise the rename.
public class ResultParameterCollider {
    public init() {}

    /// Direct (blittable) return with a `result` parameter.
    public func compute(result: Int32) -> Int32 {
        return result * 2
    }

    /// String return path with a `result` parameter — exercises Utf8Slice marshalling
    /// alongside the renamed return local.
    public func describe(result: Int32) -> String {
        return "result=\(result)"
    }

    /// Static variant — confirms the rename applies regardless of dispatch.
    public static func staticCompute(result: Int32) -> Int32 {
        return result + 1
    }
}

/// Frozen struct with a failable initializer whose parameter is named `result`.
/// Without the rename the generator emits `TryCreate(int result, out ResultFailable result)`
/// — duplicate parameter name.
public struct ResultFailable {
    public let value: Int32

    public init?(result: Int32) {
        guard result >= 0 else { return nil }
        self.value = result
    }
}
