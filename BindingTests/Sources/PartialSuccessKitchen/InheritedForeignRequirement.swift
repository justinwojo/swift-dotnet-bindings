// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import ExtensionFoundation

// A protocol that adds nothing to a protocol of an SDK module the run never loads, the shape of
// LiveCommunicationKit's `LiveAssistanceExtensionConfiguration`. The generator reads the parent's
// requirement `nonisolated func accept(connection: NSXPCConnection) -> Bool` from its SDK
// interface and adds it to the child, so the child's proxy (`KitchenForeignChildConfigurationProxy`,
// a positive control of the gate) must be emitted rather than its conformance withdrawn.

// Same-module parent of the same shape: a main-actor protocol with a nonisolated requirement.
@preconcurrency @MainActor public protocol KitchenLocalParentConfiguration: Sendable {
    nonisolated func accept(connection: NSObject) -> Bool
}
@MainActor @preconcurrency public protocol KitchenLocalChildConfiguration: KitchenLocalParentConfiguration {}

// The unbound SDK parent.
@available(iOS 16.0, *)
@MainActor @preconcurrency public protocol KitchenForeignChildConfiguration: ExtensionFoundation.AppExtensionConfiguration {}

public struct KitchenUsesConfigurations {
    public init() {}
    public func takeLocal(_ c: some KitchenLocalChildConfiguration) {}
    @available(iOS 16.0, *)
    public func takeForeign(_ c: some KitchenForeignChildConfiguration) {}
}
