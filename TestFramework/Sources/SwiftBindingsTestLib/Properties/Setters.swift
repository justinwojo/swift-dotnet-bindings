// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Mutable Properties (Frozen) — Tier 1: Property Setters

/// Frozen struct with mutable stored properties.
@frozen
public struct MutableProps {
    public var value: Int32
    public var name: String

    public init(value: Int32, name: String) {
        self.value = value
        self.name = name
    }
}

// MARK: - Mutable Properties (Non-Frozen)

/// Non-frozen struct with mutable stored properties.
public struct NonFrozenMutableProps {
    public var value: Int32
    public var label: String

    public init(value: Int32, label: String) {
        self.value = value
        self.label = label
    }
}

// MARK: - Mutable Class

/// Class with mutable properties.
public class MutableClass {
    public var counter: Int32
    public var title: String

    public init(counter: Int32, title: String) {
        self.counter = counter
        self.title = title
    }
}

// MARK: - Computed Setter

/// Frozen struct with a computed property that has both getter and setter.
@frozen
public struct ComputedSetterStruct {
    public var rawValue: Int32

    public init(rawValue: Int32) {
        self.rawValue = rawValue
    }

    /// Computed property with get and set.
    public var doubled: Int32 {
        get { return rawValue * 2 }
        set { rawValue = newValue / 2 }
    }

    /// Computed property backed by transformation.
    public var negated: Int32 {
        get { return -rawValue }
        set { rawValue = -newValue }
    }
}

// MARK: - Y1: Nonmutating Set Property (SnapKit ConstraintViewDSL pattern)

/// Mutable reference storage for nonmutating set pattern.
public class MutableBox {
    public var value: Int32
    public init(value: Int32) { self.value = value }
}

/// Struct with a nonmutating set property.
/// The setter modifies external state (the box) rather than the struct's own memory.
public struct NonMutatingView {
    private let box: MutableBox

    public init(value: Int32) {
        self.box = MutableBox(value: value)
    }

    public var currentValue: Int32 {
        get { box.value }
        nonmutating set { box.value = newValue }
    }
}
