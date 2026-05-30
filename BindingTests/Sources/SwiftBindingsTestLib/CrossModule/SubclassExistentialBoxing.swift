// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftBindingsTestLibDependency

// MARK: - Subclass-Only Existential Conformance (AnchorEntity / HasAnchoring shape)
//
// `DependencyMarkedEntity` (dependency module) conforms to `DependencyBaseMarker`.
// This main-module subclass adds `DependencyAnchorMarker` — a protocol the base does
// NOT conform to. That is exactly the RealityKit `AnchorEntity : Entity, HasAnchoring`
// shape that the existing cross-module box fixtures missed: they box on the declaring
// class itself, so the inherited `BoxAsExistential1<T> => Create<Base, T>` happened to
// be correct. Here the subclass MUST emit its own `Create<AnchoredMarkedEntity, T>`;
// inheriting the base's would request the non-existent `DependencyMarkedEntity :
// DependencyAnchorMarker` witness and throw at box time.

/// Subclass that adds a second cross-module conformance on top of the base's.
public class AnchoredMarkedEntity: DependencyMarkedEntity, DependencyAnchorMarker {
    public let anchorName: String

    public init(baseId: Int32, anchorName: String) {
        self.anchorName = anchorName
        super.init(baseId: baseId)
    }

    public func anchorMarkerName() -> String {
        return "anchor:\(anchorName):\(baseMarkerTag())"
    }
}

/// Factory for `AnchoredMarkedEntity`.
public func makeAnchoredMarkedEntity(baseId: Int32, anchorName: String) -> AnchoredMarkedEntity {
    return AnchoredMarkedEntity(baseId: baseId, anchorName: anchorName)
}
