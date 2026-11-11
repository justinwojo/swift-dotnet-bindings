// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Apple supplement value-factory helpers
//
// Constructs initialized supplement values into a C#-provided buffer so
// cross-assembly round-trip tests can exercise payload copy/destroy ABI. C#
// allocates a buffer of the supplement type's metadata size, calls the helper,
// then wraps the buffer via SwiftObjectHelper<T>.NewFromPayload. The supplement
// types construct by copy, so the buffer still holds an initialized value
// afterwards: the caller VWT-destroys it, then frees the raw allocation.

/// Writes an initialized `Foundation.Locale.Language(identifier: "en")` into
/// `bufferPtr`. The buffer must be at least the size reported by the type's
/// metadata accessor.
@available(iOS 16, tvOS 16, macOS 13, *)
@_cdecl("SBT_AppleSupplement_CreateLocaleLanguage")
public func sbt_appleSupplement_createLocaleLanguage(_ bufferPtr: UnsafeMutableRawPointer) {
    let lang = Foundation.Locale.Language(identifier: "en")
    bufferPtr.initializeMemory(as: Foundation.Locale.Language.self, repeating: lang, count: 1)
}

/// Writes an initialized two-element `[Foundation.Locale.Language]` into
/// `bufferPtr`. Reading either element in C# exercises the unconstrained
/// SwiftArray element-materialization path that must resolve Language through
/// the supplement's NativeAOT-safe typed dispatch registrations.
@available(iOS 16, tvOS 16, macOS 13, *)
@_cdecl("SBT_AppleSupplement_CreateLocaleLanguages")
public func sbt_appleSupplement_createLocaleLanguages(_ bufferPtr: UnsafeMutableRawPointer) {
    let languages = [
        Foundation.Locale.Language(identifier: "en"),
        Foundation.Locale.Language(identifier: "fr")
    ]
    bufferPtr.initializeMemory(
        as: [Foundation.Locale.Language].self,
        repeating: languages,
        count: 1)
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

// MARK: - Known-value samples for every supplement manifest type
//
// `SBT_AppleSupplement_InitSample` writes a fixed value of one supplement type into a
// C#-provided buffer, and `SBT_AppleSupplement_SampleMatches` reads a buffer back as that
// type and compares it with the same value. C# materializes the first buffer into the
// managed projection, marshals the projection into a fresh buffer and asks Swift whether
// the value survived. A pass shows the type's metadata accessor, value witnesses and
// payload copy all work on the running OS, which is what each platform column of the
// supplement manifest claims.
//
// `kind` indexes the types in the order of `AppleSupplementSample` in
// `AppleSupplementRoundTripTests.cs`; keep the two lists in step. Both functions return 1 on
// success, 0 when the value does not match or the type is unavailable on this OS version,
// and -1 for a kind this platform has no sample for. `InitSample` initializes the buffer
// only when it returns 1.

@_cdecl("SBT_AppleSupplement_InitSample")
public func sbt_appleSupplement_initSample(_ kind: Int, _ bufferPtr: UnsafeMutableRawPointer) -> Int {
    visitSupplementSample(kind, StoreSupplementSample(buffer: bufferPtr))
}

@_cdecl("SBT_AppleSupplement_SampleMatches")
public func sbt_appleSupplement_sampleMatches(_ kind: Int, _ bufferPtr: UnsafeRawPointer) -> Int {
    visitSupplementSample(kind, MatchSupplementSample(buffer: bufferPtr))
}

private protocol SupplementSampleVisitor {
    func visit<T>(_ sample: T, _ equal: (T, T) -> Bool) -> Int
}

private struct StoreSupplementSample: SupplementSampleVisitor {
    let buffer: UnsafeMutableRawPointer
    func visit<T>(_ sample: T, _ equal: (T, T) -> Bool) -> Int {
        buffer.initializeMemory(as: T.self, repeating: sample, count: 1)
        return 1
    }
}

private struct MatchSupplementSample: SupplementSampleVisitor {
    let buffer: UnsafeRawPointer
    func visit<T>(_ sample: T, _ equal: (T, T) -> Bool) -> Int {
        equal(buffer.assumingMemoryBound(to: T.self).pointee, sample) ? 1 : 0
    }
}

private func visitSupplementSample<V: SupplementSampleVisitor>(_ kind: Int, _ visitor: V) -> Int {
    switch kind {
    case 0...4:
        guard #available(iOS 16, tvOS 16, macOS 13, *) else { return 0 }
        switch kind {
        case 0: return visitor.visit(Locale.Language(identifier: "fr-CA"), ==)
        case 1: return visitor.visit(Locale.Region("FR"), ==)
        case 2: return visitor.visit(Locale.Currency("EUR"), ==)
        case 3: return visitor.visit(Locale.LanguageCode("fr"), ==)
        default: return visitor.visit(Locale.Collation("phonebook"), ==)
        }
    case 5...7:
#if os(iOS)
        guard #available(iOS 15, macCatalyst 15, *) else { return 0 }
        switch kind {
        case 5: return visitor.visit(ManagedSettings.Application(bundleIdentifier: "com.apple.Preferences"), ==)
        case 6: return visitor.visit(ManagedSettings.WebDomain(domain: "example.com"), ==)
        default:
            // An ActivityCategory is only built from a token. A token decodes from its
            // Codable form, `{"data": <base64>}`, without the Screen Time entitlement.
            guard let token = try? JSONDecoder().decode(
                ActivityCategoryToken.self, from: Data(#"{"data":"U0JUOkFwcGxlU3VwcGxlbWVudA=="}"#.utf8))
            else { return 0 }
            return visitor.visit(ManagedSettings.ActivityCategory(token: token), ==)
        }
#else
        return -1
#endif
    case 8...10:
#if canImport(CryptoKit)
        guard #available(iOS 13, tvOS 13, macOS 10.15, *) else { return 0 }
        // A raw ECDSA signature is r || s, each the curve's coordinate size. Fixed bytes
        // keep the sample deterministic; nothing verifies it.
        func bytes(_ count: Int) -> Data { Data((0..<count).map { UInt8(truncatingIfNeeded: $0 &+ 1) }) }
        switch kind {
        case 8:
            return visitor.visit(try! P256.Signing.ECDSASignature(rawRepresentation: bytes(64))) {
                $0.rawRepresentation == $1.rawRepresentation
            }
        case 9:
            return visitor.visit(try! P384.Signing.ECDSASignature(rawRepresentation: bytes(96))) {
                $0.rawRepresentation == $1.rawRepresentation
            }
        default:
            return visitor.visit(try! P521.Signing.ECDSASignature(rawRepresentation: bytes(132))) {
                $0.rawRepresentation == $1.rawRepresentation
            }
        }
#else
        return -1
#endif
    case 11:
        return visitor.visit(DateComponents(year: 2026, month: 11, day: 11, hour: 9, minute: 30), ==)
    case 12:
        var name = PersonNameComponents()
        name.givenName = "Ada"
        name.familyName = "Lovelace"
        return visitor.visit(name, ==)
    case 13:
        guard #available(iOS 15, tvOS 15, macOS 12, *) else { return 0 }
        var text = AttributedString("Hello, ")
        text += AttributedString("supplement")
        text.link = URL(string: "https://example.com")
        return visitor.visit(text, ==)
    default:
        return -1
    }
}
