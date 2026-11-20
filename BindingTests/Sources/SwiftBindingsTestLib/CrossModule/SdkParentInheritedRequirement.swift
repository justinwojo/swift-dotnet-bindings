// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import ExtensionFoundation

// MARK: - Empty child of a protocol of an SDK module the generator never loads
//
// `SdkParentAccepting` adds nothing to ExtensionFoundation's `AppExtensionConfiguration`, the
// shape of LiveCommunicationKit's `LiveAssistanceExtensionConfiguration`. The run loads neither
// ExtensionFoundation nor a binding of it, so the parent's requirement
// `nonisolated func accept(connection: NSXPCConnection) -> Bool` (no default) is read from its
// SDK interface and added to the child: the carrier witnesses it through the child's vtable, and
// Swift must reach a C# conformer through it with the very connection it passed, an
// Objective-C object handed across the reverse trampoline.
// (`CrossModuleInheritedRequirement.swift` is the same shape with a bound dependency parent.)

@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public protocol SdkParentAccepting: AppExtensionConfiguration {}

// The connection the last driver handed to the conformer, kept alive so its address stays valid.
private nonisolated(unsafe) var recordedSdkConnection: NSXPCConnection?

private func freshConnection() -> NSXPCConnection {
    // Never resumed: the conformer only inspects the object.
    let connection = NSXPCConnection(listenerEndpoint: NSXPCListener.anonymous().endpoint)
    recordedSdkConnection = connection
    return connection
}

/// Hands a fresh connection to the conformer through the child existential.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public func acceptFreshConnectionThroughSdkChild(_ acceptor: any SdkParentAccepting) -> Bool {
    return acceptor.accept(connection: freshConnection())
}

/// Hands a fresh connection to the conformer through the parent existential, the way the system
/// reaches an extension's configuration.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public func acceptFreshConnectionThroughSdkParent(_ acceptor: any SdkParentAccepting) -> Bool {
    let configuration: any AppExtensionConfiguration = acceptor
    return configuration.accept(connection: freshConnection())
}

/// The address of the connection the last driver passed, or 0.
public func recordedSdkConnectionAddress() -> UInt {
    guard let connection = recordedSdkConnection else { return 0 }
    return UInt(bitPattern: Unmanaged.passUnretained(connection).toOpaque())
}
