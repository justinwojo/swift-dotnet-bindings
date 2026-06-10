// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Non-Decomposed Optional Setter (GetCdeclParamMapping path)

/// Class with Optional<Double> property — exercises the GetCdeclParamMapping branch
/// in PropertyWrapperEmitter.EmitSetterWrapper (line 471-496) instead of the
/// IsDecomposedOptionalType branch (line 461-469).
/// Exercises the Optional<Double> setter path (GetCdeclParamMapping branch).
public class CacheConfig {
    public var ttl: Double?
    public var maxSize: Int32

    public init(ttl: Double?, maxSize: Int32) {
        self.ttl = ttl
        self.maxSize = maxSize
    }

    public func effectiveTtl() -> Double {
        return ttl ?? 300.0
    }
}

// MARK: - Decomposed Optional Setter (IsDecomposedOptionalType path)

/// Class with Optional<ComplexEnum> property — exercises the IsDecomposedOptionalType
/// branch in PropertyWrapperEmitter.EmitSetterWrapper (line 461-469).
/// Shape is a complex enum with associated values (decomposed in ABI).
public class ShapeHolder {
    public var currentShape: Shape?

    public init(shape: Shape?) {
        self.currentShape = shape
    }

    public func describeShape() -> String {
        if let s = currentShape {
            return s.describe()
        }
        return "none"
    }
}

// MARK: - Optional<Class> Property (reference Optional, not decomposed)

/// Class with Optional<Class> property — exercises IsDecomposedOptionalType returning false
/// (line 250-251) because inner type is a class (reference type → IntPtr ABI).
/// Real-world pattern: UIKit parent references.
public class NodeWithParent {
    public var label: String
    public var parent: Animal?

    public init(label: String, parent: Animal?) {
        self.label = label
        self.parent = parent
    }

    public func parentName() -> String {
        return parent?.name ?? "none"
    }
}

// MARK: - Frozen Struct with Optional<BlittablePrimitive> (tag-byte fixup)

/// Frozen struct with Optional<Int32> property — exercises the tag-byte fixup path
/// in PropertyWrapperEmitter.EmitGetterWrapper (line 349-372).
@frozen
public struct TaggedCounter {
    public var count: Int32?
    public var name: String

    public init(count: Int32?, name: String) {
        self.count = count
        self.name = name
    }

    public func effectiveCount() -> Int32 {
        return count ?? 0
    }
}

// MARK: - Static Optional Property

/// Class with static Optional<Double> property — exercises the isStatic branch
/// in PropertyWrapperEmitter.EmitSetterWrapper (line 564).
public class GlobalSettings {
    public static var defaultTimeout: Double? = nil
    public static var appName: String = "TestApp"

    public static func effectiveTimeout() -> Double {
        return defaultTimeout ?? 30.0
    }
}
