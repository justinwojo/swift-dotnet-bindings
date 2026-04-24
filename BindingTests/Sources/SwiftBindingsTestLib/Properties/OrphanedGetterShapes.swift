// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Orphaned Getter Shapes (Issue #33)

// Reproduces FirebaseAILogic 12.6 `GenerateContentResponse` getter drop:
// the getter's P/Invoke plumbing is emitted but the public property body
// is silently missing. Three canonical shapes, all on a non-frozen struct
// parent (which is what forces the `@_cdecl` property-wrapper path where
// the preflight/emission asymmetry bites).

/// Non-frozen nested element, used as the inner type of Optional<T> and Array<T> getters below.
public struct OrphanedGetterElement {
    public let id: Int32
    public let label: String

    public init(id: Int32, label: String) {
        self.id = id
        self.label = label
    }
}

/// Non-frozen struct mirroring the shape of `GenerateContentResponse`.
/// All three getters are read-only stored properties — the parser lowers
/// them to `@_cdecl` property-wrapper getters.
public struct OrphanedGetterParent {
    /// Shape 1: Optional<String> getter (mirrors `.text`).
    public let text: String?

    /// Shape 2: Optional<NonFrozenStruct> getter (mirrors `.usageMetadata`).
    public let metadata: OrphanedGetterElement?

    /// Shape 3: Array<NonFrozenStruct> getter (mirrors `.functionCalls`).
    public let elements: [OrphanedGetterElement]

    public init(text: String?, metadata: OrphanedGetterElement?, elements: [OrphanedGetterElement]) {
        self.text = text
        self.metadata = metadata
        self.elements = elements
    }
}

/// Free-function constructor so tests don't have to invoke the generated
/// all-args init if the emitter ever skips it alongside the getters.
public func makeOrphanedGetterParent(
    text: String?,
    metadataId: Int32,
    metadataLabel: String,
    elementCount: Int32
) -> OrphanedGetterParent {
    let metadata: OrphanedGetterElement? = metadataId < 0
        ? nil
        : OrphanedGetterElement(id: metadataId, label: metadataLabel)
    var elements: [OrphanedGetterElement] = []
    if elementCount > 0 {
        for i in 0..<elementCount {
            elements.append(OrphanedGetterElement(id: i, label: "e\(i)"))
        }
    }
    return OrphanedGetterParent(text: text, metadata: metadata, elements: elements)
}
