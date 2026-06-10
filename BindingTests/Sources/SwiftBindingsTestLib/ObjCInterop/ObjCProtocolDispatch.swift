// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - @objc protocol routed through EveryObjCProtocol
//
// Minimum repro for @objc protocols that inherit only NSObjectProtocol. The plain
// Swift `EveryProtocol` class cannot satisfy NSObjectProtocol's NSObject identity
// surface (isEqual:, hash, description). The fix adds a parallel `EveryObjCProtocol:
// NSObject` helper class and routes NSObjectProtocol-only conformances through
// it so the synthesized `extension EveryObjCProtocol: NumberProvider` type-checks.
//
// This fixture is the minimum repro: an @objc protocol that inherits only
// NSObjectProtocol (no NSCoding/NSSecureCoding/NSCopying), plus a free function
// that takes the existential and invokes the witness method. The C# side implements
// the generated `INumberProvider` interface as a plain managed class — auto-wrap
// must construct an `EveryObjCProtocol`-backed proxy (NOT EveryProtocol) so the
// Swift call site round-trips into the managed implementation.

/// @objc protocol that inherits ONLY NSObjectProtocol (no encoding / copying requirements).
@objc public protocol NumberProvider: NSObjectProtocol {
    func provideNumber() -> Int32
}

/// Invokes the witness method through the existential. The C# auto-wrap path
/// constructs an `EveryObjCProtocol`-backed proxy and hands it to this function
/// as `any NumberProvider`. If the routing regresses to plain `EveryProtocol`
/// the wrapper module fails to compile (the conformance cannot type-check), so
/// reaching this call already proves the routing fix is in place; the return-value
/// assertion proves the witness table dispatches into the managed method.
public func callNumberProvider(_ provider: NumberProvider) -> Int32 {
    return provider.provideNumber()
}
