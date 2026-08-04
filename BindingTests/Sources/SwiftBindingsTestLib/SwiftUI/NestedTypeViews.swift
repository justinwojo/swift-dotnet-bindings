// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// SwiftUI Views declared NESTED inside another type.
//
// The generated bridge is compiled as a separate Swift module that only `import`s the
// framework, so a nested View's leaf name is never in scope on its own — every Swift
// type reference the bridge emits for it has to carry its enclosing type path. A bare
// leaf name compiles as "cannot find 'X' in scope".
//
// Shape observed in a payments SDK's sheet/button nesting.

// SwiftUI types (View, Text, etc.) are not accessible in the Mac Catalyst
// compiler environment despite the module importing successfully.
#if !targetEnvironment(macCatalyst)
import SwiftUI

/// Enclosing public type that owns a nested SwiftUI View. It carries its own member
/// surface on purpose: a member-less public struct with nested types is the namespace
/// facade idiom and takes a different emission path, and the shape under test here is
/// the ordinary one — a real type that happens to also declare a View inside itself.
public struct NestedViewOwner {
    public let ownerLabel: String

    public init(ownerLabel: String) {
        self.ownerLabel = ownerLabel
    }

    public func describe() -> String {
        return "owner:\(ownerLabel)"
    }

    /// A View nested one level deep. Exercises the bridge's raw-view init spelling
    /// (the wrapper body constructs this type) — it must be `NestedViewOwner.NestedTitleView`,
    /// not `NestedTitleView`.
    public struct NestedTitleView: View {
        public let title: String

        public init(title: String) {
            self.title = title
        }

        /// Self-returning modifier. The generated `applyModifiers` helper takes and returns
        /// the concrete view type, so the nested spelling has to reach that signature too.
        public func highlighted() -> Self { return self }

        public var body: some View {
            Text("NestedTitle: \(title)")
        }
    }
}

/// A leaf name is unique only inside its enclosing type, so two Views nested in different
/// enclosing types can share one. Every generated bridge name — the `@_cdecl` symbols, the
/// session class, the native-methods class — has to separate them, or the pair emits two
/// declarations of each and neither the bridge Swift nor the emitted C# compiles.
public struct SharedLeafOwnerA {
    public let ownerLabel: String

    public init(ownerLabel: String) {
        self.ownerLabel = ownerLabel
    }

    public struct SharedLeafView: View {
        public let caption: String

        public init(caption: String) {
            self.caption = caption
        }

        public var body: some View {
            Text("A: \(caption)")
        }
    }
}

/// The colliding sibling: same leaf name, different enclosing type.
public struct SharedLeafOwnerB {
    public let ownerLabel: String

    public init(ownerLabel: String) {
        self.ownerLabel = ownerLabel
    }

    public struct SharedLeafView: View {
        public let caption: String

        public init(caption: String) {
            self.caption = caption
        }

        public var body: some View {
            Text("B: \(caption)")
        }
    }
}
#endif
