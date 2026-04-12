// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - CaselessNamespaceLocalizedError (fix #4 emission path, compile-only)
//
// Sibling to SimpleEnumLocalizedError.swift. That fixture pins the runtime
// throw/catch path of fix #4 (4235d568) for a payloadful-case-less enum.
// This fixture pins the other half of fix #4: a *caseless* namespace enum
// that conforms to LocalizedError via extension. No instance can ever be
// thrown because the enum has no cases — which is exactly the point. The
// WeatherKit-shaped emission path the generator must handle here is:
//
//   1. Parser sees an enum with zero cases used purely as a namespace
//      container for `static let` constants.
//   2. An extension declares LocalizedError conformance with an
//      `errorDescription: String?` getter.
//   3. The generator must emit the LocalizedError extension member on the
//      C# side WITHOUT crashing AND without trying to synthesize a Swift
//      throwing @_cdecl trampoline (there's nothing to throw).
//
// Before fix #4, the generator either crashed on the caseless enum path or
// emitted a C# extension method referencing a non-existent @_cdecl entry
// point, causing the generated C# to fail to compile. A pure compile-gate
// fixture here is the right shape: if `nuke binding-tests` passes with this
// file present, the emission path is intact. No runtime test is possible
// because `WeatherErrorNamespace` literally cannot be instantiated.

/// Caseless namespace enum. Acts as a container for `static let` constants —
/// the Swift idiom equivalent to a C# static class. Conforms to
/// `LocalizedError` via extension below; exercises the emission path
/// that previously crashed the generator on WeatherKit's error namespace.
public enum WeatherErrorNamespace {
    public static let missingDataIdentifier = "weather-missing-data"
    public static let truncatedPayloadIdentifier = "weather-truncated-payload"
}

/// LocalizedError conformance on a caseless namespace enum. The body is
/// unreachable because no instance of `WeatherErrorNamespace` can ever be
/// constructed, but Swift accepts the conformance and the generator must
/// accept it too. Returning `nil` is the vacuous answer; `String(describing:)`
/// on a caseless enum is undefined in practice, but the compile-time shape
/// is what fix #4 covers.
extension WeatherErrorNamespace: LocalizedError {
    public var errorDescription: String? {
        return nil
    }
}
