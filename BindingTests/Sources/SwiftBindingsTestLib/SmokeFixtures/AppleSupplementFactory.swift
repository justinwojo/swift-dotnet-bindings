// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Apple supplement value-factory helpers
//
// Constructs initialized supplement values into a C#-provided buffer so
// cross-assembly round-trip tests can exercise payload copy/destroy ABI. C#
// allocates a buffer of the supplement type's metadata size, calls the helper,
// then wraps the buffer via SwiftObjectHelper<T>.NewFromPayload. NewFromPayload
// moves the value out of the buffer, so the caller must NOT call VWT Destroy
// on the buffer afterwards — it must only free the raw allocation.

/// Writes an initialized `Foundation.Locale.Language(identifier: "en")` into
/// `bufferPtr`. The buffer must be at least the size reported by the type's
/// metadata accessor.
@available(iOS 16, tvOS 16, macOS 13, *)
@_cdecl("SBT_AppleSupplement_CreateLocaleLanguage")
public func sbt_appleSupplement_createLocaleLanguage(_ bufferPtr: UnsafeMutableRawPointer) {
    let lang = Foundation.Locale.Language(identifier: "en")
    bufferPtr.initializeMemory(as: Foundation.Locale.Language.self, repeating: lang, count: 1)
}

#if canImport(CryptoKit)
import CryptoKit

/// Writes an initialized `P256.Signing.ECDSASignature` into `bufferPtr`. Signs
/// a fixed message with a throwaway private key — the payload is only needed
/// for ABI round-trip exercise, not for cryptographic verification.
@available(iOS 13, tvOS 13, macOS 10.15, *)
@_cdecl("SBT_AppleSupplement_CreateP256Signature")
public func sbt_appleSupplement_createP256Signature(_ bufferPtr: UnsafeMutableRawPointer) {
    let key = P256.Signing.PrivateKey()
    let message = Data([0x01, 0x02, 0x03, 0x04])
    let sig = try! key.signature(for: message)
    bufferPtr.initializeMemory(as: P256.Signing.ECDSASignature.self, repeating: sig, count: 1)
}
#endif

#if os(iOS)
import ManagedSettings

/// Writes an initialized `ManagedSettings.Application(bundleIdentifier:)`
/// into `bufferPtr`. ManagedSettings.Application is iOS/macCatalyst-only
/// (under macCatalyst `os(iOS)` is true); macOS builds skip this factory.
@available(iOS 15, macCatalyst 15, *)
@_cdecl("SBT_AppleSupplement_CreateManagedSettingsApplication")
public func sbt_appleSupplement_createManagedSettingsApplication(_ bufferPtr: UnsafeMutableRawPointer) {
    let app = ManagedSettings.Application(bundleIdentifier: "com.apple.Preferences")
    bufferPtr.initializeMemory(as: ManagedSettings.Application.self, repeating: app, count: 1)
}
#endif
