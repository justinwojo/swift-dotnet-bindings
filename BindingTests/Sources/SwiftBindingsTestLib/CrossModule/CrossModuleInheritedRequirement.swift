// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftBindingsTestLibDependency

// MARK: - Empty main-actor child of a cross-module protocol
//
// `LocalAccepting` adds nothing to `DepAccepting` (dependency module), the shape of
// LiveCommunicationKit's `LiveAssistanceExtensionConfiguration:
// ExtensionFoundation.AppExtensionConfiguration`. The generator writes the child's
// conformance only when it can also write every inherited witness; here the parent's
// module is loaded, so the conformance must be kept, and a C# conformer must be
// reached through the inherited requirement, on the main actor.

@MainActor
public protocol LocalAccepting: DepAccepting {}

/// Dispatches the inherited requirement through the child existential.
@MainActor
public func acceptThroughLocal(_ acceptor: any LocalAccepting, _ value: Int32) -> Bool {
    return acceptor.accept(value)
}
