// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nested Type Flattening
// Pattern caught in SwipeCellKit validation.
// Swift nested types (Outer.Inner) must be flattened for C# emission
// because C# nested types have different semantics.

/// Outer class containing nested types.
/// Generator must flatten: TypeContainer.State → TypeContainer_State (or similar).
public class TypeContainer {
    public var name: String

    public init(name: String) {
        self.name = name
    }

    /// Nested enum inside a class.
    public enum State: Int32 {
        case empty = 0
        case loading = 1
        case loaded = 2
        case error = 3
    }

    /// Nested struct inside a class.
    public struct Options {
        public var animated: Bool
        public var duration: Double

        public init(animated: Bool = true, duration: Double = 0.3) {
            self.animated = animated
            self.duration = duration
        }
    }

    /// Method using nested types.
    public func describe(state: State) -> String {
        return "\(name): \(state)"
    }
}

/// Function using nested enum type from TypeContainer.
public func containerStateName(_ state: TypeContainer.State) -> String {
    switch state {
    case .empty: return "empty"
    case .loading: return "loading"
    case .loaded: return "loaded"
    case .error: return "error"
    @unknown default: return "unknown"
    }
}

/// Function using nested struct type from TypeContainer.
public func defaultTypeContainerOptions() -> TypeContainer.Options {
    return TypeContainer.Options()
}

// MARK: - Multi-Level Nesting

/// Class with deeper nesting to test flattening depth.
public class Outer {
    public var id: Int32

    public init(id: Int32) {
        self.id = id
    }

    /// First level nesting.
    public struct Inner {
        public var label: String

        public init(label: String) {
            self.label = label
        }

        /// Second level nesting — Outer.Inner.Detail.
        public struct Detail {
            public var info: String

            public init(info: String) {
                self.info = info
            }
        }
    }
}

/// Function using multi-level nested type.
public func makeOuterInner(label: String) -> Outer.Inner {
    return Outer.Inner(label: label)
}

/// Function using deeply nested type.
public func makeOuterInnerDetail(info: String) -> Outer.Inner.Detail {
    return Outer.Inner.Detail(info: info)
}
