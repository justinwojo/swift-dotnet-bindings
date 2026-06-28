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
}
