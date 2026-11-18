// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// A main-actor theme singleton. The theme bridge's setter and getter wrappers read and write the
// singleton's properties from inside the bridge's main-thread helper; unless that helper runs its
// block on the main actor, swiftc (6.2 and 6.4 alike, even under -strict-concurrency=minimal)
// refuses the property mutation ("main actor-isolated property ... can not be mutated from a
// nonisolated context") and the bridge file does not compile.

// SwiftUI types are not accessible in the Mac Catalyst compiler environment (see SimpleViews.swift).
#if !targetEnvironment(macCatalyst)
import SwiftUI

@MainActor
public final class MainActorBridgeTheme {
    public static let shared = MainActorBridgeTheme()

    public var accent: SwiftUI.Color = SwiftUI.Color(red: 0.1, green: 0.2, blue: 0.3, opacity: 1.0)

    public var headline: SwiftUI.Font = .system(size: 17)

    private init() {}
}
#endif
