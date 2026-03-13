// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Visibility Tests

/// Struct with mixed visibility members.
/// Only public members should appear in generated bindings.
public struct VisibilityTest {
    /// Public stored property — should appear in bindings.
    public var publicValue: Int32

    /// Internal stored property — should NOT appear in bindings.
    var internalValue: Int32

    /// Private stored property — should NOT appear in bindings.
    private var privateValue: Int32

    public init(publicValue: Int32) {
        self.publicValue = publicValue
        self.internalValue = publicValue * 2
        self.privateValue = publicValue * 3
    }

    /// Public method — should appear in bindings.
    public func getPublic() -> Int32 {
        return publicValue
    }

    /// Internal method — should NOT appear in bindings.
    func getInternal() -> Int32 {
        return internalValue
    }

    /// Private method — should NOT appear in bindings.
    private func getPrivate() -> Int32 {
        return privateValue
    }
}

// MARK: - Open Class

/// Open class with overridable methods.
/// Tests that open classes are emitted as non-sealed C# classes.
open class OpenBaseClass {
    public var label: String

    public init(label: String) {
        self.label = label
    }

    /// Overridable method.
    open func process() -> String {
        return "Base: \(label)"
    }

    /// Non-overridable public method.
    public func identifier() -> String {
        return "OpenBaseClass"
    }
}

/// Subclass of the open class.
public class DerivedClass: OpenBaseClass {
    public init() {
        super.init(label: "derived")
    }

    override public func process() -> String {
        return "Derived: \(label)"
    }
}
