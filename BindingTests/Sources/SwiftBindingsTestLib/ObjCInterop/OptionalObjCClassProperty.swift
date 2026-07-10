// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Optional ObjC-rooted class property round-trip
//
// Mirrors the Stripe `STPAPIClient.appInfo: STPAppInfo?` / `STPAppInfo.name: String`
// shape that surfaced a confirmed string-corruption bug: reading an Optional-typed
// ObjC-rooted (NSObject-derived) class property back out, then reading a String
// property off the returned object, mangled the string ("TestApp" -> "TestCpp",
// "ABCDEFG" -> "ABCDGFG" — byte at offset 4 drifting upward on each getter call).
//
// `InfoCarrier` is an NSObject subclass (so the generator treats it as ObjC-rooted)
// with a String stored property plus three Optional<String> stored properties,
// matching STPAppInfo's `init(name:partnerId:version:url:)`. `ClientCarrier` holds
// it as a settable `Optional<InfoCarrier>` property so the optbuf/`passRetained`
// getter path is exercised end to end.

/// NSObject-derived info object with a non-optional and several optional String
/// properties — the STPAppInfo analogue.
public class InfoCarrier: NSObject {
    public let name: String
    public let partnerId: String?
    public let version: String?
    public let url: String?

    public init(name: String, partnerId: String?, version: String?, url: String?) {
        self.name = name
        self.partnerId = partnerId
        self.version = version
        self.url = url
        super.init()
    }
}

/// NSObject-derived client holding the info object as a settable Optional property —
/// the STPAPIClient.appInfo analogue. The getter returns `Optional<InfoCarrier>`.
public class ClientCarrier: NSObject {
    public var info: InfoCarrier?

    public override init() {
        self.info = nil
        super.init()
    }

    /// Returns the held info through a *method return* of `Optional<InfoCarrier>`.
    /// A method return crosses the emitter's OptionalProjection copy-out path, which is
    /// DISTINCT from the `info` property accessor path (AccessorConversionVisitors) that
    /// the property tests above pin. The accessor path is the one that historically had the
    /// double-VWT `InitializeWithCopy` small-string corruption; OptionalProjection always
    /// bypassed `SwiftOptional` (the IntPtr result IS the payload), so it never had that bug.
    /// This exercises the return copy-out with the same multi-String-field shape to gate its
    /// string integrity independently.
    public func snapshotInfo() -> InfoCarrier? {
        return self.info
    }
}

/// Builds an `InfoCarrier` Swift-side and hands it back through the `Optional<InfoCarrier>`
/// return path, so the String bytes originate and live Swift-side before crossing the
/// bridge — the exact condition under which the small-string ivar corruption was observed.
public func makeInfoCarrier(name: String, partnerId: String?, version: String?, url: String?) -> InfoCarrier? {
    return InfoCarrier(name: name, partnerId: partnerId, version: version, url: url)
}

/// Same `Optional<InfoCarrier>` return type, returning nil — gates the None branch of the
/// method-return path (IntPtr.Zero → null) independently of the property accessor's None.
public func makeNilInfoCarrier() -> InfoCarrier? {
    return nil
}
