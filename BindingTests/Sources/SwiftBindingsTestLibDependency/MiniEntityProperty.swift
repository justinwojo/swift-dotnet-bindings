// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Cross-assembly KeyPath-init factory fixture
//
// `MiniEntityProperty<Value>` is a deliberate, minimal stand-in for AppIntents'
// `EntityProperty<Value>`: a generic reference type, living in a *dependency*
// module, whose only constructors are method-own-generic and take a
// `KeyPath<Entity, Value>` / `WritableKeyPath<Entity, Value>` constrained on
// `Entity : AppEntity`. That shape is exactly why the real `EntityProperty`
// KeyPath inits tombstone in the AppIntents binding (C# has no generic
// constructors with method-own type parameters, and `Entity` can't satisfy the
// C# `ISwiftObject` constraint), so this reproduces the block without dragging
// the ~47k-line AppIntents binding into the BindingTests harness.
//
// The consumer-side factory emitter rescues these inits by closing `Entity` to
// a concrete `AppEntity` conformer (`MockBook`) that already ships KeyPath
// singletons, emitting a static factory in the *consumer* assembly that builds
// `MiniEntityProperty<Value>` via a Swift trampoline and adopts the returned
// handle through `SwiftObjectHelper<T>.NewFromPayload`.

#if canImport(AppIntents)
import AppIntents
import Foundation

@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public final class MiniEntityProperty<Value> {
    /// Plain `String` identifier (real `EntityProperty` uses `_const String`,
    /// which the parser filters; plain `String` keeps the round-trip observable).
    public let identifier: String

    /// The captured key path, type-erased so the class generic stays `<Value>`
    /// while the init's `Entity` is closed at the call site.
    public let capturedKeyPath: AnyKeyPath

    /// `true` when constructed via `getSetter:` (a `WritableKeyPath`).
    public let isWritable: Bool

    public init<Entity: AppEntity>(identifier: String, getter: KeyPath<Entity, Value>) {
        self.identifier = identifier
        self.capturedKeyPath = getter
        self.isWritable = false
    }

    public init<Entity: AppEntity>(identifier: String, getSetter: WritableKeyPath<Entity, Value>) {
        self.identifier = identifier
        self.capturedKeyPath = getSetter
        self.isWritable = true
    }

    /// Non-generic read-back so a C# test can verify the captured path describes
    /// a real key path (and not just a null pointer) without a method-own generic.
    public var capturedKeyPathDescription: String {
        String(describing: capturedKeyPath)
    }
}
#endif // canImport(AppIntents)
