// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Bug 12: `[any Protocol]` array property on a witness-dispatched protocol requirement

/// Reproduces RealityKit `Scene.anchors` which returns a heap-allocated
/// `[any AnchorEntity]`. The generator must keep the existential element
/// in the rendered Swift type — earlier it dropped the generic parameter
/// (rendered `[Swift.Array]` / `[any Anchor]` mismatched the C# Element).
///
/// The bug lives in `WitnessDispatchEmitter.EmitPropertyGetterAccessor`, so
/// the fixture must declare an existential array as a protocol requirement
/// (and a conformer) — that's what drives witness-dispatch property emission.
/// A plain class property would only exercise the standard property wrapper
/// path and leave the changed code uncovered.
public protocol BugReproExistentialItem {
    func describe() -> String
}

public class BugReproExistentialItemImpl: BugReproExistentialItem {
    public let label: String
    public init(label: String) { self.label = label }
    public func describe() -> String { label }
}

/// Witness-dispatched property requirement returning `[any BugReproExistentialItem]`.
public protocol BugReproExistentialArrayProvider {
    var items: [any BugReproExistentialItem] { get }
}

public class BugReproExistentialArrayHolder: BugReproExistentialArrayProvider {
    public var items: [any BugReproExistentialItem]

    public init() {
        items = [
            BugReproExistentialItemImpl(label: "alpha"),
            BugReproExistentialItemImpl(label: "beta"),
        ]
    }
}
