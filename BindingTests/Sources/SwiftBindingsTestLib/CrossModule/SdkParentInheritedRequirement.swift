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

/// Hands a fresh connection to the conformer through a generic parameter, the static-dispatch path.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public func acceptFreshConnectionGenerically(_ acceptor: some SdkParentAccepting) -> Bool {
    return acceptor.accept(connection: freshConnection())
}

// MARK: - Foundation objects returned and stored through a reverse-dispatch conformance
//
// The other directions of the Objective-C class slot the receiver above reads: a requirement that
// returns a Foundation class, and a get/set property of one. A C# conformer's returned object must
// cross to Swift with a reference of its own (the Swift thunk takes a +1 over), and the object
// Swift stores must reach the conformer as the live object, not as a pointer read as a managed
// reference, and an Optional one must arrive as that object or as null. `NSObject` is the Foundation class no binding records, like `NSXPCConnection`.

public protocol FoundationObjectVending {
    func vend() -> NSObject
    var current: NSObject { get set }
    func inspect(_ object: NSObject?)
}

// What the drivers hold on Swift's side, so the tests can ask about it after C# lets go.
private nonisolated(unsafe) var vendedObject: NSObject?
private nonisolated(unsafe) var storedObject: NSObject?
// An object only Swift ever referenced, measured the same way: its count is what Swift's own
// reference alone reads as, whatever the build's optimizer leaves around the measurement.
private nonisolated(unsafe) var swiftOwnedObject: NSObject? = NSObject()

private func retainCount(of slot: NSObject?) -> Int {
    guard let object = slot else { return 0 }
    return CFGetRetainCount(object)
}

private func address(of object: NSObject) -> UInt {
    UInt(bitPattern: Unmanaged.passUnretained(object).toOpaque())
}

/// Calls `vend()` and keeps the result; returns its address.
public func keepVendedObject(_ vendor: any FoundationObjectVending) -> UInt {
    let object = vendor.vend()
    vendedObject = object
    return address(of: object)
}

/// The retain count of the object `keepVendedObject` kept: once every C# wrapper of it is gone it
/// equals `swiftOwnedObjectRetainCount()`.
public func vendedObjectRetainCount() -> Int {
    retainCount(of: vendedObject)
}

/// The retain count of an object only Swift references, read the way the other counts are.
public func swiftOwnedObjectRetainCount() -> Int {
    retainCount(of: swiftOwnedObject)
}

/// Drops the object `keepVendedObject` kept.
public func releaseVendedObject() {
    vendedObject = nil
}

/// Reads `current` through the existential; returns the address of the object it got.
public func readCurrentObject(_ vendor: any FoundationObjectVending) -> UInt {
    return address(of: vendor.current)
}

/// Stores a fresh object into `current` through the existential, keeps it, and returns its address.
public func storeFreshObject(_ vendor: any FoundationObjectVending) -> UInt {
    var target = vendor
    let object = NSObject()
    storedObject = object
    target.current = object
    return address(of: object)
}

/// The retain count of the object `storeFreshObject` kept.
public func storedObjectRetainCount() -> Int {
    retainCount(of: storedObject)
}

/// Hands `inspect(_:)` a fresh object, or nil, through the existential; returns the address it
/// passed (0 for nil). Swift's `NSObject?` is one pointer word, nil = 0.
public func inspectThroughVendor(_ vendor: any FoundationObjectVending, passingNil: Bool) -> UInt {
    let object: NSObject? = passingNil ? nil : NSObject()
    vendor.inspect(object)
    return object.map(address(of:)) ?? 0
}

/// Drops the object `storeFreshObject` kept.
public func releaseStoredObject() {
    storedObject = nil
}
