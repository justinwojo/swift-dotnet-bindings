// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Failable-init overload-collapse dedup naming.
//
// Two `init?` overloads whose Swift argument labels differ (`nonce:` vs `messengerPageId:`) but which
// erase to the SAME projected C# factory signature — `TryCreate(IEnumerable<string>, LoginTrackingPref,
// string, out …)`. They survive the primary label-inclusive dedup (distinct labels) yet collide at the
// secondary projected-C# dedup. A failable initializer emits as a static `TryCreate(…, out T)` factory,
// so the second overload used to be silently dropped as DuplicateSignature — the real-world shape was
// FBSDKLoginKit's `LoginConfiguration.init?(permissions:tracking:nonce:)` /
// `init?(permissions:tracking:messengerPageId:)` pair, one of which vanished from the binding.
//
// The fix: the first-declared failable init keeps the plain `TryCreate`; each colliding sibling is
// recovered under a label-disambiguated factory name built from the label(s) that distinguish it from
// the winner (here the third label). Nothing already emitted is renamed. Distinct `detail` values prove
// WHICH init body each emitted factory reaches.

import Foundation

public enum LoginTrackingPref {
    case enabled
    case limited
}

public final class LabeledLoginConfig {
    public let detail: String

    // First-declared → keeps the plain `TryCreate`.
    public init?(permissions: [String], tracking: LoginTrackingPref, nonce: String) {
        guard !permissions.isEmpty else { return nil }
        self.detail = "nonce:\(nonce)"
    }

    // Colliding sibling: same erased C# signature, differs only in the third label → recovered under a
    // label-disambiguated factory instead of being dropped.
    public init?(permissions: [String], tracking: LoginTrackingPref, messengerPageId: String) {
        guard !permissions.isEmpty else { return nil }
        self.detail = "page:\(messengerPageId)"
    }
}
